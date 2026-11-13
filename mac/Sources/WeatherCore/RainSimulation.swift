import Foundation

/// 二维点，y 轴向下（和 Windows 版一致，画的时候再翻转）。
public struct Pt: Equatable, Sendable {
    public var x: Double, y: Double
    public init(_ x: Double, _ y: Double) { self.x = x; self.y = y }
}

/// 桌面雷雨场景的模拟部分，移植自 Windows 版 Desktop/RainScene.cs：
/// 多层景深雨丝、地面溅射、分叉闪电与亮度曲线、乌云压暗、底部雨雾。
/// 只算数据不画图；RainLayers 把它画成 CALayer。坐标单位是点（pt），y 向下。
public final class RainSimulation {
    public struct Drop { public var x, y, z, endY: Double }
    public struct Splash { public var x, y, t, size: Double }
    /// 一段雨丝。
    public struct Streak { public var x1, y1, x2, y2: Double }

    public private(set) var width: Double
    public private(set) var height: Double
    private let rng: DotNetRandom

    public private(set) var drops: [Drop] = []
    public private(set) var active = 0
    public private(set) var splashes: [Splash] = []

    /// 每帧算好的三层雨丝（远、中、近）。
    public private(set) var streaks: [[Streak]] = [[], [], []]

    /// 闪电折线：[0] 是主干，其余是分叉。
    public private(set) var bolt: [[Pt]] = []
    public private(set) var boltX = 0.5
    private var boltT = 99.0
    private var boltIntensity = 0.0
    private var pulses: [Double] = []

    // 本帧的整体效果
    public private(set) var flash = 0.0
    public private(set) var intensity = 0.0
    public private(set) var darkness = 0.0

    /// 缩放系数：Windows 版按 1080 高设计。
    public var k: Double { height / 1080.0 }

    public init(width: Double, height: Double, seed: Int32 = 0, dropCount: Int = 480) {
        self.width = max(1, width)
        self.height = max(1, height)
        rng = seed == 0 ? DotNetRandom() : DotNetRandom(seed: seed)
        drops = (0..<dropCount).map { _ in Drop(x: 0, y: 0, z: 0, endY: 0) }
        for i in drops.indices { drops[i] = newDrop(anywhere: true) }
    }

    public func resize(width: Double, height: Double) {
        guard width != self.width || height != self.height else { return }
        self.width = max(1, width)
        self.height = max(1, height)
        for i in drops.indices { drops[i] = newDrop(anywhere: true) }
        splashes.removeAll()
    }

    private func newDrop(anywhere: Bool) -> Drop {
        let w = width, h = height
        let z = 0.25 + pow(rng.nextDouble(), 1.6) * 0.75
        let x = rng.nextDouble() * w * 1.3 - w * 0.15
        let y = anywhere ? rng.nextDouble() * h : -rng.nextDouble() * h * 0.25 - 40
        let endY = h * (z > 0.8 ? 0.82 + rng.nextDouble() * 0.2 : 0.45 + rng.nextDouble() * 0.6)
        return Drop(x: x, y: y, z: z, endY: endY)
    }

    // MARK: 闪电

    public func strike(_ s: LightningStrike) {
        boltT = 0
        boltIntensity = s.intensity
        boltX = s.x
        let r = DotNetRandom(seed: s.seed)
        let n = 1 + r.next(3)
        pulses = []
        for i in 0..<n { pulses.append(i == 0 ? 0 : pulses[i - 1] + 0.05 + r.nextDouble() * 0.12) }
        buildBolt(r)
    }

    private func buildBolt(_ r: DotNetRandom) {
        bolt.removeAll()
        let w = width, h = height
        let start = Pt(boltX * w, -20)
        let end = Pt(start.x + (r.nextDouble() - 0.5) * w * 0.25, h * (0.55 + r.nextDouble() * 0.35))
        addBranch(r, start, end, jag: h * 0.09, depth: 0)
    }

