using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace LightningTaskbar;

/// <summary>
/// 屏幕顶部的 Finder 栏：放托盘、输入法、天气、音效、快速设置和时钟。
/// 这些控件和它们的事件都还属于 <see cref="TaskbarWindow"/>，这里只提供玻璃外壳、位置和滑入滑出。
/// </summary>
public partial class FinderBarWindow : Window
{
    /// <summary>窗口比玻璃四周多出的透明边（与 XAML 里的 ShadowPad 一致）。</summary>
    public const double ShadowPad = 12;
    /// <summary>玻璃离屏幕顶边和侧边的距离（DIP）。</summary>
    public const double TopGap = 6;

    readonly TaskbarWindow _bar;
    IntPtr _hwnd;
    double _shown = 1;
    int _lastX = int.MinValue, _lastY = int.MinValue;
    bool _lightTheme, _themeApplied;

    internal FinderBarWindow(TaskbarWindow bar)
    {
        _bar = bar;
        InitializeComponent();
        GlassBg.FrostRadius = 10;
        GlassBg.FrostMix = 0.85;
        GlassBg.Refraction = 7;
        GlassBg.RimStrength = 0.5;
        GlassBg.ShadowOpacity = 0.16;
        GlassBg.Veil = 0.01;
        GlassBg.Fps = 24;
        if (!Glass.LiquidGlassEffect.IsSupported || !WeatherEngine.Instance.Settings.GlassRefraction)
        {
            GlassBg.Visibility = Visibility.Collapsed;
            Host.Background = new SolidColorBrush(Color.FromArgb(0xD8, 22, 28, 40));
        }

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            // 不抢焦点、不进 Alt+Tab
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        };
        SizeChanged += (_, _) => Reposition();
    }

    double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    public bool IsMostlyShown => _shown > 0.3;

    /// <summary>玻璃本体完全显示时的屏幕矩形（物理像素）。</summary>
    internal Native.RECT ShownRect
    {
        get
        {
            var mon = Native.GetPrimaryMonitorRect();
            double s = Scale;
            int w = (int)Math.Round((ActualWidth - ShadowPad * 2) * s), h = (int)Math.Round((ActualHeight - ShadowPad * 2) * s);
            int gap = (int)Math.Round(TopGap * s);
            int left = WeatherEngine.Instance.Settings.FinderCorner == TopCorner.Left ? mon.Left + gap : mon.Right - gap - w;
            return new Native.RECT { Left = left, Top = mon.Top + gap, Right = left + w, Bottom = mon.Top + gap + h };
        }
    }

    public void Reposition()
    {
        _lastX = _lastY = int.MinValue;
        if (_hwnd != IntPtr.Zero && IsVisible) ApplyPosition();
    }

    void ApplyPosition()
    {
        var r = ShownRect;
        double s = Scale;
        int pad = (int)Math.Round(ShadowPad * s);
        int x = r.Left - pad;
        int shownY = r.Top - pad;
        int hiddenY = -(int)Math.Round(ActualHeight * s) - 2 + Native.GetPrimaryMonitorRect().Top;
        double e = 1 - Math.Pow(1 - _shown, 3);
        int y = (int)Math.Round(hiddenY + (shownY - hiddenY) * e);
        if (x == _lastX && y == _lastY) return;
        bool reassertTop = _lastY == int.MinValue;
        Native.SetWindowPos(_hwnd, reassertTop ? Native.HWND_TOPMOST : IntPtr.Zero, x, y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | (reassertTop ? 0 : Native.SWP_NOZORDER));
        _lastX = x;
        _lastY = y;
    }

    /// <summary>每帧由任务栏调用：推进滑入滑出，并按背后亮度切换深浅色文字。</summary>
    internal void Step(bool want)
    {
        if (!IsVisible) return;
        double target = want ? 1 : 0;
        _shown += (target - _shown) * (1 - Math.Exp(-0.016 * (want ? 14 : 9)));
        if (Math.Abs(target - _shown) < 0.002) _shown = target;
        ApplyPosition();
        UpdateTint();
    }

    void UpdateTint()
    {
        double back = GlassBg.BackdropLuminance;
        if (back < 0 || GlassBg.Visibility != Visibility.Visible) return;
        bool light = _lightTheme ? back > 0.56 : back > 0.66;   // 滞回，避免在阈值附近来回闪
        if (light == _lightTheme && _themeApplied) return;
        _lightTheme = light;
        _themeApplied = true;
        GlassBg.TintColor = light ? Color.FromRgb(246, 248, 252) : Color.FromRgb(12, 16, 24);
        GlassBg.BaseTint = light ? 0.2 : 0.12;
        GlassBg.AdaptiveTint = !light;
        GlassBg.HighlightCompression = light ? 0.05 : 0.38;
        Resources["Fg"] = new SolidColorBrush(light ? Color.FromRgb(16, 20, 28) : Colors.White);
        Resources["FgDim"] = new SolidColorBrush(light ? Color.FromArgb(0xB0, 16, 20, 28) : Color.FromArgb(0xB3, 255, 255, 255));
    }

    void Host_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _bar.OpenBarMenu(Host);
    }
}
