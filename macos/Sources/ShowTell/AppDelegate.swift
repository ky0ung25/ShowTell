import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var settings = AppSettings.load()
    private let hotkey = HotkeyMonitor()
    private var statusItem: NSStatusItem!
    private var toggleItem: NSMenuItem!
    private var recorder: Recorder?
    private var starting = false
    private var overlay: RecordingOverlay?
    private var maxTimer: Timer?
    private var sendWindow: SendWindowController?
    private var settingsWindow: SettingsWindowController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        settings.save() // writes defaults on first run so the file is there to edit
        try? FileManager.default.createDirectory(at: Paths.recordings, withIntermediateDirectories: true)
        setupStatusItem()

        hotkey.configure(settings)
        hotkey.onToggle = { [weak self] in self?.toggle() }
        hotkey.start()

        Recorder.prune(keep: settings.keepRecordings)
        prefetchSpeechModel()
        Log.write("ShowTell started")

        if !Permissions.snapshot().all {
            Permissions.requestMissing()
            openSettings() // first run: shows which permissions are still needed
        }
    }

    /// Opening the app again (Finder, Spotlight, Launchpad) while it runs opens Settings.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        openSettings()
        return false
    }

    private func prefetchSpeechModel() {
        let model = settings.speechModel
        Task.detached(priority: .utility) { await Transcriber.prefetch(model) }
    }

    // ---------------- menu bar ----------------

    private func setupStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        let menu = NSMenu()
        menu.delegate = self
        toggleItem = NSMenuItem(title: "Start recording", action: #selector(toggleFromMenu), keyEquivalent: "")
        toggleItem.target = self
        menu.addItem(toggleItem)
        menu.addItem(.separator())
        let settingsItem = NSMenuItem(title: "Settings…", action: #selector(openSettingsFromMenu), keyEquivalent: ",")
        settingsItem.target = self
        menu.addItem(settingsItem)
        let folder = NSMenuItem(title: "Open recordings folder", action: #selector(openRecordings), keyEquivalent: "")
        folder.target = self
        menu.addItem(folder)
        let reload = NSMenuItem(title: "Reload settings.json", action: #selector(reloadSettings), keyEquivalent: "")
        reload.target = self
        menu.addItem(reload)
        menu.addItem(.separator())
        let quit = NSMenuItem(title: "Quit ShowTell", action: #selector(quit), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)
        statusItem.menu = menu
        updateStatus()
    }

    private func updateStatus() {
        let rec = recorder != nil
        if let button = statusItem.button {
            let img = NSImage(systemSymbolName: rec ? "record.circle.fill" : "record.circle", accessibilityDescription: "ShowTell")
            img?.isTemplate = !rec
            button.image = img
            button.contentTintColor = rec ? .systemRed : nil
            button.toolTip = rec ? "ShowTell — recording" : "ShowTell — \(settings.hotkeyText) ×2 to record"
        }
        toggleItem.title = rec ? "Stop recording (\(settings.hotkeyText) ×2)" : "Start recording (\(settings.hotkeyText) ×2)"
    }

    @objc private func toggleFromMenu() { toggle() }
    @objc private func openSettingsFromMenu() { openSettings() }
    @objc private func openRecordings() { _ = NSWorkspace.shared.open(Paths.recordings) }
    @objc private func reloadSettings() { apply(AppSettings.load()) }
    @objc private func quit() {
        if let rec = recorder {
            recorder = nil
            Task { _ = await rec.stop(); NSApp.terminate(nil) }
            return
        }
        NSApp.terminate(nil)
    }

    // ---------------- recording ----------------

    private func toggle() {
        if recorder != nil { stopRecording() } else { startRecording() }
    }

    private func startRecording() {
        guard !starting, recorder == nil else { return }
        if sendWindow != nil { sendWindow?.show(); return } // finish the previous one first
        starting = true
        let rec = Recorder(settings: settings)
        Task {
            defer { starting = false }
            do {
                try await rec.start()
                recorder = rec
                if settings.playSounds { NSSound(named: "Tink")?.play() }
                var hint = "\(settings.hotkeyText) ×2 to stop"
                if rec.audioError != nil { hint = "no mic, screen only · " + hint }
                if let screen = rec.screen ?? NSScreen.main {
                    let o = RecordingOverlay(screen: screen, elapsed: { [weak rec] in rec?.elapsed ?? 0 }, hint: hint)
                    o.orderFrontRegardless()
                    overlay = o
                }
                maxTimer = Timer.scheduledTimer(withTimeInterval: Double(settings.maxRecordingMinutes) * 60, repeats: false) { [weak self] _ in
                    Task { @MainActor in self?.stopRecording() }
                }
            } catch {
                Log.write("Start failed: \(error)")
                try? FileManager.default.removeItem(at: rec.folder)
                alert("Couldn't start recording",
                      "\(error.localizedDescription)\n\nShowTell needs Screen Recording permission: System Settings → Privacy & Security → Screen & System Audio Recording. Turn on ShowTell, then quit and reopen it.",
                      pane: "Privacy_ScreenCapture")
            }
            updateStatus()
        }
    }

    private func stopRecording() {
        guard let rec = recorder else { return }
        recorder = nil
        maxTimer?.invalidate()
        maxTimer = nil
        overlay?.dismiss()
        overlay = nil
        updateStatus()
        if settings.playSounds { NSSound(named: "Pop")?.play() }

        Task {
            let result = await rec.stop()
            showSendWindow(result)
        }
    }

    private func showSendWindow(_ result: RecordingResult) {
        let targets = AppCatalog.detect(settings)
        let model = SendModel(rec: result, settings: settings, targets: targets)
        let controller = SendWindowController(model: model)
        var pasted = false
        model.onPasted = { [weak self] t in
            pasted = true
            guard let self else { return }
            self.settings.lastTarget = t.name
            self.settings.save()
        }
        model.onSettings = { [weak self] in self?.openSettings() }
        controller.onClosed = { [weak self] in
            guard let self else { return }
            if pasted && self.settings.deleteAfterPaste {
                try? FileManager.default.removeItem(at: result.folder)
                Log.write("Deleted pasted recording \(result.folder.lastPathComponent)")
            }
            Recorder.prune(keep: self.settings.keepRecordings)
            self.sendWindow = nil
        }
        sendWindow = controller
        controller.show()
    }

    // ---------------- settings ----------------

    private func openSettings() {
        if let w = settingsWindow { w.show(); return }
        let w = SettingsWindowController(settings: settings) { [weak self] s in self?.apply(s) }
        w.onClosed = { [weak self] in self?.settingsWindow = nil }
        settingsWindow = w
        w.show()
    }

    private func apply(_ s: AppSettings) {
        let modelChanged = s.speechModel != settings.speechModel
        settings = s
        settings.save()
        hotkey.configure(settings)
        updateStatus()
        if modelChanged { prefetchSpeechModel() }
    }

    private func alert(_ title: String, _ text: String, pane: String? = nil) {
        NSApp.activate(ignoringOtherApps: true)
        let a = NSAlert()
        a.messageText = title
        a.informativeText = text
        a.addButton(withTitle: "OK")
        if pane != nil { a.addButton(withTitle: "Open System Settings") }
        if a.runModal() == .alertSecondButtonReturn, let pane { Permissions.openPane(pane) }
    }
}
