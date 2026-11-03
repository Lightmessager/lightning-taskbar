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
internal sealed unsafe class BackdropCapture : IDisposable
{
    public const int Downsample = 4;

    IntPtr _memDC, _dib, _bits, _oldObj;
    int _w, _h, _fw, _fh;
    uint[] _small = Array.Empty<uint>(), _tmp = Array.Empty<uint>();
    ulong _lastHash;

    public WriteableBitmap Sharp { get; private set; }
    public WriteableBitmap Frost { get; private set; }
    /// <summary>截取区域相对玻璃元素四周多出的像素。</summary>
    public int Margin { get; set; } = 48;
    /// <summary>背景平均亮度 0..1（取自磨砂图，已平滑）。</summary>
    public double Luminance { get; private set; } = -1;

    /// <summary>截取屏幕矩形（物理像素）。返回 true 表示纹理对象被重建，需要重新绑定。</summary>
    public bool Capture(int x, int y, int w, int h, double frostRadiusPx)
    {
        if (w <= 0 || h <= 0) return false;
        bool rebuilt = Ensure(w, h);

        IntPtr screen = GetDC(IntPtr.Zero);
        BitBlt(_memDC, 0, 0, w, h, screen, x, y, 0x00CC0020 /*SRCCOPY*/);
        ReleaseDC(IntPtr.Zero, screen);

        DownsampleInto();
        ulong hash = Hash();
        if (hash == _lastHash && !rebuilt) return false;   // 背景没变就不重复上传
        _lastHash = hash;

        BoxBlur3(frostRadiusPx / Downsample);
        UpdateLuminance();

        Sharp.WritePixels(new Int32Rect(0, 0, w, h), _bits, w * h * 4, w * 4);
        fixed (uint* p = _small) Frost.WritePixels(new Int32Rect(0, 0, _fw, _fh), (IntPtr)p, _fw * _fh * 4, _fw * 4);
        return rebuilt;
    }

    bool Ensure(int w, int h)
    {
        if (w == _w && h == _h && Sharp != null) return false;
        FreeDib();
        _w = w; _h = h;
        _fw = (w + Downsample - 1) / Downsample;
        _fh = (h + Downsample - 1) / Downsample;

        var bmi = new BITMAPINFO { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr screen = GetDC(IntPtr.Zero);
        _memDC = CreateCompatibleDC(screen);
        ReleaseDC(IntPtr.Zero, screen);
        _dib = CreateDIBSection(_memDC, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        _oldObj = SelectObject(_memDC, _dib);

        _small = new uint[_fw * _fh];
        _tmp = new uint[_fw * _fh];
        Sharp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
        Frost = new WriteableBitmap(_fw, _fh, 96, 96, PixelFormats.Bgr32, null);
        _lastHash = 0;
        return true;
    }

    /// <summary>4×4 平均降采样。</summary>
    void DownsampleInto()
    {
        byte* src = (byte*)_bits;
        for (int sy = 0; sy < _fh; sy++)
        {
            int y0 = sy * Downsample, y1 = Math.Min(_h, y0 + Downsample);
            for (int sx = 0; sx < _fw; sx++)
            {
                int x0 = sx * Downsample, x1 = Math.Min(_w, x0 + Downsample);
                int b = 0, g = 0, r = 0, n = 0;
                for (int y = y0; y < y1; y++)
                {
                    byte* row = src + (y * _w + x0) * 4;
                    for (int x = x0; x < x1; x++, row += 4) { b += row[0]; g += row[1]; r += row[2]; n++; }
                }
                _small[sy * _fw + sx] = (uint)(b / n | (g / n) << 8 | (r / n) << 16);
            }
        }
    }

    ulong Hash()
    {
        ulong h = 1469598103934665603UL;
        for (int i = 0; i < _small.Length; i += 3) h = (h ^ _small[i]) * 1099511628211UL;
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
        _memDC = _dib = _bits = IntPtr.Zero;
        _w = _h = 0;
        Sharp = Frost = null;
    }

    public void Dispose() => FreeDib();

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant, bmiColors; }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
}
