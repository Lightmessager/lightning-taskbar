namespace LightningTaskbar;

/// <summary>隐藏/恢复系统原生任务栏，并接管工作区（让最大化窗口占满全屏）。</summary>
internal static class ShellTaskbar
{
    static Native.RECT? _originalWorkArea;
    static bool _hidden;

    /// <summary>Explorer 的任务栏窗口。托盘服务会在本进程里注册同名的 Shell_TrayWnd，必须排除。</summary>
    static List<IntPtr> TrayWindows()
    {
        var own = (uint)Environment.ProcessId;
        var list = new List<IntPtr>();
        Native.EnumWindows((h, _) =>
        {
            var cls = Native.GetClass(h);
            if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                Native.GetWindowThreadProcessId(h, out uint pid);
                if (pid != own) list.Add(h);
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    static IntPtr MainTray() =>
        TrayWindows().FirstOrDefault(h => Native.GetClass(h) == "Shell_TrayWnd");

    public static void Hide()
    {
        if (_originalWorkArea == null)
        {
            var wa = new Native.RECT();
            Native.SystemParametersInfo(Native.SPI_GETWORKAREA, 0, ref wa, 0);
            _originalWorkArea = wa;
        }
        _hidden = true;
        Enforce();
    }

    /// <summary>Explorer 偶尔会把任务栏重新显示出来（如按下 Win 键），定时调用以保持隐藏。</summary>
    public static void Enforce()
    {
        if (!_hidden) return;
        foreach (var h in TrayWindows())
            if (Native.IsWindowVisible(h)) Native.ShowWindow(h, Native.SW_HIDE);

        // Explorer 重启后会把工作区改回去
        var wa = new Native.RECT();
        Native.SystemParametersInfo(Native.SPI_GETWORKAREA, 0, ref wa, 0);
        var full = Native.GetPrimaryMonitorRect();
        if (wa.Bottom != full.Bottom || wa.Top != full.Top || wa.Left != full.Left || wa.Right != full.Right)
            Native.SystemParametersInfo(Native.SPI_SETWORKAREA, 0, ref full, Native.SPIF_SENDCHANGE);
    }

    public static void Restore()
    {
        if (!_hidden) return;
        _hidden = false;
        foreach (var h in TrayWindows()) Native.ShowWindow(h, Native.SW_SHOW);

        // 按系统任务栏的实际位置重算工作区（启动时记录的值可能来自上次被强杀后的全屏状态）
        var wa = Native.GetPrimaryMonitorRect();
        var tray = MainTray();
        if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out var t))
        {
            if (t.Top > wa.Top + wa.Height / 2) wa.Bottom = t.Top;
            else if (t.Bottom < wa.Top + wa.Height / 2 && t.Width > t.Height) wa.Top = t.Bottom;
            else if (t.Left > wa.Left + wa.Width / 2) wa.Right = t.Left;
            else wa.Left = t.Right;
        }
        else if (_originalWorkArea is Native.RECT orig) wa = orig;
        Native.SystemParametersInfo(Native.SPI_SETWORKAREA, 0, ref wa, Native.SPIF_SENDCHANGE);
    }
}
