using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Sluice;

/// <summary>
/// GitHub API 叶。双防线：
/// ① 解析层——api.github.com 一律经 IP 直连 DoH（阿里/腾讯公共 DNS）解析真实 IP 后直连，
///    系统 hosts 对 sluice 失效（Watt Toolkit 的 hosts+本地443 MITM 加速与其共存互不干扰，
///    恶意改 hosts 劫持 token 的攻击面在解析层消灭）；
/// ② 证书层——leaf issuer 必须是 GitHub 官方 CA（DigiCert/Sectigo），本地 MITM 签发的链拒发 token。
/// 可选 config.json 的 "proxy"（如 http://127.0.0.1:7890）显式走非 MITM 代理隧道。
/// </summary>
internal static class GitHubApi
{
    private const string BaseUrl = "https://api.github.com/";

    /// <summary>
    /// GitHub 官方在用的签发 CA。2026-10-29 实测真链路 issuer = Sectigo（*.github.com）；
    /// DigiCert 为历史 CA（plan.md 探针时代）。本地 MITM（SteamTools/Fiddler 等）签发的
    /// 链 issuer 不在此列即拒。GitHub 若再换 CA，拒绝消息会打印实际 issuer，加白即可。
    /// </summary>
    private static readonly string[] AllowedIssuers = ["DigiCert", "Sectigo"];

    /// <summary>最近一次被 issuer 校验拒绝的实际颁发者（诊断用， Describe 输出）。</summary>
    internal static string? LastRejectedIssuer;

    public enum OutcomeKind { Created, Unauthorized, NotSent, UnknownOutcome, Other }

    public sealed record ApiResult(OutcomeKind Kind, int Status = 0, string? Url = null, string? Reason = null);

    // ---------- 防线①：DoH 解析（绕过系统 hosts） ----------

    /// <summary>DoH 端点用裸 IP 直连（证书含 IP SAN），自身解析不依赖被劫持的 hosts。</summary>
    private static readonly (string Ip, string Name)[] DohEndpoints =
    [
        ("223.5.5.5", "阿里公共 DNS"),   // dns.alidns.com
        ("120.53.53.53", "腾讯公共 DNS"), // doh.pub
    ];

    private sealed record DnsCache(string[] Ips, DateTime Expires);
    private static DnsCache? _dnsCache; // 引用原子交换，无锁；读侧容忍短暂陈旧

