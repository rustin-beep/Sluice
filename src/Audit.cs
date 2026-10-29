using System.Text.Json;

namespace Sluice;

/// <summary>§10：audit.jsonl 追加写。拒绝也记（理由是协商界面的一部分）。</summary>
internal static class Audit
{
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
