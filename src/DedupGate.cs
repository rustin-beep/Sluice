namespace Sluice;

/// <summary>
/// v2.31 同操作未决去重 + pending 行可见化。缘起 E0-20261108 实弹（rustin-beep/vivarium v3-libc）：
/// agent 在上一枚回执未决时两次重发，同目标三窗并发堆叠，用户为一次逻辑操作点了 6 次批准窗；
/// 且未决请求在台账中零痕迹——sluice_status 的"尚无终态"无法区分"窗口排队中"与"回执不存在"，
/// 终态行落地前调用方只能盲等。机制：每个写请求进入闸门前先落一行 decision=pending 审计行
/// （req 随 SLUICE_MCP_REQ 环境变量随身，CLI 来源无 req），pending 自此对 sluice_status
/// 与本去重全程可见；新请求撞上同 (op, repo, target) 的存活 pending → 不开新窗，
/// 落 dedup_blocked 行并入原回执（MCP）/返回等待指引（CLI）——"终态未定禁止重发"由
/// 调用方纪律升级为闸门强制。去重是 UX 面不是安全面：文件缺失/损坏一律视作无 pending（fail-open）。
/// </summary>
internal static class DedupGate
{
    /// <summary>pending 行超过该窗龄即视为孤儿（窗口 Gate.TimeoutSeconds 必自动超时落终态行，
    /// 再留 120 秒覆盖慢执行链）。孤儿不阻塞新请求——死窗若永久占坑，同目标会被无限锁死，
    /// 比窗口堆叠更糟；孤儿态由 sluice_status 以 StatusOrphan 点名（终态不会再来，按结果未知查证）。</summary>
    public static TimeSpan StaleAfter => TimeSpan.FromSeconds(Gate.TimeoutSeconds + 120);

    /// <summary>去重视角的行摘要（收割视角是 Audit.LastFor，两者字段需求不同）。</summary>
    internal sealed record Row(DateTime Ts, string? Req, string Op, string? Repo, string Target, string Decision);

    /// <summary>查同 (op, repo, target) 的存活未决请求：返回其 req（CLI 来源无 req 时返回 "cli"），无则 null。</summary>
    public static string? FindPending(string op, string? repo, string target) =>
        FindPendingCore(ReadRecentRows(), op, repo, target, DateTime.UtcNow, StaleAfter);

    /// <summary>
    /// 纯核心（selftest 锁）。rowsDesc = 逆序（新行在前）的非 api_read 审计行。存活判定：
    /// pending 行的 req 已被更晚的终态行收割（逆序先见终态即记 resolved）→ 已了结；
    /// CLI 行（req=null，无凭据可关联）按"更晚存在同目标真终态行"判定已了结——同目标
    /// 快速决策后不得把下一次请求锁到超龄；dedup_blocked 不是终态（它只拦了自己那枚请求，
    /// 原 pending 仍活）。超龄 pending（孤儿）一律放行。匹配键 = (op, repo, target) 字面相等
    /// ——target 用各 verb 的审计 target 形态（push 例外：统一用裸分支名，重放形态随内容变化
    /// 不可作键）。
    /// </summary>
    internal static string? FindPendingCore(IEnumerable<Row> rowsDesc, string op, string? repo, string target,
        DateTime nowUtc, TimeSpan staleAfter)
    {
        var resolved = new HashSet<string>();
        var matchedTerminalSeen = false;
        foreach (var r in rowsDesc)
        {
            var matches = r.Op == op && r.Repo == repo && r.Target == target;
            if (r.Decision != "pending")
            {
                if (r.Decision != "dedup_blocked")
                {
                    if (matches) matchedTerminalSeen = true;
                    if (r.Req is { } q) resolved.Add(q);
                }
                continue;
            }
            if (!matches) continue;
            if (r.Req is { } rq && resolved.Contains(rq)) continue;   // MCP 行：同 req 更晚已有终态
            if (r.Req is null && matchedTerminalSeen) continue;       // CLI 行：同目标更晚已有真终态
            if (nowUtc - r.Ts > staleAfter) continue;                 // 孤儿：不阻塞（终态不会再来）
            return r.Req ?? "cli";
        }
        return null;
    }

    /// <summary>请求进入闸门前落 pending 行——先写行再开窗，窗口等待期全程可查。</summary>
    public static void MarkPending(string op, string? repo, string target, string? sha8) =>
        Audit.Append(op, repo, target, sha8, "pending", result: "window");

    private const int MaxScan = 2000;

    /// <summary>逆序有界读（fresh pending 必在近尾部；全量收益不值复杂度，同 LastFor 纪律）。</summary>
    private static IEnumerable<Row> ReadRecentRows()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sluice", "audit.jsonl");
        var rows = new List<Row>();
        try
        {
            foreach (var line in File.ReadLines(path).Reverse().Take(MaxScan))
                if (ParseRow(line) is { } r && r.Op != "api_read") rows.Add(r);
        }
        catch { /* 文件缺失/行损坏：视作无 pending（fail-open） */ }
        return rows;
    }

    /// <summary>行解析（损坏行返 null 跳过，不拖垮整扫）。ts 解析失败落 MinValue ⇒ 必然超龄 ⇒ 放行，
    /// 与孤儿纪律一致。</summary>
    internal static Row? ParseRow(string line)
    {
        try
        {
            using var d = System.Text.Json.JsonDocument.Parse(line);
            var r = d.RootElement;
            if (!r.TryGetProperty("decision", out var dec)) return null;
            var ts = r.TryGetProperty("ts", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String
                && DateTime.TryParse(t.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var dt)
                ? dt : DateTime.MinValue;
            return new Row(ts,
                r.TryGetProperty("req", out var q) ? q.GetString() : null,
                r.TryGetProperty("op", out var o) ? o.GetString() ?? "?" : "?",
                r.TryGetProperty("repo", out var rp) ? rp.GetString() : null,
                r.TryGetProperty("target", out var tg) ? tg.GetString() ?? "" : "",
                dec.GetString() ?? "?");
        }
        catch { return null; }
    }
}
