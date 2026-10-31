using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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
    /// GitHub 官方在用的签发 CA。Sectigo 为 2026-10-29/30 两次实测主流 issuer（*.github.com，
    /// 覆盖 140.82.x 与 20.x 两段共 8 个边缘 IP）；DigiCert 为历史 CA（plan.md 探针时代）；
    /// 2026-10-30 真机间歇撞见 Microsoft TLS G2 RSA CA OCSP 02——GitHub（微软旗下）向自家
    /// PKI（Microsoft TLS RSA Root G2）灰度迁移的边缘节点已上线，命中即拒，故加白 "Microsoft TLS"
    /// （微软中间 CA 私钥不外流，本地 MITM 拿不到其签名，加白不破防）。
    /// GitHub 若再换 CA，拒绝消息会打印实际 issuer，加白即可。
    /// </summary>
    private static readonly string[] AllowedIssuers = ["DigiCert", "Sectigo", "Microsoft TLS"];

    /// <summary>最近一次被 issuer 校验拒绝的实际颁发者与时刻（诊断用， Describe 输出）。
    /// 静态值可能滞留自上一次发作——间歇性故障靠时间戳对上时间窗，而非误读为"刚刚"。</summary>
    internal static string? LastRejectedIssuer;
    internal static DateTime? LastRejectedAt;

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
    private static DnsCache? _dnsCache; // api.github.com 热路径（一次 push 最多 ~90 请求）；引用原子交换，无锁
    // CDN 域（*.githubusercontent.com——api 读通道跟随 logs 等端点的 302 签名 URL 时到达）：冷路径，小并发字典够用
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DnsCache> _cdnDnsCache = new();

    /// <summary>DoH 直连 + 重定向跟随共同的主机集合（v2.7）：api.github.com 与 GitHub CDN
    /// （*.githubusercontent.com）。logs 等 actions 端点 302 到 CDN 签名 URL——不跟随则永远
    /// 拿不到日志体（2026-10-30 实战：actions/jobs/{id}/logs http_failed ×8，agent 空转重试）。</summary>
    internal static bool IsGithubApiOrCdnHost(string host) =>
        host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析 GitHub 域名的真实 IP（绕过系统 hosts）；全失败返回 null（调用方退回系统解析，由 issuer 校验兜底）。</summary>
    internal static async Task<string[]?> ResolveViaDoHAsync(string host, CancellationToken ct)
    {
        if (host == "api.github.com")
        {
            if (_dnsCache is { } cc && cc.Expires > DateTime.UtcNow)
                return cc.Ips;
        }
        else if (_cdnDnsCache.TryGetValue(host, out var cached) && cached.Expires > DateTime.UtcNow)
            return cached.Ips;

        foreach (var (ip, _) in DohEndpoints)
        {
            try
            {
                using var doh = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var resp = await doh.GetAsync($"https://{ip}/resolve?name={Uri.EscapeDataString(host)}&type=A", ct);
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
                if (host == "api.github.com") _dnsCache = new DnsCache(arr, DateTime.UtcNow.AddMinutes(5));
                else _cdnDnsCache[host] = new DnsCache(arr, DateTime.UtcNow.AddMinutes(5));
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
                    if (!ok) { LastRejectedIssuer = issuer; LastRejectedAt = DateTime.Now; }
                    return ok;
                },
            },
            ConnectCallback = async (ctx, ct) =>
            {
                // 只对 api.github.com 与 GitHub CDN 域启用 DoH 直连；DoH 失败退回系统解析（hosts 若被劫持由 issuer 校验拦截并报清晰错误）
                if (IsGithubApiOrCdnHost(ctx.DnsEndPoint.Host) &&
                    await ResolveViaDoHAsync(ctx.DnsEndPoint.Host, ct) is { Length: > 0 } ips)
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

    /// <summary>创建 PR（§9 pr_create）。422（已存在同头 PR / 分支无差异）带服务端 message 走 Other。</summary>
    public static async Task<ApiResult> CreatePrAsync(
        string token, string repo, string @base, string head, string title, string body)
    {
        using var http = CreateClient();
        try
        {
            using var req = Req(HttpMethod.Post, $"repos/{repo}/pulls", token);
            req.Content = new StringContent(
                $$"""{"title":{{JsonEscape(title)}},"body":{{JsonEscape(body)}},"base":{{JsonEscape(@base)}},"head":{{JsonEscape(head)}}}""",
                Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            return (int)resp.StatusCode switch
            {
                201 => await ReadCreatedAsync(resp),
                401 or 403 => new ApiResult(OutcomeKind.Unauthorized, (int)resp.StatusCode),
                >= 500 => new ApiResult(OutcomeKind.UnknownOutcome, (int)resp.StatusCode,
                    $"HTTP {(int)resp.StatusCode}"),
                _ => new ApiResult(OutcomeKind.Other, (int)resp.StatusCode,
                    $"HTTP {(int)resp.StatusCode}：{await MessageOfAsync(resp)}"),
            };
        }
        catch (HttpRequestException ex)
        {
            return new ApiResult(OutcomeKind.NotSent, Reason: Describe(ex));
        }
        catch (TaskCanceledException)
        {
            return new ApiResult(OutcomeKind.UnknownOutcome, Reason: "超时");
        }
    }

    /// <summary>
    /// 合并 PR（§9 pr_merge，2026-10-30 实战补齐：PR 工作流闭环最后一米）。
    /// PUT merge 由 GitHub 服务端执行——天然尊重分支保护与 required checks，比任何本地
    /// 推送形态都安全。非幂等（重复 PUT 已合并的 PR 得 405）：超时归 unknown，勿盲重试。
    /// </summary>
    public static async Task<ApiResult> MergePrAsync(string token, string repo, int number, string? method)
    {
        using var http = CreateClient();
        try
        {
            using var req = Req(HttpMethod.Put, $"repos/{repo}/pulls/{number}/merge", token);
            var json = method is null
                ? "{}"
                : $$"""{"merge_method":{{JsonEscape(method)}}}""";
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            return (int)resp.StatusCode switch
            {
                200 => await ReadMergedAsync(resp, repo),
                // 403 拆出（2026-10-30 评审）：合并 403 多为分支保护/无合并权限（after_fix 家族），
                // 归凭据过期(9)会把 agent 引向重新 setup 的死路——落 Other 由 Program 按 PrMergeForbidden 定档
                401 => new ApiResult(OutcomeKind.Unauthorized, (int)resp.StatusCode),
                >= 500 => new ApiResult(OutcomeKind.UnknownOutcome, (int)resp.StatusCode,
                    $"HTTP {(int)resp.StatusCode}"),
                _ => new ApiResult(OutcomeKind.Other, (int)resp.StatusCode,
                    $"HTTP {(int)resp.StatusCode}：{await MessageOfAsync(resp)}"),
            };
        }
        catch (HttpRequestException ex)
        {
            return new ApiResult(OutcomeKind.NotSent, Reason: Describe(ex));
        }
        catch (TaskCanceledException)
        {
            return new ApiResult(OutcomeKind.UnknownOutcome, Reason: "超时");
        }
    }

    private static async Task<ApiResult> ReadMergedAsync(HttpResponseMessage resp, string repo)
    {
        var json = await resp.Content.ReadAsStringAsync();
        var sha = JsonNode.Parse(json)?["sha"]?.GetValue<string>();
        return sha is null
            ? new ApiResult(OutcomeKind.Other, 200, Reason: "merge 响应缺少 sha")
            : new ApiResult(OutcomeKind.Created, 200, Url: $"https://github.com/{repo}/commit/{sha}");
    }

    /// <summary>GitHub 错误响应的 message 字段（无则空串）。JsonDocument 解析——手刮字符串遇转义引号会截断。</summary>
    private static async Task<string> MessageOfAsync(HttpResponseMessage resp) =>
        MessageOfBody(await resp.Content.ReadAsStringAsync());

    /// <summary>GitHub 错误响应 body 的 message 字段（SendRawAsync 路径用；解析失败/缺失给空串）。</summary>
    internal static string MessageOfBody(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    /// <summary>GitHub 403 限流识别（v2.10，2026-10-31 实战）：GitHub 对匿名配额耗尽与
    /// secondary/abuse 拦截都回 403——与"私有/凭据失效"混流会开死方子。primary body 为
    /// "API rate limit exceeded for &lt;ip&gt;"（重置时刻只在响应头 x-ratelimit-reset，
    /// SendRawAsync 不回 headers，首版不取）；secondary/abuse 为 "exceeded a secondary
    /// rate limit" / "abuse detection mechanism"，body 自带 "wait N minutes"。非限流返回 null。</summary>
    internal static (bool Primary, int? WaitMinutes)? ParseRateLimit(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var msg = MessageOfBody(body);
        if (msg.Length == 0) return null;
        var primary = msg.Contains("API rate limit exceeded", StringComparison.OrdinalIgnoreCase);
        var secondary = msg.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)
                     || msg.Contains("abuse detection mechanism", StringComparison.OrdinalIgnoreCase);
        if (!primary && !secondary) return null;
        var m = System.Text.RegularExpressions.Regex.Match(msg, @"wait (\d+) minutes?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return (primary, m.Success ? int.Parse(m.Groups[1].Value) : null);
    }

    /// <summary>分支名 → refs URL 路径段（逐段 percent-encode）。分支名是自由字符（git 引用语法
    /// 允许 # ? 空格等），裸拼 URL 时 # 截成 fragment、? 截成 query（2026-10-30 评审）。
    /// POST refs 的 ref 名走 JSON body，不经过此函数——body 里要的是字面分支名。</summary>
    internal static string RefPath(string branch) =>
        string.Join("/", branch.Split('/').Select(Uri.EscapeDataString));

    /// <summary>
    /// api 读通道（§9 v2.2）：GET-only。token=null 走匿名（公开数据零摩擦）；
    /// 认证读由调用方先过 Hello 解 vault。NotSent=true 表示请求未发出（无副作用）。
    /// v2.7：3xx 受控跟随（≤3 跳，仅 api.github.com / *.githubusercontent.com 域内）——
    /// logs 等 actions 端点 302 到 CDN 签名 URL，不跟随则永远拿不到日志体；跳出域内或超跳数
    /// → 原样返回 3xx 与去向（Reason），由调用方明说——这不是开放代理。后续跳不再带
    /// Authorization（CDN 签名 URL 自带授权，多余的 Authorization 头反而可能被拒）。
    /// </summary>
    public static async Task<(int Status, string Body, bool NotSent, string? Reason)> ApiGetAsync(
        string? token, string path)
    {
        using var http = CreateClient();
        var url = BaseUrl + path;
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Accept", "application/vnd.github+json");
                req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                req.Headers.Add("User-Agent", "sluice/0.1");
                if (token is not null)
                    req.Headers.Add("Authorization", "Bearer " + token);
                using var resp = await http.SendAsync(req);
                if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } loc && hop < 3)
                {
                    var next = loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(url), loc).ToString();
                    var host = new Uri(next).Host;
                    if (IsGithubApiOrCdnHost(host))
                    {
                        url = next;
                        // CDN 签名 URL 自带授权，多余的 Authorization 反而可能被拒；但同域 api.github.com
                        // 跳转保留 token——否则下一跳变匿名 401/404（2026-10-31 评审 D；GitHub 现网
                        // 这组端点不做同域 302，属正确性兜底而非实测行为）
                        if (host != "api.github.com") token = null;
                        continue;
                    }
                    return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(), false,
                        $"去向 {host} 不在跟随白名单（api.github.com / *.githubusercontent.com）");
                }
                if ((int)resp.StatusCode is >= 300 and < 400)
                    return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(), false,
                        "重定向超过 3 跳或缺少 Location，停止跟随");
                return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(), false, null);
            }
        }
        catch (HttpRequestException ex)
        {
            return (0, "", true, Describe(ex));
        }
        catch (TaskCanceledException)
        {
            return (0, "", false, "超时"); // 请求可能已送达，但 GET 无副作用，可安全重试
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
            var when = LastRejectedAt is { } t ? $"，最近拒绝于 {t:yyyy-MM-dd HH:mm:ss}" : "";
            return $"证书颁发者校验失败——实际 issuer：{LastRejectedIssuer ?? "(未记录)"}{when}；允许：{string.Join("/", AllowedIssuers)}。"
                 + "实际 issuer 若是 SteamTools/Fiddler/Clash 等本地代理证书，请关闭其对 GitHub 的劫持；"
                 + "若是 GitHub 官方新换的 CA，请将 issuer 关键词加入白名单后重编译";
        }
        return root.Message;
    }

    /// <summary>push 序列复用客户端：一次 push 最多约 90 个请求（blob×N+tree+commit×M+update-ref），逐请求新建 TLS 连接是纯性能债（40 blob 实测 ≈90 次握手）。</summary>
    private static readonly HttpClient SharedClient = CreateClient();

    /// <summary>
    /// REST push 等复合序列用的通用请求：同一条 DoH+issuer 防线；
    /// 返回原始 (status, body)。序列幂等性由调用方语义保证。
    /// </summary>
    internal static async Task<(int Status, string Body)> SendRawAsync(
        HttpMethod m, string path, string token, string? jsonBody)
    {
        using var req = Req(m, path, token);
        if (jsonBody is not null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var resp = await SharedClient.SendAsync(req);
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// 幂等对象创建（blob/tree/commit——内容寻址，同内容必得同 sha，服务器去重）的 5xx 有界重试（v2.7）。
    /// 不变量：批准覆盖"把这些内容放到该分支"的一次执行；对同一对象的创建重试不超出该范围
    /// （sha 不变、分支未动）。update-ref（唯一写点）与一切非幂等请求永不自动重试。
    /// 背景：2026-10-30 Vitro 平台故障窗 blob 连续 4×HTTP 500，每轮人工重试 = 一道批准 + 一枚指纹。
    /// 退避 2s/8s 共 3 次尝试；429 不在此列（限流是"该慢"，不是抖动）。
    /// </summary>
    internal static async Task<(int Status, string Body)> SendIdempotentAsync(
        HttpMethod m, string path, string token, string? jsonBody)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            var (st, body) = await SendRawAsync(m, path, token, jsonBody);
            if (st < 500 || attempt >= maxAttempts)
                return (st, body);
            var delay = attempt == 1 ? 2 : 8;
            Console.Error.WriteLine(
                $"[Sluice] HTTP {st}（{path}），{delay}s 后第 {attempt + 1}/{maxAttempts} 次尝试（幂等对象创建，不超出本次批准范围）");
            await Task.Delay(TimeSpan.FromSeconds(delay));
        }
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
        var doh = await ResolveViaDoHAsync("api.github.com", CancellationToken.None);
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
