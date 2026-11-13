import AppKit
import ImageIO
import UniformTypeIdentifiers
import QuartzCore
import WeatherCore
import RainLayers

// 开发/CI 用：离线渲染音频和雨景，输出到指定目录。
//   weather-render <输出目录>
// 产物：
//   scene.wav / scene.txt   70 秒雷雨场景（与 Windows 版 --render-scene 相同的脚本和种子）及分析报告
//   mixer.wav               20 秒，经过实时混音器 StormMixer（与应用播放走同一条路径）
//   rain_*.png              雨景截图（同一个 RainLayerRenderer，叠在系统壁纸或渐变背景上）

let outDir = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "render-out")
try? FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)
let sr = AudioAnalysis.sr

func write(_ data: Data, _ name: String) {
    let url = outDir.appendingPathComponent(name)
    do { try data.write(to: url); print("写出 \(url.path)") } catch { print("写 \(name) 失败：\(error)") }
}

// MARK: 70 秒场景（对应 RainRender.RunScene）

do {
    let n = sr * 70
    let rain = RainSynth(seed: 777), wind = WindSynth(seed: 778)
    var l = [Float](repeating: 0, count: n), r = l
    for i in 0..<n {
        let t = Double(i) / Double(sr)
        let gust = Float(t < 10 ? 0 : t < 20 ? (t - 10) / 10 * 0.9 : t < 40 ? 0.9 : t < 50 ? (50 - t) / 10 * 0.9 : 0)
        let (a, b) = rain.next(1 + 0.25 * gust)
        let (c, d) = wind.next(gust)
        l[i] = a + c; r[i] = b + d
    }
    for (at, inten) in [(5.0, 0.9), (30.0, 0.5), (55.0, 1.0)] {
        let th = ThunderSynth.generate(intensity: inten, pan: 0.5, seed: Int32(at * 100))
        let s = Int(at * Double(sr))
        var i = 0
        while i * 2 + 1 < th.count && s + i < n { l[s + i] += th[i * 2]; r[s + i] += th[i * 2 + 1]; i += 1 }
    }
    write(AudioAnalysis.wav(left: l, right: r), "scene.wav")

    var rep = "== 每 5 秒平均响度 (dBFS) ==\n"
    for w in 0..<14 {
        rep += String(format: "%2ld s %6.1f   ", w * 5, AudioAnalysis.rmsDB(l[(w * 5 * sr)..<((w + 1) * 5 * sr)]))
        if w % 4 == 3 { rep += "\n" }
    }
    rep += "\n"
    rep += String(format: "整段混音：200ms 内最大上升 %.1f dB，50ms 内 %.1f dB\n",
                  AudioAnalysis.maxRise(l, window: sr / 5), AudioAnalysis.maxRise(l, window: sr / 20))
    rep += "== 单独的雷声（强度 1.0）==\n"
    let t1 = ThunderSynth.generate(intensity: 1.0, pan: 0.5, seed: 4242)
    let mono = stride(from: 0, to: t1.count, by: 2).map { t1[$0] }
    rep += AudioAnalysis.analyze(mono, label: 1)
    rep += String(format: "起音最陡：50ms 内最大上升 %.1f dB\n", AudioAnalysis.maxRise(mono, window: sr / 20))
    rep += String(format: "整体起落：200ms 内最大上升 %.1f dB（<6dB 基本听不出“突然”）\n", AudioAnalysis.maxRise(mono, window: sr / 5))
    rep += "== 单独的风声（阵风 0.9，稳定后 20 秒）==\n"
    let ws = WindSynth(seed: 99)
    let wm = (0..<(sr * 30)).map { _ in ws.next(0.9).l }
    rep += AudioAnalysis.analyze(Array(wm[(sr * 10)...]), label: 0.9)
    rep += "== 单独的雨声（种子 12345，雨量 1.0，20 秒）==\n"
    let rs = RainSynth(seed: 12345)
    rep += AudioAnalysis.analyze((0..<(sr * 20)).map { _ in rs.next(1).l }, label: 1)
    write(Data(rep.utf8), "scene.txt")
    print(rep)
}

// MARK: 实时混音器路径

