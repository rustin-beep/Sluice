using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace ProbeApp;

internal static class Program
{
    private const string LogPath = @"D:\Sluice\probes\probe.log";

    public static void Log(string msg)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
        Console.WriteLine(line);
    }

    [STAThread]
    public static int Main(string[] args)
    {
        Log($"=== probe start: [{string.Join(' ', args)}]");
        if (args.Contains("--p7")) { NCryptProbe.RunAsync().GetAwaiter().GetResult(); return 0; }
        if (args.Contains("--p7c")) { NCryptProbe.RunSignDetAsync().GetAwaiter().GetResult(); return 0; }
        if (args.Contains("--p45")) { P45().GetAwaiter().GetResult(); return 0; }
        if (args.Contains("--hello-prompt")) { HelloPromptAsync().GetAwaiter().GetResult(); return 0; }
        if (args.Contains("--selftest")) { SelftestAsync().GetAwaiter().GetResult(); return 0; }

        HookFilter.Install();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        Log("=== probe exit");
        return 0;
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int ptX; public int ptY; }
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    private static void SendEsc()
    {
        keybd_event(0x1B, 0, 0, UIntPtr.Zero);
        keybd_event(0x1B, 0, 2, UIntPtr.Zero); // KEYEVENTF_KEYUP
    }

    private static async Task P45()
    {
        HookFilter.VerboseKbd = true;
        HookFilter.ObserveOnly = true;
        // LL hooks require the installing thread to pump messages: dedicated STA thread.
        var pump = new Thread(() =>
        {
            HookFilter.Install();
            while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0) { _ = m; }
        });
        pump.SetApartmentState(ApartmentState.STA);
        pump.IsBackground = true;
        pump.Start();
        await Task.Delay(600);
        Log("[p45] armed. Prompt rules: touch nothing at first; ESC auto-injected at t=8s and t=12s; if the prompt survives, type a few digits, then cancel manually.");
        var inject = Task.Run(async () =>
        {
            await Task.Delay(8000); SendEsc(); Log("[p45] ESC#1 injected (t=8s)");
            await Task.Delay(4000); SendEsc(); Log("[p45] ESC#2 injected (t=12s)");
        });
        try
        {
            var r = await Windows.Security.Credentials.UI.UserConsentVerifier.RequestVerificationAsync(
                "P4/P5 probe: wait; later type a few digits then cancel");
            Log($"[p45] prompt result = {r}");
        }
        catch (Exception ex) { Log($"[p45] prompt exception: {ex.Message}"); }
        await inject;
        Log("[p45] done");
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static async Task SelftestAsync()
    {
        var sw = Stopwatch.StartNew();

        try
        {
            var avail = await Windows.Security.Credentials.UI.UserConsentVerifier.CheckAvailabilityAsync();
            Log($"[selftest] Hello availability = {avail} (Available=0)");
        }
        catch (Exception ex) { Log($"[selftest] Hello availability FAILED: {ex.GetType().Name}: {ex.Message}"); }

        try
        {
            var secret = Encoding.UTF8.GetBytes("sluice-probe-token-123");
            var enc = ProtectedData.Protect(secret, null, DataProtectionScope.CurrentUser);
            var dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
            Log($"[selftest] DPAPI roundtrip ok={dec.AsSpan().SequenceEqual(secret)} cipherBytes={enc.Length}");
        }
        catch (Exception ex) { Log($"[selftest] DPAPI FAILED: {ex.GetType().Name}: {ex.Message}"); }

        sw.Stop();
        Log($"[selftest] done in {sw.ElapsedMilliseconds}ms");
    }

    private static async Task HelloPromptAsync()
    {
        Log("[hello] RequestVerificationAsync called, waiting for user...");
        var sw = Stopwatch.StartNew();
        try
        {
            var r = await Windows.Security.Credentials.UI.UserConsentVerifier.RequestVerificationAsync(
                "Sluice 探针：请验证 Windows Hello（指纹/PIN，或取消）");
            Log($"[hello] result = {r} ({sw.ElapsedMilliseconds}ms)");
        }
        catch (Exception ex)
        {
            Log($"[hello] FAILED: {ex.GetType().Name}: {ex.Message} ({sw.ElapsedMilliseconds}ms)");
        }
    }
}
