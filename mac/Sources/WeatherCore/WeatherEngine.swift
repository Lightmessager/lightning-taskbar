import Foundation

public struct LightningStrike: Equatable, Sendable {
    /// 水平位置 0..1（也用作雷声声像）。
    public var x: Double
    public var intensity: Double
    /// 光速 vs 声速：雷声比闪电晚多少秒。
    public var thunderDelay: Double
    public var seed: Int32

    public init(x: Double, intensity: Double, thunderDelay: Double, seed: Int32) {
        self.x = x; self.intensity = intensity; self.thunderDelay = thunderDelay; self.seed = seed
    }
}

/// 天气状态中心，移植自 Windows 版 Weather.cs 的 WeatherEngine：
/// 桌面雨层、音效都订阅这里，保证闪电与雷声同步。
/// 不自己开定时器：由调用方每隔一小段时间调用 tick，并传入单调时钟，便于测试。
public final class WeatherEngine {
    public private(set) var mode: WeatherMode
    /// 雨量 0..1，切换模式时平滑过渡。
    public private(set) var intensity = 0.0
    /// 风（雨丝倾斜），-1..1，缓慢漂移。
    public private(set) var wind = 0.1
    /// 阵风 0..1：大部分时间为 0，偶尔起一阵再慢慢平息（只用于风声）。
    public private(set) var gust = 0.0

    public var onModeChanged: [(WeatherMode) -> Void] = []
    public var onLightning: [(LightningStrike) -> Void] = []

    private let rng: DotNetRandom
    private var now: TimeInterval
    private var nextStrike: TimeInterval = 0
    private var windTarget = 0.1, gustTarget = 0.0
    private var gustUntil = -Double.infinity
    private var nextGust: TimeInterval

    public init(mode: WeatherMode, now: TimeInterval, seed: Int32 = 0) {
        self.mode = mode
        self.now = now
        rng = seed == 0 ? DotNetRandom() : DotNetRandom(seed: seed)
        nextGust = now + 40
        scheduleNext(3, 8)
    }

    public func setMode(_ m: WeatherMode) {
        guard m != mode else { return }
        mode = m
        if m == .storm { scheduleNext(2, 6) }
        onModeChanged.forEach { $0(m) }
    }

    /// 音频用的目标雨量：小雨时声音也小一些。
    public var audioRain: Double { intensity * (mode == .storm ? 1.0 : 0.7) }

    public func tick(dt: Double, now: TimeInterval) {
        self.now = now
        let target: Double = mode == .storm ? 1.0 : mode == .rain ? 0.55 : 0
        intensity += (target - intensity) * min(1, dt * 0.6)
        if abs(target - intensity) < 0.002 { intensity = target }

        if rng.nextDouble() < dt * 0.08 {
            windTarget = (mode == .storm ? 0.2 : 0.08) * (rng.nextDouble() * 1.6 - 0.3)
        }
        wind += (windTarget - wind) * dt * 0.3

        if mode != .off && now >= nextGust {
            gustTarget = (mode == .storm ? 0.55 : 0.3) * (0.6 + rng.nextDouble() * 0.4)
            gustUntil = now + 8 + rng.nextDouble() * 14
            nextGust = gustUntil + 45 + rng.nextDouble() * 90
        }
        if now >= gustUntil || mode == .off { gustTarget = 0 }
        // 起风快一点，停风慢一点
        gust += (gustTarget - gust) * dt * (gustTarget > gust ? 0.35 : 0.12)

        if mode == .storm && now >= nextStrike {
            strike()
            // 偶尔连闪
            if rng.nextDouble() < 0.3 { scheduleNext(0.25, 1.2) } else { scheduleNext(6, 20) }
        }
    }

    /// 打一道闪电（菜单里的“打个雷”也走这里）。
    public func strike() {
        let intensity = 0.45 + rng.nextDouble() * 0.55
        // 越亮越近，雷声延迟越短
        let delay = (1.0 - intensity) * 3.2 + rng.nextDouble() * 0.4
        let s = LightningStrike(x: 0.08 + rng.nextDouble() * 0.84, intensity: intensity, thunderDelay: delay, seed: rng.next())
        onLightning.forEach { $0(s) }
    }

    private func scheduleNext(_ lo: Double, _ hi: Double) {
        nextStrike = now + lo + rng.nextDouble() * (hi - lo)
    }
}
