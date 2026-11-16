import AppKit
import SwiftUI

@MainActor
final class SendModel: ObservableObject {
    let rec: RecordingResult
    let settings: AppSettings
    let targets: [InstalledTarget]
    let thumbs: [NSImage?]

    @Published var excluded: Set<URL> = []
    @Published var appIndex = 0 { didSet { if appIndex != oldValue { loadThreads() } } }
    @Published var threads: [ThreadChoice] = []
    @Published var threadIndex = 0
    @Published var instructions = ""
    @Published var status = ""
    @Published var error: String?
    @Published var busy = false

    var onHide: (() -> Void)?
    var onShow: (() -> Void)?
    var onClose: (() -> Void)?
    var onPasted: ((InstalledTarget) -> Void)?
    var onSettings: (() -> Void)?

    private var transcript: Task<[TranscriptSegment], Never>?
    private var transcriptError: String?
    private var loadVersion = 0
    private var sendTask: Task<Void, Never>?

    init(rec: RecordingResult, settings: AppSettings, targets: [InstalledTarget]) {
        self.rec = rec
        self.settings = settings
        self.targets = targets
        self.thumbs = rec.frames.map { NSImage(contentsOf: $0.url) }
        if let i = targets.firstIndex(where: { $0.name == settings.lastTarget }) { appIndex = i }
        if targets.isEmpty {
            error = "No supported AI desktop app found (Claude, Codex, Grok). Install one, or add it to \"targets\" in settings.json."
        }
        loadThreads()
        startTranscription()
    }

    var meta: String {
        let n = rec.frames.count - excluded.count
        let frames = excluded.isEmpty ? "\(rec.frames.count) frame\(rec.frames.count == 1 ? "" : "s")" : "\(n) of \(rec.frames.count) frames"
        return "\(formatTime(rec.duration)) · \(frames)"
    }

    func toggle(_ f: KeyFrame) {
        if excluded.contains(f.url) { excluded.remove(f.url) } else { excluded.insert(f.url) }
    }

    private func startTranscription() {
        guard let audio = rec.audioURL else {
            status = "No microphone audio: \(rec.audioError ?? "unknown")"
            transcript = Task { [TranscriptSegment]() }
            return
        }
        status = Transcriber.isDownloaded(settings.speechModel) ? "Transcribing…" : "Downloading the speech model (first time only)…"
        let model = settings.speechModel
        transcript = Task<[TranscriptSegment], Never> { [weak self] in
            do {
                let segs = try await Transcriber.transcribe(audio, model: model)
                if let self, !self.busy {
                    self.status = segs.isEmpty ? "Transcript ready (no speech detected)" : "Transcript ready · \(segs.count) segment\(segs.count == 1 ? "" : "s")"
                }
                return segs
            } catch {
                Log.write("Transcription failed: \(error)")
                self?.transcriptError = error.localizedDescription
                if let self, !self.busy { self.status = "Transcription failed: \(error.localizedDescription)" }
                return []
            }
        }
    }

    func loadThreads() {
        guard targets.indices.contains(appIndex) else { threads = []; return }
        let t = targets[appIndex]
        loadVersion += 1
        let version = loadVersion
        let newChat = ThreadChoice(kind: .newChat, label: "New chat")
        guard let app = AppCatalog.runningApp(t.target) else {
            threads = [ThreadChoice(kind: .newChat, label: "New chat (\(t.name) will open)")]
            threadIndex = 0
            return
        }
        threads = [newChat, ThreadChoice(kind: .current, label: "Currently open chat"), ThreadChoice(kind: .newChat, label: "Loading recent chats…")]
        threadIndex = 0
        let pid = app.processIdentifier
        Task { [weak self] in
            let (title, names) = await Task.detached { (Automation.currentTitle(pid), Automation.listThreads(pid)) }.value
            guard let self, version == self.loadVersion else { return }
            let keepCurrent = self.threadIndex == 1 // the user picked "currently open" while loading
            var items = [newChat, ThreadChoice(kind: .current, label: title.isEmpty ? "Currently open chat" : "Currently open: \(Self.trim(title, 48))")]
            items += names.map { ThreadChoice(kind: .existing, label: Self.trim($0, 60), name: $0) }
            self.threads = items
            self.threadIndex = keepCurrent ? 1 : 0
        }
    }

    static func trim(_ s: String, _ max: Int) -> String { s.count <= max ? s : String(s.prefix(max - 1)) + "…" }

