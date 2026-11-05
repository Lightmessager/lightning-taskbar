using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace LightningTaskbar.Glass;

/// <summary>
/// 真玻璃表面：实时截取元素背后（外扩一圈）的屏幕画面，交给 <see cref="LiquidGlassEffect"/> 渲染。
/// 宿主窗口被设为“不参与屏幕捕获”，这样截到的才是它背后的内容，而不是它自己。
/// 元素四周留 <see cref="ShadowPad"/> 的空白用来画投影，玻璃本体在其内侧。
/// </summary>
public sealed class GlassSurface : FrameworkElement
{
    /// <summary>圆角半径（DIP）。</summary>
    public double CornerRadius { get; set; } = 22;
    /// <summary>边缘圆弧倒角宽度（DIP），折射只发生在这一圈。</summary>
    public double Bevel { get; set; } = 16;
    /// <summary>玻璃外侧留给投影的边距（DIP）。</summary>
    public double ShadowPad { get; set; }
    public double ShadowOpacity { get; set; } = 0.32;

    /// <summary>倒角处最大折射位移（DIP）。</summary>
    public double Refraction { get; set; } = 14;
    public double Dispersion { get; set; } = 0.12;
    /// <summary>磨砂模糊半径（DIP，≈ 高斯 σ）。</summary>
    public double FrostRadius { get; set; } = 7;
    /// <summary>磨砂比例：0 = 完全清透，1 = 完全磨砂。</summary>
    public double FrostMix { get; set; } = 0.9;
    public double Saturation { get; set; } = 1.25;
    public double Brightness { get; set; } = 1.0;
    /// <summary>压低背后的高光，保证亮背景上内容可读。</summary>
    public double HighlightCompression { get; set; } = 0.38;
    /// <summary>边缘高光线强度。</summary>
    public double RimStrength { get; set; } = 1.0;
    /// <summary>乳白雾度：玻璃整体略微提亮的量。</summary>
    public double Veil { get; set; } = 0.025;

    /// <summary>基础染色比例；背景越亮，在此基础上额外加深。</summary>
    public double BaseTint { get; set; } = 0.12;
    public Color TintColor { get; set; } = Color.FromRgb(12, 16, 24);
    /// <summary>背景越亮染色越深（深色主题下保证白字可读）。浅色主题应关闭，否则白底上会变成灰塑料。</summary>
    public bool AdaptiveTint { get; set; } = true;
    /// <summary>闪电照亮玻璃的亮度 0..1。</summary>
    public double Flash { get; set; }
    /// <summary>雨量 0..1；大于 0 时玻璃上会出现雨珠。</summary>
    public double Rain { get; set; }
    /// <summary>每秒截取帧数。</summary>
    public double Fps { get; set; } = 40;

    public LiquidGlassEffect Glass { get; }

    /// <summary>
    /// 允许截图 / 录屏时看到玻璃窗口。玻璃靠截取背后的屏幕来渲染，要参与捕获就不能再截（会截到自己），
    /// 所以开启后背景定格在开启前的最后一帧，不再实时刷新。
    /// </summary>
    public static bool AllowCapture { get; set; }

    readonly BackdropCapture _capture = new();
    readonly GlassDrops _drops = new();
    readonly ImageBrush _sharpBrush = new() { Stretch = Stretch.Fill }, _frostBrush = new() { Stretch = Stretch.Fill };
    IntPtr _hwnd;
    double _lastFrame = -1, _sinceCapture = 99, _tintAmount = -1;
    bool _frozen, _attached;

    static string DebugFlag => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LightningTaskbar", "debug-show-in-capture");

    public GlassSurface()
    {
        IsHitTestVisible = false;
        if (!LiquidGlassEffect.IsSupported) return;
        Glass = new LiquidGlassEffect { Sharp = _sharpBrush, Frost = _frostBrush };
        Effect = Glass;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => { if (IsVisible) _sinceCapture = 99; };
    }

    // 着色器只看元素尺寸，画一块不透明底即可
    protected override void OnRender(DrawingContext dc) => dc.DrawRectangle(Brushes.Black, null, new Rect(RenderSize));

    void Attach()
    {
        if (_attached) return;
        var win = Window.GetWindow(this);
        if (win == null) return;
        _hwnd = new WindowInteropHelper(win).EnsureHandle();
        SetCaptureExclusion(true);
        CompositionTarget.Rendering += OnRendering;
        _attached = true;
    }

