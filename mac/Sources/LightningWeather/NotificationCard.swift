import AppKit
import NotifyCore

/// 一张玻璃通知卡片（对应 Windows 版 NotificationCard.xaml）。
/// macOS 26 起用系统的液态玻璃 NSGlassEffectView，更早的系统用 NSVisualEffectView 磨砂。
/// 6 秒后自动收起，鼠标停留时不消失；点击打开发通知的应用。
@MainActor
final class NotificationCard {
    struct Content {
        var appName: String
        var title: String
        var body: String
        var icon: NSImage?
        var appURL: URL?

        init(appName: String, title: String, body: String, icon: NSImage?, appURL: URL?) {
            self.appName = appName; self.title = title; self.body = body; self.icon = icon; self.appURL = appURL
        }

        init(notification n: DeliveredNotification) {
            let url = n.bundleID.isEmpty ? nil : NSWorkspace.shared.urlForApplication(withBundleIdentifier: n.bundleID)
            let name = url.flatMap { FileManager.default.displayName(atPath: $0.path) }?
                .replacingOccurrences(of: ".app", with: "") ?? "通知"
            let lines = [n.subtitle, n.body].filter { !$0.isEmpty }
            self.init(appName: name,
                      title: n.title.isEmpty ? name : n.title,
                      body: lines.joined(separator: "\n"),
                      icon: url.map { NSWorkspace.shared.icon(forFile: $0.path) },
                      appURL: url)
        }
    }

    static let width: CGFloat = 360
    static let lifetime: TimeInterval = 6

    let panel: NSPanel
    private let content: Content
    private let cardView: CardView
    private var expireTimer: Timer?
    private(set) var leaving = false
    var onClose: (() -> Void)?
    var onExpire: (() -> Void)?

    var cardSize: NSSize { panel.frame.size }

    init(content: Content) {
        self.content = content
        cardView = CardView(content: content)
        let size = cardView.fittingSize
        panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: Self.width, height: max(72, size.height)),
                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.level = .statusBar
        panel.isFloatingPanel = true
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = true
        panel.isReleasedWhenClosed = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        panel.alphaValue = 0
        panel.contentView = Self.glassWrap(cardView)

        cardView.onClick = { [weak self] in self?.clicked() }
        cardView.onCloseButton = { [weak self] in self?.onClose?() }
        cardView.onHover = { [weak self] inside in
            guard let self else { return }
            if inside { self.expireTimer?.invalidate() } else { self.scheduleExpiry() }   // 移开后重新计时
        }
    }

    /// 把内容放进玻璃里。
    private static func glassWrap(_ inner: NSView) -> NSView {
        #if compiler(>=6.2)
        if #available(macOS 26.0, *) {
            let glass = NSGlassEffectView()
            glass.cornerRadius = 18
            glass.contentView = inner
            return glass
        }
        #endif
        let v = NSVisualEffectView()
        v.material = .hudWindow
        v.blendingMode = .behindWindow
        v.state = .active
        v.wantsLayer = true
        v.layer?.cornerRadius = 18
        v.layer?.masksToBounds = true
        inner.translatesAutoresizingMaskIntoConstraints = false
        v.addSubview(inner)
        NSLayoutConstraint.activate([
            inner.leadingAnchor.constraint(equalTo: v.leadingAnchor),
            inner.trailingAnchor.constraint(equalTo: v.trailingAnchor),
            inner.topAnchor.constraint(equalTo: v.topAnchor),
            inner.bottomAnchor.constraint(equalTo: v.bottomAnchor),
        ])
        return v
    }

    func present(at rest: NSRect, from start: NSRect) {
        panel.setFrame(start, display: false)
        panel.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.35
            ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
            panel.animator().setFrame(rest, display: true)
            panel.animator().alphaValue = 1
        }
        scheduleExpiry()
    }

    func move(to rest: NSRect) {
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.25
            panel.animator().setFrame(rest, display: true)
        }
    }

    func leave(towardX x: CGFloat, completion: @escaping () -> Void) {
        leaving = true
        expireTimer?.invalidate()
        var f = panel.frame
        f.origin.x = x
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.3
            ctx.timingFunction = CAMediaTimingFunction(name: .easeIn)
            panel.animator().setFrame(f, display: true)
            panel.animator().alphaValue = 0
        }, completionHandler: { [panel] in
            MainActor.assumeIsolated {
                panel.orderOut(nil)
                panel.close()
                completion()
            }
        })
    }

    private func scheduleExpiry() {
        expireTimer?.invalidate()
        let t = Timer(timeInterval: Self.lifetime, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated { self?.onExpire?() }
        }
        RunLoop.main.add(t, forMode: .common)
        expireTimer = t
    }

    private func clicked() {
        onClose?()
        guard let url = content.appURL else { return }
        // 系统没有提供通知的原始动作，只能打开对应应用
        NSWorkspace.shared.openApplication(at: url, configuration: NSWorkspace.OpenConfiguration())
    }
}

