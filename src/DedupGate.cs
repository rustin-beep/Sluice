namespace Sluice;

/// <summary>
/// v2.31 同操作未决去重 + pending 行可见化；v2.32 审阅修复批（P1/P2/P3.1/P3.5）规则升版。
/// 缘起 E0-20261108 实弹（rustin-beep/vivarium v3-libc）：agent 在上一枚回执未决时两次重发，
/// 同目标三窗并发堆叠，用户为一次逻辑操作点了 6 次批准窗；且未决请求在台账中零痕迹。
/// 机制：每个写请求进入闸门前先落一行 decision=pending 审计行（req 随 SLUICE_MCP_REQ 随身，
/// CLI 来源无 req；result 携带占位 nonce）；pending 自此对 sluice_status 与本去重全程可见。
/// v2.32 占位-扫描序（审阅 P2 check-then-act 闭口）：**先 append 占位、后扫描**——pending 行
/// 本身就是锁，文件行序即全序；并发双发时只有"最早一行"的请求胜出开窗，后来者写
/// dedup_blocked 自赎（同 req 行相互解析）并退让。去重是 UX 面不是安全面：文件缺失/损坏
/// 一律视作无 pending（fail-open）。
/// </summary>
internal static class DedupGate
{
    /// <summary>pending 行超过该窗龄即视为孤儿（窗口 Gate.TimeoutSeconds 必自动超时落终态行，
    /// 再留 120 秒覆盖慢执行链）。孤儿不阻塞新请求——死窗若永久占坑，同目标会被无限锁死，
    /// 比窗口堆叠更糟；孤儿态由 sluice_status 以 StatusOrphan 点名（终态不会再来，按结果未知查证）。</summary>
    public static TimeSpan StaleAfter => TimeSpan.FromSeconds(Gate.TimeoutSeconds + 120);

    /// <summary>去重视角的行摘要（收割视角是 Audit.LastFor，两者字段需求不同）。
    /// Result 携带 pending 占位 nonce（"window:{nonce}"）与 dedup_blocked 的指向（"pending:{...}"）。</summary>
    internal sealed record Row(DateTime Ts, string? Req, string Op, string? Repo, string Target, string Decision,
        string Result = "");

    /// <summary>占位-扫描（v2.32）：ownNonce = 本请求刚落的占位行标识。查同 (op, repo, target)
    /// 的**更早**存活未决请求（文件行序即全序——并发双发只有最早者胜出）：返回其 req
    /// （CLI 来源无 req 时返回 "cli"），无则 null（本请求胜出）。</summary>
    public static string? FindPending(string op, string? repo, string target, string? ownNonce = null) =>
        FindPendingCore(ReadRecentRows(), op, repo, target, DateTime.UtcNow, StaleAfter, ownNonce);

    /// <summary>
    /// 纯核心（selftest 锁，v2.32 规则升版）。rowsDesc = 逆序（新行在前，index 0 最新）的非
    /// api_read 审计行。判定规则：
    /// ① 非终态行两类——pending（占位/活窗）与 dedup_blocked（自赎行：只解析**同 req** 的
    /// pending——竞态败者写的，req=null 的 CLI 自赎行解析不了自己，但其幽灵 pending 会与
    /// CLI 胜者同生共死（同目标终态行一到双双解析），无害；dedup_blocked **绝不**置
    /// matchedTerminalSeen——否则 CLI 败者行会错放 CLI 胜者的活窗（E0 复燃，审阅 S6b 变异点））；
    /// ② 真终态行（其余一切 decision）——req 非空则 req 进 resolved（收割该 req 的一切 pending）；
    /// 且若 Matches 则置 matchedTerminalSeen（CLI 行 req=null 无凭据关联，按同目标终态关联）；
    /// ③ Matches：op/repo 相等 + target 字面相等，或 push 的重放形态 target（"{branch}@旧→新"）
    /// 前缀匹配裸分支名（v2.32 P3.1——重放终态行此前永远关联不上 CLI pending，白锁 180s；
    /// "@" 界标防 v3/v3-libc 前缀误撞）；
    /// ④ 胜负（v2.32 占位-扫描）：ownNonce 行 = 自己的占位（index 记为 ownIdx，找不到 = -1
    /// 保守视为最新）；**仅 index &gt; ownIdx 的存活 pending（文件序更早）能胜我**；多个胜者中
    /// 返回 index 最大（最早）的那个——它是真正持窗者；无胜者 → null。
    /// </summary>
    internal static string? FindPendingCore(IReadOnlyList<Row> rowsDesc, string op, string? repo, string target,
        DateTime nowUtc, TimeSpan staleAfter, string? ownNonce = null)
    {
        var resolved = new HashSet<string>();
        var matchedTerminalSeen = false;
        var ownIdx = -1;
        for (var i = 0; i < rowsDesc.Count; i++)
            if (ownNonce is not null && rowsDesc[i].Decision == "pending" && rowsDesc[i].Result == $"window:{ownNonce}")
            { ownIdx = i; break; }

        bool Matches(Row r) => r.Op == op && r.Repo == repo
            && (r.Target == target
                || (op == "push" && r.Target.StartsWith(target + "@", StringComparison.Ordinal)));
        bool Live(Row r, int i) =>
            i > ownIdx
            && !(r.Req is { } rq && resolved.Contains(rq))
            && !(r.Req is null && matchedTerminalSeen)
            && nowUtc - r.Ts <= staleAfter;

        string? winner = null;
        var winnerIdx = -1;
        for (var i = 0; i < rowsDesc.Count; i++)
        {
            var r = rowsDesc[i];
            if (r.Decision != "pending")
            {
                if (r.Decision != "dedup_blocked")
                {
                    if (Matches(r)) matchedTerminalSeen = true;
                }
                if (r.Req is { } q) resolved.Add(q); // 真终态与 dedup_blocked 同：req 非空即收割该 req
                continue;
            }
            if (!Matches(r)) continue;
            if (!Live(r, i)) continue;
            if (i > winnerIdx) { winner = r.Req ?? "cli"; winnerIdx = i; } // 最早存活者持窗
        }
        return winner;
    }

    /// <summary>请求进入闸门前先落占位行（v2.32：占位先于扫描——pending 行本身就是锁）。
    /// result 携带占位 nonce，扫描时据此排除自己。</summary>
    public static void MarkPending(string op, string? repo, string target, string? sha8, string nonce) =>
        Audit.Append(op, repo, target, sha8, "pending", result: $"window:{nonce}");

    private const int MaxScan = 2000;

    /// <summary>逆序有界读（v2.32 环形缓冲：顺序单遍读文件、只留末 MaxScan 行——Reverse()
    /// 物化全文件，审计只增不减，4200 行起每写请求 O(全文件) 不划算；fresh pending 必在近尾部）。</summary>
    private static IReadOnlyList<Row> ReadRecentRows()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Sluice", "audit.jsonl");
        var ring = new Queue<string>();
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                ring.Enqueue(line);
                if (ring.Count > MaxScan) ring.Dequeue();
            }
        }
        catch { /* 文件缺失/行损坏：视作无 pending（fail-open） */ }
        var arr = ring.ToArray(); // 文件序（旧→新）
        var list = new List<Row>(arr.Length);
        for (var i = arr.Length - 1; i >= 0; i--) // 逆序：新行在前（FindPendingCore 的约定）
            if (ParseRow(arr[i]) is { } r && r.Op != "api_read")
                list.Add(r);
        return list;
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
                dec.GetString() ?? "?",
                r.TryGetProperty("result", out var res) ? res.GetString() ?? "" : "");
        }
        catch { return null; }
    }
}
