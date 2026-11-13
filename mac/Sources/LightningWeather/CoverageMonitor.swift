import AppKit

/// 每秒检查一次各显示器是否被全屏或铺满的窗口挡住（对应 Windows 版的“最大化/全屏时暂停桌面雨层”）。
/// 只读窗口的位置和层级，不读窗口内容，所以不需要“屏幕录制”权限。
@MainActor
final class CoverageMonitor {
    var screens: [NSScreen] = []
    var onChange: (([CGDirectDisplayID: Bool]) -> Void)?
    private var timer: Timer?
    private var last: [CGDirectDisplayID: Bool] = [:]

    func start() {
        let t = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.check() }
        }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    private func check() {
        guard let info = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]],
              let primary = NSScreen.screens.first else { return }
        let myPID = ProcessInfo.processInfo.processIdentifier
        var result: [CGDirectDisplayID: Bool] = [:]
        for screen in screens {
            // NSScreen 坐标原点在主屏左下，CGWindow 坐标原点在主屏左上
            let f = screen.frame
            let cgFrame = CGRect(x: f.minX, y: primary.frame.maxY - f.maxY, width: f.width, height: f.height)
            var covered = false
            for w in info {
                guard (w[kCGWindowLayer as String] as? Int) == 0,
                      (w[kCGWindowOwnerPID as String] as? Int32) != myPID,
                      (w[kCGWindowAlpha as String] as? Double ?? 1) > 0.9,
                      let bd = w[kCGWindowBounds as String] as? NSDictionary,
                      let b = CGRect(dictionaryRepresentation: bd as CFDictionary) else { continue }
                let overlap = b.intersection(cgFrame)
                if !overlap.isNull && overlap.width * overlap.height >= cgFrame.width * cgFrame.height * 0.9 {
                    covered = true
                    break
                }
            }
            result[screen.displayID] = covered
        }
        if result != last {
            last = result
            onChange?(result)
        }
    }
}
