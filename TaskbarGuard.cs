using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace LightningTaskbar;

/// <summary>
/// 守护进程：<c>LightningTaskbar.exe --taskbar-guard 主进程PID 启动时间 握手事件名 [主进程的启动参数…]</c>。
/// <para>
/// 主进程崩在 WPF 原生渲染线程（wpfgfx_cor3.dll 里的访问冲突、fail-fast、堆损坏）时，进程内没有任何机会善后：
/// 托管的 try/catch 和 UnhandledException 都收不到，系统任务栏就一直藏着。所以由这个独立的小进程在外面等主进程退出：
/// </para>
/// <list type="bullet">
/// <item>异常退出：自动把主进程重新拉起来（它会继续接管任务栏），用户只会看到任务栏闪一下；</item>
/// <item>10 分钟内连崩 3 次：改用软件渲染重启（绕开显卡 / 驱动）；连崩 5 次：不再重启，把系统任务栏还回去；</item>
/// <item>被强制结束（任务管理器、Stop-Process、build.ps1）：只恢复系统任务栏，不重启；</item>
/// <item>正常退出：主进程自己已经恢复过任务栏，什么都不做。</item>
/// </list>
/// 顺带每 5 分钟把主进程的句柄 / GDI / USER 对象数和内存记到 resource.log，崩溃时能看出是不是资源泄漏。
/// 守护进程不创建窗口、音频、托盘，也不会再启动自己的守护进程。
/// </summary>
internal static class TaskbarGuard
{
    internal const string Flag = "--taskbar-guard";
    internal const string RecoveredFlag = "--recovered";
    internal const string SoftwareFlag = "--software-rendering";
    internal const string HardwareFlag = "--hardware-rendering";

    const string SingleInstanceMutex = "LightningTaskbar.SingleInstance";
    static readonly TimeSpan LoopWindow = TimeSpan.FromMinutes(10);
    const int SoftwareAfter = 3, GiveUpAfter = 5;
    static readonly TimeSpan SampleEvery = TimeSpan.FromMinutes(5);

    static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LightningTaskbar");
    static string HistoryPath => Path.Combine(DataDir, "crash-history.log");
    static string ResourcePath => Path.Combine(DataDir, "resource.log");

    // ---------------- 主进程一侧 ----------------

