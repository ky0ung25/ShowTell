import AppKit
import ApplicationServices

struct InstalledTarget: Hashable {
    let target: TargetApp
    let appURL: URL?
    var name: String { target.name }
}

/// Finds which supported AI apps are installed (/Applications, ~/Applications, Launch Services) or running.
enum AppCatalog {
    static func detect(_ s: AppSettings) -> [InstalledTarget] {
        let fm = FileManager.default
        let dirs = ["/Applications", NSHomeDirectory() + "/Applications", "/Applications/Utilities"]
        var found: [InstalledTarget] = []
        for t in s.targets {
            var url: URL?
            for id in t.bundleIds where url == nil {
                url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: id)
            }
            for n in t.appNames where url == nil {
                for d in dirs where url == nil {
                    let p = URL(fileURLWithPath: d).appendingPathComponent(n + ".app")
                    if fm.fileExists(atPath: p.path) { url = p }
                }
            }
            if url != nil || runningApp(t) != nil { found.append(InstalledTarget(target: t, appURL: url)) }
        }
        Log.write("Detected targets: " + found.map { "\($0.name)=\($0.appURL?.lastPathComponent ?? "running")" }.joined(separator: ", "))
        return found
    }

    static func runningApp(_ t: TargetApp) -> NSRunningApplication? {
        let apps = NSWorkspace.shared.runningApplications.filter { $0.activationPolicy == .regular }
        for id in t.bundleIds {
            if let a = apps.first(where: { $0.bundleIdentifier == id }) { return a }
        }
        return apps.first { a in t.appNames.contains { $0 == a.localizedName } }
    }
}

enum ThreadKind { case newChat, current, existing }

struct ThreadChoice: Hashable {
    let kind: ThreadKind
    let label: String
    var name: String? = nil
}

/// Accessibility (AX) helpers that drive a target app's window: list threads, open one, focus the composer, paste.
/// Needs the Accessibility permission. Electron/Chromium apps build their AX tree only after AXManualAccessibility is set.
enum Automation {
    // ---------- AX basics ----------

    static func attr(_ e: AXUIElement, _ name: String) -> CFTypeRef? {
        var v: CFTypeRef?
        return AXUIElementCopyAttributeValue(e, name as CFString, &v) == .success ? v : nil
    }

    static func element(_ v: CFTypeRef?) -> AXUIElement? {
        guard let v, CFGetTypeID(v) == AXUIElementGetTypeID() else { return nil }
        return (v as! AXUIElement)
    }

