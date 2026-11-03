using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace LightningTaskbar.Desktop;

/// <summary>
/// 把雷雨场景挂到桌面的 WorkerW 层：位于桌面图标之下、壁纸之上。
/// 不创建任何覆盖在其它窗口上的顶层窗口，所以不遮挡、不拦截鼠标。
/// </summary>
internal sealed class DesktopLayer : IDisposable
{
    HwndSource _source;
    IntPtr _workerW;
    readonly RainScene _scene = new();
    readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(2) };

    public RainScene Scene => _scene;

    public void Attach()
    {
        Create();
        _watchdog.Tick += (_, _) =>
        {
            // Explorer 重启后 WorkerW 会被销毁，需要重新挂载
            if (_workerW == IntPtr.Zero || !Native.IsWindow(_workerW)) { Destroy(); Create(); }
            else BringToTop();
        };
        _watchdog.Start();
    }

    static IntPtr FindWorkerW()
    {
        IntPtr progman = Native.FindWindow("Progman", null);
        // 让 Progman 在壁纸与图标之间生成一个 WorkerW（Wallpaper Engine 等同样做法）
        Native.SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 0, 1000, out _);
        Native.SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        IntPtr worker = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            if (Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                worker = Native.FindWindowEx(IntPtr.Zero, h, "WorkerW", null);
            return true;
        }, IntPtr.Zero);

        // Windows 11 24H2 起 WorkerW 变成了 Progman 的子窗口
        if (worker == IntPtr.Zero)
            worker = Native.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        return worker;
    }

    void Create()
    {
        _workerW = FindWorkerW();
        if (_workerW == IntPtr.Zero) return;

        Native.GetWindowRect(_workerW, out var host);
        var mon = Native.GetPrimaryMonitorRect();

        // 已有动态壁纸（Wallpaper Engine 等）挂在同一个 WorkerW 里时，雨层改为逐像素透明叠在它上面
        bool liveWallpaper = false;
        Native.EnumChildWindows(_workerW, (_, _) => { liveWallpaper = true; return false; }, IntPtr.Zero);
        _scene.ShowWallpaper = !liveWallpaper;

        var p = new HwndSourceParameters("LightningTaskbar.DesktopRain")
        {
            ParentWindow = _workerW,
            WindowStyle = unchecked((int)(Native.WS_CHILD | 0x10000000 /*WS_VISIBLE*/ | 0x04000000 /*WS_CLIPSIBLINGS*/)),
            ExtendedWindowStyle = 0x20 /*WS_EX_TRANSPARENT：鼠标穿透到壁纸*/,
            PositionX = mon.Left - host.Left,
            PositionY = mon.Top - host.Top,
            Width = mon.Width,
            Height = mon.Height,
            UsesPerPixelTransparency = liveWallpaper,
        };
        _source = new HwndSource(p) { RootVisual = _scene, SizeToContent = SizeToContent.Manual };
        if (liveWallpaper) _source.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
        BringToTop();
        _scene.Start();
    }

    /// <summary>动态壁纸可能会重新抬高自己的 Z 序，定期把雨层放回最上面。</summary>
    void BringToTop()
    {
        if (_source != null)
            Native.SetWindowPos(_source.Handle, IntPtr.Zero /*HWND_TOP*/, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void SetVisible(bool visible)
    {
        if (_source == null) return;
        Native.ShowWindow(_source.Handle, visible ? Native.SW_SHOWNA : Native.SW_HIDE);
    }

    void Destroy()
    {
        _scene.Stop();
        if (_source != null)
        {
            _source.RootVisual = null;
            _source.Dispose();
            _source = null;
        }
    }

    public void Dispose()
    {
        _watchdog.Stop();
        Destroy();
        // 让 Explorer 重绘真实壁纸，避免残留最后一帧
        var path = Native.GetWallpaperPath();
        SystemParametersInfoSet(0x14 /*SPI_SETDESKWALLPAPER*/, 0, path, 0);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "SystemParametersInfoW")]
    static extern bool SystemParametersInfoSet(uint action, uint param, string value, uint winIni);
}
