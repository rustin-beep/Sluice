using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sluice;

/// <summary>
/// GitHub API 叶。双防线：
/// ① 解析层——api.github.com 经 IP 直连 DoH（阿里/腾讯公共 DNS）解析真实 IP 后直连，
///    系统 hosts 对 sluice 失效（Watt Toolkit 的 hosts+本地443 MITM 加速与其共存互不干扰，
///    恶意改 hosts 劫持 token 的攻击面在解析层消灭）；codeload 等公共下载域保留系统解析，
///    让国内 hosts 加速 IP 继续可用，TLS issuer 校验兜底。
/// ② 证书层——leaf issuer 必须是 GitHub 官方 CA（DigiCert/Sectigo/Microsoft TLS），
///    本地 MITM 签发的链拒发 token。
/// 代理策略：默认不使用系统/环境代理（避免 HTTPS_PROXY/系统代理把 sluice 带进 Steam++ 的
/// TLS 终结）；仅 config.json 的 "proxy" 或 SLUICE_PROXY 显式指定非 MITM 隧道代理。
/// </summary>
internal static class GitHubApi
{
    private const string BaseUrl = "https://api.github.com/";
    internal const string JsonAccept = "application/vnd.github+json";

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

    /// <summary>DoH 直连主机集合（v2.41 拆分）：api.github.com 带 token，必须绕过系统 hosts；
    /// *.githubusercontent.com 是 logs/release 的 302 签名 CDN，仍走 DoH（签名 URL 无凭据）。
    /// codeload 不在此列——国内 hosts 常把它指向可用的真实加速 IP，强制 DoH 会绕掉这条路；
    /// 它只在重定向白名单里，走系统解析，TLS issuer 校验兜底。</summary>
    internal static bool IsDohHost(string host) =>
        host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>受控重定向白名单（v2.7/v2.41）：api.github.com、GitHub CDN、codeload。
    /// codeload 是 tarball/zipball 的 302 目标；它不带 Authorization（跨主机时 token 已剥离），
    /// 所以允许它走系统解析不破坏 token 防线。其余主机一律不跟随——这不是开放代理。</summary>
    internal static bool IsRedirectAllowed(string host) =>
        IsDohHost(host) ||
        host.Equals("codeload.github.com", StringComparison.OrdinalIgnoreCase);


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
                // DoH 自身不走系统/环境代理：否则 HTTPS_PROXY 指向 Steam++ 时，连 DNS 解析器
                // 都会被本地 MITM（解析结果可被篡改；主 TLS 校验仍拦 token，但没必要多这一层）。
                using var doh = new HttpClient(new SocketsHttpHandler { UseProxy = false })
                { Timeout = TimeSpan.FromSeconds(5) };
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

