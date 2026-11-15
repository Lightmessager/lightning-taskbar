using System.Windows;
using System.Windows.Media;

namespace LightningTaskbar.Desktop;

/// <summary>
/// 桌面雪景。刻意做得稀疏、柔和（满屏均匀的小硬点会让人不适）：
/// 近处只有少量大的六角雪花，半透明、边缘带柔光、慢慢翻转着飘；
/// 中景是柔边的绒雪；远处是极淡的细雪。暴雪不拉斜线，而是靠白茫茫的雪幕和随风掠过的雪雾表现。
/// 底部会慢慢堆起积雪，停雪后慢慢化掉。
/// </summary>
internal sealed class SnowLayer
{
    enum Kind { Faint, Fluffy, Crystal }

    struct Flake
    {
        public double X, Y, Z, Size, Phase, SwayAmp, SwayFreq, Angle, Spin, Tilt, TiltSpeed;
        public Kind Kind;
        public int Shape, Level;
    }

    const int MaxFlakes = 260;
    const int Levels = 4;

    readonly Random _rng = new(4242);
    readonly Flake[] _flakes = new Flake[MaxFlakes];
    int _active;
    double _time, _accum;
    bool _seeded;

    // 积雪的起伏轮廓
    readonly double[] _driftFreq = new double[5], _driftPhase = new double[5], _driftAmp = new double[5];

    // 雪雾团：几团大的柔光椭圆随风飘过
    struct Haze { public double X, Y, R, Speed; }
    readonly Haze[] _haze = new Haze[7];

