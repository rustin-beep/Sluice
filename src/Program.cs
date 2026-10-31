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
        // GUI 宿主可能无控制台句柄（sluice mcp 被宿主以管道方式拉起时 setter 会抛）——尽力设置
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* 管道/无控制台：McpServer 自带显式 UTF-8 流 */ }
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

    /// <summary>
    /// §9 写动词表（v2.11 面一致锁数据源）：CLI dispatch 由本表驱动——本表 ↔ plan-lite §9 op 列 ↔
    /// McpServer.Actions 三方对账入 selftest，"§9 表承诺而实现没有"从此红脸（缘起：pr_review 漂移
    /// 靠人眼评审抓到）。op 与动词不同名的历史映射仅 pr_comment→comment、pr_review→review
    /// （后者是 §9 v2.0 的既有承诺形状）。
    /// </summary>
    internal static readonly (string Verb, string Op, Func<string[], int> Run)[] WriteVerbs =
    [
        ("comment",          "pr_comment",       RunComment),
        ("push",             "push",             RunPush),
        ("pr create",        "pr_create",        RunPrCreate),
        ("pr merge",         "pr_merge",         RunPrMerge),
        ("pr reopen",        "pr_reopen",        RunPrReopen),
        ("pr close",         "pr_close",         RunPrClose),
        ("pr edit",          "pr_edit",          RunPrEdit),
        ("issue create",     "issue_create",     RunIssueCreate),
        ("issue close",      "issue_close",      RunIssueClose),
        ("issue reopen",     "issue_reopen",     RunIssueReopen),
        ("review",           "pr_review",        RunReview),
        ("workflow dispatch","workflow_dispatch",RunWorkflowDispatch),
        ("branch delete",    "branch_delete",    RunBranchDelete),
    ];

    private static int Dispatch(string[] args)
    {
        // 写动词先查表（两词路径优先于一词），其余固定命令走 switch
        for (var n = Math.Min(2, args.Length); n >= 1; n--)
        {
            var path = string.Join(' ', args[..n]);
            var idx = Array.FindIndex(WriteVerbs, v => v.Verb == path);
            if (idx >= 0) return WriteVerbs[idx].Run(args[n..]);
        }
        return args switch
        {
            [] or ["--help"] or ["help"] or ["-h"] => Usage(),
            ["--version"] or ["version"] => Version(),
            ["setup"] => SetupCommand.SetupAsync(null).GetAwaiter().GetResult(),
            ["setup", "--token-file", var f] => SetupCommand.SetupAsync(f).GetAwaiter().GetResult(),
            ["seal"] => SetupCommand.Seal(fix: false),
            ["seal", "--fix"] => SetupCommand.Seal(fix: true),
            ["selftest"] => Selftest.Run(),
            ["api", .. var rest] => RunApi(rest),
            ["mcp", .. var rest] => McpServer.Run(rest),
            [var verb, ..] => BadVerb(verb),
        };
    }

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
                                                        --base：新建分支给增量基点（如 origin/master）；
                                                        已有分支给新基点 = 重放式更新（原地替换旧头，
                                                        PR 原地 rebase 更新，弹窗明示丢弃清单）。
                                                        多请求序列，调用方超时应给 ≥300s）
              sluice pr create --repo O/R --base B --head H --title T --body-file F
                                                        创建 PR（经批准 + Hello）
              sluice pr merge --repo O/R --number N [--method merge|squash|rebase] [--delete-head]
                                                        合并 PR（服务端 merge API：天然尊重分支保护与
                                                        required checks；--delete-head 合并后删 head 分支）
              sluice pr reopen --repo O/R --number N
                                                        重开已关闭的 PR（经批准；reopened 事件会再次触发
                                                        pull_request 类 CI——也是 CI 卡死时的首选补救）
              sluice issue close --repo O/R --number N
                                                        关闭 issue（经批准；销案闭环。PR 编号会被预检拦下）
              sluice issue create --repo O/R --title T --body-file F
                                                        新建 issue（经批准；立案闭环——issue_close 的对偶件）
              sluice issue reopen --repo O/R --number N
                                                        重开已关 issue（经批准；issue_close 的对偶件，
                                                        PR 编号同样会被预检拦下）
              sluice pr close --repo O/R --number N
                                                        关闭 PR（经批准；pr reopen 的对偶件。讨论保留，
                                                        可随时 reopen）
              sluice pr edit --repo O/R --number N [--title T] [--body-file F]
                                                        勘误 PR 标题/描述（经批准；弹窗披露旧值→新值，
                                                        至少给 --title / --body-file 之一）
              sluice review --repo O/R --number N --event COMMENT|APPROVE|REQUEST_CHANGES [--body-file F]
                                                        在 PR 上落评审（经批准；commit_id 锚定"所见即所评"。
                                                        注意 GitHub 禁止 APPROVE 自己账户的 PR）
              sluice workflow dispatch --repo O/R --workflow W --ref R [--inputs-file F]
                                                        手动触发 workflow（经批准；W 为 id 或文件名，R 为
                                                        分支/标签，F 为 JSON 对象对应 dispatch inputs。
                                                        需 PAT Actions:Read and write，缺则 403 有专属指引）
              sluice branch delete --repo O/R --branch B
                                                        删除远端分支 ref（commits 保留，可用 sha 重建）
              sluice api <api-path> [--auth]           REST GET 读（默认匿名；--auth 过 Hello 带 token；
                                                        路径如 repos/owner/repo/pulls/9，可含 ?query，每次调用入读审计；
                                                        logs 等端点 302→CDN 签名 URL 自动跟随，仅限 GitHub 域 ≤3 跳）
              sluice selftest                           消息表与 exit code 自检
              sluice mcp [--print-config]               MCP 前门（§9.7）：stdio JSON-RPC 服务，
                                                        单一类型化工具 github_write；spawn-child
                                                        拓扑——本进程不持凭据，每次调用绝对路径
                                                        spawn CLI 子进程走完整闸门

            退出码见 `sluice selftest` 输出；消息规范见 plan-lite.md §8。
            """);
        return 0;
    }

    private static int Version()
    {
        // 版本号来自 csproj <Version>（2026-10-30 评审：升版不打印等于没升）；构建戳另含时间+git sha(+dirty)
        var v = typeof(Program).Assembly.GetName().Version;
        Console.WriteLine($"sluice v{v?.ToString(3) ?? "?"} · {BuildStamp} · plan-lite v2.12");
        return 0;
    }

    private static int BadVerb(string verb)
    {
        Messages.Write(Messages.BadRequest(
            $"未知命令 \"{verb}\"；可用动词：setup / seal / comment / push / pr / issue / review / workflow / branch / api / mcp / selftest。{BuildStamp}——若你需要的是较新版本的动词，说明正在使用旧构建，请重新 dotnet publish"));
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
                    // 增量基点（ref 或 sha，如 origin/master）：新建远端分支 = 增量推送（存量仓库新分支必给）；
                    // 已有远端分支 = 重放式更新（原地替换旧头，PR 不换分支，弹窗明示替换与丢弃清单，§9.6）
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
                    // policy tripwire：裸 force 永不批准，弹窗都不出现（§9）。
                    // 覆盖式更新的唯一合法形态 = --base 重放通道（§9.6）：force 不是参数，是"声明新基点"的结论
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

        // v2.12（缺陷一 belts 层）：--base 基点远端在场的匿名预检——公开库可在弹窗前警示（省一道批准），
        // 私有库匿名 404 与"不在远端"不可区分（v2.8 纪律：不硬拒）；权威终判在执行期首步（已前移到
        // 任何上传之前，带 token）。读落审计，失败不阻塞。
        string? baseWarn = null;
        if (plan.Base is { Length: 40 or 64 } baseSha)
        {
            try
            {
                var (bst, bbody, bNotSent, _) = GitHubApi.ApiGetAsync(null, $"repos/{repo}/git/commits/{baseSha}")
                    .GetAwaiter().GetResult();
                if (!bNotSent)
                    Audit.Append("api_read", "api.github.com", $"repos/{repo}/git/commits/{baseSha}", null,
                        $"http_{bst}", result: $"{bbody.Length}B anon");
                if (!bNotSent && bst == 404)
                    baseWarn = $"--base 基点 {baseSha[..8]} 未见於远端匿名读：公开库 = 确不在远端（执行将拒绝、零上传零副作用）；私有库匿名不可见属正常——由执行时带 token 终判";
            }
            catch { /* 预检尽力而为，不阻塞 */ }
        }

        // 重放式更新：弹窗 target 行明示替换（§9.6 闸门③）——旧头→新头一眼可辨，与常规推送区分
        var gateTarget = plan.ReplacingSha is string oldHead
            ? $"{branch} {oldHead[..8]}→{plan.HeadSha[..8]}（重放式更新）"
            : $"{branch} @ {plan.HeadSha[..8]}";
        var gateWarns = new[]
        {
            plan.TouchesWorkflows ? "本次推送修改 .github/workflows/** —— 批准即部署 CI 变更，请特别确认" : null,
            baseWarn,
        }.Where(w => w is not null).ToArray();
        Gate.Outcome outcome;
        using (InputFilter.Install())
        {
            outcome = Gate.Run(new Gate.Request("push", repo, gateTarget,
                plan.WindowText, plan.HeadSha[..8], TokenStore.PeekChallenge(),
                gateWarns.Length > 0 ? string.Join("\n", gateWarns) : null));
        }

        switch (outcome)
        {
            case Gate.Outcome.Denied d:
                Audit.Append("push", repo, AuditTarget(plan), plan.HeadSha[..8], "denied", d.Preset, d.FreeText, consent: "none");
                Messages.Write(Messages.Denied("push", $"{repo}@{branch}", d.Preset, d.FreeText));
                return ExitCodes.Denied;
            case Gate.Outcome.TimedOut:
                Audit.Append("push", repo, AuditTarget(plan), plan.HeadSha[..8], "timeout");
                Messages.Write(Messages.Timeout("push", $"{repo}@{branch}", Gate.TimeoutSeconds));
                return ExitCodes.Timeout;
            case Gate.Outcome.ConsentFailed:
                Audit.Append("push", repo, AuditTarget(plan), plan.HeadSha[..8], "consent_failed", consent: "hello_failed");
                Messages.Write(Messages.ConsentFailed("push", $"{repo}@{branch}"));
                return ExitCodes.ConsentFailed;
            case Gate.Outcome.VaultKeyMissing vk:
                Audit.Append("push", repo, AuditTarget(plan), plan.HeadSha[..8], "vault_broken");
                Messages.Write(Messages.VaultBroken(vk.Detail));
                return ExitCodes.NotInitialized;
            case Gate.Outcome.Crashed c:
                Audit.Append("push", repo, AuditTarget(plan), plan.HeadSha[..8], "internal_gate_crash", result: c.Error);
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
        try { token = TokenStore.LoadApproved(a.Signature); }
        catch (Exception)
        {
            Audit.Append("push", plan.Repo, AuditTarget(plan), plan.HeadSha[..8], "internal_vault_decrypt_failed", consent: TokenStore.ConsentChannel);
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
            Audit.Append("push", plan.Repo, AuditTarget(plan), plan.HeadSha[..8], "http_failed", consent: TokenStore.ConsentChannel, result: "not_sent");
            Messages.Write(Messages.HttpFailed(GitHubApi.Describe(ex)));
            return ExitCodes.HttpFailed;
        }
        catch (TaskCanceledException)
        {
            Audit.Append("push", plan.Repo, AuditTarget(plan), plan.HeadSha[..8], "unknown", consent: TokenStore.ConsentChannel, result: "timeout");
            Messages.Write(Messages.Unknown("超时"));
            return ExitCodes.Unknown;
        }
        catch (RestPush.LocalPlanException ex)
        {
            Audit.Append("push", plan.Repo, AuditTarget(plan), plan.HeadSha[..8], "bad_request", consent: TokenStore.ConsentChannel);
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
            // 原 policy_reject（"策略永禁"）定性不准，且缺 rebase 正解。裸 force 仍永禁（tripwire 不动）；
            // rebase 到新基点的原地更新走 --base 重放通道（§9.6，消息末行已指向）。
            RestPush.PushResult.NonFF => PushFail(plan, "non_ff",
                Messages.PushNonFF(plan.Repo, plan.Branch, r.Reason ?? "?"), ExitCodes.BadRequest, r.Reason),
            RestPush.PushResult.HasMerge => PushFail(plan, "has_merge",
                Messages.PushHasMerge(r.Reason ?? "?"), ExitCodes.BadRequest, r.Reason),
            RestPush.PushResult.Submodule => PushFail(plan, "submodule", Messages.BadRequest(r.Reason ?? "含 submodule"), ExitCodes.BadRequest, r.Reason),
            RestPush.PushResult.Unclear => PushFail(plan, "unknown", Messages.Unknown(r.Reason ?? "结果不明"), ExitCodes.Unknown, r.Reason),
            RestPush.PushResult.HeadMoved => PushFail(plan, "head_moved", Messages.BadRequest(r.Reason ?? "批准期间 HEAD 已变化"), ExitCodes.BadRequest, r.Reason),
            // 重放式更新保真复核（§9.6 闸门④）：批准→执行间远端头被推进/分支被删即中止，
            // 与 HeadMoved / PrMergeChanged 同族——静默 force 会孤儿化窗口外的新提交
            RestPush.PushResult.RemoteMoved => PushFail(plan, "remote_moved",
                Messages.PushRemoteMoved(plan.Repo, plan.Branch, r.Reason ?? "远端头已变化"), ExitCodes.BadRequest, r.Reason),
            // 重放前置缺口（v2.10 §9.6 闸门⑤）：--base 已声明 + 远端分支已存在 + 规划期本地无
            // origin/{branch}（REST 推送不回写本地引用）——丢弃清单需要本地对象，fail-closed 点名
            // fetch；不落 NonFF（那会开出"加 --base"这条已满足的死指令，2026-10-31 实战绕圈根源）
            RestPush.PushResult.ReplayUnresolved => PushFail(plan, "replay_unresolved",
                Messages.PushReplayUnresolved(plan.Repo, plan.Branch), ExitCodes.BadRequest, r.Reason),
            // v2.12：三新档 + Other 兜底臂改渲染 Reason——原 Internal(after=true) 无 Reason 槽位且
            // "可能已完成"对整个 Other 族都是假话（update-ref 未执行，分支必未动）
            RestPush.PushResult.BaseMissing => PushFail(plan, "base_missing",
                Messages.PushBaseMissing(r.Reason ?? "--base 基点不在远端"), ExitCodes.BadRequest, r.Reason),
            RestPush.PushResult.TooLarge => PushFail(plan, "too_large",
                Messages.PushTooLarge(r.Reason ?? "超过推送上限"), ExitCodes.BadRequest, r.Reason),
            RestPush.PushResult.BlobMismatch => PushFail(plan, "blob_mismatch",
                Messages.PushBlobMismatch(r.Reason ?? "blob sha 与内容寻址不符"), ExitCodes.HttpFailed, r.Reason),
            _ => PushFail(plan, "other", Messages.PushStepRejected(r.Reason), ExitCodes.BadRequest, r.Reason),
        };
    }

    private static int PushFail(RestPush.Plan plan, string decision, Msg msg, int exit, string? reason = null)
    {
        Audit.Append("push", plan.Repo, AuditTarget(plan), plan.HeadSha[..8], decision, consent: TokenStore.ConsentChannel,
            result: reason is null ? "not_executed" : $"not_executed:{reason}");
        Messages.Write(msg);
        return exit;
    }

    private static int Done(RestPush.Result r, RestPush.Plan plan)
    {
        Audit.Append("push", plan.Repo, AuditTarget(plan), plan.HeadSha[..8], "approved", consent: TokenStore.ConsentChannel,
            result: plan.ReplacingSha is string old
                ? $"executed:{r.NewSha}（重放替换 {old[..8]}）"
                : $"executed:{r.NewSha}");
        // v2.10：换 PushDone 专属模板——REST 推送不回写本地 origin/{branch} 引用，成功消息
        // 必附 fetch 提示（否则"首次成功 → rebase → 再推必踩"是隐形确定性路径）
        // NewSha 兜底 HeadSha：Done 路径服务端必回 sha，兜底仅为可空性收口（内容寻址，二者同物）
        Messages.Write(Messages.PushDone(plan.Repo, plan.Branch, r.NewSha ?? plan.HeadSha, plan.ReplacingSha is not null));
        return ExitCodes.Success;
    }

    private static int UpToDate(RestPush.Plan plan)
    {
        Audit.Append("push", plan.Repo, AuditTarget(plan), plan.HeadSha[..8], "up_to_date", consent: TokenStore.ConsentChannel);
        Messages.Write(Messages.PushUpToDate(plan.Repo, plan.Branch, plan.HeadSha[..8]));
        return ExitCodes.Success;
    }

    /// <summary>审计 target（§9.6）：重放记 branch@旧头sha8→新头sha8——替换前后可追溯；常规记分支名。</summary>
    private static string AuditTarget(RestPush.Plan plan) =>
        plan.ReplacingSha is string old ? $"{plan.Branch}@{old[..8]}→{plan.HeadSha[..8]}" : plan.Branch;

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

    /// <summary>--repo 形状校验（owner/name；v2.11 收敛单源，原各 op 内联）。</summary>
    private static bool IsRepoName(string repo) =>
        System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$");

    /// <summary>SHA256 前 8 位小写（指纹公式族共用）。</summary>
    private static string Sha8(string s) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..8].ToLowerInvariant();

    /// <summary>写 op 前置（v2.11 收敛单源）：vault 存在性 + 承重墙侧门自查。
    /// false = 已写终态消息，out exitCode 直接返回。</summary>
    private static bool TryPreflight(string op, string repo, string auditTarget, out int exitCode)
    {
        exitCode = 0;
        if (!TokenStore.Exists)
        {
            Messages.Write(Messages.NotInitialized());
            exitCode = ExitCodes.NotInitialized;
            return false;
        }
        var violations = SealCheck.Run();
        if (violations.Count > 0)
        {
            Audit.Append(op, repo, auditTarget, null, "seal_violation");
            Messages.Write(Messages.SealViolation(string.Join("；", violations)));
            exitCode = ExitCodes.SealViolation;
            return false;
        }
        return true;
    }

    /// <summary>尽力匿名元数据预读（pr_merge/pr_reopen/issue_close 纪律，v2.11 收敛单源）：
    /// 读落审计；失败/非 200 不阻塞批准流——弹窗退化为参数展示 + 警示行。
    /// 返回 (状态码：-1=未发出/0=超时/其它=HTTP 状态，200 时给 JsonNode)。</summary>
    private static (int St, System.Text.Json.Nodes.JsonNode? Json) TryReadMeta(string path)
    {
        try
        {
            var (st, body, notSent, _) = GitHubApi.ApiGetAsync(null, path).GetAwaiter().GetResult();
            if (notSent)
                Audit.Append("api_read", "api.github.com", path, null, "http_failed", result: "not_sent");
            else if (st == 0)
                Audit.Append("api_read", "api.github.com", path, null, "timeout", result: "retry_safe_get");
            else
                Audit.Append("api_read", "api.github.com", path, null, $"http_{st}", result: $"{body.Length}B anon");
            return notSent ? (-1, null) : st == 200 ? (st, System.Text.Json.Nodes.JsonNode.Parse(body)) : (st, null);
        }
        catch { return (0, null); }
    }

    /// <summary>v2.11 新 op 共用的闸门段（与既有 op 的 outcome switch 逐分支同构，收敛单源）。</summary>
    private static int RunGatedOp(string op, string repo, string auditTarget, string msgTarget, string sha,
        string window, string? gateWarning, Func<Gate.Outcome.Approved, int> execute)
    {
        Gate.Outcome outcome;
        using (InputFilter.Install())
        {
            outcome = Gate.Run(new Gate.Request(op, repo, auditTarget, window, sha,
                TokenStore.PeekChallenge(), gateWarning));
        }

        switch (outcome)
        {
            case Gate.Outcome.Denied d:
                Audit.Append(op, repo, auditTarget, sha, "denied", d.Preset, d.FreeText, consent: "none");
                Messages.Write(Messages.Denied(op, msgTarget, d.Preset, d.FreeText));
                return ExitCodes.Denied;
            case Gate.Outcome.TimedOut:
                Audit.Append(op, repo, auditTarget, sha, "timeout");
                Messages.Write(Messages.Timeout(op, msgTarget, Gate.TimeoutSeconds));
                return ExitCodes.Timeout;
            case Gate.Outcome.ConsentFailed:
                Audit.Append(op, repo, auditTarget, sha, "consent_failed", consent: "hello_failed");
                Messages.Write(Messages.ConsentFailed(op, msgTarget));
                return ExitCodes.ConsentFailed;
            case Gate.Outcome.VaultKeyMissing vk:
                Audit.Append(op, repo, auditTarget, sha, "vault_broken");
                Messages.Write(Messages.VaultBroken(vk.Detail));
                return ExitCodes.NotInitialized;
            case Gate.Outcome.Crashed c:
                Audit.Append(op, repo, auditTarget, sha, "internal_gate_crash", result: c.Error);
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
            case Gate.Outcome.Approved a:
                return execute(a);
            default:
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
        }
    }

    /// <summary>
    /// v2.11 新 op 共用执行段：SendRawAsync 单请求。四态纪律：2xx→0；401→9；
    /// 403/404/422→writeRejected 闭包（exit 10/after_fix 族，forbidden/not_found/{op}_rejected
    /// 的审计细分由闭包落笔）；≥500 与超时→21 unknown——非幂等写下服务端 5xx 同样结果不明
    /// （v2.11 不变量，对齐 comment/pr_create 的 create 族先例；v2.7 PATCH 族原归 10 的
    /// pr_reopen/issue_close 已同批改齐）；未发出→20。
    /// </summary>
    private static int ExecuteGated(Gate.Outcome.Approved a, string op, string repo, string auditTarget,
        string msgTarget, string sha, HttpMethod method, string path, string jsonBody,
        Func<string, string> urlOfBody, Action<int, string> writeRejected)
    {
        string token;
        try { token = TokenStore.LoadApproved(a.Signature); }
        catch (Exception)
        {
            Audit.Append(op, repo, auditTarget, sha, "internal_vault_decrypt_failed", consent: TokenStore.ConsentChannel);
            Messages.Write(Messages.Internal(afterExecutionStarted: false));
            return ExitCodes.Internal;
        }

        try
        {
            var (st, body) = GitHubApi.SendRawAsync(method, path, token, jsonBody).GetAwaiter().GetResult();
            switch (st)
            {
                case 200 or 201:
                    Audit.Append(op, repo, auditTarget, sha, "approved", consent: TokenStore.ConsentChannel, result: "executed");
                    Messages.Write(Messages.Success(op, msgTarget, st, urlOfBody(body)));
                    return ExitCodes.Success;
                case 401:
                    Audit.Append(op, repo, auditTarget, sha, "credential_expired", consent: TokenStore.ConsentChannel, result: "http_401");
                    Messages.Write(Messages.CredentialExpired("HTTP 401"));
                    return ExitCodes.CredentialExpired;
                case >= 500:
                    Audit.Append(op, repo, auditTarget, sha, "unknown", consent: TokenStore.ConsentChannel, result: $"http_{st}");
                    Messages.Write(Messages.Unknown($"HTTP {st}"));
                    return ExitCodes.Unknown;
                default:
                    writeRejected(st, HttpDetail(st, body));
                    return ExitCodes.BadRequest;
            }
        }
        catch (HttpRequestException ex)
        {
            Audit.Append(op, repo, auditTarget, sha, "http_failed", consent: TokenStore.ConsentChannel, result: "not_sent");
            Messages.Write(Messages.HttpFailed(GitHubApi.Describe(ex)));
            return ExitCodes.HttpFailed;
        }
        catch (TaskCanceledException)
        {
            // 非幂等写（PATCH/POST）：可能已生效——诚实归 unknown，查证后决定是否重发
            Audit.Append(op, repo, auditTarget, sha, "unknown", consent: TokenStore.ConsentChannel, result: "timeout");
            Messages.Write(Messages.Unknown("超时"));
            return ExitCodes.Unknown;
        }
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
        try { token = TokenStore.LoadApproved(a.Signature); }
        catch (Exception)
        {
            Audit.Append("pr_comment", repo, $"#{number}", sha, "internal_vault_decrypt_failed", consent: TokenStore.ConsentChannel);
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
            // 读无破坏力：不弹批准窗，指纹（Hello）即唯一门槛——vault 解密的技术必然。
            // D1：静默 vault（SLCV2，hello:false）无签名可言，直接静默取——
            // vault 本身已是 owner 显式选择的静默格式，--auth 读随之下沉为零交互。
            byte[]? sig = null;
            if (!TokenStore.IsDpapiVault)
            {
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
            }
            try { token = TokenStore.LoadApproved(sig); }
            catch (Exception)
            {
                Audit.Append("api_read", "api.github.com", path, null, "internal_vault_decrypt_failed", consent: TokenStore.ConsentChannel);
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
            case >= 300 and < 400:
                // v2.7：域外/超跳重定向不跟随——去向在 Reason 里明说，不装开放代理
                Messages.Write(Messages.ApiRedirected(path, reason ?? "域外重定向"));
                return ExitCodes.BadRequest;
            case 401 or 403:
                // 403 限流嗅探（v2.10，2026-10-31 实战）：GitHub 对匿名配额耗尽与 secondary/abuse
                // 拦截都回 403——与"私有/凭据失效"混流会开死方子（匿名限流被报成"仓库私有"、
                // secondary 的等待分钟数就在 body 里却被开成"重新 setup"）
                if (st == 403 && GitHubApi.ParseRateLimit(body) is { } rl)
                {
                    if (token is null && rl.Primary)
                    {
                        Messages.Write(Messages.ApiRateLimitedAnon(path, st));
                        return ExitCodes.BadRequest;
                    }
                    var what = rl.Primary
                        ? "配额（rate limit）耗尽，等待当前配额窗重置（约 1 小时内）"
                        : rl.WaitMinutes is int m
                            ? $"secondary rate limit，服务端要求等待约 {m} 分钟"
                            : "secondary rate limit / 滥用检测拦截，等待数分钟";
                    Messages.Write(Messages.ApiRateLimitedWait(path, st, what));
                    return ExitCodes.RateLimitWait;
                }
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
        try { token = TokenStore.LoadApproved(a.Signature); }
        catch (Exception)
        {
            Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "internal_vault_decrypt_failed", consent: TokenStore.ConsentChannel);
            Messages.Write(Messages.Internal(afterExecutionStarted: false));
            return ExitCodes.Internal;
        }

        var r = GitHubApi.CreatePrAsync(token, repo, @base, head, title, body).GetAwaiter().GetResult();
        switch (r.Kind)
        {
            case GitHubApi.OutcomeKind.Created:
                Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "approved", consent: TokenStore.ConsentChannel,
                    result: $"executed:{r.Url}");
                Messages.Write(Messages.Success("pr_create", target, 201, r.Url!));
                return ExitCodes.Success;
            case GitHubApi.OutcomeKind.Unauthorized:
                Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "credential_expired", consent: TokenStore.ConsentChannel,
                    result: $"http_{r.Status}");
                Messages.Write(Messages.CredentialExpired($"HTTP {r.Status}"));
                return ExitCodes.CredentialExpired;
            case GitHubApi.OutcomeKind.NotSent:
                Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "http_failed", consent: TokenStore.ConsentChannel, result: "not_sent");
                Messages.Write(Messages.HttpFailed(r.Reason ?? "连接失败"));
                return ExitCodes.HttpFailed;
            case GitHubApi.OutcomeKind.UnknownOutcome:
                Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "unknown", consent: TokenStore.ConsentChannel,
                    result: $"unknown:{r.Reason}");
                Messages.Write(Messages.Unknown(r.Reason ?? "结果不明"));
                return ExitCodes.Unknown;
            default:
                Audit.Append("pr_create", repo, $"{@base}←{head}", sha, "internal", consent: TokenStore.ConsentChannel,
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
        try { token = TokenStore.LoadApproved(a.Signature); }
        catch (Exception)
        {
            Audit.Append("pr_merge", repo, $"#{number}", sha, "internal_vault_decrypt_failed", consent: TokenStore.ConsentChannel);
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
                Audit.Append("pr_merge", repo, $"#{number}", sha, "http_failed", consent: TokenStore.ConsentChannel,
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
                    Audit.Append("pr_merge", repo, $"#{number}", sha, "internal_verify_failed", consent: TokenStore.ConsentChannel);
                    Messages.Write(Messages.Internal(afterExecutionStarted: false));
                    return ExitCodes.Internal;
                }
                if (now != snap)
                {
                    Audit.Append("pr_merge", repo, $"#{number}", sha, "head_moved", consent: TokenStore.ConsentChannel,
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
                Audit.Append("pr_merge", repo, $"#{number}", sha, "approved", consent: TokenStore.ConsentChannel,
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
                            consent: TokenStore.ConsentChannel, result: dst == 204 ? "executed" : $"http_{dst}");
                        if (dst != 204)
                            Messages.Write(Messages.BranchDeleteFailed(repo, headBranch, HttpDetail(dst, dbody)));
                    }
                    catch (TaskCanceledException)
                    {
                        Audit.Append("pr_merge", repo, headBranch, sha, "delete_head_failed",
                            consent: TokenStore.ConsentChannel, result: "timeout");
                        Messages.Write(Messages.BranchDeleteFailed(repo, headBranch,
                            "删除请求超时（可能已送达）——查证 refs 后按需重删"));
                    }
                    catch (Exception ex)
                    {
                        Audit.Append("pr_merge", repo, headBranch, sha, "delete_head_failed",
                            consent: TokenStore.ConsentChannel, result: "not_sent");
                        Messages.Write(Messages.BranchDeleteFailed(repo, headBranch, ex.Message));
                    }
                }
                return ExitCodes.Success;
            case GitHubApi.OutcomeKind.Unauthorized: // v2.5 起 401 专属（403 已在服务端拆出）
                Audit.Append("pr_merge", repo, $"#{number}", sha, "credential_expired", consent: TokenStore.ConsentChannel,
                    result: $"http_{r.Status}");
                Messages.Write(Messages.CredentialExpired($"HTTP {r.Status}"));
                return ExitCodes.CredentialExpired;
            case GitHubApi.OutcomeKind.NotSent:
                Audit.Append("pr_merge", repo, $"#{number}", sha, "http_failed", consent: TokenStore.ConsentChannel, result: "not_sent");
                Messages.Write(Messages.HttpFailed(r.Reason ?? "连接失败"));
                return ExitCodes.HttpFailed;
            case GitHubApi.OutcomeKind.UnknownOutcome:
                Audit.Append("pr_merge", repo, $"#{number}", sha, "unknown", consent: TokenStore.ConsentChannel,
                    result: $"unknown:{r.Reason}");
                Messages.Write(Messages.Unknown(r.Reason ?? "结果不明"));
                return ExitCodes.Unknown;
            default:
                // 405（不可合并）/ 409（冲突）/ 422（校验拒）：服务端明确拒绝；403 与 404 各配专属
                // 文案（2026-10-30 评审：403 归"不可合并"是误导——它是保护/权限；404 是"不存在"，
                // 与 mergeability 无关）
                Audit.Append("pr_merge", repo, $"#{number}", sha,
                    r.Status == 403 ? "forbidden" : r.Status == 404 ? "not_found" : "pr_merge_rejected",
                    consent: TokenStore.ConsentChannel, result: r.Reason ?? $"http_{r.Status}");
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

        // 连带后果披露（2026-10-31 Vitro 复盘 #7：删 #389 的 head 分支，弹窗没说会关 PR，用户批前不知情）：
        // 尽力而为的匿名预查该分支是否为开放 PR 的 head（公开库可查；私有库/网络失败不阻塞——静态警示行仍生效）。
        // 读无破坏力，走 api 读通道同款匿名 GET 并落读审计（与 pr_merge 元数据读同纪律）。
        var prQuery = $"repos/{repo}/pulls?head={repo[..repo.IndexOf('/')]}:{branch}&state=open";
        var openPrLine = "该分支若是某开放 PR 的 head，删除将连带关闭该 PR（讨论上下文断链；重建同名分支不会恢复已关的 PR）";
        try
        {
            var (pst, pbody, pNotSent, _) = GitHubApi.ApiGetAsync(null, prQuery).GetAwaiter().GetResult();
            if (pNotSent)
                Audit.Append("api_read", "api.github.com", prQuery, null, "http_failed", result: "not_sent");
            else if (pst == 0)
                Audit.Append("api_read", "api.github.com", prQuery, null, "timeout", result: "retry_safe_get");
            else
                Audit.Append("api_read", "api.github.com", prQuery, null, $"http_{pst}", result: $"{pbody.Length}B anon");
            if (pst == 200 && System.Text.Json.Nodes.JsonNode.Parse(pbody) is System.Text.Json.Nodes.JsonArray prArr)
            {
                var prs = new List<string>();
                foreach (var p in prArr)
                {
                    var n = p?["number"]?.GetValue<int>();
                    var t = p?["title"]?.GetValue<string>() ?? "";
                    if (n is { } nn) prs.Add(nn + " " + (t.Length > 40 ? t[..40] + "…" : t));
                }
                if (prs.Count > 0)
                    openPrLine = $"⚠ 删除将连带关闭该分支上的开放 PR：{string.Join("；", prs)}——讨论上下文断链，重建同名分支不会恢复已关的 PR";
            }
        }
        catch { /* 预查失败不阻塞删除流——静态警示行仍披露规则 */ }

        var window = $"""
            删除远端分支：{target}
            这是删除分支 ref，不是销毁历史：commits 仍留在服务端（GC 窗口内可用 sha 重建）。
            但若该分支含未合并的独有提交，删除后它将不再出现在任何 PR/分支列表中。
            {openPrLine}
            """;

        Gate.Outcome outcome;
        using (InputFilter.Install())
        {
            outcome = Gate.Run(new Gate.Request("branch_delete", repo, branch, window, sha,
                TokenStore.PeekChallenge(), openPrLine.StartsWith('⚠') ? openPrLine : null));
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
        try { token = TokenStore.LoadApproved(a.Signature); }
        catch (Exception)
        {
            Audit.Append("branch_delete", repo, branch, sha, "internal_vault_decrypt_failed", consent: TokenStore.ConsentChannel);
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
                    Audit.Append("branch_delete", repo, branch, sha, "approved", consent: TokenStore.ConsentChannel,
                        result: "executed");
                    Messages.Write(Messages.Success("branch_delete", target, 204,
                        $"https://github.com/{repo}/branches"));
                    return ExitCodes.Success;
                // 403 拆分（2026-10-30 评审）：403 多为分支保护/无删除权限（服务端策略拒），
                // 归凭据过期(9)会把 agent 引向重新 setup 的死路；仅 401 是凭据问题
                case 401:
                    Audit.Append("branch_delete", repo, branch, sha, "credential_expired", consent: TokenStore.ConsentChannel,
                        result: "http_401");
                    Messages.Write(Messages.CredentialExpired("HTTP 401"));
                    return ExitCodes.CredentialExpired;
                case 403:
                    Audit.Append("branch_delete", repo, branch, sha, "forbidden", consent: TokenStore.ConsentChannel,
                        result: HttpDetail(st, body));
                    Messages.Write(Messages.BranchDeleteForbidden(repo, branch, HttpDetail(st, body)));
                    return ExitCodes.BadRequest;
                case 404:
                    Audit.Append("branch_delete", repo, branch, sha, "bad_request", consent: TokenStore.ConsentChannel,
                        result: $"http_404");
                    Messages.Write(Messages.BadRequest($"分支不存在（可能已被删除）：{repo}@{branch}。本次未产生任何副作用"));
                    return ExitCodes.BadRequest;
                default:
                    Audit.Append("branch_delete", repo, branch, sha, "bad_request", consent: TokenStore.ConsentChannel,
                        result: $"http_{st}");
                    Messages.Write(Messages.BadRequest($"branch_delete 被服务端拒绝：{HttpDetail(st, body)}。本次未产生任何副作用"));
                    return ExitCodes.BadRequest;
            }
        }
        catch (HttpRequestException ex)
        {
            Audit.Append("branch_delete", repo, branch, sha, "http_failed", consent: TokenStore.ConsentChannel, result: "not_sent");
            Messages.Write(Messages.HttpFailed(GitHubApi.Describe(ex)));
            return ExitCodes.HttpFailed;
        }
        catch (TaskCanceledException)
        {
            // DELETE 幂等（重删得 404）但请求可能已送达——诚实归 unknown，查证 refs/heads 即知
            Audit.Append("branch_delete", repo, branch, sha, "unknown", consent: TokenStore.ConsentChannel, result: "timeout");
            Messages.Write(Messages.Unknown("超时"));
            return ExitCodes.Unknown;
        }
    }

    // ---------- sluice pr reopen（V1.6 · 2026-10-31 Vitro 复盘 #2：PR 误关/断链后讨论上下文的正解；
    //             reopened 亦会再次触发 pull_request 类 CI——比 workflow dispatch 更常用的 CI 补救。
    //             有意不做 pr_merge 式执行前 TOCTOU 复核：reopen 非破坏性、不发布内容，
    //             批准→执行间的状态漂移由服务端 422 原话兜底（已开放/已合并/head 已删各有专属指引）） ----------

    private static int RunPrReopen(string[] args)
    {
        string? repo = null;
        int number = 0;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo":
                    repo = i + 1 < args.Length ? args[++i] : null;
                    if (repo is null) return ErrMissing("pr_reopen", "--repo");
                    break;
                case "--number":
                    var v = i + 1 < args.Length ? args[++i] : null;
                    if (v is null || !int.TryParse(v, out number) || number <= 0)
                        return ErrBad("--number 需要正整数");
                    break;
                default:
                    return ErrUnknownArg(args[i]);
            }
        }
        if (repo is null || number <= 0)
            return ErrBad("pr_reopen 缺少 --repo / --number（示例：sluice pr reopen --repo owner/repo --number 389）");
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
            Audit.Append("pr_reopen", repo, $"#{number}", null, "seal_violation");
            Messages.Write(Messages.SealViolation(string.Join("；", violations)));
            return ExitCodes.SealViolation;
        }

        // 尽力而为的匿名元数据读（同 pr_merge 纪律：读落审计，失败不阻塞，弹窗退化为参数展示）
        string? prTitle = null, prHead = null, prBase = null, prHeadSha = null, prState = null, prMerged = null;
        int metaSt = 0;
        try
        {
            var (st, body, notSent, _) = GitHubApi.ApiGetAsync(null, $"repos/{repo}/pulls/{number}")
                .GetAwaiter().GetResult();
            metaSt = notSent ? -1 : st;
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
                prHeadSha = pr?["head"]?["sha"]?.GetValue<string>();
                prState = pr?["state"]?.GetValue<string>();
                prMerged = pr?["merged_at"]?.GetValue<string>();
            }
        }
        catch { /* 元数据读失败不阻塞批准流 */ }
        // v2.8（2026-10-31 评审 A）：匿名 404 不再断言"不存在"——私有库对匿名 GET 同样 404，
        // 两者不可区分；断言成终态会误导 agent 认定编号错、换路重试，而正解（继续执行让服务端判）
        // 永远不会被尝试。降级为弹窗警示行，存在性由服务端执行时终判（同 pr_merge 元数据读纪律）。
        var warn404 = metaSt == 404
            ? "\n⚠ 匿名预读 404：编号不存在，或私有库对匿名读不可见——两者不可区分，存在性由服务端执行时判定"
            : "";
        if (prState == "open")
            return ErrBad($"PR #{number} 已是开放状态，无需 reopen；本次未产生任何副作用。若预期中的 CI 未触发，先核对仓库 workflow 的 on: 配置（push/pull_request 触发类型）");
        if (prMerged is not null)
            return ErrBad($"PR #{number} 已合并关闭，不能 reopen（服务端同样会拒）；如需继续变更请开新 PR；本次未产生任何副作用");

        var target = $"{repo}#{number}";
        // 批准指纹混入 head sha（同 pr_merge v2.5 语义）：编号不变内容可变——指纹锚定"所见版本"
        var sha = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{repo}#{number}:reopen:{prHeadSha ?? "?"}")))[..8].ToLowerInvariant();
        var window = $"""
            PR：{target}（当前状态：{prState ?? "（元数据不可读：私有库或网络原因，重开前请自行核对）"}）
            标题：{prTitle ?? "（不可读）"}
            方向：{prBase ?? "?"} ← {prHead ?? "?"}{(prHeadSha is { Length: >= 8 } h ? $"（head {h[..8]}）" : "")}
            重开将恢复该 PR 的讨论与审查上下文；reopened 事件会再次触发 pull_request 类 workflow。{warn404}
            """;

        Gate.Outcome outcome;
        using (InputFilter.Install())
        {
            outcome = Gate.Run(new Gate.Request("pr_reopen", repo, $"#{number}", window, sha,
                TokenStore.PeekChallenge(),
                metaSt == 404 ? "匿名预读 404：编号不存在或私有库匿名不可见，存在性由服务端执行时终判"
                    : prHeadSha is null ? "PR 元数据不可读，重开对象请自行核对" : null));
        }

        switch (outcome)
        {
            case Gate.Outcome.Denied d:
                Audit.Append("pr_reopen", repo, $"#{number}", sha, "denied", d.Preset, d.FreeText, consent: "none");
                Messages.Write(Messages.Denied("pr_reopen", target, d.Preset, d.FreeText));
                return ExitCodes.Denied;
            case Gate.Outcome.TimedOut:
                Audit.Append("pr_reopen", repo, $"#{number}", sha, "timeout");
                Messages.Write(Messages.Timeout("pr_reopen", target, Gate.TimeoutSeconds));
                return ExitCodes.Timeout;
            case Gate.Outcome.ConsentFailed:
                Audit.Append("pr_reopen", repo, $"#{number}", sha, "consent_failed", consent: "hello_failed");
                Messages.Write(Messages.ConsentFailed("pr_reopen", target));
                return ExitCodes.ConsentFailed;
            case Gate.Outcome.VaultKeyMissing vk:
                Audit.Append("pr_reopen", repo, $"#{number}", sha, "vault_broken");
                Messages.Write(Messages.VaultBroken(vk.Detail));
                return ExitCodes.NotInitialized;
            case Gate.Outcome.Crashed c:
                Audit.Append("pr_reopen", repo, $"#{number}", sha, "internal_gate_crash", result: c.Error);
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
            case Gate.Outcome.Approved a:
                return ExecutePrReopen(a, repo, number, sha, target);
            default:
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
        }
    }

    private static int ExecutePrReopen(Gate.Outcome.Approved a, string repo, int number, string sha, string target)
    {
        string token;
        try { token = TokenStore.LoadApproved(a.Signature); }
        catch (Exception)
        {
            Audit.Append("pr_reopen", repo, $"#{number}", sha, "internal_vault_decrypt_failed", consent: TokenStore.ConsentChannel);
            Messages.Write(Messages.Internal(afterExecutionStarted: false));
            return ExitCodes.Internal;
        }

        try
        {
            var (st, body) = GitHubApi.SendRawAsync(HttpMethod.Patch, $"repos/{repo}/pulls/{number}", token,
                new System.Text.Json.Nodes.JsonObject { ["state"] = "open" }.ToJsonString())
                .GetAwaiter().GetResult();
            switch (st)
            {
                case 200:
                    Audit.Append("pr_reopen", repo, $"#{number}", sha, "approved", consent: TokenStore.ConsentChannel, result: "executed");
                    Messages.Write(Messages.Success("pr_reopen", target, 200, $"https://github.com/{repo}/pull/{number}"));
                    return ExitCodes.Success;
                case 401:
                    Audit.Append("pr_reopen", repo, $"#{number}", sha, "credential_expired", consent: TokenStore.ConsentChannel, result: "http_401");
                    Messages.Write(Messages.CredentialExpired("HTTP 401"));
                    return ExitCodes.CredentialExpired;
                case >= 500:
                    // v2.11：PATCH 族 5xx 归 unknown——服务端 5xx 同样结果不明（原归 rejected 会谎称"未执行"），
                    // 对齐 comment/pr_create 的 create 族先例
                    Audit.Append("pr_reopen", repo, $"#{number}", sha, "unknown", consent: TokenStore.ConsentChannel, result: $"http_{st}");
                    Messages.Write(Messages.Unknown($"HTTP {st}"));
                    return ExitCodes.Unknown;
                default:
                    // 403（无 Pull requests 写权限）/ 404（批准期间被删）/ 422（已开放/已合并/head 已删）——透传服务端原话
                    Audit.Append("pr_reopen", repo, $"#{number}", sha,
                        st == 403 ? "forbidden" : st == 404 ? "not_found" : "pr_reopen_rejected",
                        consent: TokenStore.ConsentChannel, result: HttpDetail(st, body));
                    Messages.Write(Messages.PrReopenRejected(target, HttpDetail(st, body)));
                    return ExitCodes.BadRequest;
            }
        }
        catch (HttpRequestException ex)
        {
            Audit.Append("pr_reopen", repo, $"#{number}", sha, "http_failed", consent: TokenStore.ConsentChannel, result: "not_sent");
            Messages.Write(Messages.HttpFailed(GitHubApi.Describe(ex)));
            return ExitCodes.HttpFailed;
        }
        catch (TaskCanceledException)
        {
            // PATCH 非幂等（重开可能已生效）——诚实归 unknown，查证 PR state 后决定是否重发
            Audit.Append("pr_reopen", repo, $"#{number}", sha, "unknown", consent: TokenStore.ConsentChannel, result: "timeout");
            Messages.Write(Messages.Unknown("超时"));
            return ExitCodes.Unknown;
        }
    }

    // ---------- sluice issue close（V1.6 · 2026-10-31 Vitro 复盘 #3：销案闭环——评论留证之后能落锤，
    //             不再"关闭等手动"。同为非破坏性状态写：预检拦常见误用（PR 编号/已关闭），漂移由服务端兜底） ----------

    private static int RunIssueClose(string[] args)
    {
        string? repo = null;
        int number = 0;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo":
                    repo = i + 1 < args.Length ? args[++i] : null;
                    if (repo is null) return ErrMissing("issue_close", "--repo");
                    break;
                case "--number":
                    var v = i + 1 < args.Length ? args[++i] : null;
                    if (v is null || !int.TryParse(v, out number) || number <= 0)
                        return ErrBad("--number 需要正整数");
                    break;
                default:
                    return ErrUnknownArg(args[i]);
            }
        }
        if (repo is null || number <= 0)
            return ErrBad("issue_close 缺少 --repo / --number（示例：sluice issue close --repo owner/repo --number 369）");
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
            Audit.Append("issue_close", repo, $"#{number}", null, "seal_violation");
            Messages.Write(Messages.SealViolation(string.Join("；", violations)));
            return ExitCodes.SealViolation;
        }

        // 尽力而为的匿名元数据读（issues API 对 PR 编号同样应答——靠 pull_request 字段区分类型）
        string? issueTitle = null, issueState = null;
        var isPr = false;
        int metaSt = 0;
        try
        {
            var (st, body, notSent, _) = GitHubApi.ApiGetAsync(null, $"repos/{repo}/issues/{number}")
                .GetAwaiter().GetResult();
            metaSt = notSent ? -1 : st;
            if (notSent)
                Audit.Append("api_read", "api.github.com", $"repos/{repo}/issues/{number}", null, "http_failed", result: "not_sent");
            else if (st == 0)
                Audit.Append("api_read", "api.github.com", $"repos/{repo}/issues/{number}", null, "timeout", result: "retry_safe_get");
            else
                Audit.Append("api_read", "api.github.com", $"repos/{repo}/issues/{number}", null, $"http_{st}", result: $"{body.Length}B anon");
            if (st == 200)
            {
                var iss = System.Text.Json.Nodes.JsonNode.Parse(body);
                issueTitle = iss?["title"]?.GetValue<string>();
                issueState = iss?["state"]?.GetValue<string>();
                isPr = iss?["pull_request"] is not null;
            }
        }
        catch { /* 元数据读失败不阻塞批准流 */ }
        // v2.8（2026-10-31 评审 A）：匿名 404 不再断言"不存在"——私有库对匿名 GET 同样 404，
        // 两者不可区分（同 pr_reopen 降级）。且 issues API 对 PR 编号同样应答——404 降级后
        // "是不是 PR"的类型守卫一并失效，警示行须说破：关到 PR 编号上就是关 PR。
        var warn404 = metaSt == 404
            ? "\n⚠ 匿名预读 404：编号不存在，或私有库对匿名读不可见——若实为 PR 编号，本操作将关闭该 PR（类型未验证）；存在性由服务端执行时判定"
            : "";
        if (isPr)
            return ErrBad($"#{number} 是 PR 不是 issue——PR 的生命周期走 pr merge / pr reopen / branch delete，不走 issue close；本次未产生任何副作用");
        if (issueState == "closed")
            return ErrBad($"#{number} 已处于关闭状态，无需重复关闭；本次未产生任何副作用");

        var target = $"{repo}#{number}";
        // 批准指纹混入标题：编号不变标题可变——指纹锚定"所见内容"（与 comment 的 body 指纹同族）
        var sha = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{repo}#{number}:{issueTitle ?? "?"}")))[..8].ToLowerInvariant();
        var window = $"""
            关闭 issue：{target}
            标题：{issueTitle ?? "（元数据不可读：私有库或网络原因，关闭前请自行核对）"}
            当前状态：{issueState ?? "（不可读）"} → 关闭（closed）
            评论（如需留证请先单独用 sluice comment 发出）不在本操作范围内。{warn404}
            """;

        Gate.Outcome outcome;
        using (InputFilter.Install())
        {
            outcome = Gate.Run(new Gate.Request("issue_close", repo, $"#{number}", window, sha,
                TokenStore.PeekChallenge(),
                metaSt == 404 ? "匿名预读 404：编号不存在或私有库匿名不可见（可能是 PR 编号），由服务端执行时终判"
                    : issueTitle is null ? "issue 元数据不可读，关闭对象请自行核对" : null));
        }

        switch (outcome)
        {
            case Gate.Outcome.Denied d:
                Audit.Append("issue_close", repo, $"#{number}", sha, "denied", d.Preset, d.FreeText, consent: "none");
                Messages.Write(Messages.Denied("issue_close", target, d.Preset, d.FreeText));
                return ExitCodes.Denied;
            case Gate.Outcome.TimedOut:
                Audit.Append("issue_close", repo, $"#{number}", sha, "timeout");
                Messages.Write(Messages.Timeout("issue_close", target, Gate.TimeoutSeconds));
                return ExitCodes.Timeout;
            case Gate.Outcome.ConsentFailed:
                Audit.Append("issue_close", repo, $"#{number}", sha, "consent_failed", consent: "hello_failed");
                Messages.Write(Messages.ConsentFailed("issue_close", target));
                return ExitCodes.ConsentFailed;
            case Gate.Outcome.VaultKeyMissing vk:
                Audit.Append("issue_close", repo, $"#{number}", sha, "vault_broken");
                Messages.Write(Messages.VaultBroken(vk.Detail));
                return ExitCodes.NotInitialized;
            case Gate.Outcome.Crashed c:
                Audit.Append("issue_close", repo, $"#{number}", sha, "internal_gate_crash", result: c.Error);
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
            case Gate.Outcome.Approved a:
                return ExecuteIssueClose(a, repo, number, sha, target);
            default:
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
        }
    }

    private static int ExecuteIssueClose(Gate.Outcome.Approved a, string repo, int number, string sha, string target)
    {
        string token;
        try { token = TokenStore.LoadApproved(a.Signature); }
        catch (Exception)
        {
            Audit.Append("issue_close", repo, $"#{number}", sha, "internal_vault_decrypt_failed", consent: TokenStore.ConsentChannel);
            Messages.Write(Messages.Internal(afterExecutionStarted: false));
            return ExitCodes.Internal;
        }

        try
        {
            var (st, body) = GitHubApi.SendRawAsync(HttpMethod.Patch, $"repos/{repo}/issues/{number}", token,
                new System.Text.Json.Nodes.JsonObject { ["state"] = "closed" }.ToJsonString())
                .GetAwaiter().GetResult();
            switch (st)
            {
                case 200:
                    Audit.Append("issue_close", repo, $"#{number}", sha, "approved", consent: TokenStore.ConsentChannel, result: "executed");
                    Messages.Write(Messages.Success("issue_close", target, 200, $"https://github.com/{repo}/issues/{number}"));
                    return ExitCodes.Success;
                case 401:
                    Audit.Append("issue_close", repo, $"#{number}", sha, "credential_expired", consent: TokenStore.ConsentChannel, result: "http_401");
                    Messages.Write(Messages.CredentialExpired("HTTP 401"));
                    return ExitCodes.CredentialExpired;
                case >= 500:
                    // v2.11：PATCH 族 5xx 归 unknown（同 pr_reopen 改齐）
                    Audit.Append("issue_close", repo, $"#{number}", sha, "unknown", consent: TokenStore.ConsentChannel, result: $"http_{st}");
                    Messages.Write(Messages.Unknown($"HTTP {st}"));
                    return ExitCodes.Unknown;
                default:
                    // 403（无 Issues 写权限）/ 404（批准期间被删）/ 422（含 PR 编号误用透传）——服务端原话
                    Audit.Append("issue_close", repo, $"#{number}", sha,
                        st == 403 ? "forbidden" : st == 404 ? "not_found" : "issue_close_rejected",
                        consent: TokenStore.ConsentChannel, result: HttpDetail(st, body));
                    Messages.Write(Messages.IssueCloseRejected(target, HttpDetail(st, body)));
                    return ExitCodes.BadRequest;
            }
        }
        catch (HttpRequestException ex)
        {
            Audit.Append("issue_close", repo, $"#{number}", sha, "http_failed", consent: TokenStore.ConsentChannel, result: "not_sent");
            Messages.Write(Messages.HttpFailed(GitHubApi.Describe(ex)));
            return ExitCodes.HttpFailed;
        }
        catch (TaskCanceledException)
        {
            // PATCH 非幂等（关闭可能已生效）——诚实归 unknown，查证 issue state 后决定是否重发
            Audit.Append("issue_close", repo, $"#{number}", sha, "unknown", consent: TokenStore.ConsentChannel, result: "timeout");
            Messages.Write(Messages.Unknown("超时"));
            return ExitCodes.Unknown;
        }
    }

    // ---------- sluice issue create（V1.7 · v2.11 生命周期对称批：issue_close 落了销案闭环，
    //             立案没有对偶件——备稿只能搁本地等网页建。pr_create 同模板：title+body 入指纹、
    //             正文全文入窗；POST 非幂等不自动重试（v2.7 不变量引用），5xx/超时 21 查证后再决定） ----------

    private static int RunIssueCreate(string[] args)
    {
        string? repo = null, title = null, bodyFile = null;
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--repo":
                    repo = Next();
                    if (repo is null) return ErrMissing("issue_create", "--repo");
                    break;
                case "--title":
                    title = Next();
                    if (title is null) return ErrMissing("issue_create", "--title");
                    break;
                case "--body-file":
                    bodyFile = Next();
                    if (bodyFile is null) return ErrMissing("issue_create", "--body-file");
                    break;
                default:
                    return ErrUnknownArg(args[i]);
            }
        }
        if (repo is null || title is null || bodyFile is null)
            return ErrBad("issue_create 缺少 --repo / --title / --body-file（示例：sluice issue create --repo owner/repo --title \"标题\" --body-file issue.md）");
        if (!IsRepoName(repo)) return ErrBad($"--repo 应为 owner/name 形式，收到：{repo}");
        if (title.Trim().Length == 0) return ErrBad("--title 不能为空");
        if (!File.Exists(bodyFile)) return ErrBad($"--body-file 不存在：{bodyFile}");
        var auditTarget = "new:" + (title.Length <= 48 ? title : title[..48] + "…");
        if (!TryPreflight("issue_create", repo, auditTarget, out var pre)) return pre;

        string body;
        try { body = File.ReadAllText(bodyFile, System.Text.Encoding.UTF8); }
        catch (Exception ex) { return ErrBad($"无法读取 --body-file：{ex.Message}"); }

        // 指纹公式与 pr_create 同款（title+body）——create 族单源
        var sha = Sha8(title + "\n\n" + body);
        var target = $"{repo} 新建 issue";
        var window = $"""
            新建 issue：{repo}
            标题：{title}
            正文（{body.Length} 字符）：
            {body}
            """;
        return RunGatedOp("issue_create", repo, auditTarget, target, sha, window, null,
            a => ExecuteGated(a, "issue_create", repo, auditTarget, target, sha,
                HttpMethod.Post, $"repos/{repo}/issues",
                new System.Text.Json.Nodes.JsonObject { ["title"] = title, ["body"] = body }.ToJsonString(),
                b => GitHubApi.HtmlUrlOf(b) ?? $"https://github.com/{repo}/issues",
                (st, detail) =>
                {
                    Audit.Append("issue_create", repo, auditTarget, sha,
                        st == 403 ? "forbidden" : st == 404 ? "not_found" : "issue_create_rejected",
                        consent: TokenStore.ConsentChannel, result: detail);
                    Messages.Write(Messages.IssueCreateRejected(target, detail));
                }));
    }

    // ---------- sluice pr close（V1.7 · v2.11：pr_reopen 的镜像——状态机两半各缺半边，错关只能网页救。
    //             关闭非破坏（讨论保留、可随时 reopen）；预检拦已关/已合并省一道弹窗） ----------

    private static int RunPrClose(string[] args)
    {
        string? repo = null;
        int number = 0;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo":
                    repo = i + 1 < args.Length ? args[++i] : null;
                    if (repo is null) return ErrMissing("pr_close", "--repo");
                    break;
                case "--number":
                    var v = i + 1 < args.Length ? args[++i] : null;
                    if (v is null || !int.TryParse(v, out number) || number <= 0)
                        return ErrBad("--number 需要正整数");
                    break;
                default:
                    return ErrUnknownArg(args[i]);
            }
        }
        if (repo is null || number <= 0)
            return ErrBad("pr_close 缺少 --repo / --number（示例：sluice pr close --repo owner/repo --number 389）");
        if (!IsRepoName(repo)) return ErrBad($"--repo 应为 owner/name 形式，收到：{repo}");
        if (!TryPreflight("pr_close", repo, $"#{number}", out var pre)) return pre;

        var (metaSt, pr) = TryReadMeta($"repos/{repo}/pulls/{number}");
        string? prTitle = pr?["title"]?.GetValue<string>();
        string? prBase = pr?["base"]?["ref"]?.GetValue<string>();
        string? prHead = pr?["head"]?["ref"]?.GetValue<string>();
        string? prHeadSha = pr?["head"]?["sha"]?.GetValue<string>();
        string? prState = pr?["state"]?.GetValue<string>();
        string? prMerged = pr?["merged_at"]?.GetValue<string>();
        // v2.8 纪律：匿名 404 不断言"不存在"（私有库匿名读同样 404）——降级为警示行，服务端执行时终判
        var warn404 = metaSt == 404
            ? "\n⚠ 匿名预读 404：编号不存在，或私有库对匿名读不可见——两者不可区分，存在性由服务端执行时判定"
            : "";
        if (prState == "closed")
            return ErrBad($"PR #{number} 已处于关闭状态，无需 close；本次未产生任何副作用。若是误关想恢复：sluice pr reopen --repo {repo} --number {number}");
        if (prMerged is not null)
            return ErrBad($"PR #{number} 已合并——合并是终态（讨论保留），close 无意义，服务端同样会拒；本次未产生任何副作用");

        var target = $"{repo}#{number}";
        // 指纹混入 head sha（镜像 pr_reopen）：编号不变内容可变——锚定"所见版本"
        var sha = Sha8($"{repo}#{number}:close:{prHeadSha ?? "?"}");
        var window = $"""
            PR：{target}（当前状态：{prState ?? "（元数据不可读：私有库或网络原因，关闭前请自行核对）"}）
            标题：{prTitle ?? "（不可读）"}
            方向：{prBase ?? "?"} ← {prHead ?? "?"}{(prHeadSha is { Length: >= 8 } h ? $"（head {h[..8]}）" : "")}
            关闭将停止该 PR 的合并与审查流；讨论与引用保留，可随时用 sluice pr reopen 重开。{warn404}
            """;
        return RunGatedOp("pr_close", repo, $"#{number}", target, sha, window,
            metaSt == 404 ? "匿名预读 404：编号不存在或私有库匿名不可见，存在性由服务端执行时终判"
                : prTitle is null ? "PR 元数据不可读，关闭对象请自行核对" : null,
            a => ExecuteGated(a, "pr_close", repo, $"#{number}", target, sha,
                HttpMethod.Patch, $"repos/{repo}/pulls/{number}",
                new System.Text.Json.Nodes.JsonObject { ["state"] = "closed" }.ToJsonString(),
                _ => $"https://github.com/{repo}/pull/{number}",
                (st, detail) =>
                {
                    Audit.Append("pr_close", repo, $"#{number}", sha,
                        st == 403 ? "forbidden" : st == 404 ? "not_found" : "pr_close_rejected",
                        consent: TokenStore.ConsentChannel, result: detail);
                    Messages.Write(Messages.PrCloseRejected(target, detail));
                }));
    }

    // ---------- sluice issue reopen（V1.7 · v2.11：issue_close 的镜像。PR 编号守卫同款——
    //             issues API 对 PR 编号同样应答，404 降级后类型未验证须说破"重开 PR 编号就是重开 PR"） ----------

    private static int RunIssueReopen(string[] args)
    {
        string? repo = null;
        int number = 0;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo":
                    repo = i + 1 < args.Length ? args[++i] : null;
                    if (repo is null) return ErrMissing("issue_reopen", "--repo");
                    break;
                case "--number":
                    var v = i + 1 < args.Length ? args[++i] : null;
                    if (v is null || !int.TryParse(v, out number) || number <= 0)
                        return ErrBad("--number 需要正整数");
                    break;
                default:
                    return ErrUnknownArg(args[i]);
            }
        }
        if (repo is null || number <= 0)
            return ErrBad("issue_reopen 缺少 --repo / --number（示例：sluice issue reopen --repo owner/repo --number 369）");
        if (!IsRepoName(repo)) return ErrBad($"--repo 应为 owner/name 形式，收到：{repo}");
        if (!TryPreflight("issue_reopen", repo, $"#{number}", out var pre)) return pre;

        var (metaSt, iss) = TryReadMeta($"repos/{repo}/issues/{number}");
        string? issueTitle = iss?["title"]?.GetValue<string>();
        string? issueState = iss?["state"]?.GetValue<string>();
        var isPr = iss?["pull_request"] is not null;
        var warn404 = metaSt == 404
            ? "\n⚠ 匿名预读 404：编号不存在，或私有库对匿名读不可见——若实为 PR 编号，本操作将重开该 PR（类型未验证）；存在性由服务端执行时判定"
            : "";
        if (isPr)
            return ErrBad($"#{number} 是 PR 不是 issue——PR 的生命周期走 pr merge / pr reopen；本次未产生任何副作用");
        if (issueState == "open")
            return ErrBad($"#{number} 已处于开放状态，无需 reopen；本次未产生任何副作用");

        var target = $"{repo}#{number}";
        // 指纹混入标题（镜像 issue_close）：编号不变标题可变——锚定"所见内容"
        var sha = Sha8($"{repo}#{number}:{issueTitle ?? "?"}");
        var window = $"""
            重开 issue：{target}
            标题：{issueTitle ?? "（元数据不可读：私有库或网络原因，重开前请自行核对）"}
            当前状态：{issueState ?? "（不可读）"} → 开放（open）{warn404}
            """;
        return RunGatedOp("issue_reopen", repo, $"#{number}", target, sha, window,
            metaSt == 404 ? "匿名预读 404：编号不存在或私有库匿名不可见（可能是 PR 编号），由服务端执行时终判"
                : issueTitle is null ? "issue 元数据不可读，重开对象请自行核对" : null,
            a => ExecuteGated(a, "issue_reopen", repo, $"#{number}", target, sha,
                HttpMethod.Patch, $"repos/{repo}/issues/{number}",
                new System.Text.Json.Nodes.JsonObject { ["state"] = "open" }.ToJsonString(),
                _ => $"https://github.com/{repo}/issues/{number}",
                (st, detail) =>
                {
                    Audit.Append("issue_reopen", repo, $"#{number}", sha,
                        st == 403 ? "forbidden" : st == 404 ? "not_found" : "issue_reopen_rejected",
                        consent: TokenStore.ConsentChannel, result: detail);
                    Messages.Write(Messages.IssueReopenRejected(target, detail));
                }));
    }

    // ---------- sluice pr edit（V1.7 · v2.11：PR 描述勘误——CHANGELOG 连坐场景曾只能网页。
    //             写覆盖首次入面：弹窗披露旧值→新值（重放通道"丢弃清单"同族纪律），指纹混入新旧两侧） ----------

    private static int RunPrEdit(string[] args)
    {
        string? repo = null, title = null, bodyFile = null;
        int number = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--repo":
                    repo = Next();
                    if (repo is null) return ErrMissing("pr_edit", "--repo");
                    break;
                case "--number":
                    var v = Next();
                    if (v is null || !int.TryParse(v, out number) || number <= 0)
                        return ErrBad("--number 需要正整数");
                    break;
                case "--title":
                    title = Next();
                    if (title is null) return ErrMissing("pr_edit", "--title");
                    break;
                case "--body-file":
                    bodyFile = Next();
                    if (bodyFile is null) return ErrMissing("pr_edit", "--body-file");
                    break;
                default:
                    return ErrUnknownArg(args[i]);
            }
        }
        if (repo is null || number <= 0)
            return ErrBad("pr_edit 缺少 --repo / --number（示例：sluice pr edit --repo owner/repo --number 383 --title \"新标题\" --body-file body.md；--title 与 --body-file 至少给一项）");
        if (title is null && bodyFile is null)
            return ErrBad("pr_edit 需要 --title / --body-file 至少一项（都缺 = 无变更可做）；本次未产生任何副作用");
        if (!IsRepoName(repo)) return ErrBad($"--repo 应为 owner/name 形式，收到：{repo}");
        if (bodyFile is not null && !File.Exists(bodyFile))
            return ErrBad($"--body-file 不存在：{bodyFile}");
        if (!TryPreflight("pr_edit", repo, $"#{number}", out var pre)) return pre;

        string? newBody = null;
        if (bodyFile is not null)
        {
            try { newBody = File.ReadAllText(bodyFile, System.Text.Encoding.UTF8); }
            catch (Exception ex) { return ErrBad($"无法读取 --body-file：{ex.Message}"); }
        }

        var (metaSt, pr) = TryReadMeta($"repos/{repo}/pulls/{number}");
        string? oldTitle = pr?["title"]?.GetValue<string>();
        string? oldBody = pr?["body"]?.GetValue<string>();
        string? oldState = pr?["state"]?.GetValue<string>();
        var warn404 = metaSt == 404
            ? "\n⚠ 匿名预读 404：编号不存在，或私有库对匿名读不可见——两者不可区分，存在性由服务端执行时判定"
            : "";
        // 丢弃清单纪律：旧值必须露面（旧标题原文；旧正文给规模+指纹+预览——全文双窗对长描述不可读）
        var oldDigest = oldBody is null ? "不可读或为空" : $"{oldBody.Length} 字符，指纹 {Sha8(oldBody)}";
        var oldPreview = oldBody is { Length: > 0 }
            ? (oldBody.Length <= 120 ? oldBody : oldBody[..120] + "……") : "（空）";
        var sha = Sha8($"{repo}#{number}:edit:{oldTitle ?? "?"}|{(oldBody is null ? "-" : Sha8(oldBody))}|{title ?? "-"}|{(newBody is null ? "-" : Sha8(newBody))}");
        var target = $"{repo}#{number}";
        var window = $"""
            PR：{target}（当前状态：{oldState ?? "（不可读）"}）
            标题（旧）：{oldTitle ?? "（不可读）"}
            标题（新）：{title ?? "（不变）"}
            正文（旧，{oldDigest}）：{oldPreview}
            正文（新{(newBody is null ? "" : $"，{newBody.Length} 字符")}）：
            {newBody ?? "（不变）"}{warn404}
            """;
        var patch = new System.Text.Json.Nodes.JsonObject();
        if (title is not null) patch["title"] = title;
        if (newBody is not null) patch["body"] = newBody;
        return RunGatedOp("pr_edit", repo, $"#{number}", target, sha, window,
            metaSt == 404 ? "匿名预读 404：编号不存在或私有库匿名不可见，存在性由服务端执行时终判"
                : oldTitle is null ? "PR 元数据不可读（旧值无法披露），编辑对象请自行核对" : null,
            a => ExecuteGated(a, "pr_edit", repo, $"#{number}", target, sha,
                HttpMethod.Patch, $"repos/{repo}/pulls/{number}", patch.ToJsonString(),
                _ => $"https://github.com/{repo}/pull/{number}",
                (st, detail) =>
                {
                    Audit.Append("pr_edit", repo, $"#{number}", sha,
                        st == 403 ? "forbidden" : st == 404 ? "not_found" : "pr_edit_rejected",
                        consent: TokenStore.ConsentChannel, result: detail);
                    Messages.Write(Messages.PrEditRejected(target, detail));
                }));
    }

    // ---------- sluice review（V1.7 · §9 v2.0 既有承诺形状 sluice review --event E——v2.11 销漂移账落地。
    //             commit_id 锚定"所见即所评"（pr_merge head-sha 同族）；APPROVE 自批禁令双说破——
    //             GitHub 服务端 422，agents 与 owner 同账户时 APPROVE 仅对他人 PR 有效） ----------

    private static int RunReview(string[] args)
    {
        string? repo = null, @event = null, bodyFile = null;
        int number = 0;
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--repo":
                    repo = Next();
                    if (repo is null) return ErrMissing("pr_review", "--repo");
                    break;
                case "--number":
                    var v = Next();
                    if (v is null || !int.TryParse(v, out number) || number <= 0)
                        return ErrBad("--number 需要正整数");
                    break;
                case "--event":
                    @event = Next();
                    if (@event is null) return ErrMissing("pr_review", "--event");
                    break;
                case "--body-file":
                    bodyFile = Next();
                    if (bodyFile is null) return ErrMissing("pr_review", "--body-file");
                    break;
                default:
                    return ErrUnknownArg(args[i]);
            }
        }
        @event = @event?.Trim().ToUpperInvariant();
        if (repo is null || number <= 0 || @event is null)
            return ErrBad("pr_review 缺少 --repo / --number / --event（示例：sluice review --repo owner/repo --number 383 --event REQUEST_CHANGES --body-file note.md）");
        if (@event is not ("COMMENT" or "APPROVE" or "REQUEST_CHANGES"))
            return ErrBad($"--event 应为 COMMENT / APPROVE / REQUEST_CHANGES，收到：{@event}");
        if (@event == "COMMENT" && bodyFile is null)
            return ErrBad("COMMENT 评审必须带 --body-file（服务端同样拒绝空正文 COMMENT）；本次未产生任何副作用");
        if (!IsRepoName(repo)) return ErrBad($"--repo 应为 owner/name 形式，收到：{repo}");
        if (bodyFile is not null && !File.Exists(bodyFile))
            return ErrBad($"--body-file 不存在：{bodyFile}");
        if (!TryPreflight("pr_review", repo, $"#{number}", out var pre)) return pre;

        string? body = null;
        if (bodyFile is not null)
        {
            try { body = File.ReadAllText(bodyFile, System.Text.Encoding.UTF8); }
            catch (Exception ex) { return ErrBad($"无法读取 --body-file：{ex.Message}"); }
        }

        var (metaSt, pr) = TryReadMeta($"repos/{repo}/pulls/{number}");
        string? prTitle = pr?["title"]?.GetValue<string>();
        string? prState = pr?["state"]?.GetValue<string>();
        string? prHeadSha = pr?["head"]?["sha"]?.GetValue<string>();
        var warn404 = metaSt == 404
            ? "\n⚠ 匿名预读 404：编号不存在，或私有库对匿名读不可见——两者不可区分，存在性由服务端执行时判定"
            : "";
        if (prState == "closed") // merged 的 state 也是 closed
            return ErrBad($"PR #{number} 已关闭（或已合并）——服务端拒绝在其上创建评审；如需补充意见用 sluice comment，如需复活用 sluice pr reopen；本次未产生任何副作用");

        var eventZh = @event switch { "COMMENT" => "评论", "APPROVE" => "批准", "REQUEST_CHANGES" => "要求修改", _ => @event };
        // commit_id 锚定：批准期解析的 head sha 随评审提交——"所见即所评"；不可读则不锚定并披露
        var sha = Sha8($"{repo}#{number}:review:{@event}:{prHeadSha ?? "?"}");
        var target = $"{repo}#{number}";
        var window = $"""
            PR：{target}（状态：{prState ?? "（不可读）"}）
            标题：{prTitle ?? "（不可读）"}
            动作：{eventZh}（{@event}）
            评审对象：{(prHeadSha is { Length: >= 8 } h ? $"head {h[..8]}（commit_id 锚定——批准的是这个版本）" : "head 未能预读——评审将挂在执行时的当前 head（服务端判定）")}
            附言（{body?.Length ?? 0} 字符）：
            {body ?? "（无）"}{(@event == "APPROVE" ? "\n注意：GitHub 禁止批准自己账户的 PR（HTTP 422）——agents 与 owner 同账户时 APPROVE 仅对他人 PR 有效。" : "")}{warn404}
            """;
        var payload = new System.Text.Json.Nodes.JsonObject();
        if (prHeadSha is not null) payload["commit_id"] = prHeadSha;
        payload["event"] = @event;
        if (body is not null) payload["body"] = body;
        return RunGatedOp("pr_review", repo, $"#{number}", target, sha, window,
            metaSt == 404 ? "匿名预读 404：编号不存在或私有库匿名不可见，存在性由服务端执行时终判"
                : prHeadSha is null ? "PR 元数据不可读（head 未锚定），评审对象请自行核对" : null,
            a => ExecuteGated(a, "pr_review", repo, $"#{number}", target, sha,
                HttpMethod.Post, $"repos/{repo}/pulls/{number}/reviews", payload.ToJsonString(),
                b => GitHubApi.HtmlUrlOf(b) ?? $"https://github.com/{repo}/pull/{number}",
                (st, detail) =>
                {
                    Audit.Append("pr_review", repo, $"#{number}", sha,
                        st == 403 ? "forbidden" : st == 404 ? "not_found" : "pr_review_rejected",
                        consent: TokenStore.ConsentChannel, result: detail);
                    Messages.Write(Messages.PrReviewRejected(target, detail));
                }));
    }

    // ---------- sluice workflow dispatch（V1.6 · 2026-10-31 Vitro 复盘 #4——定性修正后落地：
    //             这是运维便利（手动 nudge 无 PR 场景的 CI），不是"REST push 不触发 CI"的修复
    //             （该定论已被 10-30 上午三次 synchronize 触发实证否定）；CI 卡死优先 pr reopen） ----------

    private static int RunWorkflowDispatch(string[] args)
    {
        string? repo = null, workflow = null, @ref = null, inputsFile = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo":
                    repo = i + 1 < args.Length ? args[++i] : null;
                    if (repo is null) return ErrMissing("workflow_dispatch", "--repo");
                    break;
                case "--workflow":
                    workflow = i + 1 < args.Length ? args[++i] : null;
                    if (workflow is null) return ErrMissing("workflow_dispatch", "--workflow");
                    break;
                case "--ref":
                    @ref = i + 1 < args.Length ? args[++i] : null;
                    if (@ref is null) return ErrMissing("workflow_dispatch", "--ref");
                    break;
                case "--inputs-file":
                    inputsFile = i + 1 < args.Length ? args[++i] : null;
                    if (inputsFile is null) return ErrMissing("workflow_dispatch", "--inputs-file");
                    break;
                default:
                    return ErrUnknownArg(args[i]);
            }
        }
        if (repo is null || workflow is null || @ref is null)
            return ErrBad("workflow_dispatch 缺少 --repo / --workflow / --ref（示例：sluice workflow dispatch --repo owner/repo --workflow ci.yml --ref master）");
        if (!System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[\w.-]+/[\w.-]+$"))
            return ErrBad($"--repo 应为 owner/name 形式，收到：{repo}");
        if (workflow.Trim().Length == 0) return ErrBad("--workflow 不能为空（workflow id 或 .yml 文件名）");
        if (@ref.Trim().Length == 0) return ErrBad("--ref 不能为空（分支或标签名）");
        if (!TokenStore.Exists)
        {
            Messages.Write(Messages.NotInitialized());
            return ExitCodes.NotInitialized;
        }
        var violations = SealCheck.Run();
        if (violations.Count > 0)
        {
            Audit.Append("workflow_dispatch", repo, $"{workflow}@{@ref}", null, "seal_violation");
            Messages.Write(Messages.SealViolation(string.Join("；", violations)));
            return ExitCodes.SealViolation;
        }

        string? inputsJson = null;
        if (inputsFile is not null)
        {
            if (!File.Exists(inputsFile)) return ErrBad($"--inputs-file 不存在：{inputsFile}");
            try { inputsJson = File.ReadAllText(inputsFile, System.Text.Encoding.UTF8); }
            catch (Exception ex) { return ErrBad($"无法读取 --inputs-file：{ex.Message}"); }
            if (System.Text.Json.Nodes.JsonNode.Parse(inputsJson) is not System.Text.Json.Nodes.JsonObject)
                return ErrBad("--inputs-file 须为 JSON 对象（键值对应 workflow 的 workflow_dispatch inputs 声明）");
        }

        var target = $"{repo} · {workflow} @ {@ref}";
        var sha = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{repo}:{workflow}:{@ref}:{inputsJson ?? ""}")))[..8].ToLowerInvariant();
        var window = $"""
            手动触发 workflow：{target}
            inputs：{(inputsJson is null ? "（无）" : (inputsJson.Length > 200 ? inputsJson[..200] + "…" : inputsJson))}
            将向该 workflow 的 dispatches 端点发出触发事件（HTTP 204 = 受理）。
            注意：需要 PAT 具备 Actions: Read and write 权限，缺失时服务端 403。
            """;

        Gate.Outcome outcome;
        using (InputFilter.Install())
        {
            outcome = Gate.Run(new Gate.Request("workflow_dispatch", repo, $"{workflow}@{@ref}", window, sha,
                TokenStore.PeekChallenge()));
        }

        switch (outcome)
        {
            case Gate.Outcome.Denied d:
                Audit.Append("workflow_dispatch", repo, $"{workflow}@{@ref}", sha, "denied", d.Preset, d.FreeText, consent: "none");
                Messages.Write(Messages.Denied("workflow_dispatch", target, d.Preset, d.FreeText));
                return ExitCodes.Denied;
            case Gate.Outcome.TimedOut:
                Audit.Append("workflow_dispatch", repo, $"{workflow}@{@ref}", sha, "timeout");
                Messages.Write(Messages.Timeout("workflow_dispatch", target, Gate.TimeoutSeconds));
                return ExitCodes.Timeout;
            case Gate.Outcome.ConsentFailed:
                Audit.Append("workflow_dispatch", repo, $"{workflow}@{@ref}", sha, "consent_failed", consent: "hello_failed");
                Messages.Write(Messages.ConsentFailed("workflow_dispatch", target));
                return ExitCodes.ConsentFailed;
            case Gate.Outcome.VaultKeyMissing vk:
                Audit.Append("workflow_dispatch", repo, $"{workflow}@{@ref}", sha, "vault_broken");
                Messages.Write(Messages.VaultBroken(vk.Detail));
                return ExitCodes.NotInitialized;
            case Gate.Outcome.Crashed c:
                Audit.Append("workflow_dispatch", repo, $"{workflow}@{@ref}", sha, "internal_gate_crash", result: c.Error);
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
            case Gate.Outcome.Approved a:
                return ExecuteWorkflowDispatch(a, repo, workflow, @ref, inputsJson, sha, target);
            default:
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
        }
    }

    private static int ExecuteWorkflowDispatch(Gate.Outcome.Approved a, string repo, string workflow, string @ref,
        string? inputsJson, string sha, string target)
    {
        string token;
        try { token = TokenStore.LoadApproved(a.Signature); }
        catch (Exception)
        {
            Audit.Append("workflow_dispatch", repo, $"{workflow}@{@ref}", sha, "internal_vault_decrypt_failed", consent: TokenStore.ConsentChannel);
            Messages.Write(Messages.Internal(afterExecutionStarted: false));
            return ExitCodes.Internal;
        }

        try
        {
            var body = new System.Text.Json.Nodes.JsonObject { ["ref"] = @ref };
            if (inputsJson is not null)
                body["inputs"] = System.Text.Json.Nodes.JsonNode.Parse(inputsJson);
            var (st, rbody) = GitHubApi.SendRawAsync(HttpMethod.Post,
                $"repos/{repo}/actions/workflows/{Uri.EscapeDataString(workflow)}/dispatches", token,
                body.ToJsonString()).GetAwaiter().GetResult();
            switch (st)
            {
                case 204:
                    Audit.Append("workflow_dispatch", repo, $"{workflow}@{@ref}", sha, "approved", consent: TokenStore.ConsentChannel, result: "executed");
                    Messages.Write(Messages.WorkflowDispatched(repo, workflow, @ref));
                    return ExitCodes.Success;
                case 401:
                    Audit.Append("workflow_dispatch", repo, $"{workflow}@{@ref}", sha, "credential_expired", consent: TokenStore.ConsentChannel, result: "http_401");
                    Messages.Write(Messages.CredentialExpired("HTTP 401"));
                    return ExitCodes.CredentialExpired;
                case 403:
                    // fine-grained PAT 缺 Actions: Read and write——用户动作可修，专属指引（勿归凭据过期死路）
                    Audit.Append("workflow_dispatch", repo, $"{workflow}@{@ref}", sha, "forbidden", consent: TokenStore.ConsentChannel,
                        result: HttpDetail(st, rbody));
                    Messages.Write(Messages.DispatchForbidden(repo, workflow, HttpDetail(st, rbody)));
                    return ExitCodes.BadRequest;
                case 404:
                    Audit.Append("workflow_dispatch", repo, $"{workflow}@{@ref}", sha, "not_found", consent: TokenStore.ConsentChannel,
                        result: "http_404");
                    Messages.Write(Messages.DispatchNotFound(repo, workflow));
                    return ExitCodes.BadRequest;
                default:
                    Audit.Append("workflow_dispatch", repo, $"{workflow}@{@ref}", sha, "workflow_dispatch_rejected",
                        consent: TokenStore.ConsentChannel, result: HttpDetail(st, rbody));
                    Messages.Write(Messages.DispatchRejected(HttpDetail(st, rbody)));
                    return ExitCodes.BadRequest;
            }
        }
        catch (HttpRequestException ex)
        {
            Audit.Append("workflow_dispatch", repo, $"{workflow}@{@ref}", sha, "http_failed", consent: TokenStore.ConsentChannel, result: "not_sent");
            Messages.Write(Messages.HttpFailed(GitHubApi.Describe(ex)));
            return ExitCodes.HttpFailed;
        }
        catch (TaskCanceledException)
        {
            // 触发非幂等（可能已受理——重发 = 重复 run）——诚实归 unknown，先查 runs 再决定
            Audit.Append("workflow_dispatch", repo, $"{workflow}@{@ref}", sha, "unknown", consent: TokenStore.ConsentChannel, result: "timeout");
            Messages.Write(Messages.Unknown("超时"));
            return ExitCodes.Unknown;
        }
    }

    private static int Created(GitHubApi.ApiResult r, string repo, int number, string sha)
    {
        Audit.Append("pr_comment", repo, $"#{number}", sha, "approved", consent: TokenStore.ConsentChannel, result: $"executed:{r.Url}");
        Messages.Write(Messages.Success("pr_comment", $"{repo}#{number}", 201, r.Url!));
        return ExitCodes.Success;
    }

    private static int Expired(GitHubApi.ApiResult r, string repo, int number, string sha)
    {
        Audit.Append("pr_comment", repo, $"#{number}", sha, "credential_expired", consent: TokenStore.ConsentChannel,
            result: $"http_{r.Status}");
        Messages.Write(Messages.CredentialExpired($"HTTP {r.Status}"));
        return ExitCodes.CredentialExpired;
    }

    private static int NotSent(GitHubApi.ApiResult r, string repo, int number, string sha)
    {
        Audit.Append("pr_comment", repo, $"#{number}", sha, "http_failed", consent: TokenStore.ConsentChannel, result: "not_sent");
        Messages.Write(Messages.HttpFailed(r.Reason ?? "连接失败"));
        return ExitCodes.HttpFailed;
    }

    private static int UnknownOutcome(GitHubApi.ApiResult r, string repo, int number, string sha)
    {
        Audit.Append("pr_comment", repo, $"#{number}", sha, "unknown", consent: TokenStore.ConsentChannel,
            result: $"unknown:{r.Reason}");
        Messages.Write(Messages.Unknown(r.Reason ?? "结果不明"));
        return ExitCodes.Unknown;
    }

    private static int InternalPost(GitHubApi.ApiResult r, string repo, int number, string sha)
    {
        Audit.Append("pr_comment", repo, $"#{number}", sha, "internal", consent: TokenStore.ConsentChannel,
            result: $"http_{r.Status}");
        Messages.Write(Messages.Internal(afterExecutionStarted: true));
        return ExitCodes.Internal;
    }
}
