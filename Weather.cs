using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace LightningTaskbar;

/// <summary>
/// 天气模式。settings.json 里按数字保存，新模式只能追加在末尾，旧文件（0/1/2）才能照常读取。
/// </summary>
public enum WeatherMode { Off, Rain, Storm, LightSnow, Snow, Blizzard, Tornado }

/// <summary>通知边缘特效的样式，随天气变化。</summary>
public enum EdgeStyle { Glow, StormGlow, Frost, Leaves }

public static class WeatherModes
{
    /// <summary>菜单和按钮循环的顺序。</summary>
    public static readonly WeatherMode[] All =
        { WeatherMode.Off, WeatherMode.Rain, WeatherMode.Storm, WeatherMode.LightSnow, WeatherMode.Snow, WeatherMode.Blizzard, WeatherMode.Tornado };

    public static string Glyph(this WeatherMode m) => m switch
    {
        WeatherMode.Off => "☁", WeatherMode.Rain => "🌧", WeatherMode.Storm => "⛈",
        WeatherMode.LightSnow => "🌨", WeatherMode.Snow => "❄", WeatherMode.Blizzard => "🌬", WeatherMode.Tornado => "🌪",
        _ => "☁",
    };

    public static string Label(this WeatherMode m) => m switch
    {
        WeatherMode.Off => "晴（关闭天气）", WeatherMode.Rain => "下雨", WeatherMode.Storm => "雷雨",
        WeatherMode.LightSnow => "小雪", WeatherMode.Snow => "中雪", WeatherMode.Blizzard => "暴雪", WeatherMode.Tornado => "龙卷风",
        _ => m.ToString(),
    };

    public static string ShortLabel(this WeatherMode m) => m == WeatherMode.Off ? "晴" : m.Label();

    public static bool IsSnow(this WeatherMode m) => m is WeatherMode.LightSnow or WeatherMode.Snow or WeatherMode.Blizzard;

    public static EdgeStyle Edge(this WeatherMode m) => m switch
    {
        WeatherMode.Storm => EdgeStyle.StormGlow,
        WeatherMode.Tornado => EdgeStyle.Leaves,
        _ when m.IsSnow() => EdgeStyle.Frost,
        _ => EdgeStyle.Glow,
    };

    /// <summary>启动参数：--rain / --storm / --snow-light / --snow / --blizzard / --tornado / --clear。</summary>
    public static WeatherMode? FromArgs(IEnumerable<string> args)
    {
        WeatherMode? m = null;
        foreach (var a in args)
            m = a.ToLowerInvariant() switch
            {
                "--clear" or "--sunny" or "--off" => WeatherMode.Off,
                "--rain" => WeatherMode.Rain,
                "--storm" => WeatherMode.Storm,
                "--snow-light" or "--light-snow" => WeatherMode.LightSnow,
                "--snow" => WeatherMode.Snow,
                "--blizzard" => WeatherMode.Blizzard,
                "--tornado" => WeatherMode.Tornado,
                _ => m,
            };
        return m;
    }
}

/// <summary>Dock = 居中悬浮、带倒影和放大；Classic = 通栏胶囊。</summary>
public enum BarTheme { Dock, Classic }

/// <summary>Dock 停靠的屏幕边。</summary>
public enum DockEdge { Bottom, Left, Right }

/// <summary>屏幕顶部的两个角：Finder 栏和通知卡片各选一个。</summary>
public enum TopCorner { Left, Right }

/// <summary>鼠标悬停 Dock 图标时的特效（和放大可以同时生效）。</summary>
public enum HoverEffect { LightMask, Glow, Spotlight, Bounce, None }

