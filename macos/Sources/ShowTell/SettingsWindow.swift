import AppKit
import AVFoundation
import ServiceManagement
import SwiftUI

/// The macOS privacy permissions ShowTell uses.
enum Permissions {
    struct Snapshot: Equatable {
        var screen: Bool
        var accessibility: Bool
        var input: Bool
        var mic: Bool
        var all: Bool { screen && accessibility && input && mic }
    }

    static func snapshot() -> Snapshot {
        Snapshot(screen: CGPreflightScreenCaptureAccess(),
                 accessibility: AXIsProcessTrusted(),
                 input: CGPreflightListenEventAccess() || AXIsProcessTrusted(),
                 mic: AVCaptureDevice.authorizationStatus(for: .audio) == .authorized)
    }

    /// Ask for anything not yet decided. macOS shows each prompt only once; after that, the user flips it in System Settings.
    static func requestMissing() {
        if AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined {
            AVCaptureDevice.requestAccess(for: .audio) { ok in Log.write("Microphone permission: \(ok)") }
        }
        if !CGPreflightListenEventAccess() { _ = CGRequestListenEventAccess() }
        if !AXIsProcessTrusted() {
            let opts = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
            _ = AXIsProcessTrustedWithOptions(opts)
        }
        if !CGPreflightScreenCaptureAccess() { _ = CGRequestScreenCaptureAccess() }
    }

    static func openPane(_ anchor: String) {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(anchor)") {
            NSWorkspace.shared.open(url)
        }
    }
}

@MainActor
final class SettingsModel: ObservableObject {
    @Published var s: AppSettings { didSet { onChange(s) } }
    @Published var capturing: Int? // 0 = first key, 1 = second key
    @Published var perms = Permissions.snapshot()
    @Published var launchAtLogin = SMAppService.mainApp.status == .enabled
    private var monitor: Any?
    private var timer: Timer?
    private let onChange: (AppSettings) -> Void

    init(settings: AppSettings, onChange: @escaping (AppSettings) -> Void) {
        s = settings
        self.onChange = onChange
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                let p = Permissions.snapshot()
                if p != self.perms { self.perms = p }
            }
        }
    }

    func stop() {
        timer?.invalidate()
        endCapture()
    }

    func capture(_ which: Int) {
        endCapture()
        capturing = which
        monitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .flagsChanged, .systemDefined]) { [weak self] e in
            guard let self, let which = self.capturing else { return e }
            if e.type == .keyDown && e.keyCode == 53 { self.endCapture(); return nil } // Esc cancels
            guard let code = KeyNames.capturedKey(e) else { return e }
            if which == 0 { self.s.keyA = code } else { self.s.keyB = code }
            self.endCapture()
            return nil
        }
    }

    func endCapture() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
        capturing = nil
    }

    func setLaunchAtLogin(_ on: Bool) {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            Log.write("Launch at login change failed: \(error.localizedDescription)")
        }
        launchAtLogin = SMAppService.mainApp.status == .enabled
    }
}

struct SettingsView: View {
    @ObservedObject var m: SettingsModel

