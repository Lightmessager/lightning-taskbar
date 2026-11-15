using System.Windows;
using System.Windows.Media;

namespace LightningTaskbar.Desktop;

/// <summary>
/// 落叶的形状和颜色，龙卷风场景和“落叶”通知边缘特效共用。
/// 形状都以原点为中心、长度为 1（x 从 -0.5 到 0.5），画的时候用矩阵缩放、旋转、翻转。
/// </summary>
internal static class LeafArt
{
    public static readonly Geometry[] Shapes;
    public static readonly Geometry[] Veins;
    public static readonly Brush[] Fills;
    public static readonly Brush Shadow;
    public static readonly Pen VeinPen;

    static LeafArt()
    {
        Shapes = new[] { Oval(), Slender(), Maple() };
        Veins = new[] { OvalVeins(), SlenderVeins(), MapleVeins() };
        foreach (var g in Shapes) g.Freeze();
        foreach (var g in Veins) g.Freeze();

        // 秋叶：橙、金、赭红、褐、带点黄绿
        (byte r, byte g, byte b)[] colors =
        {
            (214, 96, 34), (232, 160, 46), (178, 62, 32), (150, 98, 46), (196, 126, 40), (150, 150, 52), (222, 120, 30), (122, 72, 38),
        };
        Fills = colors.Select(c =>
        {
            var light = Color.FromRgb((byte)Math.Min(255, c.r + 40), (byte)Math.Min(255, c.g + 36), (byte)Math.Min(255, c.b + 20));
            var dark = Color.FromRgb((byte)(c.r * 0.68), (byte)(c.g * 0.62), (byte)(c.b * 0.6));
            Brush brush = new LinearGradientBrush(light, dark, new Point(0, 0), new Point(1, 1));
            brush.Freeze();
            return brush;
        }).ToArray();
        Shadow = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
        Shadow.Freeze();
        VeinPen = new Pen(new SolidColorBrush(Color.FromArgb(110, 70, 34, 14)), 0.03);
        VeinPen.Freeze();
    }

    static Geometry Oval() => Geometry.Parse("M -0.5,0 C -0.25,-0.3 0.2,-0.28 0.5,0 C 0.2,0.28 -0.25,0.3 -0.5,0 Z");
    static Geometry OvalVeins() => Geometry.Parse("M -0.5,0 L 0.44,0 M -0.2,0 L -0.05,-0.15 M 0.05,0 L 0.2,-0.13 M -0.15,0 L 0,0.15 M 0.1,0 L 0.25,0.12");

    static Geometry Slender() => Geometry.Parse("M -0.5,0 C -0.2,-0.17 0.25,-0.14 0.5,0 C 0.25,0.14 -0.2,0.17 -0.5,0 Z");
    static Geometry SlenderVeins() => Geometry.Parse("M -0.5,0 L 0.46,0");

    /// <summary>五裂的枫叶：极坐标下半径随角度起伏，裂片尖、凹口深。</summary>
    static Geometry Maple()
    {
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            const int n = 90;
            for (int i = 0; i <= n; i++)
            {
                double a = (double)i / n * Math.PI * 2;
                // 叶柄方向（-x）留一个缺口
                double lobes = Math.Pow(Math.Abs(Math.Cos(2.5 * (a - Math.PI))), 0.5);
                double r = 0.5 * (0.42 + 0.58 * lobes);
                if (Math.Abs(Math.IEEERemainder(a - Math.PI, Math.PI * 2)) < 0.35) r *= 0.55;
                // 锯齿边
                r *= 1 + 0.06 * Math.Sin(a * 22);
                var p = new Point(Math.Cos(a) * r, Math.Sin(a) * r);
                if (i == 0) c.BeginFigure(p, true, true); else c.LineTo(p, true, true);
            }
        }
        return geo;
    }

    static Geometry MapleVeins() =>
        Geometry.Parse("M -0.42,0 L 0.42,0 M -0.1,0 L 0.12,-0.38 M -0.1,0 L 0.12,0.38 M -0.12,0 L -0.3,-0.3 M -0.12,0 L -0.3,0.3");

    /// <summary>
    /// 画一片叶子。flip = cos(翻转相位)，模拟叶子在空中翻面；shadowOffset 为 0 时不画投影。
    /// </summary>
    public static void Draw(DrawingContext dc, int shape, int color, Point at, double size, double angleDeg, double flip, Vector shadowOffset = default)
    {
        var m = Matrix.Identity;
        // 翻到侧面时不要缩成一条线
        double sx = Math.Abs(flip) < 0.12 ? 0.12 * Math.Sign(flip == 0 ? 1 : flip) : flip;
        m.Scale(size, size * sx);
        m.Rotate(angleDeg);
        if (shadowOffset != default)
        {
            var sm = m;
            sm.Translate(at.X + shadowOffset.X, at.Y + shadowOffset.Y);
            dc.PushTransform(new MatrixTransform(sm));
            dc.DrawGeometry(Shadow, null, Shapes[shape]);
            dc.Pop();
        }
        m.Translate(at.X, at.Y);
        dc.PushTransform(new MatrixTransform(m));
        dc.DrawGeometry(Fills[color % Fills.Length], null, Shapes[shape]);
        dc.DrawGeometry(null, VeinPen, Veins[shape]);
        dc.Pop();
    }
}
