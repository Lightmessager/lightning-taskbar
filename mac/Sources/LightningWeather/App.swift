import AppKit

@main
@MainActor
enum LightningWeatherMain {
    // NSApplication.delegate 是弱引用，要自己留住
    static var delegate: AppDelegate?

    static func main() {
        let app = NSApplication.shared
        let d = AppDelegate()
        delegate = d
        app.delegate = d
        // 只在菜单栏显示，不进 Dock、不进 Cmd+Tab（Info.plist 里也设了 LSUIElement）
        app.setActivationPolicy(.accessory)
        app.run()
    }
}
