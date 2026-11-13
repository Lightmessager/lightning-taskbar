import AVFoundation
import WeatherCore

/// 实时播放：AVAudioEngine + AVAudioSourceNode，在音频线程里逐采样合成。
/// 对应 Windows 版用 winmm waveOut 推流的 StormAudio。不依赖任何音频文件。
final class AudioOutput {
    let mixer = StormMixer()
    private let engine = AVAudioEngine()
    private var node: AVAudioSourceNode?
    private var observer: NSObjectProtocol?

    func start() {
        guard node == nil else { return }
        // 标准格式 = 非交错 Float32；输出设备采样率不同时由混音器重采样
        guard let format = AVAudioFormat(standardFormatWithSampleRate: Double(StormMixer.sampleRate), channels: 2) else { return }
        let mixer = self.mixer
        let n = AVAudioSourceNode(format: format) { _, _, frameCount, bufferList -> OSStatus in
            let abl = UnsafeMutableAudioBufferListPointer(bufferList)
            guard abl.count >= 2,
                  let l = abl[0].mData?.assumingMemoryBound(to: Float.self),
                  let r = abl[1].mData?.assumingMemoryBound(to: Float.self) else { return noErr }
            mixer.render(frames: Int(frameCount), left: l, right: r)
            return noErr
        }
        engine.attach(n)
        engine.connect(n, to: engine.mainMixerNode, format: format)
        node = n

        // 插拔耳机、切换输出设备后引擎会停，要重新启动
        observer = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main) { [weak self] _ in
            self?.restart()
        }
        run()
    }

    func restart() {
        engine.stop()
        run()
    }

    func stop() {
        engine.stop()
    }

    private func run() {
        do {
            engine.prepare()
            try engine.start()
        } catch {
            Log.write("音频启动失败：\(error)")
        }
    }
}
