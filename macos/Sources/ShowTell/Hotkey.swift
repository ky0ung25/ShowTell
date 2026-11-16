import AppKit
import CoreGraphics

/// Pure state machine for "press keyA+keyB together, twice in a row". No system calls, so it can be self-tested.
/// Unlike Windows, nothing is swallowed: on a Mac F1/F2 have no default action worth hiding.
final class ChordDetector {
    var keyA = 122
    var keyB = 120
    var windowMs = 500
    var onToggle: (() -> Void)?

    private var aDown = false, bDown = false, chordActive = false
    private var lastChordAt = Int64.min / 2

    func reset() {
        aDown = false; bDown = false; chordActive = false
        lastChordAt = Int64.min / 2
    }

    func onKey(_ code: Int, up: Bool, nowMs: Int64) {
        guard keyA != keyB, code == keyA || code == keyB else { return }
        let isA = code == keyA
        if !up {
            let wasDown = isA ? aDown : bDown
            if isA { aDown = true } else { bDown = true }
            if wasDown { return } // auto-repeat
            if aDown && bDown && !chordActive {
                chordActive = true
                if nowMs - lastChordAt <= Int64(windowMs) {
                    lastChordAt = Int64.min / 2
                    onToggle?()
                } else {
                    lastChordAt = nowMs
                }
            }
        } else {
            if isA { aDown = false } else { bDown = false }
            if !aDown && !bDown { chordActive = false }
        }
    }
}

/// Global listen-only keyboard event tap that drives a ChordDetector.
/// Needs Input Monitoring (or Accessibility) permission; until it's granted, it retries every few seconds.
final class HotkeyMonitor {
    let detector = ChordDetector()
    var onToggle: (() -> Void)?
    private var tap: CFMachPort?
    private var source: CFRunLoopSource?
    private var retry: Timer?

    var isActive: Bool { tap != nil }

    init() {
        detector.onToggle = { [weak self] in
            DispatchQueue.main.async { self?.onToggle?() }
        }
    }

    func configure(_ s: AppSettings) {
        detector.keyA = s.keyA
        detector.keyB = s.keyB
        detector.windowMs = s.doubleTapWindowMs
        detector.reset()
    }

    func start() {
        if install() { return }
        Log.write("Keyboard monitor not allowed yet (Input Monitoring / Accessibility); retrying")
        retry?.invalidate()
        retry = Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { [weak self] t in
            if self?.install() ?? true { t.invalidate() }
        }
    }

    private func install() -> Bool {
        if tap != nil { return true }
        var mask: CGEventMask = 0
        for t in [CGEventType.keyDown, .keyUp, .flagsChanged] { mask |= CGEventMask(1) << CGEventMask(t.rawValue) }
        mask |= CGEventMask(1) << 14 // NX_SYSDEFINED: brightness / media keys (F1, F2 … on Apple keyboards)

        let me = Unmanaged.passUnretained(self).toOpaque()
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap, place: .headInsertEventTap, options: .listenOnly,
            eventsOfInterest: mask,
            callback: { _, type, event, info in
                if let info {
                    Unmanaged<HotkeyMonitor>.fromOpaque(info).takeUnretainedValue().handle(type: type, event: event)
                }
                return Unmanaged.passUnretained(event)
            },
            userInfo: me)
        else { return false }

        let src = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), src, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        self.tap = tap
        self.source = src
        Log.write("Keyboard monitor installed")
        return true
    }

    private func now() -> Int64 { Int64(ProcessInfo.processInfo.systemUptime * 1000) }

    private func handle(type: CGEventType, event: CGEvent) {
        switch type {
        case .tapDisabledByTimeout, .tapDisabledByUserInput:
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
        case .keyDown, .keyUp:
            if event.getIntegerValueField(.keyboardEventAutorepeat) != 0 { return }
            detector.onKey(Int(event.getIntegerValueField(.keyboardEventKeycode)), up: type == .keyUp, nowMs: now())
        case .flagsChanged:
            let code = Int(event.getIntegerValueField(.keyboardEventKeycode))
            guard let flag = KeyNames.modifierFlag(code) else { return }
            detector.onKey(code, up: !event.flags.contains(flag), nowMs: now())
        default:
            guard type.rawValue == 14, let ns = NSEvent(cgEvent: event), let key = KeyNames.mediaKey(ns) else { return }
            detector.onKey(key.0, up: key.1, nowMs: now())
        }
    }
}

