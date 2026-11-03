using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace LightningTaskbar;

public sealed class InputProfile
{
    public string Name { get; init; }
    public ushort LangId { get; init; }
    public uint ProfileType { get; init; }
    public Guid Clsid { get; init; }
    public Guid GuidProfile { get; init; }
    public IntPtr Hkl { get; init; }
    public bool IsCurrent { get; set; }
    public string Check => IsCurrent ? "✓" : "";
    public string LanguageName { get; init; }
}

/// <summary>
/// 输入法：用 TSF 枚举已启用的输入法（同一语言下的多个输入法，如微软拼音 / 小狼毫，也能区分），
/// 切换时按会话激活，并通知前台窗口切换输入语言；当前状态从前台窗口读取。
/// </summary>
internal static class ImeService
{
    // ---------------- TSF 接口（vtable 顺序必须与 msctf.h 一致） ----------------

    const uint TF_PROFILETYPE_INPUTPROCESSOR = 1, TF_PROFILETYPE_KEYBOARDLAYOUT = 2;
    const uint TF_IPP_FLAG_ACTIVE = 1, TF_IPP_FLAG_ENABLED = 2;
    const uint TF_IPPMF_FORSESSION = 0x20000000, TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE = 0x4;
    static readonly Guid GUID_TFCAT_TIP_KEYBOARD = new("34745C63-B2F0-4784-8B67-5E12C8701A31");
    static readonly Guid CLSID_TF_InputProcessorProfiles = new("33C53A50-F456-4884-B049-85FD643ECFED");

    [StructLayout(LayoutKind.Sequential)]
    struct TF_INPUTPROCESSORPROFILE
    {
        public uint dwProfileType;
        public ushort langid;
        public Guid clsid;
        public Guid guidProfile;
        public Guid catid;
        public IntPtr hklSubstitute;
        public uint dwCaps;
        public IntPtr hkl;
        public uint dwFlags;
    }

    [ComImport, Guid("71C6E74C-0F28-11D8-A82A-00065B84435C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITfInputProcessorProfileMgr
    {
        [PreserveSig] int ActivateProfile(uint dwProfileType, ushort langid, ref Guid clsid, ref Guid guidProfile, IntPtr hkl, uint dwFlags);
        [PreserveSig] int DeactivateProfile(uint dwProfileType, ushort langid, ref Guid clsid, ref Guid guidProfile, IntPtr hkl, uint dwFlags);
        [PreserveSig] int GetProfile(uint dwProfileType, ushort langid, ref Guid clsid, ref Guid guidProfile, IntPtr hkl, out TF_INPUTPROCESSORPROFILE profile);
        [PreserveSig] int EnumProfiles(ushort langid, out IEnumTfInputProcessorProfiles ppEnum);
        [PreserveSig] int ReleaseInputProcessor(ref Guid rclsid, uint dwFlags);
        [PreserveSig] int RegisterProfile(IntPtr a, ushort b, IntPtr c, IntPtr d, uint e, IntPtr f, uint g, uint h, IntPtr i, uint j, int k, uint l);
        [PreserveSig] int UnregisterProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile, uint dwFlags);
        [PreserveSig] int GetActiveProfile(ref Guid catid, out TF_INPUTPROCESSORPROFILE profile);
    }

    [ComImport, Guid("71C6E74D-0F28-11D8-A82A-00065B84435C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumTfInputProcessorProfiles
    {
        [PreserveSig] int Clone(out IEnumTfInputProcessorProfiles ppEnum);
        [PreserveSig] int Next(uint ulCount, out TF_INPUTPROCESSORPROFILE profile, out uint pcFetch);
        [PreserveSig] int Reset();
        [PreserveSig] int Skip(uint ulCount);
    }

    [ComImport, Guid("1F02B6C5-7842-4EE6-8A0B-9A24183A95CA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ITfInputProcessorProfiles
    {
        [PreserveSig] int Register(ref Guid rclsid);
        [PreserveSig] int Unregister(ref Guid rclsid);
        [PreserveSig] int AddLanguageProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile, IntPtr desc, uint cchDesc, IntPtr iconFile, uint cchFile, uint uIconIndex);
        [PreserveSig] int RemoveLanguageProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile);
        [PreserveSig] int EnumInputProcessorInfo(out IntPtr ppEnum);
        [PreserveSig] int GetDefaultLanguageProfile(ushort langid, ref Guid catid, out Guid pclsid, out Guid pguidProfile);
        [PreserveSig] int SetDefaultLanguageProfile(ushort langid, ref Guid rclsid, ref Guid guidProfiles);
        [PreserveSig] int ActivateLanguageProfile(ref Guid rclsid, ushort langid, ref Guid guidProfiles);
        [PreserveSig] int GetActiveLanguageProfile(ref Guid rclsid, out ushort plangid, out Guid pguidProfile);
        [PreserveSig] int GetLanguageProfileDescription(ref Guid rclsid, ushort langid, ref Guid guidProfile, [MarshalAs(UnmanagedType.BStr)] out string desc);
    }

    // ---------------- IMM / 窗口 ----------------

    [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("imm32.dll")] static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);
    const uint WM_INPUTLANGCHANGEREQUEST = 0x50, WM_IME_CONTROL = 0x283;
    const int IMC_GETCONVERSIONMODE = 0x1, IMC_GETOPENSTATUS = 0x5;

    // ---------------- 公开 API ----------------

    /// <summary>已启用的键盘输入法（含普通键盘布局），按系统顺序。</summary>
    public static List<InputProfile> GetProfiles()
    {
        var list = new List<InputProfile>();
        try
        {
            var obj = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_TF_InputProcessorProfiles)!);
            var mgr = (ITfInputProcessorProfileMgr)obj;
            var profiles = (ITfInputProcessorProfiles)obj;
            if (mgr.EnumProfiles(0, out var en) != 0 || en == null) return list;

            var current = CurrentLangId();
            var tip = GUID_TFCAT_TIP_KEYBOARD;
            mgr.GetActiveProfile(ref tip, out var active);

            while (en.Next(1, out var p, out uint fetched) == 0 && fetched == 1)
            {
                if ((p.dwFlags & TF_IPP_FLAG_ENABLED) == 0) continue;
                bool isLayout = p.dwProfileType == TF_PROFILETYPE_KEYBOARDLAYOUT;
                if (!isLayout && p.catid != GUID_TFCAT_TIP_KEYBOARD) continue;

                string lang = LangName(p.langid);
                string name;
                if (isLayout) name = $"{lang}键盘";
                else
                {
                    var clsid = p.clsid; var gp = p.guidProfile;
                    name = profiles.GetLanguageProfileDescription(ref clsid, p.langid, ref gp, out var d) == 0 && !string.IsNullOrWhiteSpace(d) ? d : lang;
                }

                bool isCurrent = isLayout
                    ? active.dwProfileType == TF_PROFILETYPE_KEYBOARDLAYOUT && active.hkl == p.hkl
                    : active.clsid == p.clsid && active.guidProfile == p.guidProfile;
                list.Add(new InputProfile
                {
                    Name = name, LangId = p.langid, ProfileType = p.dwProfileType, Clsid = p.clsid,
                    GuidProfile = p.guidProfile, Hkl = p.hkl, LanguageName = lang, IsCurrent = isCurrent,
                });
            }
            // 我们自己线程上的“当前输入法”可能落后于前台窗口，至少保证语言对得上
            if (list.Count(x => x.IsCurrent) != 1 || list.First(x => x.IsCurrent).LangId != current)
            {
                foreach (var x in list) x.IsCurrent = false;
                var match = list.FirstOrDefault(x => x.LangId == current);
                if (match != null) match.IsCurrent = true;
            }
            Marshal.ReleaseComObject(obj);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        return list;
    }

    /// <summary>切换到指定输入法：按会话激活，再让前台窗口切换输入语言。</summary>
    public static void Activate(InputProfile p)
    {
        try
        {
            var obj = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_TF_InputProcessorProfiles)!);
            var mgr = (ITfInputProcessorProfileMgr)obj;
            var clsid = p.Clsid; var gp = p.GuidProfile;
            mgr.ActivateProfile(p.ProfileType, p.LangId, ref clsid, ref gp, p.Hkl, TF_IPPMF_FORSESSION | TF_IPPMF_DONTCARECURRENTINPUTLANGUAGE);
            Marshal.ReleaseComObject(obj);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }

