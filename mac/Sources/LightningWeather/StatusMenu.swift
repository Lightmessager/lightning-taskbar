import AppKit
import ServiceManagement
import WeatherCore
import NotifyCore

/// 菜单栏图标和菜单：天气、手动打雷、音效与音量、玻璃通知、开机启动、退出。
/// 对应 Windows 版任务栏右侧的天气/音效按钮和右键菜单。
@MainActor
final class StatusMenu: NSObject, NSMenuDelegate {
    private unowned let app: AppDelegate
    private let item: NSStatusItem
    static let autosaveName = "LightningWeatherStatusItem"
    private let menu = NSMenu()
    private let volumeSlider = NSSlider(value: 0.6, minValue: 0, maxValue: 1, target: nil, action: nil)

    init(app: AppDelegate) {
        self.app = app
        // 新加的菜单栏图标默认排在右侧图标区的最左边——正好紧挨刘海，图标一多就第一个被藏起来。
        // 系统按 “NSStatusItem Preferred Position <autosaveName>”（离屏幕右边的距离）摆放图标，
        // 第一次运行时把它设为 0：紧贴系统图标（控制中心、时钟）的左边，最不容易被刘海挡住。
        // 之后用户按住 ⌘ 拖到别处，系统会记住新位置，这里不再改。
        let defaults = UserDefaults.standard
        if !defaults.bool(forKey: "StatusItemPositioned.v2") {
            defaults.set(0.0, forKey: "NSStatusItem Preferred Position \(Self.autosaveName)")
            defaults.set(true, forKey: "NSStatusItem Visible \(Self.autosaveName)")
            defaults.set(true, forKey: "StatusItemPositioned.v2")
        }
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.autosaveName = Self.autosaveName
        item.isVisible = true
        super.init()
        menu.delegate = self
        item.menu = menu
        volumeSlider.target = self
        volumeSlider.action = #selector(volumeChanged(_:))
        volumeSlider.isContinuous = true
        app.notifications.onAccessChanged = { [weak self] in self?.refresh() }
        refresh()
    }

    /// 图标是否被刘海挡住或被系统藏起来了。
    /// 有刘海的屏幕上，auxiliaryTopRightArea 是刘海右边能放图标的区域；图标不在里面就看不见。
    var iconHidden: Bool {
        guard let win = item.button?.window, item.isVisible else { return true }
        let f = win.frame
        guard let screen = win.screen ?? NSScreen.screens.first(where: { $0.frame.intersects(f) }) else { return true }
        if !win.occlusionState.contains(.visible) && !screen.frame.intersects(f) { return true }
        if let right = screen.auxiliaryTopRightArea {
            // auxiliaryTopRightArea 用的是屏幕本地坐标
            return f.minX - screen.frame.minX < right.minX - 1
        }
        return !screen.frame.contains(f)
    }

    /// 图标的位置（诊断用）。
    var iconFrameDescription: String {
        guard let f = item.button?.window?.frame else { return "无窗口" }
        return "x=\(Int(f.minX)) 宽=\(Int(f.width))"
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
        add("来通知时屏幕边缘闪光", #selector(toggleGlow)).state = s.edgeGlow ? .on : .off
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
        add("设置与诊断…", #selector(openSettings))
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

    @objc private func toggleGlow() { app.update { $0.edgeGlow.toggle() } }

    @objc private func toggleAllScreens() { app.update { $0.allScreens.toggle() } }

    @objc private func openFullDiskAccess() { app.openFullDiskAccess() }
    @objc private func testCard() { app.showTestCard() }
    @objc private func openSettings() { app.showSettings() }
    @objc private func toggleLogin() { app.toggleLoginItem() }
}
