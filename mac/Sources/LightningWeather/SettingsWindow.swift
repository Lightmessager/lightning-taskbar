import AppKit
import ServiceManagement
import WeatherCore
import NotifyCore

/// 设置窗口：和菜单栏菜单的功能一样。
/// 有刘海的 MacBook 上菜单栏图标多了会被藏到刘海后面，所以再次打开应用（双击、聚焦搜索）时弹出这个窗口，首次启动也会弹出一次。
@MainActor
final class SettingsWindow: NSObject, NSWindowDelegate {
    private unowned let app: AppDelegate
    private let window: NSWindow
    private let weatherControl = NSSegmentedControl(labels: ["晴", "雨", "雷雨"], trackingMode: .selectOne, target: nil, action: nil)
    private let soundBox = NSButton(checkboxWithTitle: "音效", target: nil, action: nil)
    private let volume = NSSlider(value: 0.6, minValue: 0, maxValue: 1, target: nil, action: nil)
    private let notifyBox = NSButton(checkboxWithTitle: "用玻璃卡片显示通知", target: nil, action: nil)
    private let cornerPopup = NSPopUpButton()
    private let notifyStatus = NSTextField(wrappingLabelWithString: "")
    private let accessButton = NSButton(title: "打开“完全磁盘访问权限”设置…", target: nil, action: nil)
    private let allScreensBox = NSButton(checkboxWithTitle: "所有显示器都下雨", target: nil, action: nil)
    private let loginBox = NSButton(checkboxWithTitle: "登录时启动", target: nil, action: nil)
    private var statusTimer: Timer?

