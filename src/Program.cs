using System.Security.Cryptography;
using System.Text;

namespace Sluice;

internal static class Program
{
    /// <summary>
    /// B2（2026-10-30 实战）：构建信息自报家门。csproj 的 EmbedBuildInfo target 把
    /// 构建时间与 git sha 注入 AssemblyMetadata；--version / 未知命令 / 未知参数
    /// 消息附带——新旧 exe 混用一眼可辨（实战曾拿旧 publish 跑新动词，裸报"无法识别的参数"）。
    /// </summary>
    internal static readonly string BuildStamp = ReadBuildStamp();

    private static string ReadBuildStamp()
    {
        try
        {
            // 显式静态调用：Assembly 上的实例 GetCustomAttributes(bool) 会遮蔽泛型扩展方法
            var metas = System.Reflection.CustomAttributeExtensions
                .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(Program).Assembly);
            var ts = metas.FirstOrDefault(m => m.Key == "SluiceBuildTimestamp")?.Value?.Trim();
            var sha = metas.FirstOrDefault(m => m.Key == "SluiceGitSha")?.Value?.Trim();
            return $"sluice build {ts ?? "?"} / git {sha ?? "?"}";
        }
        catch { return "sluice build (unknown)"; }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            return Dispatch(args);
        }
        catch (Exception ex)
        {
            // 兜底：任何未分类异常 → internal 前段（执行尚未开始）
            Console.Error.WriteLine($"[Sluice] 未捕获异常：{ex.GetType().Name}: {ex.Message}");
            Messages.Write(Messages.Internal(afterExecutionStarted: false));
            return ExitCodes.Internal;
        }
    }

    private static int Dispatch(string[] args) => args switch
    {
        [] or ["--help"] or ["help"] or ["-h"] => Usage(),
        ["--version"] or ["version"] => Version(),
        ["setup"] => SetupCommand.SetupAsync(null).GetAwaiter().GetResult(),
        ["setup", "--token-file", var f] => SetupCommand.SetupAsync(f).GetAwaiter().GetResult(),
        ["seal"] => SetupCommand.Seal(fix: false),
        ["seal", "--fix"] => SetupCommand.Seal(fix: true),
        ["selftest"] => Selftest.Run(),
        ["comment", .. var rest] => RunComment(rest),
        ["push", .. var rest] => RunPush(rest),
        ["pr", "create", .. var rest] => RunPrCreate(rest),
        ["pr", "merge", .. var rest] => RunPrMerge(rest),
        ["branch", "delete", .. var rest] => RunBranchDelete(rest),
        ["api", .. var rest] => RunApi(rest),
        [var verb, ..] => BadVerb(verb),
    };

    private static int Usage()
    {
        Console.WriteLine("""
            sluice — GitHub 写操作事件闸门

            用法：
              sluice setup [--token-file F]             配置凭据（粘贴 fine-grained PAT + Windows Hello 建 vault）
              sluice seal [--fix]                       侧门自查 / 封堵（凭据管理器、gh、git-credentials、SSH、环境变量）
              sluice comment --repo O/R --number N --body-file F   在 issue/PR 下发表评论（经批准 + Hello）
              sluice push --repo O/R --branch B [--repo-path D] [--base REF]
                                                        推送本地 HEAD 到远端分支（REST，经批准 + Hello；
                                                        新建远端分支时 --base 给增量基点，如 origin/master。
                                                        多请求序列，调用方超时应给 ≥300s）
              sluice pr create --repo O/R --base B --head H --title T --body-file F
                                                        创建 PR（经批准 + Hello）
              sluice pr merge --repo O/R --number N [--method merge|squash|rebase] [--delete-head]
                                                        合并 PR（服务端 merge API：天然尊重分支保护与
                                                        required checks；--delete-head 合并后删 head 分支）
              sluice branch delete --repo O/R --branch B
                                                        删除远端分支 ref（commits 保留，可用 sha 重建）
              sluice api <api-path> [--auth]           REST GET 读（默认匿名；--auth 过 Hello 带 token；
                                                        路径如 repos/owner/repo/pulls/9，可含 ?query，每次调用入读审计）
              sluice selftest                           消息表与 exit code 自检

            退出码见 `sluice selftest` 输出；消息规范见 plan-lite.md §8。
            """);
        return 0;
    }

    private static int Version()
    {
        // 版本号来自 csproj <Version>（2026-10-30 评审：升版不打印等于没升）；构建戳另含时间+git sha(+dirty)
        var v = typeof(Program).Assembly.GetName().Version;
        Console.WriteLine($"sluice v{v?.ToString(3) ?? "?"} · {BuildStamp} · plan-lite v2.5");
        return 0;
    }

    private static int BadVerb(string verb)
    {
        Messages.Write(Messages.BadRequest(
            $"未知命令 \"{verb}\"；可用动词：setup / seal / comment / push / pr / branch / api / selftest。{BuildStamp}——若你需要的是较新版本的动词，说明正在使用旧构建，请重新 dotnet publish"));
        return ExitCodes.BadRequest;
    }

    /// <summary>B2：未知参数统一归口——附构建戳，旧构建缺新参数时自曝（实战踩坑：旧 exe 裸报"无法识别的参数"）。</summary>
    private static int ErrUnknownArg(string arg)
    {
        Messages.Write(Messages.BadRequest(
            $"无法识别的参数：{arg}。{BuildStamp}——若该参数确属新版本功能，说明 exe 过旧，请重新 publish 后重试"));
        return ExitCodes.BadRequest;
    }

    // ---------- sluice comment ----------

    // ---------- sluice push（V1 · 纯 REST，§9 push bridge 依 2026-10-29 实测改向） ----------

    private static int RunPush(string[] args)
    {
        string? repo = null, branch = null, @base = null;
        var repoPath = ".";
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo":
                    if (i + 1 >= args.Length) return ErrMissing("push", "--repo");
                    repo = args[++i];
                    break;
                case "--base":
                    // 新建分支的增量基点（ref 或 sha，如 origin/master）——存量仓库新分支必给，否则全历史重放
                    if (i + 1 >= args.Length) return ErrMissing("push", "--base");
                    @base = args[++i];
                    break;
                case "--branch":
                    if (i + 1 >= args.Length) return ErrMissing("push", "--branch");
                    branch = args[++i];
                    break;
                case "--repo-path":
                    if (i + 1 >= args.Length) return ErrMissing("push", "--repo-path");
                    repoPath = args[++i];
                    break;
                case "--force":
                    // policy tripwire：force 永不批准，弹窗都不出现（§9）
                    Messages.Write(Messages.PolicyReject("push --force / force push"));
                    return ExitCodes.PolicyReject;
                default:
                    return ErrUnknownArg(args[i]);
            }
        }

        if (repo is null || branch is null)
        {
            Messages.Write(Messages.BadRequest(
                "push 缺少 --repo / --branch（示例：sluice push --repo owner/repo --branch feat/x）"));
            return ExitCodes.BadRequest;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$"))
        {
            Messages.Write(Messages.BadRequest($"--repo 应为 owner/name 形式，收到：{repo}"));
            return ExitCodes.BadRequest;
        }
        if (IsTrunkBranch(branch))
        {
            // policy tripwire：受保护分支永不批准（§9）
            Messages.Write(Messages.PolicyReject($"push → 受保护分支 {branch}"));
            return ExitCodes.PolicyReject;
        }
        if (!Directory.Exists(repoPath))
        {
            Messages.Write(Messages.BadRequest($"--repo-path 不存在：{repoPath}"));
            return ExitCodes.BadRequest;
        }
        if (!TokenStore.Exists)
        {
            Messages.Write(Messages.NotInitialized());
            return ExitCodes.NotInitialized;
        }
        var violations = SealCheck.Run();
        if (violations.Count > 0)
        {
            Audit.Append("push", repo, branch, null, "seal_violation");
            Messages.Write(Messages.SealViolation(string.Join("；", violations)));
            return ExitCodes.SealViolation;
        }

        RestPush.Plan plan;
        try
        {
            plan = RestPush.PlanLocal(repo, branch, Path.GetFullPath(repoPath), @base);
        }
        catch (RestPush.LocalPlanException ex)
        {
            Messages.Write(Messages.BadRequest(ex.Message));
            return ExitCodes.BadRequest;
        }
        catch (Exception)
        {
            Messages.Write(Messages.Internal(afterExecutionStarted: false));
            return ExitCodes.Internal;
        }

        Gate.Outcome outcome;
        using (InputFilter.Install())
        {
            outcome = Gate.Run(new Gate.Request("push", repo, $"{branch} @ {plan.HeadSha[..8]}",
                plan.WindowText, plan.HeadSha[..8], TokenStore.PeekChallenge(),
                plan.TouchesWorkflows ? "本次推送修改 .github/workflows/** —— 批准即部署 CI 变更，请特别确认" : null));
        }

        switch (outcome)
        {
            case Gate.Outcome.Denied d:
                Audit.Append("push", repo, branch, plan.HeadSha[..8], "denied", d.Preset, d.FreeText, consent: "none");
                Messages.Write(Messages.Denied("push", $"{repo}@{branch}", d.Preset, d.FreeText));
                return ExitCodes.Denied;
            case Gate.Outcome.TimedOut:
                Audit.Append("push", repo, branch, plan.HeadSha[..8], "timeout");
                Messages.Write(Messages.Timeout("push", $"{repo}@{branch}", Gate.TimeoutSeconds));
                return ExitCodes.Timeout;
            case Gate.Outcome.ConsentFailed:
                Audit.Append("push", repo, branch, plan.HeadSha[..8], "consent_failed", consent: "hello_failed");
                Messages.Write(Messages.ConsentFailed("push", $"{repo}@{branch}"));
                return ExitCodes.ConsentFailed;
            case Gate.Outcome.VaultKeyMissing vk:
                Audit.Append("push", repo, branch, plan.HeadSha[..8], "vault_broken");
                Messages.Write(Messages.VaultBroken(vk.Detail));
                return ExitCodes.NotInitialized;
            case Gate.Outcome.Crashed c:
                Audit.Append("push", repo, branch, plan.HeadSha[..8], "internal_gate_crash", result: c.Error);
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
            case Gate.Outcome.Approved a:
                return ExecutePush(a, plan);
            default:
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
        }
    }

    private static int ExecutePush(Gate.Outcome.Approved a, RestPush.Plan plan)
    {
        string token;
        try { token = TokenStore.Load(a.Signature); }
        catch (Exception)
        {
            Audit.Append("push", plan.Repo, plan.Branch, plan.HeadSha[..8], "internal_vault_decrypt_failed", consent: "hello");
            Messages.Write(Messages.Internal(afterExecutionStarted: false));
            return ExitCodes.Internal;
        }

        // E2（2026-10-30 实战）：多请求序列可能数分钟无输出，agent 侧易误判挂死——
        // 开工前打一行心跳（stderr，不污染任何数据通道），并提示调用方给足超时
        Console.Error.WriteLine(
            $"[Sluice] 推送序列进行中：{plan.BlobCount} blobs / {plan.CommitCount} commits（多请求序列，可能需数分钟）…");

        RestPush.Result r;
        try { r = RestPush.ExecuteAsync(token, plan).GetAwaiter().GetResult(); }
        catch (HttpRequestException ex)
        {
            Audit.Append("push", plan.Repo, plan.Branch, plan.HeadSha[..8], "http_failed", consent: "hello", result: "not_sent");
            Messages.Write(Messages.HttpFailed(GitHubApi.Describe(ex)));
            return ExitCodes.HttpFailed;
        }
        catch (TaskCanceledException)
        {
            Audit.Append("push", plan.Repo, plan.Branch, plan.HeadSha[..8], "unknown", consent: "hello", result: "timeout");
            Messages.Write(Messages.Unknown("超时"));
            return ExitCodes.Unknown;
        }
        catch (RestPush.LocalPlanException ex)
        {
            Audit.Append("push", plan.Repo, plan.Branch, plan.HeadSha[..8], "bad_request", consent: "hello");
            Messages.Write(Messages.BadRequest(ex.Message));
            return ExitCodes.BadRequest;
        }

        var target = $"{plan.Repo}@{plan.Branch}";
        return r.Kind switch
        {
            RestPush.PushResult.Done => Done(r, plan),
            RestPush.PushResult.UpToDate => UpToDate(plan),
            RestPush.PushResult.Unauthorized => PushFail(plan, "credential_expired", Messages.CredentialExpired("HTTP 401/403"), ExitCodes.CredentialExpired, r.Reason),
            RestPush.PushResult.NeedFetch => PushFail(plan, "need_fetch", Messages.BadRequest(r.Reason ?? "需先同步远端"), ExitCodes.BadRequest, r.Reason),
            // 非 ff 改档 after_fix（2026-10-30 实战）：rebase 后可重推同一分支，agent 可自行修复——
            // 原 policy_reject（"策略永禁"）定性不准，且缺 rebase 正解。force 本身仍永禁（tripwire 不动）。
            RestPush.PushResult.NonFF => PushFail(plan, "non_ff",
                Messages.PushNonFF(plan.Repo, plan.Branch, r.Reason ?? "?"), ExitCodes.BadRequest, r.Reason),
            RestPush.PushResult.HasMerge => PushFail(plan, "has_merge",
                Messages.PushHasMerge(r.Reason ?? "?"), ExitCodes.BadRequest, r.Reason),
            RestPush.PushResult.Submodule => PushFail(plan, "submodule", Messages.BadRequest(r.Reason ?? "含 submodule"), ExitCodes.BadRequest, r.Reason),
            RestPush.PushResult.Unclear => PushFail(plan, "unknown", Messages.Unknown(r.Reason ?? "结果不明"), ExitCodes.Unknown, r.Reason),
            RestPush.PushResult.HeadMoved => PushFail(plan, "head_moved", Messages.BadRequest(r.Reason ?? "批准期间 HEAD 已变化"), ExitCodes.BadRequest, r.Reason),
            _ => PushFail(plan, "other", Messages.Internal(afterExecutionStarted: true), ExitCodes.Internal, r.Reason),
        };
    }

    private static int PushFail(RestPush.Plan plan, string decision, Msg msg, int exit, string? reason = null)
    {
        Audit.Append("push", plan.Repo, plan.Branch, plan.HeadSha[..8], decision, consent: "hello",
            result: reason is null ? "not_executed" : $"not_executed:{reason}");
        Messages.Write(msg);
        return exit;
    }

    private static int Done(RestPush.Result r, RestPush.Plan plan)
    {
        Audit.Append("push", plan.Repo, plan.Branch, plan.HeadSha[..8], "approved", consent: "hello",
            result: $"executed:{r.NewSha}");
        Messages.Write(Messages.Success("push", $"{plan.Repo}@{plan.Branch}", 200,
            $"https://github.com/{plan.Repo}/commit/{r.NewSha}"));
        return ExitCodes.Success;
    }

    private static int UpToDate(RestPush.Plan plan)
    {
        Audit.Append("push", plan.Repo, plan.Branch, plan.HeadSha[..8], "up_to_date", consent: "hello");
        Messages.Write(Messages.PushUpToDate(plan.Repo, plan.Branch, plan.HeadSha[..8]));
        return ExitCodes.Success;
    }

    private static int ErrMissing(string verb, string opt)
    {
        Messages.Write(Messages.BadRequest($"{verb} 参数 {opt} 缺少值"));
        return ExitCodes.BadRequest;
    }

    /// <summary>主干 tripwire（§9）。GitHub refs 大小写敏感（Main 与 main 是两个分支），
    /// 但保护意图按"主干"语义——变体同禁（2026-10-30 评审：原 ordinal 比较漏网 Main/MASTER）。</summary>
    private static bool IsTrunkBranch(string? b) =>
        b is not null && (b.Equals("main", StringComparison.OrdinalIgnoreCase)
                       || b.Equals("master", StringComparison.OrdinalIgnoreCase));

    /// <summary>HTTP 状态 + 服务端 message（权限/保护类错误直接显示服务端原话）。</summary>
    private static string HttpDetail(int st, string body)
    {
        var m = GitHubApi.MessageOfBody(body);
        return m is { Length: > 0 } ? $"HTTP {st}：{m}" : $"HTTP {st}";
    }

    private static int ErrBad(string detail)
    {
        Messages.Write(Messages.BadRequest(detail));
        return ExitCodes.BadRequest;
    }

    private static int RunComment(string[] args)
    {
        string? repo = null, bodyFile = null;
        int number = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string? Next()
            {
                return i + 1 < args.Length ? args[++i] : null;
            }
            switch (args[i])
            {
                case "--repo":
                    repo = Next();
                    if (repo is null) return ErrMissing("pr_comment", "--repo");
                    break;
                case "--number":
                    var v = Next();
                    if (v is null || !int.TryParse(v, out number) || number <= 0)
                        return ErrBad("--number 需要正整数");
                    break;
                case "--body-file":
                    bodyFile = Next();
                    if (bodyFile is null) return ErrMissing("pr_comment", "--body-file");
                    break;
                default:
                    return ErrUnknownArg(args[i]);
            }
        }

        if (repo is null || number <= 0 || bodyFile is null)
        {
            Messages.Write(Messages.BadRequest(
                "pr_comment 缺少 --repo / --number / --body-file（示例：sluice comment --repo owner/repo --number 5 --body-file x.md）"));
            return ExitCodes.BadRequest;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$"))
        {
            Messages.Write(Messages.BadRequest($"--repo 应为 owner/name 形式，收到：{repo}"));
            return ExitCodes.BadRequest;
        }
        if (!File.Exists(bodyFile))
        {
            Messages.Write(Messages.BadRequest($"--body-file 不存在：{bodyFile}"));
            return ExitCodes.BadRequest;
        }

        if (!TokenStore.Exists)
        {
            Messages.Write(Messages.NotInitialized());
            return ExitCodes.NotInitialized;
        }

        // 承重墙：侧门自查不干净 → 不弹窗直接终态（§6）
        var violations = SealCheck.Run();
        if (violations.Count > 0)
        {
            Audit.Append("pr_comment", repo, $"#{number}", null, "seal_violation");
            Messages.Write(Messages.SealViolation(string.Join("；", violations)));
            return ExitCodes.SealViolation;
        }

        string body;
        try { body = File.ReadAllText(bodyFile, System.Text.Encoding.UTF8); }
        catch (Exception ex)
        {
            Messages.Write(Messages.BadRequest($"无法读取 --body-file：{ex.Message}"));
            return ExitCodes.BadRequest;
        }
        var sha = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)))[..8].ToLowerInvariant();
        var target = $"{repo}#{number}";

        Gate.Outcome outcome;
        using (InputFilter.Install()) // T1.5：弹窗期间丢弃 injected 输入
        {
            outcome = Gate.Run(new Gate.Request("pr_comment", repo, $"#{number}", body, sha,
                TokenStore.PeekChallenge()));
        }

        switch (outcome)
        {
            case Gate.Outcome.Denied d:
                Audit.Append("pr_comment", repo, $"#{number}", sha, "denied", d.Preset, d.FreeText, consent: "none");
                Messages.Write(Messages.Denied("pr_comment", target, d.Preset, d.FreeText));
                return ExitCodes.Denied;

            case Gate.Outcome.TimedOut:
                Audit.Append("pr_comment", repo, $"#{number}", sha, "timeout");
                Messages.Write(Messages.Timeout("pr_comment", target, Gate.TimeoutSeconds));
                return ExitCodes.Timeout;

            case Gate.Outcome.ConsentFailed:
                Audit.Append("pr_comment", repo, $"#{number}", sha, "consent_failed", consent: "hello_failed");
                Messages.Write(Messages.ConsentFailed("pr_comment", target));
                return ExitCodes.ConsentFailed;

            case Gate.Outcome.VaultKeyMissing vk:
                Audit.Append("pr_comment", repo, $"#{number}", sha, "vault_broken");
                Messages.Write(Messages.VaultBroken(vk.Detail));
                return ExitCodes.NotInitialized;

            case Gate.Outcome.Crashed c:
                Audit.Append("pr_comment", repo, $"#{number}", sha, "internal_gate_crash", result: c.Error);
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;

            case Gate.Outcome.Approved a:
                return Execute(a, repo, number, target, sha, body);

            default:
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
        }
    }

    private static int Execute(Gate.Outcome.Approved a, string repo, int number, string target, string sha, string body)
    {
        string token;
        try { token = TokenStore.Load(a.Signature); }
        catch (Exception)
        {
            Audit.Append("pr_comment", repo, $"#{number}", sha, "internal_vault_decrypt_failed", consent: "hello");
            Messages.Write(Messages.Internal(afterExecutionStarted: false));
            return ExitCodes.Internal;
        }

        var result = GitHubApi.PostCommentAsync(token, repo, number, body).GetAwaiter().GetResult();
        return result.Kind switch
        {
            GitHubApi.OutcomeKind.Created => Created(result, repo, number, sha),
            GitHubApi.OutcomeKind.Unauthorized => Expired(result, repo, number, sha),
            GitHubApi.OutcomeKind.NotSent => NotSent(result, repo, number, sha),
            GitHubApi.OutcomeKind.UnknownOutcome => UnknownOutcome(result, repo, number, sha),
            _ => InternalPost(result, repo, number, sha),
        };
    }

    // ---------- sluice api（§9 v2.2 读通道：GET-only，默认匿名，读审计落行） ----------

    private static int RunApi(string[] args)
    {
        string? path = null;
        var auth = false;
        foreach (var a in args)
        {
            if (a == "--auth") { auth = true; continue; }
            if (a.StartsWith("--")) return ErrUnknownArg(a);
            if (path is not null)
            {
                Messages.Write(Messages.BadRequest("api 只接受一个 <api-path>（GET 单路径，示例：sluice api repos/owner/repo/pulls/9）"));
                return ExitCodes.BadRequest;
            }
            path = a;
        }
        if (path is null)
        {
            Messages.Write(Messages.BadRequest(
                "api 缺少 <api-path>（示例：sluice api repos/owner/repo/pulls/9；私有资源加 --auth 过 Windows Hello）"));
            return ExitCodes.BadRequest;
        }
        // GET-only + 路径白名单（拒 scheme/主机/.. 逃逸——这不是开放代理，只收 api.github.com 的相对路径）。
        // ?&: 支持分页/过滤（?page=2、?head=owner:branch——`://` 已单拒，裸 `:` 无 scheme 危害）；
        // + 是分页 cursor（after=/before=）base64 的合法字符（2026-10-30 实战补——缺它翻页即拒），
        // ,; 见于 sort/direction 类参数。
        if (path.StartsWith('/') || path.Contains("..") || path.Contains("://")
            || !System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z0-9._/@{}=%\-:?&+,;]+$"))
        {
            Messages.Write(Messages.BadRequest($"<api-path> 须为 api.github.com 的相对路径（无 scheme、无 ..，可含 ?query），收到：{path}"));
            return ExitCodes.BadRequest;
        }

        string? token = null;
        if (auth)
        {
            if (!TokenStore.Exists)
            {
                Messages.Write(Messages.NotInitialized());
                return ExitCodes.NotInitialized;
            }
            // 读无破坏力：不弹批准窗，指纹（Hello）即唯一门槛——vault 解密的技术必然
            byte[]? sig;
            try
            {
                sig = new HelloConsent().SignAsync(TokenStore.PeekChallenge()).GetAwaiter().GetResult();
            }
            catch (VaultKeyMissingException ex)
            {
                Audit.Append("api_read", "api.github.com", path, null, "vault_broken");
                Messages.Write(Messages.VaultBroken(ex.Message));
                return ExitCodes.NotInitialized;
            }
            if (sig is null)
            {
                Audit.Append("api_read", "api.github.com", path, null, "consent_failed", consent: "hello_failed");
                Messages.Write(Messages.ConsentFailed("api", path));
                return ExitCodes.ConsentFailed;
            }
            try { token = TokenStore.Load(sig); }
            catch (Exception)
            {
                Audit.Append("api_read", "api.github.com", path, null, "internal_vault_decrypt_failed", consent: "hello");
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
            }
        }

        var (st, body, notSent, reason) = GitHubApi.ApiGetAsync(token, path).GetAwaiter().GetResult();
        if (notSent)
        {
            Audit.Append("api_read", "api.github.com", path, null, "http_failed", result: "not_sent");
            Messages.Write(Messages.HttpFailed(reason ?? "连接失败"));
            return ExitCodes.HttpFailed;
        }
        if (st == 0)
        {
            // GET 超时：请求可能已送达但幂等无副作用——20/retry_ok（§8.2：21 是非幂等 POST 专属档）
            Audit.Append("api_read", "api.github.com", path, null, "timeout", result: "retry_safe_get");
            Messages.Write(Messages.ApiTimeout(path));
            return ExitCodes.HttpFailed;
        }

        Audit.Append("api_read", "api.github.com", path, null, $"http_{st}", result: $"{body.Length}B{(token is not null ? " auth" : " anon")}");
        Console.Out.Write(body);
        switch (st)
        {
            case >= 200 and < 300:
                return ExitCodes.Success;
            case 401 or 403:
                // 匿名：资源需要认证，加 --auth 是 agent 可自行完成的修正（after_fix）；
                // 带 token：凭据真的失效/越权，才归 credential_expired（wait_user，交还用户）
                if (token is null)
                {
                    Messages.Write(Messages.ApiAuthRequired(path, st));
                    return ExitCodes.BadRequest;
                }
                Messages.Write(Messages.CredentialExpired($"HTTP {st}（api GET {path}）"));
                return ExitCodes.CredentialExpired;
            default:
                Messages.Write(Messages.ApiHttpError(path, st, body.Length));
                return ExitCodes.BadRequest;
        }
    }

    // ---------- sluice pr create（V1 · §9 pr_create，同闸门路径） ----------

    private static int RunPrCreate(string[] args)
    {
        string? repo = null, @base = null, head = null, title = null, bodyFile = null;
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--repo":
                    repo = Next();
                    if (repo is null) return ErrMissing("pr_create", "--repo");
                    break;
                case "--base":
                    @base = Next();
                    if (@base is null) return ErrMissing("pr_create", "--base");
                    break;
                case "--head":
                    head = Next();
                    if (head is null) return ErrMissing("pr_create", "--head");
                    break;
                case "--title":
                    title = Next();
                    if (title is null) return ErrMissing("pr_create", "--title");
                    break;
                case "--body-file":
                    bodyFile = Next();
                    if (bodyFile is null) return ErrMissing("pr_create", "--body-file");
                    break;
                default:
                    return ErrUnknownArg(args[i]);
            }
        }

        if (repo is null || @base is null || head is null || title is null || bodyFile is null)
        {
            Messages.Write(Messages.BadRequest(
                "pr_create 缺少 --repo / --base / --head / --title / --body-file（示例：sluice pr create --repo owner/repo --base master --head feat/x --title \"标题\" --body-file body.md）"));
            return ExitCodes.BadRequest;
        }
        if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$"))
        {
            Messages.Write(Messages.BadRequest($"--repo 应为 owner/name 形式，收到：{repo}"));
            return ExitCodes.BadRequest;
        }
        if (title.Trim().Length == 0)
        {
            Messages.Write(Messages.BadRequest("--title 不能为空"));
            return ExitCodes.BadRequest;
        }
        if (!File.Exists(bodyFile))
        {
            Messages.Write(Messages.BadRequest($"--body-file 不存在：{bodyFile}"));
            return ExitCodes.BadRequest;
        }

        if (!TokenStore.Exists)
        {
            Messages.Write(Messages.NotInitialized());
            return ExitCodes.NotInitialized;
        }
        var violations = SealCheck.Run();
        if (violations.Count > 0)
        {
            Audit.Append("pr_create", repo, $"{@base}←{head}", null, "seal_violation");
            Messages.Write(Messages.SealViolation(string.Join("；", violations)));
            return ExitCodes.SealViolation;
        }

        string body;
        try { body = File.ReadAllText(bodyFile, System.Text.Encoding.UTF8); }
        catch (Exception ex)
        {
            Messages.Write(Messages.BadRequest($"无法读取 --body-file：{ex.Message}"));
            return ExitCodes.BadRequest;
        }
        var sha = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(title + "\n\n" + body)))[..8].ToLowerInvariant();
        var target = $"{repo} {@base} ← {head}";
        var window = $"""
            标题：{title}
            方向：{@base} ← {head}
            正文（{body.Length} 字符）：
            {body}
            """;

        Gate.Outcome outcome;
        using (InputFilter.Install())
        {
            outcome = Gate.Run(new Gate.Request("pr_create", repo, $"{@base} ← {head}", window, sha,
                TokenStore.PeekChallenge()));
        }

        switch (outcome)
        {
            case Gate.Outcome.Denied d:
                Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "denied", d.Preset, d.FreeText, consent: "none");
                Messages.Write(Messages.Denied("pr_create", target, d.Preset, d.FreeText));
                return ExitCodes.Denied;
            case Gate.Outcome.TimedOut:
                Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "timeout");
                Messages.Write(Messages.Timeout("pr_create", target, Gate.TimeoutSeconds));
                return ExitCodes.Timeout;
            case Gate.Outcome.ConsentFailed:
                Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "consent_failed", consent: "hello_failed");
                Messages.Write(Messages.ConsentFailed("pr_create", target));
                return ExitCodes.ConsentFailed;
            case Gate.Outcome.VaultKeyMissing vk:
                Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "vault_broken");
                Messages.Write(Messages.VaultBroken(vk.Detail));
                return ExitCodes.NotInitialized;
            case Gate.Outcome.Crashed c:
                Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "internal_gate_crash", result: c.Error);
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
            case Gate.Outcome.Approved a:
                return ExecutePr(a, repo, @base, head, title, body, sha, target);
            default:
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
        }
    }

    private static int ExecutePr(Gate.Outcome.Approved a, string repo, string @base, string head,
        string title, string body, string sha, string target)
    {
        string token;
        try { token = TokenStore.Load(a.Signature); }
        catch (Exception)
        {
            Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "internal_vault_decrypt_failed", consent: "hello");
            Messages.Write(Messages.Internal(afterExecutionStarted: false));
            return ExitCodes.Internal;
        }

        var r = GitHubApi.CreatePrAsync(token, repo, @base, head, title, body).GetAwaiter().GetResult();
        switch (r.Kind)
        {
            case GitHubApi.OutcomeKind.Created:
                Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "approved", consent: "hello",
                    result: $"executed:{r.Url}");
                Messages.Write(Messages.Success("pr_create", target, 201, r.Url!));
                return ExitCodes.Success;
            case GitHubApi.OutcomeKind.Unauthorized:
                Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "credential_expired", consent: "hello",
                    result: $"http_{r.Status}");
                Messages.Write(Messages.CredentialExpired($"HTTP {r.Status}"));
                return ExitCodes.CredentialExpired;
            case GitHubApi.OutcomeKind.NotSent:
                Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "http_failed", consent: "hello", result: "not_sent");
                Messages.Write(Messages.HttpFailed(r.Reason ?? "连接失败"));
                return ExitCodes.HttpFailed;
            case GitHubApi.OutcomeKind.UnknownOutcome:
                Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "unknown", consent: "hello",
                    result: $"unknown:{r.Reason}");
                Messages.Write(Messages.Unknown(r.Reason ?? "结果不明"));
                return ExitCodes.Unknown;
            default:
                Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "internal", consent: "hello",
                    result: r.Reason ?? $"http_{r.Status}");
                // 422（PR 已存在/分支无差异）：服务端明确拒绝，原样重跑必再 422——专用消息指引查已有 PR；
                // 其余非 2xx（如 400 真·用法错）仍走通用 bad_request 模板
                if (r.Status == 422)
                {
                    Messages.Write(Messages.PrRejected(repo, head, r.Reason ?? "HTTP 422"));
                    return ExitCodes.BadRequest;
                }
                Messages.Write(Messages.BadRequest($"pr_create 被服务端拒绝：{r.Reason}"));
                return ExitCodes.BadRequest;
        }
    }

    // ---------- sluice pr merge（V1.5 · §9 pr_merge，2026-10-30 实战补齐：PR 工作流闭环最后一米） ----------

    private static int RunPrMerge(string[] args)
    {
        string? repo = null, method = null;
        int number = 0;
        var deleteHead = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo":
                    repo = i + 1 < args.Length ? args[++i] : null;
                    if (repo is null) return ErrMissing("pr_merge", "--repo");
                    break;
                case "--number":
                    var v = i + 1 < args.Length ? args[++i] : null;
                    if (v is null || !int.TryParse(v, out number) || number <= 0)
                        return ErrBad("--number 需要正整数");
                    break;
                case "--method":
                    method = i + 1 < args.Length ? args[++i] : null;
                    if (method is not ("merge" or "squash" or "rebase"))
                        return ErrBad("--method 仅接受 merge / squash / rebase（缺省 = 仓库默认合并方式）");
                    break;
                case "--delete-head":
                    deleteHead = true;
                    break;
                default:
                    return ErrUnknownArg(args[i]);
            }
        }
        if (repo is null || number <= 0)
            return ErrBad("pr_merge 缺少 --repo / --number（示例：sluice pr merge --repo owner/repo --number 383 --method squash）");
        if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$"))
            return ErrBad($"--repo 应为 owner/name 形式，收到：{repo}");
        if (!TokenStore.Exists)
        {
            Messages.Write(Messages.NotInitialized());
            return ExitCodes.NotInitialized;
        }
        var violations = SealCheck.Run();
        if (violations.Count > 0)
        {
            Audit.Append("pr_merge", repo, $"#{number}", null, "seal_violation");
            Messages.Write(Messages.SealViolation(string.Join("；", violations)));
            return ExitCodes.SealViolation;
        }

        // 尽力而为的匿名元数据读（公开库可用；私有库/网络失败不阻塞——弹窗退化为参数展示）。
        // 读无破坏力、无凭据参与，走 api 读通道同款匿名 GET——同通道纪律：读也落审计（2026-10-30 评审）。
        string? prTitle = null, prHead = null, prBase = null, prHeadSha = null;
        int changed = -1, additions = -1, deletions = -1;
        try
        {
            var (st, body, notSent, _) = GitHubApi.ApiGetAsync(null, $"repos/{repo}/pulls/{number}")
                .GetAwaiter().GetResult();
            if (notSent)
                Audit.Append("api_read", "api.github.com", $"repos/{repo}/pulls/{number}", null, "http_failed", result: "not_sent");
            else if (st == 0)
                Audit.Append("api_read", "api.github.com", $"repos/{repo}/pulls/{number}", null, "timeout", result: "retry_safe_get");
            else
                Audit.Append("api_read", "api.github.com", $"repos/{repo}/pulls/{number}", null, $"http_{st}", result: $"{body.Length}B anon");
            if (st == 200)
            {
                var pr = System.Text.Json.Nodes.JsonNode.Parse(body);
                prTitle = pr?["title"]?.GetValue<string>();
                prHead = pr?["head"]?["ref"]?.GetValue<string>();
                prBase = pr?["base"]?["ref"]?.GetValue<string>();
                prHeadSha = pr?["head"]?["sha"]?.GetValue<string>(); // 保真锚点：批准所见 = 合并所执行
                changed = pr?["changed_files"]?.GetValue<int>() ?? -1;
                additions = pr?["additions"]?.GetValue<int>() ?? -1;
                deletions = pr?["deletions"]?.GetValue<int>() ?? -1;
            }
        }
        catch { /* 元数据读失败不阻塞批准流 */ }
        if (deleteHead && prHead is null)
            return ErrBad("--delete-head 需要知道 head 分支名，但 PR 元数据匿名读取失败（私有库？网络？）——请合并后改用 sluice branch delete");
        if (IsTrunkBranch(prHead))
            return ErrBad("head 分支是 main/master，--delete-head 拒绝执行（受保护分支）");

        var target = $"{repo}#{number}";
        // 批准指纹混入 head sha（2026-10-30 评审）：编号不变内容可变——指纹锚定"所见内容"，
        // 审计行才能回答"用户批准的是哪一版"
        var sha = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{repo}#{number}:{method ?? "default"}:{prHeadSha ?? "?}"}")))[..8].ToLowerInvariant();
        var window = $"""
            PR：{target}
            标题：{prTitle ?? "（元数据不可读：私有库或网络原因，合并前请自行核对）"}
            方向：{prBase ?? "?"} ← {prHead ?? "?"}{(prHeadSha is { Length: >= 8 } h ? $"（head {h[..8]}）" : "")}
            变更：{(changed >= 0 ? $"{changed} 文件（+{additions}/-{deletions}）" : "（统计不可读）")}
            合并方式：{method ?? "仓库默认"}
            服务端将执行分支保护与 required checks 校验；批准即合并。
            """;

        Gate.Outcome outcome;
        using (InputFilter.Install())
        {
            outcome = Gate.Run(new Gate.Request("pr_merge", repo, $"#{number}", window, sha,
                TokenStore.PeekChallenge(),
                deleteHead ? "合并成功后将同时删除 head 分支（commits 仍保留，可用 sha 重建）" : null));
        }

        switch (outcome)
        {
            case Gate.Outcome.Denied d:
                Audit.Append("pr_merge", repo, $"#{number}", sha, "denied", d.Preset, d.FreeText, consent: "none");
                Messages.Write(Messages.Denied("pr_merge", target, d.Preset, d.FreeText));
                return ExitCodes.Denied;
            case Gate.Outcome.TimedOut:
                Audit.Append("pr_merge", repo, $"#{number}", sha, "timeout");
                Messages.Write(Messages.Timeout("pr_merge", target, Gate.TimeoutSeconds));
                return ExitCodes.Timeout;
            case Gate.Outcome.ConsentFailed:
                Audit.Append("pr_merge", repo, $"#{number}", sha, "consent_failed", consent: "hello_failed");
                Messages.Write(Messages.ConsentFailed("pr_merge", target));
                return ExitCodes.ConsentFailed;
            case Gate.Outcome.VaultKeyMissing vk:
                Audit.Append("pr_merge", repo, $"#{number}", sha, "vault_broken");
                Messages.Write(Messages.VaultBroken(vk.Detail));
                return ExitCodes.NotInitialized;
            case Gate.Outcome.Crashed c:
                Audit.Append("pr_merge", repo, $"#{number}", sha, "internal_gate_crash", result: c.Error);
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
            case Gate.Outcome.Approved a:
                return ExecutePrMerge(a, repo, number, method, deleteHead, prHead, prHeadSha, sha, target);
            default:
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
        }
    }

    private static int ExecutePrMerge(Gate.Outcome.Approved a, string repo, int number, string? method,
        bool deleteHead, string? headBranch, string? headShaSnapshot, string sha, string target)
    {
        string token;
        try { token = TokenStore.Load(a.Signature); }
        catch (Exception)
        {
            Audit.Append("pr_merge", repo, $"#{number}", sha, "internal_vault_decrypt_failed", consent: "hello");
            Messages.Write(Messages.Internal(afterExecutionStarted: false));
            return ExitCodes.Internal;
        }

        // 内容保真复核（2026-10-30 评审）：弹窗元数据是 fetch 时刻快照，批准→执行之间 head 若被
        // 推进（新 push 到 PR 分支），合并的将是用户没见过的内容——与 push 的 HeadMoved 同族，
        // 变了即中止（after_fix，重跑重新弹窗）。复核用带 token GET（私有库同样可查）；
        // 快照缺失（匿名读失败）时无可比对，不阻断——弹窗已声明"请自行核对"。
        if (headShaSnapshot is { } snap)
        {
            var (vst, vbody, vNotSent, vWhy) = GitHubApi.ApiGetAsync(token, $"repos/{repo}/pulls/{number}")
                .GetAwaiter().GetResult();
            if (vNotSent || vst == 0)
            {
                // 复核 GET 无副作用且 merge 未发出——整条命令可安全重跑（retry_ok）
                Audit.Append("pr_merge", repo, $"#{number}", sha, "http_failed", consent: "hello",
                    result: vNotSent ? "not_sent" : "timeout_retry_safe_get");
                Messages.Write(Messages.PrMergeVerifyFailed(vNotSent ? vWhy ?? "连接失败" : "复核请求超时"));
                return ExitCodes.HttpFailed;
            }
            if (vst == 200)
            {
                var now = System.Text.Json.Nodes.JsonNode.Parse(vbody)?["head"]?["sha"]?.GetValue<string>();
                if (now is null)
                {
                    // 200 却无 head.sha：结构漂移按 sluice 侧错误诚实归类，不 fail-open
                    Audit.Append("pr_merge", repo, $"#{number}", sha, "internal_verify_failed", consent: "hello");
                    Messages.Write(Messages.Internal(afterExecutionStarted: false));
                    return ExitCodes.Internal;
                }
                if (now != snap)
                {
                    Audit.Append("pr_merge", repo, $"#{number}", sha, "head_moved", consent: "hello",
                        result: $"expected:{snap[..8]} actual:{now[..8]}");
                    Messages.Write(Messages.PrMergeChanged(target, snap[..8], now[..8]));
                    return ExitCodes.BadRequest;
                }
            }
            // 其余非 200：无变化实证则不在此拦截——真实状态由随后的 merge 调用如实分类
        }

        var r = GitHubApi.MergePrAsync(token, repo, number, method).GetAwaiter().GetResult();
        switch (r.Kind)
        {
            case GitHubApi.OutcomeKind.Created:
                Audit.Append("pr_merge", repo, $"#{number}", sha, "approved", consent: "hello",
                    result: $"executed:{r.Url}");
                Messages.Write(Messages.Success("pr_merge", target, 200, r.Url!));
                // --delete-head：合并已生效（不可回滚）；删除失败不吞——如实附注并指引单独删除。
                // 删除同样是用户批准过的动作（弹窗脚注明示）：成败都落审计（2026-10-30 评审补环）
                if (deleteHead && headBranch is not null)
                {
                    try
                    {
                        var (dst, dbody) = GitHubApi.SendRawAsync(HttpMethod.Delete,
                            $"repos/{repo}/git/refs/heads/{GitHubApi.RefPath(headBranch)}", token, null).GetAwaiter().GetResult();
                        Audit.Append("pr_merge", repo, headBranch, sha,
                            dst == 204 ? "delete_head_executed" : "delete_head_failed",
                            consent: "hello", result: dst == 204 ? "executed" : $"http_{dst}");
                        if (dst != 204)
                            Messages.Write(Messages.BranchDeleteFailed(repo, headBranch, HttpDetail(dst, dbody)));
                    }
                    catch (TaskCanceledException)
                    {
                        Audit.Append("pr_merge", repo, headBranch, sha, "delete_head_failed",
                            consent: "hello", result: "timeout");
                        Messages.Write(Messages.BranchDeleteFailed(repo, headBranch,
                            "删除请求超时（可能已送达）——查证 refs 后按需重删"));
                    }
                    catch (Exception ex)
                    {
                        Audit.Append("pr_merge", repo, headBranch, sha, "delete_head_failed",
                            consent: "hello", result: "not_sent");
                        Messages.Write(Messages.BranchDeleteFailed(repo, headBranch, ex.Message));
                    }
                }
                return ExitCodes.Success;
            case GitHubApi.OutcomeKind.Unauthorized: // v2.5 起 401 专属（403 已在服务端拆出）
                Audit.Append("pr_merge", repo, $"#{number}", sha, "credential_expired", consent: "hello",
                    result: $"http_{r.Status}");
                Messages.Write(Messages.CredentialExpired($"HTTP {r.Status}"));
                return ExitCodes.CredentialExpired;
            case GitHubApi.OutcomeKind.NotSent:
                Audit.Append("pr_merge", repo, $"#{number}", sha, "http_failed", consent: "hello", result: "not_sent");
                Messages.Write(Messages.HttpFailed(r.Reason ?? "连接失败"));
                return ExitCodes.HttpFailed;
            case GitHubApi.OutcomeKind.UnknownOutcome:
                Audit.Append("pr_merge", repo, $"#{number}", sha, "unknown", consent: "hello",
                    result: $"unknown:{r.Reason}");
                Messages.Write(Messages.Unknown(r.Reason ?? "结果不明"));
                return ExitCodes.Unknown;
            default:
                // 405（不可合并）/ 409（冲突）/ 422（校验拒）：服务端明确拒绝；403 与 404 各配专属
                // 文案（2026-10-30 评审：403 归"不可合并"是误导——它是保护/权限；404 是"不存在"，
                // 与 mergeability 无关）
                Audit.Append("pr_merge", repo, $"#{number}", sha,
                    r.Status == 403 ? "forbidden" : r.Status == 404 ? "not_found" : "pr_merge_rejected",
                    consent: "hello", result: r.Reason ?? $"http_{r.Status}");
                Messages.Write(r.Status switch
                {
                    403 => Messages.PrMergeForbidden(target, r.Reason ?? $"HTTP {r.Status}"),
                    404 => Messages.PrMergeNotFound(target),
                    _ => Messages.PrMergeRejected(target, r.Reason ?? $"HTTP {r.Status}"),
                });
                return ExitCodes.BadRequest;
        }
    }

    // ---------- sluice branch delete（V1.5 · 2026-10-30 实战补齐：PR 作废后远端分支残留，原来只能网页删） ----------

    private static int RunBranchDelete(string[] args)
    {
        string? repo = null, branch = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo":
                    repo = i + 1 < args.Length ? args[++i] : null;
                    if (repo is null) return ErrMissing("branch_delete", "--repo");
                    break;
                case "--branch":
                    branch = i + 1 < args.Length ? args[++i] : null;
                    if (branch is null) return ErrMissing("branch_delete", "--branch");
                    break;
                default:
                    return ErrUnknownArg(args[i]);
            }
        }
        if (repo is null || branch is null)
            return ErrBad("branch_delete 缺少 --repo / --branch（示例：sluice branch delete --repo owner/repo --branch feat/x-r2）");
        if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$"))
            return ErrBad($"--repo 应为 owner/name 形式，收到：{repo}");
        if (IsTrunkBranch(branch))
        {
            // policy tripwire：与 push 受保护分支禁令同族，永不批准
            Messages.Write(Messages.PolicyReject($"删除受保护分支 {branch}"));
            return ExitCodes.PolicyReject;
        }
        if (!TokenStore.Exists)
        {
            Messages.Write(Messages.NotInitialized());
            return ExitCodes.NotInitialized;
        }
        var violations = SealCheck.Run();
        if (violations.Count > 0)
        {
            Audit.Append("branch_delete", repo, branch, null, "seal_violation");
            Messages.Write(Messages.SealViolation(string.Join("；", violations)));
            return ExitCodes.SealViolation;
        }

        var target = $"{repo}@{branch}";
        var sha = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(target)))[..8].ToLowerInvariant();
        var window = $"""
            删除远端分支：{target}
            这是删除分支 ref，不是销毁历史：commits 仍留在服务端（GC 窗口内可用 sha 重建）。
            但若该分支含未合并的独有提交，删除后它将不再出现在任何 PR/分支列表中。
            """;

        Gate.Outcome outcome;
        using (InputFilter.Install())
        {
            outcome = Gate.Run(new Gate.Request("branch_delete", repo, branch, window, sha,
                TokenStore.PeekChallenge()));
        }

        switch (outcome)
        {
            case Gate.Outcome.Denied d:
                Audit.Append("branch_delete", repo, branch, sha, "denied", d.Preset, d.FreeText, consent: "none");
                Messages.Write(Messages.Denied("branch_delete", target, d.Preset, d.FreeText));
                return ExitCodes.Denied;
            case Gate.Outcome.TimedOut:
                Audit.Append("branch_delete", repo, branch, sha, "timeout");
                Messages.Write(Messages.Timeout("branch_delete", target, Gate.TimeoutSeconds));
                return ExitCodes.Timeout;
            case Gate.Outcome.ConsentFailed:
                Audit.Append("branch_delete", repo, branch, sha, "consent_failed", consent: "hello_failed");
                Messages.Write(Messages.ConsentFailed("branch_delete", target));
                return ExitCodes.ConsentFailed;
            case Gate.Outcome.VaultKeyMissing vk:
                Audit.Append("branch_delete", repo, branch, sha, "vault_broken");
                Messages.Write(Messages.VaultBroken(vk.Detail));
                return ExitCodes.NotInitialized;
            case Gate.Outcome.Crashed c:
                Audit.Append("branch_delete", repo, branch, sha, "internal_gate_crash", result: c.Error);
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
            case Gate.Outcome.Approved a:
                return ExecuteBranchDelete(a, repo, branch, sha, target);
            default:
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
        }
    }

    private static int ExecuteBranchDelete(Gate.Outcome.Approved a, string repo, string branch, string sha, string target)
    {
        string token;
        try { token = TokenStore.Load(a.Signature); }
        catch (Exception)
        {
            Audit.Append("branch_delete", repo, branch, sha, "internal_vault_decrypt_failed", consent: "hello");
            Messages.Write(Messages.Internal(afterExecutionStarted: false));
            return ExitCodes.Internal;
        }

        try
        {
            var (st, body) = GitHubApi.SendRawAsync(HttpMethod.Delete,
                $"repos/{repo}/git/refs/heads/{GitHubApi.RefPath(branch)}", token, null).GetAwaiter().GetResult();
            switch (st)
            {
                case 204:
                    Audit.Append("branch_delete", repo, branch, sha, "approved", consent: "hello",
                        result: "executed");
                    Messages.Write(Messages.Success("branch_delete", target, 204,
                        $"https://github.com/{repo}/branches"));
                    return ExitCodes.Success;
                // 403 拆分（2026-10-30 评审）：403 多为分支保护/无删除权限（服务端策略拒），
                // 归凭据过期(9)会把 agent 引向重新 setup 的死路；仅 401 是凭据问题
                case 401:
                    Audit.Append("branch_delete", repo, branch, sha, "credential_expired", consent: "hello",
                        result: "http_401");
                    Messages.Write(Messages.CredentialExpired("HTTP 401"));
                    return ExitCodes.CredentialExpired;
                case 403:
                    Audit.Append("branch_delete", repo, branch, sha, "forbidden", consent: "hello",
                        result: HttpDetail(st, body));
                    Messages.Write(Messages.BranchDeleteForbidden(repo, branch, HttpDetail(st, body)));
                    return ExitCodes.BadRequest;
                case 404:
                    Audit.Append("branch_delete", repo, branch, sha, "bad_request", consent: "hello",
                        result: $"http_404");
                    Messages.Write(Messages.BadRequest($"分支不存在（可能已被删除）：{repo}@{branch}。本次未产生任何副作用"));
                    return ExitCodes.BadRequest;
                default:
                    Audit.Append("branch_delete", repo, branch, sha, "bad_request", consent: "hello",
                        result: $"http_{st}");
                    Messages.Write(Messages.BadRequest($"branch_delete 被服务端拒绝：{HttpDetail(st, body)}。本次未产生任何副作用"));
                    return ExitCodes.BadRequest;
            }
        }
        catch (HttpRequestException ex)
        {
            Audit.Append("branch_delete", repo, branch, sha, "http_failed", consent: "hello", result: "not_sent");
            Messages.Write(Messages.HttpFailed(GitHubApi.Describe(ex)));
            return ExitCodes.HttpFailed;
        }
        catch (TaskCanceledException)
        {
            // DELETE 幂等（重删得 404）但请求可能已送达——诚实归 unknown，查证 refs/heads 即知
            Audit.Append("branch_delete", repo, branch, sha, "unknown", consent: "hello", result: "timeout");
            Messages.Write(Messages.Unknown("超时"));
            return ExitCodes.Unknown;
        }
    }

    private static int Created(GitHubApi.ApiResult r, string repo, int number, string sha)
    {
        Audit.Append("pr_comment", repo, $"#{number}", sha, "approved", consent: "hello", result: $"executed:{r.Url}");
        Messages.Write(Messages.Success("pr_comment", $"{repo}#{number}", 201, r.Url!));
        return ExitCodes.Success;
    }

    private static int Expired(GitHubApi.ApiResult r, string repo, int number, string sha)
    {
        Audit.Append("pr_comment", repo, $"#{number}", sha, "credential_expired", consent: "hello",
            result: $"http_{r.Status}");
        Messages.Write(Messages.CredentialExpired($"HTTP {r.Status}"));
        return ExitCodes.CredentialExpired;
    }

    private static int NotSent(GitHubApi.ApiResult r, string repo, int number, string sha)
    {
        Audit.Append("pr_comment", repo, $"#{number}", sha, "http_failed", consent: "hello", result: "not_sent");
        Messages.Write(Messages.HttpFailed(r.Reason ?? "连接失败"));
        return ExitCodes.HttpFailed;
    }

    private static int UnknownOutcome(GitHubApi.ApiResult r, string repo, int number, string sha)
    {
        Audit.Append("pr_comment", repo, $"#{number}", sha, "unknown", consent: "hello",
            result: $"unknown:{r.Reason}");
        Messages.Write(Messages.Unknown(r.Reason ?? "结果不明"));
        return ExitCodes.Unknown;
    }

    private static int InternalPost(GitHubApi.ApiResult r, string repo, int number, string sha)
    {
        Audit.Append("pr_comment", repo, $"#{number}", sha, "internal", consent: "hello",
            result: $"http_{r.Status}");
        Messages.Write(Messages.Internal(afterExecutionStarted: true));
        return ExitCodes.Internal;
    }
}
