import AppKit
import AVFoundation
import ScreenCaptureKit
import UniformTypeIdentifiers

struct KeyFrame: Hashable {
    let url: URL
    let at: TimeInterval
    let score: Double
}

struct RecordingResult {
    let folder: URL
    let duration: TimeInterval
    let frames: [KeyFrame]
    let audioURL: URL?
    let audioError: String?
}

enum RecorderError: LocalizedError {
    case noDisplay
    var errorDescription: String? { "No display found to record." }
}

/// Records the microphone (16 kHz mono WAV) and samples the screen a few times per second with ScreenCaptureKit,
/// keeping only frames where something visibly changed (plus a periodic heartbeat frame).
/// ShowTell's own windows (the REC pill, dialogs) are excluded from the capture.
final class Recorder {
    private let s: AppSettings
    let folder: URL
    private var t0: TimeInterval = 0
    private var candidates: [KeyFrame] = []
    private let lock = NSLock()
    private var loop: Task<Void, Never>?
    private var running = false
    private var audio: AVAudioRecorder?
    private(set) var audioError: String?
    private var filter: SCContentFilter?
    private var config: SCStreamConfiguration?
    private(set) var screen: NSScreen?

    var elapsed: TimeInterval { running || t0 > 0 ? ProcessInfo.processInfo.systemUptime - t0 : 0 }

    init(settings: AppSettings) {
        s = settings
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd_HHmmss"
        folder = Paths.recordings.appendingPathComponent(f.string(from: Date()), isDirectory: true)
        try? FileManager.default.createDirectory(at: folder.appendingPathComponent("frames"), withIntermediateDirectories: true)
    }

    /// Starts recording the display under the mouse. Throws if Screen Recording permission is missing.
    @MainActor
    func start() async throws {
        let mouse = NSEvent.mouseLocation
        let scr = NSScreen.screens.first(where: { NSMouseInRect(mouse, $0.frame, false) }) ?? NSScreen.main ?? NSScreen.screens.first
        screen = scr
        let key = NSDeviceDescriptionKey("NSScreenNumber")
        let displayID = (scr?.deviceDescription[key] as? NSNumber)?.uint32Value ?? CGMainDisplayID()

        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first(where: { $0.displayID == displayID }) ?? content.displays.first
        else { throw RecorderError.noDisplay }
        let mine = content.applications.filter { $0.processID == ProcessInfo.processInfo.processIdentifier }
        let filter = SCContentFilter(display: display, excludingApplications: mine, exceptingWindows: [])

        let cfg = SCStreamConfiguration()
        let scale = CGFloat(filter.pointPixelScale)
        var w = filter.contentRect.width * scale
        var h = filter.contentRect.height * scale
        let longEdge = max(w, h), cap = CGFloat(s.maxImageLongEdge)
        if longEdge > cap { w = w * cap / longEdge; h = h * cap / longEdge }
        cfg.width = max(2, Int(w))
        cfg.height = max(2, Int(h))
        cfg.showsCursor = s.drawCursor
        self.filter = filter
        self.config = cfg

        t0 = ProcessInfo.processInfo.systemUptime
        running = true
        startAudio()
        loop = Task.detached(priority: .userInitiated) { [weak self] in
            await self?.screenLoop()
        }
        Log.write("Recording started: display \(displayID) \(cfg.width)x\(cfg.height) folder=\(folder.lastPathComponent)")
    }

    func stop() async -> RecordingResult {
        running = false
        await loop?.value
        let duration = elapsed
        stopAudio()

        // One final frame so the end state is always included.
        if let img = await captureImage() { save(img, at: duration, score: 1e12) }

        let all = snapshotCandidates()
        let chosen = Recorder.selectFrames(all, max: s.maxFrames)
        let keep = Set(chosen)
        for f in all where !keep.contains(f) { try? FileManager.default.removeItem(at: f.url) }

        Log.write("Recording stopped: \(formatTime(duration)), \(all.count) candidates -> \(chosen.count) frames")
        return RecordingResult(folder: folder, duration: duration, frames: chosen,
                               audioURL: audioError == nil ? folder.appendingPathComponent("audio.wav") : nil,
                               audioError: audioError)
    }

    /// Always keep the first and last frame, then the biggest visual changes, returned in time order.
    static func selectFrames(_ candidates: [KeyFrame], max: Int) -> [KeyFrame] {
        if candidates.count <= max { return candidates }
        guard let first = candidates.first, let last = candidates.last else { return [] }
        let middle = candidates.dropFirst().dropLast()
            .sorted { $0.score > $1.score }
            .prefix(Swift.max(0, max - 2))
        return ([first, last] + middle).sorted { $0.at < $1.at }
    }

    // ---------------- audio ----------------

