using System.Windows;
using LightningTaskbar.Audio;
using LightningTaskbar.Desktop;

namespace LightningTaskbar;

public partial class App : Application
{
    Mutex _single;
    internal StormAudio Audio { get; private set; }
    internal DesktopLayer Desktop { get; private set; }
    internal TrayHost Tray { get; private set; }
    internal StartMenu.StartMenuWindow StartMenu { get; private set; }
    internal TaskbarWindow Bar { get; private set; }
    internal Notifications.NotificationCenter Notifications { get; private set; }
    AppearanceWindow _appearance;
    StartMenu.WinKeyHook _winHook;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 开发用：离线渲染雨声并分析，不启动任务栏
        int ri = Array.IndexOf(e.Args, "--render-rain");
        if (ri >= 0 && ri + 1 < e.Args.Length)
        {
            float rain = ri + 2 < e.Args.Length && float.TryParse(e.Args[ri + 2], out var v) ? v : 1f;
            RainRender.Run(e.Args[ri + 1], rain);
            Shutdown();
            return;
        }

        // 通知边缘特效在独立的短命子进程里播放（见 NotificationCenter.PlayEdge），播完就退出
        int ei = Array.IndexOf(e.Args, "--edge-effect");
        if (ei >= 0 && ei + 1 < e.Args.Length)
        {
            RunEdgeEffect(e.Args[ei + 1], e.Args.Contains("--allow-capture"));
            return;
        }

        int fi = Array.IndexOf(e.Args, "--render-frames");
        if (fi >= 0 && fi + 1 < e.Args.Length)
        {
            try { FrameRender.Run(e.Args[fi + 1]); } catch (Exception ex) { Log(ex); }
            Shutdown();
            return;
        }

        int st = Array.IndexOf(e.Args, "--stress-scene");
        if (st >= 0 && st + 1 < e.Args.Length)
        {
            try { FrameRender.Stress(int.Parse(e.Args[st + 1]), st + 2 < e.Args.Length ? e.Args[st + 2] : null); Log("stress ok"); }
            catch (Exception ex) { Log(ex); }
            Shutdown();
            return;
        }

        int gi = Array.IndexOf(e.Args, "--render-gale");
        if (gi >= 0 && gi + 4 < e.Args.Length)
        {
            float F(int i) => float.TryParse(e.Args[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
            RainRender.RunGale(e.Args[gi + 1], F(gi + 2), F(gi + 3), F(gi + 4));
            Shutdown();
            return;
        }

        int si = Array.IndexOf(e.Args, "--render-scene");
        if (si >= 0 && si + 1 < e.Args.Length)
        {
            RainRender.RunScene(e.Args[si + 1]);
            Shutdown();
            return;
        }

        _single = new Mutex(true, "LightningTaskbar.SingleInstance", out bool first);
        if (!first)
        {
            // 已经在运行：让那边的任务栏弹出来，否则双击快捷方式看起来像“没反应”
            try { if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal)) signal.Set(); } catch { }
            Shutdown();
            return;
        }

        // 任何崩溃都必须把系统任务栏还回去
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { Log(args.ExceptionObject); Cleanup(); };
        DispatcherUnhandledException += (_, args) => { Log(args.Exception); Cleanup(); };
        SessionEnding += (_, _) => Cleanup();

        base.OnStartup(e);

        var settings = Settings.Load();
        if (WeatherModes.FromArgs(e.Args) is { } argMode) settings.Mode = argMode;
        WeatherEngine.Instance.Start(settings);
        Glass.GlassSurface.AllowCapture = settings.AllowScreenshots;

        Audio = new StormAudio();
        Desktop = new DesktopLayer();
        Desktop.Attach();

        Tray = new TrayHost();
        if (settings.ExplorerGlass) ExplorerGlass.Start();
        ShellTaskbar.Hide();
        StartMenu = new StartMenu.StartMenuWindow();
        _winHook = new StartMenu.WinKeyHook(() => StartMenu.Toggle());
        var bar = new TaskbarWindow();
        Bar = bar;
        bar.Show();
        ListenForShowSignal(bar);
        Notifications = new Notifications.NotificationCenter();
        Notifications.Start();
    }

    void RunEdgeEffect(string styleName, bool allowCapture)
    {
        // 子进程里任何错误都只是少播一次特效，直接退出
        DispatcherUnhandledException += (_, a) => { a.Handled = true; Shutdown(); };
        if (!Enum.TryParse<EdgeStyle>(styleName, true, out var style)) { Shutdown(); return; }
        Glass.GlassSurface.AllowCapture = allowCapture;
        var win = new Notifications.EdgeEffectWindow();
        win.Finished += Shutdown;
        // 保险：无论如何 8 秒后退出
        var guard = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        guard.Tick += (_, _) => Shutdown();
        guard.Start();
        win.Play(style);
    }

    internal bool AppearanceOpen => _appearance?.IsVisible == true;

    internal void ShowAppearance()
    {
        _appearance ??= new AppearanceWindow();
        _appearance.Open();
    }

    /// <summary>崩溃原因写到 %AppData%\LightningTaskbar\error.log，方便排查。</summary>
    internal static void Log(object error)
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LightningTaskbar");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    const string ShowSignalName = "LightningTaskbar.Show";

    /// <summary>再次启动程序时（如双击快捷方式），第二个进程会触发这个信号。</summary>
    void ListenForShowSignal(TaskbarWindow bar)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        var thread = new Thread(() =>
        {
            while (signal.WaitOne()) Dispatcher.BeginInvoke(bar.Reveal);
        }) { IsBackground = true, Name = "ShowSignal" };
        thread.Start();
    }

    bool _cleaned;
    void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;
        try { _winHook?.Dispose(); } catch { }
        try { Notifications?.Dispose(); } catch { }
        ShellTaskbar.Restore();
        try { Desktop?.Dispose(); } catch { }
        try { Audio?.Dispose(); } catch { }
        try { Tray?.Dispose(); } catch { }
        try { ExplorerGlass.Stop(); } catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }
}
