import XCTest
@testable import WeatherCore

/// 和 Windows 版 C# 代码同种子输出逐样本比对。
/// Fixtures 由 mac/tools/fixturegen 在 Windows 上用原版 RainSynth/WindSynth/ThunderSynth 生成。
final class DotNetParityTests: XCTestCase {
    private func fixture(_ name: String) throws -> URL {
        try XCTUnwrap(Bundle.module.url(forResource: name, withExtension: nil, subdirectory: "Fixtures"),
                      "缺少测试数据 \(name)")
    }

    /// 读取 .f32：[0] = 抽样步长，其余为抽样后的交错立体声。
    private func loadF32(_ name: String) throws -> (stride: Int, samples: [Float]) {
        let data = try Data(contentsOf: fixture(name))
        let floats = data.withUnsafeBytes { raw in
            (0..<(data.count / 4)).map { Float(bitPattern: UInt32(littleEndian: raw.loadUnaligned(fromByteOffset: $0 * 4, as: UInt32.self))) }
        }
        return (Int(floats[0]), Array(floats.dropFirst()))
    }

    /// 返回 (最大绝对误差, 参考峰值)，并打印出来，CI 日志里可以看到实际贴合程度。
    private func compare(_ name: String, actual: [Float]) throws -> (Float, Float) {
        let (stride, ref) = try loadF32(name)
        var maxDiff: Float = 0, peak: Float = 0, i = 0, frame = 0
        while i + 1 < ref.count {
            XCTAssertLessThan(frame * 2 + 1, actual.count, "\(name): 输出比参考短")
            if frame * 2 + 1 >= actual.count { break }
            maxDiff = max(maxDiff, abs(ref[i] - actual[frame * 2]), abs(ref[i + 1] - actual[frame * 2 + 1]))
            peak = max(peak, abs(ref[i]), abs(ref[i + 1]))
            i += 2
            frame += stride
        }
        print("[parity] \(name): 比较 \(ref.count / 2) 帧，最大误差 \(maxDiff)，参考峰值 \(peak)")
        return (maxDiff, peak)
    }

    func testRandomMatchesDotNet() throws {
        let text = try String(contentsOf: fixture("random.txt"), encoding: .utf8)
        for line in text.split(whereSeparator: \.isNewline) {
            let parts = line.split(separator: " ").map(String.init)
            let r = DotNetRandom(seed: Int32(parts[0])!)
            for i in 0..<8 { XCTAssertEqual(r.nextDouble(), Double(parts[1 + i])!, "seed \(parts[0]) NextDouble #\(i)") }
            for i in 0..<4 { XCTAssertEqual(r.next(5), Int(parts[9 + i])!, "seed \(parts[0]) Next(5) #\(i)") }
            for i in 0..<4 { XCTAssertEqual(Int(r.next()), Int(parts[13 + i])!, "seed \(parts[0]) Next() #\(i)") }
        }
    }

    private func renderRain(seed: Int32, rain: Float, seconds: Int) -> [Float] {
        let s = RainSynth(seed: seed)
        var out = [Float](); out.reserveCapacity(44100 * seconds * 2)
        for _ in 0..<(44100 * seconds) { let (l, r) = s.next(rain); out.append(l); out.append(r) }
        return out
    }

    func testRainSynthMatchesDotNet() throws {
        let (d1, p1) = try compare("rain_12345.f32", actual: renderRain(seed: 12345, rain: 1, seconds: 3))
        XCTAssertGreaterThan(p1, 0.01)
        XCTAssertLessThan(d1, 1e-3)
        let (d2, _) = try compare("rain_777.f32", actual: renderRain(seed: 777, rain: 0.385, seconds: 3))
        XCTAssertLessThan(d2, 1e-3)
    }

    func testWindSynthMatchesDotNet() throws {
        let s = WindSynth(seed: 99)
        var out = [Float]()
        for _ in 0..<(44100 * 4) { let (l, r) = s.next(0.9); out.append(l); out.append(r) }
        let (d, p) = try compare("wind_99.f32", actual: out)
        XCTAssertGreaterThan(p, 0.001)
        XCTAssertLessThan(d, max(1e-4, p * 1e-3))
    }

    func testThunderMatchesDotNet() throws {
        let t1 = ThunderSynth.generate(intensity: 1.0, pan: 0.5, seed: 4242)
        let (d1, p1) = try compare("thunder_4242.f32", actual: t1)
        XCTAssertGreaterThan(p1, 0.1)
        XCTAssertLessThan(d1, 2e-3)
        // 长度也必须一致（由随机时长决定）
        let (stride, ref) = try loadF32("thunder_4242.f32")
        XCTAssertEqual((t1.count / 2 + stride - 1) / stride, ref.count / 2)

        let t2 = ThunderSynth.generate(intensity: 0.5, pan: 0.2, seed: 3000)
        let (d2, _) = try compare("thunder_3000.f32", actual: t2)
        XCTAssertLessThan(d2, 2e-3)
    }
}