    private func startAudio() {
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .denied, .restricted:
            audioError = "microphone access is off for ShowTell (System Settings → Privacy & Security → Microphone)"
            return
        default:
            break
        }
        guard AVCaptureDevice.default(for: .audio) != nil else {
            audioError = "no microphone found"
            return
        }
        let url = folder.appendingPathComponent("audio.wav")
        let settings: [String: Any] = [
            AVFormatIDKey: Int(kAudioFormatLinearPCM),
            AVSampleRateKey: 16000.0,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
        ]
        do {
            let r = try AVAudioRecorder(url: url, settings: settings)
            guard r.record() else { throw NSError(domain: "ShowTell", code: 1, userInfo: [NSLocalizedDescriptionKey: "couldn't start the microphone"]) }
            audio = r
            Log.write("Mic: \(AVCaptureDevice.default(for: .audio)?.localizedName ?? "default input")")
        } catch {
            audioError = error.localizedDescription
            Log.write("Audio start failed: \(error)")
        }
    }

    private func stopAudio() {
        audio?.stop()
        audio = nil
    }

    // ---------------- screen ----------------

    private func screenLoop() async {
        var lastThumb: [UInt8]?
        var lastKept: TimeInterval = -1_000
        let interval = Double(max(100, s.sampleIntervalMs)) / 1000

        while running {
            let now = elapsed
            if let img = await captureImage() {
                let thumb = Recorder.thumbnail(img)
                let diff = lastThumb.map { Recorder.changedPercent(thumb, $0) } ?? 1e9
                let changed = diff >= s.changeThreshold
                if changed || now - lastKept >= s.heartbeatSeconds {
                    save(img, at: now, score: changed ? diff : 0.01)
                    lastThumb = thumb
                    lastKept = now
                }
            }
            let wait = interval - (elapsed - now)
            if wait > 0 { try? await Task.sleep(nanoseconds: UInt64(wait * 1_000_000_000)) }
        }
    }

    private func captureImage() async -> CGImage? {
        guard let filter, let config else { return nil }
        do {
            return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
        } catch {
            Log.write("Frame capture failed: \(error.localizedDescription)")
            try? await Task.sleep(nanoseconds: 500_000_000)
            return nil
        }
    }

    private func snapshotCandidates() -> [KeyFrame] {
        lock.lock(); defer { lock.unlock() }
        return candidates
    }

    private func save(_ img: CGImage, at: TimeInterval, score: Double) {
        lock.lock(); let idx = candidates.count; lock.unlock()
        let name = String(format: "frame_%04d_%07dms.jpg", idx, Int(at * 1000))
        let url = folder.appendingPathComponent("frames").appendingPathComponent(name)
        guard let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.jpeg.identifier as CFString, 1, nil) else { return }
        CGImageDestinationAddImage(dest, img, [kCGImageDestinationLossyCompressionQuality as String: 0.88] as CFDictionary)
        guard CGImageDestinationFinalize(dest) else { return }
        lock.lock(); candidates.append(KeyFrame(url: url, at: at, score: score)); lock.unlock()
    }

    static let thumbW = 128, thumbH = 72

    static func thumbnail(_ img: CGImage) -> [UInt8] {
        var buf = [UInt8](repeating: 0, count: thumbW * thumbH)
        buf.withUnsafeMutableBytes { p in
            guard let ctx = CGContext(data: p.baseAddress, width: thumbW, height: thumbH, bitsPerComponent: 8,
                                      bytesPerRow: thumbW, space: CGColorSpaceCreateDeviceGray(),
                                      bitmapInfo: CGImageAlphaInfo.none.rawValue) else { return }
            ctx.interpolationQuality = .low
            ctx.draw(img, in: CGRect(x: 0, y: 0, width: thumbW, height: thumbH))
        }
        return buf
    }

    /// Percent of thumbnail pixels that changed noticeably (ignores noise and the moving cursor).
    static func changedPercent(_ a: [UInt8], _ b: [UInt8]) -> Double {
        let n = min(a.count, b.count)
        guard n > 0 else { return 0 }
        var changed = 0
        for i in 0..<n where abs(Int(a[i]) - Int(b[i])) > 16 { changed += 1 }
        return 100.0 * Double(changed) / Double(n)
    }

    /// Delete all but the newest `keep` recording folders.
    static func prune(keep: Int) {
        let fm = FileManager.default
        guard let dirs = try? fm.contentsOfDirectory(at: Paths.recordings, includingPropertiesForKeys: nil) else { return }
        for d in dirs.sorted(by: { $0.lastPathComponent > $1.lastPathComponent }).dropFirst(max(1, keep)) {
            try? fm.removeItem(at: d)
        }
    }
}
