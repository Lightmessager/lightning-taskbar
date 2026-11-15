using System.Windows;
using System.Windows.Media;

namespace LightningTaskbar.Desktop;

/// <summary>
/// 桌面龙卷风天。不画写实的漏斗云，而是：
/// 天色压暗发青，远处天上一团缓慢旋转的暗云（螺旋云带），下面垂着一道淡淡的雨幕 / 尘幕；
/// 地面一层尘霾；几个看不见的旋涡随风从左往右移动，几十片落叶被它们卷着打转、扫过屏幕。
/// 刻意不画满屏的尘点和风痕。
/// </summary>
internal sealed class TornadoLayer
{
    struct Leaf { public double X, Y, Vx, Vy, Angle, FlipPhase, FlipSpeed, Size; public int Shape, Color; }
    struct Eddy { public double X, Y, R, Strength, Spin, Speed; }

    const int LeafCount = 30, EddyCount = 3;
    const int Arms = 3, ArmBlobs = 22;

    readonly Random _rng = new(9191);
    readonly Leaf[] _leaves = new Leaf[LeafCount];
    readonly Eddy[] _eddies = new Eddy[EddyCount];
    double _time;
    bool _seeded;

    static readonly Brush CloudShelf = Frozen(new LinearGradientBrush(new GradientStopCollection
    {
        new(Color.FromArgb(200, 22, 26, 28), 0),
        new(Color.FromArgb(120, 30, 36, 36), 0.5),
        new(Color.FromArgb(0, 40, 46, 44), 1),
    }, 90));
    static readonly Brush CloudBase = Frozen(new RadialGradientBrush(Color.FromArgb(170, 20, 24, 26), Color.FromArgb(0, 20, 24, 26)));
    static readonly Brush ArmDark = Frozen(new RadialGradientBrush(Color.FromArgb(72, 26, 30, 32), Color.FromArgb(0, 26, 30, 32)));
    static readonly Brush ArmLight = Frozen(new RadialGradientBrush(Color.FromArgb(26, 150, 156, 158), Color.FromArgb(0, 150, 156, 158)));
    static readonly Brush Curtain = Frozen(new RadialGradientBrush(Color.FromArgb(80, 40, 44, 42), Color.FromArgb(0, 40, 44, 42)));
    static readonly Brush GroundHaze = Frozen(new LinearGradientBrush(
        new GradientStopCollection { new(Color.FromArgb(0, 120, 104, 84), 0), new(Color.FromArgb(120, 120, 104, 84), 1) }, 90));
    static readonly Brush EddyDust = Frozen(new RadialGradientBrush(Color.FromArgb(34, 150, 132, 108), Color.FromArgb(0, 150, 132, 108)));

    Leaf NewLeaf(double w, double h, bool anywhere) => new()
    {
        X = anywhere ? _rng.NextDouble() * w : -40,
        Y = anywhere ? _rng.NextDouble() * h * 0.9 : h * (0.2 + _rng.NextDouble() * 0.7),
        Angle = _rng.NextDouble() * 360,
        FlipPhase = _rng.NextDouble() * Math.PI * 2,
        FlipSpeed = 1.5 + _rng.NextDouble() * 4,
        Size = 22 + _rng.NextDouble() * 14,
        Shape = _rng.Next(LeafArt.Shapes.Length),
        Color = _rng.Next(LeafArt.Fills.Length),
    };

    Eddy NewEddy(double w, double h, bool anywhere)
    {
        double r = (130 + _rng.NextDouble() * 140);
        return new Eddy
        {
            R = r,
            X = anywhere ? _rng.NextDouble() * w : -r * 1.5,
            Y = h * (0.3 + _rng.NextDouble() * 0.5),
            Strength = 0.6 + _rng.NextDouble() * 0.5,
            Spin = _rng.NextDouble() < 0.8 ? 1 : -1,     // 大多逆时针
            Speed = 130 + _rng.NextDouble() * 140,
        };
    }

