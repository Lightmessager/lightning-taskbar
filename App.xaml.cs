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

    /// <summary>整个进程是否在用软件渲染（设置项或启动参数决定，启动后不变）。</summary>
    internal static bool SoftwareRendering { get; private set; }

    static void UseSoftwareRendering()
    {
        SoftwareRendering = true;
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // 守护进程：只等主进程退出（见 TaskbarGuard）。放到后台线程里等，本线程照常跑消息循环
        if (e.Args.FirstOrDefault() == TaskbarGuard.Flag)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            new Thread(() =>
            {
                TaskbarGuard.Run(e.Args);
                Dispatcher.BeginInvoke(() => Shutdown());
            }) { IsBackground = true, Name = "TaskbarGuard" }.Start();
            return;
        }

        // 必须在创建任何窗口之前决定；边缘特效子进程也靠这个参数跟主进程保持一致
        if (e.Args.Contains(TaskbarGuard.SoftwareFlag)) UseSoftwareRendering();

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

        // 按“能不能拿到所有权”判断，而不是“互斥体是不是新建的”：
        // 上一个实例崩溃后，它的守护进程会短暂打开同一个互斥体，这时互斥体已存在但没有主人
        _single = new Mutex(false, "LightningTaskbar.SingleInstance");
        bool first;
        try { first = _single.WaitOne(0); }
        catch (AbandonedMutexException) { first = true; }
        if (!first)
        {
            // 已经在运行：让那边的任务栏弹出来，否则双击快捷方式看起来像“没反应”
            try { if (EventWaitHandle.TryOpenExisting(ShowSignalName, out var signal)) signal.Set(); } catch { }
            Shutdown();
            return;
        }

        var settings = Settings.Load();
        if (!SoftwareRendering && settings.SoftwareRendering && !e.Args.Contains(TaskbarGuard.HardwareFlag)) UseSoftwareRendering();

        // 任何崩溃都必须把系统任务栏还回去
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { Log(args.ExceptionObject); Cleanup(); };
        DispatcherUnhandledException += (_, args) => { Log(args.Exception); Cleanup(); };
        SessionEnding += (_, _) => Cleanup();

        base.OnStartup(e);

        // 原生渲染线程崩溃时进程内来不及善后，交给外面的守护进程：恢复系统任务栏、自动重启。
        // 要赶在隐藏系统任务栏之前就绪。
        TaskbarGuard.Start(e.Args);

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

        int rec = Array.IndexOf(e.Args, TaskbarGuard.RecoveredFlag);
        if (rec >= 0) AnnounceRecovery(rec + 1 < e.Args.Length ? e.Args[rec + 1] : "", e.Args.Contains(TaskbarGuard.SoftwareFlag));
    }

    /// <summary>被守护进程从崩溃里拉起来之后，过几秒告诉用户一声（否则只会觉得任务栏莫名闪了一下）。</summary>
    void AnnounceRecovery(string code, bool softwareByArg)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                Notifications?.Show(new Notifications.NotificationInfo
                {
                    AppName = "Lightning Taskbar",
                    Title = "已从一次崩溃中自动恢复",
                    Body = (softwareByArg
                               ? "短时间内连续崩溃，这次改用软件渲染运行（没有真玻璃折射）。"
                               : "渲染线程异常退出，任务栏已自动重启。")
                           + $"异常码 {code}，详情见 %AppData%\\LightningTaskbar\\error.log。",
                });
            }
            catch (Exception ex) { Log(ex); }
        };
        timer.Start();
    }

    /// <summary>正常退出后马上重新启动（切换软件 / 硬件渲染要重启才生效）。</summary>
    internal void Restart(params string[] args)
    {
        Cleanup();
        try { _single?.ReleaseMutex(); } catch { }
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            foreach (var a in args) info.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(info)?.Dispose();
        }
        catch (Exception ex) { Log(ex); }
        Shutdown();
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
