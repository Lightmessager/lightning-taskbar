using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LightningTaskbar.StartMenu;

/// <summary>玻璃风格的开始菜单：所有应用（含商店应用）+ 搜索 + 电源。</summary>
public partial class StartMenuWindow : Window
{
    List<AppEntry> _all = new();
    List<AppEntry> _pinned = new();
    bool _loaded, _showAll, _syncing;
    int _iconSize = 48;
    IntPtr _hwnd;

    [DllImport("powrprof.dll")] static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);

    public StartMenuWindow()
    {
        InitializeComponent();
        UserName.Text = Environment.UserName;
        Avatar.Text = string.IsNullOrEmpty(Environment.UserName) ? "U" : Environment.UserName.Substring(0, 1).ToUpperInvariant();
        // 菜单里文字多：磨砂更重、染色更深，倒角更宽
        GlassBg.BaseTint = 0.28;
        GlassBg.FrostRadius = 14;
        GlassBg.FrostMix = 1;
        GlassBg.Refraction = 18;
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

    public void Toggle()
    {
        if (IsVisible) Close2();
        else Open();
    }

    async void Open()
    {
        Search.Text = "";
        _showAll = false;
        ApplyFilter();
        Show();
        PlaceAboveTaskbar();

        // 绕过前台锁，确保搜索框能直接打字
        Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
        Native.SetForegroundWindow(_hwnd);
        Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
        Activate();
        Search.Focus();
        Keyboard.Focus(Search);

        Root.Opacity = 0;
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(36, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));

        if (!_loaded)
        {
            _loaded = true;
            _iconSize = (int)Math.Round(48 * VisualTreeHelper.GetDpi(this).DpiScaleX);
            _all = await AppCatalog.LoadAsync();
            ApplyFilter();
            AppCatalog.LoadIconsAsync(_all, Dispatcher, _iconSize);
        }
        await SyncPinnedAsync();
    }

    /// <summary>每次打开都在后台和 Windows 开始菜单的“已固定”同步一次。</summary>
    async Task SyncPinnedAsync()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            var pinned = await AppCatalog.LoadPinnedAsync(_all);
            if (pinned == null) return;
            _pinned = pinned;
            AppCatalog.LoadIconsAsync(_pinned, Dispatcher, _iconSize);
            ApplyFilter();
        }
        finally { _syncing = false; }
    }

    void ViewToggle_Click(object sender, RoutedEventArgs e)
    {
        _showAll = !_showAll;
        ApplyFilter();
    }

    void PlaceAboveTaskbar()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var mon = Native.GetPrimaryMonitorRect();
        var s = WeatherEngine.Instance.Settings;
        int w = (int)(Width * scale), h = (int)(Height * scale);
        int x = mon.Left + (int)(14 * scale);
        int y = mon.Bottom - (int)((8 + s.BarHeight + 12) * scale) - h;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_SHOWWINDOW);
    }

    void Close2()
    {
        if (!IsVisible) return;
        Hide();
    }

    void OnDeactivated(object sender, EventArgs e) => Close2();

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close2(); e.Handled = true; }
        else if (e.Key == Key.Enter && Apps.Items.Count > 0) { Launch((AppEntry)Apps.Items[0]); e.Handled = true; }
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(Search.Text) ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    void ApplyFilter()
    {
        var q = Search.Text.Trim();
        ViewToggle.Visibility = q.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (q.Length == 0)
        {
            SectionTitle.Text = _showAll ? "所有应用" : "已固定";
            ViewToggleText.Text = _showAll ? "‹  返回" : "所有应用  ›";
            Apps.ItemsSource = _showAll ? _all : _pinned;
            return;
        }
        // 开头匹配优先，其次包含
        var hits = _all.Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                       .OrderBy(a => a.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        SectionTitle.Text = hits.Count == 0 ? "没有找到匹配的应用" : "最佳匹配";
        Apps.ItemsSource = hits;
    }

    void App_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is AppEntry a) Launch(a);
    }

    void Launch(AppEntry a)
    {
        Close2();
        AppCatalog.Launch(a);
    }

    void NativeStart_Click(object sender, RoutedEventArgs e)
    {
        Close2();
        Native.PressKey(0x5B);   // 注入的 Win 键会被钩子放行
    }

    void Settings_Click(object sender, RoutedEventArgs e) { Close2(); Shell("ms-settings:"); }

    void Power_Click(object sender, RoutedEventArgs e)
    {
        PowerButton.ContextMenu.PlacementTarget = PowerButton;
        PowerButton.ContextMenu.IsOpen = true;
    }

    void Sleep_Click(object sender, RoutedEventArgs e) { Close2(); SetSuspendState(false, false, false); }
    void SignOut_Click(object sender, RoutedEventArgs e) { Close2(); Run("shutdown.exe", "/l"); }
    void Restart_Click(object sender, RoutedEventArgs e) { Close2(); Run("shutdown.exe", "/r /t 0"); }
    void Shutdown_Click(object sender, RoutedEventArgs e) { Close2(); Run("shutdown.exe", "/s /t 0"); }

    static void Shell(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    static void Run(string exe, string args)
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false }); } catch { }
    }
}
