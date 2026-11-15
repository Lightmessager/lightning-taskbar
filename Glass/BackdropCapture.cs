using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LightningTaskbar.Glass;

/// <summary>
/// 截取玻璃背后的屏幕画面，产出两张纹理：
/// <list type="bullet">
/// <item><see cref="Sharp"/>：原分辨率，用于折射和水珠里的倒影；</item>
/// <item><see cref="Frost"/>：1/4 分辨率 + 三次盒式模糊（≈ 高斯），GPU 双线性放大后就是平滑的磨砂。</item>
/// </list>
/// 截取范围比玻璃本身大一圈（<see cref="Margin"/>），折射和模糊在边缘也有真实像素可取，不会出现拉丝。
/// </summary>
internal sealed class BackdropCapture : IDisposable
{
    public const int Downsample = 4;
    /// <summary>纹理尺寸按这个粒度向上取整（必须是 Downsample 的倍数）。</summary>
    const int Bucket = 64;
    /// <summary>单张截图的像素上限，超过说明布局数值异常，直接不截。</summary>
    const long MaxPixels = 8192L * 2048;

    IntPtr _memDC, _dib, _bits, _oldObj;
    // _w/_h：这一帧实际截取的尺寸；_capW/_capH：纹理（和 DIB）的容量，只增不减
    int _w, _h, _fw, _fh, _capW, _capH;
    byte[] _pixels = Array.Empty<byte>();
    uint[] _small = Array.Empty<uint>(), _tmp = Array.Empty<uint>();
    ulong _lastHash;
    int _lastW, _lastH;
    DateTime _retryAt = DateTime.MinValue;

    public WriteableBitmap Sharp { get; private set; }
    public WriteableBitmap Frost { get; private set; }
    /// <summary>纹理的像素尺寸。只有左上角“这一帧截取的尺寸”那一块是有效画面，着色器按这个尺寸换算 uv。</summary>
    public int TextureWidth => _capW;
    public int TextureHeight => _capH;
    /// <summary>截取区域相对玻璃元素四周多出的像素。</summary>
    public int Margin { get; set; } = 48;
    /// <summary>背景平均亮度 0..1（取自磨砂图，已平滑）。</summary>
    public double Luminance { get; private set; } = -1;

    /// <summary>
    /// 截取屏幕矩形（物理像素）。返回 true 表示纹理对象被重建，需要重新绑定。
    /// <para>
    /// 纹理按 <see cref="Bucket"/> 取整、只增不减：玻璃尺寸连续变化（Dock 增减图标、通知卡片跟着内容伸缩）时
    /// 不会每一帧都销毁重建两张 WriteableBitmap 和一个 DIB——那会让 WPF 渲染线程以每秒几十次的频率
    /// 创建、释放显存纹理，是渲染线程崩溃的高危操作。
    /// </para>
    /// <para>像素一律经过托管数组并做边界检查，尺寸算错只会抛托管异常，不会写坏内存。</para>
    /// </summary>
    public bool Capture(int x, int y, int w, int h, double frostRadiusPx)
    {
        if (w <= 0 || h <= 0 || (long)w * h > MaxPixels) return false;
        if (!(frostRadiusPx >= 0) || double.IsInfinity(frostRadiusPx)) frostRadiusPx = 0;
        if (!Ensure(w, h, out bool rebuilt)) return false;
        bool resized = w != _lastW || h != _lastH;
        _w = w; _h = h;
        _fw = (w + Downsample - 1) / Downsample;
        _fh = (h + Downsample - 1) / Downsample;

        IntPtr screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return rebuilt;
        bool ok = BitBlt(_memDC, 0, 0, w, h, screen, x, y, 0x00CC0020 /*SRCCOPY*/);
        ReleaseDC(IntPtr.Zero, screen);
        // 锁屏、UAC 安全桌面、显示器正在切换时截不到，保留上一帧
        if (!ok) return rebuilt;
        GdiFlush();

        int stride = _capW * 4;
        Marshal.Copy(_bits, _pixels, 0, (h - 1) * stride + w * 4);

        DownsampleInto();
        ulong hash = Hash();
        if (hash == _lastHash && !rebuilt && !resized) return false;   // 背景没变就不重复上传
        _lastHash = hash; _lastW = w; _lastH = h;

        BoxBlur3(frostRadiusPx / Downsample);
        UpdateLuminance();

        Sharp.WritePixels(new Int32Rect(0, 0, w, h), _pixels, stride, 0);
        Frost.WritePixels(new Int32Rect(0, 0, _fw, _fh), _small, _fw * 4, 0);
        return rebuilt;
    }

