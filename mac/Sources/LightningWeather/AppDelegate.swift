import AppKit
import ServiceManagement
import WeatherCore

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private(set) var settings = Settings.load()
    private(set) var weather: WeatherEngine!
    let audio = AudioOutput()
    private var rainWindows: [RainWindow] = []
    private var statusMenu: StatusMenu!
    private var settingsWindow: SettingsWindow?
    private(set) var notifications: NotificationController!
    private let coverage = CoverageMonitor()
    private let edgeGlow = EdgeGlow()
    private var timer: Timer?
    private var lastTick = ProcessInfo.processInfo.systemUptime

    func applicationDidFinishLaunching(_ notification: Notification) {
        let args = CommandLine.arguments
        if args.contains("--rain") { settings.mode = .rain }
        if args.contains("--storm") { settings.mode = .storm }

        weather = WeatherEngine(mode: settings.mode, now: lastTick)
        weather.onLightning.append { [weak self] s in self?.onLightning(s) }
        weather.onModeChanged.append { [weak self] _ in self?.weatherChanged() }

        audio.start()
        applyAudioSettings()

        rebuildRainWindows()
        NotificationCenter.default.addObserver(self, selector: #selector(screensChanged),
                                               name: NSApplication.didChangeScreenParametersNotification, object: nil)
        // 睡眠唤醒后音频设备可能变了，显示刷新也要重新挂
        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(didWake),
                                                          name: NSWorkspace.didWakeNotification, object: nil)

        coverage.onChange = { [weak self] covered in
            self?.rainWindows.forEach { $0.setCovered(covered[$0.screenID] ?? false) }
        }
        coverage.start()

        notifications = NotificationController(settings: { [weak self] in self?.settings ?? Settings() })
        notifications.onCardShown = { [weak self] screen in
            guard let self, self.settings.edgeGlow else { return }
            self.edgeGlow.flash(on: screen)
        }
        notifications.start()

        statusMenu = StatusMenu(app: self)
        let version = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "dev"
        Log.write("启动 \(version)，macOS \(ProcessInfo.processInfo.operatingSystemVersionString)，\(NSScreen.screens.count) 块屏幕")

        // 首次启动弹出设置窗口：菜单栏图标可能被刘海挡住，得让用户知道应用已经在运行、去哪里找它
        if !settings.welcomed || args.contains("--show-settings") {
            update { $0.welcomed = true }
            showSettings()
        }
        // 菜单栏布局好之后检查图标是否被刘海挡住；挡住了就弹出设置窗口说明怎么办
        DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in
            MainActor.assumeIsolated {
                guard let self else { return }
                let hidden = self.statusMenu.iconHidden
                Log.write("菜单栏图标：\(hidden ? "被挡住" : "可见")（\(self.statusMenu.iconFrameDescription)）")
                if hidden { self.showSettings() }
            }
        }

        // 开发/CI 用：启动时弹一张测试卡片，便于截图检查
        if args.contains("--demo-notification") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 2.2) { [weak self] in
                MainActor.assumeIsolated {
                    self?.notifications.show(.init(appName: "闪电天气", title: "玻璃通知预览",
                                                   body: "这张卡片由 --demo-notification 启动参数生成。",
                                                   icon: NSApp.applicationIconImage, appURL: nil))
                }
            }
        }
        if args.contains("--demo-strike") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 2.5) { [weak self] in
                MainActor.assumeIsolated { self?.strikeNow() }
            }
        }

        // 天气引擎 20 Hz，和 Windows 版一致
        let t = Timer(timeInterval: 0.05, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    /// 应用已在运行时再次打开它（双击、聚焦搜索、启动台）：弹出设置窗口。
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        showSettings()
        return true
    }

    var statusIconHidden: Bool { statusMenu?.iconHidden ?? false }

    func showSettings() {
        if settingsWindow == nil { settingsWindow = SettingsWindow(app: self) }
        settingsWindow?.show()
    }

    func applicationWillTerminate(_ notification: Notification) {
        audio.stop()
    }

    private func tick() {
        let now = ProcessInfo.processInfo.systemUptime
        let dt = min(0.25, now - lastTick)
        lastTick = now
        weather.tick(dt: dt, now: now)
        audio.mixer.rainTarget = Float(weather.audioRain)
        audio.mixer.windTarget = Float(weather.gust)
        for w in rainWindows { w.weatherTick() }
    }

    private func onLightning(_ s: LightningStrike) {
        for w in rainWindows { w.strike(s) }
        // 光速 vs 声速：雷声按距离延迟
        DispatchQueue.main.asyncAfter(deadline: .now() + s.thunderDelay) { [weak self] in
            MainActor.assumeIsolated { self?.audio.mixer.thunder(intensity: s.intensity, pan: s.x, seed: s.seed) }
        }
    }

    private func weatherChanged() {
        settings.mode = weather.mode
        settings.save()
        rainWindows.forEach { $0.weatherTick() }
        statusMenu?.refresh()
        settingsWindow?.refresh()
    }

    // MARK: 菜单调用

    func setMode(_ m: WeatherMode) { weather.setMode(m) }
    func strikeNow() { weather.strike() }

    /// refreshMenu = false：拖音量滑块时不能重建正打开着的菜单，否则滑块会在手底下消失。
    func update(refreshMenu: Bool = true, _ change: (inout Settings) -> Void) {
        let oldAll = settings.allScreens
        change(&settings)
        settings.save()
        applyAudioSettings()
        if oldAll != settings.allScreens { rebuildRainWindows() }
        if refreshMenu {
            statusMenu?.refresh()
            settingsWindow?.refresh()
        }
    }

    func toggleLoginItem() {
        do {
            if SMAppService.mainApp.status == .enabled { try SMAppService.mainApp.unregister() }
            else { try SMAppService.mainApp.register() }
        } catch {
            Log.write("登录项设置失败：\(error)")
        }
        statusMenu?.refresh()
    }

    func openFullDiskAccess() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles") {
            NSWorkspace.shared.open(url)
        }
    }

    func showTestCard() {
        notifications.show(.init(appName: "闪电天气", title: "这是一张测试通知",
                                 body: "真正的通知来自系统通知数据库；点击卡片会打开发通知的应用。",
                                 icon: NSApp.applicationIconImage, appURL: nil))
    }

    private func applyAudioSettings() {
        audio.mixer.muted = !settings.sound
        audio.mixer.masterVolume = Float(settings.volume)
    }

    // MARK: 雨层窗口

    @objc private func screensChanged() { rebuildRainWindows() }

    @objc private func didWake() {
        audio.restart()
        rebuildRainWindows()
    }

    private func rebuildRainWindows() {
        rainWindows.forEach { $0.close() }
        let screens = settings.allScreens ? NSScreen.screens : Array(NSScreen.screens.prefix(1))
        rainWindows = screens.map { RainWindow(screen: $0, weather: weather) }
        coverage.screens = screens
    }
}