    func send() {
        guard !busy, targets.indices.contains(appIndex) else { return }
        let target = targets[appIndex]
        var thread = threads.indices.contains(threadIndex) ? threads[threadIndex] : ThreadChoice(kind: .newChat, label: "New chat")
        if thread.label.hasPrefix("Loading") { thread = ThreadChoice(kind: .newChat, label: "New chat") }

        busy = true
        error = nil
        sendTask = Task { [weak self] in
            guard let self else { return }
            var prompt: String?
            do {
                self.status = "Finishing transcript…"
                let segs = await self.transcript?.value ?? []
                let frames = self.rec.frames.filter { !self.excluded.contains($0.url) }
                let p = PromptBuilder.build(instructions: self.instructions, duration: self.rec.duration, frames: frames,
                                            audioError: self.rec.audioError, transcript: segs, transcriptError: self.transcriptError)
                prompt = p
                Sender.saveArtifacts(self.rec, prompt: p, transcript: segs)
                self.onHide?()
                try await Sender.send(target: target, thread: thread, frames: frames, text: p, settings: self.settings)
                self.onPasted?(target)
                self.onClose?()
            } catch {
                Log.write("Send failed: \(error)")
                var msg = error.localizedDescription
                if let p = prompt {
                    Automation.setClipboardText(p)
                    msg += "  The prompt text is on your clipboard; the frames are in the recording folder."
                }
                self.onShow?()
                self.error = msg
                self.status = ""
                self.busy = false
            }
        }
    }

    func cancel() {
        if busy { return }
        transcript?.cancel()
        onClose?()
    }
}

struct SendView: View {
    @ObservedObject var m: SendModel

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 9) {
                Circle().fill(Color(red: 0.9, green: 0.28, blue: 0.3)).frame(width: 9, height: 9)
                Text("Send recording").font(.system(size: 16, weight: .semibold))
                Text(m.meta).font(.system(size: 13)).foregroundStyle(.secondary)
                Spacer()
                Button("Settings") { m.onSettings?() }
                    .help("Change the shortcut keys, speech model and more")
            }
            .padding(.bottom, 14)

            ScrollView(.horizontal, showsIndicators: true) {
                HStack(spacing: 8) {
                    ForEach(Array(m.rec.frames.enumerated()), id: \.element.url) { i, f in
                        ZStack(alignment: .bottomLeading) {
                            if let img = m.thumbs[i] {
                                Image(nsImage: img).resizable().aspectRatio(contentMode: .fit).frame(height: 72)
                            } else {
                                Rectangle().fill(Color.gray.opacity(0.3)).frame(width: 128, height: 72)
                            }
                            Text(formatTime(f.at))
                                .font(.system(size: 10)).foregroundStyle(.white)
                                .padding(.horizontal, 4).padding(.vertical, 1)
                                .background(Color.black.opacity(0.63))
                                .padding(4)
                        }
                        .clipShape(RoundedRectangle(cornerRadius: 6))
                        .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color(nsColor: .separatorColor)))
                        .opacity(m.excluded.contains(f.url) ? 0.25 : 1)
                        .onTapGesture { m.toggle(f) }
                        .help("Click to include / leave out")
                    }
                }
                .padding(.bottom, 4)
            }
            Text("Click a frame to leave it out.").font(.system(size: 11)).foregroundStyle(.secondary).padding(.bottom, 14)

            HStack(alignment: .top, spacing: 12) {
                VStack(alignment: .leading, spacing: 4) {
                    label("AI APP")
                    Picker("", selection: $m.appIndex) {
                        ForEach(Array(m.targets.enumerated()), id: \.offset) { i, t in Text(t.name).tag(i) }
                    }
                    .labelsHidden()
                    .frame(width: 190)
                }
                VStack(alignment: .leading, spacing: 4) {
                    label("CHAT THREAD")
                    Picker("", selection: $m.threadIndex) {
                        ForEach(Array(m.threads.enumerated()), id: \.offset) { i, t in Text(t.label).tag(i) }
                    }
                    .labelsHidden()
                }
            }
            .padding(.bottom, 14)

            label("INSTRUCTIONS").padding(.bottom, 4)
            ZStack(alignment: .topLeading) {
                InstructionsEditor(text: $m.instructions, onSubmit: { m.send() }, onCancel: { m.cancel() })
                if m.instructions.isEmpty {
                    Text("e.g. Recreate the style of this website as a Next.js page")
                        .font(.system(size: 14)).foregroundStyle(.tertiary)
                        .padding(.leading, 9).padding(.top, 7)
                        .allowsHitTesting(false)
                }
            }
            .frame(minHeight: 84, maxHeight: 200)
            .background(RoundedRectangle(cornerRadius: 8).fill(Color(nsColor: .textBackgroundColor)))
            .overlay(RoundedRectangle(cornerRadius: 8).stroke(Color(nsColor: .separatorColor)))

            HStack(alignment: .center) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(m.status).font(.system(size: 12)).foregroundStyle(.secondary).lineLimit(1).truncationMode(.tail)
                    HStack(spacing: 4) {
                        Text("Return to paste · Shift+Return for a new line · Esc to close ·")
                        Button("open folder") { _ = NSWorkspace.shared.open(m.rec.folder) }.buttonStyle(.link)
                    }
                    .font(.system(size: 11)).foregroundStyle(.tertiary)
                }
                Spacer(minLength: 12)
                Button(m.busy ? "Working…" : "Paste into chat") { m.send() }
                    .buttonStyle(.borderedProminent)
                    .tint(Color(red: 0.9, green: 0.28, blue: 0.3))
                    .controlSize(.large)
                    .disabled(m.busy || m.targets.isEmpty)
            }
            .padding(.top, 14)

            if let e = m.error {
                Text(e).font(.system(size: 12)).foregroundStyle(Color(red: 0.85, green: 0.2, blue: 0.25))
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 10)
            }
        }
        .padding(22)
        .frame(width: 620)
        .disabled(m.busy)
        .onExitCommand { m.cancel() }
    }

    private func label(_ s: String) -> some View {
        Text(s).font(.system(size: 11, weight: .semibold)).foregroundStyle(.secondary).kerning(0.6)
    }
}

