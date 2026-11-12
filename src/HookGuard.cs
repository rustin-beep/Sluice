using System.Text.Json;
using System.Text.RegularExpressions;

namespace Sluice;

/// <summary>
/// 宿主 PreToolUse 快失败层（spec-guard-20261112 层 B；plan-lite §6 ⓪' hook）。
/// stdin 收宿主工具调用 JSON → 封堵态下命中 GitHub 写模式 → stderr 指路 + exit 2（deny）；
/// 其余一切 exit 0 放行。层糕定位：①MCP schema / ①'AGENTS.md 片段（概率层）→ ⓪'hook（执行前
/// deny，结构层）→ ②notice（凭据索求时）→ ③seal → ④gate。本层是发现层的糖——处处 fail-open
/// （stdin 解析失败 / gitconfig 读不到 / 任何异常 → 放行），封堵墙（seal/notice/gate）不变；
/// 正则可绕 = T2 形状，不管（与 notice 的 -c 覆盖绕过同定性）。模式契约 = selftest 的
/// HookGuardChecks 矩阵（源自 2026-11-12 临时件 hook-guard.sh 的 29 例实弹矩阵）。
/// 状态感知不自持策略：notice helper 在位 = 封堵态；pause_until 未到期或 notice 被摘
/// （pause/unseal）= 放行——全部复用 SealCheck/SetupCommand 既有代码，勿造第二套。
/// </summary>
internal static partial class HookGuard
{
    public static int Run()
    {
        try
        {
            if (!Console.IsInputRedirected) return 0; // 交互式手跑无管道：fail-open（不挂等 stdin）
            var cmd = ExtractCommand(Console.In.ReadToEnd());
            if (string.IsNullOrEmpty(cmd)) return 0;
            // 先纯模式后状态：绝大多数工具调用（只读/无关）不命中模式，连 git 子进程都不必起
            if (!DenyCommand(cmd, out var kind)) return 0;
            if (!BlockedState()) return 0;
            Audit.Append("hook_guard", null, cmd.Length > 300 ? cmd[..300] : cmd, null,
                "hook_guard", reasonPreset: kind, result: "not_executed"); // result 与 notice 同形状；
            // kind（git_push/gh_write/gh_api_write/curl_write）入 reason_preset——T3 风暴归因多一个维度
            foreach (var line in Messages.HookGuardDeniedLines())
                Console.Error.WriteLine(line);
            return 2; // 宿主 PreToolUse deny 契约（ZCode / Claude Code 同）；host 面是二值契约，
                      // 非 §8.3 消息面——exit 2 在宿主侧固定表 deny，无 retry 语义槽位
        }
        catch { return 0; } // fail-open：本层任何故障不得让宿主工具调用失败
    }

    /// <summary>纯决策（selftest 锁）：模式命中 ∧ 封堵态（notice 在位 ∧ 非暂停窗）。
    /// hitKind 供审计归因（git_push / gh_write / gh_api_write / curl_write）。</summary>
    internal static bool Decide(string cmd, bool noticePresent, bool paused, out string? hitKind)
    {
        hitKind = null;
        if (paused || !noticePresent) return false;
        return DenyCommand(cmd, out hitKind);
    }

    /// <summary>模式判定（纯函数，矩阵入 selftest 锁）。逐门点名不搞通配大网——拦写形态、
    /// 放一切只读；多行命令按行语义（^/$ 多行模式，与临时件 grep 的逐行匹配一致）。</summary>
    internal static bool DenyCommand(string cmd, out string? hitKind)
    {
        if (GitPush().IsMatch(cmd)) { hitKind = "git_push"; return true; }
        if (GhWrite().IsMatch(cmd)) { hitKind = "gh_write"; return true; }
        if (GhApi().IsMatch(cmd) && WriteMethod().IsMatch(cmd)) { hitKind = "gh_api_write"; return true; }
        if (CurlCmd().IsMatch(cmd) && GhHost().IsMatch(cmd) && CurlWriteShape().IsMatch(cmd))
        { hitKind = "curl_write"; return true; }
        hitKind = null;
        return false;
    }

    /// <summary>宿主工具调用 JSON → tool_input.command（拿不到/形状不符 → null，调用方放行）。
    /// ZCode 与 Claude Code 的 PreToolUse 载荷同此形状。</summary>
    internal static string? ExtractCommand(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("tool_input", out var ti)
                && ti.TryGetProperty("command", out var c)
                && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>git config --get-all 输出中是否存在**任意路径**的 notice helper 值（比 doctor 的
    /// ProcessPath 精确匹配宽一档：exe 路径漂移期间（重 publish 后、setup --install 前）封堵态
    /// 依旧成立——hook 宁多拦一拍，路径漂移的报修归 doctor）。</summary>
    internal static bool AnyNoticeHelperPresent(string getAllOutput) =>
        getAllOutput.Split('\n').Any(l => l.Trim().Trim('"').EndsWith(" notice", StringComparison.Ordinal));

    /// <summary>封堵态 = 非 pause 窗 ∧ notice helper 在位。任何失败 → false（fail-open 放行）。</summary>
    private static bool BlockedState()
    {
        try
        {
            if (SealCheck.SealPause.Active) return false;
            return AnyNoticeHelperPresent(SetupCommand.GitOut("--get-all", "credential.https://github.com.helper"));
        }
        catch { return false; }
    }

    // git push（含 git -C dir push / 分隔符后 / env git push / --dry-run——训练面一致）
    [GeneratedRegex(@"(^|[;&|]\s*|env\s+)git(\s+[^;&|]*)?\s+push(\s|$)", RegexOptions.Multiline)]
    private static partial Regex GitPush();

    // gh：登录流 + 写动词族（§9 写面的直连对偶）
    [GeneratedRegex(@"gh\s+(auth\s+login|pr\s+(create|merge|close|edit|comment|ready|reopen|review|delete)|issue\s+(create|close|edit|comment|delete|reopen)|release\s+(create|delete|edit|upload)|repo\s+(create|delete|edit|archive)|label\s+(create|edit|delete)|workflow\s+run|run\s+rerun)")]
    private static partial Regex GhWrite();

    // gh api 带写方法（-X/--method，空格或 = 两种形态）
    [GeneratedRegex(@"(^|\s)gh(\s+[^;&|]*)?\s+api(\s|$)", RegexOptions.Multiline)]
    private static partial Regex GhApi();

    [GeneratedRegex(@"(-X|--method)(\s|=)(POST|PUT|PATCH|DELETE)")]
    private static partial Regex WriteMethod();

    // curl 打 GitHub API 且带写形态（显式方法，或 -d/--data/--upload-file/-T 这类隐式写）
    [GeneratedRegex(@"(^|\s)curl(\s|$)", RegexOptions.Multiline)]
    private static partial Regex CurlCmd();

    [GeneratedRegex(@"(api|uploads)\.github\.com")]
    private static partial Regex GhHost();

    [GeneratedRegex(@"(-X|--request)(\s|=)(POST|PUT|PATCH|DELETE)|--data(\s|=)|(\s|^)-d(\s|=)|--upload-file|(\s|^)-T(\s|$)", RegexOptions.Multiline)]
    private static partial Regex CurlWriteShape();
}
