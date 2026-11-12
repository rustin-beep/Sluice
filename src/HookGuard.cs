using System.Text;
using System.Text.Json;

namespace Sluice;

/// <summary>
/// 宿主 PreToolUse 快失败层（spec-guard-20261112 层 B；plan-lite §6 ⓪' hook）。
/// stdin 收宿主工具调用 JSON → 命中 GitHub 写形态且处于封堵面 → stderr 指路 + exit 2（deny）；
/// 其余一切 exit 0 放行。层糕定位：①MCP schema / ①'AGENTS.md 片段（概率层）→ ⓪'hook（执行前
/// deny，结构层）→ ②notice（凭据索求时）→ ③seal → ④gate。本层是发现层的糖——处处 fail-open
/// （stdin 解析失败 / gitconfig 读不到 / 任何异常 → 放行），封堵墙（seal/notice/gate）不变；
/// 正则可绕 = T2 形状，不管（与 notice 的 -c 覆盖绕过同定性）。模式契约 = selftest 的
/// HookGuardChecks 矩阵（v2.36 临时件 29 例 + v2.37 审阅补例：命令位锚定、分隔符双向、
/// -X 紧贴、gh api -f 隐式 POST、前导空格）。
/// 状态感知不自持策略（v2.37 审阅 P3-7 修订）：notice 在位 = 封堵态；**暂停窗 = 仍拦**——窗内
/// GCM 恢复交互，agent 直推会重演"登录窗 + 挂起"的首错代价（L14 在窗口期原样复发）；人交互
/// 登录在自己的终端，不经过宿主 hook，不受影响。未封态（notice 被摘且非暂停，即 unseal）才放行。
/// </summary>
internal static class HookGuard
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
            Audit.Append("hook_guard", null, null, null, "hook_guard",
                reasonPreset: kind,
                // v2.37 审阅 P3-9：命令文本入 reason_text（target 语义留给 repo/path，与 notice 的
                // meta path 同位）；result 与 notice 同形状，kind（git_push/gh_write/gh_api_write/
                // curl_write）入 reason_preset——T3 风暴归因多一个维度
                reasonText: cmd.Length > 300 ? cmd[..300] : cmd, result: "not_executed");
            foreach (var line in Messages.HookGuardDeniedLines())
                Console.Error.WriteLine(line);
            return 2; // 宿主 PreToolUse deny 契约（ZCode / Claude Code 同）；host 面是二值契约，
                      // 非 §8.3 消息面——exit 2 在宿主侧固定表 deny，无 retry 语义槽位
        }
        catch { return 0; } // fail-open：本层任何故障不得让宿主工具调用失败
    }

    /// <summary>纯决策（selftest 锁）：模式命中 ∧ 封堵面（notice 在位 **或** 暂停窗——P3-7 修订：
    /// 暂停窗内仍拦；两者皆非 = unseal 未封态，放行）。</summary>
    internal static bool Decide(string cmd, bool noticePresent, bool paused, out string? hitKind)
    {
        hitKind = null;
        if (!noticePresent && !paused) return false;
        return DenyCommand(cmd, out hitKind);
    }

    // ---------- 模式判定（v2.37 审阅 P2-1：正则 → 分词语义） ----------
    // 正则形态的两漏（`git push;` 后随命令、`curl -XPOST` 紧贴）两误（`git log --grep push`、
    // `echo "gh pr merge"`）共同根因 = 没有命令位概念。改为 bash 语义的最小分词：按引号外分隔符
    // 切段、段内按空白分词（引号剥除合并），首词即命令位——git/gh/curl 都必须在命令位才判定，
    // 子命令同理（git 的首个非全局选项 token 必须恰为 push）。

    /// <summary>模式判定（纯函数，矩阵入 selftest 锁）。逐段判命令位：git → 子命令位必须恰为
    /// push；gh → 登录流/写动词族（子命令对）或 api 带写形态（显式方法三形态 / -f 族隐式 POST）；
    /// curl → 打 GitHub API 域且带写形态。</summary>
    internal static bool DenyCommand(string cmd, out string? hitKind)
    {
        hitKind = null;
        foreach (var seg in SplitSegments(cmd))
        {
            var t = Tokenize(seg);
            if (t.Count == 0) continue;
            // env 前缀（spec §2.3 形态）：跳过 env 自身、其选项与 VAR=VAL 赋值
            var i = 0;
            if (t[0] == "env")
                for (i = 1; i < t.Count && (t[i].StartsWith('-') || t[i].Contains('=')); i++) { }
            if (i >= t.Count) continue;
            switch (t[i])
            {
                case "git" when IsGitPush(t, i): hitKind = "git_push"; return true;
                case "gh" when GhHit(t, i) is { } gh: hitKind = gh; return true;
                case "curl" when IsCurlWrite(t, i): hitKind = "curl_write"; return true;
            }
        }
        return false;
    }

    /// <summary>git push 判定：全局选项逐一跳过后，首个非选项 token 必须恰为 push——
    /// `git log --grep push` 的 push 不在子命令位（审阅 P2-1 误拦修复）；带独立值的全局选项
    /// （-C/-c/--git-dir/--work-tree/--namespace/--exec-path）连值一起跳（`git -C D:/x push`
    /// 是矩阵既定拦截形态）。</summary>
    private static bool IsGitPush(List<string> t, int i)
    {
        string[] valOpts = ["-C", "-c", "--git-dir", "--work-tree", "--namespace", "--exec-path"];
        var j = i + 1;
        while (j < t.Count && t[j].StartsWith('-'))
        {
            if (valOpts.Contains(t[j]) && j + 1 < t.Count) j += 2;
            else j++;
        }
        return j < t.Count && t[j] == "push";
    }

    /// <summary>gh 判定：返回命中门类（gh_write / gh_api_write）或 null。-R/--repo 全局选项
    /// 带值跳过（`gh -R o/r pr merge` 合法形态）；写动词族按（子命令, 动词）对点名——gh 必须在
    /// 命令位，`echo "gh pr merge"` 不再误拦（审阅 P2-1）。</summary>
    private static string? GhHit(List<string> t, int i)
    {
        var j = i + 1;
        while (j + 1 < t.Count && (t[j] == "-R" || t[j] == "--repo")) j += 2;
        if (j >= t.Count) return null;
        var sub = t[j];
        if (sub == "api") return GhApiWriteShape(t, j + 1) ? "gh_api_write" : null;
        var subsub = j + 1 < t.Count ? t[j + 1] : null;
        return (sub, subsub) switch
        {
            ("auth", "login") => "gh_write",
            ("pr", "create" or "merge" or "close" or "edit" or "comment" or "ready" or "reopen" or "review" or "delete") => "gh_write",
            ("issue", "create" or "close" or "edit" or "comment" or "delete" or "reopen") => "gh_write",
            ("release", "create" or "delete" or "edit" or "upload") => "gh_write",
            ("repo", "create" or "delete" or "edit" or "archive") => "gh_write",
            ("label", "create" or "edit" or "delete") => "gh_write",
            ("workflow", "run") => "gh_write",
            ("run", "rerun") => "gh_write",
            _ => null,
        };
    }

    /// <summary>gh api 写形态：显式方法（-X/--method，空格、=、紧贴三形态，方法名大小写不敏感）
    /// 或隐式 POST——gh 语义下 --field 族 / --input 在场即默认 POST（审阅 P2-1 漏拦修复）。</summary>
    private static bool GhApiWriteShape(List<string> t, int start)
    {
        for (var j = start; j < t.Count; j++)
        {
            var tok = t[j];
            if (tok is "-X" or "--method")
            { if (j + 1 < t.Count && IsWriteMethod(t[j + 1])) return true; }
            else if (tok.StartsWith("--method=") && IsWriteMethod(tok["--method=".Length..])) return true;
            else if (tok.StartsWith("-X") && tok.Length > 2 && IsWriteMethod(tok[2..])) return true;
            if (tok is "-f" or "-F" or "--field" or "--raw-field" or "--input" ||
                tok.StartsWith("-f=") || tok.StartsWith("-F=") || tok.StartsWith("--field=") ||
                tok.StartsWith("--raw-field=") || tok.StartsWith("--input=")) return true;
        }
        return false;
    }

    /// <summary>curl 写形态：显式方法（-X/--request 三形态）或隐式写体（--data 族前缀、-d 独立/
    /// 紧贴、-T 独立/紧贴、--upload-file）。URL token 含 api/uploads.github.com 为 GitHub 面。</summary>
    private static bool IsCurlWrite(List<string> t, int i)
    {
        var github = false;
        for (var j = i + 1; j < t.Count && !github; j++)
            if (t[j].Contains("api.github.com", StringComparison.Ordinal) ||
                t[j].Contains("uploads.github.com", StringComparison.Ordinal)) github = true;
        if (!github) return false;
        for (var j = i + 1; j < t.Count; j++)
        {
            var tok = t[j];
            if (tok is "-X" or "--request")
            { if (j + 1 < t.Count && IsWriteMethod(t[j + 1])) return true; }
            else if (tok.StartsWith("--request=") && IsWriteMethod(tok["--request=".Length..])) return true;
            else if (tok.StartsWith("-X") && tok.Length > 2 && IsWriteMethod(tok[2..])) return true;
            if (tok.StartsWith("--data", StringComparison.Ordinal) || tok == "-d" ||
                (tok.StartsWith("-d") && tok.Length > 2) || tok == "-T" ||
                (tok.StartsWith("-T") && tok.Length > 2) || tok == "--upload-file") return true;
        }
        return false;
    }

    private static bool IsWriteMethod(string s) =>
        s.ToUpperInvariant() is "POST" or "PUT" or "PATCH" or "DELETE";

    /// <summary>引号外分隔切段（; &amp; | \n——&amp;&amp;/|| 连用产生空段自动跳过；引号内的
    /// 分隔符是字面量）。段首即命令位，天然覆盖前导空格/缩进、多行脚本、`cd x &amp;&amp; …`。</summary>
    private static IEnumerable<string> SplitSegments(string cmd)
    {
        var sb = new StringBuilder();
        var inS = false; var inD = false;
        foreach (var ch in cmd)
        {
            if (ch == '\'' && !inD) inS = !inS;
            else if (ch == '"' && !inS) inD = !inD;
            else if (!inS && !inD && ch is ';' or '&' or '|' or '\n')
            { if (sb.Length > 0) yield return sb.ToString(); sb.Clear(); }
            else sb.Append(ch);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>空白分词（引号剥除、相邻引号段合并——bash 语义 "git push" 是一个词；引号外
    /// 反斜杠转义取字面）。</summary>
    private static List<string> Tokenize(string seg)
    {
        var toks = new List<string>();
        var sb = new StringBuilder();
        var inS = false; var inD = false;
        var any = false;
        for (var i = 0; i < seg.Length; i++)
        {
            var ch = seg[i];
            if (ch == '\'' && !inD) { inS = !inS; any = true; }
            else if (ch == '"' && !inS) { inD = !inD; any = true; }
            else if (ch == '\\' && !inS && i + 1 < seg.Length) { sb.Append(seg[++i]); any = true; }
            else if (!inS && !inD && char.IsWhiteSpace(ch))
            { if (any) { toks.Add(sb.ToString()); sb.Clear(); any = false; } }
            else { sb.Append(ch); any = true; }
        }
        if (any) toks.Add(sb.ToString());
        return toks;
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

    /// <summary>封堵面 = notice 在位 **或** 暂停窗（v2.37 审阅 P3-7：暂停窗内仍拦——GCM 已恢复
    /// 交互，agent 直推 = 登录窗 + 挂起原样复发；人登录走自己终端不经宿主）。任何失败 → false
    ///（fail-open 放行）。</summary>
    private static bool BlockedState()
    {
        try
        {
            if (SealCheck.SealPause.Active) return true;
            return AnyNoticeHelperPresent(SetupCommand.GitOut("--get-all", "credential.https://github.com.helper"));
        }
        catch { return false; }
    }
}