/// Multi-line text box: Return submits, Shift+Return adds a line, Esc cancels.
struct InstructionsEditor: NSViewRepresentable {
    @Binding var text: String
    var onSubmit: () -> Void
    var onCancel: () -> Void

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.drawsBackground = false
        scroll.borderType = .noBorder

        let tv = SubmitTextView(frame: NSRect(x: 0, y: 0, width: 560, height: 84))
        tv.isRichText = false
        tv.allowsUndo = true
        tv.font = .systemFont(ofSize: 14)
        tv.drawsBackground = false
        tv.textContainerInset = NSSize(width: 4, height: 7)
        tv.isVerticallyResizable = true
        tv.isHorizontallyResizable = false
        tv.autoresizingMask = [.width]
        tv.minSize = NSSize(width: 0, height: 0)
        tv.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        tv.textContainer?.widthTracksTextView = true
        tv.isAutomaticQuoteSubstitutionEnabled = false
        tv.isAutomaticDashSubstitutionEnabled = false
        tv.delegate = context.coordinator
        tv.onSubmit = onSubmit
        tv.onCancel = onCancel
        tv.string = text
        scroll.documentView = tv
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) { tv.window?.makeFirstResponder(tv) }
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        guard let tv = scroll.documentView as? SubmitTextView else { return }
        if tv.string != text { tv.string = text }
        tv.onSubmit = onSubmit
        tv.onCancel = onCancel
    }

    final class Coordinator: NSObject, NSTextViewDelegate {
        var parent: InstructionsEditor
        init(_ p: InstructionsEditor) { parent = p }
        func textDidChange(_ notification: Notification) {
            guard let tv = notification.object as? NSTextView else { return }
            parent.text = tv.string
        }
    }
}

final class SubmitTextView: NSTextView {
    var onSubmit: (() -> Void)?
    var onCancel: (() -> Void)?

    override func keyDown(with event: NSEvent) {
        switch event.keyCode {
        case 36, 76: // Return, keypad Enter
            if event.modifierFlags.contains(.shift) { insertNewlineIgnoringFieldEditor(nil); return }
            if !hasMarkedText() { onSubmit?(); return }
        case 53: // Esc
            onCancel?()
            return
        default:
            break
        }
        super.keyDown(with: event)
    }
}

/// Window hosting the send dialog. Floats above other apps and comes to the front when shown.
@MainActor
final class SendWindowController: NSObject, NSWindowDelegate {
    let window: NSWindow
    let model: SendModel
    var onClosed: (() -> Void)?

    init(model: SendModel) {
        self.model = model
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 620, height: 480),
                          styleMask: [.titled, .closable, .fullSizeContentView], backing: .buffered, defer: false)
        super.init()
        window.title = "ShowTell"
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.isMovableByWindowBackground = true
        window.level = .floating
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.contentView = NSHostingView(rootView: SendView(m: model).padding(.top, 8))
        window.setContentSize(window.contentView?.fittingSize ?? NSSize(width: 620, height: 480))

        model.onHide = { [weak self] in self?.window.orderOut(nil) }
        model.onShow = { [weak self] in self?.show() }
        model.onClose = { [weak self] in self?.window.close() }
    }

    func show() {
        if !window.isVisible { centerOnMouseScreen() }
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    private func centerOnMouseScreen() {
        let mouse = NSEvent.mouseLocation
        guard let scr = NSScreen.screens.first(where: { NSMouseInRect(mouse, $0.frame, false) }) ?? NSScreen.main else { window.center(); return }
        let f = window.frame, vf = scr.visibleFrame
        window.setFrameOrigin(NSPoint(x: vf.midX - f.width / 2, y: vf.midY - f.height / 2 + vf.height * 0.08))
    }

    func windowWillClose(_ notification: Notification) {
        window.delegate = nil
        onClosed?()
    }
}
