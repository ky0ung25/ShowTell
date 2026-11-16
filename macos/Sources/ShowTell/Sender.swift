import AppKit

enum PromptBuilder {
    static func build(instructions: String, duration: TimeInterval, frames: [KeyFrame], audioError: String?,
                      transcript: [TranscriptSegment], transcriptError: String?) -> String {
        var lines: [String] = []
        let ins = instructions.trimmingCharacters(in: .whitespacesAndNewlines)
        if !ins.isEmpty { lines += [ins, ""] }

        lines.append("[Screen recording, \(formatTime(duration)) long. \(frames.count) key frame\(frames.count == 1 ? "" : "s") from it are attached in time order.]")
        if !frames.isEmpty {
            lines.append("Frame times: " + frames.enumerated().map { "#\($0.offset + 1) @ \(formatTime($0.element.at))" }.joined(separator: ", "))
        }
        lines.append("")

        if let audioError {
            lines.append("Voice transcript: (microphone unavailable: \(audioError))")
        } else if let transcriptError {
            lines.append("Voice transcript: (transcription failed: \(transcriptError))")
        } else if transcript.isEmpty {
            lines.append("Voice transcript: (no speech detected)")
        } else {
            lines.append("Voice transcript (auto-generated, may contain small errors):")
            lines += transcript.map { "[\(formatTime($0.start))] \($0.text)" }
        }
        return lines.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
    }
}

enum SendError: LocalizedError {
    case message(String)
    var errorDescription: String? { if case .message(let m) = self { return m }; return nil }
}

/// Brings the target app forward, opens the chosen thread, and pastes frames + text into its input box. Never presses Return.
enum Sender {
    static func sleep(_ seconds: Double) async throws {
        try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
    }

    static func isFrontmost(_ app: NSRunningApplication) -> Bool {
        NSWorkspace.shared.frontmostApplication?.processIdentifier == app.processIdentifier
    }

    @MainActor
    static func bringForward(_ app: NSRunningApplication) async {
        app.unhide()
        NSApp.yieldActivation(to: app)
        app.activate()
        try? await sleep(0.3)
        if !isFrontmost(app) { app.activate(options: [.activateIgnoringOtherApps]) }
    }

    @MainActor
    static func launchAndWait(_ t: InstalledTarget) async throws -> NSRunningApplication {
        if let app = AppCatalog.runningApp(t.target), Automation.mainWindow(app.processIdentifier) != nil { return app }
        guard let url = t.appURL else { throw SendError.message("\(t.name) isn't running and couldn't be launched.") }
        let cfg = NSWorkspace.OpenConfiguration()
        cfg.activates = true
        let app = try await NSWorkspace.shared.openApplication(at: url, configuration: cfg)
        for _ in 0..<60 {
            try await sleep(0.5)
            if Automation.mainWindow(app.processIdentifier) != nil {
                try await sleep(2.5) // let the web UI finish loading
                return app
            }
        }
        throw SendError.message("\(t.name) didn't open a window within 30 seconds.")
    }

    @MainActor
    static func send(target: InstalledTarget, thread: ThreadChoice, frames: [KeyFrame], text: String, settings s: AppSettings) async throws {
        guard AXIsProcessTrusted() else {
            throw SendError.message("ShowTell needs the Accessibility permission to paste into \(target.name): System Settings → Privacy & Security → Accessibility.")
        }
        await Automation.waitForKeysReleased()

        let app = try await launchAndWait(target)
        let pid = app.processIdentifier
        await bringForward(app)
        try await sleep(0.35)

        switch thread.kind {
        case .newChat:
            let ok = await Task.detached { Automation.startNewChat(pid, target: target.target) }.value
            if !ok { Log.write("No new-chat button or shortcut found; pasting into the current chat") }
            try await sleep(1.2)
        case .existing:
            let name = thread.name ?? ""
            let ok = await Task.detached { Automation.openThread(pid, name: name) }.value
            if !ok { throw SendError.message("Couldn't find the chat \"\(name)\" in \(target.name)'s sidebar. Is the sidebar open?") }
            try await sleep(1.5)
        case .current:
            break
        }

        await bringForward(app)
        try await sleep(0.15)
        let focused = await Task.detached { Automation.focusComposer(pid) }.value
        if !focused { Log.write("Composer not found via Accessibility; relying on the app's default focus") }
        try await sleep(0.25)

        for (i, f) in frames.enumerated() {
            try Task.checkCancellation()
            try await ensureFrontmost(app, target)
            Log.write("send: pasting frame \(i + 1) of \(frames.count)")
            try Automation.setClipboardImage(f.url)
            try await sleep(0.06)
            Automation.pressPaste()
            try await sleep(Double(s.pasteDelayMs) / 1000)
        }

        try await ensureFrontmost(app, target)
        Automation.setClipboardText(text)
        try await sleep(0.06)
        Automation.pressPaste()
        try await sleep(0.2)
        Log.write("Pasted \(frames.count) frames + \(text.count) chars into \(target.name) (\(thread.kind))")
    }

    @MainActor
    static func ensureFrontmost(_ app: NSRunningApplication, _ target: InstalledTarget) async throws {
        if isFrontmost(app) { return }
        await bringForward(app)
        try await sleep(0.2)
        if !isFrontmost(app) {
            throw SendError.message("\(target.name) lost focus, so pasting stopped. Click into its message box and try again.")
        }
    }

    static func saveArtifacts(_ rec: RecordingResult, prompt: String, transcript: [TranscriptSegment]) {
        try? prompt.write(to: rec.folder.appendingPathComponent("prompt.txt"), atomically: true, encoding: .utf8)
        if !transcript.isEmpty {
            let text = transcript.map { "[\(formatTime($0.start)) - \(formatTime($0.end))] \($0.text)" }.joined(separator: "\n")
            try? text.write(to: rec.folder.appendingPathComponent("transcript.txt"), atomically: true, encoding: .utf8)
        }
    }
}
