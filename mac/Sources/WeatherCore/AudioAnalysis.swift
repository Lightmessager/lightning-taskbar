import Foundation

/// 开发用的离线工具：写 WAV、电平与频段能量报告。移植自 Windows 版 Audio/RainRender.cs，
/// 两边报告格式一致，可以直接对照。
public enum AudioAnalysis {
    public static let sr = 44100

    /// 16 位立体声 WAV。applyOutputCurve = true 时与实时播放一致：音量 0.6 × 1.2 后 tanh。
    public static func wav(left l: [Float], right r: [Float], applyOutputCurve: Bool = true) -> Data {
        var d = Data()
        func u32(_ v: UInt32) { withUnsafeBytes(of: v.littleEndian) { d.append(contentsOf: $0) } }
        func u16(_ v: UInt16) { withUnsafeBytes(of: v.littleEndian) { d.append(contentsOf: $0) } }
        let bytes = UInt32(l.count * 4)
        d.append(contentsOf: Array("RIFF".utf8)); u32(36 + bytes); d.append(contentsOf: Array("WAVE".utf8))
        d.append(contentsOf: Array("fmt ".utf8)); u32(16); u16(1); u16(2); u32(UInt32(sr)); u32(UInt32(sr * 4)); u16(4); u16(16)
        d.append(contentsOf: Array("data".utf8)); u32(bytes)
        d.reserveCapacity(d.count + Int(bytes))
        for i in 0..<l.count {
            let a = applyOutputCurve ? tanhf(l[i] * 0.72) * 32000 : max(-32767, min(32767, l[i] * 32768))
            let b = applyOutputCurve ? tanhf(r[i] * 0.72) * 32000 : max(-32767, min(32767, r[i] * 32768))
            u16(UInt16(bitPattern: Int16(a)))
            u16(UInt16(bitPattern: Int16(b)))
        }
        return d
    }

    public static func rmsDB(_ x: ArraySlice<Float>) -> Double {
        var sum = 0.0
        for v in x { sum += Double(v) * Double(v) }
        return 10 * log10(sum / Double(max(1, x.count)) + 1e-12)
    }

    /// 窗口内最大上升（dB），衡量起音是否突兀。
    public static func maxRise(_ x: [Float], window win: Int) -> Double {
        var prev = -120.0, best = 0.0
        var s = 0
        while s + win <= x.count {
            let db = rmsDB(x[s..<(s + win)])
            // 只统计能听见的部分
            if prev > -60 && db > -40 { best = max(best, db - prev) }
            prev = db
            s += win
        }
        return best
    }

    public static func analyze(_ x: [Float], label: Float) -> String {
        var out = ""
        var sum = 0.0, peak = 0.0
        for v in x { sum += Double(v) * Double(v); peak = max(peak, Double(abs(v))) }
        let rms = sqrt(sum / Double(max(1, x.count)))
        out += String(format: "雨量 %.3g  RMS %.1f dBFS  峰值 %.1f dBFS  峰均比 %.1f dB\n",
                      label, 20 * log10(rms + 1e-12), 20 * log10(peak + 1e-12), 20 * log10(peak / max(rms, 1e-12)))

        // 平均功率谱（4096 点 FFT，Hann 窗）
        let n = 4096
        var power = [Double](repeating: 0, count: n / 2)
        var re = [Double](repeating: 0, count: n), im = re
        var start = 0
        while start + n <= x.count {
            for i in 0..<n {
                re[i] = Double(x[start + i]) * (0.5 - 0.5 * cos(2 * Double.pi * Double(i) / Double(n - 1)))
                im[i] = 0
            }
            fft(&re, &im)
            for k in 0..<(n / 2) { power[k] += re[k] * re[k] + im[k] * im[k] }
            start += n
        }
        let bands: [(Int, Int, String)] = [
            (20, 250, "低频 20-250"), (250, 1000, "中低 250-1k"), (1000, 3000, "中频 1k-3k"),
            (3000, 6000, "中高 3k-6k"), (6000, 12000, "高频 6k-12k"), (12000, 20000, "极高 12k-20k"),
        ]
        let total = max(power.reduce(0, +), 1e-30)
        for (lo, hi, name) in bands {
            var e = 0.0
            var k = lo * n / sr
            while k < hi * n / sr && k < power.count { e += power[k]; k += 1 }
            out += name.padding(toLength: 14, withPad: " ", startingAt: 0) + String(format: " %6.1f%%\n", 100 * e / total)
        }

        // 音调性：最强频点相对其邻域中值，>12 dB 说明有可闻的“音高”
        var maxRatio = 0.0, maxK = 0
        if power.count > 40 {
            for k in 20..<(power.count - 20) {
                var nb: [Double] = []
                for j in -20...20 where abs(j) > 2 { nb.append(power[k + j]) }
                nb.sort()
                let ratio = power[k] / (nb[nb.count / 2] + 1e-20)
                if ratio > maxRatio { maxRatio = ratio; maxK = k }
            }
        }
        out += String(format: "最突出单频 %ld Hz，高出邻域 %.1f dB\n", maxK * sr / n, 10 * log10(max(maxRatio, 1e-12)))
        return out
    }

    /// 原地基 2 FFT。
    public static func fft(_ re: inout [Double], _ im: inout [Double]) {
        let n = re.count
        var j = 0
        for i in 1..<n {
            var bit = n >> 1
            while j & bit != 0 { j ^= bit; bit >>= 1 }
            j ^= bit
            if i < j { re.swapAt(i, j); im.swapAt(i, j) }
        }
        var len = 2
        while len <= n {
            let ang = -2 * Double.pi / Double(len)
            let wr = cos(ang), wi = sin(ang)
            var i = 0
            while i < n {
                var cr = 1.0, ci = 0.0
                for k in 0..<(len / 2) {
                    let a = i + k, b = i + k + len / 2
                    let vr = re[b] * cr - im[b] * ci, vi = re[b] * ci + im[b] * cr
                    re[b] = re[a] - vr; im[b] = im[a] - vi
                    re[a] += vr; im[a] += vi
                    let ncr = cr * wr - ci * wi
                    ci = cr * wi + ci * wr
                    cr = ncr
                }
                i += len
            }
            len <<= 1
        }
    }
}