    /// <summary>主进程启动时调用（要在隐藏系统任务栏之前）。启动失败只记日志：没有守护也比起不来强。</summary>
    internal static void Start(string[] appArgs)
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            string name = "LightningTaskbar.GuardReady." + Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, name);
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add(Flag);
            info.ArgumentList.Add(self.Id.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add(self.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            info.ArgumentList.Add(name);
            foreach (var a in appArgs) info.ArgumentList.Add(a);
            using var guard = Process.Start(info);
            if (guard == null || !ready.WaitOne(TimeSpan.FromSeconds(10)))
                App.Log("[守护] 守护进程没有在 10 秒内就绪；这次运行如果崩溃，系统任务栏不会自动恢复。");
        }
        catch (Exception ex) { App.Log(ex); }
    }

    // ---------------- 守护进程一侧 ----------------

    internal static void Run(string[] args)
    {
        try
        {
            if (args.Length < 4 || !int.TryParse(args[1], out int pid) || !long.TryParse(args[2], out long started)) return;
            string[] appArgs = args.Skip(4).ToArray();

            Process parent;
            try { parent = Process.GetProcessById(pid); }
            catch (ArgumentException) { return; }
            using (parent)
            {
                // 先拿到进程句柄再握手：之后即使主进程退出、PID 被别的进程重用，我们等的仍然是原来那个进程
                IntPtr handle = parent.Handle;
                if (parent.StartTime.ToUniversalTime().Ticks != started) return;
                using (var ready = EventWaitHandle.OpenExisting(args[3])) ready.Set();

                string lastSample = null;
                while (!parent.WaitForExit((int)SampleEvery.TotalMilliseconds))
                    lastSample = Sample(parent, handle) ?? lastSample;

                OnParentExit(parent, appArgs, lastSample);
            }
        }
        catch (Exception ex) { App.Log(ex); }
    }

    static void OnParentExit(Process parent, string[] appArgs, string lastSample)
    {
        int code = parent.ExitCode;
        if (code == 0) return;   // 正常退出，主进程自己恢复过任务栏了

        TimeSpan uptime = TimeSpan.Zero;
        try { uptime = parent.ExitTime - parent.StartTime; } catch { }
        bool crashed = IsCrash(code);

        // 用户（或别的脚本）已经启动了新实例：它正藏着系统任务栏，不能给它掀出来，也不用我们重启
        using var single = new Mutex(false, SingleInstanceMutex);
        bool owned;
        try { owned = single.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned)
        {
            App.Log($"[守护] 主进程退出 0x{(uint)code:X8}，已有新实例在运行，不处理。");
            return;
        }

        string action;
        try
        {
            if (!crashed)
            {
                ShellTaskbar.Restore(force: true);
                action = "已恢复系统任务栏，不重启";
            }
            else
            {
                bool software = appArgs.Contains(SoftwareFlag);
                int recent = RecordCrash(code, uptime, software);
                if (recent >= GiveUpAfter)
                {
                    ShellTaskbar.Restore(force: true);
                    action = $"{LoopWindow.TotalMinutes:0} 分钟内第 {recent} 次崩溃：不再重启，已恢复系统任务栏";
                }
                else
                {
                    bool fallback = recent >= SoftwareAfter && !software;
                    var next = appArgs.ToList();
                    int ri = next.IndexOf(RecoveredFlag);
                    if (ri >= 0) next.RemoveRange(ri, Math.Min(2, next.Count - ri));
                    if (fallback) { next.Remove(HardwareFlag); next.Add(SoftwareFlag); }
                    next.Add(RecoveredFlag);
                    next.Add($"0x{(uint)code:X8}");
                    action = fallback ? $"{LoopWindow.TotalMinutes:0} 分钟内第 {recent} 次崩溃：改用软件渲染重启" : "已自动重启";

                    // 给 build.ps1 之类“先杀主进程、再杀守护”的脚本留一点时间
                    Thread.Sleep(600);
                    single.ReleaseMutex();
                    owned = false;
                    if (!Restart(next))
                    {
                        ShellTaskbar.Restore(force: true);
                        action += "失败：已恢复系统任务栏";
                    }
                }
            }
        }
        finally { if (owned) single.ReleaseMutex(); }

        App.Log($"[守护] 主进程{(crashed ? "异常退出" : "被强制结束")} 0x{(uint)code:X8}，运行了 {Format(uptime)}。{action}。" +
                (lastSample != null ? $"{Environment.NewLine}  最近一次资源采样：{lastSample}" : ""));
    }

    /// <summary>
    /// 0 = 正常退出；-1 / 1 = Process.Kill、Stop-Process、taskkill、任务管理器；0xC000013A = Ctrl+C / 注销时被系统结束。
    /// 其余 0x8xxxxxxx / 0xCxxxxxxx / 0xExxxxxxx 是异常码（访问冲突、fail-fast、堆损坏、未处理的托管异常）。
    /// </summary>
    static bool IsCrash(int code) => code < -1 && (uint)code != 0xC000013A;

    static bool Restart(List<string> appArgs)
    {
        try
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath!)!,
            };
            foreach (var a in appArgs) info.ArgumentList.Add(a);
            using var p = Process.Start(info);
            if (p == null) return false;
            // 新实例如果在自己的守护就绪之前就死掉，没人替它恢复任务栏，这里多看 20 秒
            if (!p.WaitForExit(20000) || p.ExitCode == 0) return true;
            using var single = new Mutex(false, SingleInstanceMutex);
            bool free;
            try { free = single.WaitOne(0); } catch (AbandonedMutexException) { free = true; }
            if (free) single.ReleaseMutex();
            return !free;   // 还有别的实例活着就算成功
        }
        catch (Exception ex) { App.Log(ex); return false; }
    }

    /// <summary>记一次崩溃，返回最近 <see cref="LoopWindow"/> 内（含这次）的崩溃次数。</summary>
    static int RecordCrash(int code, TimeSpan uptime, bool software)
    {
        var now = DateTime.UtcNow;
        int recent = 1;
        try
        {
            Directory.CreateDirectory(DataDir);
            if (File.Exists(HistoryPath))
                foreach (var line in File.ReadLines(HistoryPath))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0 && DateTime.TryParse(line[..tab], CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) && now - t < LoopWindow)
                        recent++;
                }
            File.AppendAllText(HistoryPath,
                $"{now:yyyy-MM-ddTHH:mm:ssZ}\t0x{(uint)code:X8}\t运行 {Format(uptime)}\t{(software ? "软件渲染" : "硬件渲染")}{Environment.NewLine}");
        }
        catch { }
        return recent;
    }

    static string Sample(Process p, IntPtr handle)
    {
        try
        {
            p.Refresh();
            string line = $"句柄 {p.HandleCount}，GDI {GetGuiResources(handle, 0)}，USER {GetGuiResources(handle, 1)}，" +
                          $"线程 {p.Threads.Count}，私有内存 {p.PrivateMemorySize64 / 1048576} MB，工作集 {p.WorkingSet64 / 1048576} MB";
            Directory.CreateDirectory(DataDir);
            var file = new FileInfo(ResourcePath);
            if (file.Exists && file.Length > 256 * 1024)
            {
                // 只留后一半，文件不会无限长
                var lines = File.ReadAllLines(ResourcePath);
                File.WriteAllLines(ResourcePath, lines.Skip(lines.Length / 2));
            }
            File.AppendAllText(ResourcePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] PID {p.Id}：{line}{Environment.NewLine}");
            return line;
        }
        catch { return null; }
    }

    static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} 小时 {t.Minutes} 分" : t.TotalMinutes >= 1 ? $"{t.Minutes} 分 {t.Seconds} 秒" : $"{t.TotalSeconds:0.0} 秒";

    [DllImport("user32.dll")] static extern uint GetGuiResources(IntPtr process, uint flags);
}