        var fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            var hkl = p.Hkl != IntPtr.Zero ? p.Hkl : new IntPtr(p.LangId | (p.LangId << 16));
            Native.PostMessage(fg, WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
        }
    }

    /// <summary>任务栏上显示的简短状态：中 / 英 / EN / あ ……</summary>
    public static string Indicator()
    {
        ushort lang = CurrentLangId();
        int primary = lang & 0x3FF;
        bool? native = NativeMode();
        return primary switch
        {
            0x04 => native == false ? "英" : "中",   // 中文
            0x11 => native == false ? "A" : "あ",     // 日文
            0x12 => native == false ? "A" : "한",     // 韩文
            _ => SafeCulture(lang)?.TwoLetterISOLanguageName.ToUpperInvariant() ?? "EN",
        };
    }

    public static string CurrentLanguageName() => LangName(CurrentLangId());

    static ushort CurrentLangId()
    {
        var fg = Native.GetForegroundWindow();
        uint tid = fg == IntPtr.Zero ? 0 : Native.GetWindowThreadProcessId(fg, out _);
        return (ushort)(GetKeyboardLayout(tid).ToInt64() & 0xFFFF);
    }

    /// <summary>输入法是否处于“本地语言”模式（中文模式）。读不到时返回 null。</summary>
    static bool? NativeMode()
    {
        var fg = Native.GetForegroundWindow();
        var ime = fg == IntPtr.Zero ? IntPtr.Zero : ImmGetDefaultIMEWnd(fg);
        if (ime == IntPtr.Zero) return null;
        if (Native.SendMessageTimeout(ime, WM_IME_CONTROL, new IntPtr(IMC_GETOPENSTATUS), IntPtr.Zero, 0x2, 50, out var open) == IntPtr.Zero) return null;
        if (open == IntPtr.Zero) return false;
        if (Native.SendMessageTimeout(ime, WM_IME_CONTROL, new IntPtr(IMC_GETCONVERSIONMODE), IntPtr.Zero, 0x2, 50, out var mode) == IntPtr.Zero) return null;
        return (mode.ToInt64() & 1) != 0;   // IME_CMODE_NATIVE
    }

    static string LangName(ushort lang) => SafeCulture(lang)?.DisplayName ?? $"0x{lang:X4}";

    static CultureInfo SafeCulture(ushort lang)
    {
        try { return CultureInfo.GetCultureInfo(lang); } catch { return null; }
    }
}
