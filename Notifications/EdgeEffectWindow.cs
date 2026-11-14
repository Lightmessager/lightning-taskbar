using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using LightningTaskbar.Desktop;

namespace LightningTaskbar.Notifications;

/// <summary>
/// 来通知时屏幕四周的天气特效：盖住整个主屏、鼠标穿透、不抢焦点的透明顶层窗口，播完自动隐藏。
/// 晴 / 雨：一圈流动的紫蓝粉光；雷雨：流光里带闪电；下雪：一圈淡淡的霜雾再化开；龙卷风：几片淡淡的落叶飘进来再被吹走。
/// </summary>
internal sealed class EdgeEffectWindow : Window
{
    readonly EdgeCanvas _canvas = new();
    IntPtr _hwnd;

    public EdgeEffectWindow()
    {
        Title = "Lightning Edge Effect";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Content = _canvas;
        // 播完不 Hide：反复隐藏 / 显示整屏的分层窗口会让 WPF 反复释放、重建整屏渲染目标，实测会让渲染线程崩溃。
        // 改为清空内容后挪到屏幕外停着（不在屏幕上，也就不会被系统当成全屏程序）。
        _canvas.Finished += Park;
        _canvas.Finished += () => Finished?.Invoke();

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            // 鼠标穿透（WS_EX_TRANSPARENT + 分层）、不抢焦点、不进 Alt+Tab
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | 0x20 /*WS_EX_TRANSPARENT*/ | 0x80000 /*WS_EX_LAYERED*/
                | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        };
    }

    /// <summary>一次特效播完。</summary>
    public event Action Finished;

    uint _affinity = uint.MaxValue;
    int _placedW, _placedH;

    public void Play(EdgeStyle style)
    {
        if (_hwnd == IntPtr.Zero) new WindowInteropHelper(this).EnsureHandle();
        // 和通知卡片一致：没打开“允许截图和录屏”时，截图、录屏、共享屏幕里看不到它。只在取值变化时才改。
        uint affinity = Glass.GlassSurface.AllowCapture ? 0u : 0x11u /*WDA_EXCLUDEFROMCAPTURE*/;
        if (affinity != _affinity) { SetWindowDisplayAffinity(_hwnd, affinity); _affinity = affinity; }

        // 尽量少动窗口：尺寸只在第一次或屏幕分辨率变了时设置，之后每次只是从屏幕外挪回来。
        // 窗口的每次显示 / 隐藏 / 改尺寸都会让 WPF 同步重建分层窗口的渲染目标，频繁这样做会触发渲染线程失败。
        var mon = Native.GetPrimaryMonitorRect();
        uint flags = Native.SWP_NOACTIVATE;
        if (mon.Width == _placedW && mon.Height == _placedH) flags |= Native.SWP_NOSIZE;
        _placedW = mon.Width; _placedH = mon.Height;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, mon.Left, mon.Top, mon.Width, mon.Height, flags);
        if (!IsVisible)
        {
            Show();
            // 第一次显示时 WPF 可能按自己记的尺寸摆窗口，再对齐一次
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, mon.Left, mon.Top, mon.Width, mon.Height, Native.SWP_NOACTIVATE);
        }
        _canvas.Start(style);
    }

    void Park()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, -32000, -32000, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}

/// <summary>
/// 边缘特效的绘制。软层（辉光、雾气）+ 清晰层（线条、冰晶、叶子）。
/// 不用 BlurEffect：在全屏的分层透明窗口上，模糊要分配整屏大小的中间纹理，反复显示 / 隐藏时会让 WPF 渲染线程崩溃。
/// 柔和的光和雾全部用渐变直接画出来。
/// </summary>
internal sealed class EdgeCanvas : FrameworkElement
{
    readonly VisualCollection _children;
    readonly DrawingVisual _soft = new();
    readonly DrawingVisual _sharp = new();
    readonly Random _rng = new();

    EdgeStyle _style;
    double _t, _last = -1, _duration;
    bool _running;
    int _seed;

    public event Action Finished;

    public EdgeCanvas()
    {
        _children = new VisualCollection(this) { _soft, _sharp };
        // 光带由一段段矩形拼成：关掉抗锯齿，相邻矩形共用边界时既没有缝也不会重叠出亮线
        RenderOptions.SetEdgeMode(_soft, EdgeMode.Aliased);
        IsHitTestVisible = false;
    }

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

