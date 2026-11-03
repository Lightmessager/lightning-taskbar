using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using ManagedShell;
using ManagedShell.Common.Helpers;
using ManagedShell.WindowsTray;
using Microsoft.Win32;

namespace LightningTaskbar;

/// <summary>
/// 系统托盘：借助 ManagedShell 接收各应用的 Shell_NotifyIcon 消息（同时转发给 Explorer），
/// 并把我们任务栏上的鼠标操作原样转发回图标所属的应用。
/// </summary>
internal sealed class TrayHost : IDisposable
{
    readonly ShellManager _shell;
    readonly HashSet<string> _promoted;

    public ICollectionView PinnedIcons => _shell.NotificationArea.PinnedIcons;
    public ICollectionView UnpinnedIcons => _shell.NotificationArea.UnpinnedIcons;

    static Settings Settings => WeatherEngine.Instance.Settings;

    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();

    public TrayHost()
    {
        var cfg = new ShellConfig
        {
            EnableTrayService = true,
            AutoStartTrayService = true,
            EnableTasksService = false,
            AutoStartTasksService = false,
            PinnedNotifyIcons = Settings.TrayPinned.ToArray(),
        };
        _shell = new ShellManager(cfg);

        // 首次运行：沿用 Windows 11 设置里"在任务栏上显示"的图标
        _promoted = Settings.TrayPinsImported ? new() : ReadPromotedApps();
        if (!Settings.TrayPinsImported)
        {
            _shell.NotificationArea.TrayIcons.CollectionChanged += ImportPromoted;
            foreach (var icon in _shell.NotificationArea.TrayIcons.ToList()) TryPromote(icon);
            // 30 秒内出现的图标都参与导入，之后定型
            var stop = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            stop.Tick += (_, _) =>
            {
                stop.Stop();
                _shell.NotificationArea.TrayIcons.CollectionChanged -= ImportPromoted;
                Settings.TrayPinsImported = true;
                SavePins();
            };
            stop.Start();
        }
    }

    static HashSet<string> ReadPromotedApps()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (root == null) return set;
            foreach (var name in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(name);
                if (k?.GetValue("IsPromoted") is int p && p == 1 && k.GetValue("ExecutablePath") is string path)
                    set.Add(ExpandKnownFolders(path));
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        return set;
    }

    /// <summary>注册表里路径的 {GUID} 前缀是已知文件夹 ID，这里只处理最常见的几个。</summary>
    static string ExpandKnownFolders(string path) => path
        .Replace("{6D809377-6AF0-444B-8957-A3773F02200E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))
        .Replace("{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))
        .Replace("{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", Environment.GetFolderPath(Environment.SpecialFolder.System))
        .Replace("{F38BF404-1D43-42F2-9305-67DE0B28FC23}", Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    void ImportPromoted(object sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems == null) return;
        foreach (NotifyIcon icon in e.NewItems) TryPromote(icon);
    }

    void TryPromote(NotifyIcon icon)
    {
        if (!icon.IsPinned && icon.Path != null && _promoted.Contains(icon.Path)) { icon.Pin(); SavePins(); }
    }

    public void TogglePin(NotifyIcon icon)
    {
        if (icon.IsPinned) icon.Unpin(); else icon.Pin();
        SavePins();
    }

    void SavePins()
    {
        Settings.TrayPinned = PinnedIcons.Cast<NotifyIcon>().Select(i => i.Identifier).Where(s => s != null).ToList();
        Settings.Save();
    }

    // ---------------- 鼠标转发 ----------------

    static uint CursorParam => MouseHelper.GetCursorPositionParam();

    public static void MouseDown(NotifyIcon icon, MouseButton button) =>
        icon.IconMouseDown(button, CursorParam, (int)GetDoubleClickTime());

    public static void MouseUp(NotifyIcon icon, MouseButton button) =>
        icon.IconMouseUp(button, CursorParam, (int)GetDoubleClickTime());

    public static void MouseEnter(NotifyIcon icon) => icon.IconMouseEnter(CursorParam);
    public static void MouseLeave(NotifyIcon icon) => icon.IconMouseLeave(CursorParam);
    public static void MouseMove(NotifyIcon icon) => icon.IconMouseMove(CursorParam);

    public void Dispose() => _shell.Dispose();
}
