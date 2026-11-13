import AppKit
import QuartzCore
import WeatherCore
import RainLayers

extension NSScreen {
    /// 稳定的显示器编号，用来把遮挡检测结果对应到窗口。
    var displayID: CGDirectDisplayID {
        (deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? 0
    }
}

/// 一块显示器上的雨层：放在“桌面窗口层”之上一级——在壁纸之上、桌面图标和所有普通窗口之下。
/// 鼠标完全穿透，出现在所有桌面空间里。对应 Windows 版挂在 WorkerW 上的 DesktopLayer。
/// 窗口本身透明，雨和压暗直接叠在系统壁纸上（动态壁纸也一样），所以不需要自己画壁纸。
@MainActor
final class RainWindow: NSObject {
    let screenID: CGDirectDisplayID
    private let window: NSWindow
    private let view: NSView
    private let sim: RainSimulation
    private let renderer = RainLayerRenderer()
    private unowned let weather: WeatherEngine
    private var link: CADisplayLink?
    private var lastFrame: CFTimeInterval = 0
    private var covered = false

    init(screen: NSScreen, weather: WeatherEngine) {
        self.weather = weather
        screenID = screen.displayID
        let frame = screen.frame
        sim = RainSimulation(width: frame.width, height: frame.height)

        let w = NSWindow(contentRect: frame, styleMask: .borderless, backing: .buffered, defer: false)
        w.setFrame(frame, display: false)
        w.level = NSWindow.Level(rawValue: Int(CGWindowLevelForKey(.desktopWindow)) + 1)
        w.isOpaque = false
        w.backgroundColor = .clear
        w.hasShadow = false
        w.ignoresMouseEvents = true
        w.isReleasedWhenClosed = false
        w.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenNone]
        w.animationBehavior = .none
        window = w

        let v = NSView(frame: NSRect(origin: .zero, size: frame.size))
        v.wantsLayer = true
        v.layer?.backgroundColor = NSColor.clear.cgColor
        view = v
        super.init()

        renderer.root.contentsScale = screen.backingScaleFactor
        renderer.root.sublayers?.forEach { $0.contentsScale = screen.backingScaleFactor }
        v.layer?.addSublayer(renderer.root)
        w.contentView = v
        w.orderFront(nil)

        // macOS 14 起每个 NSView 都能拿到跟随所在显示器刷新率的 CADisplayLink
        let l = v.displayLink(target: self, selector: #selector(onFrame(_:)))
        l.add(to: .main, forMode: .common)
        link = l
        updateRunning()
    }

    func close() {
        link?.invalidate()
        link = nil
        window.orderOut(nil)
        window.close()
    }

    /// 被全屏或铺满屏幕的窗口挡住时停止绘制，省电（声音照常）。
    func setCovered(_ c: Bool) {
        guard c != covered else { return }
        covered = c
        updateRunning()
    }

    /// 天气引擎每 50ms 调一次：从晴转雨时要重新开始刷新。
    func weatherTick() { updateRunning() }

    func strike(_ s: LightningStrike) {
        sim.strike(s)
        updateRunning()
    }

    private func updateRunning() {
        guard let link else { return }
        let run = !covered && !(weather.mode == .off && weather.intensity < 0.001 && sim.isIdle)
        if run == !link.isPaused { return }
        link.isPaused = !run
        if run { lastFrame = 0 }
    }

    @objc private func onFrame(_ link: CADisplayLink) {
        let now = link.timestamp
        let dt = lastFrame == 0 ? 1.0 / 60 : now - lastFrame
        lastFrame = now
        sim.step(dt: dt, intensity: weather.intensity, wind: weather.wind, mode: weather.mode)
        renderer.update(sim, mode: weather.mode)
        if weather.mode == .off && sim.isIdle { updateRunning() }
    }
}
