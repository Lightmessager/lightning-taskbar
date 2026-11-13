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
    private var cards: [NotificationCard] = []
    private let queue = DispatchQueue(label: "LightningWeather.notifications", qos: .utility)
    private var db: NotificationDatabase?
    private var busy = false

    /// 读库状态，菜单里据此提示去开“完全磁盘访问权限”。
    private(set) var access: NotificationAccess = .notFound
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

    private func poll() {
        guard !busy else { return }
        busy = true
        let previous = lastID
        let existing = db
        queue.async { [weak self] in
            // 没打开过、或者上次没权限，就再试一次（用户可能刚在系统设置里授权）
            var database = existing
            var access = NotificationAccess.ok
            if database == nil { (database, access) = NotificationDatabase.openSystem() }
            var fresh: [DeliveredNotification] = []
            var newLast = previous
            var focus = false
            if let database {
                do {
                    if let previous {
                        fresh = try database.records(after: previous)
                        if let last = fresh.last { newLast = last.recordID }
                    } else {
                        // 第一轮只记下已有的通知，不把通知中心里的旧消息重新弹一遍
                        newLast = try database.maxRecordID()
                    }
                    if !fresh.isEmpty { focus = FocusState.isFocusActive() }
                } catch {
                    access = .denied
                    database = nil
                }
            }
            let result = (database, access, fresh, newLast, focus)
            DispatchQueue.main.async {
                MainActor.assumeIsolated { self?.finish(result) }
            }
        }
    }

    private func finish(_ r: (NotificationDatabase?, NotificationAccess, [DeliveredNotification], Int64?, Bool)) {
        let (database, access, fresh, newLast, focus) = r
        busy = false
        db = database
        lastID = newLast
        if access != self.access {
            self.access = access
            onAccessChanged?()
        }
        guard settings().glassNotifications, !focus else { return }
        let ownID = Bundle.main.bundleIdentifier?.lowercased()
        for n in fresh {
            if n.bundleID.lowercased() == ownID { continue }
            if let d = n.delivered, Date().timeIntervalSince(d) > Self.maxAge { continue }
            if n.title.isEmpty && n.body.isEmpty { continue }   // 没有文字的静默通知
            show(NotificationCard.Content(notification: n))
        }
    }

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