    init(app: AppDelegate) {
        self.app = app
        window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 440, height: 520),
                          styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
        super.init()
        window.title = "闪电天气"
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.contentView = buildContent()
        window.center()
        refresh()
    }

    func show() {
        refresh()
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
        statusTimer?.invalidate()
        let t = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.refreshStatus() }
        }
        RunLoop.main.add(t, forMode: .common)
        statusTimer = t
    }

    func windowWillClose(_ notification: Notification) {
        statusTimer?.invalidate()
        statusTimer = nil
    }

    var isVisible: Bool { window.isVisible }

    // MARK: 布局

    private func buildContent() -> NSView {
        let icon = NSImageView(image: NSApp.applicationIconImage)
        icon.translatesAutoresizingMaskIntoConstraints = false
        icon.widthAnchor.constraint(equalToConstant: 56).isActive = true
        icon.heightAnchor.constraint(equalToConstant: 56).isActive = true
        let title = NSTextField(labelWithString: "闪电天气")
        title.font = .systemFont(ofSize: 20, weight: .semibold)
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "开发版"
        let subtitle = NSTextField(labelWithString: "版本 \(version)")
        subtitle.textColor = .secondaryLabelColor
        let titles = NSStackView(views: [title, subtitle])
        titles.orientation = .vertical
        titles.alignment = .leading
        titles.spacing = 2
        let header = NSStackView(views: [icon, titles])
        header.spacing = 12

        let hint = note("菜单栏右上角有一朵云的图标。有刘海的 MacBook 上图标太多时它会被藏到刘海后面——这时再次打开“闪电天气”（双击应用或用聚焦搜索）就会显示这个窗口；也可以按住 ⌘ 拖动菜单栏图标，把它挪到左边。")

        weatherControl.target = self
        weatherControl.action = #selector(weatherChanged)
        let strike = NSButton(title: "打个雷", target: self, action: #selector(strikeNow))
        let weatherRow = row("天气", [weatherControl, strike])

        soundBox.target = self
        soundBox.action = #selector(soundToggled)
        volume.target = self
        volume.action = #selector(volumeChanged)
        volume.isContinuous = true
        volume.widthAnchor.constraint(equalToConstant: 200).isActive = true
        let soundRow = row("声音", [soundBox, volume])

        notifyBox.target = self
        notifyBox.action = #selector(notifyToggled)
        cornerPopup.addItems(withTitles: ["左上角", "右上角"])
        cornerPopup.target = self
        cornerPopup.action = #selector(cornerChanged)
        let notifyRow = row("通知", [notifyBox, cornerPopup])
        notifyStatus.textColor = .secondaryLabelColor
        notifyStatus.font = .systemFont(ofSize: 12)
        notifyStatus.preferredMaxLayoutWidth = 380
        accessButton.target = self
        accessButton.action = #selector(openFullDiskAccess)
        let test = NSButton(title: "测试卡片", target: self, action: #selector(testCard))
        let logButton = NSButton(title: "诊断日志", target: self, action: #selector(openLog))
        let notifyButtons = NSStackView(views: [accessButton, test, logButton])
        notifyButtons.spacing = 8
        let notifyHelp = note("macOS 不让第三方应用直接读其它应用的通知，需要在 系统设置 › 隐私与安全性 › 完全磁盘访问权限 里加入“闪电天气”。系统自己的横幅仍会弹出；只想看玻璃卡片的话，把对应应用的提醒样式设为“无”。")

        allScreensBox.target = self
        allScreensBox.action = #selector(allScreensToggled)
        loginBox.target = self
        loginBox.action = #selector(loginToggled)
        let otherRow = row("其它", [allScreensBox, loginBox])

        let quit = NSButton(title: "退出闪电天气", target: NSApp, action: #selector(NSApplication.terminate(_:)))

        let stack = NSStackView(views: [header, hint, separator(), weatherRow, soundRow, separator(),
                                        notifyRow, notifyStatus, notifyButtons, notifyHelp, separator(), otherRow, quit])
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 12
        stack.edgeInsets = NSEdgeInsets(top: 20, left: 24, bottom: 20, right: 24)
        stack.translatesAutoresizingMaskIntoConstraints = false
        stack.setCustomSpacing(16, after: header)

        let container = NSView()
        container.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: container.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: container.trailingAnchor),
            stack.topAnchor.constraint(equalTo: container.topAnchor),
            stack.bottomAnchor.constraint(equalTo: container.bottomAnchor),
            stack.widthAnchor.constraint(equalToConstant: 460),
        ])
        return container
    }

    private func note(_ text: String) -> NSTextField {
        let l = NSTextField(wrappingLabelWithString: text)
        l.font = .systemFont(ofSize: 12)
        l.textColor = .secondaryLabelColor
        l.preferredMaxLayoutWidth = 412
        return l
    }

    private func row(_ label: String, _ views: [NSView]) -> NSStackView {
        let l = NSTextField(labelWithString: label)
        l.font = .systemFont(ofSize: 13, weight: .medium)
        l.alignment = .right
        l.widthAnchor.constraint(equalToConstant: 40).isActive = true
        let r = NSStackView(views: [l] + views)
        r.spacing = 12
        return r
    }

    private func separator() -> NSView {
        let b = NSBox()
        b.boxType = .separator
        b.widthAnchor.constraint(equalToConstant: 412).isActive = true
        return b
    }

    // MARK: 状态

    func refresh() {
        let s = app.settings
        weatherControl.selectedSegment = WeatherMode.allCases.firstIndex(of: app.weather.mode) ?? 0
        soundBox.state = s.sound ? .on : .off
        volume.doubleValue = s.volume
        volume.isEnabled = s.sound
        notifyBox.state = s.glassNotifications ? .on : .off
        cornerPopup.selectItem(at: s.notifyCorner == .left ? 0 : 1)
        allScreensBox.state = s.allScreens ? .on : .off
        loginBox.state = SMAppService.mainApp.status == .enabled ? .on : .off
        refreshStatus()
    }

    private func refreshStatus() {
        notifyStatus.stringValue = "状态：" + app.notifications.statusText
        accessButton.isHidden = app.notifications.access == .ok
    }

    // MARK: 操作

    @objc private func weatherChanged() {
        let modes = WeatherMode.allCases
        let i = weatherControl.selectedSegment
        if modes.indices.contains(i) { app.setMode(modes[i]) }
    }

    @objc private func strikeNow() {
        if app.weather.mode == .off { app.setMode(.storm) }
        app.strikeNow()
    }

    @objc private func soundToggled() { app.update { $0.sound = soundBox.state == .on } }
    @objc private func volumeChanged() {
        let v = volume.doubleValue
        app.update(refreshMenu: false) { $0.volume = v }
    }
    @objc private func notifyToggled() { app.update { $0.glassNotifications = notifyBox.state == .on } }
    @objc private func cornerChanged() { app.update { $0.notifyCorner = cornerPopup.indexOfSelectedItem == 0 ? .left : .right } }
    @objc private func allScreensToggled() { app.update { $0.allScreens = allScreensBox.state == .on } }

    @objc private func loginToggled() {
        app.toggleLoginItem()
        refresh()
    }

    @objc private func openFullDiskAccess() { app.openFullDiskAccess() }
    @objc private func testCard() { app.showTestCard() }

    @objc private func openLog() {
        if !FileManager.default.fileExists(atPath: Log.url.path) { Log.write("打开诊断日志") }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) {
            NSWorkspace.shared.activateFileViewerSelecting([Log.url])
        }
    }
}