    public void Render(DrawingContext dc, double dt, double w, double h, double k)
    {
        var weather = WeatherEngine.Instance;
        double T = weather.Tornado;
        if (T <= 0.001) { _seeded = false; return; }
        if (!_seeded)
        {
            for (int i = 0; i < LeafCount; i++) _leaves[i] = NewLeaf(w, h, true);
            for (int i = 0; i < EddyCount; i++) _eddies[i] = NewEddy(w, h, true);
            _seeded = true;
        }
        _time += dt;
        double gust = weather.Gust;

        // ---- 天色：压暗、发青 ----
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(T * 0.36 * 255), 22, 30, 26)), null, new Rect(0, 0, w, h));
        dc.PushOpacity(T);
        dc.DrawRectangle(CloudShelf, null, new Rect(0, 0, w, h * 0.42));

        // ---- 远处旋转的暗云 ----
        double cx = w * (0.62 + 0.06 * Math.Sin(_time * 0.02)), cy = h * 0.15;
        const double flat = 0.32;   // 透视压扁
        dc.DrawEllipse(CloudBase, null, new Point(cx, cy), 640 * k, 210 * k);
        // 云下垂着的雨幕 / 尘幕：只是一道柔和的暗影，不画成实心漏斗
        double sway = Math.Sin(_time * 0.3) * 20 * k;
        dc.DrawEllipse(Curtain, null, new Point(cx + sway, cy + h * 0.3), 170 * k, h * 0.34);
        // 螺旋云带：沿着几条旋臂排一串柔和的云团，整体慢慢转
        double rot = _time * 0.16;
        for (int arm = 0; arm < Arms; arm++)
            for (int j = 0; j < ArmBlobs; j++)
            {
                double r = (50 + j * 25) * k;
                double a = arm * Math.PI * 2 / Arms + rot + j * 0.23;
                var p = new Point(cx + Math.Cos(a) * r, cy + Math.Sin(a) * r * flat);
                double rx = (80 + j * 9) * k, ry = rx * 0.46;
                dc.DrawEllipse(ArmDark, null, p, rx, ry);
                // 云团外侧一点点亮边，显出起伏
                dc.DrawEllipse(ArmLight, null, new Point(p.X, p.Y - ry * 0.45), rx * 0.8, ry * 0.5);
            }

        // ---- 地面尘霾 ----
        dc.PushOpacity(0.7 + 0.3 * gust);
        dc.DrawRectangle(GroundHaze, null, new Rect(0, h * 0.62, w, h * 0.38));
        dc.Pop();

        // ---- 旋涡：随风从左往右移动 ----
        for (int i = 0; i < EddyCount; i++)
        {
            ref var e = ref _eddies[i];
            e.X += e.Speed * k * (0.7 + gust) * dt;
            e.Y += Math.Sin(_time * 0.5 + i * 2) * 12 * k * dt;
            if (e.X - e.R * k * 1.5 > w) e = NewEddy(w, h, false);
            // 旋涡处一团很淡的扬尘，暗示风在这里打转
            dc.PushOpacity(0.6 * e.Strength);
            dc.DrawEllipse(EddyDust, null, new Point(e.X, e.Y), e.R * k * 1.3, e.R * k * 0.55);
            dc.Pop();
        }

        // ---- 落叶 ----
        for (int i = 0; i < LeafCount; i++)
        {
            ref var l = ref _leaves[i];
            // 背景风：从左往右，阵风时更猛；略微下坠
            double fx = (90 + gust * 300) * k, fy = 25 * k;
            foreach (var e in _eddies)
            {
                double dx = l.X - e.X, dy = (l.Y - e.Y) / 0.6;   // 旋涡在屏幕上略扁
                double dist = Math.Sqrt(dx * dx + dy * dy);
                double rr = e.R * k;
                if (dist > rr * 2.5 || dist < 1e-3) continue;
                double q = dist / rr;
                double tangential = e.Strength * 520 * k * q * Math.Exp(-q * q);
                // 切向速度（绕旋涡转）+ 被旋涡带着走 + 轻微上抽
                fx += -dy / dist * tangential * e.Spin + e.Speed * k * Math.Exp(-q * q);
                fy += dx / dist * tangential * e.Spin * 0.6 - tangential * 0.25;
            }
            double relax = Math.Min(1, dt * 2.5);
            l.Vx += (fx - l.Vx) * relax;
            l.Vy += (fy - l.Vy) * relax;
            l.X += l.Vx * dt;
            l.Y += l.Vy * dt;
            double speed = Math.Sqrt(l.Vx * l.Vx + l.Vy * l.Vy);
            l.Angle += speed / Math.Max(1, k) * 0.6 * dt * (i % 2 == 0 ? 1 : -1);
            l.FlipPhase += l.FlipSpeed * dt * (0.5 + speed / (400 * k));

            if (l.X > w + 60 || l.X < -120 || l.Y > h + 60 || l.Y < -80) { l = NewLeaf(w, h, false); continue; }
            if (FrameRender.Checking && !(double.IsFinite(l.X) && double.IsFinite(l.Y) && double.IsFinite(l.Angle)))
                throw new InvalidOperationException($"leaf NaN x={l.X} y={l.Y}");
            LeafArt.Draw(dc, l.Shape, l.Color, new Point(l.X, l.Y), l.Size * k, l.Angle, Math.Cos(l.FlipPhase));
        }
        dc.Pop();
    }

    static Brush Frozen(Brush b) { b.Freeze(); return b; }
}
