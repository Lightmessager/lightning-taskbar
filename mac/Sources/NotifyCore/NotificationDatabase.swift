import Foundation
import SQLite3

/// 一条系统通知。
public struct DeliveredNotification: Equatable, Sendable {
    public var recordID: Int64
    /// 发通知的应用的 bundle id（数据库里是小写）。
    public var bundleID: String
    public var title: String
    public var subtitle: String
    public var body: String
    /// 送达时间（可能缺失）。
    public var delivered: Date?

    public init(recordID: Int64, bundleID: String, title: String, subtitle: String, body: String, delivered: Date?) {
        self.recordID = recordID; self.bundleID = bundleID; self.title = title
        self.subtitle = subtitle; self.body = body; self.delivered = delivered
    }
}

public enum NotificationAccess: Equatable, Sendable {
    case ok
    /// 文件存在但打不开：没有“完全磁盘访问权限”。
    case denied
    /// 找不到数据库（系统版本不支持或路径又改了）。
    case notFound
}

/// 读取 macOS 通知中心的数据库（usernoted 进程维护的 SQLite 库）。
///
/// macOS 没有让第三方读取其它应用通知的公开接口（Windows 有 UserNotificationListener），
/// 只能直接读这个库。它不是公开格式，系统升级可能改变；读不到时应用会退回“不显示玻璃通知”。
/// - macOS 15 起：~/Library/Group Containers/group.com.apple.usernoted/db2/db
/// - 更早：$(getconf DARWIN_USER_DIR)/com.apple.notificationcenter/db2/db
/// 两个位置都受 TCC 保护，需要在“隐私与安全性 › 完全磁盘访问权限”里勾选本应用。
public final class NotificationDatabase {
    private var db: OpaquePointer?
    public let url: URL

    public static func candidateURLs(home: URL = FileManager.default.homeDirectoryForCurrentUser) -> [URL] {
        var urls = [home.appendingPathComponent("Library/Group Containers/group.com.apple.usernoted/db2/db")]
        var buf = [CChar](repeating: 0, count: Int(PATH_MAX))
        if confstr(_CS_DARWIN_USER_DIR, &buf, buf.count) > 0 {
            let dir = String(cString: buf)
            urls.append(URL(fileURLWithPath: dir).appendingPathComponent("com.apple.notificationcenter/db2/db"))
        }
        return urls
    }

    /// 依次尝试候选路径，返回第一个能打开、能查询的库。
    public static func openSystem() -> (NotificationDatabase?, NotificationAccess) {
        var sawFile = false
        for url in candidateURLs() {
            // 没有权限时 fileExists 也可能返回 false（TCC 连目录都不让看），所以照样尝试打开
            if FileManager.default.fileExists(atPath: url.path) { sawFile = true }
            if let d = NotificationDatabase(url: url), (try? d.maxRecordID()) != nil { return (d, .ok) }
        }
        // 目录本身能列出说明系统支持；打不开基本就是权限问题
        let parentVisible = candidateURLs().contains {
            FileManager.default.fileExists(atPath: $0.deletingLastPathComponent().deletingLastPathComponent().path)
        }
        return (nil, sawFile || parentVisible ? .denied : .notFound)
    }

    public init?(url: URL) {
        self.url = url
        var handle: OpaquePointer?
        // 只读打开；库是 WAL 模式，只读连接也能看到 usernoted 刚写进 -wal 的记录
        let rc = sqlite3_open_v2(url.path, &handle, SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX, nil)
        guard rc == SQLITE_OK, let handle else {
            if let handle { sqlite3_close(handle) }
            return nil
        }
        sqlite3_busy_timeout(handle, 200)
        db = handle
    }

    deinit { if let db { sqlite3_close(db) } }

    public struct QueryError: Error, CustomStringConvertible {
        public let description: String
    }

    private func check(_ rc: Int32) throws {
        if rc != SQLITE_OK && rc != SQLITE_ROW && rc != SQLITE_DONE {
            throw QueryError(description: String(cString: sqlite3_errmsg(db)))
        }
    }

    /// 当前最大的记录号（第一次轮询用它做起点，不把通知中心里的旧消息重新弹一遍）。
    public func maxRecordID() throws -> Int64 {
        var stmt: OpaquePointer?
        try check(sqlite3_prepare_v2(db, "SELECT IFNULL(MAX(rec_id), 0) FROM record", -1, &stmt, nil))
        defer { sqlite3_finalize(stmt) }
        let rc = sqlite3_step(stmt)
        try check(rc)
        return rc == SQLITE_ROW ? sqlite3_column_int64(stmt, 0) : 0
    }

    /// 记录号大于 afterID 的通知，按时间顺序。
    public func records(after afterID: Int64, limit: Int = 50) throws -> [DeliveredNotification] {
        let sql = """
            SELECT r.rec_id, IFNULL(a.identifier, ''), r.data, r.delivered_date
            FROM record r LEFT JOIN app a ON a.app_id = r.app_id
            WHERE r.rec_id > ? ORDER BY r.rec_id LIMIT ?
            """
        var stmt: OpaquePointer?
        try check(sqlite3_prepare_v2(db, sql, -1, &stmt, nil))
        defer { sqlite3_finalize(stmt) }
        sqlite3_bind_int64(stmt, 1, afterID)
        sqlite3_bind_int64(stmt, 2, Int64(limit))

        var out: [DeliveredNotification] = []
        while true {
            let rc = sqlite3_step(stmt)
            if rc == SQLITE_DONE { break }
            try check(rc)
            let id = sqlite3_column_int64(stmt, 0)
            let bundle = sqlite3_column_text(stmt, 1).map { String(cString: $0) } ?? ""
            var blob = Data()
            if let p = sqlite3_column_blob(stmt, 2) {
                blob = Data(bytes: p, count: Int(sqlite3_column_bytes(stmt, 2)))
            }
            var delivered: Date?
            if sqlite3_column_type(stmt, 3) != SQLITE_NULL {
                delivered = Date(timeIntervalSinceReferenceDate: sqlite3_column_double(stmt, 3))
            }
            let parsed = Self.parse(blob: blob)
            out.append(DeliveredNotification(
                recordID: id,
                bundleID: bundle.isEmpty ? (parsed.app ?? "") : bundle,
                title: parsed.title, subtitle: parsed.subtitle, body: parsed.body,
                delivered: delivered ?? parsed.date))
        }
        return out
    }

    /// data 列是二进制 plist：{ app: bundle id, date: 自 2001 年起的秒数, req: { titl, subt, body, ... } }。
    public static func parse(blob: Data) -> (app: String?, title: String, subtitle: String, body: String, date: Date?) {
        guard !blob.isEmpty,
              let obj = try? PropertyListSerialization.propertyList(from: blob, options: [], format: nil),
              let dict = obj as? [String: Any] else { return (nil, "", "", "", nil) }
        let req = dict["req"] as? [String: Any] ?? [:]
        func str(_ key: String) -> String { (req[key] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? "" }
        var date: Date?
        if let d = dict["date"] as? Double { date = Date(timeIntervalSinceReferenceDate: d) }
        else if let d = dict["date"] as? Date { date = d }
        return (dict["app"] as? String, str("titl"), str("subt"), str("body"), date)
    }
}
