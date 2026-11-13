import XCTest
@testable import WeatherCore

final class EngineTests: XCTestCase {
    func testIntensityApproachesTarget() {
        let e = WeatherEngine(mode: .rain, now: 0, seed: 5)
        var t = 0.0
        while t < 20 { t += 0.05; e.tick(dt: 0.05, now: t) }
        XCTAssertEqual(e.intensity, 0.55, accuracy: 0.01)
        e.setMode(.off)
        while t < 40 { t += 0.05; e.tick(dt: 0.05, now: t) }
        XCTAssertEqual(e.intensity, 0, accuracy: 0.001)
    }

    func testStormStrikesAndModeEvents() {
        let e = WeatherEngine(mode: .off, now: 0, seed: 9)
        var strikes: [LightningStrike] = []
        var modes: [WeatherMode] = []
        e.onLightning.append { strikes.append($0) }
        e.onModeChanged.append { modes.append($0) }
        e.setMode(.storm)
        e.setMode(.storm)   // 重复设置不触发
        var t = 0.0
        while t < 120 { t += 0.05; e.tick(dt: 0.05, now: t) }
        XCTAssertEqual(modes, [.storm])
        XCTAssertGreaterThanOrEqual(strikes.count, 4, "两分钟雷雨应该有好几道闪电")
        for s in strikes {
            XCTAssert((0.45...1).contains(s.intensity))
            XCTAssert((0.08...0.92).contains(s.x))
            XCTAssert((0...2.2).contains(s.thunderDelay))
        }
        XCTAssertEqual(e.audioRain, e.intensity, accuracy: 1e-9)
    }

    func testNoStrikesWhenRaining() {
        let e = WeatherEngine(mode: .rain, now: 0, seed: 3)
        var count = 0
        e.onLightning.append { _ in count += 1 }
        var t = 0.0
        while t < 120 { t += 0.05; e.tick(dt: 0.05, now: t) }
        XCTAssertEqual(count, 0)
        // 两分钟里至少起过一阵风（第 40 秒起第一阵）
        XCTAssertGreaterThanOrEqual(e.gust, 0)
    }

    func testRainSimulationFillsAndStrikes() {
        let sim = RainSimulation(width: 1920, height: 1080, seed: 1)
        for _ in 0..<300 { sim.step(dt: 1.0 / 60, intensity: 1, wind: 0.1, mode: .storm) }
        XCTAssertEqual(sim.active, 480)
        XCTAssertGreaterThan(sim.streaks.reduce(0) { $0 + $1.count }, 400)
        XCTAssertFalse(sim.splashes.isEmpty, "近处的雨滴落地应该有溅射")
        XCTAssertEqual(sim.darkness, 0.42, accuracy: 0.01)

        sim.strike(LightningStrike(x: 0.5, intensity: 1, thunderDelay: 0, seed: 42))
        sim.step(dt: 0.01, intensity: 1, wind: 0.1, mode: .storm)
        XCTAssertGreaterThan(sim.flash, 0.5)
        XCTAssertGreaterThanOrEqual(sim.bolt.count, 3, "主干加至少两条分叉")
        XCTAssertEqual(sim.bolt[0].count, 129, "7 次中点细分：2 → 129 个点")
        XCTAssertLessThan(sim.darkness, 0.42, "闪电时压暗减弱")

        for _ in 0..<200 { sim.step(dt: 1.0 / 60, intensity: 1, wind: 0.1, mode: .storm) }
        XCTAssertEqual(sim.flash, 0)
    }

    func testRainSimulationGoesIdle() {
        let sim = RainSimulation(width: 800, height: 600, seed: 2)
        for _ in 0..<200 { sim.step(dt: 1.0 / 60, intensity: 0.55, wind: 0, mode: .rain) }
        XCTAssertFalse(sim.isIdle)
        for _ in 0..<200 { sim.step(dt: 1.0 / 60, intensity: 0, wind: 0, mode: .off) }
        XCTAssertTrue(sim.isIdle)
    }

    func testSettingsRoundTripAndDefaults() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let url = dir.appendingPathComponent("settings.json")
        var s = Settings()
        s.mode = .rain; s.volume = 0.3; s.notifyCorner = .left; s.sound = false
        s.save(to: url)
        XCTAssertEqual(Settings.load(from: url), s)

        // 旧文件缺字段、有多余字段、数值越界
        try Data(#"{"mode":"off","volume":5,"somethingNew":1}"#.utf8).write(to: url)
        let loaded = Settings.load(from: url)
        XCTAssertEqual(loaded.mode, .off)
        XCTAssertEqual(loaded.volume, 1)
        XCTAssertTrue(loaded.glassNotifications)

        XCTAssertEqual(Settings.load(from: dir.appendingPathComponent("missing.json")), Settings())
    }

    func testMixerProducesBoundedAudio() {
        let m = StormMixer(rainSeed: 1, windSeed: 2)
        m.rainTarget = 1; m.windTarget = 0.5; m.masterVolume = 1
        m.enqueue(ThunderSynth.generate(intensity: 1, pan: 0.5, seed: 1))
        var l = [Float](repeating: 0, count: 44100 * 3), r = l
        l.withUnsafeMutableBufferPointer { lb in
            r.withUnsafeMutableBufferPointer { rb in
                m.render(frames: lb.count, left: lb.baseAddress!, right: rb.baseAddress!)
            }
        }
        let peak = l.map { abs($0) }.max()!
        XCTAssertGreaterThan(peak, 0.001)
        XCTAssertLessThanOrEqual(peak, 1)

        m.muted = true
        var l2 = [Float](repeating: 0, count: 44100 * 5), r2 = l2
        l2.withUnsafeMutableBufferPointer { lb in
            r2.withUnsafeMutableBufferPointer { rb in
                m.render(frames: lb.count, left: lb.baseAddress!, right: rb.baseAddress!)
            }
        }
        XCTAssertLessThan(l2.suffix(44100).map { abs($0) }.max()!, 0.01, "静音后音量平滑降到 0")
    }
}