    var body: some View {
        Form {
            Section {
                HStack(spacing: 8) {
                    keyButton(0)
                    Text("+").foregroundStyle(.secondary)
                    keyButton(1)
                    Spacer()
                }
                Text(m.capturing != nil ? "Press the new key… (Esc to cancel)" : "Press both keys together, twice, to start or stop recording. Click a key to change it.")
                    .font(.callout).foregroundStyle(.secondary)
                Stepper(value: $m.s.doubleTapWindowMs, in: 250...1200, step: 50) {
                    Text("Double-tap window: \(m.s.doubleTapWindowMs) ms")
                }
                Text("Apple keyboards send F1/F2 as brightness keys; ShowTell recognizes them either way.")
                    .font(.footnote).foregroundStyle(.secondary)
            } header: { Text("Shortcut") }

            Section {
                Stepper(value: $m.s.maxFrames, in: 1...30) { Text("Max frames: \(m.s.maxFrames)") }
                Stepper(value: $m.s.maxRecordingMinutes, in: 1...60) { Text("Max length: \(m.s.maxRecordingMinutes) min") }
                Toggle("Show the mouse pointer in frames", isOn: $m.s.drawCursor)
                Toggle("Delete recordings once they're pasted", isOn: $m.s.deleteAfterPaste)
                Text("Records the screen under the mouse and your Mac's current input device (System Settings → Sound → Input).")
                    .font(.footnote).foregroundStyle(.secondary)
            } header: { Text("Recording") }

            Section {
                Picker("Speech model", selection: $m.s.speechModel) {
                    ForEach(Transcriber.models, id: \.id) { model in
                        Text(model.label + (Transcriber.isDownloaded(model.id) ? "  ✓ downloaded" : "")).tag(model.id)
                    }
                }
                Text("Runs on your Mac with the Neural Engine. Uses no tokens; audio never leaves the Mac. A model downloads once (roughly 500 MB).")
                    .font(.footnote).foregroundStyle(.secondary)
            } header: { Text("Speech-to-text") }

            Section {
                Toggle("Play a sound when recording starts and stops", isOn: $m.s.playSounds)
                Toggle("Open ShowTell at login", isOn: Binding(get: { m.launchAtLogin }, set: { m.setLaunchAtLogin($0) }))
            } header: { Text("General") }

            Section {
                permRow("Screen Recording", ok: m.perms.screen, pane: "Privacy_ScreenCapture", why: "to capture frames")
                permRow("Accessibility", ok: m.perms.accessibility, pane: "Privacy_Accessibility", why: "to open chats and paste")
                permRow("Input Monitoring", ok: m.perms.input, pane: "Privacy_ListenEvent", why: "to hear the shortcut")
                permRow("Microphone", ok: m.perms.mic, pane: "Privacy_Microphone", why: "to record your voice")
                Text("After turning on Screen Recording, quit and reopen ShowTell.")
                    .font(.footnote).foregroundStyle(.secondary)
            } header: { Text("Permissions") }

            Section {
                HStack {
                    Button("Open recordings folder") {
                        try? FileManager.default.createDirectory(at: Paths.recordings, withIntermediateDirectories: true)
                        _ = NSWorkspace.shared.open(Paths.recordings)
                    }
                    Button("Edit settings.json") { _ = NSWorkspace.shared.open(Paths.settingsFile) }
                    Button("Show log") { NSWorkspace.shared.activateFileViewerSelecting([Paths.logFile]) }
                }
            }
        }
        .formStyle(.grouped)
        .frame(width: 520)
        .frame(minHeight: 560)
    }

    private func keyButton(_ which: Int) -> some View {
        let code = which == 0 ? m.s.keyA : m.s.keyB
        return Button(m.capturing == which ? "Press a key…" : KeyNames.name(code)) { m.capture(which) }
            .frame(minWidth: 110)
            .controlSize(.large)
    }

    private func permRow(_ name: String, ok: Bool, pane: String, why: String) -> some View {
        HStack {
            Image(systemName: ok ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                .foregroundStyle(ok ? Color.green : Color.orange)
            VStack(alignment: .leading, spacing: 1) {
                Text(name)
                Text(why).font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if !ok { Button("Open System Settings") { Permissions.openPane(pane) } }
        }
    }
}

@MainActor
final class SettingsWindowController: NSObject, NSWindowDelegate {
    let window: NSWindow
    let model: SettingsModel
    var onClosed: (() -> Void)?

    init(settings: AppSettings, onChange: @escaping (AppSettings) -> Void) {
        model = SettingsModel(settings: settings, onChange: onChange)
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 520, height: 640),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        super.init()
        window.title = "ShowTell Settings"
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.contentView = NSHostingView(rootView: SettingsView(m: model))
        window.setContentSize(NSSize(width: 520, height: 640))
        window.center()
    }

    func show() {
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    func windowWillClose(_ notification: Notification) {
        window.delegate = nil
        model.stop()
        onClosed?()
    }
}
