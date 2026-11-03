using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LightningTaskbar.StartMenu;

public sealed class AppEntry : INotifyPropertyChanged
{
    public string Name { get; init; }
    /// <summary>shell:AppsFolder 里的解析名：桌面程序是路径，UWP 是 AUMID；也可以直接是 .lnk 的完整路径。</summary>
    public string ParsingName { get; init; }

    /// <summary>可交给 Shell 打开 / 取图标的目标。</summary>
    public string ShellTarget => System.IO.Path.IsPathRooted(ParsingName) ? ParsingName : "shell:AppsFolder\\" + ParsingName;

    ImageSource _icon;
    public ImageSource Icon { get => _icon; set { _icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); } }

    public event PropertyChangedEventHandler PropertyChanged;
}

/// <summary>
/// 从 shell:AppsFolder 读取“所有应用”（与系统开始菜单同源，包含 UWP / 商店应用），
/// 图标用 IShellItemImageFactory 取，保留透明通道。
/// </summary>
internal static class AppCatalog
{
    public static Task<List<AppEntry>> LoadAsync() => RunSta(() =>
    {
        var list = new List<AppEntry>();
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            dynamic folder = shell.NameSpace("shell:::{4234d49b-0245-4df3-b780-3893943456e1}");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name, path = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                // 过滤卸载程序、说明文档之类的噪音
                if (name.Contains("卸载") || name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new AppEntry { Name = name, ParsingName = path });
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        return list.GroupBy(a => a.Name).Select(g => g.First())
                   .OrderBy(a => a.Name, StringComparer.Create(new System.Globalization.CultureInfo("zh-CN"), true)).ToList();
    });

    /// <summary>后台线程批量取图标，取到一个就回填一个。</summary>
    public static void LoadIconsAsync(IReadOnlyList<AppEntry> apps, Dispatcher dispatcher, int size)
    {
        RunSta(() =>
        {
            foreach (var app in apps)
            {
                if (app.Icon != null) continue;
                var img = GetIcon(app.ShellTarget, size);
                if (img != null) dispatcher.BeginInvoke(() => app.Icon = img);
            }
            return 0;
        });
    }

    public static void Launch(AppEntry app)
    {
        try
        {
            if (System.IO.Path.IsPathRooted(app.ParsingName))
                Process.Start(new ProcessStartInfo(app.ParsingName) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + app.ParsingName) { UseShellExecute = true });
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    /// <summary>
    /// 读取 Windows 开始菜单“已固定”的应用（顺序一致）。用系统自带的 Export-StartLayout 导出 JSON，
    /// 再尽量映射到“所有应用”里的同一项，以便复用名称与图标。
    /// </summary>
    public static async Task<List<AppEntry>> LoadPinnedAsync(IReadOnlyList<AppEntry> all)
    {
        var json = Path.Combine(Path.GetTempPath(), "LightningTaskbar-startlayout.json");
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Export-StartLayout -Path '{json}'\"")
            { CreateNoWindow = true, UseShellExecute = false };
            using (var p = Process.Start(psi)) await p!.WaitForExitAsync();
            if (!File.Exists(json)) return null;

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(json, Encoding.UTF8));
            var byId = all.GroupBy(a => a.ParsingName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var byName = all.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var result = new List<AppEntry>();

            foreach (var item in doc.RootElement.GetProperty("pinnedList").EnumerateArray())
            {
                AppEntry e = null;
                if (item.TryGetProperty("packagedAppId", out var pid) || item.TryGetProperty("desktopAppId", out pid))
                {
                    var id = pid.GetString();
                    if (!byId.TryGetValue(id, out e)) e = null;   // 已卸载的应用直接跳过
                }
                else if (item.TryGetProperty("desktopAppLink", out var link))
                {
                    var path = Environment.ExpandEnvironmentVariables(link.GetString());
                    var name = Path.GetFileNameWithoutExtension(path);
                    if (!byName.TryGetValue(name, out e) && File.Exists(path))
                        e = new AppEntry { Name = name, ParsingName = path };
                }
                if (e != null && !result.Contains(e)) result.Add(e);
            }
            return result;
        }
        catch (Exception ex) { Debug.WriteLine(ex); return null; }
    }

    static Task<T> RunSta<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>();
        var t = new Thread(() =>
        {
            try { tcs.SetResult(work()); } catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    // ---------------- 图标 ----------------

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, ref BITMAP bmp);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bmi, uint usage);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

    static ImageSource GetIcon(string parsingName, int size)
    {
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory);
            // SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK
            if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x4 | 0x1, out hbmp) != 0 || hbmp == IntPtr.Zero) return null;

            var bm = new BITMAP();
            GetObject(hbmp, Marshal.SizeOf<BITMAP>(), ref bm);
            int w = bm.bmWidth, h = bm.bmHeight;
            var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var px = new byte[w * h * 4];
            IntPtr dc = GetDC(IntPtr.Zero);
            GetDIBits(dc, hbmp, 0, (uint)h, px, ref bmi, 0);
            ReleaseDC(IntPtr.Zero, dc);

            // 部分图标没有 alpha（全 0），视为不透明
            bool hasAlpha = false;
            for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { hasAlpha = true; break; }
            if (!hasAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255;

            var src = BitmapSource.Create(w, h, 96, 96, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, px, w * 4);
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { if (hbmp != IntPtr.Zero) DeleteObject(hbmp); }
    }
}
