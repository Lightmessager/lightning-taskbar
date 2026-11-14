using System.Windows;
using System.Windows.Media;

namespace LightningTaskbar.Desktop;

/// <summary>
/// 桌面雪景：四层景深的雪花（飘落 + 左右摆动，暴雪时被风横着吹），
/// 雪天的冷灰天色、暴雪的白茫茫雪雾，以及底部慢慢堆起来的积雪。
/// 雪花按景深和大小分桶，每桶一个 StreamGeometry（圆头短线 = 圆点，速度快时自然拉成雪线）。
/// </summary>
internal sealed class SnowLayer
{
    struct Flake { public double X, Y, Z, Phase, SwayAmp, SwayFreq; public int Bucket; }

    const int MaxFlakes = 1700;
    const int Layers = 4, SizesPerLayer = 2, Buckets = Layers * SizesPerLayer;

    readonly Random _rng = new(4242);
    readonly Flake[] _flakes = new Flake[MaxFlakes];
    int _active;
    double _time, _accum;
    bool _seeded;

    // 积雪的起伏轮廓（固定的几组正弦叠加，像被风堆出来的雪坡）
    readonly double[] _driftFreq = new double[5], _driftPhase = new double[5], _driftAmp = new double[5];

    // 雪雾团：几团大的柔光椭圆随风飘过
    struct Haze { public double X, Y, R, Speed; }
    readonly Haze[] _haze = new Haze[6];

    Pen[] _pens, _glowPens;
    double _penK;

