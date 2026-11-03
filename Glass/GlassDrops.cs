using System.Windows.Media.Media3D;

namespace LightningTaskbar.Glass;

/// <summary>
/// 玻璃表面的雨珠：随机落下、停留、慢慢蒸发；大的偶尔向下滑走，沿途留下小水点。
/// 只做模拟，绘制交给 <see cref="LiquidGlassEffect"/>（每颗雨珠都是真透镜）。
/// </summary>
internal sealed class GlassDrops
{
    struct Bead { public double X, Y, R, Age, Life, Vy, TrailAt; public bool Sliding; }

    readonly Bead[] _beads = new Bead[LiquidGlassEffect.MaxDrops];
    readonly bool[] _alive = new bool[LiquidGlassEffect.MaxDrops];
    readonly Random _rng = new();

    public bool Any => _alive.Contains(true);

    /// <param name="area">可落雨珠的区域（DIP，元素坐标）。</param>
    public void Step(double dt, double intensity, System.Windows.Rect area)
    {
        if (area.Width <= 0 || area.Height <= 0) return;

        if (_rng.NextDouble() < intensity * dt * 3.5)
            Spawn(area.X + _rng.NextDouble() * area.Width, area.Y + _rng.NextDouble() * area.Height,
                  2.4 + Math.Pow(_rng.NextDouble(), 2) * 4.2, 4 + _rng.NextDouble() * 10);

        for (int i = 0; i < _beads.Length; i++)
        {
            if (!_alive[i]) continue;
            ref var b = ref _beads[i];
            b.Age += dt;
            if (!b.Sliding && b.R > 4.8 && _rng.NextDouble() < dt * 0.25) { b.Sliding = true; b.Vy = 3; b.TrailAt = b.Y; }
            if (b.Sliding)
            {
                b.Vy = Math.Min(42, b.Vy + dt * 30);
                b.Y += b.Vy * dt;
                b.X += Math.Sin(b.Age * 2.3) * dt * 2;
                b.R = Math.Max(2.2, b.R - dt * 0.35);
                if (b.Y - b.TrailAt > b.R * 2.2)
                {
                    b.TrailAt = b.Y;
                    Spawn(b.X, b.Y - b.R * 1.4, b.R * 0.35, 1.5 + _rng.NextDouble() * 2.5);
                }
            }
            if (b.Age > b.Life || b.Y - b.R > area.Bottom + 2) _alive[i] = false;
        }
    }

    void Spawn(double x, double y, double r, double life)
    {
        int slot = Array.IndexOf(_alive, false);
        if (slot < 0) return;
        _beads[slot] = new Bead { X = x, Y = y, R = r, Life = life };
        _alive[slot] = true;
    }

    public void Apply(LiquidGlassEffect fx)
    {
        for (int i = 0; i < _beads.Length; i++)
        {
            if (!_alive[i]) { fx.SetDrop(i, default); continue; }
            var b = _beads[i];
            double fade = Math.Clamp(Math.Min(b.Age * 5, (b.Life - b.Age) * 1.2), 0, 1);
            fx.SetDrop(i, new Point4D(b.X, b.Y, b.R, fade * 0.95));
        }
    }

    public void Clear() => Array.Clear(_alive);
}
