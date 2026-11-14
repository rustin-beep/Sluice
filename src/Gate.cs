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

    /// <summary>
    /// v2.25：MCP 前门"未决即回 pending"的等待秒数（config.json "mcp_pending_sec"，默认 15，
    /// 夹 [5,55]）。必须小于一切已知 MCP 宿主的工具调用超时（实测 ZCode 系 ~30s）——
    /// 否则终态落在客户端离场之后，deny_reason 一世沉没（2026-11-04 实弹根因）。
    /// 纯 UX 旋钮：改大 = 更少走 pending 轮询、更大风险撞客户端超时，自伤侧。
    /// </summary>
    public static int McpPendingSec { get; } = ReadMcpPending();

    private static int ReadMcpPending()
    {
        try
        {
            var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Sluice", "config.json");
            if (!File.Exists(cfg)) return 15;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg));
            return doc.RootElement.TryGetProperty("mcp_pending_sec", out var v)
                && v.TryGetInt32(out var sec) ? Math.Clamp(sec, 5, 55) : 15;
        }
        catch { return 15; }
    }

    /// <summary>
    /// D1（2026-10-31 用户决策）：config.json "hello": false 时 vault 走静默降级格式
    /// （Windows = SLCV2/DPAPI；Linux v2.38 = SLCF1/0600 明文文件），批准点击即批准事件，
    /// 不再要求同意因子。默认 true = 强档（Windows = Hello 签名派生密钥，一次生物事件 = 解锁+授权；
    /// Linux = 口令签名派生密钥，一次口令输入 = 解锁+授权——等价 T1.5 主防线）。这是 owner 显式的
    /// 防护降级：批准事件从到场因子降为桌面事件、vault 降为同用户可静默读取（并发 agent/后台进程
    /// 不被 sluice 阻塞；Windows 另有 InputFilter 用户态拦截，Linux 无——§14 L15）。agent 也可改此
    /// 文件，但改 false = 降低自身防护（自伤侧），无防篡改必要。
    /// </summary>
    public static bool HelloEnabled { get; } = ReadHello();

    private static bool ReadHello()
    {
        try
        {
            var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Sluice", "config.json");
            if (!File.Exists(cfg)) return true;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg));
            // 仅显式 false 才降级；缺失/true/其它值一律保持 Hello
            return !(doc.RootElement.TryGetProperty("hello", out var v)
                && v.ValueKind == System.Text.Json.JsonValueKind.False);
        }
        catch { return true; }
    }

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
                // UI 线程任何崩溃都收敛为 Crashed，让主线程给出 [Sluice·终态] internal 消息而非裸崩。
                // 诊断保真（v2.18）：TypeInitializationException 的外层 Message 是通用占位句、不含出错
                // 类型——10-30×2 / 11-01×4 六次复发只留一句话无法归因。内层异常链 + 最内层堆栈帧
                // （AOT 帧名含类型与静态构造器名，是唯一定位线索）随身进审计与 stderr，下次复发当场可定位。
                var detail = DescribeCrash(ex);
                Result = new Outcome.Crashed(detail);
                try { Console.Error.WriteLine($"[Sluice] gate 线程异常详情：{detail}"); } catch { }
            }
        });
        // v2.38 Linux：STA 是 Win32 概念（Unix 上 SetApartmentState 无意义）——Windows 面保持原行为
        if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return Result ?? new Outcome.TimedOut();
    }

    /// <summary>
    /// v2.18 崩溃诊断保真（纯函数，selftest 锁）：异常链外→内扁平化（" &lt;- " 连接），
    /// 附最内层堆栈前 3 帧（" | " 连接），截 600 字符。静态构造器崩溃的类型名只存在于
    /// InnerException 与其堆栈帧里——只记外层类型名+Message 等于每次复发都从零开始。
    /// </summary>
    internal static string DescribeCrash(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (sb.Length > 0) sb.Append(" <- ");
            sb.Append(e.GetType().FullName).Append(": ").Append(e.Message);
        }
        var stack = ex.GetBaseException().StackTrace ?? ex.StackTrace ?? "";
        var frames = string.Join(" | ", stack.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains(" at ")).Take(3).Select(l => l.Trim()));
        if (frames.Length > 0) sb.Append(" @ ").Append(frames);
        return sb.Length > 600 ? sb.ToString(0, 600) + "…" : sb.ToString();
    }
}
