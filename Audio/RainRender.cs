using System.IO;
using System.Numerics;
using System.Text;

namespace LightningTaskbar.Audio;

/// <summary>
/// 开发用：把雨声离线渲染成 WAV，并输出电平与频段能量报告（用于检查底噪和刺耳频段）。
/// 用法：LightningTaskbar.exe --render-rain 输出.wav [雨量0..1]
/// </summary>
internal static class RainRender
{
    const int SR = 44100;

    public static void Run(string wavPath, float rain)
    {
        int seconds = 20;
        int n = SR * seconds;
        var synth = new RainSynth(12345);
        var l = new float[n];
        var r = new float[n];
        for (int i = 0; i < n; i++) synth.Next(rain, out l[i], out r[i]);

        WriteWav(wavPath, l, r);
        File.WriteAllText(Path.ChangeExtension(wavPath, ".txt"), Analyze(l, rain), Encoding.UTF8);
    }

    /// <summary>
    /// 70 秒完整场景：雷雨 + 一阵风（10~50 秒）+ 三声雷（5、30、55 秒），
    /// 并单独分析雷声与风声的频谱、起音陡峭程度。
    /// </summary>
    public static void RunScene(string wavPath)
    {
        int n = SR * 70;
        var rain = new RainSynth(777);
        var wind = new WindSynth(778);
        var l = new float[n];
        var r = new float[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SR;
            float gust = (float)(t < 10 ? 0 : t < 20 ? (t - 10) / 10 * 0.9 : t < 40 ? 0.9 : t < 50 ? (50 - t) / 10 * 0.9 : 0);
            rain.Next(1f + 0.25f * gust, out var a, out var b);
            wind.Next(gust, out var c, out var d);
            l[i] = a + c; r[i] = b + d;
        }
        var thunders = new[] { (5.0, 0.9), (30.0, 0.5), (55.0, 1.0) };
        foreach (var (at, inten) in thunders)
        {
            var th = ThunderSynth.Generate(inten, 0.5, (int)(at * 100));
            int s = (int)(at * SR);
            for (int i = 0; i * 2 + 1 < th.Length && s + i < n; i++) { l[s + i] += th[i * 2]; r[s + i] += th[i * 2 + 1]; }
        }
        WriteWav(wavPath, l, r);

        var sb = new StringBuilder();
        sb.AppendLine("== 每 5 秒平均响度 (dBFS) ==");
        for (int w = 0; w < 14; w++)
        {
            double sum = 0;
            for (int i = w * 5 * SR; i < (w + 1) * 5 * SR; i++) sum += l[i] * l[i];
            sb.Append($"{w * 5,2}s {10 * Math.Log10(sum / (5 * SR) + 1e-12),6:F1}   ");
            if (w % 4 == 3) sb.AppendLine();
        }
        sb.AppendLine();

        sb.AppendLine($"整段混音（雷声叠在雨里，实际听感）：200ms 内最大上升 {MaxRise(l, SR / 5):F1} dB，50ms 内 {MaxRise(l, SR / 20):F1} dB");
        sb.AppendLine("== 单独的雷声（强度 1.0，最近最响）==");
        var t1 = ThunderSynth.Generate(1.0, 0.5, 4242);
        var mono = new float[t1.Length / 2];
        for (int i = 0; i < mono.Length; i++) mono[i] = t1[i * 2];
        sb.Append(Analyze(mono, 1));
        sb.AppendLine($"起音最陡：50ms 内最大上升 {MaxRise(mono, SR / 20):F1} dB（含低频噪声自身的质感起伏）");
        sb.AppendLine($"整体起落：200ms 内最大上升 {MaxRise(mono, SR / 5):F1} dB（<6dB 基本听不出“突然”）");

        sb.AppendLine("== 单独的风声（阵风 0.9，稳定后 20 秒）==");
        var ws = new WindSynth(99);
        var wm = new float[SR * 30];
        for (int i = 0; i < wm.Length; i++) ws.Next(0.9f, out wm[i], out _);
        sb.Append(Analyze(wm[(SR * 10)..], 0.9f));

        File.WriteAllText(Path.ChangeExtension(wavPath, ".txt"), sb.ToString(), Encoding.UTF8);
    }

