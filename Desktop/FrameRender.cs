using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LightningTaskbar.Notifications;

namespace LightningTaskbar.Desktop;

/// <summary>
/// 开发用：不启动任务栏，把各种天气的桌面画面和各种通知边缘特效的若干时刻渲染成 PNG，方便检查效果。
/// 用法：LightningTaskbar.exe --render-frames 输出目录
/// </summary>
internal static class FrameRender
{
    const int W = 1920, H = 1080;

    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var engine = WeatherEngine.Instance;
        var settings = new Settings();
        engine.Start(settings);   // 只为拿到 Settings；不保存

        foreach (var mode in new[] { WeatherMode.LightSnow, WeatherMode.Snow, WeatherMode.Blizzard, WeatherMode.Tornado })
        {
            settings.Mode = mode;
            var scene = new RainScene();
            scene.Measure(new Size(W, H));
            scene.Arrange(new Rect(0, 0, W, H));
            // 模拟 25 秒：天气过渡完成、雪花铺满、积雪开始堆起来
            const double dt = 1 / 30.0;
            for (int i = 0; i < 25 * 30; i++)
            {
                engine.Tick(dt);
                scene.Step(dt);
            }
            Save(scene, Path.Combine(dir, $"weather-{mode}.png"));
            // 回到晴天，让下一种天气从零开始
            settings.Mode = WeatherMode.Off;
            for (int i = 0; i < 40 * 30; i++) engine.Tick(dt);
        }

        foreach (var style in Enum.GetValues<EdgeStyle>())
        {
            var canvas = new EdgeCanvas();
            var root = new Grid
            {
                Background = new LinearGradientBrush(Color.FromRgb(40, 52, 74), Color.FromRgb(16, 20, 30), 90),
                Children = { canvas },
            };
            root.Measure(new Size(W, H));
            root.Arrange(new Rect(0, 0, W, H));
            canvas.Start(style);
            double t = 0;
            foreach (double at in new[] { 0.5, 1.0, 1.6, 2.3, 2.7 })
            {
                while (t < at) { canvas.Advance(1 / 60.0); t += 1 / 60.0; }
                Save(root, Path.Combine(dir, $"edge-{style}-{at:0.0}s.png"));
            }
        }
    }

    /// <summary>开发用：快速轮换天气、连续推进几万帧，检查切换天气时是否出错。--stress-scene 帧数 [跳过的层: snow/tornado]</summary>
    /// <summary>压力测试时检查坐标是否有限（NaN / 无穷大会让 WPF 渲染线程直接崩溃）。</summary>
    public static bool Checking;

    public static void Stress(int frames, string skip)
    {
        Checking = true;
        var engine = WeatherEngine.Instance;
        var settings = new Settings();
        engine.Start(settings);
        var scene = new RainScene();
        scene.SkipLayers = skip ?? "";
        scene.Measure(new Size(W, H));
        scene.Arrange(new Rect(0, 0, W, H));
        var rng = new Random(1);
        const double dt = 1 / 60.0;
        for (int i = 0; i < frames; i++)
        {
            if (i % 120 == 0) settings.Mode = WeatherModes.All[rng.Next(WeatherModes.All.Length)];
            engine.Tick(dt);
            scene.Step(dt);
        }
    }

    static void Save(FrameworkElement e, string path)
    {
        e.UpdateLayout();
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(e);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var f = File.Create(path);
        enc.Save(f);
    }
}
