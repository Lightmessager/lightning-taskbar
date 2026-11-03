using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace LightningTaskbar.Audio;

/// <summary>
/// 纯程序化的雷雨音效：winmm waveOut 低延迟推流，雨声实时合成，雷声按闪电参数离线合成后混入。
/// 不依赖任何音频文件或第三方库。
/// </summary>
public sealed unsafe class StormAudio : IDisposable
{
    const int SampleRate = 44100;
    const int Frames = 2048;          // 每个缓冲 ~46ms
    const int BufferCount = 4;

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEFORMATEX { public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec; public ushort nBlockAlign, wBitsPerSample, cbSize; }

    [StructLayout(LayoutKind.Sequential)]
    struct WAVEHDR { public IntPtr lpData; public uint dwBufferLength, dwBytesRecorded; public IntPtr dwUser; public uint dwFlags, dwLoops; public IntPtr lpNext, reserved; }

    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, int dev, ref WAVEFORMATEX fmt, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, WAVEHDR* hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, WAVEHDR* hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, WAVEHDR* hdr, int size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
    [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);

    sealed class Voice { public float[] Data; public int Pos; }

    readonly ConcurrentQueue<Voice> _incoming = new();
    readonly List<Voice> _voices = new();
    readonly Thread _thread;
    volatile bool _running = true;
    IntPtr _hwo;

    /// <summary>0..1 目标雨量。</summary>
    public volatile float RainTarget;
    public volatile float MasterVolume = 0.6f;
    public volatile bool Muted;

