import Foundation

/// 雨声 + 风声 + 雷声的实时混音，对应 Windows 版 StormAudio.Fill。
/// 音频线程调用 render；其它线程只写几个目标值、投递雷声。
/// 目标值是单个 Float 的读写，丢一次更新也无所谓（下一块缓冲又会读到），所以不加锁。
public final class StormMixer: @unchecked Sendable {
    public static let sampleRate = 44100

    /// 0..1 目标雨量。
    public var rainTarget: Float = 0
    /// 阵风强度 0..1（来自天气引擎）。
    public var windTarget: Float = 0
    public var masterVolume: Float = 0.6
    public var muted = false

    private let synth: RainSynth
    private let wind: WindSynth
    private var rain: Float = 0
    private var gain: Float = 0

    private final class Voice { let data: [Float]; var pos = 0; init(_ d: [Float]) { data = d } }
    private var voices: [Voice] = []
    private var incoming: [[Float]] = []
    private let incomingLock = NSLock()

    /// Windows 版写 16 位整数时乘的是 32000，这里输出浮点，保持同样的响度。
    private static let outputScale: Float = 32000 / 32768

    public init(rainSeed: Int32 = 0, windSeed: Int32 = 0) {
        synth = RainSynth(seed: rainSeed)
        wind = WindSynth(seed: windSeed)
    }

    /// 在后台线程合成一声雷，合成好后混入。
    public func thunder(intensity: Double, pan: Double, seed: Int32) {
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            let data = ThunderSynth.generate(intensity: intensity, pan: pan, seed: seed)
            self?.enqueue(data)
        }
    }

    /// 直接混入一段交错立体声（离线渲染和测试用）。
    public func enqueue(_ interleaved: [Float]) {
        incomingLock.lock()
        incoming.append(interleaved)
        incomingLock.unlock()
    }

    /// 填充非交错的左右声道缓冲。音频线程里只尝试拿锁，拿不到就下一块再取，绝不阻塞。
    public func render(frames: Int, left: UnsafeMutablePointer<Float>, right: UnsafeMutablePointer<Float>) {
        if incomingLock.try() {
            if !incoming.isEmpty {
                voices.append(contentsOf: incoming.map(Voice.init))
                incoming.removeAll(keepingCapacity: true)
            }
            incomingLock.unlock()
        }
        let vol: Float = muted ? 0 : masterVolume
        let rainTarget = self.rainTarget, gust = self.windTarget

        for f in 0..<frames {
            // 平滑跟随
            rain += (rainTarget - rain) * 0.00002
            gain += (vol - gain) * 0.0005

            // 刮风时雨声也跟着稍微起伏变大
            var (l, r) = synth.next(rain * (1 + 0.25 * gust))
            let (wl, wr) = wind.next(gust)
            l += wl; r += wr

            var i = voices.count - 1
            while i >= 0 {
                let v = voices[i]
                if v.pos >= v.data.count { voices.remove(at: i); i -= 1; continue }
                l += v.data[v.pos]; r += v.data[v.pos + 1]
                v.pos += 2
                i -= 1
            }

            left[f] = tanhf(l * gain * 1.2) * StormMixer.outputScale
            right[f] = tanhf(r * gain * 1.2) * StormMixer.outputScale
        }
    }
}