    public void Start(EdgeStyle style)
    {
        _style = style;
        _t = 0;
        _last = -1;
        _seed = _rng.Next();
        _frostPhase = null;
        _frostBlooms = null;
        _leaves = null;
        _bolts.Clear();
        _duration = style switch { EdgeStyle.Frost => 3.3, EdgeStyle.Leaves => 3.4, _ => 2.8 };
        if (!_running) { _running = true; CompositionTarget.Rendering += OnFrame; }
    }

    void Stop()
    {
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnFrame;
        using (_soft.RenderOpen()) { }
        using (_sharp.RenderOpen()) { }
        Finished?.Invoke();
    }

    void OnFrame(object sender, EventArgs e)
    {
        double now = ((RenderingEventArgs)e).RenderingTime.TotalSeconds;
        if (now == _last) return;
        double dt = _last < 0 ? 0 : Math.Min(0.05, now - _last);
        _last = now;
        try { Advance(dt); }
        catch (Exception ex) { App.Log(ex); Stop(); }
    }

    /// <summary>推进 dt 秒并重画（离线渲染时直接调用）。</summary>
    internal void Advance(double dt)
    {
        _t += dt;
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        if (_t >= _duration) { Stop(); return; }
        double k = Math.Max(0.6, Math.Min(w, h) / 1080.0);

        switch (_style)
        {
            case EdgeStyle.Glow: DrawGlow(w, h, k, false); break;
            case EdgeStyle.StormGlow: DrawGlow(w, h, k, true); break;
            case EdgeStyle.Frost: DrawFrost(w, h, k); break;
            case EdgeStyle.Leaves: DrawLeaves(w, h, k); break;
        }
    }

