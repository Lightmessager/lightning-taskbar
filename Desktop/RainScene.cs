using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LightningTaskbar.Desktop;

/// <summary>桌面层的天气场景：壁纸 + 压暗 + 多层雨丝 + 溅射 + 闪电 + 底部雨雾，再叠上雪（SnowLayer）和龙卷风（TornadoLayer）。</summary>
internal sealed class RainScene : FrameworkElement
{
    readonly VisualCollection _children;
    readonly DrawingVisual _wallpaper = new();
    readonly DrawingVisual _dynamic = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly Random _rng = new();
    double _last;
    bool _running, _paused;

    ImageSource _wallImage;
    public Color WallpaperBottomColor { get; private set; } = Color.FromRgb(30, 34, 44);
    public event Action WallpaperChanged;

    // 雨滴（结构数组，减少 GC）
    struct RainDrop { public double X, Y, Z, EndY; }
    RainDrop[] _drops = new RainDrop[480];
    int _active;

    struct Splash { public double X, Y, T, Size; }
    readonly List<Splash> _splashes = new();

    // 闪电
    readonly List<PointCollection> _bolt = new();
    double _boltT = 99, _boltIntensity, _boltX;
    double[] _pulses = Array.Empty<double>();

    // 雪和龙卷风各自一层，画在雨之后
    readonly SnowLayer _snow = new();
    readonly TornadoLayer _tornado = new();
    /// <summary>调试用：跳过某些层（"snow" / "tornado"）。</summary>
    internal string SkipLayers { get; set; } = "";
    bool _layerErrorLogged;

    static readonly Color RainColor = Color.FromRgb(205, 225, 255);
    static readonly Pen[] LayerPens =
    {
        Frozen(new Pen(new SolidColorBrush(Color.FromArgb(46, 205, 225, 255)), 0.8)),
        Frozen(new Pen(new SolidColorBrush(Color.FromArgb(74, 205, 225, 255)), 1.1)),
        Frozen(new Pen(new SolidColorBrush(Color.FromArgb(110, 215, 232, 255)), 1.6)),
    };
    static readonly Brush FogBrush = FrozenB(new LinearGradientBrush(
        new GradientStopCollection { new(Color.FromArgb(0, 120, 135, 155), 0), new(Color.FromArgb(70, 120, 135, 155), 1) }, 90));

