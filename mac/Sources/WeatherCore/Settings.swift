import Foundation

public enum WeatherMode: String, Codable, CaseIterable, Sendable {
    case off, rain, storm
}

/// 屏幕顶部的两个角，通知卡片选一个。
public enum TopCorner: String, Codable, Sendable {
    case left, right
}

/// 保存在 ~/Library/Application Support/LightningWeather/settings.json。
/// 缺字段时取默认值，以后加设置不会把旧文件读坏。
public struct Settings: Codable, Equatable, Sendable {
    public var mode: WeatherMode = .storm
    public var sound = true
    public var volume = 0.6
    /// 用玻璃卡片显示系统通知。
    public var glassNotifications = true
    public var notifyCorner: TopCorner = .right
    /// 在所有显示器上下雨（否则只有主显示器）。
    public var allScreens = true

    public init() {}

    enum CodingKeys: String, CodingKey {
        case mode, sound, volume, glassNotifications, notifyCorner, allScreens
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        let d = Settings()
        mode = (try? c.decodeIfPresent(WeatherMode.self, forKey: .mode)) ?? d.mode
        sound = (try? c.decodeIfPresent(Bool.self, forKey: .sound)) ?? d.sound
        volume = (try? c.decodeIfPresent(Double.self, forKey: .volume)) ?? d.volume
        glassNotifications = (try? c.decodeIfPresent(Bool.self, forKey: .glassNotifications)) ?? d.glassNotifications
        notifyCorner = (try? c.decodeIfPresent(TopCorner.self, forKey: .notifyCorner)) ?? d.notifyCorner
        allScreens = (try? c.decodeIfPresent(Bool.self, forKey: .allScreens)) ?? d.allScreens
        volume = min(1, max(0, volume))
    }

    public static var defaultURL: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support")
        return base.appendingPathComponent("LightningWeather/settings.json")
    }

    public static func load(from url: URL = defaultURL) -> Settings {
        guard let data = try? Data(contentsOf: url),
              let s = try? JSONDecoder().decode(Settings.self, from: data) else { return Settings() }
        return s
    }

    public func save(to url: URL = defaultURL) {
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            let enc = JSONEncoder()
            enc.outputFormatting = [.prettyPrinted, .sortedKeys]
            try enc.encode(self).write(to: url, options: .atomic)
        } catch {
            NSLog("LightningWeather: 保存设置失败 \(error)")
        }
    }
}