    /// <param name="ignoreProxy">仅供 selftest 本地探针隔离用户 config/env 代理；生产路径不传。</param>
    /// <param name="allowLocalMitm">仅 --allow-mitm-read 匿名读：额外信任 Steam++/Watt Toolkit
    /// 本地 MITM 证书，并跳过 DoH（让 hosts/系统代理解析到本地 MITM）。绝不用于写/--auth。</param>
    /// <param name="mitmIssuer">命中本地 MITM 时写入产品标签（审计用）；未命中保持 null。</param>
    internal static SocketsHttpHandler CreateHandler(
        bool ignoreProxy = false, bool allowLocalMitm = false, StrongBox<string?>? mitmIssuer = null)
    {
        var h = new SocketsHttpHandler
        {
            // v2.41：必须显式关掉 .NET 的自动重定向。默认 AllowAutoRedirect=true 会让下面
            // IsRedirectAllowed 的白名单与 ≤3 跳全部失效（tarball 的 302→codeload 就是被
            // 自动跟走的；同时它也让“不是开放代理”的承诺不成立）。
            AllowAutoRedirect = false,
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
                    if (!ok && allowLocalMitm && IsLocalMitmIssuer(issuer))
                    {
                        // 双钥匙 opt-in 的窄口：只接受 Steam++/Watt Toolkit，不是放宽到任意本地 CA。
                        ok = true;
                        if (mitmIssuer is not null) mitmIssuer.Value = "Steam++";
                    }
                    if (!ok) { LastRejectedIssuer = issuer; LastRejectedAt = DateTime.Now; }
                    return ok;
                },
            },
            ConnectCallback = async (ctx, ct) =>
            {
                // v2.41：只有 IsDohHost 走 DoH；codeload 走系统解析（国内 hosts 加速 IP 优先），
                // TLS issuer 校验仍在证书层兜底（hosts 指到 MITM 会被拒，而不是被静默信任）。
                // --allow-mitm-read 下必须跳过 DoH：否则绕过 hosts/本地 443 的 MITM，例外无意义。
                if (!allowLocalMitm && IsDohHost(ctx.DnsEndPoint.Host) &&
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

        if (ignoreProxy)
        {
            h.UseProxy = false;
            return h;
        }

        // v2.41 代理策略：默认直连（UseProxy=false）。环境里的 HTTPS_PROXY/HTTP_PROXY/系统代理
        // 常指向 Steam++ 这类 TLS 终结者——sluice 不应默认继承它。只有显式 config.json.proxy
        // 或 SLUICE_PROXY 才启用代理；use_env_proxy:true 作为显式逃生门恢复旧行为。
        var (useProxy, proxyUrl) = ResolveProxyChoice(
            ReadConfiguredProxy(), Environment.GetEnvironmentVariable("SLUICE_PROXY"), ReadUseEnvProxy());
        // MITM 例外面要兼容 Steam++ 的两种入口：hosts→127.0.0.1（系统 DNS）与系统/环境代理。
        // 未显式配置代理时把默认代理打开；没有系统代理则仍走系统 DNS 直连（hosts 生效）。
        if (allowLocalMitm && proxyUrl is null) useProxy = true;
        if (proxyUrl is not null)
        {
            // 显式代理值无效时 fail-closed 到直连：绝不能悄悄退回环境代理（那正是事故形态）。
            if (Uri.TryCreate(proxyUrl, UriKind.Absolute, out var proxyUri))
            {
                h.UseProxy = true;
                h.Proxy = new WebProxy(proxyUri);
            }
            else
            {
                h.UseProxy = false;
            }
        }
        else
        {
            h.UseProxy = useProxy;
        }

        return h;
    }

    /// <summary>v2.41 纯函数（selftest 锁）：config.proxy > SLUICE_PROXY > use_env_proxy 的系统默认。
    /// use_env_proxy 为 true 时才把选择权交回 .NET 默认代理；否则直连。</summary>
    internal static (bool UseProxy, string? ProxyUrl) ResolveProxyChoice(
        string? configProxy, string? sluiceProxy, bool useEnvProxy)
    {
        var explicitProxy = FirstNonEmpty(configProxy, sluiceProxy);
        if (explicitProxy is not null) return (true, explicitProxy);
        return (useEnvProxy, null);
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? ReadConfiguredProxy() => ReadConfigString("proxy");

    private static bool ReadUseEnvProxy() => ReadConfigBool("use_env_proxy");

    /// <summary>v2.42 纯函数（selftest 锁）：本地 MITM 只认 Steam++/Watt Toolkit 两个 issuer 关键词；
    /// 不是“信任任意本地 CA”。</summary>
    internal static bool IsLocalMitmIssuer(string issuer) =>
        issuer.Contains("SteamTools", StringComparison.OrdinalIgnoreCase) ||
        issuer.Contains("BeyondDimension", StringComparison.OrdinalIgnoreCase);

    /// <summary>v2.42：--allow-mitm-read 的第二把钥匙（config.json "allow_mitm_read": true）。</summary>
    internal static bool AllowMitmReadConfigured() => ReadConfigBool("allow_mitm_read");

    private static string? ReadConfigString(string key)
    {
        try
        {
            var cfg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sluice", "config.json");
            if (!File.Exists(cfg)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            return doc.RootElement.TryGetProperty(key, out var p) ? p.GetString() : null;
        }
        catch { return null; }
    }

    private static bool ReadConfigBool(string key)
    {
        try
        {
            var cfg = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sluice", "config.json");
            if (!File.Exists(cfg)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            return doc.RootElement.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    private static HttpClient CreateClient(TimeSpan? timeout = null, bool ignoreProxy = false,
        bool allowLocalMitm = false, StrongBox<string?>? mitmIssuer = null) =>
        new(CreateHandler(ignoreProxy, allowLocalMitm, mitmIssuer), disposeHandler: true)
    {
        // 默认 30s；sluice api 的下载面传更长值（tarball/release asset 的正文读取也受
        // HttpClient.Timeout 约束——默认档只适合 JSON 小响应）。
        Timeout = timeout ?? TimeSpan.FromSeconds(30),
    };

    private static HttpRequestMessage Req(HttpMethod m, string path, string token) => new(m, BaseUrl + path)
    {
        Headers =
        {
            { "Accept", JsonAccept },
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
                // v2.16：403 自 Unauthorized 拆出（v2.5 为 merge 拆过、comment 漏网）——权限拒与
                // 限流都不是凭据失效；Reason 带 message 供 Program 层嗅探/透传
                401 => new ApiResult(OutcomeKind.Unauthorized, (int)resp.StatusCode),
                403 => new ApiResult(OutcomeKind.Other, (int)resp.StatusCode,
                    $"HTTP {(int)resp.StatusCode}：{await MessageOfAsync(resp)}"),
                >= 500 => new ApiResult(OutcomeKind.UnknownOutcome, (int)resp.StatusCode,
                    $"HTTP {(int)resp.StatusCode}"),
                _ => new ApiResult(OutcomeKind.Other, (int)resp.StatusCode,
                    $"HTTP {(int)resp.StatusCode}：{await MessageOfAsync(resp)}"),
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
                // v2.16：403 自 Unauthorized 拆出（镜像 MergePrAsync v2.5 先例）
                401 => new ApiResult(OutcomeKind.Unauthorized, (int)resp.StatusCode),
                403 => new ApiResult(OutcomeKind.Other, (int)resp.StatusCode,
                    $"HTTP {(int)resp.StatusCode}：{await MessageOfAsync(resp)}"),
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
        var msg = RateLimitText(body);
        var primary = msg.Contains("API rate limit exceeded", StringComparison.OrdinalIgnoreCase);
        var secondary = msg.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)
                     || msg.Contains("abuse detection mechanism", StringComparison.OrdinalIgnoreCase);
        if (!primary && !secondary) return null;
        return (primary, WaitMinutesOf(msg));
    }

    /// <summary>限流特征文本归一（v2.16）：raw JSON body 走 message 字段提取；提取不出（空/非 JSON）
    /// 按原文本处理——兼容 ApiResult.Reason 路径（已是"HTTP {st}：{message}"拼好的纯文本，
    /// MessageOfBody 对非 JSON 恒空串会漏嗅探）。</summary>
    private static string RateLimitText(string? text)
    {
        var msg = MessageOfBody(text ?? "");
        return msg.Length > 0 ? msg : text ?? "";
    }

    private static int? WaitMinutesOf(string msg)
    {
        var m = System.Text.RegularExpressions.Regex.Match(msg, @"wait (\d+) minutes?",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>写通道 403/429 限流嗅探的统一文案（v2.16）：api 读通道 v2.10 修掉的死方子在写通道
    /// 原样存在——POST/PATCH 密集创建恰是 secondary 高发面，被诊成"无写权限/凭据失效"会引向
    /// 改参立即重试或重发 PAT（密集重试延长封锁窗）。非限流返回 null（403 的权限/保护语义交各
    /// op 专属文案）；429 本身即限流，无特征文本也归等待档。text 兼容 raw body 与已提取 message。</summary>
    internal static string? RateLimitWhat(int st, string? text)
    {
        if (st is not (403 or 429)) return null;
        var msg = RateLimitText(text);
        var primary = msg.Contains("API rate limit exceeded", StringComparison.OrdinalIgnoreCase);
        var secondary = msg.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase)
                     || msg.Contains("abuse detection mechanism", StringComparison.OrdinalIgnoreCase);
        if (st != 429 && !primary && !secondary) return null;
        if (primary) return "API 配额（rate limit）耗尽，等待当前配额窗重置（约 1 小时内）";
        if (secondary)
            return WaitMinutesOf(msg) is int m ? $"secondary rate limit，服务端要求等待约 {m} 分钟"
                : "secondary rate limit / 滥用检测拦截，等待数分钟";
        return "服务端限流（HTTP 429）——通常 1 分钟内解封，稍候重试";
    }

    /// <summary>分支名 → refs URL 路径段（逐段 percent-encode）。分支名是自由字符（git 引用语法
    /// 允许 # ? 空格等），裸拼 URL 时 # 截成 fragment、? 截成 query（2026-10-30 评审）。
    /// POST refs 的 ref 名走 JSON body，不经过此函数——body 里要的是字面分支名。</summary>
    internal static string RefPath(string branch) =>
        string.Join("/", branch.Split('/').Select(Uri.EscapeDataString));

    /// <summary>v2.41 读通道原始结果：Body 保持字节，二进制不再经过 UTF-8 解码。
    /// ContentType/FinalUrl 供输出选择与审计（最终 host 可能是 codeload/CDN）。
    /// v2.42：MitmIssuer 仅在 --allow-mitm-read 命中本地 MITM 证书时非空。</summary>
    internal sealed record ApiReadResult(
        int Status, byte[] Body, string? ContentType, string? FinalUrl, bool NotSent, string? Reason,
        string? MitmIssuer = null);

    /// <summary>v2.42 流式下载结果：Bytes 为实际写入目标的字节数；ErrorBody 仅非 2xx
    /// （有界读取，供错误分类/透传），2xx 时为空。</summary>
    internal sealed record ApiStreamResult(
        int Status, long Bytes, string? ContentType, string? FinalUrl,
        bool NotSent, string? Reason, byte[] ErrorBody, string? MitmIssuer = null);

    /// <summary>文本包装：供 JSON 消费者（issue list / meta 预读等）沿用原签名。</summary>
    public static async Task<(int Status, string Body, bool NotSent, string? Reason)> ApiGetAsync(
        string? token, string path)
    {
        var r = await ApiGetRawAsync(token, path).ConfigureAwait(false);
        return (r.Status, DecodeText(r.Body), r.NotSent, r.Reason);
    }

    /// <summary>v2.41/v2.42 重定向决策单源（字节读与流式下载共用）：返回是否跟随、
    /// 下一跳 URL、拒绝原因。只允许 HTTPS + GitHub 白名单 + ≤3 跳。</summary>
    private static (bool Follow, string? NextUrl, string? Refusal) RedirectDecision(
        int st, HttpResponseMessage resp, string currentUrl, int hop)
    {
        if (st is < 300 or >= 400) return (false, null, null);
        if (resp.Headers.Location is not { } loc || hop >= 3)
            return (false, null, "重定向超过 3 跳或缺少 Location，停止跟随");
        string next;
        try { next = loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(currentUrl), loc).ToString(); }
        catch (UriFormatException) { return (false, null, "重定向 Location 非法，停止跟随"); }
        // 保持 .NET 自动重定向的既有安全性质：绝不从 HTTPS 降级到 HTTP。
        if (!Uri.TryCreate(next, UriKind.Absolute, out var nextUri)
            || !nextUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return (false, null, "重定向目标不是 HTTPS，停止跟随");
        var host = nextUri.Host;
        if (!IsRedirectAllowed(host))
            return (false, null,
                $"去向 {host} 不在跟随白名单（api.github.com / codeload.github.com / *.githubusercontent.com）");
        return (true, nextUri.ToString(), null);
    }

    /// <summary>
    /// api 读通道（§9 v2.2）：GET-only。token=null 走匿名（公开数据零摩擦）；
    /// 认证读由调用方先过 Hello 解 vault。NotSent=true 表示请求未发出（无副作用）。
    /// v2.7/v2.41：3xx 受控跟随（≤3 跳，仅 api.github.com / codeload / *.githubusercontent.com）——
    /// logs/tarball 等端点 302 到 CDN/归档域，不跟随则永远拿不到正文；跳出白名单或超跳数
    /// → 原样返回 3xx 与去向（Reason），由调用方明说——这不是开放代理。跨域跳不带
    /// Authorization（CDN 签名 URL 自带授权，多余的 Authorization 反而可能被拒）。
    /// v2.41：Body 按字节读；accept 参数供 release asset（application/octet-stream）等媒体类型。
    /// v2.42：allowLocalMitm 供 --allow-mitm-read 匿名读（跳过 DoH、额外信任 Steam++ 证书）。
    /// </summary>
    internal static async Task<ApiReadResult> ApiGetRawAsync(
        string? token, string path, string accept = JsonAccept, string? baseUrl = null,
        TimeSpan? timeout = null, bool ignoreProxy = false, bool allowLocalMitm = false)
    {
        var mitm = new StrongBox<string?>();
        using var http = CreateClient(timeout, ignoreProxy, allowLocalMitm, mitm);
        var url = (baseUrl ?? BaseUrl) + path;
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", accept);
                req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                req.Headers.Add("User-Agent", "sluice/0.1");
                if (token is not null)
                    req.Headers.Add("Authorization", "Bearer " + token);
                using var resp = await http.SendAsync(req);
                var st = (int)resp.StatusCode;
                var (follow, next, refusal) = RedirectDecision(st, resp, url, hop);
                if (follow)
                {
                    url = next!;
                    // 同域 api.github.com 跳转保留 token；跨域（codeload/CDN）剥掉。
                    if (!new Uri(url).Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                        token = null;
                    continue;
                }
                return new ApiReadResult(st, await resp.Content.ReadAsByteArrayAsync(),
                    resp.Content.Headers.ContentType?.MediaType, url, false, refusal, mitm.Value);
            }
        }
        catch (HttpRequestException ex)
        {
            return new ApiReadResult(0, [], null, url, true, Describe(ex), mitm.Value);
        }
        catch (TaskCanceledException)
        {
            return new ApiReadResult(0, [], null, url, false, "超时", mitm.Value); // 请求可能已送达，但 GET 无副作用，可安全重试
        }
    }

    /// <summary>v2.42：--output/--binary 流式下载。2xx 直接 Copy 到 destination（不占内存），
    /// 非 2xx 只读有界错误体（≤1MB）交给调用方；重定向/HTTPS/白名单与 ApiGetRawAsync 同源。</summary>
    internal static async Task<ApiStreamResult> ApiDownloadAsync(
        string? token, string path, string accept, Stream destination, TimeSpan timeout,
        bool ignoreProxy = false, bool allowLocalMitm = false, string? baseUrl = null)
    {
        var mitm = new StrongBox<string?>();
        using var http = CreateClient(timeout, ignoreProxy, allowLocalMitm, mitm);
        using var cts = new CancellationTokenSource(timeout);
        var url = (baseUrl ?? BaseUrl) + path;
        long total = 0;
        var headersReceived = false;
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", accept);
                req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                req.Headers.Add("User-Agent", "sluice/0.1");
                if (token is not null)
                    req.Headers.Add("Authorization", "Bearer " + token);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                headersReceived = true;
                var st = (int)resp.StatusCode;
                var (follow, next, refusal) = RedirectDecision(st, resp, url, hop);
                if (follow)
                {
                    url = next!;
                    if (!new Uri(url).Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase))
                        token = null;
                    continue;
                }
                var ct = resp.Content.Headers.ContentType?.MediaType;
                if (refusal is not null || st is < 200 or >= 300)
                {
                    var err = await ReadBoundedAsync(resp, refusal is not null ? 64 * 1024 : 1024 * 1024, cts.Token);
                    return new ApiStreamResult(st, 0, ct, url, false, refusal, err, mitm.Value);
                }
                await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
                var buffer = new byte[81920];
                while (true)
                {
                    int n;
                    try { n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token); }
                    catch (IOException ex)
                    {
                        // 源读断链 = 下载中断（目标写入的 IOException 不在这里吞，交给调用方
                        // 按“本地写入失败”处理——两者都叫 IOException，语义不同）。
                        return new ApiStreamResult(0, total, ct, url, false,
                            $"下载中断：{ex.Message}", [], mitm.Value);
                    }
                    if (n <= 0) break;
                    await destination.WriteAsync(buffer.AsMemory(0, n), cts.Token);
                    total += n;
                }
                await destination.FlushAsync(cts.Token);
                return new ApiStreamResult(st, total, ct, url, false, null, [], mitm.Value);
            }
        }
        catch (HttpRequestException ex)
        {
            return new ApiStreamResult(0, total, null, url, !headersReceived,
                headersReceived ? $"下载中断：{Describe(ex)}" : Describe(ex), [], mitm.Value);
        }
        catch (TaskCanceledException)
        {
            return new ApiStreamResult(0, total, null, url, false,
                headersReceived ? "下载中断/超时" : "超时", [], mitm.Value);
        }
    }

    /// <summary>v2.42：有界读取非 2xx 响应体（错误分类/透传用），不把错误页无界吃进内存。</summary>
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage resp, int max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        try
        {
            await using var s = await resp.Content.ReadAsStreamAsync(ct);
            var buf = new byte[81920];
            while (ms.Length < max)
            {
                var want = (int)Math.Min(buf.Length, max - ms.Length);
                var n = await s.ReadAsync(buf.AsMemory(0, want), ct);
                if (n <= 0) break;
                ms.Write(buf, 0, n);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException)
        {
            // 错误体不是主数据面：读不全就返回已有部分，保留原 non-2xx 状态。
        }
        return ms.ToArray();
    }

    /// <summary>读通道字节 → 文本（JSON/日志等）；GitHub 响应是 UTF-8，不做 charset 猜测。</summary>
    internal static string DecodeText(byte[] body) => Encoding.UTF8.GetString(body);

    /// <summary>v2.41 纯函数（selftest 锁）：content-type 是否可按文本输出。
    /// 未知 application/*（含 application/vnd.github.raw）按二进制处理——宁可让调用方
    /// 显式 --output/--binary，也不做 UTF-8 替换式损坏。</summary>
    internal static bool IsTextContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return true;
        var ct = contentType.Split(';', 2)[0].Trim();
        if (ct.StartsWith("text/", StringComparison.OrdinalIgnoreCase)) return true;
        if (ct.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
            ct.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)) return true;
        return ct.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || ct.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || ct.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
            || ct.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>v2.41 纯函数（selftest 锁）：证书校验失败是确定性环境问题，重试不会改变结果。
    /// 代理 502/连接拒绝不在此列（可能是瞬时上游抖动）。</summary>
    internal static bool IsDeterministicNetFailure(string? reason) =>
        !string.IsNullOrEmpty(reason) &&
        (reason.StartsWith("证书颁发者校验失败", StringComparison.Ordinal) ||
         reason.Contains("RemoteCertificateValidationCallback", StringComparison.Ordinal));

    /// <summary>根因中文化；issuer 校验拒绝时打印实际 issuer（区分官方新 CA 与本地 MITM）。
    /// v2.41：Steam++/Watt Toolkit 单列——不引导用户把本地 MITM CA 加进白名单。</summary>
    internal static string Describe(Exception ex)
    {
        var root = ex;
        while (root.InnerException is not null) root = root.InnerException;
        if (root is System.Security.Authentication.AuthenticationException ||
            root.Message.Contains("RemoteCertificateValidationCallback", StringComparison.Ordinal))
        {
            var when = LastRejectedAt is { } t ? $"，最近拒绝于 {t:yyyy-MM-dd HH:mm:ss}" : "";
            var issuer = LastRejectedIssuer ?? "(未记录)";
            var mitm = IsLocalMitmIssuer(issuer);
            if (mitm)
                return $"证书颁发者校验失败——检测到 Steam++/Watt Toolkit 的本地 MITM 证书（实际 issuer：{issuer}{when}）。"
                     + "Sluice 不会信任本地 MITM：写操作和 --auth 读的 token 会被代理读到。"
                     + "处理：让 sluice 直连（DoH）或改用非 MITM 的隧道代理（Clash/v2ray 的 HTTP CONNECT，"
                     + "配置 config.json.proxy 或 SLUICE_PROXY）；Steam++ 可继续给 git/浏览器使用。"
                     + "不要把 SteamTools/BeyondDimension 加入 AllowedIssuers。";
            return $"证书颁发者校验失败——实际 issuer：{issuer}{when}；允许：{string.Join("/", AllowedIssuers)}。"
                 + "若为本地代理证书，请关闭其对 GitHub 的 TLS 劫持，或改用非 MITM 代理；"
                 + "若确认是 GitHub 官方新换的 CA，再将 issuer 关键词加入白名单后重编译（不要加入本地 MITM CA）。";
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

    /// <summary>响应体里的 html_url（issue/PR/review 等创建类响应）——手刮子串，只取这一个字段
    /// （同 ReadCreatedAsync 的手法；v2.11 供 SendRawAsync 路径的新 op 复用）。找不到给 null。</summary>
    internal static string? HtmlUrlOf(string body)
    {
        const string key = "\"html_url\":\"";
        var i = body.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return null;
        var j = body.IndexOf('"', i + key.Length);
        return j > i + key.Length ? body[(i + key.Length)..j] : null;
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
