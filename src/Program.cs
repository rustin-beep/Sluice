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
              sluice push --repo O/R --branch B [--repo-path D]    推送本地 HEAD 到远端分支（REST，经批准 + Hello）
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
        Messages.Write(Messages.BadRequest($"未知命令 \"{verb}\"；可用动词：setup / seal / comment / selftest"));
        return ExitCodes.BadRequest;
    }

    // ---------- sluice comment ----------

    // ---------- sluice push（V1 · 纯 REST，§9 push bridge 依 2026-10-29 实测改向） ----------

    private static int RunPush(string[] args)
    {
        string? repo = null, branch = null;
        var repoPath = ".";
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo":
                    if (i + 1 >= args.Length) return ErrMissing("--repo");
                    repo = args[++i];
                    break;
                case "--branch":
                    if (i + 1 >= args.Length) return ErrMissing("--branch");
                    branch = args[++i];
                    break;
                case "--repo-path":
                    if (i + 1 >= args.Length) return ErrMissing("--repo-path");
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
            plan = RestPush.PlanLocal(repo, branch, Path.GetFullPath(repoPath));
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

    private static int ErrMissing(string opt)    {
        Messages.Write(Messages.BadRequest($"pr_comment 参数 {opt} 缺少值"));
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
                    if (repo is null) return ErrMissing("--repo");
                    break;
                case "--number":
                    var v = Next();
                    if (v is null || !int.TryParse(v, out number) || number <= 0)
                        return ErrBad("--number 需要正整数");
                    break;
                case "--body-file":
                    bodyFile = Next();
                    if (bodyFile is null) return ErrMissing("--body-file");
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
