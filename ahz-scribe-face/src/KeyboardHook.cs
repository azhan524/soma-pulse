using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AhzScribe.Face
{
    // Private global hotkey: Ctrl+Alt+Space. Uses a low-level hook so it does not
    // depend on RegisterHotKey or on the main window being visible.
    public static class KeyboardHook
    {
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;
        const int VK_SPACE = 0x20;
        static IntPtr _hook;
        static LowLevelProc _proc;
        static Action _show;

        public static void Install(Action show)
        {
            _show = show;
            _proc = Callback;
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        }

        static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
                {
                    var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    if (info.vkCode == VK_SPACE && (GetAsyncKeyState(0x11) & 0x8000) != 0 && (GetAsyncKeyState(0x12) & 0x8000) != 0)
                    {
                        if (_show != null) _show();
                        return (IntPtr)1;
                    }
                }
            }
            catch { }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }
        delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc proc, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
    }
}