    public RainScene()
    {
        _children = new VisualCollection(this) { _wallpaper, _dynamic };
        for (int i = 0; i < _drops.Length; i++) _drops[i] = NewDrop(true);
        WeatherEngine.Instance.Lightning += OnLightning;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == Microsoft.Win32.UserPreferenceCategory.Desktop) Dispatcher.BeginInvoke(LoadWallpaper);
        };
        LoadWallpaper();
    }

    protected override int VisualChildrenCount => _children.Count;
    protected override Visual GetVisualChild(int index) => _children[index];

    protected override Size ArrangeOverride(Size finalSize)
    {
        DrawWallpaper(finalSize);
        return finalSize;
    }

    /// <summary>桌面被最大化/全屏窗口完全挡住时暂停绘制，省电省 GPU。</summary>
    public bool Paused
    {
        get => _paused;
        set { _paused = value; UpdateSubscription(); }
    }

    public void Start() { _running = true; UpdateSubscription(); }
    public void Stop() { _running = false; UpdateSubscription(); }

    bool _subscribed;
    void UpdateSubscription()
    {
        bool want = _running && !_paused;
        if (want == _subscribed) return;
        _subscribed = want;
        if (want) { _last = _clock.Elapsed.TotalSeconds; CompositionTarget.Rendering += OnFrame; }
        else CompositionTarget.Rendering -= OnFrame;
    }

    void LoadWallpaper()
    {
        _wallImage = null;
        try
        {
            var path = Native.GetWallpaperPath();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = Math.Max(1280, Native.GetPrimaryMonitorRect().Width);
                bmp.EndInit();
                bmp.Freeze();
                _wallImage = bmp;
                WallpaperBottomColor = SampleBottomColor(bmp);
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        DrawWallpaper(RenderSize);
        WallpaperChanged?.Invoke();
    }

    /// <summary>取壁纸底部条带的平均色，供任务栏染色，提高与背景的契合度。</summary>
    static Color SampleBottomColor(BitmapSource src)
    {
        var small = new TransformedBitmap(new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0),
            new ScaleTransform(64.0 / src.PixelWidth, 36.0 / src.PixelHeight));
        int w = small.PixelWidth, h = small.PixelHeight;
        var px = new byte[w * h * 4];
        small.CopyPixels(px, w * 4, 0);
        long r = 0, g = 0, b = 0, n = 0;
        for (int y = (int)(h * 0.8); y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                b += px[i]; g += px[i + 1]; r += px[i + 2]; n++;
            }
        return n == 0 ? Colors.Black : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    /// <summary>false = 透明模式（下面是动态壁纸），只画雨不画壁纸。</summary>
    public bool ShowWallpaper { get; set; } = true;

    void DrawWallpaper(Size size)
    {
        if (size.Width <= 0) return;
        using var dc = _wallpaper.RenderOpen();
        if (!ShowWallpaper) return;
        if (_wallImage is BitmapSource b)
        {
            // “填充”模式：等比缩放后裁剪居中
            double scale = Math.Max(size.Width / b.Width, size.Height / b.Height);
            double w = b.Width * scale, h = b.Height * scale;
            dc.DrawImage(b, new Rect((size.Width - w) / 2, (size.Height - h) / 2, w, h));
        }
        else
        {
            dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(24, 30, 44), Color.FromRgb(8, 10, 16), 90), null, new Rect(size));
        }
    }

    RainDrop NewDrop(bool anywhere)
    {
        double h = Math.Max(1, RenderSize.Height > 0 ? RenderSize.Height : 1080);
        double w = Math.Max(1, RenderSize.Width > 0 ? RenderSize.Width : 1920);
        double z = 0.25 + Math.Pow(_rng.NextDouble(), 1.6) * 0.75;
        return new RainDrop
        {
            X = _rng.NextDouble() * w * 1.3 - w * 0.15,
            Y = anywhere ? _rng.NextDouble() * h : -_rng.NextDouble() * h * 0.25 - 40,
            Z = z,
            EndY = h * (z > 0.8 ? 0.82 + _rng.NextDouble() * 0.2 : 0.45 + _rng.NextDouble() * 0.6),
        };
    }

    void OnLightning(LightningStrike s)
    {
        _boltT = 0;
        _boltIntensity = s.Intensity;
        _boltX = s.X;
        var rng = new Random(s.Seed);
        int n = 1 + rng.Next(3);
        _pulses = new double[n];
        for (int i = 0; i < n; i++) _pulses[i] = i == 0 ? 0 : _pulses[i - 1] + 0.05 + rng.NextDouble() * 0.12;
        BuildBolt(rng);
    }

    void BuildBolt(Random rng)
    {
        _bolt.Clear();
        double w = RenderSize.Width, h = RenderSize.Height;
        var start = new Point(_boltX * w, -20);
        var end = new Point(start.X + (rng.NextDouble() - 0.5) * w * 0.25, h * (0.55 + rng.NextDouble() * 0.35));
        AddBranch(rng, start, end, h * 0.09, 0);
    }

    void AddBranch(Random rng, Point a, Point b, double jag, int depth)
    {
        // 中点位移法生成锯齿闪电
        var pts = new List<Point> { a, b };
        double off = jag;
        for (int it = 0; it < 7; it++)
        {
            var next = new List<Point>(pts.Count * 2);
            for (int i = 0; i < pts.Count - 1; i++)
            {
                var p = pts[i]; var q = pts[i + 1];
                var mid = new Point((p.X + q.X) / 2 + (rng.NextDouble() - 0.5) * off, (p.Y + q.Y) / 2 + (rng.NextDouble() - 0.5) * off * 0.25);
                next.Add(p); next.Add(mid);
            }
            next.Add(pts[^1]);
            pts = next;
            off *= 0.55;
        }
        var pc = new PointCollection(pts);
        pc.Freeze();
        _bolt.Add(pc);

        if (depth >= 2) return;
        int branches = depth == 0 ? 2 + rng.Next(4) : rng.Next(2);
        for (int i = 0; i < branches; i++)
        {
            var from = pts[rng.Next(pts.Count / 6, pts.Count * 2 / 3)];
            double len = (b.Y - a.Y) * (0.15 + rng.NextDouble() * 0.3) / (depth + 1);
            var to = new Point(from.X + (rng.NextDouble() - 0.5) * len * 1.4, from.Y + len);
            AddBranch(rng, from, to, jag * 0.45, depth + 1);
        }
    }

    double BoltBrightness()
    {
        if (_boltT > 1.6) return 0;
        double b = 0;
        foreach (var p in _pulses)
            if (_boltT >= p) b = Math.Max(b, Math.Exp(-(_boltT - p) / 0.06));
        // 余辉
        b = Math.Max(b, 0.25 * Math.Exp(-_boltT / 0.5));
        return b * _boltIntensity;
    }

    void OnFrame(object sender, EventArgs e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(0.05, now - _last);
        _last = now;
        Step(dt);
    }

    /// <summary>推进一帧并重画（离线渲染检查画面时也直接调用它）。</summary>
    internal void Step(double dt)
    {

        var weather = WeatherEngine.Instance;
        double intensity = weather.Intensity;
        double w = RenderSize.Width, h = RenderSize.Height;
        if (w <= 0) return;
        double k = h / 1080.0;

        _boltT += dt;
        double flash = BoltBrightness();

        int target = (int)(intensity * _drops.Length);
        if (_active < target) _active = Math.Min(target, _active + (int)(dt * 120) + 1);
        else if (_active > target) _active = Math.Max(target, _active - (int)(dt * 600) - 1);

        var geos = new StreamGeometry[3];
        var ctxs = new StreamGeometryContext[3];
        for (int i = 0; i < 3; i++) { geos[i] = new StreamGeometry(); ctxs[i] = geos[i].Open(); }

        double wind = weather.Wind;
        for (int i = 0; i < _active; i++)
        {
            ref var d = ref _drops[i];
            double speed = (320 + 460 * d.Z) * k;   // 慢悠悠的小雨
            double vx = speed * wind;
            d.Y += speed * dt;
            d.X += vx * dt;
            if (d.Y > d.EndY)
            {
                if (d.Z > 0.72 && _rng.NextDouble() < 0.5 && _splashes.Count < 90)
                    _splashes.Add(new Splash { X = d.X, Y = d.EndY, T = 0, Size = (3 + 6 * d.Z) * k });
                d = NewDrop(false);
                continue;
            }
            double len = (9 + 20 * d.Z) * k * (0.7 + intensity * 0.3);
            int layer = d.Z < 0.5 ? 0 : d.Z < 0.8 ? 1 : 2;
            var c = ctxs[layer];
            c.BeginFigure(new Point(d.X, d.Y), false, false);
            c.LineTo(new Point(d.X - vx / speed * len, d.Y - len), true, false);
        }
        for (int i = 0; i < 3; i++) { ctxs[i].Close(); geos[i].Freeze(); }

        using var dc = _dynamic.RenderOpen();

        // 雨天压暗：让壁纸像被乌云遮住
        double dark = intensity * (weather.Mode == WeatherMode.Storm ? 0.42 : 0.28) * (1 - flash * 0.8);
        if (dark > 0.004)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(dark * 255), 8, 12, 24)), null, new Rect(0, 0, w, h));

        // 闪电照亮云层
        if (flash > 0.01)
        {
            var glow = new RadialGradientBrush(Color.FromArgb((byte)(flash * 150), 200, 215, 255), Color.FromArgb(0, 200, 215, 255))
            {
                Center = new Point(_boltX, 0.05), GradientOrigin = new Point(_boltX, 0.0), RadiusX = 0.7, RadiusY = 0.9,
            };
            dc.DrawRectangle(glow, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(flash * 70), 220, 230, 255)), null, new Rect(0, 0, w, h));
        }

        for (int i = 0; i < 3; i++) dc.DrawGeometry(null, LayerPens[i], geos[i]);

        // 溅射
        for (int i = _splashes.Count - 1; i >= 0; i--)
        {
            var s = _splashes[i];
            s.T += dt;
            if (s.T > 0.28) { _splashes.RemoveAt(i); continue; }
            _splashes[i] = s;
            double p = s.T / 0.28;
            double rx = s.Size * (0.4 + p * 1.4);
            var pen = SplashPen((int)((1 - p) * (SplashPens.Length - 1)), k);
            dc.DrawEllipse(null, pen, new Point(s.X, s.Y), rx, rx * 0.28);
            // 两颗飞溅的小水珠
            double up = Math.Sin(p * Math.PI) * s.Size * 1.6;
            var dot = pen.Brush;
            dc.DrawEllipse(dot, null, new Point(s.X - rx * 0.7, s.Y - up), 0.9 * k + 0.2, 0.9 * k + 0.2);
            dc.DrawEllipse(dot, null, new Point(s.X + rx * 0.8, s.Y - up * 0.8), 0.8 * k + 0.2, 0.8 * k + 0.2);
        }

        // 闪电本体：多层描边模拟辉光
        if (flash > 0.02 && _bolt.Count > 0)
        {
            var layers = new (double width, double alpha, Color color)[]
            {
                (22 * k, 0.07, Color.FromRgb(140, 170, 255)),
                (9 * k, 0.2, Color.FromRgb(170, 195, 255)),
                (3.2 * k, 0.65, Color.FromRgb(225, 235, 255)),
                (1.3 * k, 1.0, Colors.White),
            };
            for (int bi = 0; bi < _bolt.Count; bi++)
            {
                var geo = new StreamGeometry();
                using (var c = geo.Open())
                {
                    c.BeginFigure(_bolt[bi][0], false, false);
                    c.PolyLineTo(_bolt[bi], true, true);
                }
                geo.Freeze();
                double branchFade = bi == 0 ? 1 : 0.55;
                foreach (var (width, alpha, color) in layers)
                {
                    var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)(Math.Min(1, alpha * flash * branchFade * 1.4) * 255), color.R, color.G, color.B)), width * (bi == 0 ? 1 : 0.6))
                    { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                    dc.DrawGeometry(null, pen, geo);
                }
            }
        }

        // 底部雨雾
        if (intensity > 0.01)
        {
            dc.PushOpacity(intensity);
            dc.DrawRectangle(FogBrush, null, new Rect(0, h * 0.62, w, h * 0.38));
            dc.Pop();
        }

        // 新加的两层出错时只丢这一帧（记一次日志），不让异常冒到 Dispatcher 把任务栏带崩
        // 新加的两层出错时只丢这一帧（只记一次日志），不让异常冒到 Dispatcher 把任务栏带崩
        try
        {
            if (!SkipLayers.Contains("tornado")) _tornado.Render(dc, dt, w, h, k);
            if (!SkipLayers.Contains("snow")) _snow.Render(dc, dt, w, h, k);
        }
        catch (Exception ex) when (!FrameRender.Checking)
        {
            if (!_layerErrorLogged) { _layerErrorLogged = true; App.Log(ex); }
        }
    }

    // 溅射按透明度分档缓存，避免每帧分配几百个 Pen
    readonly Pen[] SplashPens = new Pen[16];
    double _splashK;
    Pen SplashPen(int level, double k)
    {
        if (k != _splashK) { Array.Clear(SplashPens); _splashK = k; }
        return SplashPens[level] ??= Frozen(new Pen(
            new SolidColorBrush(Color.FromArgb((byte)(level * 120 / (SplashPens.Length - 1)), RainColor.R, RainColor.G, RainColor.B)), 0.9 * k + 0.3));
    }

    static Pen Frozen(Pen p) { p.Freeze(); return p; }
    static Brush FrozenB(Brush b) { b.Freeze(); return b; }
}
