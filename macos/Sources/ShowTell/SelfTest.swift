import FluidAudio
import Foundation

/// `ShowTell --selftest [transcribe=/path/speech.wav]` checks the parts that don't need permissions or a screen.
/// Prints a report and returns the number of failures as the exit code.
enum SelfTest {
    private static var failures = 0

    private static func check(_ ok: Bool, _ what: String) {
        print((ok ? "PASS  " : "FAIL  ") + what)
        if !ok { failures += 1 }
    }

    static func run(_ args: [String]) -> Int32 {
        chordTests()
        frameTests()
        promptTests()
        sentenceTests()
        settingsTests()
        if let arg = args.first(where: { $0.hasPrefix("transcribe=") }) {
            transcribeTest(URL(fileURLWithPath: String(arg.dropFirst("transcribe=".count))))
        }
        print(failures == 0 ? "All checks passed." : "\(failures) check(s) failed.")
        return Int32(failures)
    }

    static func chordTests() {
        let d = ChordDetector()
        var toggles = 0
        d.onToggle = { toggles += 1 }
        // Double tap F1+F2 within the window -> one toggle.
        d.onKey(122, up: false, nowMs: 0); d.onKey(120, up: false, nowMs: 20)
        d.onKey(122, up: true, nowMs: 90); d.onKey(120, up: true, nowMs: 100)
        d.onKey(120, up: false, nowMs: 250); d.onKey(122, up: false, nowMs: 260)
        d.onKey(120, up: true, nowMs: 330); d.onKey(122, up: true, nowMs: 340)
        check(toggles == 1, "double tap toggles once")

        // Too slow -> no toggle.
        d.reset(); toggles = 0
        d.onKey(122, up: false, nowMs: 0); d.onKey(120, up: false, nowMs: 10)
        d.onKey(122, up: true, nowMs: 50); d.onKey(120, up: true, nowMs: 60)
        d.onKey(122, up: false, nowMs: 900); d.onKey(120, up: false, nowMs: 910)
        check(toggles == 0, "slow second tap doesn't toggle")

        // Auto-repeat and single keys don't count.
        d.reset(); toggles = 0
        for t in stride(from: 0, to: 400, by: 30) { d.onKey(122, up: false, nowMs: Int64(t)) }
        d.onKey(122, up: true, nowMs: 420)
        check(toggles == 0, "holding one key doesn't toggle")

        check(KeyNames.name(122) == "F1" && KeyNames.name(120) == "F2" && KeyNames.name(0) == "A", "key names")
        check(KeyNames.mediaToFKey[3] == 122 && KeyNames.mediaToFKey[2] == 120, "brightness keys map to F1/F2")
        let sc = Automation.parseShortcut("cmd+shift+o")
        check(sc?.0 == 31 && sc?.1.contains(.maskCommand) == true && sc?.1.contains(.maskShift) == true, "shortcut parsing")
    }

    static func frameTests() {
        let base = URL(fileURLWithPath: "/tmp")
        let frames = (0..<20).map { KeyFrame(url: base.appendingPathComponent("f\($0).jpg"), at: Double($0), score: Double(($0 * 7) % 11)) }
        let chosen = Recorder.selectFrames(frames, max: 5)
        check(chosen.count == 5, "selects max frames")
        check(chosen.first?.at == 0 && chosen.last?.at == 19, "keeps first and last")
        check(chosen.map(\.at) == chosen.map(\.at).sorted(), "frames stay in time order")

        let a = [UInt8](repeating: 10, count: 100)
        var b = a
        for i in 0..<5 { b[i] = 200 }
        check(abs(Recorder.changedPercent(a, b) - 5) < 0.001, "change detection")
        check(formatTime(65) == "1:05" && formatTime(3725) == "1:02:05", "time format")
    }

    static func promptTests() {
        let f = [KeyFrame(url: URL(fileURLWithPath: "/a.jpg"), at: 0, score: 1), KeyFrame(url: URL(fileURLWithPath: "/b.jpg"), at: 5, score: 1)]
        let p = PromptBuilder.build(instructions: "Make the header blue", duration: 12, frames: f, audioError: nil,
                                    transcript: [TranscriptSegment(start: 1, end: 3, text: "I like this header.")], transcriptError: nil)
        check(p.hasPrefix("Make the header blue"), "prompt starts with instructions")
        check(p.contains("#2 @ 0:05") && p.contains("[0:01] I like this header."), "prompt has frame times and transcript")
        let q = PromptBuilder.build(instructions: "", duration: 3, frames: [], audioError: "no microphone found", transcript: [], transcriptError: nil)
        check(q.contains("microphone unavailable: no microphone found"), "prompt explains a missing mic")
    }

    static func sentenceTests() {
        let toks: [(String, Double)] = [("\u{2581}Hello", 0.1), ("\u{2581}there", 0.4), (".", 0.6), ("\u{2581}How", 1.2), ("\u{2581}are", 1.4), ("\u{2581}you", 1.6), ("?", 1.8)]
        let timings = toks.enumerated().map { i, t in
            TokenTiming(token: t.0, tokenId: i, startTime: t.1, endTime: t.1 + 0.2, confidence: 1)
        }
        let segs = Transcriber.sentences("Hello there. How are you?", tokens: timings, duration: 2)
        check(segs.count == 2 && segs[0].text == "Hello there." && segs[1].text == "How are you?", "sentence split")
        check(abs((segs.last?.start ?? 0) - 1.2) < 0.001, "sentence start times")
        let fallback = Transcriber.sentences("Something else entirely", tokens: timings, duration: 2)
        check(fallback.count == 1 && fallback[0].text == "Something else entirely", "falls back when tokens don't match")
    }

    static func settingsTests() {
        let data = #"{"keyA": 96, "maxFrames": 5}"#.data(using: .utf8)!
        let s = try? AppSettings.merged(AppSettings(), with: data)
        check(s?.keyA == 96 && s?.maxFrames == 5 && s?.keyB == 120 && s?.targets.count == 3, "partial settings.json keeps defaults")
    }

    static func transcribeTest(_ url: URL) {
        let done = DispatchSemaphore(value: 0)
        let started = Date()
        Task.detached {
            do {
                let segs = try await Transcriber.transcribe(url, model: Transcriber.defaultModel)
                print(String(format: "Transcribed in %.1fs (includes loading the model):", Date().timeIntervalSince(started)))
                for s in segs { print("  [\(formatTime(s.start))] \(s.text)") }
                check(!segs.isEmpty, "transcription produced text")
            } catch {
                print("Transcription error: \(error)")
                check(false, "transcription ran")
            }
            done.signal()
        }
        done.wait()
    }
}
