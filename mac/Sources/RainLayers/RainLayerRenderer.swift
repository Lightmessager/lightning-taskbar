import QuartzCore
import CoreGraphics
import WeatherCore

/// 把 RainSimulation 画成一棵 CALayer 树：压暗、云层照亮、三层雨丝、溅射、闪电辉光、底部雨雾。
/// 颜色和线宽都取自 Windows 版 RainScene.cs。所有图层由 GPU 合成，每帧只换路径和不透明度。
/// 模拟坐标 y 向下，这里统一换成 Core Animation 的 y 向上。
public final class RainLayerRenderer {
    public let root = CALayer()

    private let dark = CALayer()
    private let glow = CAGradientLayer()
    private let wash = CALayer()
    private let rain: [CAShapeLayer]
    private let splashRings: [CAShapeLayer]
    private let splashDots: [CAShapeLayer]
    private let boltMain: [CAShapeLayer]
    private let boltBranch: [CAShapeLayer]
    private let fog = CAGradientLayer()

    private static let splashLevels = 8

    private static func rgba(_ r: Int, _ g: Int, _ b: Int, _ a: Double) -> CGColor {
        CGColor(srgbRed: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255, alpha: CGFloat(a))
    }

    /// 闪电的四层描边：外层宽而淡做辉光，最里面是白色的芯。
    private static let boltStyles: [(width: Double, alpha: Double, color: CGColor)] = [
        (22, 0.07, rgba(140, 170, 255, 1)),
        (9, 0.2, rgba(170, 195, 255, 1)),
        (3.2, 0.65, rgba(225, 235, 255, 1)),
        (1.3, 1.0, rgba(255, 255, 255, 1)),
    ]

    public init() {
        root.masksToBounds = true
        root.backgroundColor = nil

        dark.backgroundColor = Self.rgba(8, 12, 24, 1)
        dark.opacity = 0

        glow.type = .radial
        glow.colors = [Self.rgba(200, 215, 255, 150.0 / 255), Self.rgba(200, 215, 255, 0)]
        glow.opacity = 0

        wash.backgroundColor = Self.rgba(220, 230, 255, 70.0 / 255)
        wash.opacity = 0

        let rainStyles: [(Int, Int, Int, Int, Double)] = [
            (46, 205, 225, 255, 0.8), (74, 205, 225, 255, 1.1), (110, 215, 232, 255, 1.6),
        ]
        rain = rainStyles.map { a, r, g, b, w in
            let l = CAShapeLayer()
            l.fillColor = nil
            l.strokeColor = Self.rgba(r, g, b, Double(a) / 255)
            l.lineWidth = CGFloat(w)
            l.lineCap = .butt
            return l
        }

        splashRings = (0..<Self.splashLevels).map { level in
            let l = CAShapeLayer()
            l.fillColor = nil
            l.strokeColor = Self.rgba(205, 225, 255, Double(level) * 120 / Double(Self.splashLevels - 1) / 255)
            return l
        }
        splashDots = (0..<Self.splashLevels).map { level in
            let l = CAShapeLayer()
            l.strokeColor = nil
            l.fillColor = Self.rgba(205, 225, 255, Double(level) * 120 / Double(Self.splashLevels - 1) / 255)
            return l
        }

        func boltLayers() -> [CAShapeLayer] {
            Self.boltStyles.map { style in
                let l = CAShapeLayer()
                l.fillColor = nil
                l.strokeColor = style.color
                l.lineJoin = .round
                l.lineCap = .round
                l.opacity = 0
                return l
            }
        }
        boltMain = boltLayers()
        boltBranch = boltLayers()

        fog.colors = [Self.rgba(120, 135, 155, 70.0 / 255), Self.rgba(120, 135, 155, 0)]
        // y 向上：startPoint 在下（浓），endPoint 在上（透明）
        fog.startPoint = CGPoint(x: 0.5, y: 0)
        fog.endPoint = CGPoint(x: 0.5, y: 1)
        fog.opacity = 0

        // 绘制顺序与 Windows 版一致
        for l in [dark, glow, wash] { root.addSublayer(l) }
        rain.forEach { root.addSublayer($0) }
        splashRings.forEach { root.addSublayer($0) }
        splashDots.forEach { root.addSublayer($0) }
        boltBranch.forEach { root.addSublayer($0) }
        boltMain.forEach { root.addSublayer($0) }
        root.addSublayer(fog)

        // 全部关掉隐式动画，否则每帧的属性变化都会被插值成 0.25 秒的过渡
        let none: [String: CAAction] = ["path": NSNull(), "opacity": NSNull(), "bounds": NSNull(), "position": NSNull(),
                                         "hidden": NSNull(), "lineWidth": NSNull(), "startPoint": NSNull(), "endPoint": NSNull(),
                                         "frame": NSNull(), "contents": NSNull(), "sublayers": NSNull()]
        root.actions = none
        root.sublayers?.forEach { $0.actions = none }
    }

