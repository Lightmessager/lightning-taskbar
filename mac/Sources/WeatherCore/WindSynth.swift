import Foundation

/// 风声（逐采样），移植自 Windows 版 Audio/WindSynth.cs。
/// 宽带通噪声，中心频率缓慢游走，产生“呼——”的涌动；再垫一层很低的隆隆声做底。
public final class WindSynth {
    private static let sr: Float = 44100
    private let rng: DotNetRandom

    private var g: Float = 0                       // 平滑后的阵风强度
    private var fc: Float = 300, fcTarget: Float = 300
    private var flutter: Float = 1, flutterTarget: Float = 1
    private var timer = 0
    private var lowL: Float = 0, bandL: Float = 0, lowR: Float = 0, bandR: Float = 0
    private var brownL: Float = 0, brownR: Float = 0, rumbleL: Float = 0, rumbleR: Float = 0
    private var subL: Float = 0, subR: Float = 0, softL: Float = 0, softR: Float = 0

    public init(seed: Int32 = 0) {
        rng = seed == 0 ? DotNetRandom() : DotNetRandom(seed: seed)
    }

    @inline(__always)
    private func noise() -> Float { Float(rng.nextDouble() * 2 - 1) }

    public func next(_ gust: Float) -> (l: Float, r: Float) {
        // 逐采样跟随，避免“拉链”噪声；真正的起落节奏由天气引擎控制
        g += (gust - g) * 0.00005
        if g < 0.0005 { return (0, 0) }

        timer -= 1
        if timer <= 0 {
            timer = 2048
            // 风越大，呼啸的频段越高越宽
            if rng.nextDouble() < 0.25 { fcTarget = 180 + Float(rng.nextDouble()) * (250 + 450 * g) }
            if rng.nextDouble() < 0.4 { flutterTarget = 0.65 + Float(rng.nextDouble()) * 0.35 }
        }
        fc += (fcTarget - fc) * 0.00004
        flutter += (flutterTarget - flutter) * 0.00008

        // 宽带通（Chamberlin SVF，阻尼 1.3 → Q≈0.77）
        let f = 2 * sinf(Float.pi * fc / WindSynth.sr)
        let f2 = 2 * sinf(Float.pi * fc * 1.12 / WindSynth.sr)   // 右声道略偏，增加宽度
        let damp: Float = 1.3
        let xl = noise(), xr = noise()
        lowL += f * bandL; bandL += f * (xl - lowL - damp * bandL)
        lowR += f2 * bandR; bandR += f2 * (xr - lowR - damp * bandR)

        // 低沉的隆隆底：布朗噪声 → 低通 ~120 Hz → 高通 ~40 Hz
        brownL = (brownL + xl * 0.02) * 0.997; brownR = (brownR + xr * 0.02) * 0.997
        let a: Float = 0.017, hp: Float = 0.0057
        rumbleL += (brownL - rumbleL) * a; rumbleR += (brownR - rumbleR) * a
        subL += (rumbleL - subL) * hp; subR += (rumbleR - subR) * hp

        // 再柔化一级（~1.4 kHz），像隔着窗户听到的风
        let soft: Float = 0.18
        softL += (bandL - softL) * soft; softR += (bandR - softR) * soft

        let amp = powf(g, 1.5) * flutter * 0.36
        let l = (softL * 1.8 + (rumbleL - subL) * 0.9) * amp
        let r = (softR * 1.8 + (rumbleR - subR) * 0.9) * amp
        return (l, r)
    }
}