    static readonly Brush SnowBank = Frozen(new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromArgb(235, 248, 250, 255), 0),
        new(Color.FromArgb(225, 226, 234, 246), 0.35),
        new(Color.FromArgb(215, 196, 210, 230), 1),
    }, 90));
    static readonly Brush HazeBrush = Frozen(new RadialGradientBrush(Color.FromArgb(140, 236, 242, 250), Color.FromArgb(0, 236, 242, 250)));
    static readonly Brush GroundMist = Frozen(new LinearGradientBrush(
        new GradientStopCollection { new(Color.FromArgb(0, 230, 236, 245), 0), new(Color.FromArgb(150, 230, 236, 245), 1) }, 90));

    /// <summary>柔边雪团：中心亮、向外渐隐，没有硬边。按透明度分几档。</summary>
    static readonly Brush[] SoftFlake = Enumerable.Range(0, Levels).Select(i =>
    {
        byte a = (byte)(90 + i * 45);
        return Frozen(new RadialGradientBrush(new GradientStopCollection
        {
            new(Color.FromArgb(a, 255, 255, 255), 0),
            new(Color.FromArgb((byte)(a * 0.7), 248, 251, 255), 0.35),
            new(Color.FromArgb((byte)(a * 0.25), 240, 246, 255), 0.7),
            new(Color.FromArgb(0, 240, 246, 255), 1),
        }));
    }).ToArray();

    /// <summary>六角雪花的柔光底。</summary>
    static readonly Brush CrystalHalo = Frozen(new RadialGradientBrush(Color.FromArgb(40, 235, 244, 255), Color.FromArgb(0, 235, 244, 255)));

    /// <summary>两种六角雪花（单位尺寸，半径 0.5）：枝状和星盘状。</summary>
    static readonly Geometry[] Crystals = { BuildCrystal(dendrite: true), BuildCrystal(dendrite: false) };
    static readonly Pen CrystalPen = FrozenPen(new Pen(new SolidColorBrush(Color.FromArgb(150, 250, 252, 255)), 0.045)
    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });

    static Geometry BuildCrystal(bool dendrite)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            for (int i = 0; i < 6; i++)
            {
                double a = i * Math.PI / 3;
                var dir = new Vector(Math.Cos(a), Math.Sin(a));
                var perp = new Vector(-dir.Y, dir.X);
                c.BeginFigure(new Point(0, 0), false, false);
                c.LineTo((Point)(dir * 0.5), true, false);
                // 每根主枝上两对侧枝，向外 60°
                foreach (double t in dendrite ? new[] { 0.22, 0.36 } : new[] { 0.3 })
                {
                    double len = dendrite ? (t < 0.3 ? 0.13 : 0.09) : 0.08;
                    var p = (Point)(dir * t);
                    c.BeginFigure(p, false, false);
                    c.LineTo(p + (dir * 0.5 + perp * 0.866) * len, true, false);
                    c.BeginFigure(p, false, false);
                    c.LineTo(p + (dir * 0.5 - perp * 0.866) * len, true, false);
                }
            }
            if (!dendrite)
            {
                // 星盘：中间一个小六边形
                for (int i = 0; i <= 6; i++)
                {
                    double a = i * Math.PI / 3 + Math.PI / 6;
                    var p = new Point(Math.Cos(a) * 0.14, Math.Sin(a) * 0.14);
                    if (i == 0) c.BeginFigure(p, false, true); else c.LineTo(p, true, true);
                }
            }
        }
        g.Freeze();
        return g;
    }

    public SnowLayer()
    {
        for (int i = 0; i < 5; i++)
        {
            _driftFreq[i] = (i + 1) * (0.8 + _rng.NextDouble() * 0.9);
            _driftPhase[i] = _rng.NextDouble() * Math.PI * 2;
            _driftAmp[i] = 0.35 / (i + 1) * (0.6 + _rng.NextDouble() * 0.8);
        }
    }

    Flake NewFlake(double w, double h, bool anywhere, double windPx)
    {
        double r = _rng.NextDouble();
        // 远处细雪 40%、中景绒雪 48%、近处六角雪花 12%
        var kind = r < 0.4 ? Kind.Faint : r < 0.88 ? Kind.Fluffy : Kind.Crystal;
        double z = kind switch
        {
            Kind.Faint => 0.1 + _rng.NextDouble() * 0.25,
            Kind.Fluffy => 0.4 + _rng.NextDouble() * 0.35,
            _ => 0.8 + _rng.NextDouble() * 0.2,
        };
        var f = new Flake
        {
            Kind = kind,
            Z = z,
            Size = kind switch
            {
                Kind.Faint => 2.2 + _rng.NextDouble() * 1.6,     // 半径
                Kind.Fluffy => 4 + _rng.NextDouble() * 5,
                _ => 30 + _rng.NextDouble() * 22,                // 直径
            },
            Phase = _rng.NextDouble() * Math.PI * 2,
            SwayAmp = 0.5 + _rng.NextDouble() * 1.0,
            SwayFreq = 0.35 + _rng.NextDouble() * 0.8,
            Angle = _rng.NextDouble() * 60,
            Spin = (_rng.NextDouble() * 2 - 1) * 25,
            Tilt = _rng.NextDouble() * Math.PI * 2,
            TiltSpeed = 0.4 + _rng.NextDouble() * 0.9,
            Shape = _rng.Next(Crystals.Length),
            Level = kind == Kind.Faint ? 0 : 1 + _rng.Next(Levels - 1),
        };
        if (anywhere)
        {
            f.X = _rng.NextDouble() * w;
            f.Y = _rng.NextDouble() * h;
        }
        else if (Math.Abs(windPx) > 120 && _rng.NextDouble() < 0.6)
        {
            // 横风大时一部分从上风方向的侧边进来，否则屏幕一侧会空着
            f.X = windPx > 0 ? -40 : w + 40;
            f.Y = _rng.NextDouble() * h * 0.85;
        }
        else
        {
            f.X = _rng.NextDouble() * (w + 200) - 100;
            f.Y = -40 - _rng.NextDouble() * 60;
        }
        return f;
    }

    public void Render(DrawingContext dc, double dt, double w, double h, double k)
    {
        var weather = WeatherEngine.Instance;
        double snow = weather.Snow, blizzard = weather.Blizzard;
        _time += dt;

        // 积雪：下雪时慢慢堆起来（上限随雪量），停了以后慢慢化掉
        double accumTarget = snow * (0.45 + 0.55 * blizzard);
        if (snow > 0.01 && _accum < accumTarget) _accum = Math.Min(accumTarget, _accum + dt * snow * 0.012);
        else if (_accum > accumTarget) _accum = Math.Max(accumTarget, _accum - dt * 0.006);

        int target = (int)(Math.Pow(snow, 1.2) * MaxFlakes);
        double wind = weather.Wind, gust = weather.Gust;
        // 横向风速（px/s）。暴雪的风大，但雪花依然是一片片柔和的雪，不拉成线
        double windPx = (wind * (40 + 300 * blizzard) + Math.Sign(wind) * gust * 220 * blizzard) * k;

        if (!_seeded && target > 0)
        {
            // 第一次（比如启动时就是雪天）直接铺满全屏，不要等雪从顶上慢慢落下来
            for (int i = 0; i < MaxFlakes; i++) _flakes[i] = NewFlake(w, h, true, windPx);
            _seeded = true;
        }
        if (_active < target) _active = Math.Min(target, _active + (int)(dt * 40) + 1);
        else if (_active > target) _active = Math.Max(target, _active - (int)(dt * 80) - 1);

        if (snow <= 0.001 && _accum <= 0.001 && _active == 0) return;

        // ---- 天色：雪天微微发灰发冷，暴雪白茫茫 ----
        double overcast = snow * 0.12;
        if (overcast > 0.004)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(overcast * 255), 150, 162, 182)), null, new Rect(0, 0, w, h));
        double white = blizzard * (0.24 + 0.12 * gust);
        if (white > 0.004)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(white * 255), 228, 234, 243)), null, new Rect(0, 0, w, h));

        // ---- 雪雾团（暴雪）：降低能见度，随风大片掠过 ----
        if (blizzard > 0.01)
        {
            for (int i = 0; i < _haze.Length; i++)
            {
                ref var hz = ref _haze[i];
                if (hz.R == 0 || hz.X - hz.R * 1.8 > w + 50 || hz.X + hz.R * 1.8 < -50)
                {
                    bool initial = hz.R == 0;
                    hz.R = (280 + _rng.NextDouble() * 420) * k;
                    hz.Y = h * (0.1 + _rng.NextDouble() * 0.85);
                    hz.Speed = 0.8 + _rng.NextDouble() * 0.8;
                    hz.X = initial ? _rng.NextDouble() * w : windPx >= 0 ? -hz.R * 1.8 : w + hz.R * 1.8;
                }
                hz.X += (windPx * 1.4 + Math.Sign(windPx) * 60 * k) * hz.Speed * dt;
                double a = blizzard * (0.5 + 0.3 * Math.Sin(_time * 0.4 + i * 1.7)) * (0.75 + 0.5 * gust);
                dc.PushOpacity(Math.Clamp(a, 0, 1));
                dc.DrawEllipse(HazeBrush, null, new Point(hz.X, hz.Y), hz.R * 1.8, hz.R * 0.6);
                dc.Pop();
            }
        }

        // ---- 雪花 ----
        double fallBoost = 1 + blizzard * 0.6;
        for (int i = 0; i < _active; i++)
        {
            ref var f = ref _flakes[i];
            double depth = 0.3 + 0.7 * f.Z;                    // 近处看起来更快
            double vy = (26 + 70 * f.Z) * k * fallBoost;
            double sway = Math.Cos(_time * f.SwayFreq + f.Phase) * f.SwayAmp * 26 * k * (1 - blizzard * 0.5);
            double vx = windPx * depth + sway;
            f.X += vx * dt;
            f.Y += vy * dt;
            f.Angle += f.Spin * dt;
            f.Tilt += f.TiltSpeed * dt;

            double margin = f.Kind == Kind.Crystal ? f.Size * k : 20;
            if (f.Y > h + margin || f.X < -200 || f.X > w + 200)
            {
                f = NewFlake(w, h, false, windPx);
                continue;
            }
            if (FrameRender.Checking && !(double.IsFinite(f.X) && double.IsFinite(f.Y)))
                throw new InvalidOperationException($"snow NaN x={f.X} y={f.Y}");

            var p = new Point(f.X, f.Y);
            switch (f.Kind)
            {
                case Kind.Faint:
                case Kind.Fluffy:
                {
                    double r = f.Size * Math.Max(0.6, k);
                    dc.DrawEllipse(SoftFlake[f.Level], null, p, r, r);
                    break;
                }
                case Kind.Crystal:
                {
                    double size = f.Size * Math.Max(0.6, k);
                    dc.DrawEllipse(CrystalHalo, null, p, size * 0.9, size * 0.9);
                    // 翻转：一个方向按 cos 压扁，看起来像在空中慢慢翻面
                    double squash = 0.55 + 0.45 * Math.Abs(Math.Cos(f.Tilt));
                    var m = Matrix.Identity;
                    m.Scale(size, size * squash);
                    m.Rotate(f.Angle);
                    m.Translate(p.X, p.Y);
                    dc.PushTransform(new MatrixTransform(m));
                    dc.DrawGeometry(null, CrystalPen, Crystals[f.Shape]);
                    dc.Pop();
                    break;
                }
            }
        }

        // ---- 暴雪的地面吹雪 ----
        if (blizzard > 0.01)
        {
            dc.PushOpacity(blizzard * (0.6 + 0.4 * gust));
            dc.DrawRectangle(GroundMist, null, new Rect(0, h * 0.55, w, h * 0.45));
            dc.Pop();
        }

        // ---- 积雪 ----
        if (_accum > 0) DrawSnowBank(dc, w, h, k);
    }

    void DrawSnowBank(DrawingContext dc, double w, double h, double k)
    {
        // 积雪很薄时不要画出零点几像素高的多边形（WPF 原生曲面细分对近乎退化的几何很脆弱），
        // 保持至少几像素高，用透明度淡入淡出
        double realH = 70 * k * _accum;
        double maxH = Math.Max(realH, 6 * k);
        double fade = Math.Clamp(realH / (6 * k), 0, 1);
        if (fade < 0.02) return;
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(new Point(0, h + 2), true, true);
            const int steps = 96;
            var pts = new List<Point>(steps + 1);
            for (int i = 0; i <= steps; i++)
            {
                double x = w * i / steps;
                double u = (double)i / steps * Math.PI * 2;
                double p = 0.62;
                for (int j = 0; j < 5; j++) p += _driftAmp[j] * Math.Sin(u * _driftFreq[j] + _driftPhase[j]);
                // 两边角落堆得更厚
                double corner = Math.Pow(Math.Abs((double)i / steps - 0.5) * 2, 3) * 0.6;
                pts.Add(new Point(x, h - maxH * Math.Max(0.15, p + corner)));
            }
            c.PolyLineTo(pts, true, true);
            c.LineTo(new Point(w, h + 2), true, false);
        }
        geo.Freeze();
        dc.PushOpacity(fade);
        dc.DrawGeometry(SnowBank, null, geo);
        dc.Pop();
    }

    static Brush Frozen(Brush b) { b.Freeze(); return b; }
    static Pen FrozenPen(Pen p) { p.Freeze(); return p; }
}
