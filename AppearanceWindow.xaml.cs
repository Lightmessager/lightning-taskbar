using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace LightningTaskbar;

/// <summary>外观面板：主题切换和 Dock 的各项参数，拖动时任务栏实时变化。</summary>
public partial class AppearanceWindow : Window
{
    static Settings S => WeatherEngine.Instance.Settings;
    static App AppInstance => (App)Application.Current;

    IntPtr _hwnd;
    /// <summary>初始为 true：滑块在 InitializeComponent 里设 Minimum 时就会触发 ValueChanged，那时别的控件还没创建。</summary>
    bool _syncing = true;

    public AppearanceWindow()
    {
        InitializeComponent();
        // 面板上文字多：和开始菜单一样用较重的磨砂
        GlassBg.BaseTint = 0.32;
        GlassBg.FrostRadius = 14;
        GlassBg.FrostMix = 1;
        GlassBg.HighlightCompression = 0.5;
        if (!Glass.LiquidGlassEffect.IsSupported)
            Root.Background = new SolidColorBrush(Color.FromArgb(0xEE, 22, 28, 40));
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        };
    }

    public void Open()
    {
        LoadValues();
        Show();
        Place();
        // 拿到前台才能在点别处时收到 Deactivated
        Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
        Native.SetForegroundWindow(_hwnd);
        Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
        Activate();
    }

    /// <summary>摆在屏幕角落并让开 Dock，拖动滑块时才看得到效果。</summary>
    void Place()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var mon = Native.GetPrimaryMonitorRect();
        int w = (int)(ActualWidth * scale), h = (int)(ActualHeight * scale);
        var bar = AppInstance.Bar;
        int x = bar.ActiveEdge == DockEdge.Right ? mon.Left + (int)(8 * scale) : mon.Right - w - (int)(8 * scale);
        // Dock 在底部时留足最大放大时图标伸出的高度；在两侧时贴近底边就行
        double clearance = bar.ActiveEdge == DockEdge.Bottom ? bar.BodyTopFromBottom + S.DockIconSize * (S.DockMagnify - 1) + 8 : 8;
        int y = mon.Bottom - (int)(clearance * scale) - h;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE);
    }

    void LoadValues()
    {
        _syncing = true;
        ThemeDock.IsChecked = S.Theme == BarTheme.Dock;
        ThemeClassic.IsChecked = S.Theme == BarTheme.Classic;
        EdgeBottom.IsChecked = S.DockEdge == DockEdge.Bottom;
        EdgeLeft.IsChecked = S.DockEdge == DockEdge.Left;
        EdgeRight.IsChecked = S.DockEdge == DockEdge.Right;
        FxMask.IsChecked = S.DockHoverEffect == HoverEffect.LightMask;
        FxGlow.IsChecked = S.DockHoverEffect == HoverEffect.Glow;
        FxSpot.IsChecked = S.DockHoverEffect == HoverEffect.Spotlight;
        FxBounce.IsChecked = S.DockHoverEffect == HoverEffect.Bounce;
        FxNone.IsChecked = S.DockHoverEffect == HoverEffect.None;
        FinderLeft.IsChecked = S.FinderCorner == TopCorner.Left;
        FinderRight.IsChecked = S.FinderCorner == TopCorner.Right;
        NotifyLeft.IsChecked = S.NotifyCorner == TopCorner.Left;
        NotifyRight.IsChecked = S.NotifyCorner == TopCorner.Right;
        IconSlider.Value = S.DockIconSize;
        LiftSlider.Value = S.DockLift;
        MagnifySlider.Value = S.DockMagnify;
        ReflectionSlider.Value = S.DockReflection;
        GlassSlider.Value = S.DockGlass;
        NotifyToggle.IsChecked = S.GlassNotifications;
        CaptureToggle.IsChecked = S.AllowScreenshots;
        NotifyState.Text = AppInstance.Notifications.Available
            ? "读取系统通知，免打扰和全屏时不弹出"
            : "没有读取通知的权限：设置 → 隐私和安全性 → 通知";
        _syncing = false;
        UpdateLabels();
    }

    void UpdateLabels()
    {
        IconValue.Text = $"{IconSlider.Value:0}";
        LiftValue.Text = $"{LiftSlider.Value:0}";
        MagnifyValue.Text = MagnifySlider.Value <= 1.001 ? "关" : $"{MagnifySlider.Value:0.00}×";
        ReflectionValue.Text = ReflectionSlider.Value <= 0.001 ? "关" : $"{ReflectionSlider.Value:P0}";
        GlassValue.Text = GlassSlider.Value <= 0.001 ? "无底座" : $"{GlassSlider.Value:P0}";
        DockOptions.IsEnabled = PlacementOptions.IsEnabled = ThemeDock.IsChecked == true;
        DockOptions.Opacity = PlacementOptions.Opacity = DockOptions.IsEnabled ? 1 : 0.4;
    }

    void Commit()
    {
        S.Save();
        AppInstance.Bar.ApplyTheme();
    }

    void Theme_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        S.Theme = ThemeDock.IsChecked == true ? BarTheme.Dock : BarTheme.Classic;
        UpdateLabels();
        Commit();
    }

    void Placement_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        S.DockEdge = EdgeLeft.IsChecked == true ? DockEdge.Left : EdgeRight.IsChecked == true ? DockEdge.Right : DockEdge.Bottom;
        S.DockHoverEffect = FxGlow.IsChecked == true ? HoverEffect.Glow : FxSpot.IsChecked == true ? HoverEffect.Spotlight
            : FxBounce.IsChecked == true ? HoverEffect.Bounce : FxNone.IsChecked == true ? HoverEffect.None : HoverEffect.LightMask;
        S.FinderCorner = FinderLeft.IsChecked == true ? TopCorner.Left : TopCorner.Right;
        S.NotifyCorner = NotifyRight.IsChecked == true ? TopCorner.Right : TopCorner.Left;
        Commit();
        Place();   // Dock 换边后面板也要让开
    }

    void Dock_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        S.DockIconSize = IconSlider.Value;
        S.DockLift = LiftSlider.Value;
        S.DockMagnify = MagnifySlider.Value;
        S.DockReflection = ReflectionSlider.Value;
        S.DockGlass = GlassSlider.Value;
        UpdateLabels();
        Commit();
    }

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        var d = new Settings();
        S.Theme = d.Theme;
        S.DockEdge = d.DockEdge;
        S.DockHoverEffect = d.DockHoverEffect;
        S.FinderCorner = d.FinderCorner;
        S.NotifyCorner = d.NotifyCorner;
        S.DockIconSize = d.DockIconSize;
        S.DockLift = d.DockLift;
        S.DockMagnify = d.DockMagnify;
        S.DockReflection = d.DockReflection;
        S.DockGlass = d.DockGlass;
        LoadValues();
        Commit();
        Place();
    }

    void Notify_Click(object sender, RoutedEventArgs e)
    {
        S.GlassNotifications = NotifyToggle.IsChecked == true;
        S.Save();
    }

    void Capture_Click(object sender, RoutedEventArgs e)
    {
        S.AllowScreenshots = CaptureToggle.IsChecked == true;
        Glass.GlassSurface.AllowCapture = S.AllowScreenshots;
        S.Save();
    }

    void TestNotify_Click(object sender, RoutedEventArgs e) =>
        AppInstance.Notifications.Show(new Notifications.NotificationInfo
        {
            AppName = "Lightning Taskbar",
            Title = "这是一条测试通知",
            Body = "真实通知会以同样的样式显示在这里，6 秒后自动收起，鼠标停在上面时不会消失。",
        });

    void OnDeactivated(object sender, EventArgs e) => Hide();

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Hide(); e.Handled = true; }
    }
}
