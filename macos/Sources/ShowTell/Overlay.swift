import AppKit

/// Small "● 0:12" pill at the top of the recorded screen. Click-through, and left out of the frames
/// (the recorder excludes all of ShowTell's windows from the capture).
final class RecordingOverlay: NSPanel {
    private let pill: PillView
    private var timer: Timer?

    init(screen: NSScreen, elapsed: @escaping () -> TimeInterval, hint: String) {
        pill = PillView(elapsed: elapsed, hint: hint)
        let size = pill.preferredSize
        let vf = screen.visibleFrame
        let rect = NSRect(x: vf.midX - size.width / 2, y: vf.maxY - size.height - 10, width: size.width, height: size.height)
        super.init(contentRect: rect, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        level = .statusBar
        ignoresMouseEvents = true
        isReleasedWhenClosed = false
        sharingType = .none
        collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
        pill.frame = NSRect(origin: .zero, size: size)
        contentView = pill
        timer = Timer.scheduledTimer(withTimeInterval: 0.08, repeats: true) { [weak self] _ in
            self?.pill.needsDisplay = true
        }
    }

    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    func dismiss() {
        timer?.invalidate()
        timer = nil
        orderOut(nil)
    }
}

final class PillView: NSView {
    private let elapsed: () -> TimeInterval
    private let hint: String
    private let born = Date()
    private let timeAttrs: [NSAttributedString.Key: Any] = [
        .font: NSFont.monospacedDigitSystemFont(ofSize: 13, weight: .semibold),
        .foregroundColor: NSColor.white,
    ]
    private let hintAttrs: [NSAttributedString.Key: Any] = [
        .font: NSFont.systemFont(ofSize: 12),
        .foregroundColor: NSColor(white: 0.72, alpha: 1),
    ]

    init(elapsed: @escaping () -> TimeInterval, hint: String) {
        self.elapsed = elapsed
        self.hint = hint
        super.init(frame: .zero)
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    var preferredSize: NSSize {
        let tw = ("00:00" as NSString).size(withAttributes: timeAttrs).width
        let hw = (hint as NSString).size(withAttributes: hintAttrs).width
        return NSSize(width: ceil(14 + 10 + 8 + tw + 10 + hw + 14), height: 32)
    }

    override func draw(_ dirtyRect: NSRect) {
        let r = bounds.height / 2
        NSColor(srgbRed: 0.094, green: 0.094, blue: 0.106, alpha: 0.93).setFill()
        NSBezierPath(roundedRect: bounds, xRadius: r, yRadius: r).fill()

        // Pulsing red dot.
        let t = Date().timeIntervalSince(born) / 0.7
        let pulse = 0.25 + 0.75 * (0.5 + 0.5 * cos(Double.pi * t))
        NSColor(srgbRed: 0.898, green: 0.282, blue: 0.302, alpha: CGFloat(pulse)).setFill()
        let d: CGFloat = 10
        var x: CGFloat = 14
        NSBezierPath(ovalIn: NSRect(x: x, y: (bounds.height - d) / 2, width: d, height: d)).fill()
        x += d + 8

        let time = formatTime(elapsed()) as NSString
        let ts = time.size(withAttributes: timeAttrs)
        time.draw(at: NSPoint(x: x, y: (bounds.height - ts.height) / 2), withAttributes: timeAttrs)
        x += ts.width + 10

        let h = hint as NSString
        let hs = h.size(withAttributes: hintAttrs)
        h.draw(at: NSPoint(x: x, y: (bounds.height - hs.height) / 2), withAttributes: hintAttrs)
    }
}
