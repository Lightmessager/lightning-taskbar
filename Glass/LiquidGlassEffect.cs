using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Media3D;

namespace LightningTaskbar.Glass;

/// <summary>
/// 玻璃像素着色器（ps_3_0）。元素本身只提供尺寸，真正的颜色全部来自两张背景纹理：
/// <para>
/// 1. 圆角矩形 SDF 定出玻璃形状；边缘一圈是圆弧形倒角，按倒角坡度把背景往里折（越靠边折得越多），带轻微色散。<br/>
/// 2. 玻璃本体取磨砂纹理，饱和度略提、高光压缩，保证亮背景上图标依然清楚。<br/>
/// 3. 厚度感：倒角处轻微吸收变暗，最外沿是一条 1 像素宽、四周基本均匀的高光线（光源方向只做轻微偏重）。<br/>
/// 4. 外侧一圈柔和的投影，让玻璃“浮”在桌面上。<br/>
/// 5. 最多 16 颗水珠：每颗都是一个小凸透镜，里面是背后景物的倒像，边缘发暗、底部聚光、左上角一点反光。
/// </para>
/// HLSL 在运行时用系统自带的 d3dcompiler_47.dll 编译，无需 SDK。
/// </summary>
public sealed class LiquidGlassEffect : ShaderEffect
{
    public const int MaxDrops = 16;