    /// <summary>保证纹理容量够用。返回 false 表示这一帧截不了（GDI 资源申请失败，稍后重试）。</summary>
    bool Ensure(int w, int h, out bool rebuilt)
    {
        rebuilt = false;
        if (Sharp != null && w <= _capW && h <= _capH) return true;
        if (DateTime.UtcNow < _retryAt) return false;

        int capW = Math.Max(_capW, (w + Bucket - 1) / Bucket * Bucket);
        int capH = Math.Max(_capH, (h + Bucket - 1) / Bucket * Bucket);
        FreeDib();

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memDC = screen != IntPtr.Zero ? CreateCompatibleDC(screen) : IntPtr.Zero;
        if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        var bmi = new BITMAPINFO { biSize = 40, biWidth = capW, biHeight = -capH, biPlanes = 1, biBitCount = 32 };
        IntPtr bits = IntPtr.Zero;
        IntPtr dib = memDC != IntPtr.Zero ? CreateDIBSection(memDC, ref bmi, 0, out bits, IntPtr.Zero, 0) : IntPtr.Zero;
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            // GDI 句柄或内存不足：不要拿着空指针继续，过几秒再试
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memDC != IntPtr.Zero) DeleteDC(memDC);
            _retryAt = DateTime.UtcNow.AddSeconds(5);
            return false;
        }
        _memDC = memDC; _dib = dib; _bits = bits;
        _oldObj = SelectObject(_memDC, _dib);
        _capW = capW; _capH = capH;

        int capFw = capW / Downsample, capFh = capH / Downsample;
        _pixels = new byte[capW * capH * 4];
        _small = new uint[capFw * capFh];
        _tmp = new uint[capFw * capFh];
        Sharp = new WriteableBitmap(capW, capH, 96, 96, PixelFormats.Bgr32, null);
        Frost = new WriteableBitmap(capFw, capFh, 96, 96, PixelFormats.Bgr32, null);
        _lastHash = 0;
        rebuilt = true;
        return true;
    }

    /// <summary>4×4 平均降采样。</summary>
    void DownsampleInto()
    {
        byte[] src = _pixels;
        int stride = _capW * 4;
        for (int sy = 0; sy < _fh; sy++)
        {
            int y0 = sy * Downsample, y1 = Math.Min(_h, y0 + Downsample);
            for (int sx = 0; sx < _fw; sx++)
            {
                int x0 = sx * Downsample, x1 = Math.Min(_w, x0 + Downsample);
                int b = 0, g = 0, r = 0, n = 0;
                for (int y = y0; y < y1; y++)
                {
                    int i = y * stride + x0 * 4;
                    for (int x = x0; x < x1; x++, i += 4) { b += src[i]; g += src[i + 1]; r += src[i + 2]; n++; }
                }
                _small[sy * _fw + sx] = (uint)(b / n | (g / n) << 8 | (r / n) << 16);
            }
        }
    }

    ulong Hash()
    {
        ulong h = 1469598103934665603UL;
        for (int i = 0, n = _fw * _fh; i < n; i += 3) h = (h ^ _small[i]) * 1099511628211UL;
        return h;
    }

    /// <summary>三遍盒式模糊（横 + 竖），近似 σ = radius 的高斯。</summary>
    void BoxBlur3(double sigma)
    {
        int r = Math.Max(1, (int)Math.Round(sigma - 0.5));
        for (int pass = 0; pass < 3; pass++)
        {
            BlurLine(_small, _tmp, _fw, _fh, 1, _fw, r);   // 横向：small → tmp
            BlurLine(_tmp, _small, _fh, _fw, _fw, 1, r);   // 纵向：tmp → small
        }
    }

    /// <param name="len">每条线的像素数</param><param name="lines">线条数</param>
    /// <param name="step">线内相邻像素的下标间隔</param><param name="lineStep">相邻线起点的下标间隔</param>
    static void BlurLine(uint[] src, uint[] dst, int len, int lines, int step, int lineStep, int r)
    {
        int win = r * 2 + 1;
        for (int l = 0; l < lines; l++)
        {
            int start = l * lineStep;
            int sb = 0, sg = 0, sr = 0;
            for (int i = -r; i <= r; i++)
            {
                uint c = src[start + Math.Clamp(i, 0, len - 1) * step];
                sb += (int)(c & 0xFF); sg += (int)(c >> 8 & 0xFF); sr += (int)(c >> 16 & 0xFF);
            }
            for (int i = 0; i < len; i++)
            {
                dst[start + i * step] = (uint)(sb / win | (sg / win) << 8 | (sr / win) << 16);
                uint add = src[start + Math.Min(i + r + 1, len - 1) * step];
                uint sub = src[start + Math.Max(i - r, 0) * step];
                sb += (int)(add & 0xFF) - (int)(sub & 0xFF);
                sg += (int)(add >> 8 & 0xFF) - (int)(sub >> 8 & 0xFF);
                sr += (int)(add >> 16 & 0xFF) - (int)(sub >> 16 & 0xFF);
            }
        }
    }

    void UpdateLuminance()
    {
        // 只统计玻璃本体下方（去掉四周 margin）
        int m = Margin / Downsample;
        long sum = 0; int n = 0;
        for (int y = m; y < _fh - m; y++)
            for (int x = m; x < _fw - m; x += 2)
            {
                uint c = _small[y * _fw + x];
                sum += ((c >> 16 & 0xFF) * 54 + (c >> 8 & 0xFF) * 183 + (c & 0xFF) * 19) >> 8;
                n++;
            }
        if (n == 0) return;
        double lum = sum / (double)n / 255;
        Luminance = Luminance < 0 ? lum : Luminance + (lum - Luminance) * 0.2;
    }

    void FreeDib()
    {
        if (_memDC != IntPtr.Zero)
        {
            SelectObject(_memDC, _oldObj);
            DeleteObject(_dib);
            DeleteDC(_memDC);
        }
        _memDC = _dib = _bits = _oldObj = IntPtr.Zero;
        _w = _h = _fw = _fh = _lastW = _lastH = 0;
        Sharp = Frost = null;
    }

    /// <summary>释放 GDI 资源；之后再调用 Capture 会重新申请。</summary>
    public void Dispose() { FreeDib(); _capW = _capH = 0; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant, bmiColors; }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
}
