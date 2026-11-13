import AppKit
import QuartzCore
import CoreImage

/// 来通知时屏幕四周亮起一圈流动的紫蓝粉渐变光，然后淡出（类似 ChatGPT / Apple 智能的边缘光）。
/// 一个透明、鼠标穿透的全屏窗口，只在动画期间显示，结束后立刻收起，不占 GPU。
///
/// 图层结构：
///   root（整体淡入淡出）
///   ├─ glowOuter（高斯模糊）→ glowMasked（遮罩：粗描边）→ 旋转的锥形渐变   —— 柔和的外发光
///   └─ lineMasked（遮罩：细描边）→ 旋转的锥形渐变                         —— 贴边的亮线
@MainActor
final class EdgeGlow {
    /// 紫、蓝、粉、橙交替，首尾同色让旋转时看不出接缝。
    static let palette: [NSColor] = [
        NSColor(srgbRed: 0.74, green: 0.51, blue: 0.95, alpha: 1),   // 紫
        NSColor(srgbRed: 0.96, green: 0.73, blue: 0.92, alpha: 1),   // 粉
        NSColor(srgbRed: 0.55, green: 0.62, blue: 1.00, alpha: 1),   // 蓝
        NSColor(srgbRed: 0.67, green: 0.43, blue: 0.93, alpha: 1),   // 深紫
        NSColor(srgbRed: 1.00, green: 0.40, blue: 0.47, alpha: 1),   // 珊瑚红
        NSColor(srgbRed: 1.00, green: 0.73, blue: 0.44, alpha: 1),   // 橙
        NSColor(srgbRed: 0.78, green: 0.53, blue: 1.00, alpha: 1),   // 紫
    ]

    static let duration: CFTimeInterval = 2.4

    private var window: NSWindow?
    private var screenFrame: NSRect = .zero
    private var root: CALayer?
    private var gradients: [CAGradientLayer] = []
    private var hideItem: DispatchWorkItem?

    func flash(on screen: NSScreen) {
        if window == nil || screenFrame != screen.frame { build(for: screen) }
        guard let window, let root else { return }

        CATransaction.begin()
        CATransaction.setDisableActions(true)
        root.removeAllAnimations()
        for g in gradients {
            g.removeAllAnimations()
            let spin = CABasicAnimation(keyPath: "transform.rotation.z")
            spin.fromValue = 0
            spin.toValue = -2 * Double.pi
            spin.duration = 3.2
            spin.repeatCount = .infinity
            g.add(spin, forKey: "spin")
        }
        // 快速亮起，停一会儿，轻轻呼吸一下，再慢慢淡出
        let fade = CAKeyframeAnimation(keyPath: "opacity")
        fade.values = [0, 1, 0.75, 1, 0]
        fade.keyTimes = [0, 0.1, 0.35, 0.55, 1]
        fade.timingFunctions = [CAMediaTimingFunction(name: .easeOut), CAMediaTimingFunction(name: .easeInEaseOut),
                                CAMediaTimingFunction(name: .easeInEaseOut), CAMediaTimingFunction(name: .easeIn)]
        fade.duration = Self.duration
        root.opacity = 0
        root.add(fade, forKey: "fade")
        CATransaction.commit()

        window.orderFrontRegardless()
        hideItem?.cancel()
        let item = DispatchWorkItem { [weak self] in
            MainActor.assumeIsolated {
                self?.window?.orderOut(nil)
                self?.gradients.forEach { $0.removeAllAnimations() }
            }
        }
        hideItem = item
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.duration + 0.05, execute: item)
    }

    private func build(for screen: NSScreen) {
        window?.orderOut(nil)
        screenFrame = screen.frame
        let size = screen.frame.size
        let bounds = CGRect(origin: .zero, size: size)

        let w = NSWindow(contentRect: screen.frame, styleMask: .borderless, backing: .buffered, defer: false)
        w.setFrame(screen.frame, display: false)
        w.level = .screenSaver
        w.isOpaque = false
        w.backgroundColor = .clear
        w.hasShadow = false
        w.ignoresMouseEvents = true
        w.isReleasedWhenClosed = false
        w.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle, .fullScreenAuxiliary]
        w.animationBehavior = .none

        let view = NSView(frame: bounds)
        view.wantsLayer = true
        view.layerUsesCoreImageFilters = true   // 让下面的高斯模糊滤镜生效
        w.contentView = view

        let scale = screen.backingScaleFactor
        let root = CALayer()
        root.frame = bounds
        root.opacity = 0
        root.contentsScale = scale

        // 屏幕圆角：MacBook 显示屏四角大约 10pt
        let corner: CGFloat = 12
        func strokeMask(width: CGFloat) -> CAShapeLayer {
            let m = CAShapeLayer()
            m.frame = bounds
            m.contentsScale = scale
            // 描边以路径为中心，一半在屏幕外，看到的宽度是 width / 2
            m.path = CGPath(roundedRect: bounds, cornerWidth: corner, cornerHeight: corner, transform: nil)
            m.fillColor = nil
            m.strokeColor = NSColor.black.cgColor
            m.lineWidth = width
            return m
        }
        func spinningGradient() -> CAGradientLayer {
            // 正方形、边长等于屏幕对角线，绕中心旋转时总能盖满整个屏幕
            let side = hypot(size.width, size.height)
            let g = CAGradientLayer()
            g.type = .conic
            g.colors = Self.palette.map(\.cgColor)
            g.startPoint = CGPoint(x: 0.5, y: 0.5)
            g.endPoint = CGPoint(x: 0.5, y: 0)
            g.bounds = CGRect(x: 0, y: 0, width: side, height: side)
            g.position = CGPoint(x: size.width / 2, y: size.height / 2)
            g.contentsScale = scale
            return g
        }

        let glowMasked = CALayer()
        glowMasked.frame = bounds
        glowMasked.mask = strokeMask(width: 44)
        let g1 = spinningGradient()
        glowMasked.addSublayer(g1)
        let glowOuter = CALayer()
        glowOuter.frame = bounds
        if let blur = CIFilter(name: "CIGaussianBlur", parameters: [kCIInputRadiusKey: 16]) {
            glowOuter.filters = [blur]
        }
        glowOuter.addSublayer(glowMasked)

        let lineMasked = CALayer()
        lineMasked.frame = bounds
        lineMasked.mask = strokeMask(width: 6)
        let g2 = spinningGradient()
        lineMasked.addSublayer(g2)

        root.addSublayer(glowOuter)
        root.addSublayer(lineMasked)
        view.layer?.addSublayer(root)

        window = w
        self.root = root
        gradients = [g1, g2]
    }
}
