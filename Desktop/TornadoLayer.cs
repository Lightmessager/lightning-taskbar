using System.Windows;
using System.Windows.Media;

namespace LightningTaskbar.Desktop;

/// <summary>
/// 桌面龙卷风：压低的乌云、缓慢移动并摇摆的漏斗云、绕着漏斗旋转上升的尘土，
/// 被卷起来打转、从顶部甩出去再飘落的落叶和碎屑，以及横扫画面的风痕。
/// </summary>
internal sealed class TornadoLayer
{
    struct Dust { public double F, Angle, Rho, Spin, Rise; public int Bucket; }
    enum LeafState { Free, Caught, Flung }
    struct Leaf
    {
        public double X, Y, Vx, Vy, Angle, Spin, FlipPhase, FlipSpeed, Size;
        public double F, Orbit, Rho;     // 被卷住时的高度、角度、半径倍数
        public int Shape, Color;
        public LeafState State;
    }
    struct Twig { public double X, Y, Vx, Vy, Angle, Spin, Len; }
    struct Streak { public double X, Y, Len, Speed; }

    const int DustCount = 1100, LeafCount = 70, TwigCount = 40, StreakCount = 36;
    const int DustBuckets = 6;

    readonly Random _rng = new(9191);
    readonly Dust[] _dust = new Dust[DustCount];
    readonly Leaf[] _leaves = new Leaf[LeafCount];
    readonly Twig[] _twigs = new Twig[TwigCount];
    readonly Streak[] _streaks = new Streak[StreakCount];
    double _time, _wanderT;
    bool _seeded;

    Pen[] _dustPens;
    Pen _twigPen, _streakPen, _bandLight, _bandDark;
    double _penK;