    static double MaxRise(float[] x, int win)
    {
        double prev = -120, max = 0;
        for (int s = 0; s + win <= x.Length; s += win)
        {
            double sum = 0;
            for (int i = s; i < s + win; i++) sum += x[i] * x[i];
            double db = 10 * Math.Log10(sum / win + 1e-12);
            // 只统计能听见的部分（避免把“从完全静音起步”的几毫秒算成突变）
            if (prev > -60 && db > -40) max = Math.Max(max, db - prev);
            prev = db;
        }
        return max;
    }

    static void WriteWav(string path, float[] l, float[] r)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = l.Length * 4;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(SR); w.Write(SR * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        // 与实时播放一致：音量 0.6 × 1.2 后 tanh
        for (int i = 0; i < l.Length; i++)
        {
            w.Write((short)(MathF.Tanh(l[i] * 0.72f) * 32000));
            w.Write((short)(MathF.Tanh(r[i] * 0.72f) * 32000));
        }
    }

    static string Analyze(float[] x, float rain)
    {
        var sb = new StringBuilder();
        double sum = 0, peak = 0;
        foreach (var v in x) { sum += v * v; peak = Math.Max(peak, Math.Abs(v)); }
        double rms = Math.Sqrt(sum / x.Length);
        sb.AppendLine($"雨量 {rain}  RMS {20 * Math.Log10(rms + 1e-12):F1} dBFS  峰值 {20 * Math.Log10(peak + 1e-12):F1} dBFS  峰均比 {20 * Math.Log10(peak / rms):F1} dB");

        // 平均功率谱（4096 点 FFT，Hann 窗）
        const int N = 4096;
        var power = new double[N / 2];
        int frames = 0;
        var buf = new Complex[N];
        for (int start = 0; start + N <= x.Length; start += N)
        {
            for (int i = 0; i < N; i++)
                buf[i] = new Complex(x[start + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1))), 0);
            Fft(buf);
            for (int k = 0; k < N / 2; k++) power[k] += buf[k].Magnitude * buf[k].Magnitude;
            frames++;
        }

        (int lo, int hi, string name)[] bands =
        {
            (20, 250, "低频 20-250"), (250, 1000, "中低 250-1k"), (1000, 3000, "中频 1k-3k"),
            (3000, 6000, "中高 3k-6k"), (6000, 12000, "高频 6k-12k"), (12000, 20000, "极高 12k-20k"),
        };
        double total = power.Sum();
        foreach (var (lo, hi, name) in bands)
        {
            double e = 0;
            for (int k = lo * N / SR; k < hi * N / SR && k < power.Length; k++) e += power[k];
            sb.AppendLine($"{name,-14} {100 * e / total,6:F1}%");
        }

        // 音调性：最强频点相对其邻域中值，>12 dB 说明有可闻的“音高”
        double maxRatio = 0; int maxK = 0;
        for (int k = 20; k < power.Length - 20; k++)
        {
            var nb = new List<double>();
            for (int j = -20; j <= 20; j++) if (Math.Abs(j) > 2) nb.Add(power[k + j]);
            nb.Sort();
            double ratio = power[k] / (nb[nb.Count / 2] + 1e-20);
            if (ratio > maxRatio) { maxRatio = ratio; maxK = k; }
        }
        sb.AppendLine($"最突出单频 {maxK * SR / N} Hz，高出邻域 {10 * Math.Log10(maxRatio):F1} dB");
        return sb.ToString();
    }

    static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            var w = Complex.FromPolarCoordinates(1, -2 * Math.PI / len);
            for (int i = 0; i < n; i += len)
            {
                Complex wn = 1;
                for (int k = 0; k < len / 2; k++)
                {
                    var u = a[i + k];
                    var v = a[i + k + len / 2] * wn;
                    a[i + k] = u + v;
                    a[i + k + len / 2] = u - v;
                    wn *= w;
                }
            }
        }
    }
}
