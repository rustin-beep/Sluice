using System.Text.Json;

namespace Sluice;

/// <summary>§10：audit.jsonl 追加写。拒绝也记（理由是协商界面的一部分）。</summary>
internal static class Audit
{
    /// <summary>
    /// §9.7/§10 via 值域（v2.9）：调用来自哪个前门。MCP server spawn 子进程时注入
    /// SLUICE_VIA=mcp（§9.7）；纯标签非机密，伪造无增益——标注入口而非权限。
    /// </summary>
    private static string Via => Environment.GetEnvironmentVariable("SLUICE_VIA") == "mcp" ? "mcp" : "cli";

    /// <summary>v2.25 MCP 请求凭据（receipt）：server spawn 子进程时注入 SLUICE_MCP_REQ。
    /// 子进程的一切审计行（含 api_read 预读）随身携带——`sluice status` 按它定位终态。
    /// 同 via 纪律：纯标签非机密。</summary>
    private static string? Req => Environment.GetEnvironmentVariable("SLUICE_MCP_REQ");

    /// <summary>status 查询用：该 req 最后一条非 api_read 审计行（终态行）；无则 null。
    /// 同文件顺读（追加写、行级完整，逆读收益不值复杂度）。只读本地文件，无网络、无副作用。
    /// v2.27 增返 repo：rate_limited 收割复用 PushRateLimited(repo, branch, what) 需要（push 的
    /// 审计 target 是 branch 或重放形态 branch@旧→新，repo 另在行内 repo 字段）。</summary>
    public static (string Op, string? Repo, string? Target, string Decision, string? Preset, string? Reason, string Result)? LastFor(string req)
    {
        try
        {
            foreach (var line in File.ReadLines(PathFor()).Reverse())
            {
                if (!line.Contains($"\"req\":\"{req}\"", StringComparison.Ordinal)) continue;
                using var d = System.Text.Json.JsonDocument.Parse(line);
                var r = d.RootElement;
                if (r.TryGetProperty("op", out var op) && op.GetString() == "api_read") continue;
                return (op.GetString() ?? "?",
                    r.TryGetProperty("repo", out var rp) ? rp.GetString() : null,
                    r.TryGetProperty("target", out var t) ? t.GetString() : null,
                    r.TryGetProperty("decision", out var dec) ? dec.GetString() ?? "?" : "?",
                    r.TryGetProperty("reason_preset", out var p) ? p.GetString() : null,
                    r.TryGetProperty("reason_text", out var x) ? x.GetString() : null,
                    r.TryGetProperty("result", out var res) ? res.GetString() ?? "" : "");
            }
        }
        catch { /* 文件缺失/行损坏：视为未落盘，status 回 pending */ }
        return null;
    }

    private static string PathFor() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sluice", "audit.jsonl");

    public static void Append(string op, string? repo, string? target, string? bodySha256,
        string decision, string? reasonPreset = null, string? reasonText = null,
        string? consent = null, string result = "not_executed")
    {
        try
        {
            var path = PathFor();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("ts", DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
                w.WriteString("op", op);
                w.WriteString("repo", repo);
                w.WriteString("target", target);
                w.WriteString("body_sha256", bodySha256);
                w.WriteString("decision", decision);
                w.WriteString("reason_preset", reasonPreset);
                w.WriteString("reason_text", reasonText);
                w.WriteString("consent", consent);
                w.WriteString("result", result);
                w.WriteString("via", Via);
                if (Req is { } reqId) w.WriteString("req", reqId);
                w.WriteEndObject();
            }
            File.AppendAllBytes(path, ms.ToArray());
            File.AppendAllText(path, Environment.NewLine);
        }
        catch
        {
            // 审计失败不阻断主流程（§8 internal 已声明"审计可能缺行"）
        }
    }
}