enum KeyNames {
    /// US-layout virtual key codes for letters and digits.
    static let letters: [Character: Int] = [
        "a": 0, "s": 1, "d": 2, "f": 3, "h": 4, "g": 5, "z": 6, "x": 7, "c": 8, "v": 9, "b": 11, "q": 12,
        "w": 13, "e": 14, "r": 15, "y": 16, "t": 17, "1": 18, "2": 19, "3": 20, "4": 21, "6": 22, "5": 23,
        "9": 25, "7": 26, "8": 28, "0": 29, "o": 31, "u": 32, "i": 34, "p": 35, "l": 37, "j": 38, "k": 40,
        "n": 45, "m": 46,
    ]

    static let names: [Int: String] = {
        var n: [Int: String] = [
            122: "F1", 120: "F2", 99: "F3", 118: "F4", 96: "F5", 97: "F6", 98: "F7", 100: "F8", 101: "F9",
            109: "F10", 103: "F11", 111: "F12", 105: "F13", 107: "F14", 113: "F15", 106: "F16", 64: "F17",
            79: "F18", 80: "F19", 90: "F20",
            55: "Left ⌘", 54: "Right ⌘", 56: "Left ⇧", 60: "Right ⇧", 58: "Left ⌥", 61: "Right ⌥",
            59: "Left ⌃", 62: "Right ⌃", 63: "fn", 57: "Caps Lock",
            49: "Space", 53: "Esc", 48: "Tab", 36: "Return", 51: "Delete", 117: "Forward Delete",
            115: "Home", 119: "End", 116: "Page Up", 121: "Page Down", 123: "←", 124: "→", 125: "↓", 126: "↑",
            50: "`", 27: "-", 24: "=", 33: "[", 30: "]", 42: "\\", 41: ";", 39: "'", 43: ",", 47: ".", 44: "/",
        ]
        for (ch, code) in letters { n[code] = String(ch).uppercased() }
        return n
    }()

    static func name(_ code: Int) -> String { names[code] ?? "Key \(code)" }

    static func modifierFlag(_ code: Int) -> CGEventFlags? {
        switch code {
        case 55, 54: return .maskCommand
        case 56, 60: return .maskShift
        case 58, 61: return .maskAlternate
        case 59, 62: return .maskControl
        case 63: return .maskSecondaryFn
        case 57: return .maskAlphaShift
        default: return nil
        }
    }

    /// Apple keyboards send F1/F2/… as brightness and media keys unless fn is held. Map them back to F-key codes.
    static let mediaToFKey: [Int: Int] = [
        3: 122, 2: 120,   // brightness down / up -> F1 / F2
        22: 96, 21: 97,   // keyboard light down / up -> F5 / F6
        20: 98, 16: 100, 19: 101, // previous / play / next -> F7 / F8 / F9
        7: 109, 1: 103, 0: 111,   // mute / volume down / up -> F10 / F11 / F12
    ]

    /// (F-key code, isKeyUp) for a system-defined media key event, or nil.
    static func mediaKey(_ e: NSEvent) -> (Int, Bool)? {
        guard e.type == .systemDefined, e.subtype.rawValue == 8 else { return nil }
        let data1 = e.data1
        let keyType = (data1 & 0xFFFF_0000) >> 16
        let state = (data1 & 0xFF00) >> 8
        guard let code = mediaToFKey[keyType], state == 0x0A || state == 0x0B else { return nil }
        return (code, state == 0x0B)
    }

    /// The key a settings "press a key" capture should record for this event, or nil to ignore it.
    static func capturedKey(_ e: NSEvent) -> Int? {
        switch e.type {
        case .keyDown:
            return e.isARepeat ? nil : Int(e.keyCode)
        case .flagsChanged:
            let code = Int(e.keyCode)
            guard let flag = modifierFlag(code) else { return nil }
            return CGEventFlags(rawValue: UInt64(e.modifierFlags.rawValue)).contains(flag) ? code : nil
        case .systemDefined:
            if let key = mediaKey(e), !key.1 { return key.0 }
            return nil
        default:
            return nil
        }
    }
}
