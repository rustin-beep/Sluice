using System.Security.Cryptography;
using System.Text;

namespace Sluice;

internal static class Program
{
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
        ["api", .. var rest] => RunApi(rest),
        [var verb, ..] => BadVerb(verb),
    };

    private static int Usage()
    {
        Console.WriteLine("""
            sluice — GitHub 写操作事件闸门（Sluice Lite V0）

            用法：
              sluice setup [--token-file F]             配置凭据（粘贴 fine-grained PAT + Windows Hello 建 vault）
              sluice seal [--fix]                       侧门自查 / 封堵（凭据管理器、gh、git-credentials、SSH、环境变量）
              sluice comment --repo O/R --number N --body-file F   在 issue/PR 下发表评论（经批准 + Hello）
              sluice push --repo O/R --branch B [--repo-path D] [--base REF]
                                                        推送本地 HEAD 到远端分支（REST，经批准 + Hello；
                                                        新建远端分支时 --base 给增量基点，如 origin/master）
              sluice pr create --repo O/R --base B --head H --title T --body-file F
                                                        创建 PR（经批准 + Hello）
              sluice api <api-path> [--auth]           REST GET 读（默认匿名；--auth 过 Hello 带 token；
                                                        路径如 repos/owner/repo/pulls/9，每次调用入读审计）
              sluice selftest                           消息表与 exit code 自检

            退出码见 `sluice selftest` 输出；消息规范见 plan-lite.md §8。
            """);
        return 0;
    }

    private static int Version()
    {
        Console.WriteLine("sluice 0.1.0 (V0, plan-lite v2.2)");
        return 0;
    }

    private static int BadVerb(string verb)
    {
        Messages.Write(Messages.BadRequest($"未知命令 \"{verb}\"；可用动词：setup / seal / comment / push / pr / api / selftest"));
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
                    Messages.Write(Messages.BadRequest($"无法识别的参数：{args[i]}"));
                    return ExitCodes.BadRequest;
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
        if (branch is "main" or "master")
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
                Messages.Write(Messages.Timeout("push", $"{repo}@{branch}"));
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
            RestPush.PushResult.NonFF => PushFail(plan, "policy_non_ff", Messages.PolicyReject(r.Reason ?? "非 fast-forward"), ExitCodes.PolicyReject, r.Reason),
            RestPush.PushResult.HasMerge => PushFail(plan, "has_merge", Messages.BadRequest(r.Reason ?? "含 merge 提交"), ExitCodes.BadRequest, r.Reason),
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
                    Messages.Write(Messages.BadRequest($"无法识别的参数：{args[i]}"));
                    return ExitCodes.BadRequest;
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
                Messages.Write(Messages.Timeout("pr_comment", target));
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
            if (a.StartsWith("--"))
            {
                Messages.Write(Messages.BadRequest($"无法识别的参数：{a}"));
                return ExitCodes.BadRequest;
            }
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
        // ?&: 支持分页/过滤（?page=2、?head=owner:branch——`://` 已单拒，裸 `:` 无 scheme 危害）
        if (path.StartsWith('/') || path.Contains("..") || path.Contains("://")
            || !System.Text.RegularExpressions.Regex.IsMatch(path, @"^[A-Za-z0-9._/@{}=%\-:?&]+$"))
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
                    Messages.Write(Messages.BadRequest($"无法识别的参数：{args[i]}"));
                    return ExitCodes.BadRequest;
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
                Messages.Write(Messages.Timeout("pr_create", target));
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
