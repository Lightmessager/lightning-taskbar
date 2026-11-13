import Foundation

/// 诊断日志：~/Library/Logs/LightningWeather.log。
/// 用户把这个文件发过来，就能看到通知读取、音频等关键步骤发生了什么。超过 1 MB 时从头写。
enum Log {
    static let url = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/LightningWeather.log")
    private static let queue = DispatchQueue(label: "LightningWeather.log")
    private static let formatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss"
        return f
    }()

    static func write(_ message: String) {
        let line = "\(formatter.string(from: Date())) \(message)\n"
        NSLog("LightningWeather: %@", message)
        queue.async {
            let fm = FileManager.default
            if let size = (try? fm.attributesOfItem(atPath: url.path))?[.size] as? Int, size > 1_000_000 {
                try? fm.removeItem(at: url)
            }
            if !fm.fileExists(atPath: url.path) {
                try? fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
                fm.createFile(atPath: url.path, contents: nil)
            }
            guard let h = try? FileHandle(forWritingTo: url) else { return }
            defer { try? h.close() }
            _ = try? h.seekToEnd()
            try? h.write(contentsOf: Data(line.utf8))
        }
    }
}