public sealed class Settings
{
    public BarTheme Theme { get; set; } = BarTheme.Dock;
    /// <summary>Dock 图标边长（DIP）。</summary>
    public double DockIconSize { get; set; } = 52;
    public DockEdge DockEdge { get; set; } = DockEdge.Bottom;
    /// <summary>Dock 离所停靠屏幕边的距离（DIP）。</summary>
    public double DockLift { get; set; } = 14;
    /// <summary>Finder 栏（托盘、输入法、时钟等）所在的角。</summary>
    public TopCorner FinderCorner { get; set; } = TopCorner.Right;
    /// <summary>通知卡片弹出的角。</summary>
    public TopCorner NotifyCorner { get; set; } = TopCorner.Left;
    /// <summary>鼠标悬停时的最大放大倍率，1 = 不放大。</summary>
    public double DockMagnify { get; set; } = 1.35;
    public HoverEffect DockHoverEffect { get; set; } = HoverEffect.LightMask;
    /// <summary>倒影强度 0..1，0 = 无倒影。</summary>
    public double DockReflection { get; set; } = 0.5;
    /// <summary>玻璃底座浓度 0..1，0 = 只留图标和倒影。</summary>
    public double DockGlass { get; set; } = 0.6;
    /// <summary>允许截图和录屏时看到任务栏等玻璃窗口（玻璃背景不再实时刷新）。</summary>
    public bool AllowScreenshots { get; set; }
    /// <summary>在右上角用玻璃卡片显示系统通知。</summary>
    public bool GlassNotifications { get; set; } = true;
    /// <summary>来通知时屏幕四周的天气特效（紫蓝粉光 / 结霜 / 落叶）。</summary>
    public bool EdgeEffect { get; set; } = true;
    /// <summary>从开始菜单固定到任务栏的应用的显示名（键为固定项路径）。</summary>
    public Dictionary<string, string> PinTitles { get; set; } = new();
    /// <summary>在闪电开始菜单里额外固定的应用（AppEntry.ParsingName）。</summary>
    public List<string> StartAdded { get; set; } = new();
    /// <summary>在闪电开始菜单里取消固定的 Windows 固定项（AppEntry.ParsingName）。</summary>
    public List<string> StartHidden { get; set; } = new();

    public WeatherMode Mode { get; set; } = WeatherMode.Storm;
    public bool Sound { get; set; } = true;
    public double Volume { get; set; } = 0.6;
    /// <summary>任务栏高度（DIP）。</summary>
    public double BarHeight { get; set; } = 64;
    /// <summary>固定到任务栏的应用（.exe 或 .lnk 路径），按显示顺序。</summary>
    public List<string> Pinned { get; set; } = new();
    /// <summary>是否已从 Windows 原生任务栏导入过固定项（只导入一次）。</summary>
    public bool PinsImported { get; set; }
    /// <summary>直接显示在任务栏上的托盘图标（NotifyIcon.Identifier），其余收进 ^ 弹出框。</summary>
    public List<string> TrayPinned { get; set; } = new();
    public bool TrayPinsImported { get; set; }
    /// <summary>真玻璃折射。需要让任务栏不参与屏幕捕获，关闭则退回系统模糊。</summary>
    public bool GlassRefraction { get; set; } = true;
    /// <summary>
    /// 资源管理器窗口使用亚克力 + 深色背景。不注入 Explorer 只能改到标题栏一层，
    /// 系统为浅色主题时与白色内容区反差大，所以默认关闭。
    /// </summary>
    public bool ExplorerGlass { get; set; }

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LightningTaskbar", "settings.json");

    public static Settings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public readonly record struct LightningStrike(double X, double Intensity, double ThunderDelay, int Seed);

/// <summary>天气状态中心：桌面雨层、任务栏玻璃、音效都订阅这里，保证闪电与雷声同步。</summary>
public sealed class WeatherEngine
{
    public static WeatherEngine Instance { get; } = new();

    readonly Random _rng = new();
    readonly DispatcherTimer _timer = new();
    DateTime _nextStrike;

    public Settings Settings { get; private set; } = new();
    public WeatherMode Mode => Settings.Mode;
    /// <summary>雨量 0..1，切换模式时平滑过渡。</summary>
    public double Intensity { get; private set; }
    /// <summary>风（雨丝倾斜），-1..1，缓慢漂移。</summary>
    public double Wind { get; private set; } = 0.1;
    /// <summary>阵风 0..1：大部分时间为 0，偶尔起一阵、持续一段时间再慢慢平息（只用于风声）。</summary>
    public double Gust { get; private set; }
    /// <summary>雪量 0..1：小雪 0.3、中雪 0.6、暴雪 1。</summary>
    public double Snow { get; private set; }
    /// <summary>暴雪程度 0..1（白茫茫、呼啸声），只有暴雪模式趋向 1。</summary>
    public double Blizzard { get; private set; }
    /// <summary>龙卷风 0..1。</summary>
    public double Tornado { get; private set; }
    /// <summary>持续的底风 0..1（雪天的轻风、暴雪和龙卷风的强风），叠加在阵风之上。</summary>
    public double BaseWind { get; private set; }

    public event Action ModeChanged;
    public event Action<LightningStrike> Lightning;

    WeatherEngine()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.Tick += (_, _) => Tick(0.05);
    }

    public void Start(Settings s)
    {
        Settings = s;
        ScheduleNext(3, 8);
        _timer.Start();
    }