    /// 根据当前模拟状态更新所有图层。
    public func update(_ sim: RainSimulation, mode: WeatherMode) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        defer { CATransaction.commit() }

        let w = sim.width, h = sim.height, k = sim.k
        let bounds = CGRect(x: 0, y: 0, width: w, height: h)
        if root.bounds != bounds {
            root.bounds = bounds
            root.position = CGPoint(x: w / 2, y: h / 2)
            for l in root.sublayers ?? [] { l.frame = bounds }
            fog.frame = CGRect(x: 0, y: 0, width: w, height: h * 0.38)
        }
        @inline(__always) func fy(_ y: Double) -> CGFloat { CGFloat(h - y) }

        let flash = sim.flash, intensity = sim.intensity
        dark.opacity = Float(sim.darkness > 0.004 ? sim.darkness : 0)

        // 闪电照亮云层
        if flash > 0.01 {
            let cx = sim.boltX, cy = 1 - 0.05
            glow.startPoint = CGPoint(x: cx, y: cy)
            glow.endPoint = CGPoint(x: cx + 0.7, y: cy + 0.9)
            glow.opacity = Float(flash)
            wash.opacity = Float(flash)
        } else {
            glow.opacity = 0
            wash.opacity = 0
        }

        // 雨丝
        for (i, layer) in rain.enumerated() {
            let streaks = sim.streaks[i]
            if streaks.isEmpty { layer.path = nil; continue }
            let p = CGMutablePath()
            for s in streaks {
                p.move(to: CGPoint(x: s.x1, y: fy(s.y1)))
                p.addLine(to: CGPoint(x: s.x2, y: fy(s.y2)))
            }
            layer.path = p
        }

        // 溅射：按透明度分档，每档一条路径
        let levels = Self.splashLevels
        let rings = (0..<levels).map { _ in CGMutablePath() }
        let dots = (0..<levels).map { _ in CGMutablePath() }
        var used = [Bool](repeating: false, count: levels)
        for s in sim.splashes {
            let p = s.t / 0.28
            let rx = s.size * (0.4 + p * 1.4)
            let level = max(0, min(levels - 1, Int((1 - p) * Double(levels - 1))))
            used[level] = true
            rings[level].addEllipse(in: CGRect(x: s.x - rx, y: Double(fy(s.y)) - rx * 0.28, width: rx * 2, height: rx * 0.56))
            // 两颗飞溅的小水珠
            let up = sin(p * Double.pi) * s.size * 1.6
            let r1 = 0.9 * k + 0.2, r2 = 0.8 * k + 0.2
            dots[level].addEllipse(in: CGRect(x: s.x - rx * 0.7 - r1, y: Double(fy(s.y - up)) - r1, width: r1 * 2, height: r1 * 2))
            dots[level].addEllipse(in: CGRect(x: s.x + rx * 0.8 - r2, y: Double(fy(s.y - up * 0.8)) - r2, width: r2 * 2, height: r2 * 2))
        }
        for i in 0..<levels {
            splashRings[i].path = used[i] ? rings[i] : nil
            splashRings[i].lineWidth = CGFloat(0.9 * k + 0.3)
            splashDots[i].path = used[i] ? dots[i] : nil
        }

        // 闪电本体：多层描边模拟辉光
        if flash > 0.02 && !sim.bolt.isEmpty {
            let main = CGMutablePath(), branches = CGMutablePath()
            for (bi, line) in sim.bolt.enumerated() {
                let path = bi == 0 ? main : branches
                path.move(to: CGPoint(x: line[0].x, y: fy(line[0].y)))
                for pt in line.dropFirst() { path.addLine(to: CGPoint(x: pt.x, y: fy(pt.y))) }
            }
            for (i, style) in Self.boltStyles.enumerated() {
                boltMain[i].path = main
                boltMain[i].lineWidth = CGFloat(style.width * k)
                boltMain[i].opacity = Float(min(1, style.alpha * flash * 1.4))
                boltBranch[i].path = sim.bolt.count > 1 ? branches : nil
                boltBranch[i].lineWidth = CGFloat(style.width * k * 0.6)
                boltBranch[i].opacity = Float(min(1, style.alpha * flash * 0.55 * 1.4))
            }
        } else {
            for l in boltMain + boltBranch { l.opacity = 0; l.path = nil }
        }

        // 底部雨雾
        fog.opacity = Float(intensity > 0.01 ? intensity : 0)
    }
}
