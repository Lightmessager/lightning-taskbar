namespace LightningTaskbar.Audio;

/// <summary>
/// 雨声合成（逐采样）。
/// 雨幕：低沉柔和的粉噪，经两级低通，不再有高频“嘶嘶”底噪；
/// 雨滴：极短噪声冲击 + 随机频段带通，没有正弦音高，听起来是“沙沙、嗒嗒”而不是电子“滴哩”声。
/// </summary>
internal sealed class RainSynth
{
    const int SampleRate = 44100;
    readonly Random _rng;

    // 雨幕
    float _b0L, _b1L, _b2L, _b0R, _b1R, _b2R;
    float _lp1L, _lp2L, _lp1R, _lp2R, _hpL, _hpR;
    static readonly float Hp = 1 - MathF.Exp(-2 * MathF.PI * 150 / SampleRate);
    static readonly float Lp = 1 - MathF.Exp(-2 * MathF.PI * 2200 / SampleRate);
    float _gust = 1, _gustTarget = 1;
    int _gustTimer;

    // 雨滴：每颗是一个短噪声包络激励的 SVF 带通
    struct Drop { public bool On; public float Amp, Decay, F, Q, Low, Band, Pan; }
    readonly Drop[] _drops = new Drop[40];

    public RainSynth(int seed = 0) => _rng = seed == 0 ? new Random() : new Random(seed);

    float Noise() => (float)(_rng.NextDouble() * 2 - 1);

    /// <param name="rain">雨量 0..1。</param>
    public void Next(float rain, out float l, out float r)
    {
        l = r = 0;
        if (rain < 0.001f) return;

        // 阵风：几秒一次的缓慢起伏
        if (--_gustTimer <= 0)
        {
            _gustTimer = SampleRate * (2 + _rng.Next(5));
            _gustTarget = 0.75f + (float)_rng.NextDouble() * 0.45f;
        }
        _gust += (_gustTarget - _gust) * 0.00002f;

        // ---- 雨幕：Paul Kellet 粉噪 → 两级 ~900 Hz 低通 ----
        float wl = Noise(), wr = Noise();
        _b0L = 0.99765f * _b0L + wl * 0.0990460f; _b1L = 0.96300f * _b1L + wl * 0.2965164f; _b2L = 0.57000f * _b2L + wl * 1.0526913f;
        _b0R = 0.99765f * _b0R + wr * 0.0990460f; _b1R = 0.96300f * _b1R + wr * 0.2965164f; _b2R = 0.57000f * _b2R + wr * 1.0526913f;
        float pinkL = (_b0L + _b1L + _b2L + wl * 0.1848f) * 0.11f, pinkR = (_b0R + _b1R + _b2R + wr * 0.1848f) * 0.11f;
        // 先高通 ~150 Hz 去掉闷闷的轰鸣（也是一种“底噪”），再两级 ~2.2 kHz 低通去掉嘶嘶声
        _hpL += (pinkL - _hpL) * Hp; _hpR += (pinkR - _hpR) * Hp;
        float bandL = pinkL - _hpL, bandR = pinkR - _hpR;
        _lp1L += (bandL - _lp1L) * Lp; _lp2L += (_lp1L - _lp2L) * Lp;
        _lp1R += (bandR - _lp1R) * Lp; _lp2R += (_lp1R - _lp2R) * Lp;

        float bed = 0.55f * rain * _gust;
        l = _lp2L * bed;
        r = _lp2R * bed;

        // ---- 雨滴：小雨约 25 颗/秒，大雨约 70 颗/秒 ----
        if (_rng.NextDouble() < rain * 0.0016) Spawn();
        for (int i = 0; i < _drops.Length; i++)
        {
            ref var d = ref _drops[i];
            if (!d.On) continue;
            float x = Noise() * d.Amp;
            // Chamberlin 状态变量滤波器（带通输出）
            d.Low += d.F * d.Band;
            float high = x - d.Low - d.Q * d.Band;
            d.Band += d.F * high;
            float s = d.Band;
            l += s * (1 - d.Pan);
            r += s * d.Pan;
            d.Amp *= d.Decay;
            if (d.Amp < 0.0003f) d.On = false;
        }
    }

    void Spawn()
    {
        for (int i = 0; i < _drops.Length; i++)
        {
            if (_drops[i].On) continue;
            double fc = 500 + Math.Pow(_rng.NextDouble(), 1.5) * 2500;      // 偏中低频，更“湿”
            double tau = 0.0015 + _rng.NextDouble() * 0.0035;              // 1.5 ~ 5 ms
            float amp = (float)(0.05 + Math.Pow(_rng.NextDouble(), 3) * 0.35); // 多数很轻，偶尔一颗响
            _drops[i] = new Drop
            {
                On = true,
                Amp = amp,
                Decay = (float)Math.Exp(-1.0 / (tau * SampleRate)),
                F = (float)(2 * Math.Sin(Math.PI * fc / SampleRate)),
                Q = 1.1f + (float)_rng.NextDouble() * 0.6f,                // 低 Q：宽带通，没有明显音高
                Pan = 0.15f + (float)_rng.NextDouble() * 0.7f,
            };
            return;
        }
    }
}
