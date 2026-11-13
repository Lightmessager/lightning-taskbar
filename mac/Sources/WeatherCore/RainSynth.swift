import Foundation

/// 雨声合成（逐采样），逐行移植自 Windows 版 Audio/RainSynth.cs。
/// 雨幕：低沉柔和的粉噪，先高通再两级低通；
/// 雨滴：极短噪声冲击 + 随机频段带通，没有正弦音高，听起来是“沙沙、嗒嗒”。
public final class RainSynth {
    public static let sampleRate = 44100

    private let rng: DotNetRandom

    // 雨幕
    private var b0L: Float = 0, b1L: Float = 0, b2L: Float = 0, b0R: Float = 0, b1R: Float = 0, b2R: Float = 0
    private var lp1L: Float = 0, lp2L: Float = 0, lp1R: Float = 0, lp2R: Float = 0, hpL: Float = 0, hpR: Float = 0
    private static let hp: Float = 1 - expf(-2 * Float.pi * 150 / Float(sampleRate))
    private static let lp: Float = 1 - expf(-2 * Float.pi * 2200 / Float(sampleRate))
    private var gust: Float = 1, gustTarget: Float = 1
    private var gustTimer = 0

    // 雨滴：每颗是一个短噪声包络激励的 SVF 带通
    private struct Drop {
        var on = false
        var amp: Float = 0, decay: Float = 0, f: Float = 0, q: Float = 0, low: Float = 0, band: Float = 0, pan: Float = 0
    }
    private var drops = [Drop](repeating: Drop(), count: 40)

    /// seed = 0 表示不固定种子。
    public init(seed: Int32 = 0) {
        rng = seed == 0 ? DotNetRandom() : DotNetRandom(seed: seed)
    }

    @inline(__always)
    private func noise() -> Float { Float(rng.nextDouble() * 2 - 1) }

    /// - Parameter rain: 雨量 0..1
    public func next(_ rain: Float) -> (l: Float, r: Float) {
        if rain < 0.001 { return (0, 0) }

        // 阵风：几秒一次的缓慢起伏
        gustTimer -= 1
        if gustTimer <= 0 {
            gustTimer = RainSynth.sampleRate * (2 + rng.next(5))
            gustTarget = 0.75 + Float(rng.nextDouble()) * 0.45
        }
        gust += (gustTarget - gust) * 0.00002

        // ---- 雨幕：Paul Kellet 粉噪 → 高通 ~150 Hz → 两级 ~2.2 kHz 低通 ----
        let wl = noise(), wr = noise()
        b0L = 0.99765 * b0L + wl * 0.0990460; b1L = 0.96300 * b1L + wl * 0.2965164; b2L = 0.57000 * b2L + wl * 1.0526913
        b0R = 0.99765 * b0R + wr * 0.0990460; b1R = 0.96300 * b1R + wr * 0.2965164; b2R = 0.57000 * b2R + wr * 1.0526913
        let pinkL = (b0L + b1L + b2L + wl * 0.1848) * 0.11
        let pinkR = (b0R + b1R + b2R + wr * 0.1848) * 0.11
        hpL += (pinkL - hpL) * RainSynth.hp; hpR += (pinkR - hpR) * RainSynth.hp
        let bandL = pinkL - hpL, bandR = pinkR - hpR
        lp1L += (bandL - lp1L) * RainSynth.lp; lp2L += (lp1L - lp2L) * RainSynth.lp
        lp1R += (bandR - lp1R) * RainSynth.lp; lp2R += (lp1R - lp2R) * RainSynth.lp

        let bed = 0.55 * rain * gust
        var l = lp2L * bed
        var r = lp2R * bed

        // ---- 雨滴：小雨约 25 颗/秒，大雨约 70 颗/秒 ----
        if rng.nextDouble() < Double(rain) * 0.0016 { spawn() }
        for i in 0..<drops.count where drops[i].on {
            let x = noise() * drops[i].amp
            // Chamberlin 状态变量滤波器（带通输出）
            drops[i].low += drops[i].f * drops[i].band
            let high = x - drops[i].low - drops[i].q * drops[i].band
            drops[i].band += drops[i].f * high
            let s = drops[i].band
            l += s * (1 - drops[i].pan)
            r += s * drops[i].pan
            drops[i].amp *= drops[i].decay
            if drops[i].amp < 0.0003 { drops[i].on = false }
        }
        return (l, r)
    }

    private func spawn() {
        guard let i = drops.firstIndex(where: { !$0.on }) else { return }
        let sr = Double(RainSynth.sampleRate)
        let fc = 500 + pow(rng.nextDouble(), 1.5) * 2500            // 偏中低频，更“湿”
        let tau = 0.0015 + rng.nextDouble() * 0.0035                 // 1.5 ~ 5 ms
        let amp = Float(0.05 + pow(rng.nextDouble(), 3) * 0.35)      // 多数很轻，偶尔一颗响
        var d = Drop()
        d.on = true
        d.amp = amp
        d.decay = Float(exp(-1.0 / (tau * sr)))
        d.f = Float(2 * sin(Double.pi * fc / sr))
        d.q = 1.1 + Float(rng.nextDouble()) * 0.6                    // 低 Q：宽带通，没有明显音高
        d.pan = 0.15 + Float(rng.nextDouble()) * 0.7
        drops[i] = d
    }
}
