using System.Runtime.InteropServices;

namespace ProbeApp;

internal static class HookFilter
{
    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const uint LLMHF_INJECTED = 0x1;
    private const uint LLMHF_LOWER_IL_INJECTED = 0x2;
    private const int LLKHF_INJECTED = 0x10;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    private static HookProc? _mouseProc;
    private static HookProc? _kbdProc;
    private static IntPtr _mouseHook;
    private static IntPtr _kbdHook;

    /// <summary>Log every keydown with the injected flag (P5 evidence).</summary>
    public static bool VerboseKbd;
    /// <summary>Observe-only: never drop injected input (probe mode; the gate uses blocking mode).</summary>
    public static bool ObserveOnly;

    public static void Install()
    {
        var hMod = LoadLibrary("user32.dll");
        _mouseProc = MouseProc;
        _kbdProc = KbdProc;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
        _kbdHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbdProc, hMod, 0);
        Program.Log($"[hook] install: mouse={(_mouseHook != IntPtr.Zero)} kbd={(_kbdHook != IntPtr.Zero)} observeOnly={ObserveOnly}");
    }

    private static IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            if (msg is WM_LBUTTONDOWN or WM_LBUTTONUP)
            {
                // MSLLHOOKSTRUCT: pt(8) mouseData(4) flags@12 time(4) extra(8)
                var flags = (uint)Marshal.ReadInt32(lParam, 12);
                var injected = (flags & LLMHF_INJECTED) != 0 || (flags & LLMHF_LOWER_IL_INJECTED) != 0;
                if (injected && !ObserveOnly)
                {
                    Program.Log($"[hook] INJECTED mouse {(msg == WM_LBUTTONDOWN ? "down" : "up")} flags=0x{flags:X} -> DROPPED");
                    return (IntPtr)1;
                }
                Program.Log($"[hook] {(injected ? "INJECTED(observe)" : "real")} mouse 0x{msg:X} flags=0x{flags:X} passed");
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static IntPtr KbdProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                // KBDLLHOOKSTRUCT: vkCode(4) scanCode(4) flags@8 time(4) extra(8)
                var flags = Marshal.ReadInt32(lParam, 8);
                var vk = Marshal.ReadInt32(lParam, 0);
                var injected = (flags & LLKHF_INJECTED) != 0;
                if (VerboseKbd)
                    Program.Log($"[hook] kbd vk=0x{vk:X} flags=0x{flags:X} injected={injected}");
                if (injected && !ObserveOnly)
                {
                    Program.Log($"[hook] INJECTED key vk=0x{vk:X} -> DROPPED");
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_kbdHook, nCode, wParam, lParam);
    }
}
