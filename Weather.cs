using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace LightningTaskbar;

public enum WeatherMode { Off, Rain, Storm }

public sealed class Settings
{
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

    void Tick(double dt)
    {
        double target = Mode switch { WeatherMode.Storm => 1.0, WeatherMode.Rain => 0.55, _ => 0 };
        Intensity += (target - Intensity) * Math.Min(1, dt * 0.6);
        if (Math.Abs(target - Intensity) < 0.002) Intensity = target;

        if (_rng.NextDouble() < dt * 0.08) _windTarget = (Mode == WeatherMode.Storm ? 0.2 : 0.08) * (_rng.NextDouble() * 1.6 - 0.3);
        Wind += (_windTarget - Wind) * dt * 0.3;

        var now = DateTime.Now;
        if (Mode != WeatherMode.Off && now >= _nextGust)
        {
            _gustTarget = (Mode == WeatherMode.Storm ? 0.55 : 0.3) * (0.6 + _rng.NextDouble() * 0.4);
            _gustUntil = now.AddSeconds(8 + _rng.NextDouble() * 14);
            _nextGust = _gustUntil.AddSeconds(45 + _rng.NextDouble() * 90);
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

    void ScheduleNext(double min, double max) =>
        _nextStrike = DateTime.Now.AddSeconds(min + _rng.NextDouble() * (max - min));
}