    static readonly Brush SnowBank = Frozen(new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromArgb(250, 250, 252, 255), 0),
        new(Color.FromArgb(245, 228, 236, 248), 0.35),
        new(Color.FromArgb(240, 196, 210, 230), 1),
    }, 90));
    static readonly Pen SnowBankRim = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), 1.5));
    static readonly Brush HazeBrush = Frozen(new RadialGradientBrush(Color.FromArgb(150, 236, 242, 250), Color.FromArgb(0, 236, 242, 250)));
    static readonly Brush GroundMist = Frozen(new LinearGradientBrush(
        new GradientStopCollection { new(Color.FromArgb(0, 230, 236, 245), 0), new(Color.FromArgb(150, 230, 236, 245), 1) }, 90));

    public SnowLayer()
    {
        for (int i = 0; i < 5; i++)
        {
            _driftFreq[i] = (i + 1) * (0.8 + _rng.NextDouble() * 0.9);
            _driftPhase[i] = _rng.NextDouble() * Math.PI * 2;
            _driftAmp[i] = 0.35 / (i + 1) * (0.6 + _rng.NextDouble() * 0.8);
        }
    }

    public bool IsIdle => _active == 0 && _accum <= 0.001;

    Flake NewFlake(double w, double h, bool anywhere, double wind)
    {
        double z = Math.Pow(_rng.NextDouble(), 1.35);   // 远处的小雪花多，近处的大雪花少
        int layer = Math.Min(Layers - 1, (int)(z * Layers));
        var f = new Flake
        {
            Z = z,
            Phase = _rng.NextDouble() * Math.PI * 2,
            SwayAmp = 0.4 + _rng.NextDouble() * 1.2,
            SwayFreq = 0.5 + _rng.NextDouble() * 1.3,
            Bucket = layer * SizesPerLayer + (_rng.NextDouble() < 0.7 ? 0 : 1),
        };
        if (anywhere)
        {
            f.X = _rng.NextDouble() * w;
            f.Y = _rng.NextDouble() * h;
        }
        else if (Math.Abs(wind) > 0.35 && _rng.NextDouble() < Math.Min(0.75, Math.Abs(wind) * 0.7))
        {
            // 横风大时，一部分雪从上风方向的侧边吹进来，否则屏幕一侧会空着
            f.X = wind > 0 ? -20 : w + 20;
            f.Y = _rng.NextDouble() * h * 0.9;
        }
        else
        {
            f.X = _rng.NextDouble() * (w + 400) - 200;
            f.Y = -10 - _rng.NextDouble() * 60;
        }
        return f;
    }

    void EnsurePens(double k)
    {
        if (_pens != null && Math.Abs(k - _penK) < 0.001) return;
        _penK = k;
        _pens = new Pen[Buckets];
        _glowPens = new Pen[Buckets];
        // 远 → 近：越近越大越亮
        double[] size = { 1.5, 2.3, 3.2, 4.6 };
        byte[] alpha = { 105, 150, 200, 235 };
        for (int layer = 0; layer < Layers; layer++)
            for (int s = 0; s < SizesPerLayer; s++)
            {
                double px = size[layer] * (s == 0 ? 1 : 1.45) * Math.Max(0.6, k);
                var b = new SolidColorBrush(Color.FromArgb(alpha[layer], 248, 251, 255));
                _pens[layer * SizesPerLayer + s] = Frozen(new Pen(b, px) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
                // 近处的雪花带一圈柔光，像镜头前的失焦雪花
                var g = new SolidColorBrush(Color.FromArgb((byte)(layer >= 2 ? 34 : 0), 240, 246, 255));
                _glowPens[layer * SizesPerLayer + s] = Frozen(new Pen(g, px * 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            }
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

        int target = (int)(Math.Pow(snow, 1.4) * MaxFlakes);
        if (!_seeded && target > 0)
        {
            // 第一次（比如启动时就是雪天）直接铺满全屏，不要等雪从顶上慢慢落下来
            for (int i = 0; i < MaxFlakes; i++) _flakes[i] = NewFlake(w, h, true, weather.Wind);
            _seeded = true;
        }
        if (_active < target) _active = Math.Min(target, _active + (int)(dt * 260) + 1);
        else if (_active > target) _active = Math.Max(target, _active - (int)(dt * 500) - 1);

        if (snow <= 0.001 && _accum <= 0.001 && _active == 0) return;

        // ---- 天色：雪天发灰发冷，暴雪白茫茫 ----
        double overcast = snow * 0.16;
        if (overcast > 0.004)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(overcast * 255), 150, 162, 182)), null, new Rect(0, 0, w, h));
        double white = blizzard * (0.2 + 0.1 * weather.Gust);
        if (white > 0.004)
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(white * 255), 226, 233, 243)), null, new Rect(0, 0, w, h));

        EnsurePens(k);
        double wind = weather.Wind;
        double gust = weather.Gust;
        // 横向风速（px/s），暴雪时远大于下落速度
        double windPx = (wind * (60 + 460 * blizzard) + Math.Sign(wind) * gust * 380 * blizzard) * k;

        // ---- 雪雾团（暴雪）：降低能见度，随风掠过 ----
        if (blizzard > 0.01)
        {
            for (int i = 0; i < _haze.Length; i++)
            {
                ref var hz = ref _haze[i];
                if (hz.R == 0 || hz.X - hz.R > w + 50 || hz.X + hz.R < -50)
                {
                    bool initial = hz.R == 0;
                    hz.R = (260 + _rng.NextDouble() * 380) * k;
                    hz.Y = h * (0.15 + _rng.NextDouble() * 0.8);
                    hz.Speed = 0.6 + _rng.NextDouble() * 0.7;
                    hz.X = initial ? _rng.NextDouble() * w : windPx >= 0 ? -hz.R : w + hz.R;
                }
                hz.X += windPx * hz.Speed * dt;
                double a = blizzard * (0.45 + 0.35 * Math.Sin(_time * 0.4 + i * 1.7)) * (0.7 + 0.5 * gust);
                dc.PushOpacity(Math.Clamp(a, 0, 1));
                dc.DrawEllipse(HazeBrush, null, new Point(hz.X, hz.Y), hz.R * 1.6, hz.R * 0.7);
                dc.Pop();
            }
        }

        // ---- 雪花 ----
        var geos = new StreamGeometry[Buckets];
        var ctx = new StreamGeometryContext[Buckets];
        for (int b = 0; b < Buckets; b++) { geos[b] = new StreamGeometry(); ctx[b] = geos[b].Open(); }

        double fallBoost = 1 + blizzard * 0.9;
        for (int i = 0; i < _active; i++)
        {
            ref var f = ref _flakes[i];
            double depth = 0.35 + 0.65 * f.Z;                 // 近处的雪看起来更快
            double vy = (38 + 105 * f.Z) * k * fallBoost;
            double sway = Math.Cos(_time * f.SwayFreq + f.Phase) * f.SwayAmp * 22 * k * (1 - blizzard * 0.6);
            double vx = windPx * depth + sway;
            f.X += vx * dt;
            f.Y += vy * dt;

            if (f.Y > h + 12 || f.X < -260 || f.X > w + 260)
            {
                f = NewFlake(w, h, false, windPx / Math.Max(1, 300 * k));
                continue;
            }

            // 拖影：低速时近乎一个圆点，暴雪时拉成短线
            double blur = 0.006 + 0.022 * blizzard;
            if (FrameRender.Checking && !(double.IsFinite(f.X) && double.IsFinite(f.Y) && double.IsFinite(vx) && double.IsFinite(vy)))
                throw new InvalidOperationException($"snow NaN x={f.X} y={f.Y} vx={vx} vy={vy} wind={windPx}");
            var c = ctx[f.Bucket];
            c.BeginFigure(new Point(f.X, f.Y), false, false);
            c.LineTo(new Point(f.X - vx * blur - 0.05, f.Y - vy * blur - 0.05), true, false);
        }
        for (int b = 0; b < Buckets; b++) { ctx[b].Close(); geos[b].Freeze(); }

        for (int b = 0; b < Buckets; b++)
        {
            if (b >= 2 * SizesPerLayer) dc.DrawGeometry(null, _glowPens[b], geos[b]);
            dc.DrawGeometry(null, _pens[b], geos[b]);
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
        dc.DrawGeometry(SnowBank, SnowBankRim, geo);
        dc.Pop();
    }

    static Pen Frozen(Pen p) { p.Freeze(); return p; }
    static Brush Frozen(Brush b) { b.Freeze(); return b; }
}
