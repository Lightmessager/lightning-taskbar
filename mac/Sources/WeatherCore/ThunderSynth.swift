import Foundation

/// 雷声合成，移植自 Windows 版 Audio/StormAudio.cs 里的 ThunderSynth。
/// 只做氛围感的滚雷——低沉、缓慢涌起、隆隆滚动后远去；越近只是越响、越饱满、来得稍快一点。
public enum ThunderSynth {
    static let sr = 44100

    /// 返回交错立体声样本。pan: 0 左 .. 1 右；intensity 越大越近越响。
    public static func generate(intensity: Double, pan: Double, seed: Int32) -> [Float] {
        let rng = DotNetRandom(seed: seed)
        let srD = Double(sr)
        let seconds = 7 + intensity * 5 + rng.nextDouble() * 3
        let n = Int(seconds * srD)
        var out = [Float](repeating: 0, count: n * 2)

        // 滚雷包络：若干随机高斯“隆起”，越往后越弱
        let bumps = 4 + rng.next(5)
        var bt = [Double](repeating: 0, count: bumps)
        var ba = bt, bw = bt
        for i in 0..<bumps {
            bt[i] = 0.4 + rng.nextDouble() * seconds * 0.6
            ba[i] = (0.4 + rng.nextDouble() * 0.6) * exp(-bt[i] / (seconds * 0.5))
            bw[i] = 0.9 + rng.nextDouble() * 1.3
        }

        var brownL = 0.0, brownR = 0.0, lp1L = 0.0, lp2L = 0.0, lp1R = 0.0, lp2R = 0.0
        // 缓慢涌起：近雷约 0.6 秒，远雷可达 1.8 秒
        let attack = 0.6 + (1 - intensity) * 1.0 + rng.nextDouble() * 0.3
        // 等功率声像，收窄到中间 60%
        let angle = (0.2 + pan * 0.6) * Double.pi / 2
        let panL = cos(angle) * 1.41, panR = sin(angle) * 1.41

        out.withUnsafeMutableBufferPointer { buf in
            for i in 0..<n {
                let t = Double(i) / srD
                var wl = rng.nextDouble() * 2 - 1, wr = rng.nextDouble() * 2 - 1
                let shared = rng.nextDouble() * 2 - 1
                wl = wl * 0.5 + shared * 0.5; wr = wr * 0.5 + shared * 0.5

                brownL = (brownL + wl * 0.02) * 0.998; brownR = (brownR + wr * 0.02) * 0.998

                // 截止频率随时间下降 —— 雷声越滚越闷
                let fc = (90 + 90 * intensity) * exp(-t / (seconds * 0.6)) + 40
                let a = 1 - exp(-2 * Double.pi * fc / srD)
                lp1L += (brownL - lp1L) * a; lp2L += (lp1L - lp2L) * a
                lp1R += (brownR - lp1R) * a; lp2R += (lp1R - lp2R) * a

                var env = 0.0
                for k in 0..<bumps { let d = (t - bt[k]) / bw[k]; env += ba[k] * exp(-d * d) }
                // smoothstep 起音，没有突兀的瞬态
                let rise = min(1, t / attack)
                env *= rise * rise * (3 - 2 * rise) * exp(-t / (seconds * 0.55))
                // 隆隆的缓慢颤动
                env *= 0.8 + 0.2 * sin(t * (5 + 2 * sin(t * 0.6)))

                let l = lp2L * env * 9, r = lp2R * env * 9
                buf[i * 2] = Float(l * panL)
                buf[i * 2 + 1] = Float(r * panR)
            }

            // 高通 ~30 Hz：去掉听不见却推高峰值的次声
            let hpA = 1 - exp(-2 * Double.pi * 30 / srD)
            var hpL = 0.0, hpR = 0.0
            for i in 0..<n {
                hpL += (Double(buf[i * 2]) - hpL) * hpA; buf[i * 2] -= Float(hpL)
                hpR += (Double(buf[i * 2 + 1]) - hpR) * hpA; buf[i * 2 + 1] -= Float(hpR)
            }

            // 归一化：衬在雨声后面的氛围音量
            var peak: Float = 1e-6
            for s in buf { peak = max(peak, abs(s)) }
            let target = Float(0.22 + intensity * 0.33)
            let k2 = target / peak
            for i in 0..<buf.count { buf[i] *= k2 }

            // 结尾淡出防爆音
            let fade = sr / 50
            var i = 0
            while i < fade && i * 2 + 1 < buf.count {
                let g = Float(i) / Float(fade)
                let e = buf.count - 2 - i * 2
                buf[e] *= g; buf[e + 1] *= g
                i += 1
            }
        }
        return out
    }
}
