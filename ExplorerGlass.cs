using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace LightningTaskbar;

/// <summary>
/// 为文件资源管理器窗口设置亚克力背景与深色模式（仅调用 DWM API，不注入 DLL）。
/// </summary>
internal static class ExplorerGlass
{
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const int OBJID_WINDOW = 0;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_MAINWINDOW = 2;       // Mica，Explorer 默认值
    private const int DWMSBT_TRANSIENTWINDOW = 3;  // 亚克力
    private const string TargetClassName = "CabinetWClass";

    private delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // 委托必须存在静态字段中，否则会被 GC 回收导致回调崩溃
    private static WinEventProc _winEventProc;
    private static IntPtr _hook = IntPtr.Zero;
    private static readonly HashSet<IntPtr> _handled = new HashSet<IntPtr>();

    /// <summary>启动：处理已存在的资源管理器窗口，并挂钩后续出现的窗口。</summary>
    public static void Start()
    {
        try
        {
            if (_hook != IntPtr.Zero) return;

            // 处理已存在的窗口
            EnumWindows((hWnd, lParam) =>
            {
                TryApply(hWnd);
                return true;
            }, IntPtr.Zero);

            // 挂钩新出现的窗口（out-of-context，依赖调用线程的消息循环）
            _winEventProc = OnWinEvent;
            _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero,
                _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT);
        }
        catch
        {
            // 静默失败，不影响主程序
        }
    }

    /// <summary>停止：卸载钩子，并把已处理窗口的背景恢复为 Mica。深色模式不恢复。</summary>
    public static void Stop()
    {
        try
        {
            if (_hook != IntPtr.Zero)
            {
                UnhookWinEvent(_hook);
                _hook = IntPtr.Zero;
            }
        }
        catch
        {
        }

        try
        {
            foreach (IntPtr hwnd in new List<IntPtr>(_handled))
            {
                try
                {
                    if (!IsWindow(hwnd)) continue;
                    int backdrop = DWMSBT_MAINWINDOW;
                    DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        _handled.Clear();
        _winEventProc = null;
    }

    private static void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero) return;
            TryApply(hwnd);
        }
        catch
        {
        }
    }

    private static void TryApply(IntPtr hwnd)
    {
        try
        {
            // 清理已销毁的句柄
            _handled.RemoveWhere(h => !IsWindow(h));

            if (_handled.Contains(hwnd)) return;
            if (!IsWindow(hwnd) || GetClass(hwnd) != TargetClassName) return;

            int dark = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            int backdrop = DWMSBT_TRANSIENTWINDOW;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

            _handled.Add(hwnd);
        }
        catch
        {
        }
    }

    private static string GetClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        int len = GetClassName(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString(0, len) : string.Empty;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);
}