    public StormAudio()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "StormAudio", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>pan: 0 左 .. 1 右；intensity 越大越近越响。</summary>
    public void Thunder(double intensity, double pan, int seed)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var data = ThunderSynth.Generate(intensity, pan, seed);
            _incoming.Enqueue(new Voice { Data = data });
        });
    }

    void Run()
    {
        var fmt = new WAVEFORMATEX { wFormatTag = 1, nChannels = 2, nSamplesPerSec = SampleRate, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = SampleRate * 4 };
        if (waveOutOpen(out _hwo, -1, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) return;

        var headers = new WAVEHDR*[BufferCount];
        for (int i = 0; i < BufferCount; i++)
        {
            headers[i] = (WAVEHDR*)Marshal.AllocHGlobal(sizeof(WAVEHDR));
            *headers[i] = new WAVEHDR { lpData = Marshal.AllocHGlobal(Frames * 4), dwBufferLength = Frames * 4 };
            waveOutPrepareHeader(_hwo, headers[i], sizeof(WAVEHDR));
            Fill((short*)headers[i]->lpData);
            waveOutWrite(_hwo, headers[i], sizeof(WAVEHDR));
        }

        while (_running)
        {
            bool wrote = false;
            for (int i = 0; i < BufferCount; i++)
            {
                if ((headers[i]->dwFlags & 1 /*WHDR_DONE*/) == 0) continue;
                Fill((short*)headers[i]->lpData);
                waveOutWrite(_hwo, headers[i], sizeof(WAVEHDR));
                wrote = true;
            }
            if (!wrote) Thread.Sleep(4);
        }

        waveOutReset(_hwo);
        for (int i = 0; i < BufferCount; i++)
        {
            waveOutUnprepareHeader(_hwo, headers[i], sizeof(WAVEHDR));
            Marshal.FreeHGlobal(headers[i]->lpData);
            Marshal.FreeHGlobal((IntPtr)headers[i]);
        }
        waveOutClose(_hwo);
    }

    // ---------- 雨声 ----------
    readonly RainSynth _synth = new();
    readonly WindSynth _wind = new();
    /// <summary>阵风强度 0..1（来自天气引擎）。</summary>
    public volatile float WindTarget;
    float _rain, _gain = 0;

    void Fill(short* buf)
    {
        while (_incoming.TryDequeue(out var v)) _voices.Add(v);
        float vol = Muted ? 0 : MasterVolume;

        for (int f = 0; f < Frames; f++)
        {
            // 平滑跟随
            _rain += (RainTarget - _rain) * 0.00002f;
            _gain += (vol - _gain) * 0.0005f;

            float gust = WindTarget;
            // 刮风时雨声也跟着稍微起伏变大
            _synth.Next(_rain * (1 + 0.25f * gust), out float l, out float r);
            _wind.Next(gust, out float wl, out float wr);
            l += wl; r += wr;

            for (int i = _voices.Count - 1; i >= 0; i--)
            {
                var v = _voices[i];
                if (v.Pos >= v.Data.Length) { _voices.RemoveAt(i); continue; }
                l += v.Data[v.Pos]; r += v.Data[v.Pos + 1];
                v.Pos += 2;
            }

            l = MathF.Tanh(l * _gain * 1.2f);
            r = MathF.Tanh(r * _gain * 1.2f);
            buf[f * 2] = (short)(l * 32000);
            buf[f * 2 + 1] = (short)(r * 32000);
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread.Join(2000);
    }
}

/// <summary>
/// 雷声合成：只做氛围感的滚雷——低沉、缓慢涌起、隆隆滚动后远去。
/// 不做高频爆裂（听起来像电流“劈”声），越近只是越响、越饱满、来得稍快一点。
/// </summary>
internal static class ThunderSynth
{
    const int SR = 44100;

    public static float[] Generate(double intensity, double pan, int seed)
    {
        var rng = new Random(seed);
        double seconds = 7 + intensity * 5 + rng.NextDouble() * 3;
        int n = (int)(seconds * SR);
        var outBuf = new float[n * 2];

        // 滚雷包络：若干随机高斯“隆起”，越往后越弱
        int bumps = 4 + rng.Next(5);
        var bt = new double[bumps]; var ba = new double[bumps]; var bw = new double[bumps];
        for (int i = 0; i < bumps; i++)
        {
            bt[i] = 0.4 + rng.NextDouble() * seconds * 0.6;
            ba[i] = (0.4 + rng.NextDouble() * 0.6) * Math.Exp(-bt[i] / (seconds * 0.5));
            bw[i] = 0.9 + rng.NextDouble() * 1.3;   // 宽隆起：滚动感来自缓慢起伏，而不是突然的一下
        }

        double brownL = 0, brownR = 0, lp1L = 0, lp2L = 0, lp1R = 0, lp2R = 0;
        // 缓慢涌起：近雷约 0.6 秒，远雷可达 1.8 秒
        double attack = 0.6 + (1 - intensity) * 1.0 + rng.NextDouble() * 0.3;
        // 等功率声像，收窄到中间 60%，雷声不会完全偏到一侧
        double angle = (0.2 + pan * 0.6) * Math.PI / 2;
        double panL = Math.Cos(angle) * 1.41, panR = Math.Sin(angle) * 1.41;

        for (int i = 0; i < n; i++)
        {
            double t = (double)i / SR;
            double wl = rng.NextDouble() * 2 - 1, wr = rng.NextDouble() * 2 - 1;
            double shared = rng.NextDouble() * 2 - 1;
            wl = wl * 0.5 + shared * 0.5; wr = wr * 0.5 + shared * 0.5;

            brownL = (brownL + wl * 0.02) * 0.998; brownR = (brownR + wr * 0.02) * 0.998;

            // 截止频率随时间下降 —— 雷声越滚越闷；近雷稍亮一些，但始终在低频
            double fc = (90 + 90 * intensity) * Math.Exp(-t / (seconds * 0.6)) + 40;
            double a = 1 - Math.Exp(-2 * Math.PI * fc / SR);
            lp1L += (brownL - lp1L) * a; lp2L += (lp1L - lp2L) * a;
            lp1R += (brownR - lp1R) * a; lp2R += (lp1R - lp2R) * a;

            double env = 0;
            for (int k = 0; k < bumps; k++) { double d = (t - bt[k]) / bw[k]; env += ba[k] * Math.Exp(-d * d); }
            // smoothstep 起音，没有突兀的瞬态
            double rise = Math.Min(1, t / attack);
            env *= rise * rise * (3 - 2 * rise) * Math.Exp(-t / (seconds * 0.55));
            // 隆隆的缓慢颤动
            env *= 0.8 + 0.2 * Math.Sin(t * (5 + 2 * Math.Sin(t * 0.6)));

            double l = lp2L * env * 9, r = lp2R * env * 9;

            outBuf[i * 2] = (float)(l * panL);
            outBuf[i * 2 + 1] = (float)(r * panR);
        }

        // 高通 ~30 Hz：布朗噪声的次声成分听不见，却会推高峰值、让音箱发闷
        double hpA = 1 - Math.Exp(-2 * Math.PI * 30 / SR), hpL = 0, hpR = 0;
        for (int i = 0; i < n; i++)
        {
            hpL += (outBuf[i * 2] - hpL) * hpA; outBuf[i * 2] -= (float)hpL;
            hpR += (outBuf[i * 2 + 1] - hpR) * hpA; outBuf[i * 2 + 1] -= (float)hpR;
        }

        // 归一化到目标响度
        float peak = 1e-6f;
        foreach (var s in outBuf) peak = Math.Max(peak, Math.Abs(s));
        // 衬在雨声后面的氛围音量，近雷也不会盖过一切
        float target = (float)(0.22 + intensity * 0.33);
        float k2 = target / peak;
        for (int i = 0; i < outBuf.Length; i++) outBuf[i] *= k2;

        // 首尾淡入淡出防爆音
        int fade = SR / 50;
        for (int i = 0; i < fade && i * 2 + 1 < outBuf.Length; i++)
        {
            float g = (float)i / fade;
            int e = outBuf.Length - 2 - i * 2;
            outBuf[e] *= g; outBuf[e + 1] *= g;
        }
        return outBuf;
    }
}
