import AppKit
import NotifyCore
import WeatherCore

/// 每秒读一次系统通知数据库，把新通知用玻璃卡片显示在屏幕上方的角落。
/// 对应 Windows 版的 Notifications/NotificationCenter.cs（那边是每秒查询 UserNotificationListener）。
@MainActor
final class NotificationController {
    static let maxCards = 4
    static let edgeMargin: CGFloat = 14, cardGap: CGFloat = 8
    static let maxAge: TimeInterval = 30

    private let settings: () -> Settings
    private var timer: Timer?
    private var lastID: Int64?
    /// 已经处理过的通知（记录号 + 送达时间），配合按送达时间查询去重。
    private var seen = Set<String>()
    private var cards: [NotificationCard] = []
    private let queue = DispatchQueue(label: "LightningWeather.notifications", qos: .utility)
    private var db: NotificationDatabase?
    private var busy = false

    /// 读库状态，菜单里据此提示去开“完全磁盘访问权限”。
    private(set) var access: NotificationAccess = .notFound
    /// 给设置窗口看的一行状态，如“正常 · 库里 52 条 · 最近：微信 14:03”。
    private(set) var statusText = "正在检查…"
    var onAccessChanged: (() -> Void)?

    init(settings: @escaping () -> Settings) {
        self.settings = settings
    }

    func start() {
        let t = Timer(timeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.poll() }
        }
        RunLoop.main.add(t, forMode: .common)
        timer = t
        poll()
    }

    private struct PollResult {
        var database: NotificationDatabase?
        var access: NotificationAccess
        var rows: [DeliveredNotification] = []
        var lastID: Int64?
        var focus = false
        var total = 0
        var error: String?
        var seeding = false
    }

    private static func key(_ n: DeliveredNotification) -> String {
        "\(n.recordID)|\(n.delivered?.timeIntervalSinceReferenceDate ?? 0)"
    }

    private func poll() {
        guard !busy else { return }
        busy = true
        let previous = lastID
        let existing = db
        queue.async { [weak self] in
            // 没打开过、或者上次没权限，就再试一次（用户可能刚在系统设置里授权）
            var r = PollResult(database: existing, access: .ok, lastID: previous)
            if r.database == nil { (r.database, r.access) = NotificationDatabase.openSystem() }
            if let opened = r.database {
                do {
                    let since = Date().addingTimeInterval(-NotificationController.maxAge * 2)
                    r.seeding = previous == nil
                    let maxID = try opened.maxRecordID()
                    r.rows = try opened.records(after: previous ?? maxID, orDeliveredSince: since)
                    r.lastID = max(previous ?? 0, maxID)
                    r.total = try opened.count()
                    if !r.rows.isEmpty { r.focus = FocusState.isFocusActive() }
                } catch {
                    r.access = .denied
                    r.error = "\(error)"
                    r.database = nil
                }
            }
            let result = r
            DispatchQueue.main.async {
                MainActor.assumeIsolated { self?.finish(result) }
            }
        }
    }

    private func finish(_ r: PollResult) {
        busy = false
        if r.access != access || (db == nil) != (r.database == nil) {
            Log.write("通知库：\(r.access)，\(r.database?.url.path ?? "未打开")\(r.error.map { "，错误：\($0)" } ?? "")")
        }
        db = r.database
        lastID = r.lastID
        if r.access != access {
            access = r.access
            onAccessChanged?()
        }

        let fresh = r.rows.filter { seen.insert(Self.key($0)).inserted }
        if seen.count > 2000 { seen = Set(r.rows.map(Self.key)) }
        switch access {
        case .ok: statusText = "正常读取 · 库里共 \(r.total) 条" + (lastShown.map { " · 最近显示：\($0)" } ?? "")
        case .denied: statusText = "没有权限：请在“完全磁盘访问权限”里加入闪电天气"
        case .notFound: statusText = "找不到系统通知数据库"
        }
        // 第一轮只记下已有的通知，不把通知中心里的旧消息重新弹一遍
        if r.seeding { return }

        let enabled = settings().glassNotifications
        let ownID = Bundle.main.bundleIdentifier?.lowercased()
        for n in fresh {
            let age = n.delivered.map { Date().timeIntervalSince($0) }
            var skip: String?
            if n.bundleID.lowercased() == ownID { skip = "本应用" }
            else if !enabled { skip = "玻璃通知已关闭" }
            else if r.focus { skip = "专注模式" }
            else if let age, age > Self.maxAge { skip = "太旧（\(Int(age)) 秒前）" }
            else if n.title.isEmpty && n.body.isEmpty { skip = "没有文字" }
            Log.write("新通知 #\(n.recordID) \(n.bundleID) 标题“\(n.title.prefix(20))” → \(skip.map { "跳过：\($0)" } ?? "显示")")
            if skip != nil { continue }
            let content = NotificationCard.Content(notification: n)
            lastShown = "\(content.appName) \(Self.timeFormatter.string(from: Date()))"
            show(content)
        }
    }

    private var lastShown: String?
    private static let timeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "HH:mm:ss"
        return f
    }()

    // MARK: 卡片

    func show(_ content: NotificationCard.Content) {
        // 超出上限时把最早的一张收走
        let alive = cards.filter { !$0.leaving }
        if alive.count >= Self.maxCards { dismiss(alive[0]) }

        guard let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let card = NotificationCard(content: content)
        card.onClose = { [weak self, weak card] in if let self, let card { self.dismiss(card) } }
        card.onExpire = card.onClose
        cards.append(card)
        let rest = restFrame(for: card, index: cards.filter { !$0.leaving }.count - 1, screen: screen)
        // 从屏幕外一点点滑入
        card.present(at: rest, from: rest.offsetBy(dx: onLeft ? -40 : 40, dy: 0))
    }

    private var onLeft: Bool { settings().notifyCorner == .left }

    /// 从上往下排，新卡片在最下面；放在菜单栏下方。
    private func restFrame(for card: NotificationCard, index: Int, screen: NSScreen) -> NSRect {
        let vf = screen.visibleFrame
        let size = card.cardSize
        let x = onLeft ? vf.minX + Self.edgeMargin : vf.maxX - size.width - Self.edgeMargin
        var y = vf.maxY - Self.edgeMargin
        for c in cards.filter({ !$0.leaving }).prefix(index) { y -= c.cardSize.height + Self.cardGap }
        return NSRect(x: x, y: y - size.height, width: size.width, height: size.height)
    }

    private func dismiss(_ card: NotificationCard) {
        guard !card.leaving, let screen = NSScreen.main ?? NSScreen.screens.first else { return }
        let vf = screen.visibleFrame
        card.leave(towardX: onLeft ? vf.minX - card.cardSize.width - 4 : vf.maxX + 4) { [weak self, weak card] in
            guard let self, let card else { return }
            self.cards.removeAll { $0 === card }
        }
        relayout(screen: screen)
    }

    /// 有卡片离开后，下面的往上补位。
    private func relayout(screen: NSScreen) {
        for (i, c) in cards.filter({ !$0.leaving }).enumerated() {
            c.move(to: restFrame(for: c, index: i, screen: screen))
        }
    }
}
