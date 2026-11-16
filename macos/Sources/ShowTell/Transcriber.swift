import AVFoundation
import FluidAudio
import Foundation

struct TranscriptSegment {
    let start: TimeInterval
    let end: TimeInterval
    let text: String
}

struct SpeechModelInfo {
    let id: String
    let label: String
}

/// On-device speech-to-text with NVIDIA Parakeet TDT 0.6B, run on Core ML / the Apple Neural Engine by FluidAudio.
/// Models download once from Hugging Face (to ~/Library/Application Support/FluidAudio). Audio never leaves the Mac.
enum Transcriber {
    static let defaultModel = "parakeet-v2"
    static let models = [
        SpeechModelInfo(id: "parakeet-v2", label: "Parakeet v2 · English, most accurate"),
        SpeechModelInfo(id: "parakeet-v3", label: "Parakeet v3 · 25 European languages"),
    ]

    static func version(_ id: String) -> AsrModelVersion { id == "parakeet-v3" ? .v3 : .v2 }

    static func isDownloaded(_ id: String) -> Bool {
        let v = version(id)
        return AsrModels.modelsExist(at: AsrModels.defaultCacheDirectory(for: v), version: v)
    }

    /// Downloads and loads the model ahead of the first recording.
    static func prefetch(_ id: String) async {
        do {
            try await SpeechEngine.shared.prepare(id)
            Log.write("Speech model ready: \(id)")
        } catch {
            Log.write("Speech model pre-load failed (will retry when needed): \(error.localizedDescription)")
        }
    }

    static func transcribe(_ url: URL, model: String) async throws -> [TranscriptSegment] {
        var samples = try readSamples(url)
        if samples.isEmpty || loudestSecondRMS(samples) < 0.003 {
            Log.write("Audio is silent; skipping transcription")
            return []
        }
        // A whisper of noise: digital silence can make the decoder drop words next to it.
        var rng = SystemRandomNumberGenerator()
        for i in samples.indices { samples[i] += Float(Double.random(in: -1e-4...1e-4, using: &rng)) }

        let started = Date()
        let result = try await SpeechEngine.shared.transcribe(samples, model: model)
        Log.write(String(format: "Transcribed %.1fs of audio with %@ in %.1fs", Double(samples.count) / 16000, model, Date().timeIntervalSince(started)))
        return sentences(result.text, tokens: result.tokenTimings, duration: result.duration)
    }

    // ---------- audio ----------

    /// The file as 16 kHz mono floats.
    static func readSamples(_ url: URL) throws -> [Float] {
        let file = try AVAudioFile(forReading: url)
        let fmt = file.processingFormat
        let frames = AVAudioFrameCount(file.length)
        guard frames > 0, let buf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: frames) else { return [] }
        try file.read(into: buf)
        guard let ch = buf.floatChannelData else { return [] }
        let n = Int(buf.frameLength), channels = Int(fmt.channelCount)
        var mono = [Float](repeating: 0, count: n)
        for c in 0..<channels {
            let p = ch[c]
            for i in 0..<n { mono[i] += p[i] / Float(channels) }
        }
        return fmt.sampleRate == 16000 ? mono : resample(mono, from: fmt.sampleRate, to: 16000)
    }

    static func resample(_ x: [Float], from: Double, to: Double) -> [Float] {
        guard !x.isEmpty, from > 0 else { return x }
        let count = Int(Double(x.count) * to / from)
        var out = [Float](repeating: 0, count: count)
        for i in 0..<count {
            let pos = Double(i) * from / to
            let j = Int(pos), f = Float(pos - Double(j))
            let a = x[min(j, x.count - 1)], b = x[min(j + 1, x.count - 1)]
            out[i] = a + (b - a) * f
        }
        return out
    }

    static func loudestSecondRMS(_ x: [Float]) -> Float {
        var best: Float = 0
        var i = 0
        while i < x.count {
            let end = min(i + 16000, x.count)
            var sum: Float = 0
            for k in i..<end { sum += x[k] * x[k] }
            best = max(best, (sum / Float(end - i)).squareRoot())
            i = end
        }
        return best
    }

    // ---------- sentences ----------

    /// Splits the result into sentences using token timings (also breaks a run-on segment after ~15 s).
    static func sentences(_ text: String, tokens: [TokenTiming]?, duration: TimeInterval) -> [TranscriptSegment] {
        let whole = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !whole.isEmpty else { return [] }
        guard let tokens, !tokens.isEmpty else { return [TranscriptSegment(start: 0, end: duration, text: whole)] }

        var out: [TranscriptSegment] = []
        var cur = ""
        var start = tokens[0].startTime
        var end = start
        func flush() {
            let t = cur.split(whereSeparator: { $0 == " " }).joined(separator: " ")
            if !t.isEmpty { out.append(TranscriptSegment(start: start, end: end, text: t)) }
            cur = ""
        }
        for tk in tokens {
            let piece = tk.token.replacingOccurrences(of: "\u{2581}", with: " ")
            if cur.isEmpty {
                start = tk.startTime
            } else if piece.hasPrefix(" ") && end - start > 15 {
                flush()
                start = tk.startTime
            }
            cur += piece
            end = tk.endTime
            if let last = piece.trimmingCharacters(in: .whitespaces).last, ".?!".contains(last) { flush() }
        }
        flush()

        // If the token pieces don't spell the final text (e.g. post-processing), fall back to one segment.
        let squash: (String) -> String = { $0.filter { !$0.isWhitespace } }
        if squash(out.map(\.text).joined()) != squash(whole) {
            return [TranscriptSegment(start: out.first?.start ?? 0, end: out.last?.end ?? duration, text: whole)]
        }
        return out
    }
}

/// Holds the loaded model so it is loaded once per app run.
actor SpeechEngine {
    static let shared = SpeechEngine()
    private var manager: AsrManager?
    private var loaded: String?

    func prepare(_ model: String) async throws {
        if manager != nil, loaded == model { return }
        manager?.cleanup()
        manager = nil
        let models = try await AsrModels.downloadAndLoad(version: Transcriber.version(model))
        let m = AsrManager(config: .default)
        try await m.initialize(models: models)
        manager = m
        loaded = model
    }

    func transcribe(_ samples: [Float], model: String) async throws -> ASRResult {
        try await prepare(model)
        guard let m = manager else { throw ASRError.notInitialized }
        try? await m.resetDecoderState(for: .system)
        return try await m.transcribe(samples, source: .system)
    }
}