do {
    let mixer = StormMixer(rainSeed: 1, windSeed: 2)
    mixer.rainTarget = 1
    mixer.windTarget = 0.4
    mixer.masterVolume = 0.6
    let frames = sr * 20, block = 512
    var l = [Float](repeating: 0, count: frames), r = l
    var pos = 0
    while pos < frames {
        if pos == sr * 3 { mixer.enqueue(ThunderSynth.generate(intensity: 0.9, pan: 0.3, seed: 99)) }
        let count = min(block, frames - pos)
        l.withUnsafeMutableBufferPointer { lb in
            r.withUnsafeMutableBufferPointer { rb in
                mixer.render(frames: count, left: lb.baseAddress! + pos, right: rb.baseAddress! + pos)
            }
        }
        pos += count
    }
    write(AudioAnalysis.wav(left: l, right: r, applyOutputCurve: false), "mixer.wav")
}

// MARK: 雨景 PNG

func background(width: Int, height: Int) -> CGImage? {
    if let screen = NSScreen.main, let url = NSWorkspace.shared.desktopImageURL(for: screen),
       let src = CGImageSourceCreateWithURL(url as CFURL, nil),
       let img = CGImageSourceCreateImageAtIndex(src, 0, nil) {
        print("背景使用系统壁纸 \(url.path)")
        return img
    }
    return nil
}

func renderScene(name: String, mode: WeatherMode, intensity: Double, strikeSeed: Int32?, afterStrike: Double) {
    let w = 1920, h = 1080
    let sim = RainSimulation(width: Double(w), height: Double(h), seed: 7)
    let renderer = RainLayerRenderer()
    // 先下 4 秒，让雨滴铺满、溅射出现
    for _ in 0..<240 { sim.step(dt: 1.0 / 60, intensity: intensity, wind: 0.12, mode: mode) }
    if let seed = strikeSeed {
        sim.strike(LightningStrike(x: 0.62, intensity: 0.95, thunderDelay: 0.3, seed: seed))
        var t = 0.0
        while t < afterStrike { sim.step(dt: 1.0 / 60, intensity: intensity, wind: 0.12, mode: mode); t += 1.0 / 60 }
    }
    renderer.update(sim, mode: mode)

    guard let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                              space: CGColorSpace(name: CGColorSpace.sRGB)!,
                              bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return }
    if let bg = background(width: w, height: h) {
        // “填充”：等比缩放后裁剪居中
        let scale = max(Double(w) / Double(bg.width), Double(h) / Double(bg.height))
        let bw = Double(bg.width) * scale, bh = Double(bg.height) * scale
        ctx.draw(bg, in: CGRect(x: (Double(w) - bw) / 2, y: (Double(h) - bh) / 2, width: bw, height: bh))
    } else {
        let colors = [CGColor(srgbRed: 0.33, green: 0.45, blue: 0.62, alpha: 1), CGColor(srgbRed: 0.10, green: 0.12, blue: 0.18, alpha: 1)]
        let g = CGGradient(colorsSpace: nil, colors: colors as CFArray, locations: [0, 1])!
        ctx.drawLinearGradient(g, start: CGPoint(x: 0, y: h), end: CGPoint(x: 0, y: 0), options: [])
    }
    renderer.root.render(in: ctx)
    guard let image = ctx.makeImage() else { return }
    let url = outDir.appendingPathComponent(name)
    if let dest = CGImageDestinationCreateWithURL(url as CFURL, UTType.png.identifier as CFString, 1, nil) {
        CGImageDestinationAddImage(dest, image, nil)
        if CGImageDestinationFinalize(dest) { print("写出 \(url.path)") }
    }
}

renderScene(name: "rain_light.png", mode: .rain, intensity: 0.55, strikeSeed: nil, afterStrike: 0)
renderScene(name: "rain_storm.png", mode: .storm, intensity: 1, strikeSeed: nil, afterStrike: 0)
renderScene(name: "rain_storm_flash.png", mode: .storm, intensity: 1, strikeSeed: 2024, afterStrike: 0.03)
renderScene(name: "rain_storm_afterglow.png", mode: .storm, intensity: 1, strikeSeed: 2024, afterStrike: 0.4)
