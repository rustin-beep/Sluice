using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Sluice;

internal sealed class GateApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var w = new GateWindow();
            desktop.MainWindow = w;
            w.Show();
            // 窗口关闭（任何路径）即退出 lifetime；Gate.Run 的线程随后返回
        }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>弹批准窗并阻塞至窗口关闭。窗口线程独立于调用线程（console 主线程）。</summary>
internal static class Gate
{
    /// <summary>
    /// C4（2026-10-30 实战）：批准窗口超时秒数。config.json "gate_timeout_sec" 可配，
    /// 默认 60，夹 [30,600]。agent 同用户可改此文件，但改短只会自伤（自动拒是安全侧）、
    /// 改长不产生任何写动作——纯 UX 旋钮，无安全害处，故不做防篡改。
    /// </summary>
    public static int TimeoutSeconds { get; } = ReadTimeout();

    private static int ReadTimeout()
    {
        try
        {
            var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Sluice", "config.json");
            if (!File.Exists(cfg)) return 60;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg));
            return doc.RootElement.TryGetProperty("gate_timeout_sec", out var v)
                && v.TryGetInt32(out var sec) ? Math.Clamp(sec, 30, 600) : 60;
        }
        catch { return 60; }
    }

    public sealed record Request(string Op, string Repo, string Target, string Body, string Sha8,
        byte[] VaultChallenge, string? Warning = null);

    public abstract record Outcome
    {
        public sealed record Denied(string Preset, string? FreeText) : Outcome();
        public sealed record TimedOut() : Outcome();
        public sealed record ConsentFailed() : Outcome();
        public sealed record Approved(byte[] Signature) : Outcome();
        public sealed record VaultKeyMissing(string Detail) : Outcome();
        public sealed record Crashed(string Error) : Outcome();
    }

    public static Request? Req { get; set; }
    public static Outcome? Result { get; set; }

    /// <summary>在独立 STA 线程上起 Avalonia，阻塞直到窗口关闭。返回窗口设置的结果（null 理论不可能：关窗=Timeout）。</summary>
    public static Outcome Run(Request req)
    {
        Req = req;
        Result = null;
        var t = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<GateApp>()
                    .UsePlatformDetect()
                    .LogToTrace()
                    .StartWithClassicDesktopLifetime([]);
            }
            catch (Exception ex)
            {
                // UI 线程任何崩溃都收敛为 Crashed，让主线程给出 [Sluice·终态] internal 消息而非裸崩
                Result = new Outcome.Crashed($"{ex.GetType().Name}: {ex.Message}");
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return Result ?? new Outcome.TimedOut();
    }
}