    // 注意：着色器源码只能是 ASCII
    const string Hlsl = @"
sampler2D implicitInput : register(s0);
sampler2D sharpTex : register(s1);
sampler2D frostTex : register(s2);

float4 geo    : register(c0);   // element w, h (DIP), corner radius, bevel width
float4 map    : register(c1);   // texture uv = uv * map.xy + map.zw
float4 dipk   : register(c2);   // unused, unused, shadow pad (DIP), DIP per device pixel
float4 optics : register(c3);   // refraction (DIP), dispersion, frost mix, flash
float4 tint   : register(c4);   // rgb, amount
float4 tone   : register(c5);   // saturation, brightness, highlight compression, shadow opacity
float4 light  : register(c6);   // light dir x, y, rim strength, milky veil
float4 drops[16] : register(c8);  // x, y (DIP), radius (DIP), opacity

float sdBox(float2 p, float2 b, float r)
{
    float2 q = abs(p) - b + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

float2 toTex(float2 P) { return P / geo.xy * map.xy + map.zw; }

float3 backdrop(float2 P, float mixF)
{
    float4 t = float4(toTex(P), 0, 0);
    return lerp(tex2Dlod(sharpTex, t).rgb, tex2Dlod(frostTex, t).rgb, mixF);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 P = uv * geo.xy;
    float pad = dipk.z;
    float px = dipk.w;
    float2 hb = geo.xy * 0.5 - pad;
    float2 p = P - geo.xy * 0.5;
    float r = min(geo.z, min(hb.x, hb.y));
    float d = sdBox(p, hb, r);

    // soft drop shadow, a little lower than the glass
    float ds = sdBox(p - float2(0, pad * 0.3), hb, r);
    float shadow = tone.w * exp(-max(ds, 0) / max(pad * 0.32, 0.5)) * step(0.01, pad);
    float cover = saturate(0.5 - d / px);
    if (cover <= 0) return float4(0, 0, 0, shadow);

    // outward normal of the rounded rectangle
    float2 q = abs(p) - hb + r;
    float2 n = (q.x > 0 && q.y > 0) ? normalize(q) : ((q.x > q.y) ? float2(1, 0) : float2(0, 1));
    n *= step(0, p) * 2 - 1;

    float depth = max(-d, 0);                       // DIP from the rim inwards
    float t = saturate(1 - depth / geo.w);          // 1 at the rim, 0 on the flat part
    float bend = 1 - sqrt(1 - t * t);               // circular bevel: flat, then steep at the rim
    float2 off = -n * bend * optics.x;
    float disp = optics.y * bend;
    float mixF = optics.z;

    float3 col;
    col.r = backdrop(P + off * (1 + disp), mixF).r;
    col.g = backdrop(P + off, mixF).g;
    col.b = backdrop(P + off * (1 - disp), mixF).b;

    float3 W = float3(0.2126, 0.7152, 0.0722);
    float lum = dot(col, W);
    col = max(lerp(lum.xxx, col, tone.x), 0);
    col *= tone.y * (1 - tone.z * smoothstep(0.3, 1.0, lum));
    col = lerp(col, tint.rgb, tint.a);
    col += light.w;

    // thickness: the bevel absorbs a little light ...
    float inner = exp(-depth / max(geo.w * 0.45, 1));
    col *= 1 - 0.12 * inner;

    // ... and the very edge catches it. Nearly uniform all round, light dir only biases it.
    float2 L = normalize(light.xy);
    float f = dot(n, L);
    float dirMix = 0.62 + 0.38 * saturate(f) + 0.18 * saturate(-f);
    float edgeLine = exp(-depth / (px * 0.9 + 0.25));
    float edgeGlow = exp(-depth / 2.5);
    float lumNow = dot(col, W);
    col += (edgeLine * 0.42 + edgeGlow * 0.07) * dirMix * light.z * (0.45 + 0.55 * (1 - lumNow));

    // lightning lights the glass up, mostly along the bevel
    col += optics.w * (0.08 + 0.28 * inner) * float3(0.86, 0.91, 1.0);

    // rain beads: each one is a tiny convex lens showing the scene upside down
    [loop] for (int i = 0; i < 16; i++)
    {
        float4 b = drops[i];
        if (b.w > 0)
        {
            float2 v = P - b.xy;
            float dist = length(v) / b.z;
            // contact shadow just below the bead
            float sd = length(v - float2(0, b.z * 0.35)) / b.z;
            col *= 1 - 0.22 * b.w * saturate((1.35 - sd) * 2.5) * step(0.85, dist);
            float a = b.w * saturate((1 - dist) * b.z / px);
            if (a > 0)
            {
                float2 sp = b.xy - v * 2.6;
                float3 dc = tex2Dlod(sharpTex, float4(toTex(sp), 0, 0)).rgb * 0.82 + 0.03;
                dc *= 1 - 0.45 * smoothstep(0.55, 1.0, dist);
                float2 dirv = v / max(length(v), 0.001);
                dc += 0.22 * smoothstep(0.45, 0.95, dist) * saturate(dirv.y);
                float g = length(v / b.z - float2(-0.33, -0.38));
                dc += 0.45 * saturate(1 - g / 0.2);
                col = lerp(col, dc, a);
            }
        }
    }

    float alpha = cover + shadow * (1 - cover);
    return float4(col * cover, alpha);
}";

    static PixelShader _shader;
    static bool _failed;

    /// <summary>着色器能否在本机编译（d3dcompiler_47 缺失或显卡不支持 ps_3_0 时为 false）。</summary>
    public static bool IsSupported
    {
        get
        {
            if (_shader != null) return true;
            if (_failed) return false;
            try { _shader = Compile(); return true; }
            catch { _failed = true; return false; }
        }
    }

    public static readonly DependencyProperty InputProperty = RegisterPixelShaderSamplerProperty("Input", typeof(LiquidGlassEffect), 0);
    public static readonly DependencyProperty SharpProperty = RegisterPixelShaderSamplerProperty("Sharp", typeof(LiquidGlassEffect), 1);
    public static readonly DependencyProperty FrostProperty = RegisterPixelShaderSamplerProperty("Frost", typeof(LiquidGlassEffect), 2);

    public static readonly DependencyProperty GeometryProperty = Reg("Geometry", 0, new Point4D(100, 64, 22, 18));
    public static readonly DependencyProperty MappingProperty = Reg("Mapping", 1, new Point4D(1, 1, 0, 0));
    public static readonly DependencyProperty PixelProperty = Reg("Pixel", 2, new Point4D(0, 0, 0, 1));
    public static readonly DependencyProperty OpticsProperty = Reg("Optics", 3, new Point4D(14, 0.12, 1, 0));
    public static readonly DependencyProperty TintProperty = Reg("Tint", 4, new Point4D(0.05, 0.07, 0.1, 0.15));
    public static readonly DependencyProperty ToneProperty = Reg("Tone", 5, new Point4D(1.2, 1.0, 0.35, 0.3));
    public static readonly DependencyProperty LightProperty = Reg("Light", 6, new Point4D(-0.45, -1, 1, 0.025));

    static readonly DependencyProperty[] DropProperties =
        Enumerable.Range(0, MaxDrops).Select(i => Reg("Drop" + i, 8 + i, new Point4D())).ToArray();

    static DependencyProperty Reg(string name, int register, Point4D def) =>
        DependencyProperty.Register(name, typeof(Point4D), typeof(LiquidGlassEffect), new UIPropertyMetadata(def, PixelShaderConstantCallback(register)));

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Brush Sharp { get => (Brush)GetValue(SharpProperty); set => SetValue(SharpProperty, value); }
    public Brush Frost { get => (Brush)GetValue(FrostProperty); set => SetValue(FrostProperty, value); }
    /// <summary>元素宽、高、圆角半径、倒角宽度（DIP）。</summary>
    public Point4D Geometry { get => (Point4D)GetValue(GeometryProperty); set => SetValue(GeometryProperty, value); }
    /// <summary>元素 uv → 背景纹理 uv 的缩放与偏移。</summary>
    public Point4D Mapping { get => (Point4D)GetValue(MappingProperty); set => SetValue(MappingProperty, value); }
    /// <summary>z = 投影外边距（DIP），w = 每个物理像素对应的 DIP。</summary>
    public Point4D Pixel { get => (Point4D)GetValue(PixelProperty); set => SetValue(PixelProperty, value); }
    /// <summary>折射量（DIP）、色散、磨砂比例 0..1、闪电亮度。</summary>
    public Point4D Optics { get => (Point4D)GetValue(OpticsProperty); set => SetValue(OpticsProperty, value); }
    /// <summary>染色 rgb（0..1）与染色比例。</summary>
    public Point4D Tint { get => (Point4D)GetValue(TintProperty); set => SetValue(TintProperty, value); }
    /// <summary>饱和度、亮度、高光压缩、投影浓度。</summary>
    public Point4D Tone { get => (Point4D)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    /// <summary>光源方向 x、y，边缘高光强度，乳白雾度。</summary>
    public Point4D Light { get => (Point4D)GetValue(LightProperty); set => SetValue(LightProperty, value); }

    /// <summary>设置第 i 颗水珠：位置（DIP）、半径（DIP）、不透明度（0 = 不存在）。</summary>
    public void SetDrop(int i, Point4D drop) => SetValue(DropProperties[i], drop);

    public LiquidGlassEffect()
    {
        PixelShader = _shader ??= Compile();
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(SharpProperty);
        UpdateShaderValue(FrostProperty);
        foreach (var dp in new[] { GeometryProperty, MappingProperty, PixelProperty, OpticsProperty, TintProperty, ToneProperty, LightProperty })
            UpdateShaderValue(dp);
        foreach (var dp in DropProperties) UpdateShaderValue(dp);
    }

    // ---------------- 运行时编译 ----------------

    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ID3DBlob
    {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] IntPtr GetBufferSize();
    }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)]
    static extern int D3DCompile([MarshalAs(UnmanagedType.LPStr)] string src, IntPtr size, string name, IntPtr defines, IntPtr include,
        string entry, string target, uint flags1, uint flags2, out ID3DBlob code, out ID3DBlob errors);

    static PixelShader Compile()
    {
        int hr = D3DCompile(Hlsl, new IntPtr(Hlsl.Length), "LiquidGlass", IntPtr.Zero, IntPtr.Zero,
            "main", "ps_3_0", 1u << 15 /*OPTIMIZATION_LEVEL3*/, 0, out var code, out var errors);
        if (hr < 0)
        {
            string msg = errors != null ? Marshal.PtrToStringAnsi(errors.GetBufferPointer(), (int)errors.GetBufferSize()) : $"0x{hr:X8}";
            throw new InvalidOperationException("玻璃着色器编译失败：" + msg);
        }
        var bytes = new byte[(int)code.GetBufferSize()];
        Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);
        var shader = new PixelShader();
        shader.SetStreamSource(new MemoryStream(bytes));
        return shader;
    }
}
