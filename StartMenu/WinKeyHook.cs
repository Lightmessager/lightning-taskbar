using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LightningTaskbar.StartMenu;

/// <summary>
/// 接管单独按下的 Win 键：在 Win 抬起前注入一个无意义按键，让系统开始菜单不弹出，转而打开我们的菜单。
/// Win+E、Win+D 等组合键不受影响；我们自己注入的按键（如“打开系统开始菜单”）直接放行。
/// </summary>
internal sealed class WinKeyHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_DUMMY = 0xE8;
    const uint LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    readonly HookProc _proc;
    readonly IntPtr _hook;
    readonly Action _onWinTap;
    bool _winDown, _combo;

    public WinKeyHook(Action onWinTap)
    {
        _onWinTap = onWinTap;
        _proc = Callback;
        using var mod = Process.GetCurrentProcess().MainModule;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(mod!.ModuleName), 0);
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (k.flags & LLKHF_INJECTED) != 0;
            int msg = wParam.ToInt32();
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN, up = msg is WM_KEYUP or WM_SYSKEYUP;
            bool isWin = k.vkCode is VK_LWIN or VK_RWIN;

            if (!injected)
            {
                if (isWin && down) { if (!_winDown) { _winDown = true; _combo = false; } }
                else if (down && _winDown) _combo = true;
                else if (isWin && up && _winDown)
                {
                    _winDown = false;
                    if (!_combo)
                    {
                        // 夹一个无意义按键，系统就会认为这是组合键，不再弹出原生开始菜单
                        Native.keybd_event(VK_DUMMY, 0, 0, UIntPtr.Zero);
                        Native.keybd_event(VK_DUMMY, 0, 2, UIntPtr.Zero);
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(_onWinTap);
                    }
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }
}
