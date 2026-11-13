import XCTest
import SQLite3
@testable import NotifyCore

/// 用与 usernoted 相同结构的临时库测试读取逻辑；再顺便报告 CI 机器上真实库的可访问状态。
final class NotificationDatabaseTests: XCTestCase {
    private var dir: URL!

    override func setUpWithError() throws {
        dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    private func blob(app: String, title: String, subtitle: String = "", body: String, date: Double) throws -> Data {
        let plist: [String: Any] = [
            "app": app, "date": date,
            "req": ["titl": title, "subt": subtitle, "body": body, "iden": UUID().uuidString],
        ]
        return try PropertyListSerialization.data(fromPropertyList: plist, format: .binary, options: 0)
    }

    private func makeDB() throws -> URL {
        let url = dir.appendingPathComponent("db")
        var db: OpaquePointer?
        XCTAssertEqual(sqlite3_open(url.path, &db), SQLITE_OK)
        defer { sqlite3_close(db) }
        let schema = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE app (app_id INTEGER PRIMARY KEY, identifier VARCHAR, badge INTEGER NULL);
            CREATE TABLE record (rec_id INTEGER PRIMARY KEY, app_id INTEGER, uuid BLOB, data BLOB,
                request_date REAL, request_last_date REAL, delivered_date REAL, presented Bool, style INTEGER, snooze_fire_date REAL);
            INSERT INTO app VALUES (1, 'com.apple.mail', NULL), (2, 'com.tinyspeck.slackmacgap', NULL);
            """
        XCTAssertEqual(sqlite3_exec(db, schema, nil, nil, nil), SQLITE_OK)
        let now = Date().timeIntervalSinceReferenceDate
        let rows: [(Int, Int, Data, Double?)] = [
            (10, 1, try blob(app: "com.apple.mail", title: "旧邮件", body: "很早以前", date: now - 3600), now - 3600),
            (11, 2, try blob(app: "com.tinyspeck.slackmacgap", title: "小明", subtitle: "#general", body: "开会了", date: now), now),
            (12, 1, try blob(app: "com.apple.mail", title: "  新邮件 ", body: "正文\n第二行", date: now), nil),
        ]
        for (id, app, data, delivered) in rows {
            var stmt: OpaquePointer?
            XCTAssertEqual(sqlite3_prepare_v2(db, "INSERT INTO record (rec_id, app_id, data, delivered_date) VALUES (?,?,?,?)", -1, &stmt, nil), SQLITE_OK)
            sqlite3_bind_int64(stmt, 1, Int64(id))
            sqlite3_bind_int64(stmt, 2, Int64(app))
            _ = data.withUnsafeBytes { sqlite3_bind_blob(stmt, 3, $0.baseAddress, Int32(data.count), unsafeBitCast(-1, to: sqlite3_destructor_type.self)) }
            if let delivered { sqlite3_bind_double(stmt, 4, delivered) } else { sqlite3_bind_null(stmt, 4) }
            XCTAssertEqual(sqlite3_step(stmt), SQLITE_DONE)
            sqlite3_finalize(stmt)
        }
        return url
    }

    func testReadsNewRecordsAfterID() throws {
        let db = try XCTUnwrap(NotificationDatabase(url: makeDB()))
        XCTAssertEqual(try db.maxRecordID(), 12)
        let fresh = try db.records(after: 10)
        XCTAssertEqual(fresh.map(\.recordID), [11, 12])
        XCTAssertEqual(fresh[0].bundleID, "com.tinyspeck.slackmacgap")
        XCTAssertEqual(fresh[0].title, "小明")
        XCTAssertEqual(fresh[0].subtitle, "#general")
        XCTAssertEqual(fresh[0].body, "开会了")
        XCTAssertNotNil(fresh[0].delivered)
        XCTAssertEqual(fresh[1].title, "新邮件", "首尾空白要去掉")
        XCTAssertEqual(fresh[1].body, "正文\n第二行")
        XCTAssertNotNil(fresh[1].delivered, "delivered_date 为空时用 plist 里的 date")
        XCTAssertTrue(try db.records(after: 12).isEmpty)
    }

    /// 通知被清掉后 SQLite 复用最大记录号：只按记录号会漏掉，按送达时间能找回来。
    func testReusedRecordIDFoundByDeliveryTime() throws {
        let url = try makeDB()
        var raw: OpaquePointer?
        XCTAssertEqual(sqlite3_open(url.path, &raw), SQLITE_OK)
        let now = Date().timeIntervalSinceReferenceDate
        let data = try blob(app: "com.apple.mail", title: "复用编号的新邮件", body: "正文", date: now)
        XCTAssertEqual(sqlite3_exec(raw, "DELETE FROM record WHERE rec_id = 12", nil, nil, nil), SQLITE_OK)
        var stmt: OpaquePointer?
        XCTAssertEqual(sqlite3_prepare_v2(raw, "INSERT INTO record (app_id, data, delivered_date) VALUES (1, ?, ?)", -1, &stmt, nil), SQLITE_OK)
        _ = data.withUnsafeBytes { sqlite3_bind_blob(stmt, 1, $0.baseAddress, Int32(data.count), unsafeBitCast(-1, to: sqlite3_destructor_type.self)) }
        sqlite3_bind_double(stmt, 2, now)
        XCTAssertEqual(sqlite3_step(stmt), SQLITE_DONE)
        sqlite3_finalize(stmt)
        sqlite3_close(raw)

        let db = try XCTUnwrap(NotificationDatabase(url: url))
        XCTAssertEqual(try db.maxRecordID(), 12, "SQLite 复用了 12")
        XCTAssertTrue(try db.records(after: 12).isEmpty, "只按记录号会漏掉")
        let found = try db.records(after: 12, orDeliveredSince: Date().addingTimeInterval(-60))
        XCTAssertTrue(found.contains { $0.title == "复用编号的新邮件" })
        XCTAssertFalse(found.contains { $0.title == "旧邮件" }, "一小时前的不应出现")
        XCTAssertEqual(try db.count(), 3)
    }

    func testParseGarbageIsEmpty() {
        let p = NotificationDatabase.parse(blob: Data([1, 2, 3]))
        XCTAssertNil(p.app)
        XCTAssertEqual(p.title, "")
    }

    func testMissingDatabase() {
        XCTAssertNil(NotificationDatabase(url: dir.appendingPathComponent("nope/db")))
    }

    func testFocusAssertions() {
        let on = #"{"data":[{"storeAssertionRecords":[{"assertionDetails":{"assertionDetailsModeIdentifier":"com.apple.donotdisturb.mode.default"}}]}]}"#
        let off = #"{"data":[{}]}"#
        XCTAssertTrue(FocusState.isFocusActive(json: Data(on.utf8)))
        XCTAssertFalse(FocusState.isFocusActive(json: Data(off.utf8)))
        XCTAssertFalse(FocusState.isFocusActive(json: Data("garbage".utf8)))
    }

    /// 不断言：CI 进程一般没有完全磁盘访问权限，只把状态打到日志里。
    func testReportSystemDatabaseAccess() {
        let (db, access) = NotificationDatabase.openSystem()
        print("[notify] 候选路径：\(NotificationDatabase.candidateURLs().map(\.path))")
        print("[notify] 系统通知库状态：\(access)，打开的是 \(db?.url.path ?? "无")")
    }
}