    public void SetMode(WeatherMode m)
    {
        if (Settings.Mode == m) return;
        Settings.Mode = m;
        Settings.Save();
        if (m == WeatherMode.Storm) ScheduleNext(2, 6);
        ModeChanged?.Invoke();
    }

    public void NotifySettingsChanged()
    {
        Settings.Save();
        ModeChanged?.Invoke();
    }

    double _windTarget = 0.1, _gustTarget;
    DateTime _gustUntil = DateTime.MinValue, _nextGust = DateTime.Now.AddSeconds(40);

    internal void Tick(double dt)
    {
        double target = Mode switch { WeatherMode.Storm => 1.0, WeatherMode.Rain => 0.55, _ => 0 };
        Intensity += (target - Intensity) * Math.Min(1, dt * 0.6);
        if (Math.Abs(target - Intensity) < 0.002) Intensity = target;

        Snow = Approach(Snow, Mode switch { WeatherMode.LightSnow => 0.3, WeatherMode.Snow => 0.6, WeatherMode.Blizzard => 1.0, _ => 0 }, dt * 0.5);
        Blizzard = Approach(Blizzard, Mode == WeatherMode.Blizzard ? 1 : 0, dt * 0.35);
        Tornado = Approach(Tornado, Mode == WeatherMode.Tornado ? 1 : 0, dt * 0.3);
        BaseWind = Approach(BaseWind, Mode switch
        {
            WeatherMode.LightSnow => 0.12, WeatherMode.Snow => 0.2, WeatherMode.Blizzard => 0.62, WeatherMode.Tornado => 0.5, _ => 0,
        }, dt * 0.3);

        if (_rng.NextDouble() < dt * 0.08)
            _windTarget = Mode switch
            {
                // 暴雪：横着刮，方向基本固定，只有强弱起伏
                WeatherMode.Blizzard => 0.75 + _rng.NextDouble() * 0.5,
                WeatherMode.Tornado => (_rng.NextDouble() * 2 - 1) * 0.5,
                WeatherMode.Snow or WeatherMode.LightSnow => (_rng.NextDouble() * 2 - 0.6) * 0.25,
                WeatherMode.Storm => 0.2 * (_rng.NextDouble() * 1.6 - 0.3),
                _ => 0.08 * (_rng.NextDouble() * 1.6 - 0.3),
            };
        Wind += (_windTarget - Wind) * dt * 0.3;

        var now = DateTime.Now;
        if (Mode != WeatherMode.Off && now >= _nextGust)
        {
            double strength = Mode switch
            {
                WeatherMode.Storm => 0.55, WeatherMode.Blizzard => 0.75, WeatherMode.Tornado => 0.85,
                WeatherMode.LightSnow or WeatherMode.Snow => 0.22, _ => 0.3,
            };
            _gustTarget = strength * (0.6 + _rng.NextDouble() * 0.4);
            _gustUntil = now.AddSeconds(8 + _rng.NextDouble() * 14);
            // 暴雪和龙卷风阵风更频繁
            double pause = Mode is WeatherMode.Blizzard or WeatherMode.Tornado ? 8 + _rng.NextDouble() * 20 : 45 + _rng.NextDouble() * 90;
            _nextGust = _gustUntil.AddSeconds(pause);
        }
        if (now >= _gustUntil || Mode == WeatherMode.Off) _gustTarget = 0;
        // 起风快一点，停风慢一点
        Gust += (_gustTarget - Gust) * dt * (_gustTarget > Gust ? 0.35 : 0.12);

        if (Mode == WeatherMode.Storm && DateTime.Now >= _nextStrike)
        {
            Strike();
            // 偶尔连闪
            if (_rng.NextDouble() < 0.3) ScheduleNext(0.25, 1.2);
            else ScheduleNext(6, 20);
        }
    }

    public void Strike()
    {
        double intensity = 0.45 + _rng.NextDouble() * 0.55;
        // 越亮越近，雷声延迟越短
        double delay = (1.0 - intensity) * 3.2 + _rng.NextDouble() * 0.4;
        Lightning?.Invoke(new LightningStrike(0.08 + _rng.NextDouble() * 0.84, intensity, delay, _rng.Next()));
    }

    static double Approach(double v, double target, double rate)
    {
        v += (target - v) * Math.Min(1, rate);
        return Math.Abs(target - v) < 0.002 ? target : v;
    }

    void ScheduleNext(double min, double max) =>
        _nextStrike = DateTime.Now.AddSeconds(min + _rng.NextDouble() * (max - min));
}
