using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace LightningTaskbar.Notifications;

/// <summary>右上角的一张玻璃通知卡片。位置和进出动画由 <see cref="NotificationCenter"/> 统一驱动。</summary>
public partial class NotificationCard : Window
{
    /// <summary>窗口比卡片四周多出的透明边（与 XAML 里的 ShadowPad 一致）。</summary>
    public const double ShadowPad = 12;
    static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);

    readonly NotificationInfo _info;
    DateTime _shownAt = DateTime.Now;
    IntPtr _hwnd;

    public NotificationCard(NotificationInfo info)
    {
        _info = info;
        InitializeComponent();

        AppName.Text = info.AppName;
        TitleText.Text = info.Title;
        BodyText.Text = info.Body;
        BodyText.Visibility = string.IsNullOrEmpty(info.Body) ? Visibility.Collapsed : Visibility.Visible;
        AppIcon.Source = info.Icon;
        if (info.Icon == null) AppIcon.Visibility = Visibility.Collapsed;

        // 卡片上全是文字：磨砂和染色都比任务栏重，亮背景上也读得清
        GlassBg.BaseTint = 0.3;
        GlassBg.FrostRadius = 12;
        GlassBg.FrostMix = 1;
        GlassBg.Refraction = 10;
        GlassBg.HighlightCompression = 0.55;
        GlassBg.RimStrength = 0.8;
        GlassBg.Fps = 24;
        if (!Glass.LiquidGlassEffect.IsSupported || !WeatherEngine.Instance.Settings.GlassRefraction)
        {
            GlassBg.Visibility = Visibility.Collapsed;
            Body.Background = new SolidColorBrush(Color.FromArgb(0xEE, 22, 28, 40));
        }

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            // 不抢焦点、不进 Alt+Tab
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        };
        MouseEnter += (_, _) => CloseButton.Opacity = 1;
        MouseLeave += (_, _) => { CloseButton.Opacity = 0; _shownAt = DateTime.Now; };   // 移开后重新计时
    }

    public double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;
    public double PixelWidth => ActualWidth * DpiScale;
    public double PixelHeight => ActualHeight * DpiScale;

    public double PosX { get; set; }
    public double PosY { get; set; }
    public bool Leaving { get; set; }
    public bool Expired => !IsMouseOver && DateTime.Now - _shownAt > Lifetime;
    public double RootOpacity { get => Root.Opacity; set => Root.Opacity = value; }

    public void ApplyPosition() =>
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, (int)Math.Round(PosX), (int)Math.Round(PosY), 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

    void Close_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Leaving = true;
    }

    void Card_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && IsInside(d, CloseButton)) return;
        Leaving = true;
        if (_info.AppId == null) return;
        try { Process.Start(new ProcessStartInfo("shell:AppsFolder\\" + _info.AppId) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    static bool IsInside(DependencyObject d, DependencyObject ancestor)
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d)) if (d == ancestor) return true;
        return false;
    }
}
