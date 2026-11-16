import AppKit

@main
struct ShowTellApp {
    @MainActor private static var delegate: AppDelegate?

    @MainActor static func main() {
        let args = CommandLine.arguments
        if args.contains("--selftest") {
            exit(SelfTest.run(args))
        }
        let app = NSApplication.shared
        let d = AppDelegate()
        delegate = d // NSApplication holds its delegate weakly
        app.delegate = d
        app.setActivationPolicy(.accessory) // menu bar app: no Dock icon
        app.run()
    }
}
