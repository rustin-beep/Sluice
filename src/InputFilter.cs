#if WINDOWS
using System.Runtime.InteropServices;

namespace Sluice;

/// <summary>
/// T1.5 防线（§7）：进程内 LL 钩子丢弃 injected 输入（探针 2026-10-28 正反对照通过）。
/// 钩子须装在有消息泵的线程（P4 注记）——独立 STA 线程 + GetMessage 泵。
/// </summary>
internal sealed class InputFilter : IDisposable
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
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);

    private static HookProc? _mouseProc;
    private static HookProc? _kbdProc;
    private static IntPtr _mouseHook;
    private static IntPtr _kbdHook;
    private static uint _pumpThreadId;
    private static int _dropped;
    private static readonly ManualResetEvent Installed = new(false);

    private readonly Thread _pump;

    private InputFilter(Thread pump) => _pump = pump;

    public static int DroppedCount => _dropped;

    public static InputFilter Install()
    {
        var f = new InputFilter(new Thread(PumpLoop) { IsBackground = true });
        f._pump.SetApartmentState(ApartmentState.STA);
        f._pump.Start();
        Installed.WaitOne(2000);
        return f;
    }

    private static void PumpLoop()
    {
        _pumpThreadId = (uint)Environment.CurrentManagedThreadId;
        var hMod = LoadLibrary("user32.dll");
        _mouseProc = MouseProc;
        _kbdProc = KbdProc;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
        _kbdHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbdProc, hMod, 0);
        Installed.Set();
        while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0) { }
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_kbdHook != IntPtr.Zero) UnhookWindowsHookEx(_kbdHook);
    }

    private static IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() is WM_LBUTTONDOWN or WM_LBUTTONUP)
        {
            var flags = (uint)Marshal.ReadInt32(lParam, 12);
            if ((flags & (LLMHF_INJECTED | LLMHF_LOWER_IL_INJECTED)) != 0)
            {
                Interlocked.Increment(ref _dropped);
                return (IntPtr)1; // drop
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static IntPtr KbdProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            var flags = Marshal.ReadInt32(lParam, 8);
            if ((flags & LLKHF_INJECTED) != 0)
            {
                Interlocked.Increment(ref _dropped);
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_kbdHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        PostThreadMessage(_pumpThreadId, 0x0012 /*WM_QUIT*/, IntPtr.Zero, IntPtr.Zero); // WM_QUIT
        // 泵线程卡住则放弃等待；钩子随进程退出卸载，不泄漏
        _pump.Join(1000);
        Installed.Dispose();
    }
}
#else
namespace Sluice;

/// <summary>
/// Linux 叶（v2.38）：LL 钩子是 Win32 概念，Linux 无用户态等价物——Install() 为 no-op。
/// T1.5 主防线本就是 IConsent 同意因子（口令——agent 点得动按钮打不出口令），钩子在 §7 里
/// 本就定性为"第二道、门槛性质"；X11 注入残余面 §14 L15 入册，Wayland 下注入不成立。
/// 调用点零改动：Program.cs 的 9 处 using 照旧，Linux 下拿到本空实现。
/// </summary>
internal sealed class InputFilter : IDisposable
{
    public static InputFilter Install() => new();
    public static int DroppedCount => 0;
    public void Dispose() { }
}
#endif