    static double Smooth(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - 2 * t);
    }

    static double EaseOutCubic(double p) { p = Math.Clamp(p, 0, 1); return 1 - Math.Pow(1 - p, 3); }

    // ---------------- 周长参数化：s ∈ [0,1) 顺时针绕屏幕一圈 ----------------

    /// <summary>屏幕边上的点和指向屏幕内部的法线。</summary>
    static (Point p, Vector n) Perimeter(double s, double w, double h)
    {
        double L = 2 * (w + h);
        double d = (s - Math.Floor(s)) * L;
        if (d < w) return (new Point(d, 0), new Vector(0, 1));
        d -= w;
        if (d < h) return (new Point(w, d), new Vector(-1, 0));
        d -= h;
        if (d < w) return (new Point(w - d, h), new Vector(0, -1));
        d -= w;
        return (new Point(0, h - d), new Vector(1, 0));
    }

    // ---------------- 晴 / 雨 / 雷雨：流动的紫蓝粉光 ----------------

    const int Hues = 72, Levels = 16, GlowLayers = 3;
    static readonly Pen[,,] GlowPens = new Pen[2, GlowLayers, Hues * Levels];
    double _glowK;

    // 紫 → 蓝 → 浅蓝 → 粉 → 品红 → 紫（雷雨版更偏蓝紫）
    static readonly Color[] Palette =
    {
        Color.FromRgb(150, 90, 255), Color.FromRgb(70, 128, 255), Color.FromRgb(110, 196, 255),
        Color.FromRgb(255, 110, 196), Color.FromRgb(214, 92, 246), Color.FromRgb(150, 90, 255),
    };
    static readonly Color[] StormPalette =
    {
        Color.FromRgb(120, 100, 255), Color.FromRgb(70, 120, 255), Color.FromRgb(160, 200, 255),
        Color.FromRgb(200, 120, 255), Color.FromRgb(240, 120, 220), Color.FromRgb(120, 100, 255),
    };

    static Color PaletteAt(Color[] pal, double u)
    {
        u = (u - Math.Floor(u)) * (pal.Length - 1);
        int i = Math.Min(pal.Length - 2, (int)u);
        double f = u - i;
        var a = pal[i]; var b = pal[i + 1];
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));
    }

    Pen GlowPen(bool storm, int layer, int hue, int level, double k)
    {
        if (Math.Abs(k - _glowK) > 0.001) { Array.Clear(GlowPens); _glowK = k; }
        int pi = storm ? 1 : 0;
        ref var pen = ref GlowPens[pi, layer, hue * Levels + level];
        if (pen != null) return pen;
        var c = PaletteAt(storm ? StormPalette : Palette, (double)hue / Hues);
        double[] width = { 110, 40, 4.5 };
        double[] alpha = { 0.55, 0.8, 0.95 };
        double a = alpha[layer] * (0.35 + 0.65 * level / (Levels - 1.0));
        pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B)), width[layer] * k)
        { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    /// <summary>0 上、1 右、2 下、3 左（按指向屏幕内部的法线）。</summary>
    static int SideOf(Vector n) => n.Y > 0.5 ? 0 : n.X < -0.5 ? 1 : n.Y < -0.5 ? 2 : 3;

    /// <summary>从 a→b 这一段屏幕边向内延伸 depth 的矩形。</summary>
    static Rect Band(Point a, Point b, int side, double depth, double w, double h) => side switch
    {
        0 => new Rect(Math.Min(a.X, b.X), 0, Math.Abs(b.X - a.X), depth),
        1 => new Rect(w - depth, Math.Min(a.Y, b.Y), depth, Math.Abs(b.Y - a.Y)),
        2 => new Rect(Math.Min(a.X, b.X), h - depth, Math.Abs(b.X - a.X), depth),
        _ => new Rect(0, Math.Min(a.Y, b.Y), depth, Math.Abs(b.Y - a.Y)),
    };

    static readonly Brush[,,] GlowBrushes = new Brush[2, 4, Hues * Levels];

    /// <summary>一段光带的画刷：边上是颜色，向内按近似高斯的曲线淡到透明。</summary>
    static Brush GlowBrush(bool storm, int side, int hue, int level)
    {
        ref var brush = ref GlowBrushes[storm ? 1 : 0, side, hue * Levels + level];
        if (brush != null) return brush;
        var c = PaletteAt(storm ? StormPalette : Palette, (double)hue / Hues);
        double a = 0.62 * (0.35 + 0.65 * level / (Levels - 1.0));
        Color A(double f) => Color.FromArgb((byte)(a * f * 255), c.R, c.G, c.B);
        var stops = new GradientStopCollection { new(A(1), 0), new(A(0.62), 0.22), new(A(0.28), 0.48), new(A(0.08), 0.75), new(A(0), 1) };
        var (sp, ep) = side switch
        {
            0 => (new Point(0, 0), new Point(0, 1)),
            1 => (new Point(1, 0), new Point(0, 0)),
            2 => (new Point(0, 1), new Point(0, 0)),
            _ => (new Point(0, 0), new Point(1, 0)),
        };
        brush = new LinearGradientBrush(stops, sp, ep);
        brush.Freeze();
        return brush;
    }

    /// <summary>屏幕四边各一条从边缘向内渐隐的色带。</summary>
    static void DrawEdgeFade(DrawingContext dc, double w, double h, double depth, Color c)
    {
        var clear = Color.FromArgb(0, c.R, c.G, c.B);
        dc.DrawRectangle(new LinearGradientBrush(c, clear, 90), null, new Rect(0, 0, w, depth));
        dc.DrawRectangle(new LinearGradientBrush(clear, c, 90), null, new Rect(0, h - depth, w, depth));
        dc.DrawRectangle(new LinearGradientBrush(c, clear, 0), null, new Rect(0, 0, depth, h));
        dc.DrawRectangle(new LinearGradientBrush(clear, c, 0), null, new Rect(w - depth, 0, depth, h));
    }

    struct Bolt { public PointCollection Points; public double At; }
    readonly List<Bolt> _bolts = new();
    static readonly double[] StormFlashes = { 0.42, 0.52, 1.35, 1.43, 1.9 };

    void DrawGlow(double w, double h, double k, bool storm)
    {
        double env = Smooth(0, 0.4, _t) * (1 - Smooth(_duration - 0.9, _duration, _t));
        const int N = 220;
        double flow = _t * 0.22;

        using (var dc = _soft.RenderOpen())
        using (var sc = _sharp.RenderOpen())
        {
            for (int i = 0; i < N; i++)
            {
                double s0 = (double)i / N, s1 = (double)(i + 1) / N;
                var (a, n) = Perimeter(s0, w, h);
                var (b, _) = Perimeter(s1, w, h);
                // 两圈色彩在边上流动；亮度再叠一个反向流动的波，像光在呼吸
                int hue = (int)(((s0 * 2 + flow) % 1 + 1) % 1 * Hues) % Hues;
                double wave = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (s0 * 3 - _t * 0.55));
                int level = Math.Clamp((int)(wave * (Levels - 1) + 0.5), 0, Levels - 1);
                // 从边上向内渐隐的两层光带：宽而淡的一层 + 窄而亮的一层
                int side = SideOf(n);
                dc.DrawRectangle(GlowBrush(storm, side, hue, level), null, Band(a, b, side, 120 * k, w, h));
                dc.DrawRectangle(GlowBrush(storm, side, hue, Math.Min(Levels - 1, level + 1)), null, Band(a, b, side, 38 * k, w, h));
                sc.DrawLine(GlowPen(storm, 2, hue, Math.Min(Levels - 1, level + 2), k), a, b);
            }

            if (storm) DrawStormFlashes(dc, sc, w, h, k);
        }
        _soft.Opacity = env;
        _sharp.Opacity = env;
    }

    /// <summary>雷雨版：几次闪光，边上蹿出短促的分叉电弧，整圈被照白一下。</summary>
    void DrawStormFlashes(DrawingContext dc, DrawingContext sc, double w, double h, double k)
    {
        var rng = new Random(_seed);
        double flash = 0;
        for (int fi = 0; fi < StormFlashes.Length; fi++)
        {
            double at = StormFlashes[fi];
            if (_t >= at) flash = Math.Max(flash, Math.Exp(-(_t - at) / 0.07) * (fi == 1 || fi == 3 ? 0.7 : 1));
            if (_t >= at && !_bolts.Any(b => b.At == at))
            {
                // 每次闪光在随机的边上生两三道电弧
                var brng = new Random(_seed + fi * 977);
                for (int j = 0; j < 2 + brng.Next(2); j++) _bolts.Add(new Bolt { Points = EdgeBolt(brng, w, h, k), At = at });
            }
        }
        if (flash > 0.01)
        {
            DrawEdgeFade(dc, w, h, 90 * k, Color.FromArgb((byte)(flash * 210), 235, 235, 255));
            var core = new Pen(new SolidColorBrush(Color.FromArgb((byte)(flash * 255), 255, 255, 255)), 3 * k);
            sc.DrawRectangle(null, core, new Rect(0, 0, w, h));
        }
        foreach (var b in _bolts)
        {
            double age = _t - b.At;
            double a = age < 0 ? 0 : Math.Exp(-age / 0.12);
            if (a < 0.02) continue;
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(b.Points[0], false, false);
                c.PolyLineTo(b.Points, true, true);
            }
            geo.Freeze();
            sc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(a * 110), 180, 170, 255)), 12 * k) { LineJoin = PenLineJoin.Round }, geo);
            sc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb((byte)(a * 255), 250, 248, 255)), 1.8 * k)
            { LineJoin = PenLineJoin.Round }, geo);
        }
    }

    static PointCollection EdgeBolt(Random rng, double w, double h, double k)
    {
        var (p, n) = Perimeter(rng.NextDouble(), w, h);
        var tangent = new Vector(-n.Y, n.X);
        var pts = new PointCollection();
        var cur = p;
        double len = (90 + rng.NextDouble() * 140) * k;
        int steps = 12;
        for (int i = 0; i <= steps; i++)
        {
            pts.Add(cur);
            double f = (double)i / steps;
            // 先往里冲，再贴着边拐走
            var dir = n * (1 - f * 0.7) + tangent * (rng.NextDouble() - 0.5) * 1.6;
            dir.Normalize();
            cur += dir * (len / steps);
        }
        pts.Freeze();
        return pts;
    }

    // ---------------- 下雪：淡淡的一圈霜雾 ----------------
    // 只有柔和的冰蓝白色雾气从四边向内晕开，边缘有缓慢起伏，没有任何细密重复的纹路。

    const double FrostGrow = 1.2, MeltStart = 2.0;
    double[] _frostPhase;
    (double s, double size)[] _frostBlooms;
    static readonly Brush FrostBloom = MakeFrostBloom();

    static Brush MakeFrostBloom()
    {
        var b = new RadialGradientBrush(Color.FromArgb(70, 236, 244, 255), Color.FromArgb(0, 236, 244, 255));
        b.Freeze();
        return b;
    }
    static readonly Brush[] FrostBrushes = new Brush[4];

    static Brush FrostBrush(int side)
    {
        if (FrostBrushes[side] != null) return FrostBrushes[side];
        Color A(double f) => Color.FromArgb((byte)(f * 255), 232, 242, 255);
        var stops = new GradientStopCollection { new(A(0.42), 0), new(A(0.26), 0.25), new(A(0.1), 0.55), new(A(0.025), 0.8), new(A(0), 1) };
        var (sp, ep) = side switch
        {
            0 => (new Point(0, 0), new Point(0, 1)),
            1 => (new Point(1, 0), new Point(0, 0)),
            2 => (new Point(0, 1), new Point(0, 0)),
            _ => (new Point(0, 0), new Point(1, 0)),
        };
        var brush = new LinearGradientBrush(stops, sp, ep);
        brush.Freeze();
        return FrostBrushes[side] = brush;
    }

    void DrawFrost(double w, double h, double k)
    {
        if (_frostPhase == null)
        {
            var rng = new Random(_seed);
            _frostPhase = new[] { rng.NextDouble() * 6.28, rng.NextDouble() * 6.28, rng.NextDouble() * 6.28 };
            // 少量大霜晕：四个角各一团，边上再随机几团
            var blooms = new List<(double, double)>();
            double L = 2 * (w + h);
            foreach (var corner in new[] { 0.0, w / L, (w + h) / L, (2 * w + h) / L }) blooms.Add((corner, 200 + rng.NextDouble() * 80));
            for (int i = 0; i < 6; i++) blooms.Add((rng.NextDouble(), 120 + rng.NextDouble() * 120));
            _frostBlooms = blooms.ToArray();
        }
        double grow = EaseOutCubic(_t / FrostGrow);
        double melt = Smooth(MeltStart, _duration, _t);
        // 雾的深度：慢慢蔓延进来，化的时候退回边上
        double depth = 150 * k * grow * (1 - 0.4 * melt);

        using (var dc = _soft.RenderOpen())
        {
            if (depth > 1)
            {
                // 四条完整的渐变带（没有拼接缝），四角自然叠得更厚
                dc.DrawRectangle(FrostBrush(0), null, new Rect(0, 0, w, depth));
                dc.DrawRectangle(FrostBrush(2), null, new Rect(0, h - depth, w, depth));
                dc.DrawRectangle(FrostBrush(3), null, new Rect(0, 0, depth, h));
                dc.DrawRectangle(FrostBrush(1), null, new Rect(w - depth, 0, depth, h));
                // 几团大而柔的霜晕贴在边上，让霜的厚薄有起伏
                for (int i = 0; i < _frostBlooms.Length; i++)
                {
                    var (s0, size) = _frostBlooms[i];
                    var (p, _) = Perimeter(s0 + Math.Sin(_t * 0.3 + i) * 0.003, w, h);
                    double r = size * k * (depth / (150 * k));
                    dc.DrawEllipse(FrostBloom, null, p, r * 1.6, r);
                }
            }
        }
        using (_sharp.RenderOpen()) { }

        double appear = Smooth(0, 0.3, _t);
        _soft.Opacity = appear * (1 - melt);
    }

    // ---------------- 龙卷风：落叶 ----------------

    struct EdgeLeaf
    {
        public Point Start, Target;
        public double Arrive, Fly, Size, Angle, SpinIn, Flip, FlipTurns, Flutter;
        public double Blow, BlowVx, BlowVy, BlowSpin;
        public int Shape, Color;
    }
    EdgeLeaf[] _leaves;

    void BuildLeaves(double w, double h, double k)
    {
        var rng = new Random(_seed);
        double L = 2 * (w + h);
        int n = (int)(L / (30 * k));   // 稀疏：只是边上淡淡的一层底纹
        _leaves = new EdgeLeaf[n];
        for (int i = 0; i < n; i++)
        {
            double s = rng.NextDouble();
            var (p, nrm) = Perimeter(s, w, h);
            var tan = new Vector(-nrm.Y, nrm.X);
            double size = (26 + rng.NextDouble() * 18) * k;
            double inward = size * 0.2 + Math.Pow(rng.NextDouble(), 1.5) * 90 * k;
            var target = p + nrm * inward + tan * (rng.NextDouble() - 0.5) * 20 * k;
            // 从屏幕外、偏左上方（上风方向）飘进来
            var start = p - nrm * (size + 60 * k + rng.NextDouble() * 160 * k) + new Vector(-220 * k, -90 * k) * rng.NextDouble();
            double flipTarget = (rng.NextDouble() < 0.5 ? 1 : -1) * Math.Acos(0.75 + rng.NextDouble() * 0.25);
            _leaves[i] = new EdgeLeaf
            {
                Start = start,
                Target = target,
                Fly = 0.55 + rng.NextDouble() * 0.4,
                Arrive = 0.25 + rng.NextDouble() * 0.75 + target.X / w * 0.3,
                Size = size,
                Angle = rng.NextDouble() * 360,
                SpinIn = (rng.NextDouble() < 0.5 ? -1 : 1) * (180 + rng.NextDouble() * 360),
                Flip = flipTarget,
                FlipTurns = 1 + rng.Next(3),
                Flutter = rng.NextDouble() * Math.PI * 2,
                // 一阵风从左往右扫过，把叶子吹走
                Blow = 2.15 + target.X / w * 0.45 + rng.NextDouble() * 0.15,
                BlowVx = (500 + rng.NextDouble() * 700) * k,
                BlowVy = -(60 + rng.NextDouble() * 200) * k,
                BlowSpin = (rng.NextDouble() < 0.5 ? -1 : 1) * (300 + rng.NextDouble() * 500),
                Shape = rng.Next(LeafArt.Shapes.Length),
                Color = rng.Next(LeafArt.Fills.Length),
            };
        }
        // 先到的画在下面，后到的盖在上面
        Array.Sort(_leaves, (a, b) => a.Arrive.CompareTo(b.Arrive));
    }

    void DrawLeaves(double w, double h, double k)
    {
        if (_leaves == null) BuildLeaves(w, h, k);
        using (var dc = _soft.RenderOpen())
        {
            // 叶子底下一圈淡淡的暗影，让叶堆有厚度
        }
        _soft.Opacity = 1;

        using var sc = _sharp.RenderOpen();
        for (int i = 0; i < _leaves.Length; i++)
        {
            ref var l = ref _leaves[i];
            double t0 = l.Arrive - l.Fly;
            if (_t < t0) continue;
            Point pos;
            double angle, flip;
            if (_t < l.Arrive)
            {
                double p = (_t - t0) / l.Fly;
                double e = EaseOutCubic(p);
                var dir = l.Target - l.Start;
                var perp = new Vector(-dir.Y, dir.X);
                if (perp.Length > 0) perp.Normalize();
                pos = l.Start + dir * e + perp * Math.Sin(p * Math.PI * 2 + l.Flutter) * (1 - p) * 28 * k;
                angle = l.Angle + (1 - e) * l.SpinIn;
                flip = Math.Cos(l.Flip + (1 - e) * Math.PI * 2 * l.FlipTurns);
            }
            else if (_t < l.Blow)
            {
                pos = l.Target;
                angle = l.Angle + Math.Sin(_t * 3 + l.Flutter) * 3;
                flip = Math.Cos(l.Flip);
            }
            else
            {
                double tau = _t - l.Blow;
                pos = l.Target + new Vector(l.BlowVx * tau + 0.5 * 2200 * k * tau * tau, l.BlowVy * tau + 0.5 * 150 * k * tau * tau);
                angle = l.Angle + l.BlowSpin * tau;
                flip = Math.Cos(l.Flip + tau * 9);
                if (pos.X > w + l.Size || pos.Y < -l.Size) continue;
            }
            LeafArt.Draw(sc, l.Shape, l.Color, pos, l.Size, angle, flip);
        }
        // 整体半透明：落叶只是淡淡的底纹，不抢眼
        _sharp.Opacity = 0.32 * (1 - Smooth(_duration - 0.3, _duration, _t));
    }
}
