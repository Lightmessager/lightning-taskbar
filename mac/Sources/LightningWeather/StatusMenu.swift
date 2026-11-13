import AppKit
import ServiceManagement
import WeatherCore
import NotifyCore

/// 菜单栏图标和菜单：天气、手动打雷、音效与音量、玻璃通知、开机启动、退出。
/// 对应 Windows 版任务栏右侧的天气/音效按钮和右键菜单。
@MainActor
final class StatusMenu: NSObject, NSMenuDelegate {
    private unowned let app: AppDelegate
    private let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    private let menu = NSMenu()
    private let volumeSlider = NSSlider(value: 0.6, minValue: 0, maxValue: 1, target: nil, action: nil)

    init(app: AppDelegate) {
        self.app = app
        super.init()
        menu.delegate = self
        item.menu = menu
        volumeSlider.target = self
        volumeSlider.action = #selector(volumeChanged(_:))
        volumeSlider.isContinuous = true
        app.notifications.onAccessChanged = { [weak self] in self?.refresh() }
        refresh()
    }

    func refresh() {
        let symbol: String
        switch app.weather.mode {
        case .off: symbol = "sun.max.fill"
        case .rain: symbol = "cloud.rain.fill"
        case .storm: symbol = "cloud.bolt.rain.fill"
        }
        let image = NSImage(systemSymbolName: symbol, accessibilityDescription: "闪电天气")
        image?.isTemplate = true
        item.button?.image = image
        item.button?.toolTip = "闪电天气"
        rebuild()
    }

    func menuWillOpen(_ menu: NSMenu) { rebuild() }

    private func rebuild() {
        let s = app.settings
        menu.removeAllItems()

        menu.addItem(header("天气"))
        for (mode, title) in [(WeatherMode.off, "晴"), (.rain, "雨"), (.storm, "雷雨")] {
            let i = add(title, #selector(setMode(_:)))
            i.representedObject = mode.rawValue
            i.state = app.weather.mode == mode ? .on : .off
        }
        add("打个雷", #selector(strike))

        menu.addItem(.separator())
        add("音效", #selector(toggleSound)).state = s.sound ? .on : .off
        let sliderItem = NSMenuItem()
        let box = NSView(frame: NSRect(x: 0, y: 0, width: 220, height: 28))
        volumeSlider.frame = NSRect(x: 20, y: 4, width: 180, height: 20)
        volumeSlider.doubleValue = s.volume
        volumeSlider.isEnabled = s.sound
        box.addSubview(volumeSlider)
        sliderItem.view = box
        menu.addItem(sliderItem)

        menu.addItem(.separator())
        add("玻璃通知", #selector(toggleNotifications)).state = s.glassNotifications ? .on : .off
        let corner = NSMenuItem(title: "通知位置", action: nil, keyEquivalent: "")
        let sub = NSMenu()
        for (c, title) in [(TopCorner.left, "左上角"), (.right, "右上角")] {
            let i = NSMenuItem(title: title, action: #selector(setCorner(_:)), keyEquivalent: "")
            i.target = self
            i.representedObject = c.rawValue
            i.state = s.notifyCorner == c ? .on : .off
            sub.addItem(i)
        }
        corner.submenu = sub
        menu.addItem(corner)
        switch app.notifications.access {
        case .ok: break
        case .denied:
            add("需要“完全磁盘访问权限”才能读取通知…", #selector(openFullDiskAccess))
        case .notFound:
            let i = NSMenuItem(title: "此系统版本找不到通知数据库", action: nil, keyEquivalent: "")
            i.isEnabled = false
            menu.addItem(i)
        }
        add("测试通知卡片", #selector(testCard))

        menu.addItem(.separator())
        add("所有显示器都下雨", #selector(toggleAllScreens)).state = s.allScreens ? .on : .off
        add("登录时启动", #selector(toggleLogin)).state = SMAppService.mainApp.status == .enabled ? .on : .off

        menu.addItem(.separator())
        let quit = NSMenuItem(title: "退出闪电天气", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        menu.addItem(quit)
    }

    private func header(_ title: String) -> NSMenuItem {
        if #available(macOS 14.0, *) { return NSMenuItem.sectionHeader(title: title) }
        let i = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        i.isEnabled = false
        return i
    }

    @discardableResult
    private func add(_ title: String, _ action: Selector) -> NSMenuItem {
        let i = NSMenuItem(title: title, action: action, keyEquivalent: "")
        i.target = self
        menu.addItem(i)
        return i
    }

    @objc private func setMode(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let m = WeatherMode(rawValue: raw) else { return }
        app.setMode(m)
    }

    @objc private func strike() {
        if app.weather.mode == .off { app.setMode(.storm) }
        app.strikeNow()
    }

    @objc private func toggleSound() { app.update { $0.sound.toggle() } }

    @objc private func volumeChanged(_ sender: NSSlider) {
        let v = sender.doubleValue
        app.update(refreshMenu: false) { $0.volume = v }
    }

    @objc private func toggleNotifications() { app.update { $0.glassNotifications.toggle() } }

    @objc private func setCorner(_ sender: NSMenuItem) {
        guard let raw = sender.representedObject as? String, let c = TopCorner(rawValue: raw) else { return }
        app.update { $0.notifyCorner = c }
    }

    @objc private func toggleAllScreens() { app.update { $0.allScreens.toggle() } }

    @objc private func openFullDiskAccess() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles") {
            NSWorkspace.shared.open(url)
        }
    }

    @objc private func testCard() {
        app.notifications.show(.init(appName: "闪电天气", title: "这是一张测试通知",
                                     body: "真正的通知来自系统通知数据库；点击卡片会打开发通知的应用。",
                                     icon: NSApp.applicationIconImage, appURL: nil))
    }

    @objc private func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled { try SMAppService.mainApp.unregister() }
            else { try SMAppService.mainApp.register() }
        } catch {
            NSLog("LightningWeather: 登录项设置失败 \(error)")
        }
        rebuild()
    }
}