/// 卡片内容：图标 | 应用名 + 关闭按钮 / 标题 / 正文。
@MainActor
final class CardView: NSView {
    var onClick: (() -> Void)?
    var onCloseButton: (() -> Void)?
    var onHover: ((Bool) -> Void)?
    private let closeButton = NSButton()

    init(content: NotificationCard.Content) {
        super.init(frame: NSRect(x: 0, y: 0, width: NotificationCard.width, height: 80))
        translatesAutoresizingMaskIntoConstraints = false

        let icon = NSImageView()
        icon.image = content.icon
        icon.imageScaling = .scaleProportionallyUpOrDown
        icon.isHidden = content.icon == nil

        let app = Self.label(content.appName, size: 11.5, weight: .regular, color: .secondaryLabelColor)
        let title = Self.label(content.title, size: 14, weight: .semibold, color: .labelColor)
        let body = Self.label(content.body, size: 13, weight: .regular, color: .labelColor)
        body.maximumNumberOfLines = 4
        body.isHidden = content.body.isEmpty

        closeButton.bezelStyle = .circular
        closeButton.isBordered = false
        closeButton.image = NSImage(systemSymbolName: "xmark.circle.fill", accessibilityDescription: "关闭")
        closeButton.contentTintColor = .secondaryLabelColor
        closeButton.target = self
        closeButton.action = #selector(closeTapped)
        closeButton.alphaValue = 0

        let header = NSStackView(views: [app, NSView(), closeButton])
        header.orientation = .horizontal
        let texts = NSStackView(views: [header, title, body])
        texts.orientation = .vertical
        texts.alignment = .leading
        texts.spacing = 2
        let row = NSStackView(views: [icon, texts])
        row.orientation = .horizontal
        row.alignment = .top
        row.spacing = 12
        row.translatesAutoresizingMaskIntoConstraints = false
        addSubview(row)

        NSLayoutConstraint.activate([
            widthAnchor.constraint(equalToConstant: NotificationCard.width),
            icon.widthAnchor.constraint(equalToConstant: 42),
            icon.heightAnchor.constraint(equalToConstant: 42),
            closeButton.widthAnchor.constraint(equalToConstant: 18),
            closeButton.heightAnchor.constraint(equalToConstant: 18),
            header.widthAnchor.constraint(equalTo: texts.widthAnchor),
            row.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),
            row.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -12),
            row.topAnchor.constraint(equalTo: topAnchor, constant: 12),
            row.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -14),
            heightAnchor.constraint(greaterThanOrEqualToConstant: 72),
        ])
        for l in [app, title, body] { l.preferredMaxLayoutWidth = NotificationCard.width - 14 - 12 - 42 - 12 }
    }

    required init?(coder: NSCoder) { fatalError() }

    private static func label(_ s: String, size: CGFloat, weight: NSFont.Weight, color: NSColor) -> NSTextField {
        let l = NSTextField(wrappingLabelWithString: s)
        l.font = .systemFont(ofSize: size, weight: weight)
        l.textColor = color
        l.lineBreakMode = .byTruncatingTail
        l.maximumNumberOfLines = 1
        l.isSelectable = false
        l.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        return l
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
    }

    override func mouseEntered(with event: NSEvent) { closeButton.animator().alphaValue = 1; onHover?(true) }
    override func mouseExited(with event: NSEvent) { closeButton.animator().alphaValue = 0; onHover?(false) }
    override func mouseUp(with event: NSEvent) { onClick?() }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    @objc private func closeTapped() { onCloseButton?() }
}