    static func string(_ e: AXUIElement, _ name: String) -> String? {
        (attr(e, name) as? String)?.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func children(_ e: AXUIElement) -> [AXUIElement] {
        (attr(e, kAXChildrenAttribute) as? [AXUIElement]) ?? []
    }

    static func role(_ e: AXUIElement) -> String { string(e, kAXRoleAttribute) ?? "" }

    /// Frame in global screen coordinates, origin at the top-left of the main display.
    static func frame(_ e: AXUIElement) -> CGRect? {
        guard let p = attr(e, kAXPositionAttribute), let s = attr(e, kAXSizeAttribute),
              CFGetTypeID(p) == AXValueGetTypeID(), CFGetTypeID(s) == AXValueGetTypeID() else { return nil }
        var pt = CGPoint.zero, sz = CGSize.zero
        AXValueGetValue(p as! AXValue, .cgPoint, &pt)
        AXValueGetValue(s as! AXValue, .cgSize, &sz)
        return CGRect(origin: pt, size: sz)
    }

    /// Visible name: title, description, value, or the text inside it.
    static func label(_ e: AXUIElement) -> String {
        for a in [kAXTitleAttribute, kAXDescriptionAttribute, kAXValueAttribute] {
            if let s = string(e, a), !s.isEmpty { return s }
        }
        var parts: [String] = []
        collectText(e, depth: 0, into: &parts)
        return parts.joined(separator: " ").trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func collectText(_ e: AXUIElement, depth: Int, into parts: inout [String]) {
        guard depth < 4, parts.joined().count < 200 else { return }
        for c in children(e) {
            if role(c) == "AXStaticText", let v = string(c, kAXValueAttribute), !v.isEmpty { parts.append(v) }
            else { collectText(c, depth: depth + 1, into: &parts) }
        }
    }

    static func appElement(_ pid: pid_t) -> AXUIElement {
        let app = AXUIElementCreateApplication(pid)
        AXUIElementSetMessagingTimeout(app, 2)
        // Chromium/Electron only expose their web content to AX when asked.
        AXUIElementSetAttributeValue(app, "AXManualAccessibility" as CFString, kCFBooleanTrue)
        return app
    }

    static func mainWindow(_ pid: pid_t) -> AXUIElement? {
        let app = appElement(pid)
        for a in [kAXFocusedWindowAttribute, kAXMainWindowAttribute] {
            if let w = element(attr(app, a)) { return w }
        }
        let wins = (attr(app, kAXWindowsAttribute) as? [AXUIElement]) ?? []
        return wins.max { area(frame($0)) < area(frame($1)) }
    }

    private static func area(_ r: CGRect?) -> CGFloat { r.map { $0.width * $0.height } ?? 0 }

    struct Node {
        let element: AXUIElement
        let role: String
        let frame: CGRect
    }

    /// Breadth-first walk collecting elements with the given roles (bounded so a huge tree can't hang us).
    static func find(_ root: AXUIElement, roles: Set<String>, limit: Int = 8000) -> [Node] {
        var out: [Node] = []
        var queue: [AXUIElement] = [root]
        var head = 0
        while head < queue.count && head < limit {
            let e = queue[head]
            head += 1
            let r = role(e)
            if roles.contains(r), let f = frame(e) { out.append(Node(element: e, role: r, frame: f)) }
            queue.append(contentsOf: children(e))
        }
        return out
    }

    static let clickableRoles: Set<String> = ["AXButton", "AXLink", "AXRadioButton", "AXTab", "AXMenuItem"]

    static func clickables(_ win: AXUIElement) -> [Node] {
        var all = find(win, roles: clickableRoles)
        if all.count < 5 {
            // Chromium builds its accessibility tree lazily; the first query wakes it up.
            Thread.sleep(forTimeInterval: 1.2)
            all = find(win, roles: clickableRoles)
        }
        return all
    }

    // ---------- threads ----------

    static let notThreads: Set<String> = [
        "new", "new chat", "new thread", "new task", "new conversation", "new session", "search", "search chats",
        "artifacts", "customize", "projects", "recents", "recent", "chats", "code", "library", "settings", "home",
        "images", "explore", "plugins", "scheduled", "show more", "show less", "view all", "see all",
        "more navigation items", "this computer", "starred", "pinned", "today", "yesterday", "previous 7 days",
        "previous 30 days", "upgrade", "upgrade plan", "help", "menu", "back", "forward", "hide sidebar",
        "show sidebar", "open sidebar", "close sidebar", "toggle sidebar", "skip to content", "tasks", "workspaces",
        "automations", "skills", "voice", "imagine", "history", "companions", "files", "close", "minimize", "zoom",
        "full screen",
    ]

    static let notThreadPrefixes = [
        "new ", "open profile", "switch mode", "chat sidebar", "project actions", "project sidebar", "start new",
        "more options", "add new", "group and sort", "update available", "pin chat", "archive chat", "delete", "rename",
    ]

    static func isThreadName(_ name: String) -> Bool {
        let n = name.lowercased()
        guard name.count >= 2, name.count <= 160, !notThreads.contains(n) else { return false }
        return !notThreadPrefixes.contains { n.hasPrefix($0) }
    }

    struct Candidate {
        let element: AXUIElement
        let name: String
        let frame: CGRect
    }

    static func sidebarItems(_ pid: pid_t) -> [Candidate] {
        guard let win = mainWindow(pid), let wr = frame(win) else { return [] }
        let sidebarRight = wr.minX + min(wr.width * 0.4, 520)
        var seen = Set<String>()
        var out: [Candidate] = []
        for n in clickables(win) {
            let r = n.frame
            if r.width < 140 || r.height < 16 || r.height > 72 { continue }
            if r.minX > sidebarRight || r.maxX > sidebarRight + 40 { continue }
            if r.minY < wr.minY + 40 || r.maxY > wr.maxY - 4 { continue } // title bar / scrolled out
            let name = label(n.element)
            guard isThreadName(name), seen.insert(name).inserted else { continue }
            out.append(Candidate(element: n.element, name: name, frame: r))
        }
        return out.sorted { $0.frame.minY < $1.frame.minY }
    }

    static func listThreads(_ pid: pid_t) -> [String] {
        Array(sidebarItems(pid).map(\.name).prefix(40))
    }

    static func currentTitle(_ pid: pid_t) -> String {
        guard let win = mainWindow(pid) else { return "" }
        return string(win, kAXTitleAttribute) ?? ""
    }

    @discardableResult
    static func activate(_ e: AXUIElement, frame: CGRect?) -> Bool {
        if AXUIElementPerformAction(e, kAXPressAction as CFString) == .success { return true }
        guard let r = frame ?? self.frame(e) else { return false }
        click(CGPoint(x: r.minX + min(r.width / 2, 60), y: r.midY))
        return true
    }

    static func openThread(_ pid: pid_t, name: String) -> Bool {
        guard let item = sidebarItems(pid).first(where: { $0.name == name }) else { return false }
        return activate(item.element, frame: item.frame)
    }

    static func startNewChat(_ pid: pid_t, target: TargetApp) -> Bool {
        if let win = mainWindow(pid), let wr = frame(win) {
            let all = clickables(win)
            for wanted in target.newChatButtonNames {
                let hit = all
                    .filter { wr.contains(CGPoint(x: $0.frame.midX, y: $0.frame.midY)) && label($0.element).caseInsensitiveCompare(wanted) == .orderedSame }
                    .min { ($0.frame.minY, $0.frame.minX) < ($1.frame.minY, $1.frame.minX) }
                if let hit, activate(hit.element, frame: hit.frame) { return true }
            }
        }
        if let shortcut = parseShortcut(target.newChatShortcut) {
            sendKey(shortcut.0, flags: shortcut.1)
            return true
        }
        return false
    }

    static func parseShortcut(_ s: String) -> (CGKeyCode, CGEventFlags)? {
        var flags: CGEventFlags = []
        var key: Int?
        for raw in s.lowercased().split(separator: "+") {
            let part = raw.trimmingCharacters(in: .whitespaces)
            switch part {
            case "cmd", "command", "⌘": flags.insert(.maskCommand)
            case "shift", "⇧": flags.insert(.maskShift)
            case "alt", "option", "opt", "⌥": flags.insert(.maskAlternate)
            case "ctrl", "control", "⌃": flags.insert(.maskControl)
            default:
                if part.count == 1, let ch = part.first, let code = KeyNames.letters[ch] { key = code }
                else if let code = KeyNames.names.first(where: { $0.value.lowercased() == part })?.key { key = code }
            }
        }
        guard let key else { return nil }
        return (CGKeyCode(key), flags)
    }

    /// The chat input: the lowest wide text field in the lower part of the window.
    static func findComposer(_ pid: pid_t) -> Node? {
        guard let win = mainWindow(pid), let wr = frame(win) else { return nil }
        return find(win, roles: ["AXTextArea", "AXTextField"])
            .filter { $0.frame.width >= 200 && $0.frame.minY >= wr.minY + wr.height * 0.35 && $0.frame.maxY <= wr.maxY }
            .max { $0.frame.maxY < $1.frame.maxY }
    }

    /// Put keyboard focus in the chat input.
    static func focusComposer(_ pid: pid_t) -> Bool {
        guard let c = findComposer(pid) else { return false }
        if AXUIElementSetAttributeValue(c.element, kAXFocusedAttribute as CFString, kCFBooleanTrue) == .success,
           (attr(c.element, kAXFocusedAttribute) as? Bool) == true {
            return true
        }
        click(CGPoint(x: c.frame.midX, y: c.frame.midY))
        return true
    }

    // ---------- input ----------

    static func click(_ p: CGPoint) {
        let src = CGEventSource(stateID: .hidSystemState)
        let old = CGEvent(source: nil)?.location
        CGEvent(mouseEventSource: src, mouseType: .leftMouseDown, mouseCursorPosition: p, mouseButton: .left)?.post(tap: .cghidEventTap)
        CGEvent(mouseEventSource: src, mouseType: .leftMouseUp, mouseCursorPosition: p, mouseButton: .left)?.post(tap: .cghidEventTap)
        if let old { CGWarpMouseCursorPosition(old) }
    }

    static func sendKey(_ code: CGKeyCode, flags: CGEventFlags) {
        let src = CGEventSource(stateID: .hidSystemState)
        let down = CGEvent(keyboardEventSource: src, virtualKey: code, keyDown: true)
        down?.flags = flags
        down?.post(tap: .cghidEventTap)
        let up = CGEvent(keyboardEventSource: src, virtualKey: code, keyDown: false)
        up?.flags = flags
        up?.post(tap: .cghidEventTap)
    }

    static func pressPaste() { sendKey(9, flags: .maskCommand) } // ⌘V

    /// Wait until the user has physically let go of Return / modifiers so nothing leaks into the target app.
    static func waitForKeysReleased() async {
        let keys: [CGKeyCode] = [36, 76, 56, 60, 55, 54, 58, 61, 59, 62]
        for _ in 0..<100 {
            if !keys.contains(where: { CGEventSource.keyState(.combinedSessionState, key: $0) }) { return }
            try? await Task.sleep(nanoseconds: 20_000_000)
        }
    }

    // ---------- clipboard ----------

    static func setClipboardImage(_ url: URL) throws {
        guard let img = NSImage(contentsOf: url), let tiff = img.tiffRepresentation,
              let rep = NSBitmapImageRep(data: tiff), let png = rep.representation(using: .png, properties: [:])
        else { throw NSError(domain: "ShowTell", code: 2, userInfo: [NSLocalizedDescriptionKey: "Couldn't read frame \(url.lastPathComponent)"]) }
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.declareTypes([.png, .tiff], owner: nil)
        pb.setData(png, forType: .png)
        pb.setData(tiff, forType: .tiff)
    }

    static func setClipboardText(_ s: String) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(s, forType: .string)
    }
}
