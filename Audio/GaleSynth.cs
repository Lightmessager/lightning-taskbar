namespace LightningTaskbar.Audio;

/// <summary>
/// 大风的两种声音（逐采样，全部由噪声合成）：
/// 暴雪呼啸 —— 两个中等 Q 的带通在 300~800 Hz 之间缓慢滑动，像风从窗缝和屋檐掠过的“呜——”；
/// 龙卷风轰鸣 —— 低通的布朗噪声做“货运列车”般的低频轰隆，叠加快速不规则的颤动和左右旋转的中频气流。
/// </summary>
internal sealed class GaleSynth
{
    const int SR = 44100;
    readonly Random _rng;

    float _howl, _roar;                      // 平滑后的强度

    // 呼啸：两个 SVF 带通
    struct Whistle { public float Fc, Target, Low, Band, Amp, AmpTarget; }
    readonly Whistle[] _w = new Whistle[2];
    int _howlTimer;
    float _softL, _softR;

    // 轰鸣
    float _brown, _lp1, _lp2, _hp, _flutter = 1, _flutterTarget = 1;
    float _midLow, _midBand, _midFc = 380;
    double _panPhase;
    int _roarTimer;
    float _swell = 1, _swellTarget = 1;

    public GaleSynth(int seed = 0)
    {
        _rng = seed == 0 ? new Random() : new Random(seed);
        _w[0] = new Whistle { Fc = 420, Target = 420, Amp = 0.6f, AmpTarget = 0.6f };
        _w[1] = new Whistle { Fc = 610, Target = 610, Amp = 0.4f, AmpTarget = 0.4f };
    }

    float Noise() => (float)(_rng.NextDouble() * 2 - 1);

    public void Next(float howl, float roar, out float l, out float r)
    {
        _howl += (howl - _howl) * 0.00003f;
        _roar += (roar - _roar) * 0.00003f;
        l = r = 0;
        if (_howl > 0.0005f) Howl(ref l, ref r);
        if (_roar > 0.0005f) Roar(ref l, ref r);
    }

    void Howl(ref float l, ref float r)
    {
        if (--_howlTimer <= 0)
        {
            _howlTimer = 4096;
            for (int i = 0; i < _w.Length; i++)
            {
                if (_rng.NextDouble() < 0.18) _w[i].Target = 280 + (float)_rng.NextDouble() * 520;
                if (_rng.NextDouble() < 0.22) _w[i].AmpTarget = 0.15f + (float)_rng.NextDouble() * 0.85f;
            }
        }
        float x = Noise();
        float sum = 0;
        for (int i = 0; i < _w.Length; i++)
        {
            ref var w = ref _w[i];
            // 滑音要慢，才像风而不像哨子
            w.Fc += (w.Target - w.Fc) * 0.00002f;
            w.Amp += (w.AmpTarget - w.Amp) * 0.00004f;
            float f = 2 * MathF.Sin(MathF.PI * w.Fc / SR);
            const float damp = 0.22f;          // Q ≈ 4.5：有音高感，但不尖
            w.Low += f * w.Band;
            w.Band += f * (x - w.Low - damp * w.Band);
            sum += w.Band * w.Amp;
        }
        // 再柔化一级，去掉嘶声
        _softL += (sum - _softL) * 0.12f;
        float wide = Noise() * 0.04f;
        _softR += (sum * 0.85f + wide - _softR) * 0.12f;
        float amp = MathF.Pow(_howl, 1.3f) * 0.22f;
        l += _softL * amp;
        r += _softR * amp;
    }

    void Roar(ref float l, ref float r)
    {
        if (--_roarTimer <= 0)
        {
            _roarTimer = 1024;
            // 不规则的快速颤动（8~16 Hz 量级的粗糙感）+ 几秒一次的涌动
            _flutterTarget = 0.6f + (float)_rng.NextDouble() * 0.4f;
            if (_rng.NextDouble() < 0.03) _swellTarget = 0.7f + (float)_rng.NextDouble() * 0.5f;
            if (_rng.NextDouble() < 0.05) _midFc = 260 + (float)_rng.NextDouble() * 320;
        }
        _flutter += (_flutterTarget - _flutter) * 0.004f;
        _swell += (_swellTarget - _swell) * 0.00003f;

        float x = Noise();
        _brown = (_brown + x * 0.02f) * 0.998f;
        // 两级 ~160 Hz 低通 → 轰隆；再高通 ~35 Hz 去掉次声
        const float a = 0.0225f, hp = 0.005f;
        _lp1 += (_brown - _lp1) * a;
        _lp2 += (_lp1 - _lp2) * a;
        _hp += (_lp2 - _hp) * hp;
        float rumble = (_lp2 - _hp) * 7f;

        // 中频气流（宽带通），声像缓慢左右旋转，像风绕着转
        float f = 2 * MathF.Sin(MathF.PI * _midFc / SR);
        _midLow += f * _midBand;
        _midBand += f * (Noise() - _midLow - 1.1f * _midBand);
        _panPhase += 2 * Math.PI * 0.18 / SR;
        float pan = 0.5f + 0.35f * (float)Math.Sin(_panPhase);

        float amp = MathF.Pow(_roar, 1.2f) * _swell;
        float body = rumble * _flutter * 0.24f * amp;
        float air = _midBand * 0.24f * amp;
        l += body + air * (1 - pan) * 1.4f;
        r += body + air * pan * 1.4f;
    }
}
