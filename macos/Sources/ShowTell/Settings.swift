import Foundation

/// A desktop AI app ShowTell can paste into.
struct TargetApp: Codable, Hashable {
    var name: String
    /// Bundle identifiers, tried in order.
    var bundleIds: [String]
    /// App names (without .app) looked for in /Applications and ~/Applications, and matched against running apps.
    var appNames: [String]
    /// Accessible names of the "new chat" button, tried in order.
    var newChatButtonNames: [String]
    /// Shortcut used when no new-chat button is found, e.g. "cmd+n". Empty = none.
    var newChatShortcut: String
}

struct AppSettings: Codable {
    // Hotkey: both keys pressed together, twice within doubleTapWindowMs. Values are macOS virtual key codes.
    var keyA = 122 // F1 (or the brightness-down key)
    var keyB = 120 // F2 (or the brightness-up key)
    var doubleTapWindowMs = 500

    // Recording
    var sampleIntervalMs = 500
    var maxFrames = 12
    /// Keep a frame at least this often even if the screen barely changed.
    var heartbeatSeconds = 8.0
    /// Percent of the screen that must change before a frame counts as a new key frame.
    var changeThreshold = 0.4
    var maxImageLongEdge = 1568
    var drawCursor = true
    var playSounds = false
    var deleteAfterPaste = true
    var maxRecordingMinutes = 15

    // Transcription
    var speechModel = Transcriber.defaultModel

    // Pasting
    var pasteDelayMs = 700
    var lastTarget = ""
    var keepRecordings = 20

    var targets: [TargetApp] = AppSettings.defaultTargets

    static let defaultTargets: [TargetApp] = [
        TargetApp(name: "Claude",
                  bundleIds: ["com.anthropic.claudefordesktop"],
                  appNames: ["Claude"],
                  newChatButtonNames: ["New chat", "New"],
                  newChatShortcut: "cmd+n"),
        TargetApp(name: "Codex",
                  bundleIds: ["com.openai.codex", "com.openai.chat"],
                  appNames: ["Codex", "ChatGPT"],
                  newChatButtonNames: ["New thread", "New chat", "New task"],
                  newChatShortcut: "cmd+n"),
        TargetApp(name: "Grok",
                  bundleIds: ["ai.x.grok", "com.xai.grok"],
                  appNames: ["Grok"],
                  newChatButtonNames: ["New chat", "New Chat", "New conversation", "Chat"],
                  newChatShortcut: ""),
    ]

    var hotkeyText: String { "\(KeyNames.name(keyA)) + \(KeyNames.name(keyB))" }

    // ---------- persistence ----------

    /// Loads settings.json on top of the defaults, so a file with missing keys still works.
    static func load() -> AppSettings {
        let defaults = AppSettings()
        guard let data = try? Data(contentsOf: Paths.settingsFile) else { return defaults }
        do {
            return try merged(defaults, with: data)
        } catch {
            Log.write("Settings load failed, using defaults: \(error.localizedDescription)")
            return defaults
        }
    }

    static func merged(_ base: AppSettings, with data: Data) throws -> AppSettings {
        guard let loaded = try JSONSerialization.jsonObject(with: data) as? [String: Any],
              var dict = try JSONSerialization.jsonObject(with: JSONEncoder().encode(base)) as? [String: Any]
        else { return base }
        for (k, v) in loaded { dict[k] = v }
        return try JSONDecoder().decode(AppSettings.self, from: JSONSerialization.data(withJSONObject: dict))
    }

    func save() {
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        do { try enc.encode(self).write(to: Paths.settingsFile, options: .atomic) }
        catch { Log.write("Settings save failed: \(error.localizedDescription)") }
    }
}

enum Paths {
    static let dataDir: URL = {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        let dir = base.appendingPathComponent("ShowTell", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir
    }()
    static var recordings: URL { dataDir.appendingPathComponent("Recordings", isDirectory: true) }
    static var settingsFile: URL { dataDir.appendingPathComponent("settings.json") }
    static var logFile: URL { dataDir.appendingPathComponent("showtell.log") }
}

enum Log {
    private static let queue = DispatchQueue(label: "ShowTell.log")
    private static let stamp: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"
        return f
    }()

    static func write(_ message: String) {
        let line = "\(stamp.string(from: Date())) \(message)\n"
        queue.async {
            guard let data = line.data(using: .utf8) else { return }
            if let h = try? FileHandle(forWritingTo: Paths.logFile) {
                h.seekToEndOfFile()
                h.write(data)
                try? h.close()
            } else {
                try? data.write(to: Paths.logFile)
            }
        }
    }
}

/// "m:ss", or "h:mm:ss" past an hour.
func formatTime(_ t: TimeInterval) -> String {
    let s = max(0, Int(t))
    if s >= 3600 { return String(format: "%d:%02d:%02d", s / 3600, (s / 60) % 60, s % 60) }
    return String(format: "%d:%02d", s / 60, s % 60)
}