    private func addBranch(_ r: DotNetRandom, _ a: Pt, _ b: Pt, jag: Double, depth: Int) {
        // 中点位移法生成锯齿闪电
        var pts = [a, b]
        var off = jag
        for _ in 0..<7 {
            var next: [Pt] = []
            next.reserveCapacity(pts.count * 2)
            for i in 0..<(pts.count - 1) {
                let p = pts[i], q = pts[i + 1]
                let mx = (p.x + q.x) / 2 + (r.nextDouble() - 0.5) * off
                let my = (p.y + q.y) / 2 + (r.nextDouble() - 0.5) * off * 0.25
                next.append(p); next.append(Pt(mx, my))
            }
            next.append(pts[pts.count - 1])
            pts = next
            off *= 0.55
        }
        bolt.append(pts)

        if depth >= 2 { return }
        let branches = depth == 0 ? 2 + r.next(4) : r.next(2)
        for _ in 0..<branches {
            // C# Random.Next(min, max) = min + (int)(Sample() * (max - min))
            let lo = pts.count / 6, hi = pts.count * 2 / 3
            let from = pts[lo + Int(r.nextDouble() * Double(hi - lo))]
            let len = (b.y - a.y) * (0.15 + r.nextDouble() * 0.3) / Double(depth + 1)
            let to = Pt(from.x + (r.nextDouble() - 0.5) * len * 1.4, from.y + len)
            addBranch(r, from, to, jag: jag * 0.45, depth: depth + 1)
        }
    }

    private func boltBrightness() -> Double {
        if boltT > 1.6 { return 0 }
        var b = 0.0
        for p in pulses where boltT >= p { b = max(b, exp(-(boltT - p) / 0.06)) }
        // 余辉
        b = max(b, 0.25 * exp(-boltT / 0.5))
        return b * boltIntensity
    }

    /// 是否还有东西要画（晴天、雨停、闪电熄灭后可以停掉显示刷新）。
    public var isIdle: Bool { intensity < 0.001 && active == 0 && splashes.isEmpty && flash < 0.001 }

    // MARK: 每帧

    public func step(dt rawDt: Double, intensity: Double, wind: Double, mode: WeatherMode) {
        let dt = min(0.05, max(0, rawDt))
        self.intensity = intensity
        let k = self.k

        boltT += dt
        flash = boltBrightness()

        let target = Int(intensity * Double(drops.count))
        if active < target { active = min(target, active + Int(dt * 120) + 1) }
        else if active > target { active = max(target, active - Int(dt * 600) - 1) }

        for i in 0..<3 { streaks[i].removeAll(keepingCapacity: true) }
        for i in 0..<active {
            var d = drops[i]
            let speed = (320 + 460 * d.z) * k   // 慢悠悠的小雨
            let vx = speed * wind
            d.y += speed * dt
            d.x += vx * dt
            if d.y > d.endY {
                if d.z > 0.72 && rng.nextDouble() < 0.5 && splashes.count < 90 {
                    splashes.append(Splash(x: d.x, y: d.endY, t: 0, size: (3 + 6 * d.z) * k))
                }
                drops[i] = newDrop(anywhere: false)
                continue
            }
            drops[i] = d
            let len = (9 + 20 * d.z) * k * (0.7 + intensity * 0.3)
            let layer = d.z < 0.5 ? 0 : d.z < 0.8 ? 1 : 2
            streaks[layer].append(Streak(x1: d.x, y1: d.y, x2: d.x - vx / speed * len, y2: d.y - len))
        }

        var i = splashes.count - 1
        while i >= 0 {
            splashes[i].t += dt
            if splashes[i].t > 0.28 { splashes.remove(at: i) }
            i -= 1
        }

        // 雨天压暗：让壁纸像被乌云遮住
        darkness = intensity * (mode == .storm ? 0.42 : 0.28) * (1 - flash * 0.8)
    }
}
