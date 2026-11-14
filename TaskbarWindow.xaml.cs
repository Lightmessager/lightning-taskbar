using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace LightningTaskbar;

public partial class TaskbarWindow : Window
{
    const double SideMargin = 10, BottomMargin = 8, CornerRadiusDip = 22;
    /// <summary>窗口比玻璃本体四周多出的透明边，用来画投影（与 XAML 里 GlassBg.ShadowPad 一致）。</summary>
    const double ShadowPad = 12;

    readonly WindowTracker _tracker = new();
    readonly DispatcherTimer _tick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromMilliseconds(700) };
    IntPtr _hwnd;
    double _scale = 1;
    Native.RECT _mon;
    // 以下均为玻璃本体（不含投影边）的物理像素坐标
    int _barW, _barH, _shownY, _hiddenY, _x, _pad, _lastY = int.MinValue;
    // _shownY / _hiddenY / _lastY 是“滑动轴”上的坐标：底部为 Y，Dock 在左右两侧时为 X。
    // _bodyTopPx 是玻璃本体在窗口内沿滑动轴的偏移；_winX / _winY 是另一条轴上固定的窗口坐标。
    int _winX, _winY, _winW, _winH, _bodyTopPx;
    double _shown = 0;               // 0 = 隐藏，1 = 显示
    DateTime _lastWanted = DateTime.Now, _edgeSince = DateTime.MaxValue, _revealUntil = DateTime.MinValue;

    /// <summary>把任务栏弹出来停留几秒（程序被再次启动时调用）。</summary>
    public void Reveal() => _revealUntil = DateTime.Now.AddSeconds(3);
    /// <summary>
    /// 当前打开的右键菜单。直接跟踪菜单自身的打开 / 关闭：ContextMenuOpening 在任何右键时都会触发（哪怕没有菜单），
    /// 而菜单的主人被移除后 ContextMenuClosing 又到不了窗口，靠它们计数会卡住，任务栏就再也不隐藏、不放大。
    /// </summary>
    readonly HashSet<ContextMenu> _menus = new();
    int _openMenus => _menus.Count(m => m.IsOpen);

    static App AppInstance => (App)Application.Current;
    static WeatherEngine Weather => WeatherEngine.Instance;

    internal WindowTracker Tracker => _tracker;

    public TaskbarWindow()
    {
        InitializeComponent();
        Tasks.ItemsSource = _tracker.Items;
        TrayPinnedList.ItemsSource = AppInstance.Tray.PinnedIcons;
        TrayUnpinnedList.ItemsSource = AppInstance.Tray.UnpinnedIcons;
        AddHandler(ContextMenuOpeningEvent, new ContextMenuEventHandler((_, _) => HidePeek()), true);
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler((s, _) => _menus.Add((ContextMenu)s)));
        EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.ClosedEvent, new RoutedEventHandler((s, _) => _menus.Remove((ContextMenu)s)));
        BarMenu.Opened += (_, _) => SyncMenu();
        InitPeek();

        // Dock 悬停时内容宽度每帧都在变，只有图标数量或右侧面板变化才需要重新布局
        ContentGrid.SizeChanged += (_, _) => { if (!IsDock) CenterTasks(); };
        Tasks.SizeChanged += (_, _) => { if (!IsDock) CenterTasks(); };
        RightPanel.SizeChanged += (_, _) => { if (IsDock) _finder?.Reposition(); else CenterTasks(); };
        _tracker.Items.CollectionChanged += (_, _) =>
        {
            _dockVisuals = null;
            Dispatcher.BeginInvoke(() => { if (IsDock) LayoutDock(); else CenterTasks(); }, DispatcherPriority.Loaded);
        };

        SourceInitialized += OnSourceInitialized;
        _tick.Tick += (_, _) => OnTick();
        _slow.Tick += (_, _) => OnSlowTick();

        Weather.Lightning += OnLightning;
        Weather.ModeChanged += SyncWeatherUi;
        AppInstance.Desktop.Scene.WallpaperChanged += ApplyWallpaperTint;
    }

    // ---------------- 窗口初始化：液态玻璃底 ----------------

    void OnSourceInitialized(object sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        // 不抢焦点、不出现在 Alt+Tab
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));

        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        ApplyTheme();
        SyncWeatherUi();
        UpdateClock();

        _tracker.ImportWindowsPins();
        _tracker.Refresh();
        _imeTimer.Tick += (_, _) => UpdateIme();
        _imeTimer.Start();
        UpdateIme();
        _tick.Start();
        _slow.Start();
        // DpiChanged 是路由事件，每新建一个子元素都会冒泡到这里；只有窗口的 DPI 真的变了才重新布局
        DpiChanged += (_, args) =>
        {
            if (args.NewDpi.DpiScaleX == _scale) return;
            _scale = args.NewDpi.DpiScaleX;
            Layout();
        };
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(Layout);
    }

    /// <summary>
    /// 真玻璃：GlassSurface 自己截取背景并折射；
    /// 关闭时退回系统级模糊（对非激活窗口同样有效，Mica/Acrylic 在失焦时会退化成纯色）。
    /// </summary>
    bool RealGlass => Weather.Settings.GlassRefraction && Glass.LiquidGlassEffect.IsSupported;

    void ApplyGlassMode()
    {
        bool real = RealGlass;
        GlassBg.Visibility = real ? Visibility.Visible : Visibility.Collapsed;
        FallbackBg.Visibility = real ? Visibility.Collapsed : Visibility.Visible;
        bool dock = IsDock;
        double density = dock ? Weather.Settings.DockGlass : 1;
        GlassBg.Opacity = FallbackBg.Opacity = density;
        if (density < 0.01) GlassBg.Visibility = FallbackBg.Visibility = Visibility.Collapsed;
        // 系统模糊只能作用于整个窗口，而 Dock 窗口比底座大很多（要留放大的空间），所以 Dock 不用它
        SetBlurBehind(!real && !dock);
        ApplyRegion();
    }

    /// <summary>
    /// 真玻璃自己画圆角和投影，窗口不裁剪；退回系统模糊时用窗口区域把模糊裁成胶囊形。
    /// </summary>
    void ApplyRegion()
    {
        if (_hwnd == IntPtr.Zero || _barW == 0) return;
        if (RealGlass || IsDock) { Native.SetWindowRgn(_hwnd, IntPtr.Zero, true); return; }
        int d = (int)Math.Round(CornerRadiusDip * 2 * _scale);
        Native.SetWindowRgn(_hwnd, Native.CreateRoundRectRgn(_pad, _pad, _pad + _barW + 1, _pad + _barH + 1, d, d), true);
    }

    void SetBlurBehind(bool on)
    {
        var accent = new Native.AccentPolicy { AccentState = on ? 3 /*ACCENT_ENABLE_BLURBEHIND*/ : 0, GradientColor = 0x00000000 };
        int size = Marshal.SizeOf(accent);
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, p, false);
            var data = new Native.WindowCompositionAttributeData { Attribute = 19 /*WCA_ACCENT_POLICY*/, Data = p, SizeOfData = size };
            Native.SetWindowCompositionAttribute(_hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    void Layout()
    {
        if (_hwnd == IntPtr.Zero) return;
        _mon = Native.GetPrimaryMonitorRect();
        _pad = (int)Math.Round(ShadowPad * _scale);
        if (IsDock) { LayoutDock(force: true); return; }

        double barH = Weather.Settings.BarHeight;
        _barW = (int)Math.Round(_mon.Width - SideMargin * 2 * _scale);
        _barH = (int)Math.Round(barH * _scale);
        _x = _mon.Left + (int)Math.Round(SideMargin * _scale);
        _shownY = _mon.Bottom - _barH - (int)Math.Round(BottomMargin * _scale);
        _hiddenY = _mon.Bottom + 2;

        Width = (_barW + _pad * 2) / _scale;
        Height = barH + ShadowPad * 2;
        PlaceWindow(_x - _pad, 0, _barW + _pad * 2, _barH + _pad * 2, _pad);
        ApplyRegion();
    }

    void PlaceWindow(int winX, int winY, int winW, int winH, int bodyTopPx)
    {
        _winX = winX; _winY = winY; _winW = winW; _winH = winH; _bodyTopPx = bodyTopPx;
        _lastY = int.MinValue;
        var (x, y) = WindowAt(CurrentY());
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, _winW, _winH, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        Dispatcher.BeginInvoke(ResyncWindowSize, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Dock 横竖切换时窗口的宽高同时大幅变化，WPF 的内部布局可能停在旧尺寸，
    /// 超出旧尺寸的内容全被裁掉（窗口坐标是对的，但图标只显示一小块或完全不见）。
    /// 这里核对一次，不一致就让窗口再发一次尺寸变化。
    /// </summary>
    void ResyncWindowSize()
    {
        if (Math.Abs(Root.ActualWidth * _scale - _winW) < 2 && Math.Abs(Root.ActualHeight * _scale - _winH) < 2) return;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, _winW + 1, _winH + 1, Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, _winW, _winH, Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        InvalidateMeasure();
        UpdateLayout();
    }

    /// <summary>滑动轴坐标 → 窗口左上角。</summary>
    (int x, int y) WindowAt(int slide) =>
        DockIsVertical ? (slide - _bodyTopPx, _winY) : (_winX, slide - _bodyTopPx);

    int CurrentY()
    {
        double e = EaseOutCubic(_shown);
        return (int)Math.Round(_hiddenY + (_shownY - _hiddenY) * e);
    }

    static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);

    /// <summary>
    /// 图标组相对整条任务栏居中。图标多到居中会碰到两侧时，先压缩间距（最窄 44），
    /// 仍放不下再贴着左侧区域往左让。
    /// </summary>
    void CenterTasks()
    {
        double total = ContentGrid.ActualWidth;
        if (total <= 0) return;
        const double gap = 12, fullPitch = 62, minPitch = 44;
        double left = StartButton.ActualWidth + gap;
        double right = total - RightPanel.ActualWidth - gap;

        // 以中心为轴，两侧都不碰到时可用的宽度
        double centered = 2 * Math.Min(total / 2 - left, right - total / 2);
        int n = Math.Max(1, _tracker.Items.Count);
        double pitch = Math.Clamp(Math.Floor(centered / n), minPitch, fullPitch);
        double margin = pitch >= fullPitch ? 3 : 1;
        if (!Equals(Resources["TaskItemWidth"], pitch - margin * 2))
        {
            Resources["TaskItemWidth"] = pitch - margin * 2;
            Resources["TaskItemMargin"] = new Thickness(margin, 0, margin, 0);
        }

        Tasks.Measure(new Size(double.PositiveInfinity, ContentGrid.ActualHeight));
        // DesiredSize 含 Margin，需扣掉，否则会随上一次设置的边距来回跳
        double w = Tasks.DesiredSize.Width - Tasks.Margin.Left - Tasks.Margin.Right;
        double x = Math.Max(left, Math.Min((total - w) / 2, right - w));

        Tasks.MaxWidth = Math.Max(0, right - left);
        if (Math.Abs(Tasks.Margin.Left - x) > 0.5) Tasks.Margin = new Thickness(x, 0, 0, 0);
    }

    // ---------------- 智能隐藏 ----------------

    void OnTick()
    {
        Native.GetCursorPos(out var pt);
        var now = DateTime.Now;

        var shownRect = BodyRect(_shownY);
        bool overBar = OverBody(pt) && _shown > 0.3;
        bool atEdge = AtScreenEdge(pt);

        var fg = Native.GetForegroundWindow();
        var state = ClassifyForeground(fg, shownRect);

        if (atEdge) { if (_edgeSince == DateTime.MaxValue) _edgeSince = now; }
        else _edgeSince = DateTime.MaxValue;
        // 全屏（游戏/视频）时需要在屏幕边停留一下才呼出，避免误触；左右两侧也一样，那里常有滚动条
        bool edgeReveal = atEdge && ((state != FgState.Fullscreen && !DockIsVertical) || (now - _edgeSince).TotalMilliseconds > 350);

        bool want = overBar || edgeReveal || now < _revealUntil || _openMenus > 0 || PeekOpen || TrayPopup.IsOpen || ImePopup.IsOpen || AppInstance.StartMenu.IsVisible || AppInstance.AppearanceOpen || state == FgState.Clear;
        if (want) _lastWanted = now;
        double target = want || (now - _lastWanted).TotalMilliseconds < 800 ? 1 : 0;

        double prev = _shown;
        double speed = target > _shown ? 14 : 9;
        _shown += (target - _shown) * (1 - Math.Exp(-0.016 * speed));
        if (Math.Abs(target - _shown) < 0.002) _shown = target;

        int y = CurrentY();
        if (y != _lastY)
        {
            bool reassertTop = prev < 0.01 && _shown > 0;
            var (wx, wy) = WindowAt(y);
            Native.SetWindowPos(_hwnd, reassertTop ? Native.HWND_TOPMOST : IntPtr.Zero, wx, wy, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | (reassertTop ? 0 : Native.SWP_NOZORDER));
            _lastY = y;
        }
        ContentGrid.Opacity = 0.35 + 0.65 * _shown;
        UpdateGlassOptics();
        if (IsDock)
        {
            UpdateHover(pt);
            UpdateFinder(pt, fg, now);
        }

        // 桌面被挡住时暂停桌面雨层绘制（声音照常）
        AppInstance.Desktop.Scene.Paused = state == FgState.Fullscreen || state == FgState.Maximized;

        var audio = AppInstance.Audio;
        audio.RainTarget = (float)(Weather.Intensity * (Weather.Mode == WeatherMode.Storm ? 1.0 : 0.7));
        // 雪天只有很轻的底风；暴雪、龙卷风底风强，阵风叠在上面
        audio.WindTarget = (float)Math.Min(1, Weather.BaseWind + Weather.Gust * (Weather.BaseWind > 0 ? 0.6 : 1));
        audio.HowlTarget = (float)(Weather.Blizzard * (0.75 + 0.25 * Weather.Gust));
        audio.RoarTarget = (float)Weather.Tornado;
        audio.Muted = !Weather.Settings.Sound;
        audio.MasterVolume = (float)Weather.Settings.Volume;
    }

    enum FgState { Clear, Overlapping, Maximized, Fullscreen }

    static readonly HashSet<string> ShellClasses = new()
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow", "ForegroundStaging",
    };

    FgState ClassifyForeground(IntPtr fg, Native.RECT barRect)
    {
        if (fg == IntPtr.Zero || fg == _hwnd || ShellClasses.Contains(Native.GetClass(fg))) return FgState.Clear;
        if (Native.IsIconic(fg) || !Native.IsWindowVisible(fg) || Native.IsCloaked(fg)) return FgState.Clear;
        if (!Native.GetWindowRect(fg, out var r)) return FgState.Clear;
        if (r.Left <= _mon.Left && r.Top <= _mon.Top && r.Right >= _mon.Right && r.Bottom >= _mon.Bottom)
            return Native.IsZoomed(fg) ? FgState.Maximized : FgState.Fullscreen;
        if (Native.IsZoomed(fg) && r.Intersects(_mon)) return FgState.Maximized;
        return r.Intersects(barRect) ? FgState.Overlapping : FgState.Clear;
    }

    static string ReloadFlag => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LightningTaskbar", "debug-reload-settings");

    /// <summary>调试：存在该文件时重新读取外观设置并应用（等同于在面板里改），读完删除。</summary>
    void CheckReloadFlag()
    {
        if (!System.IO.File.Exists(ReloadFlag)) return;
        try { System.IO.File.Delete(ReloadFlag); } catch { }
        var d = Settings.Load();
        var s = Weather.Settings;
        s.Theme = d.Theme;
        s.DockEdge = d.DockEdge;
        s.DockIconSize = d.DockIconSize;
        s.DockLift = d.DockLift;
        s.DockMagnify = d.DockMagnify;
        s.DockReflection = d.DockReflection;
        s.DockGlass = d.DockGlass;
        s.DockHoverEffect = d.DockHoverEffect;
        s.FinderCorner = d.FinderCorner;
        ApplyTheme();
    }

    /// <summary>
    /// 调试：debug-weather 文件写天气名（如 Blizzard），debug-edge 文件写边缘特效名（Glow / StormGlow / Frost / Leaves，空 = 按天气），
    /// 读完删除。方便在脚本里切天气、触发特效后截图检查。
    /// </summary>
    void CheckDebugTriggers()
    {
        var dir = System.IO.Path.GetDirectoryName(ReloadFlag);
        string Take(string name)
        {
            var f = System.IO.Path.Combine(dir, name);
            if (!System.IO.File.Exists(f)) return null;
            try { var text = System.IO.File.ReadAllText(f).Trim(); System.IO.File.Delete(f); return text; } catch { return null; }
        }
        if (Take("debug-weather") is { } w && Enum.TryParse<WeatherMode>(w, true, out var mode)) Weather.SetMode(mode);
        if (Take("debug-edge") is { } e)
            AppInstance.Notifications.PlayEdge(Enum.TryParse<EdgeStyle>(e, true, out var style) ? style : Weather.Mode.Edge(), force: true);
        if (Take("debug-notify") != null) AppInstance.Notifications.ShowTest();
    }

    void OnSlowTick()
    {
        CheckReloadFlag();
        CheckDebugTriggers();
        _tracker.Refresh();
        ShellTaskbar.Enforce();
        UpdateClock();
        ApplyWallpaperTint();
    }

    void UpdateClock()
    {
        var now = DateTime.Now;
        TimeText.Text = now.ToString("HH:mm");
        DateText.Text = $"{now.Month}月{now.Day}日 周{"日一二三四五六"[(int)now.DayOfWeek]}";
    }

    // ---------------- 视觉：染色 / 光泽 / 闪电 ----------------

    void ApplyWallpaperTint()
    {
        var c = AppInstance.Desktop.Scene.WallpaperBottomColor;
        double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        // 真玻璃按实际挡在后面的画面决定深浅；退回系统模糊时只能参考壁纸
        double back = RealGlass && GlassBg.BackdropLuminance >= 0 ? GlassBg.BackdropLuminance : lum;
        // 滞回，避免在阈值附近来回闪
        bool light = _lightTheme ? back > 0.56 : back > 0.66;
        if (light == _lightTheme && _themeApplied) return;
        _lightTheme = light;
        _themeApplied = true;
        bool storm = Weather.Mode != WeatherMode.Off;
        // Dock 要“看不出有一条栏”：染色只留一半，浓淡再由 DockGlass 决定
        double tintScale = IsDock ? 0.5 : 1;

        if (light)
        {
            // 浅色背景：像磨砂白玻璃，配深色字，不再把玻璃压成灰色
            GlassBg.TintColor = Color.FromRgb(246, 248, 252);
            GlassBg.BaseTint = 0.18 * tintScale;
            GlassBg.AdaptiveTint = false;
            GlassBg.HighlightCompression = 0.05;
        }
        else
        {
            // 深色背景：极淡的环境色染色，雨天稍深，保证白字可读
            GlassBg.TintColor = Mix(c, Colors.Black, 0.6);
            GlassBg.BaseTint = (storm ? 0.16 : 0.1) * tintScale;
            GlassBg.AdaptiveTint = true;
            GlassBg.HighlightCompression = 0.38;
        }
        Resources["Fg"] = new SolidColorBrush(light ? Color.FromRgb(16, 20, 28) : Colors.White);
        Resources["FgDim"] = new SolidColorBrush(light ? Color.FromArgb(0xB0, 16, 20, 28) : Color.FromArgb(0xB3, 255, 255, 255));
    }

    bool _lightTheme, _themeApplied;

    static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    // 闪电时玻璃被照亮：交给着色器（边缘更亮，像真的光打在玻璃上）
    DateTime _flashAt = DateTime.MinValue;
    double _flashPeak;

    void UpdateGlassOptics()
    {
        double t = (DateTime.Now - _flashAt).TotalSeconds;
        double flash = t > 1.2 ? 0 : _flashPeak * Math.Max(Math.Exp(-t / 0.07), 0.85 * Math.Exp(-Math.Max(0, t - 0.13) / 0.25) * (t > 0.12 ? 1 : 0));
        GlassBg.Flash = flash;
        GlassBg.Rain = Weather.Intensity;
    }

    void OnLightning(LightningStrike s)
    {
        _flashAt = DateTime.Now;
        _flashPeak = 0.35 + s.Intensity * 0.55;

        var glow = new DoubleAnimation(1, 0.6, TimeSpan.FromMilliseconds(1200)) { EasingFunction = new QuadraticEase() };
        BoltGlow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, glow);

        // 光速 vs 声速：雷声按距离延迟
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(s.ThunderDelay) };
        t.Tick += (_, _) => { t.Stop(); AppInstance.Audio.Thunder(s.Intensity, s.X, s.Seed); };
        t.Start();
    }

    void SyncWeatherUi()
    {
        WeatherGlyph.Text = Weather.Mode.Glyph();
        WeatherButton.ToolTip = $"天气：{Weather.Mode.ShortLabel()}（点击选择）";
        SoundGlyph.Text = Weather.Settings.Sound ? "" : "";
        _themeApplied = false;
        SoundButton.ToolTip = $"天气音效：{(Weather.Settings.Sound ? "开" : "关")}  音量 {Weather.Settings.Volume:P0}（滚轮调节）";
        ApplyWallpaperTint();
    }

    void SyncMenu()
    {
        MenuOff.IsChecked = Weather.Mode == WeatherMode.Off;
        MenuRain.IsChecked = Weather.Mode == WeatherMode.Rain;
        MenuStorm.IsChecked = Weather.Mode == WeatherMode.Storm;
        MenuLightSnow.IsChecked = Weather.Mode == WeatherMode.LightSnow;
        MenuSnow.IsChecked = Weather.Mode == WeatherMode.Snow;
        MenuBlizzard.IsChecked = Weather.Mode == WeatherMode.Blizzard;
        MenuTornado.IsChecked = Weather.Mode == WeatherMode.Tornado;
        MenuSound.IsChecked = Weather.Settings.Sound;
        MenuGlass.IsChecked = Weather.Settings.GlassRefraction;
        MenuExplorer.IsChecked = Weather.Settings.ExplorerGlass;
    }

    // ---------------- 交互 ----------------

    void Start_Click(object sender, RoutedEventArgs e) => AppInstance.StartMenu.Toggle();
    void QuickSettings_Click(object sender, RoutedEventArgs e) => Native.PressKey(0x5B, 0x41);
    void Clock_Click(object sender, RoutedEventArgs e) => Native.PressKey(0x5B, 0x4E);
    void ShowDesktop_Click(object sender, RoutedEventArgs e) => Native.PressKey(0x5B, 0x44);

    void Task_Click(object sender, RoutedEventArgs e)
    {
        HidePeek();
        if (((FrameworkElement)sender).DataContext is TaskItem t) WindowTracker.Activate(t);
    }

    void Task_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && ((FrameworkElement)sender).DataContext is TaskItem t) WindowTracker.Close(t);
    }

    void TaskClose_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TaskItem t) WindowTracker.Close(t);
    }

    void TaskCloseAll_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TaskItem t) WindowTracker.CloseAll(t);
    }

    /// <summary>多窗口的应用：右键菜单顶部列出每个窗口，点哪个切到哪个。</summary>
    void TaskMenu_Opened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        if (menu.DataContext is not TaskItem t) return;
        foreach (var old in menu.Items.OfType<MenuItem>().Where(m => (string)m.Tag == "window").ToList()) menu.Items.Remove(old);
        var header = menu.Items.OfType<MenuItem>().First(m => (string)m.Tag == "header");
        header.Visibility = t.IsGroup ? Visibility.Collapsed : Visibility.Visible;
        if (!t.IsGroup) return;

        int at = menu.Items.IndexOf(header);
        foreach (var h in t.Windows)
        {
            var mi = new MenuItem { Header = Native.GetText(h), Tag = "window", IsChecked = h == Native.GetForegroundWindow() };
            mi.Click += (_, _) => WindowTracker.Focus(h);
            menu.Items.Insert(++at, mi);
        }
    }

    void TaskNew_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TaskItem t) WindowTracker.LaunchNew(t);
    }

    void TaskPin_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is TaskItem t) _tracker.TogglePin(t);
    }

    // ---------------- 输入法 ----------------

    readonly DispatcherTimer _imeTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };

    void UpdateIme()
    {
        var text = ImeService.Indicator();
        if (ImeText.Text != text) ImeText.Text = text;
    }

    void Ime_Click(object sender, RoutedEventArgs e)
    {
        if (ImePopup.IsOpen) { ImePopup.IsOpen = false; return; }
        ImeList.ItemsSource = ImeService.GetProfiles();
        ImeButton.ToolTip = "输入法：" + ImeService.CurrentLanguageName();
        ImePopup.IsOpen = true;
    }

    void ImeItem_Click(object sender, RoutedEventArgs e)
    {
        ImePopup.IsOpen = false;
        if (((FrameworkElement)sender).DataContext is InputProfile p) ImeService.Activate(p);
        Dispatcher.BeginInvoke(UpdateIme, DispatcherPriority.Background);
    }

    void ImeSettings_Click(object sender, RoutedEventArgs e)
    {
        ImePopup.IsOpen = false;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true }); } catch { }
    }

    // ---------------- 系统托盘 ----------------

    static ManagedShell.WindowsTray.NotifyIcon TrayIconOf(object sender) =>
        ((FrameworkElement)sender).DataContext as ManagedShell.WindowsTray.NotifyIcon;

    void TrayOverflow_Click(object sender, RoutedEventArgs e) => TrayPopup.IsOpen = !TrayPopup.IsOpen;

    void TrayIcon_MouseEnter(object sender, MouseEventArgs e) { if (TrayIconOf(sender) is { } i) TrayHost.MouseEnter(i); }
    void TrayIcon_MouseLeave(object sender, MouseEventArgs e) { if (TrayIconOf(sender) is { } i) TrayHost.MouseLeave(i); }
    void TrayIcon_MouseMove(object sender, MouseEventArgs e) { if (TrayIconOf(sender) is { } i) TrayHost.MouseMove(i); }

    void TrayIcon_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (TrayIconOf(sender) is not { } i) return;
        e.Handled = true;   // 不让任务栏自己的右键菜单弹出来
        if (e.ChangedButton != MouseButton.Middle) TrayHost.MouseDown(i, e.ChangedButton);
    }

    void TrayIcon_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (TrayIconOf(sender) is not { } i) return;
        e.Handled = true;
        if (e.ChangedButton == MouseButton.Middle)
        {
            AppInstance.Tray.TogglePin(i);
            return;
        }
        // 先关掉溢出框，免得挡住应用弹出的菜单
        TrayPopup.IsOpen = false;
        TrayHost.MouseUp(i, e.ChangedButton);
    }

    // 把 .exe / .lnk 拖到任务栏上即可固定
    static IEnumerable<string> PinnableDrops(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            : Enumerable.Empty<string>();

    void Root_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = PinnableDrops(e).Any() ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    void Root_Drop(object sender, DragEventArgs e)
    {
        foreach (var f in PinnableDrops(e)) _tracker.Pin(f);
    }

    /// <summary>天气按钮：弹出一个只列天气的小菜单（七种天气，循环点击太慢）。</summary>
    void Weather_Click(object sender, RoutedEventArgs e)
    {
        // Dock 主题下按钮在顶部的 Finder 栏里，菜单往下弹；通栏主题在底部，往上弹
        var menu = new ContextMenu
        {
            PlacementTarget = WeatherButton,
            Placement = IsDock ? System.Windows.Controls.Primitives.PlacementMode.Bottom : System.Windows.Controls.Primitives.PlacementMode.Top,
        };
        foreach (var m in WeatherModes.All)
        {
            var item = new MenuItem { Header = $"{m.Glyph()}  {m.Label()}", IsCheckable = true, IsChecked = Weather.Mode == m, Tag = m.ToString() };
            item.Click += MenuMode_Click;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    void Sound_Click(object sender, RoutedEventArgs e)
    {
        Weather.Settings.Sound = !Weather.Settings.Sound;
        Weather.NotifySettingsChanged();
    }

    void Sound_Wheel(object sender, MouseWheelEventArgs e)
    {
        Weather.Settings.Volume = Math.Clamp(Weather.Settings.Volume + Math.Sign(e.Delta) * 0.05, 0, 1);
        Weather.NotifySettingsChanged();
    }

    void MenuMode_Click(object sender, RoutedEventArgs e) =>
        Weather.SetMode(Enum.Parse<WeatherMode>((string)((MenuItem)sender).Tag));

    void MenuSound_Click(object sender, RoutedEventArgs e) => Sound_Click(sender, e);

    void MenuGlass_Click(object sender, RoutedEventArgs e)
    {
        Weather.Settings.GlassRefraction = !Weather.Settings.GlassRefraction;
        Weather.Settings.Save();
        ApplyGlassMode();
    }

    void MenuExplorer_Click(object sender, RoutedEventArgs e)
    {
        Weather.Settings.ExplorerGlass = !Weather.Settings.ExplorerGlass;
        Weather.Settings.Save();
        if (Weather.Settings.ExplorerGlass) ExplorerGlass.Start(); else ExplorerGlass.Stop();
    }
    void MenuStrike_Click(object sender, RoutedEventArgs e) => Weather.Strike();
    void MenuTestNotify_Click(object sender, RoutedEventArgs e) => AppInstance.Notifications.ShowTest();
    void MenuEdge_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((MenuItem)sender).Tag;
        AppInstance.Notifications.PlayEdge(string.IsNullOrEmpty(tag) ? Weather.Mode.Edge() : Enum.Parse<EdgeStyle>(tag), force: true);
    }
    void MenuAppearance_Click(object sender, RoutedEventArgs e) => AppInstance.ShowAppearance();
    void MenuTaskMgr_Click(object sender, RoutedEventArgs e) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });

    void MenuExit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
