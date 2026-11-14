using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace LightningTaskbar.Notifications;

/// <summary>一条要显示的通知。</summary>
public sealed class NotificationInfo
{
    public string AppName { get; init; }
    public string Title { get; init; }
    public string Body { get; init; }
    public ImageSource Icon { get; init; }
    /// <summary>点击卡片时启动的应用；null 表示只关闭卡片。</summary>
    public string AppId { get; init; }
}

/// <summary>
/// 读取系统通知，在屏幕右上角用玻璃卡片显示。
/// 没有包身份的进程订阅不了 NotificationChanged（COM 返回“找不到元素”），所以每秒查询一次列表、比对新增的 ID。
/// </summary>
internal sealed class NotificationCenter : IDisposable
{
    const int MaxCards = 4;
    const double EdgeMarginDip = 14, CardGapDip = 8;
    static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _anim = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    readonly List<NotificationCard> _cards = new();
    readonly HashSet<uint> _seen = new();
    UserNotificationListener _listener;
    bool _busy, _seeded;

    public NotificationCenter()
    {
        _poll.Tick += async (_, _) => await PollAsync();
        _anim.Tick += (_, _) => Animate();
    }

    /// <summary>当前是否能读到系统通知。</summary>
    public bool Available { get; private set; }

    public async void Start()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = _listener.GetAccessStatus();
            if (access == UserNotificationListenerAccessStatus.Unspecified) access = await _listener.RequestAccessAsync();
            Available = access == UserNotificationListenerAccessStatus.Allowed;
        }
        catch (Exception ex) { App.Log("通知监听不可用：" + ex.Message); }
        if (Available) _poll.Start();
    }

    async Task PollAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            bool enabled = WeatherEngine.Instance.Settings.GlassNotifications;
            // 第一轮只记下已有的通知，不把通知中心里的旧消息重新弹一遍
            bool show = _seeded && enabled && AcceptsNotifications();
            foreach (var n in list)
            {
                if (!_seen.Add(n.Id) || !show) continue;
                if (DateTimeOffset.Now - n.CreationTime > MaxAge) continue;
                if (Parse(n) is { } info) Show(info);
            }
            _seen.IntersectWith(list.Select(n => n.Id));   // 被清掉的 ID 系统可能复用
            _seeded = true;
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        finally { _busy = false; }
    }

    /// <summary>尊重系统状态：免打扰、全屏游戏、演示时不弹，通知仍留在系统通知中心。</summary>
    static bool AcceptsNotifications() =>
        SHQueryUserNotificationState(out int state) != 0 || state == 5 /*QUNS_ACCEPTS_NOTIFICATIONS*/;

    static NotificationInfo Parse(UserNotification n)
    {
        var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (texts == null || texts.Count == 0) return null;   // 没有文字的静默通知

        string appName = "通知", appId = null;
        try
        {
            appId = n.AppInfo?.AppUserModelId;
            appName = n.AppInfo?.DisplayInfo?.DisplayName ?? appName;
        }
        catch (Exception ex) { Debug.WriteLine(ex); }

        return new NotificationInfo
        {
            AppName = appName,
            AppId = appId,
            Title = texts[0],
            Body = string.Join("\n", texts.Skip(1)),
            Icon = appId == null ? null : StartMenu.AppCatalog.GetIcon("shell:AppsFolder\\" + appId, 96),
        };
    }

    // ---------------- 卡片 ----------------

    public void Show(NotificationInfo info)
    {
        // 超出上限时把最早的一张收走
        var alive = _cards.Where(c => !c.Leaving).ToList();
        if (alive.Count >= MaxCards) alive[0].Leaving = true;

        // 屏幕四周的天气特效（同一时间来好几条只播一次）
        if (DateTime.Now - _lastEdge > TimeSpan.FromSeconds(2.5))
        {
            _lastEdge = DateTime.Now;
            PlayEdge(WeatherEngine.Instance.Mode.Edge());
        }

        var card = new NotificationCard(info);
        card.Show();
        var mon = Native.GetPrimaryMonitorRect();
        // 从屏幕外一点点滑入
        card.PosX = RestX(card, mon) + (OnLeft ? -40 : 40) * card.DpiScale;
        card.PosY = TargetY(card, mon);
        card.ApplyPosition();
        _cards.Add(card);
        _anim.Start();
    }

    EdgeEffectWindow _edge;
    DateTime _lastEdge = DateTime.MinValue;

    /// <summary>播放通知边缘特效。force = 菜单里的预览，不受“边缘特效”开关限制。</summary>
    public void PlayEdge(EdgeStyle style, bool force = false)
    {
        if (!force && !WeatherEngine.Instance.Settings.EdgeEffect) return;
        try { (_edge ??= new EdgeEffectWindow()).Play(style); }
        catch (Exception ex) { App.Log(ex); }
    }

    /// <summary>一条测试通知（外观面板和右键菜单里的“试一条通知”），文字跟着天气走。</summary>
    public void ShowTest()
    {
        var mode = WeatherEngine.Instance.Mode;
        string body = mode.Edge() switch
        {
            EdgeStyle.Frost => "下雪天，屏幕四周会泛起一圈淡淡的霜雾，再慢慢化开。",
            EdgeStyle.Leaves => "龙卷风天，几片淡淡的落叶从边上飘进来，再被风吹走。",
            EdgeStyle.StormGlow => "雷雨天，边缘的紫蓝粉流光里会闪过几道电弧。",
            _ => "屏幕边缘会亮起一圈流动的紫蓝粉光。卡片 6 秒后自动收起，鼠标停在上面时不会消失。",
        };
        Show(new NotificationInfo { AppName = "Lightning Taskbar", Title = $"测试通知 · {mode.ShortLabel()}", Body = body });
    }

    static bool OnLeft => WeatherEngine.Instance.Settings.NotifyCorner == TopCorner.Left;

    double EdgeMarginPx(NotificationCard c) => (EdgeMarginDip - NotificationCard.ShadowPad) * c.DpiScale;

    double RestX(NotificationCard c, Native.RECT mon) =>
        OnLeft ? mon.Left + EdgeMarginPx(c) : mon.Right - c.PixelWidth - EdgeMarginPx(c);

    /// <summary>从上往下排，新卡片在最下面；投影留白互相重叠，视觉间距才是 CardGapDip。</summary>
    double TargetY(NotificationCard card, Native.RECT mon)
    {
        // 和 Finder 栏在同一个角时排在它下面
        double y = mon.Top + EdgeMarginPx(card) + ((App)System.Windows.Application.Current).Bar.FinderClearance(OnLeft ? TopCorner.Left : TopCorner.Right) * card.DpiScale;
        foreach (var c in _cards)
        {
            if (c == card) break;
            if (c.Leaving) continue;
            y += c.PixelHeight - (NotificationCard.ShadowPad * 2 - CardGapDip) * c.DpiScale;
        }
        return y;
    }

    void Animate()
    {
        var mon = Native.GetPrimaryMonitorRect();
        foreach (var c in _cards.ToList())
        {
            if (c.Expired) c.Leaving = true;
            double restX = RestX(c, mon);
            double targetX = !c.Leaving ? restX : OnLeft ? mon.Left - c.PixelWidth - 4 : mon.Right + 4;
            c.PosX += (targetX - c.PosX) * (c.Leaving ? 0.16 : 0.2);
            if (Math.Abs(targetX - c.PosX) < 0.5) c.PosX = targetX;
            if (!c.Leaving) c.PosY += (TargetY(c, mon) - c.PosY) * 0.2;
            c.ApplyPosition();

            double fade = c.Leaving ? 0 : 1;
            c.RootOpacity += (fade - c.RootOpacity) * 0.18;

            if (c.Leaving && Math.Abs(targetX - c.PosX) < 1)
            {
                _cards.Remove(c);
                c.Close();
            }
        }
        if (_cards.Count == 0) _anim.Stop();
    }

    public void Dispose()
    {
        _poll.Stop();
        _anim.Stop();
        foreach (var c in _cards) c.Close();
        _cards.Clear();
        _edge?.Close();
    }

    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
}