    /// <summary>解析 api.github.com 的真实 IP；全失败返回 null（调用方退回系统解析，由 issuer 校验兜底）。</summary>
    internal static async Task<string[]?> ResolveViaDoHAsync(CancellationToken ct)
    {
        var c = _dnsCache;
        if (c is { } cc && cc.Expires > DateTime.UtcNow)
            return cc.Ips;

        foreach (var (ip, _) in DohEndpoints)
        {
            try
            {
                using var doh = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var resp = await doh.GetAsync($"https://{ip}/resolve?name=api.github.com&type=A", ct);
                if (!resp.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                // 结构：{"Status":0,"Answer":[{"name":"api.github.com.","type":1,"TTL":..,"data":"140.82.x.x"}]}
                var list = new List<string>();
                if (doc.RootElement.TryGetProperty("Answer", out var answers))
                    foreach (var a in answers.EnumerateArray())
                        if (a.TryGetProperty("type", out var t) && t.GetInt32() == 1 &&
                            a.TryGetProperty("data", out var d))
                            list.Add(d.GetString() ?? "");
                if (list.Count == 0) continue;
                var arr = list.ToArray();
                _dnsCache = new DnsCache(arr, DateTime.UtcNow.AddMinutes(5));
                return arr;
            }
            catch { /* 换下一个 DoH 端点 */ }
        }
        return null;
    }

    private static SocketsHttpHandler CreateHandler()
    {
        var h = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (msg, cert, chain, errors) =>
                {
                    if (errors != SslPolicyErrors.None)
                        return false;
                    // 链有效前提下，leaf issuer 必须落在 GitHub 官方 CA 集合；
                    // 本地 MITM（SteamTools 等）签发的链在这里被拒。
                    var issuer = cert?.Issuer ?? "";
                    var ok = AllowedIssuers.Any(k => issuer.Contains(k, StringComparison.OrdinalIgnoreCase));
                    if (!ok) LastRejectedIssuer = issuer;
                    return ok;
                },
            },
            ConnectCallback = async (ctx, ct) =>
            {
                // 只对 api.github.com 启用 DoH 直连；DoH 失败退回系统解析（hosts 若被劫持由 issuer 校验拦截并报清晰错误）
                if (ctx.DnsEndPoint.Host == "api.github.com" &&
                    await ResolveViaDoHAsync(ct) is { Length: > 0 } ips)
                {
                    Exception? last = null;
                    foreach (var ip in ips)
                    {
                        try
                        {
                            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                            await s.ConnectAsync(new IPEndPoint(IPAddress.Parse(ip), ctx.DnsEndPoint.Port), ct);
                            return new NetworkStream(s, ownsSocket: true);
                        }
                        catch (Exception ex) { last = ex; }
                    }
                    if (last is not null) throw last;
                }
                // 默认路径：系统解析
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(ctx.DnsEndPoint, ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };

        // 可选显式代理（config.json: {"proxy":"http://127.0.0.1:7890"}）——非 MITM 隧道形态，
        // TLS 端到端，issuer 校验照常生效。
        var proxy = ReadConfiguredProxy();
        if (proxy is not null)
            h.Proxy = new WebProxy(proxy);

        return h;
    }

    private static string? ReadConfiguredProxy()
    {
        try
        {
            var cfg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sluice", "config.json");
            if (!File.Exists(cfg)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            return doc.RootElement.TryGetProperty("proxy", out var p) ? p.GetString() : null;
        }
        catch { return null; }
    }

    private static HttpClient CreateClient() => new(CreateHandler(), disposeHandler: true)
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static HttpRequestMessage Req(HttpMethod m, string path, string token) => new(m, BaseUrl + path)
    {
        Headers =
        {
            { "Accept", "application/vnd.github+json" },
            { "X-GitHub-Api-Version", "2022-11-28" },
            { "Authorization", "Bearer " + token },
            { "User-Agent", "sluice/0.1" },
        },
    };

    /// <summary>非幂等 POST 的三分类（§8.2）：未发出 / 已发出结果不明 / 明确结果。</summary>
    public static async Task<ApiResult> PostCommentAsync(string token, string repo, int number, string body)
    {
        using var http = CreateClient();
        try
        {
            using var req = Req(HttpMethod.Post, $"repos/{repo}/issues/{number}/comments", token);
            req.Content = new StringContent(
                $$"""{"body":{{JsonEscape(body)}}}""", Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            return (int)resp.StatusCode switch
            {
                201 => await ReadCreatedAsync(resp),
                401 or 403 => new ApiResult(OutcomeKind.Unauthorized, (int)resp.StatusCode),
                >= 500 => new ApiResult(OutcomeKind.UnknownOutcome, (int)resp.StatusCode,
                    $"HTTP {(int)resp.StatusCode}"),
                _ => new ApiResult(OutcomeKind.Other, (int)resp.StatusCode,
                    $"HTTP {(int)resp.StatusCode}"),
            };
        }
        catch (HttpRequestException ex)
        {
            // DNS / TCP / TLS（含 issuer 校验拒绝）：请求未发出，无副作用
            return new ApiResult(OutcomeKind.NotSent, Reason: Describe(ex));
        }
        catch (TaskCanceledException)
        {
            return new ApiResult(OutcomeKind.UnknownOutcome, Reason: "超时"); // 请求可能已送达
        }
    }

    /// <summary>根因中文化；issuer 校验拒绝时打印实际 issuer（区分官方新 CA 与本地 MITM）。</summary>
    internal static string Describe(Exception ex)
    {
        var root = ex;
        while (root.InnerException is not null) root = root.InnerException;
        if (root is System.Security.Authentication.AuthenticationException ||
            root.Message.Contains("RemoteCertificateValidationCallback", StringComparison.Ordinal))
        {
            return $"证书颁发者校验失败——实际 issuer：{LastRejectedIssuer ?? "(未记录)"}；允许：{string.Join("/", AllowedIssuers)}。"
                 + "实际 issuer 若是 SteamTools/Fiddler/Clash 等本地代理证书，请关闭其对 GitHub 的劫持；"
                 + "若是 GitHub 官方新换的 CA，请将 issuer 关键词加入白名单后重编译";
        }
        return root.Message;
    }

    private static async Task<ApiResult> ReadCreatedAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        // 只取 html_url 一个字段，避免引 JSON 库
        const string key = "\"html_url\":\"";
        var i = json.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return new ApiResult(OutcomeKind.Other, 201, Reason: "响应缺少 html_url");
        var j = json.IndexOf('"', i + key.Length);
        var url = json[(i + key.Length)..j];
        return new ApiResult(OutcomeKind.Created, 201, Url: url);
    }

    public static string JsonEscape(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>setup 校验：GET /user。返回 (ok, login 或错误描述)。</summary>
    public static async Task<(bool Ok, string Info)> ValidateTokenAsync(string token)
    {
        // 诊断（仅 setup，人运行；不进 §8 消息流）：显示 DoH 解析结果——该结果来自
        // IP 直连的公共 DNS over HTTPS，不经过系统 hosts，劫持是否存在一目了然。
        var doh = await ResolveViaDoHAsync(CancellationToken.None);
        Console.WriteLine(doh is { Length: > 0 }
            ? $"[net] api.github.com → {doh[0]}（DoH 解析，绕过系统 hosts；候选 {doh.Length} 个）"
            : "[net] DoH 解析不可用——退回系统解析（若 hosts 被劫持将由证书校验拦截）");
        using var http = CreateClient();
        try
        {
            using var req = Req(HttpMethod.Get, "user", token);
            using var resp = await http.SendAsync(req);
            if (resp.StatusCode == HttpStatusCode.Unauthorized ||
                resp.StatusCode == HttpStatusCode.Forbidden)
                return (false, "GitHub 返回 401/403：token 无效或已过期");
            var json = await resp.Content.ReadAsStringAsync();
            const string key = "\"login\":\"";
            var i = json.IndexOf(key, StringComparison.Ordinal);
            var login = i >= 0 ? json[(i + key.Length)..json.IndexOf('"', i + key.Length)] : "?";
            return (true, login);
        }
        catch (Exception ex)
        {
            return (false, Describe(ex));
        }
    }
}