    static readonly Brush CloudShelf = Frozen(new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromArgb(245, 24, 28, 30), 0),
        new(Color.FromArgb(215, 34, 39, 40), 0.4),
        new(Color.FromArgb(0, 50, 56, 54), 1),
    }, 90));
    static readonly Brush FunnelFill = Frozen(new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromArgb(0, 50, 52, 50), 0),
        new(Color.FromArgb(130, 58, 60, 56), 0.16),
        new(Color.FromArgb(200, 84, 84, 78), 0.4),
        new(Color.FromArgb(205, 70, 70, 66), 0.62),
        new(Color.FromArgb(120, 52, 54, 52), 0.86),
        new(Color.FromArgb(0, 30, 32, 30), 1),
    }, 0));
    static readonly Brush GroundDust = Frozen(new RadialGradientBrush(Color.FromArgb(170, 112, 94, 72), Color.FromArgb(0, 112, 94, 72)));
    static readonly Brush CloudBlob = Frozen(new RadialGradientBrush(Color.FromArgb(200, 34, 38, 40), Color.FromArgb(0, 34, 38, 40)));

    public bool IsIdle => WeatherEngine.Instance.Tornado <= 0.001;

    void EnsurePens(double k)
    {
        if (_dustPens != null && Math.Abs(k - _penK) < 0.001) return;
        _penK = k;
        _dustPens = new Pen[DustBuckets];
        // 后面（暗、细）→ 前面（亮、粗）；最后一组是深色的碎屑颗粒
        (byte a, byte r, byte g, byte b, double size)[] spec =
        {
            (70, 110, 98, 84, 2.0), (110, 128, 114, 96, 2.6), (150, 150, 134, 112, 3.0),
            (190, 168, 152, 128, 3.6), (120, 120, 106, 90, 6.5), (200, 62, 50, 40, 2.4),
        };
        for (int i = 0; i < DustBuckets; i++)
            _dustPens[i] = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(spec[i].a, spec[i].r, spec[i].g, spec[i].b)), spec[i].size * Math.Max(0.6, k))
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        _twigPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(220, 70, 52, 36)), 2.2 * Math.Max(0.6, k)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        _streakPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(38, 220, 220, 210)), 1.2 * Math.Max(0.6, k)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        _bandLight = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(70, 190, 188, 176)), 3 * Math.Max(0.6, k)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        _bandDark = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(80, 30, 32, 30)), 4 * Math.Max(0.6, k)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
    }

    // ---- 漏斗的几何：f = 0 贴地，f = 1 接云底 ----
    double _baseX, _topX, _groundY, _cloudY, _k, _w;

    double CenterX(double f) =>
        _baseX + (_topX - _baseX) * Math.Pow(f, 0.8)
        + Math.Sin(_time * 0.9 + f * 5.0) * 26 * _k * (1 - f) * (0.4 + f)   // 象鼻一样的扭动
        + Math.Sin(_time * 0.37 + f * 2.0) * 14 * _k;
    double CenterY(double f) => _groundY + (_cloudY - _groundY) * f;
    double Radius(double f) => (16 + 300 * Math.Pow(f, 2.1) + 30 * f) * _k;

    void Seed(double w, double h)
    {
        for (int i = 0; i < DustCount; i++) _dust[i] = NewDust(_rng.NextDouble());
        for (int i = 0; i < LeafCount; i++) _leaves[i] = NewLeaf(w, h, true);
        for (int i = 0; i < TwigCount; i++) _twigs[i] = NewTwig(w, h, true);
        for (int i = 0; i < StreakCount; i++) _streaks[i] = NewStreak(w, h, true);
        _seeded = true;
    }

    Dust NewDust(double f)
    {
        bool ground = _rng.NextDouble() < 0.28;     // 一部分尘土在地面附近铺开
        return new Dust
        {
            F = ground ? _rng.NextDouble() * 0.08 : f,
            Angle = _rng.NextDouble() * Math.PI * 2,
            Rho = ground ? 1.2 + _rng.NextDouble() * 4.5 : 0.75 + _rng.NextDouble() * 0.6,
            Spin = 2.4 + _rng.NextDouble() * 1.8,
            Rise = ground ? 0.01 + _rng.NextDouble() * 0.03 : 0.04 + _rng.NextDouble() * 0.09,
            Bucket = _rng.NextDouble() < 0.12 ? 5 : _rng.NextDouble() < 0.08 ? 4 : 0,
        };
    }

    Leaf NewLeaf(double w, double h, bool anywhere)
    {
        return new Leaf
        {
            X = anywhere ? _rng.NextDouble() * w : -30,
            Y = anywhere ? _rng.NextDouble() * h : h * (0.2 + _rng.NextDouble() * 0.75),
            Vx = 0, Vy = 0,
            Angle = _rng.NextDouble() * 360,
            Spin = (_rng.NextDouble() * 2 - 1) * 260,
            FlipPhase = _rng.NextDouble() * Math.PI * 2,
            FlipSpeed = 2 + _rng.NextDouble() * 5,
            Size = (18 + _rng.NextDouble() * 18),
            Shape = _rng.Next(LeafArt.Shapes.Length),
            Color = _rng.Next(LeafArt.Fills.Length),
            State = LeafState.Free,
        };
    }

    Twig NewTwig(double w, double h, bool anywhere) => new()
    {
        X = anywhere ? _rng.NextDouble() * w : -20,
        Y = anywhere ? _rng.NextDouble() * h : h * (0.3 + _rng.NextDouble() * 0.65),
        Angle = _rng.NextDouble() * Math.PI,
        Spin = (_rng.NextDouble() * 2 - 1) * 8,
        Len = 6 + _rng.NextDouble() * 12,
    };

    Streak NewStreak(double w, double h, bool anywhere) => new()
    {
        X = anywhere ? _rng.NextDouble() * w : -300 - _rng.NextDouble() * 400,
        Y = h * (0.1 + _rng.NextDouble() * 0.85),
        Len = 80 + _rng.NextDouble() * 220,
        Speed = 900 + _rng.NextDouble() * 900,
    };

    public void Render(DrawingContext dc, double dt, double w, double h, double k)
    {
        var weather = WeatherEngine.Instance;
        double T = weather.Tornado;
        if (T <= 0.001) { _seeded = false; return; }
        if (!_seeded) Seed(w, h);
        _time += dt;
        _wanderT += dt * (0.6 + weather.Gust);
        _k = k; _w = w;
        EnsurePens(k);

        // 漏斗在屏幕中部慢慢游走，避开最左最右
        _baseX = w * (0.52 + 0.26 * Math.Sin(_wanderT * 0.045) + 0.06 * Math.Sin(_wanderT * 0.13));
        _topX = _baseX + w * 0.07 * Math.Sin(_wanderT * 0.06 + 1);
        _groundY = h * 0.84;
        _cloudY = h * 0.12;

        // ---- 天色：压暗、发青 ----
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(T * 0.42 * 255), 22, 30, 26)), null, new Rect(0, 0, w, h));
        dc.PushOpacity(T);

        double gust = weather.Gust;
        // ---- 风痕 ----
        var streakGeo = new StreamGeometry();
        using (var c = streakGeo.Open())
            for (int i = 0; i < StreakCount; i++)
            {
                ref var s = ref _streaks[i];
                s.X += s.Speed * k * (0.6 + gust) * dt;
                if (s.X - s.Len * k > w) { s = NewStreak(w, h, false); continue; }
                double wave = Math.Sin(_time * 3 + i) * 6 * k;
                c.BeginFigure(new Point(s.X - s.Len * k, s.Y + wave), false, false);
                c.QuadraticBezierTo(new Point(s.X - s.Len * k * 0.5, s.Y + wave - 8 * k), new Point(s.X, s.Y + wave), true, false);
            }
        streakGeo.Freeze();
        dc.DrawGeometry(null, _streakPen, streakGeo);

        // ---- 尘土：先算位置，后面的画在漏斗后，前面的画在漏斗前 ----
        var back = new StreamGeometry();
        var front = new StreamGeometry();
        var extra = new StreamGeometry[DustBuckets];
        var extraCtx = new StreamGeometryContext[DustBuckets];
        using (var cb = back.Open())
        using (var cf = front.Open())
        {
            for (int b = 4; b < DustBuckets; b++) { extra[b] = new StreamGeometry(); extraCtx[b] = extra[b].Open(); }
            for (int i = 0; i < DustCount; i++)
            {
                ref var d = ref _dust[i];
                double fNow = Math.Clamp(d.F, 0, 1);
                d.Angle += d.Spin * (1.6 - fNow) * dt * (0.8 + gust * 0.6);
                d.F += d.Rise * dt;
                if (d.F > 1.02) { d = NewDust(0); continue; }
                double r = Radius(fNow) * d.Rho;
                double cx = CenterX(fNow), cy = CenterY(fNow);
                double sin = Math.Sin(d.Angle), cos = Math.Cos(d.Angle);
                var p = new Point(cx + r * cos, cy + r * 0.2 * sin);
                if (FrameRender.Checking && !(double.IsFinite(p.X) && double.IsFinite(p.Y))) throw new InvalidOperationException($"dust NaN f={d.F}");
                // 切线方向拖一点尾巴，显出旋转
                double tl = Math.Min(0.05, 5 * k / Math.Max(1, r));
                var tail = new Point(p.X + r * sin * tl, p.Y - r * 0.2 * cos * tl);
                StreamGeometryContext target = d.Bucket >= 4 ? extraCtx[d.Bucket] : sin > 0 ? cf : cb;
                target.BeginFigure(p, false, false);
                target.LineTo(tail, true, false);
            }
            for (int b = 4; b < DustBuckets; b++) { extraCtx[b].Close(); extra[b].Freeze(); }
        }
        back.Freeze(); front.Freeze();

        dc.DrawGeometry(null, _dustPens[1], back);

        // ---- 漏斗本体 ----
        DrawFunnel(dc);

        // 地面卷起的尘团
        double pulse = 1 + 0.08 * Math.Sin(_time * 2.3);
        dc.DrawEllipse(GroundDust, null, new Point(CenterX(0), _groundY - 6 * k), 230 * k * pulse, 64 * k * pulse);

        dc.DrawGeometry(null, _dustPens[3], front);
        dc.DrawGeometry(null, _dustPens[4], extra[4]);
        dc.DrawGeometry(null, _dustPens[5], extra[5]);

        // ---- 乌云压在漏斗顶上：漏斗从云底长出来，没有硬边 ----
        dc.DrawRectangle(CloudShelf, null, new Rect(0, 0, w, h * 0.34));
        for (int i = 0; i < 9; i++)
        {
            double a = _time * 0.22 + i * 0.7;
            double cx = _topX + Math.Cos(a) * (120 + i * 60) * k;
            double cy = _cloudY + Math.Sin(a) * 22 * k - 4 * k;
            dc.DrawEllipse(CloudBlob, null, new Point(cx, cy), (230 + i * 28) * k, (60 + i * 5) * k);
        }

        // ---- 碎屑与落叶 ----
        UpdateTwigs(dc, dt, w, h, k, gust);
        UpdateLeaves(dc, dt, w, h, k, gust);
        dc.Pop();
    }

    void DrawFunnel(DrawingContext dc)
    {
        const int steps = 40;
        var left = new Point[steps + 1];
        var right = new Point[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            // 一直画到云底以上，顶端被乌云盖住，看不到硬边
            double f = 1.35 * i / steps;
            double cx = CenterX(f), cy = CenterY(f), r = Radius(f);
            // 边缘随高度轻微起伏，不是一条光滑曲线
            double ripple = 1 + 0.07 * Math.Sin(f * 23 - _time * 6) + 0.04 * Math.Sin(f * 51 + _time * 4);
            left[i] = new Point(cx - r * ripple, cy);
            right[i] = new Point(cx + r * ripple, cy);
        }
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(left[0], true, true);
            for (int i = 1; i <= steps; i++) c.LineTo(left[i], true, true);
            for (int i = steps; i >= 0; i--) c.LineTo(right[i], true, true);
        }
        geo.Freeze();
        dc.DrawGeometry(FunnelFill, null, geo);

        // 旋转的条纹：前半圈的一段段弧，随时间转动
        var light = new StreamGeometry();
        var dark = new StreamGeometry();
        using (var cl = light.Open())
        using (var cd = dark.Open())
        {
            const int bands = 30;
            for (int bi = 0; bi < bands; bi++)
            {
                double f = (bi + 0.5) / bands;
                double cx = CenterX(f), cy = CenterY(f), r = Radius(f);
                double speed = 4.2 * (1.3 - f);
                for (int s = 0; s < 3; s++)
                {
                    double a0 = _time * speed + s * 2.1 + bi * 0.7;
                    var ctx = (bi + s) % 2 == 0 ? cl : cd;
                    bool started = false;
                    for (int j = 0; j <= 6; j++)
                    {
                        double a = a0 + j * 0.13;
                        double sin = Math.Sin(a);
                        if (sin < 0.05) { started = false; continue; }   // 只画朝向我们的半圈
                        var p = new Point(cx + r * 0.92 * Math.Cos(a), cy + r * 0.12 * sin);
                        if (!started) { ctx.BeginFigure(p, false, false); started = true; }
                        else ctx.LineTo(p, true, true);
                    }
                }
            }
        }
        light.Freeze(); dark.Freeze();
        dc.DrawGeometry(null, _bandDark, dark);
        dc.DrawGeometry(null, _bandLight, light);
    }

    void UpdateTwigs(DrawingContext dc, double dt, double w, double h, double k, double gust)
    {
        var geo = new StreamGeometry();
        using (var c = geo.Open())
            for (int i = 0; i < TwigCount; i++)
            {
                ref var t = ref _twigs[i];
                Steer(ref t.X, ref t.Y, ref t.Vx, ref t.Vy, dt, k, gust, 1.2);
                t.Angle += t.Spin * dt;
                if (FrameRender.Checking && !(double.IsFinite(t.X) && double.IsFinite(t.Y))) throw new InvalidOperationException("twig NaN");
                if (t.X > w + 40 || t.X < -60 || t.Y > h + 40 || t.Y < -60) { t = NewTwig(w, h, false); continue; }
                double dx = Math.Cos(t.Angle) * t.Len * k * 0.5, dy = Math.Sin(t.Angle) * t.Len * k * 0.5;
                c.BeginFigure(new Point(t.X - dx, t.Y - dy), false, false);
                c.LineTo(new Point(t.X + dx, t.Y + dy), true, false);
            }
        geo.Freeze();
        dc.DrawGeometry(null, _twigPen, geo);
    }

    /// <summary>
    /// 自由飘的物体受风和漏斗吸引：远处只是被风吹着走，靠近漏斗时被切向的旋转气流带着打转，
    /// 同时被往上抽。
    /// </summary>
    void Steer(ref double x, ref double y, ref double vx, ref double vy, double dt, double k, double gust, double drag)
    {
        double wind = WeatherEngine.Instance.Wind;
        double f = Math.Clamp((_groundY - y) / (_groundY - _cloudY), 0, 1);
        double cx = CenterX(f), r = Radius(f);
        double dx = x - cx;
        double dist = Math.Abs(dx) / Math.Max(1, r);
        // 背景风
        double ax = (Math.Abs(wind) * 300 + 160 + gust * 500) * k - vx * drag;   // 统一从左往右吹
        double ay = 40 * k - vy * drag;   // 轻微下坠
        // 漏斗的吸力与旋转
        double pull = Math.Exp(-dist * 0.45) * 2600 * k;
        ax += -Math.Sign(dx) * pull * 0.7;
        ay += -pull * 0.55;
        // 切向：以屏幕深度方向的旋转投影成上下的晃动
        ay += Math.Sin(_time * 5 + x * 0.01) * pull * 0.4;
        vx += ax * dt; vy += ay * dt;
        x += vx * dt; y += vy * dt;
    }

    void UpdateLeaves(DrawingContext dc, double dt, double w, double h, double k, double gust)
    {
        for (int i = 0; i < LeafCount; i++)
        {
            ref var l = ref _leaves[i];
            l.FlipPhase += l.FlipSpeed * dt;
            switch (l.State)
            {
                case LeafState.Free:
                {
                    Steer(ref l.X, ref l.Y, ref l.Vx, ref l.Vy, dt, k, gust, 1.4);
                    l.Angle += l.Spin * dt;
                    double f = Math.Clamp((_groundY - l.Y) / (_groundY - _cloudY), 0, 1);
                    // 碰到漏斗就被卷进去
                    if (Math.Abs(l.X - CenterX(f)) < Radius(f) * 1.1 && l.Y < _groundY + 20 * k)
                    {
                        l.State = LeafState.Caught;
                        l.F = f;
                        l.Orbit = l.X < CenterX(f) ? Math.PI : 0;
                        l.Rho = 1.05 + _rng.NextDouble() * 0.9;
                    }
                    else if (l.X > w + 60 || l.X < -80 || l.Y > h + 60 || l.Y < -80) l = NewLeaf(w, h, false);
                    break;
                }
                case LeafState.Caught:
                {
                    l.Orbit += (3.2 + 1.5 * (1 - l.F)) * dt;
                    l.F += (0.08 + 0.05 * gust) * dt;
                    l.Angle += l.Spin * 1.8 * dt;
                    double r = Radius(Math.Min(1, l.F)) * l.Rho;
                    double nx = CenterX(Math.Min(1, l.F)) + r * Math.Cos(l.Orbit);
                    double ny = CenterY(Math.Min(1, l.F)) + r * 0.2 * Math.Sin(l.Orbit);
                    l.Vx = (nx - l.X) / Math.Max(dt, 1e-3);
                    l.Vy = (ny - l.Y) / Math.Max(dt, 1e-3);
                    l.X = nx; l.Y = ny;
                    // 转到一定高度被甩出去
                    if (l.F > 0.55 + l.Rho * 0.2)
                    {
                        l.State = LeafState.Flung;
                        double out_ = Math.Cos(l.Orbit) >= 0 ? 1 : -1;
                        l.Vx = out_ * (300 + _rng.NextDouble() * 500) * k;
                        l.Vy = -(60 + _rng.NextDouble() * 160) * k;
                    }
                    break;
                }
                case LeafState.Flung:
                    l.Vy += 120 * k * dt;
                    l.Vx += (200 * k - l.Vx) * 0.3 * dt;
                    l.X += l.Vx * dt; l.Y += l.Vy * dt;
                    l.Angle += l.Spin * dt;
                    if (l.X > w + 60 || l.X < -80 || l.Y > h + 60) l = NewLeaf(w, h, false);
                    break;
            }
            if (FrameRender.Checking && !(double.IsFinite(l.X) && double.IsFinite(l.Y) && double.IsFinite(l.Angle)))
                throw new InvalidOperationException($"leaf NaN state={l.State} x={l.X} y={l.Y} a={l.Angle} f={l.F}");
            LeafArt.Draw(dc, l.Shape, l.Color, new Point(l.X, l.Y), l.Size * k, l.Angle, Math.Cos(l.FlipPhase));
        }
    }

    static Pen Frozen(Pen p) { p.Freeze(); return p; }
    static Brush Frozen(Brush b) { b.Freeze(); return b; }
}
