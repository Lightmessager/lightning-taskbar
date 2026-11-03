namespace LightningTaskbar.Audio;

/// <summary>
/// 风声（逐采样）：宽带通噪声，中心频率缓慢游走，产生“呼——”的涌动；
/// 再垫一层很低的隆隆声做底。Q 很低，不会出现口哨般的音高。
/// </summary>
internal sealed class WindSynth
{
    const int SR = 44100;
    readonly Random _rng;

    float _g;                       // 平滑后的阵风强度
    float _fc = 300, _fcTarget = 300;
    float _flutter = 1, _flutterTarget = 1;
    int _timer;
    float _lowL, _bandL, _lowR, _bandR;
    float _brownL, _brownR, _rumbleL, _rumbleR, _subL, _subR, _softL, _softR;

    public WindSynth(int seed = 0) => _rng = seed == 0 ? new Random() : new Random(seed);

    float Noise() => (float)(_rng.NextDouble() * 2 - 1);

    public void Next(float gust, out float l, out float r)
    {
        // 逐采样跟随，避免“拉链”噪声；真正的起落节奏由天气引擎控制
        _g += (gust - _g) * 0.00005f;
        l = r = 0;
        if (_g < 0.0005f) return;

        if (--_timer <= 0)
        {
            _timer = 2048;
            // 风越大，呼啸的频段越高越宽
            if (_rng.NextDouble() < 0.25) _fcTarget = 180 + (float)_rng.NextDouble() * (250 + 450 * _g);
            if (_rng.NextDouble() < 0.4) _flutterTarget = 0.65f + (float)_rng.NextDouble() * 0.35f;
        }
        _fc += (_fcTarget - _fc) * 0.00004f;
        _flutter += (_flutterTarget - _flutter) * 0.00008f;

        // 宽带通（Chamberlin SVF，阻尼 1.3 → Q≈0.77）
        float f = 2 * MathF.Sin(MathF.PI * _fc / SR);
        float f2 = 2 * MathF.Sin(MathF.PI * _fc * 1.12f / SR);   // 右声道略偏，增加宽度
        const float damp = 1.3f;
        float xl = Noise(), xr = Noise();
        _lowL += f * _bandL; _bandL += f * (xl - _lowL - damp * _bandL);
        _lowR += f2 * _bandR; _bandR += f2 * (xr - _lowR - damp * _bandR);

        // 低沉的隆隆底：布朗噪声 → 低通 ~120 Hz → 高通 ~40 Hz（去掉听不见的次声）
        _brownL = (_brownL + xl * 0.02f) * 0.997f; _brownR = (_brownR + xr * 0.02f) * 0.997f;
        const float a = 0.017f, hp = 0.0057f;
        _rumbleL += (_brownL - _rumbleL) * a; _rumbleR += (_brownR - _rumbleR) * a;
        _subL += (_rumbleL - _subL) * hp; _subR += (_rumbleR - _subR) * hp;

        // 再柔化一级（~1.4 kHz），像隔着窗户听到的风，而不是耳边的嘶嘶声
        const float soft = 0.18f;
        _softL += (_bandL - _softL) * soft; _softR += (_bandR - _softR) * soft;

        float amp = MathF.Pow(_g, 1.5f) * _flutter * 0.36f;
        l = (_softL * 1.8f + (_rumbleL - _subL) * 0.9f) * amp;
        r = (_softR * 1.8f + (_rumbleR - _subR) * 0.9f) * amp;
    }
}
