using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LightningTaskbar;

/// <summary>任务栏上的一个图标：一个运行中的窗口，或一个尚未运行的固定应用。</summary>
public sealed class TaskItem : INotifyPropertyChanged
{
    public string Key { get; init; }

    IntPtr _handle;
    /// <summary>主窗口（组内最近使用的那个）；固定但未运行的应用为 Zero。</summary>
    public IntPtr Handle { get => _handle; set { if (_handle != value) { _handle = value; Raise(nameof(Handle)); Raise(nameof(IsRunning)); } } }
    public string ProcessPath { get; set; }
    public bool IsRunning => Handle != IntPtr.Zero;

    IReadOnlyList<IntPtr> _windows = Array.Empty<IntPtr>();
    /// <summary>同一应用的全部窗口（按出现顺序），合并成这一个图标。</summary>
    public IReadOnlyList<IntPtr> Windows
    {
        get => _windows;
        set
        {
            bool countChanged = value.Count != _windows.Count;
            _windows = value;
            if (countChanged) { Raise(nameof(WindowCount)); Raise(nameof(IsGroup)); Raise(nameof(CountText)); }
        }
    }
    public int WindowCount => _windows.Count;
    public bool IsGroup => _windows.Count > 1;
    public string CountText => _windows.Count > 9 ? "9+" : _windows.Count.ToString();

    string _pinPath;
    /// <summary>固定项路径（.exe / .lnk），未固定为 null。</summary>
    public string PinPath
    {
        get => _pinPath;
        set { if (_pinPath != value) { _pinPath = value; Raise(nameof(PinPath)); Raise(nameof(IsPinned)); Raise(nameof(PinMenuText)); } }
    }
    public bool IsPinned => _pinPath != null;
    public string PinMenuText => IsPinned ? "从任务栏取消固定" : "固定到任务栏";
    public bool CanPin => IsPinned || WindowTracker.IsPinnable(ProcessPath);

    string _title;
    public string Title { get => _title; set { if (_title != value) { _title = value; Raise(nameof(Title)); Raise(nameof(Initial)); } } }

    ImageSource _icon;
    public ImageSource Icon { get => _icon; set { _icon = value; Raise(nameof(Icon)); Raise(nameof(HasIcon)); } }
    public bool HasIcon => _icon != null;
    public string Initial => string.IsNullOrEmpty(Title) ? "?" : Title.Substring(0, 1).ToUpperInvariant();

    bool _active;
    public bool IsActive { get => _active; set { if (_active != value) { _active = value; Raise(nameof(IsActive)); } } }

    bool _minimized;
    public bool IsMinimized { get => _minimized; set { if (_minimized != value) { _minimized = value; Raise(nameof(IsMinimized)); } } }

    public event PropertyChangedEventHandler PropertyChanged;
    void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// 枚举可在任务栏上显示的顶层窗口（规则近似 Alt+Tab），并与固定应用合并：
/// 固定项排在最前，运行中的同一应用合并到固定项位置上。
/// </summary>
internal sealed class WindowTracker
{
    public ObservableCollection<TaskItem> Items { get; } = new();
    readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _pinTargets = new(StringComparer.OrdinalIgnoreCase);
    readonly List<IntPtr> _order = new();     // 窗口出现顺序，保证图标位置稳定
    readonly Dictionary<IntPtr, string> _paths = new();
    readonly uint _ownPid = (uint)Environment.ProcessId;

    static Settings Settings => WeatherEngine.Instance.Settings;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint dwAttributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint size, uint flags);

