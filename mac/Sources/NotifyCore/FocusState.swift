import Foundation

/// “专注模式 / 勿扰”是否打开。没有公开接口，读 ~/Library/DoNotDisturb/DB/Assertions.json：
/// 手动打开的专注模式会在 storeAssertionRecords 里留下记录。同样需要完全磁盘访问权限，读不到时当作没开。
/// 按时间表自动开启的专注模式不一定写在这里，这种情况下卡片仍会弹出。
public enum FocusState {
    public static var assertionsURL: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/DoNotDisturb/DB/Assertions.json")
    }

    public static func isFocusActive(url: URL = assertionsURL) -> Bool {
        guard let data = try? Data(contentsOf: url) else { return false }
        return isFocusActive(json: data)
    }

    public static func isFocusActive(json: Data) -> Bool {
        guard let root = try? JSONSerialization.jsonObject(with: json) as? [String: Any],
              let entries = root["data"] as? [[String: Any]] else { return false }
        return entries.contains { ($0["storeAssertionRecords"] as? [Any])?.isEmpty == false }
    }
}
