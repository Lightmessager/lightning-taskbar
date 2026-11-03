using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LightningTaskbar;

/// <summary>用 IShellLinkW 读取 .lnk 的目标与图标位置（比 WScript.Shell 更可靠，且支持中文文件名）。</summary>
internal static class Shortcut
{
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    interface IPersistFile
    {
        void GetClassID(out Guid clsid);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, int mode);
    }

    public readonly record struct Info(string Target, string IconPath, int IconIndex);

    public static Info Read(string lnk)
    {
        object obj = null;
        try
        {
            obj = new ShellLink();
            ((IPersistFile)obj).Load(lnk, 0);
            var link = (IShellLinkW)obj;

            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0x4 /*SLGP_RAWPATH*/);
            string target = Normalize(sb.ToString());

            sb.Clear();
            link.GetIconLocation(sb, sb.Capacity, out int index);
            string icon = Normalize(sb.ToString());
            return new Info(target, icon, index);
        }
        catch { return default; }
        finally { if (obj != null) Marshal.ReleaseComObject(obj); }
    }

    static string Normalize(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        p = Environment.ExpandEnvironmentVariables(p.Trim().Trim('"'));
        try { return Path.GetFullPath(p); } catch { return p; }
    }
}