    void Detach()
    {
        if (!_attached) return;
        CompositionTarget.Rendering -= OnRendering;
        SetCaptureExclusion(false);
        _capture.Dispose();
        _attached = false;
    }

    void SetCaptureExclusion(bool exclude) =>
        SetWindowDisplayAffinity(_hwnd, exclude ? 0x11u /*WDA_EXCLUDEFROMCAPTURE*/ : 0u);

    void OnRendering(object sender, EventArgs e)
    {
        double now = ((RenderingEventArgs)e).RenderingTime.TotalSeconds;
        double dt = _lastFrame < 0 ? 0 : Math.Clamp(now - _lastFrame, 0, 0.1);
        if (dt <= 0 && _lastFrame >= 0) return;   // 同一帧可能触发多次
        _lastFrame = now;
        bool visible = IsVisible && ActualWidth >= 1 && ActualHeight >= 1;

        _sinceCapture += dt;
        if (_sinceCapture >= 1 / Fps)
        {
            _sinceCapture = 0;
            // 玻璃不显示时没有东西要截，窗口不必躲着截图。
            // 要求允许截图时先截到一帧背景再放开，否则玻璃一直是黑的。
            bool allow = AllowCapture || File.Exists(DebugFlag);
            bool frozen = !visible || (allow && _capture.Sharp != null);
            if (frozen != _frozen) { _frozen = frozen; SetCaptureExclusion(!frozen); }
            if (!_frozen) Capture();
        }

        if (!visible) return;
        UpdateDrops(dt);
        UpdateConstants();
    }

    void Capture()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        Point origin;
        try { origin = PointToScreen(new Point(0, 0)); } catch (InvalidOperationException) { return; }

        int m = _capture.Margin;
        int w = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX) + m * 2;
        int h = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY) + m * 2;
        int x = (int)Math.Round(origin.X) - m, y = (int)Math.Round(origin.Y) - m;

        // 整块都在屏幕外（任务栏已滑出）就不截
        if (y >= GetSystemMetrics(77 /*SM_YVIRTUALSCREEN*/) + GetSystemMetrics(79 /*SM_CYVIRTUALSCREEN*/)) return;

        if (_capture.Capture(x, y, w, h, FrostRadius * dpi.DpiScaleX))
        {
            _sharpBrush.ImageSource = _capture.Sharp;
            _frostBrush.ImageSource = _capture.Frost;
        }
    }

    void UpdateDrops(double dt)
    {
        if (Rain <= 0.01 && !_drops.Any) return;
        var area = new Rect(ShadowPad + CornerRadius * 0.6, ShadowPad + 5,
                            Math.Max(0, ActualWidth - ShadowPad * 2 - CornerRadius * 1.2), Math.Max(0, ActualHeight - ShadowPad * 2 - 10));
        _drops.Step(dt, Rain, area);
        _drops.Apply(Glass);
    }

    void UpdateConstants()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double wpx = ActualWidth * dpi.DpiScaleX, hpx = ActualHeight * dpi.DpiScaleY;
        int m = _capture.Margin;
        double tw = wpx + m * 2, th = hpx + m * 2;

        Glass.Geometry = new Point4D(ActualWidth, ActualHeight, CornerRadius, Bevel);
        Glass.Mapping = new Point4D(wpx / tw, hpx / th, m / tw, m / th);
        Glass.Pixel = new Point4D(0, 0, ShadowPad, 1 / dpi.DpiScaleX);
        Glass.Optics = new Point4D(Refraction, Dispersion, FrostMix, Flash);
        Glass.Tone = new Point4D(Saturation, Brightness, HighlightCompression, ShadowPad > 0 ? ShadowOpacity : 0);
        Glass.Light = new Point4D(-0.45, -1, RimStrength, Veil);

        double lum = Math.Max(0, _capture.Luminance);
        double target = AdaptiveTint ? Math.Min(0.5, BaseTint + Math.Max(0, lum - 0.4) * 0.6) : BaseTint;
        _tintAmount = _tintAmount < 0 ? target : _tintAmount + (target - _tintAmount) * 0.08;
        Glass.Tint = new Point4D(TintColor.R / 255.0, TintColor.G / 255.0, TintColor.B / 255.0, _tintAmount);
    }

    /// <summary>背后画面的平均亮度 0..1（尚未截取时为 -1）。</summary>
    public double BackdropLuminance => _capture.Luminance;

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
}