    static readonly HashSet<string> IgnoredClasses = new()
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow"
    };

    // ---------------- 固定 ----------------

    /// <summary>首次运行时导入 Windows 原生任务栏上的固定项。</summary>
    public void ImportWindowsPins()
    {
        if (Settings.PinsImported) return;
        Settings.PinsImported = true;
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (Directory.Exists(dir))
                foreach (var lnk in Directory.GetFiles(dir, "*.lnk").OrderBy(f => File.GetCreationTime(f)))
                    if (!Settings.Pinned.Contains(lnk, StringComparer.OrdinalIgnoreCase)) Settings.Pinned.Add(lnk);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        Settings.Save();
    }

    public static bool IsPinnable(string path) =>
        !string.IsNullOrEmpty(path) && !path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);

    public void Pin(string path)
    {
        if (!IsPinnable(path) || Settings.Pinned.Any(p => SamePin(p, path))) return;
        Settings.Pinned.Add(path);
        Settings.Save();
        Refresh();
    }

    public void Unpin(TaskItem item)
    {
        if (item.PinPath == null) return;
        Settings.Pinned.RemoveAll(p => string.Equals(p, item.PinPath, StringComparison.OrdinalIgnoreCase));
        Settings.Save();
        Refresh();
    }

    public void TogglePin(TaskItem item)
    {
        if (item.IsPinned) Unpin(item);
        else Pin(item.ProcessPath);
    }

    bool SamePin(string pin, string exe) =>
        string.Equals(pin, exe, StringComparison.OrdinalIgnoreCase) || SameExe(exe, ResolvePinTarget(pin));

    /// <summary>.lnk 解析到目标 exe，用于和运行中的进程匹配。</summary>
    string ResolvePinTarget(string pin)
    {
        if (_pinTargets.TryGetValue(pin, out var t)) return t;
        t = pin;
        if (pin.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var info = Shortcut.Read(pin);
            // 没有目标的外壳快捷方式（如“文件资源管理器”）退而用图标所在的 exe 来匹配
            t = info.Target ?? (info.IconPath != null && info.IconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? info.IconPath : null);
        }
        _pinTargets[pin] = t;
        return t;
    }

    readonly Dictionary<string, string> _finalPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 判断进程路径与固定目标是否同一程序。先比较解析掉符号链接/目录联接后的真实路径
    /// （例如被“电脑管家”迁移到 D 盘、C 盘只留链接的软件），再退回到 exe 文件名相同。
    /// </summary>
    bool SameExe(string running, string target)
    {
        if (running == null || target == null) return false;
        if (string.Equals(running, target, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(FinalPath(running), FinalPath(target), StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(Path.GetFileName(running), Path.GetFileName(target), StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetFinalPathNameByHandle(IntPtr h, System.Text.StringBuilder sb, uint cch, uint flags);

    string FinalPath(string path)
    {
        if (_finalPaths.TryGetValue(path, out var f)) return f;
        f = path;
        var h = CreateFile(path, 0, 7 /*共享读写删*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0x02000000 /*BACKUP_SEMANTICS*/, IntPtr.Zero);
        if (h != new IntPtr(-1))
        {
            var sb = new System.Text.StringBuilder(1024);
            if (GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0) > 0)
                f = sb.ToString().StartsWith(@"\\?\") ? sb.ToString(4, sb.Length - 4) : sb.ToString();
            Native.CloseHandle(h);
        }
        _finalPaths[path] = f;
        return f;
    }

    public static void Launch(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    // ---------------- 枚举与合并 ----------------

    public void Refresh()
    {
        var found = new HashSet<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            if (IsTaskWindow(h)) found.Add(h);
            return true;
        }, IntPtr.Zero);

        _order.RemoveAll(h => !found.Contains(h));
        foreach (var h in found) if (!_order.Contains(h)) _order.Add(h);
        foreach (var dead in _paths.Keys.Where(h => !found.Contains(h)).ToList()) _paths.Remove(dead);
        foreach (var h in _order) if (!_paths.ContainsKey(h)) _paths[h] = Native.GetProcessPath(h);

        var fg = Native.GetForegroundWindow();
        if (found.Contains(fg)) _lastActive[fg] = ++_activeClock;
        foreach (var dead in _lastActive.Keys.Where(h => !found.Contains(h)).ToList()) _lastActive.Remove(dead);

        // 期望的顺序：固定项（含其运行中的窗口）→ 其它运行中的应用。同一应用的窗口合并成一个图标。
        var desired = new List<(string key, List<IntPtr> windows, string pin)>();
        var used = new HashSet<IntPtr>();
        foreach (var pin in Settings.Pinned)
        {
            var target = ResolvePinTarget(pin);
            var matches = target == null ? new List<IntPtr>() :
                _order.Where(h => !used.Contains(h) && SameExe(_paths[h], target)).ToList();
            used.UnionWith(matches);
            desired.Add(("pin:" + pin, matches, pin));
        }
        var groups = new Dictionary<string, List<IntPtr>>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in _order)
        {
            if (used.Contains(h)) continue;
            string key = GroupKey(h);
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = new List<IntPtr>();
                desired.Add((key, list, null));
            }
            list.Add(h);
        }

        for (int i = 0; i < desired.Count; i++)
        {
            var (key, windows, pin) = desired[i];
            int at = -1;
            for (int j = i; j < Items.Count; j++) if (Items[j].Key == key) { at = j; break; }

            TaskItem item;
            if (at >= 0)
            {
                if (at != i) Items.Move(at, i);
                item = Items[i];
            }
            else
            {
                item = pin != null ? CreatePlaceholder(key, pin) : CreateWindowItem(key, windows[0]);
                Items.Insert(i, item);
            }

            item.PinPath = pin;
            item.Windows = windows;
            var main = windows.Count == 0 ? IntPtr.Zero : windows.OrderByDescending(h => _lastActive.GetValueOrDefault(h)).First();
            item.Handle = main;
            if (main != IntPtr.Zero)
            {
                item.ProcessPath = _paths[main];
                string title = Native.GetText(main);
                item.Title = windows.Count > 1 ? $"{title}（{windows.Count} 个窗口）" : title;
                item.IsActive = windows.Contains(fg);
                item.IsMinimized = windows.All(Native.IsIconic);
                // 未固定的应用：图标跟随主窗口（第一次创建时已取过）
            }
            else
            {
                item.Title = PinTitle(pin);
                item.IsActive = item.IsMinimized = false;
            }
        }
        while (Items.Count > desired.Count) Items.RemoveAt(Items.Count - 1);
    }

    readonly Dictionary<IntPtr, long> _lastActive = new();
    long _activeClock;

    /// <summary>未固定窗口的分组键：同一 exe 合并；UWP（都由 ApplicationFrameHost 托管）和取不到路径的单独成组。</summary>
    string GroupKey(IntPtr h)
    {
        var path = _paths[h];
        if (!IsPinnable(path)) return "hwnd:" + h;
        return "app:" + FinalPath(path);
    }

    static string PinTitle(string pin) => Path.GetFileNameWithoutExtension(pin);

    TaskItem CreateWindowItem(string key, IntPtr h)
    {
        var path = _paths[h];
        return new TaskItem { Key = key, Handle = h, ProcessPath = path, Title = Native.GetText(h), Icon = ResolveIcon(h, path) };
    }

    TaskItem CreatePlaceholder(string key, string pin)
    {
        var target = ResolvePinTarget(pin);
        ImageSource icon = null;
        if (pin.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            // 优先用快捷方式自己指定的图标（网页应用等），不带快捷方式小箭头
            var info = Shortcut.Read(pin);
            if (info.IconPath != null && File.Exists(info.IconPath)) icon = ExtractLarge(info.IconPath, info.IconIndex);
        }
        icon ??= (target != null && File.Exists(target) ? ExtractLarge(target) : null) ?? ShellIcon(pin);
        return new TaskItem { Key = key, ProcessPath = target, Title = Path.GetFileNameWithoutExtension(pin), Icon = icon };
    }

    bool IsTaskWindow(IntPtr h)
    {
        if (!Native.IsWindowVisible(h) || Native.IsCloaked(h)) return false;
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && !appWindow) return false;
        if (Native.GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero && !appWindow) return false;
        if ((ex & Native.WS_EX_NOACTIVATE) != 0 && !appWindow) return false;
        if (Native.GetWindowTextLength(h) == 0) return false;
        if (IgnoredClasses.Contains(Native.GetClass(h))) return false;
        Native.GetWindowThreadProcessId(h, out uint pid);
        return pid != _ownPid;
    }

    ImageSource ResolveIcon(IntPtr h, string path)
    {
        // UWP 窗口由 ApplicationFrameHost 托管，尝试找到真正的应用进程
        if (path != null && path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            Native.GetWindowThreadProcessId(h, out uint hostPid);
            string inner = null;
            Native.EnumChildWindows(h, (c, _) =>
            {
                Native.GetWindowThreadProcessId(c, out uint p);
                if (p != hostPid) { inner = Native.GetProcessPath(c); return false; }
                return true;
            }, IntPtr.Zero);
            if (inner != null) path = inner;
        }

        if (path != null)
        {
            var img = ExtractLarge(path);
            if (img != null) return img;
        }
        return FromWindow(h);
    }

    ImageSource ExtractLarge(string path, int index = 0)
    {
        var cacheKey = index == 0 ? path : path + "," + index;
        if (_iconCache.TryGetValue(cacheKey, out var cached)) return cached;
        ImageSource result = null;
        try
        {
            var icons = new IntPtr[1];
            var ids = new int[1];
            if (PrivateExtractIcons(path, index, 64, 64, icons, ids, 1, 0) != 0 && icons[0] != IntPtr.Zero)
            {
                try { result = Freeze(Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions())); }
                finally { Native.DestroyIcon(icons[0]); }
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        if (result != null) _iconCache[cacheKey] = result;
        return result;
    }

    /// <summary>快捷方式（包括 UWP 应用的）用 Shell 取图标。</summary>
    static ImageSource ShellIcon(string path)
    {
        var info = new SHFILEINFO();
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), 0x100 /*SHGFI_ICON*/) == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;
        try { return Freeze(Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions())); }
        catch { return null; }
        finally { Native.DestroyIcon(info.hIcon); }
    }

    static ImageSource FromWindow(IntPtr h)
    {
        IntPtr icon = IntPtr.Zero;
        foreach (int kind in new[] { 1, 2, 0 })
        {
            Native.SendMessageTimeout(h, Native.WM_GETICON, new IntPtr(kind), IntPtr.Zero, 0x2, 100, out icon);
            if (icon != IntPtr.Zero) break;
        }
        if (icon == IntPtr.Zero) icon = Native.GetClassLongPtr(h, Native.GCLP_HICON);
        if (icon == IntPtr.Zero) return null;
        try { return Freeze(Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions())); }
        catch { return null; }
    }

    static ImageSource Freeze(BitmapSource b) { b.Freeze(); return b; }

    public static void Activate(TaskItem item)
    {
        if (!item.IsRunning)
        {
            if (item.PinPath != null) Launch(item.PinPath);
            return;
        }
        var h = item.Handle;
        var fg = Native.GetForegroundWindow();
        int cur = IndexOf(item.Windows, fg);
        if (cur >= 0 && item.Windows.Count > 1)
        {
            // 组内已有窗口在前台：轮换到下一个
            h = item.Windows[(cur + 1) % item.Windows.Count];
        }
        else if (fg == h && !Native.IsIconic(h))
        {
            Native.ShowWindowAsync(h, Native.SW_MINIMIZE);
            return;
        }
        Focus(h);
    }

    static int IndexOf(IReadOnlyList<IntPtr> list, IntPtr h)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == h) return i;
        return -1;
    }

    public static void Focus(IntPtr h)
    {
        if (Native.IsIconic(h)) Native.ShowWindowAsync(h, Native.SW_RESTORE);
        // 用 Alt 键绕过前台锁，否则 SetForegroundWindow 常常只闪烁
        Native.keybd_event(0x12, 0, 0, UIntPtr.Zero);
        Native.SetForegroundWindow(h);
        Native.keybd_event(0x12, 0, 2, UIntPtr.Zero);
    }

    /// <summary>打开该应用的新实例（固定项优先用快捷方式，保留启动参数）。</summary>
    public static void LaunchNew(TaskItem item)
    {
        var path = item.PinPath ?? item.ProcessPath;
        if (IsPinnable(path)) Launch(path);
    }

    /// <summary>关闭主窗口（中键）。</summary>
    public static void Close(TaskItem item)
    {
        if (item.IsRunning) Close(item.Handle);
    }

    public static void CloseAll(TaskItem item)
    {
        foreach (var h in item.Windows) Close(h);
    }

    public static void Close(IntPtr h) =>
        Native.PostMessage(h, Native.WM_SYSCOMMAND, new IntPtr(Native.SC_CLOSE), IntPtr.Zero);
}
