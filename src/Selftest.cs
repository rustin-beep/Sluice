namespace Sluice;

/// <summary>§12 V0 验收"终态可判"：§8.2 消息表穷举 + exit code 对齐（tests/selftest 内置为单 exe 原则）。</summary>
internal static class Selftest
{
    // §8.3 exit code 表（硬编码期望，防表被改坏）
    private static readonly (string Name, int Code, string Retry)[] Expected =
    [
        ("success",      ExitCodes.Success,          "—"),
        ("pushuptodate", ExitCodes.Success,          "—"),
        ("denied",    ExitCodes.Denied,           "never"),
        ("timeout",   ExitCodes.Timeout,          "wait_user"),
        ("policy",    ExitCodes.PolicyReject,     "change_approach"),
        ("cooldown",  ExitCodes.Cooldown,         "wait_until"),
        ("consent",   ExitCodes.ConsentFailed,    "wait_user"),
        ("seal",      ExitCodes.SealViolation,    "wait_user"),
        ("notinit",   ExitCodes.NotInitialized,   "wait_user"),
        ("credexp",   ExitCodes.CredentialExpired,"wait_user"),
        ("badreq",    ExitCodes.BadRequest,       "after_fix"),
        ("httpfail",  ExitCodes.HttpFailed,       "retry_ok"),
        ("unknown",   ExitCodes.Unknown,          "wait_user"),
        ("internal1", ExitCodes.Internal,         "wait_user"),
        ("internal2", ExitCodes.Internal,         "wait_user"),
        ("apitimeout",  ExitCodes.HttpFailed,     "retry_ok"),   // api GET 超时：幂等无副作用 → 20 家族（21 是非幂等 POST 专属档）
        ("apiauth",     ExitCodes.BadRequest,     "after_fix"),  // 匿名 401/403：加 --auth 重跑
        ("apihttp",     ExitCodes.BadRequest,     "after_fix"),  // 其余非 2xx（404/429/…）
        ("vaultbroken", ExitCodes.NotInitialized, "wait_user"),  // Hello 键缺失 ≠ 用户取消
        ("prrejected",  ExitCodes.BadRequest,     "after_fix"),  // PR 422：查已有 PR，勿原样重跑
        ("prmergerejected", ExitCodes.BadRequest, "after_fix"),  // PR merge 405/409/422：查 mergeable 再重试
        ("prmergeforbidden", ExitCodes.BadRequest, "after_fix"), // v2.5 拆分：403=保护/权限，勿走重新 setup 死路
        ("prmergenotfound", ExitCodes.BadRequest, "after_fix"),  // v2.5 拆分：404=不存在，勿混"不可合并"文案
        ("prmergechanged", ExitCodes.BadRequest,  "after_fix"),  // v2.5 保真复核：head 变化，重跑重新弹窗
        ("prmergeverify", ExitCodes.HttpFailed,   "retry_ok"),   // v2.5 复核 GET 失败：merge 未发出，重试安全
        ("branchdeleteforbidden", ExitCodes.BadRequest, "after_fix"), // v2.5 拆分：403=保护/权限
        ("nonff",       ExitCodes.BadRequest,     "after_fix"),  // 2026-10-30 实战改档：rebase 后可重推同分支
        ("remotemoved", ExitCodes.BadRequest,     "after_fix"),  // v2.6 重放通道保真复核：fetch 后重跑重新弹窗
        ("hasmerge",    ExitCodes.BadRequest,     "after_fix"),  // rebase 成线性后重跑（列问题提交）
        ("prreopenrejected",  ExitCodes.BadRequest, "after_fix"), // v2.7：403/404/422 透传，含"分支已删先重建"正解
        ("issuecloserejected", ExitCodes.BadRequest, "after_fix"), // v2.7：PR 误用/权限/不存在
        ("issuecreaterejected", ExitCodes.BadRequest, "after_fix"), // v2.11：权限缺失转告 setup / 404 核对 / 422 校验
        ("prcloserejected",   ExitCodes.BadRequest, "after_fix"), // v2.11：已关/已合并用既成状态，勿重跑
        ("issuereopenrejected", ExitCodes.BadRequest, "after_fix"), // v2.11：PR 误用/权限/锁定透传
        ("issuelabelrejected", ExitCodes.BadRequest, "after_fix"), // v2.17：未知名 404/编号不存在/权限——当前标签原样保留
        ("preditrejected",    ExitCodes.BadRequest, "after_fix"), // v2.11：旧值未动，核对后重跑
        ("prreviewrejected",  ExitCodes.BadRequest, "after_fix"), // v2.11：APPROVE 自批 422 说破 → 改 RQC/COMMENT
        ("tagrejected",       ExitCodes.BadRequest, "after_fix"), // v2.13：Reference already exists → 用既有 tag 勿重试
        ("releaserejected",   ExitCodes.BadRequest, "after_fix"), // v2.13：already_exists → 查既有 release；未发通知未触发 webhook
        ("dispatched",        ExitCodes.Success,     "—"),         // v2.7：204 受理即完成，无需重发
        ("dispatchforbidden", ExitCodes.BadRequest, "after_fix"), // v2.7：PAT 缺 Actions:RW——用户动作，非 agent 可修
        ("dispatchnotfound",  ExitCodes.BadRequest, "after_fix"), // v2.7：workflow 不存在或 on: 未声明 dispatch
        ("dispatchrejected",  ExitCodes.BadRequest, "after_fix"), // v2.7：ref/inputs 校验拒
        ("apiredirected",     ExitCodes.BadRequest, "after_fix"), // v2.7：域外重定向不跟随，明说去向
        ("pushdone",        ExitCodes.Success,      "—"),          // v2.10：push 成功附"本地引用不回写"提示
        ("pushbasemissing", ExitCodes.BadRequest,   "after_fix"),  // v2.12：基点不在远端——推基点或换祖先；校验前移零上传
        ("pushtoolarge",    ExitCodes.BadRequest,   "after_fix"),  // v2.12：超限分批（规划期先拦不烧批准）
        ("pushsteprejected",ExitCodes.BadRequest,   "after_fix"),  // v2.12：Other 族渲染服务端原话，分支未动是事实
        ("pushblobmismatch",ExitCodes.HttpFailed,   "retry_ok"),   // v2.12：内容寻址平台异常，重传安全
        ("replayunresolved",ExitCodes.BadRequest,   "after_fix"),  // v2.10：--base+远端已存在+无本地引用 → fetch 后原命令重跑
        ("apirateanon",     ExitCodes.BadRequest,   "after_fix"),  // v2.10：匿名配额耗尽 ≠ 私有，加 --auth 走独立配额
        ("apiratewait",     ExitCodes.RateLimitWait, "wait_until"), // v2.10：限流等待 ≠ 凭据失效（exit 22 新档——wait_until 不寄生 20/retry_ok，函数性）
        ("writeratelimited",ExitCodes.RateLimitWait, "wait_until"), // v2.16：写通道 403/429 同档（api 读通道 v2.10 的对偶面——POST/PATCH 密集面恰是 secondary 高发处）
        ("pushratelimited", ExitCodes.RateLimitWait, "wait_until"), // v2.16：push 序列限流——分支未动、已传 blob 幂等（事实句措辞异于"未执行"族，不进该检查表）
        ("writeforbidden",  ExitCodes.BadRequest,    "after_fix"),  // v2.16：comment/pr_create 403 自 credential_expired 拆出（v2.5 漏网）
        ("commentrejected", ExitCodes.BadRequest,    "after_fix"),  // v2.16：服务端 404/422 拒绝 ≠ internal（原经 InternalPost 谎记）
        ("notice",          ExitCodes.PolicyReject,  "change_approach"), // v2.19：git 直连索凭据→指路 sluice push（不供凭据，stdout 永无 password=）
        ("mcppending",      ExitCodes.PendingGate,   "wait_until"), // v2.25：批准窗未决——非终态，receipt 收割（CLI 不产此码，MCP server 合成）
        ("statuspending",   ExitCodes.PendingGate,   "wait_until"), // v2.25：status 查询无终态行（同 23 档，函数性）
        ("statusdone",      ExitCodes.Success,       "—"),          // v2.25：status 收割已批准族
        ("statusupodate",   ExitCodes.Success,       "—"),          // v2.27：up_to_date 单列（"已获批准并执行"对它是假话）
        ("statusother",     ExitCodes.Unknown,       "wait_user"),  // v2.25：status 收割其余终态（照录决策字面）
        ("serverstale",     ExitCodes.Internal,      "wait_user"),  // v2.26：僵尸 server 自曝——重启会话后重发（与 internal 族同档：技术失败，交回用户）
    ];

    private static readonly HashSet<string> ValidRetry =
    ["never", "after_fix", "retry_ok", "wait_user", "wait_until", "change_approach", "—"];

    public static int Run()
    {
        Console.WriteLine("Sluice selftest —— §8 消息表穷举 / exit code 对齐 / §9.7 MCP 映射 / §9 面一致锁 / v2.14+v2.16 push 逻辑锁 / v2.17 标签锁 / v2.18 崩溃诊断锁 / v2.23 审阅修复锁 / v2.24 拒绝可见性锁 / v2.26 僵尸自曝锁 / v2.27 收割一致锁");
        Console.WriteLine();
        var samples = Messages.AllSamples()
            .OrderBy(s => Array.FindIndex(Expected, e => e.Name == s.Name)).ToArray();
        int fail = 0;

        foreach (var (name, sample) in samples)
        {
            Console.WriteLine($"--- {name} (exit={sample.ExitCode}, retry={sample.Retry}, stderr={sample.ToStderr}) ---");
            Console.WriteLine((sample.Prefix + " " + sample.Text).Replace("\n", Environment.NewLine));
            Console.WriteLine();

            var exp = Expected.FirstOrDefault(e => e.Name == name);
            if (exp == default) { Console.WriteLine($"!! 未知样例 {name}"); fail++; continue; }
            if (sample.ExitCode != exp.Code) { Console.WriteLine($"!! exit code 不符：{sample.ExitCode} ≠ {exp.Code}"); fail++; }
            if (sample.Retry != exp.Retry) { Console.WriteLine($"!! retry 不符：{sample.Retry} ≠ {exp.Retry}"); fail++; }
            if (!ValidRetry.Contains(sample.Retry)) { Console.WriteLine($"!! retry 非法：{sample.Retry}"); fail++; }
            if (sample.Prefix is not ("[Sluice]" or "[Sluice·终态]")) { Console.WriteLine("!! 前缀非法"); fail++; }

            // 设计原则 3：事实前置。各消息的事实句措辞以 §8.2 逐字为准：
            // 明确未执行 → "本次操作未执行"；未发出 → "无任何副作用"；用法错 → "未产生任何副作用"；
            // unknown / internal2 语义是"结果未知/可能已完成"，不得写"未执行"
            if (name == "success" && !sample.Text.Contains("无需再用其他命令验证"))
            { fail++; Console.WriteLine("!! 成功消息缺\"无需验证\"句"); }
            if (name == "unknown" && !sample.Text.Contains("结果未知"))
            { fail++; Console.WriteLine("!! unknown 缺\"结果未知\""); }
            if (name is "denied" or "timeout" or "policy" or "cooldown" or "consent" or "seal" or "internal1"
                    or "prmergerejected" or "prmergeforbidden" or "prmergenotfound" or "branchdeleteforbidden"
                    or "prreopenrejected" or "issuecloserejected" or "dispatchforbidden" or "dispatchnotfound" or "dispatchrejected"
                    or "issuecreaterejected" or "prcloserejected" or "issuereopenrejected" or "preditrejected" or "prreviewrejected"
                    or "issuelabelrejected"
                    or "tagrejected" or "releaserejected" or "writeforbidden" or "commentrejected" or "writeratelimited"
                && !sample.Text.Contains("本次操作未执行"))
            { fail++; Console.WriteLine("!! 缺事实前置句\"本次操作未执行\""); }
            if (name == "internal2" && !sample.Text.Contains("可能已完成"))
            { fail++; Console.WriteLine("!! internal(后) 缺\"可能已完成\""); }
            if (name is "badreq" or "nonff" or "remotemoved" or "hasmerge" or "prmergechanged" or "prmergeverify"
                    or "apiredirected" or "pushbasemissing" or "pushtoolarge" or "pushsteprejected"
                && !sample.Text.Contains("本次未产生任何副作用"))
            { fail++; Console.WriteLine("!! bad_request 家族缺\"未产生副作用\"句"); }
            if (name is "httpfail" or "pushblobmismatch" && !sample.Text.Contains("无任何副作用"))
            { fail++; Console.WriteLine("!! http_failed 缺\"无任何副作用\"句"); }

            // 设计原则 4：拒绝类消息必含绕行禁止语义（措辞按 §8.2 原文：cooldown 用"绕过"，其余用"重试"）
            if (name is "denied" or "timeout" or "policy" && !sample.Text.Contains("重试"))
            { fail++; Console.WriteLine("!! 拒绝类消息缺重试禁止语义"); }
            if (name == "cooldown" && !sample.Text.Contains("绕过"))
            { fail++; Console.WriteLine("!! cooldown 缺绕过禁止语义"); }
        }

        var samplesSet = samples.Select(s => s.Name).ToHashSet();
        foreach (var missing in Expected.Where(e => !samplesSet.Contains(e.Name)))
        { Console.WriteLine($"!! 样例缺失：{missing.Name}"); fail++; }

        fail += McpChecks();
        fail += SurfaceLock();
        fail += PushLogicChecks();
        fail += LabelChecks();
        fail += CrashDetailChecks();
        fail += ReviewFixChecks();
        fail += DenyVisibilityChecks();

        Console.WriteLine(fail == 0
            ? "PASS：全部消息可单读终判，exit code 与 §8.3 对齐。"
            : $"FAIL：{fail} 处不符。");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// §9.7 MCP 前门自检：(1) exit→retry 映射函数性——AllSamples 按 exit 分组 retry 必单值且与
    /// RetryOf 一致（"同 exit 不同 retry"的新消息在此 FAIL，逼作者开新 exit 档或统一措辞，杜绝双表漂移）；
    /// (2) BuildArguments 抽查（纯函数，零文件系统）。
    /// </summary>
    private static int McpChecks()
    {
        Console.WriteLine("--- mcp 映射（§9.7）---");
        int fail = 0;

        // (1) 函数性 + 一致性
        foreach (var g in Messages.AllSamples().GroupBy(s => s.Sample.ExitCode).OrderBy(g => g.Key))
        {
            var retries = g.Select(x => x.Sample.Retry).Distinct().ToArray();
            if (retries.Length != 1)
            { fail++; Console.WriteLine($"!! exit {g.Key} 的 retry 不单值：{string.Join(" / ", retries)}——MCP 映射需要函数性：新消息请开新 exit 档或统一 retry 措辞"); }
            else if (McpServer.RetryOf(g.Key) != retries[0])
            { fail++; Console.WriteLine($"!! McpServer.RetryOf({g.Key}) = {McpServer.RetryOf(g.Key)} ≠ 消息表 {retries[0]}"); }
        }
        Console.WriteLine("exit→retry 函数性："
            + string.Join(", ", Messages.AllSamples().GroupBy(s => s.Sample.ExitCode).OrderBy(g => g.Key)
                .Select(g => $"{g.Key}→{McpServer.RetryOf(g.Key)}")));

        // (2) BuildArguments 抽查
        (string[] Argv, string? Body, string? Inputs, Msg? Err) Build(string json)
        {
            using var d = System.Text.Json.JsonDocument.Parse(json);
            var ok = McpServer.BuildArguments(d.RootElement, "BODY", "INPUTS", out var argv, out var body, out var inputs, out var err);
            return (argv, body, inputs, ok ? null : err);
        }
        void Eq(string what, object? actual, object? expected)
        {
            var a = actual is string[] arr ? string.Join(" ", arr) : actual;
            var e = expected is string[] exp ? string.Join(" ", exp) : expected;
            if (!Equals(a, e)) { fail++; Console.WriteLine($"!! {what}：{a} ≠ 期望 {e}"); }
        }

        var c1 = Build("""{"action":"pr_comment","repo":"o/r","number":5,"body":"中文正文"}""");
        Eq("pr_comment argv", c1.Argv, new[] { "comment", "--repo", "o/r", "--number", "5", "--body-file", "BODY" });
        Eq("pr_comment body", c1.Body, "中文正文"); Eq("pr_comment err", c1.Err, null);

        var c2 = Build("""{"action":"push","repo":"o/r","branch":"feat/x","base":"origin/master","repo_path":"D:/x"}""");
        Eq("push argv", c2.Argv, new[] { "push", "--repo", "o/r", "--branch", "feat/x", "--repo-path", "D:/x", "--base", "origin/master" });

        // 必填缺省不在 MCP 层拦——省略 flag 交子进程 verb-aware ErrMissing（单源，文案带示例）
        var c3 = Build("""{"action":"pr_comment"}""");
        Eq("缺省透传 argv", c3.Argv, new[] { "comment" }); Eq("缺省透传 err", c3.Err, null);

        var c4 = Build("""{"action":"pr_merge","repo":"o/r","number":"383","method":"squash","delete_head":true}""");
        Eq("number 字符串宽容 + delete_head", c4.Argv,
           new[] { "pr", "merge", "--repo", "o/r", "--number", "383", "--method", "squash", "--delete-head" });

        var c5 = Build("""{"action":"workflow_dispatch","repo":"o/r","workflow":"ci.yml","ref":"master","inputs":{"k":"v"}}""");
        Eq("dispatch argv", c5.Argv, new[] { "workflow", "dispatch", "--repo", "o/r", "--workflow", "ci.yml", "--ref", "master", "--inputs-file", "INPUTS" });
        Eq("dispatch inputs 原样", c5.Inputs, """{"k":"v"}""");

        var c6 = Build("""{"action":"push","repo":"o/r","branch":"x","body":"多余"}""");
        if (c6.Err is null || c6.Err.ExitCode != ExitCodes.BadRequest)
        { fail++; Console.WriteLine("!! push 带 body 应报未知字段（防静默丢参），exit 10"); }

        var c7 = Build("""{"action":"deploy","repo":"o/r"}""");
        if (c7.Err is null || c7.Err.ExitCode != ExitCodes.BadRequest)
        { fail++; Console.WriteLine("!! 未知 action 应报 exit 10"); }

        var c8 = Build("""{"action":"branch_delete","repo":123,"branch":"x"}""");
        if (c8.Err is null || c8.Err.ExitCode != ExitCodes.BadRequest)
        { fail++; Console.WriteLine("!! repo 类型错（数字）应报 exit 10 而非当缺省"); }

        // v2.11 新 action 抽查
        var c9 = Build("""{"action":"issue_create","repo":"o/r","title":"新缺口","body":"正文"}""");
        Eq("issue_create argv", c9.Argv, new[] { "issue", "create", "--repo", "o/r", "--title", "新缺口", "--body-file", "BODY" });
        Eq("issue_create body", c9.Body, "正文"); Eq("issue_create err", c9.Err, null);

        var c10 = Build("""{"action":"pr_edit","repo":"o/r","number":"383","title":"勘误"}""");
        Eq("pr_edit argv", c10.Argv, new[] { "pr", "edit", "--repo", "o/r", "--number", "383", "--title", "勘误" });

        var c11 = Build("""{"action":"pr_review","repo":"o/r","number":7,"event":"APPROVE"}""");
        Eq("pr_review argv", c11.Argv, new[] { "review", "--repo", "o/r", "--number", "7", "--event", "APPROVE" });

        var c12 = Build("""{"action":"tag_create","repo":"o/r","tag":"v1.2.3","at":"master"}""");
        Eq("tag_create argv", c12.Argv, new[] { "tag", "create", "--repo", "o/r", "--tag", "v1.2.3", "--at", "master" });

        var c13 = Build("""{"action":"release_create","repo":"o/r","tag":"v1.2.3","body":"注记","prerelease":true,"target":"master"}""");
        Eq("release_create argv", c13.Argv, new[] { "release", "create", "--repo", "o/r", "--tag", "v1.2.3", "--target", "master", "--prerelease", "--body-file", "BODY" });
        Eq("release_create body", c13.Body, "注记");

        // v2.16：prerelease 布尔白名单（原 `||` 条件使字符串值必拒、字符串 true 接受分支不可达）
        var c14 = Build("""{"action":"release_create","repo":"o/r","tag":"v1","prerelease":"true"}""");
        Eq("prerelease 字符串 true→flag", c14.Argv, new[] { "release", "create", "--repo", "o/r", "--tag", "v1", "--prerelease" });
        var c15 = Build("""{"action":"release_create","repo":"o/r","tag":"v1","prerelease":"yes"}""");
        if (c15.Err is null || c15.Err.ExitCode != ExitCodes.BadRequest)
        { fail++; Console.WriteLine("!! prerelease 非法字符串应报 exit 10"); }
        var c16 = Build("""{"action":"release_create","repo":"o/r","tag":"v1","prerelease":{}}""");
        if (c16.Err is null || c16.Err.ExitCode != ExitCodes.BadRequest)
        { fail++; Console.WriteLine("!! prerelease 对象类型应显式拒（防静默丢参）而非漏放"); }

        // v2.26：过期自检纯函数——rename-swap 部署后，磁盘映像 (大小, 修改时间) 必然变化；
        // 文件消失（-1 哨兵）同判过期（fail-closed）；完全相同才放行
        Eq("映像未变放行", McpServer.ImageChanged((23640576, new DateTime(2026, 11, 4, 10, 28, 5, DateTimeKind.Utc)),
                                              (23640576, new DateTime(2026, 11, 4, 10, 28, 5, DateTimeKind.Utc))), false);
        Eq("大小变即过期", McpServer.ImageChanged((23640576, new DateTime(2026, 11, 4, 10, 28, 5, DateTimeKind.Utc)),
                                                 (23712768, new DateTime(2026, 11, 4, 10, 28, 5, DateTimeKind.Utc))), true);
        Eq("修改时间变即过期", McpServer.ImageChanged((23640576, new DateTime(2026, 11, 4, 10, 28, 5, DateTimeKind.Utc)),
                                                      (23640576, new DateTime(2026, 11, 4, 11, 0, 0, DateTimeKind.Utc))), true);
        Eq("文件消失即过期", McpServer.ImageChanged((23640576, new DateTime(2026, 11, 4, 10, 28, 5, DateTimeKind.Utc)),
                                                   (-1, DateTime.MinValue)), true);

        // v2.25：status 终态映射锁——pending 收割后 exit/retry 必须与原决策同族
        //（denied→2/never、timeout→3/wait_user、consent_failed→6/wait_user、rate_limited→22/wait_until、
        // approved/up_to_date→0/—、未映射→21/wait_user；v2.27 扩表：原四族映射把 consent_failed/
        // rate_limited 收割成 21/isError=true"勿重发"，与 live 指引相反——cooldown 无生产审计行
        // 且未接线，不映射，接线时同批进）
        foreach (var (decision, want) in new[]
                 {
                     ("denied", ExitCodes.Denied), ("timeout", ExitCodes.Timeout),
                     ("approved", ExitCodes.Success), ("up_to_date", ExitCodes.Success),
                     ("consent_failed", ExitCodes.ConsentFailed), ("rate_limited", ExitCodes.RateLimitWait),
                     ("not_found", ExitCodes.Unknown), ("http_405", ExitCodes.Unknown),
                 })
            if (Program.StatusFamily(decision) != want)
            { fail++; Console.WriteLine($"!! StatusFamily({decision}) = {Program.StatusFamily(decision)} ≠ {want}"); }
        if (McpServer.RetryOf(Program.StatusFamily("denied")) != "never"
            || McpServer.RetryOf(Program.StatusFamily("timeout")) != "wait_user"
            || McpServer.RetryOf(Program.StatusFamily("approved")) != "—")
        { fail++; Console.WriteLine("!! status 收割的 exit→retry 必须与原决策族一致"); }

        // v2.27：收割↔live 反向锁——同一 decision 经 status 收割（>15s 未决路径）与 live 直返
        //（≤15s 快路径）的 exit/retry 必须同族：分叉=非确定性契约（落哪边取决于人是否 15s 内点完）。
        // 缘起：v2.25 收割面只映射四族，consent_failed/rate_limited 落 21/isError=true"勿重发"，
        // 与 live 的 wait_user/wait_until"可重试"指引相反——v2.24 立的"用户决策族不得标错误"
        // 纪律没照到收割面，本锁把两面焊死（live 样例即基准，两表再漂移当场红）
        foreach (var (decision, liveNames) in new[]
                 {
                     ("denied", new[] { "denied" }),
                     ("timeout", new[] { "timeout" }),
                     ("consent_failed", new[] { "consent" }),
                     ("rate_limited", new[] { "writeratelimited", "pushratelimited" }),
                     ("approved", new[] { "success", "pushdone" }),
                     ("up_to_date", new[] { "pushuptodate" }),
                 })
            foreach (var live in Messages.AllSamples().Where(s => liveNames.Contains(s.Name))
                .Select(s => s.Sample))
            {
                if (Program.StatusFamily(decision) != live.ExitCode)
                { fail++; Console.WriteLine($"!! 收割 exit 分叉：StatusFamily({decision})={Program.StatusFamily(decision)} ≠ live {live.ExitCode}"); }
                if (McpServer.RetryOf(Program.StatusFamily(decision)) != live.Retry)
                { fail++; Console.WriteLine($"!! 收割 retry 分叉：{decision} 收割 {McpServer.RetryOf(Program.StatusFamily(decision))} ≠ live {live.Retry}"); }
            }

        // v2.27：收割渲染解析锁——rate_limited 审计 result 的 "http_{st}:{what}" / "not_executed:{what}"
        //（push 形态）两种前缀都要取回结论；≤v0.7.2 旧行 "http_403"（无结论）回退通用等待句
        Eq("收割 what 解析", Program.RateLimitWhatOf("http_403:secondary rate limit，服务端要求等待约 2 分钟"),
           "secondary rate limit，服务端要求等待约 2 分钟");
        Eq("收割 what push 形态", Program.RateLimitWhatOf("not_executed:API 配额（rate limit）耗尽，等待当前配额窗重置（约 1 小时内）"),
           "API 配额（rate limit）耗尽，等待当前配额窗重置（约 1 小时内）");
        Eq("收割 what 旧行回退非空", Program.RateLimitWhatOf("http_403") is { Length: > 0 }, true);
        Eq("收割 st 解析", Program.HttpStOf("http_429:服务端限流（HTTP 429）——通常 1 分钟内解封，稍候重试"), 429);
        Eq("收割 st 旧行解析", Program.HttpStOf("http_403"), 403);
        Eq("收割 st 非 http 前缀回 0", Program.HttpStOf("not_executed:x"), 0);

        Console.WriteLine(fail == 0 ? "MCP 映射与参数构造抽查通过。" : "MCP 检查有不符项（见上）。");
        return fail;
    }

    // ---------- push 逻辑锁（v2.14） ----------

    /// <summary>卡慢/平行链事故处置批的 pure 决策入锁（v2.14 三项；v2.16 加限流嗅探/消歧适用面）：
    /// 闸门⑦ 消歧 / blob 去重分区 / CI 聋哑判定 / RateLimitWhat 嗅探 / NeedsDisambig 适用面。
    /// 与 git、网络解耦——决策表本身就是不变量（同名两侧异 sha 必拒绝；null 集必全传；聋哑只在单向缺失时响；
    /// 非限流 403 必 null；429 无论 body 必归档）。</summary>
    private static int PushLogicChecks()
    {
        Console.WriteLine("--- push 逻辑锁（v2.14/v2.16：基点消歧 / blob 去重 / CI 聋哑 / 限流嗅探 / 消歧适用面）---");
        int fail = 0;
        void Eq<T>(string what, T actual, T expected)
        {
            if (!Equals(actual, expected)) { fail++; Console.WriteLine($"!! {what}：{actual} ≠ 期望 {expected}"); }
        }

        // (1) 闸门⑦：同名两侧并存且异 sha 必 Ambiguous（fail-closed）；两侧皆无走 tag 兜底
        Eq("两侧皆无→兜底(tag)", RestPush.ResolveBaseName(false, false, sameSha: false), RestPush.BaseResolution.Fallback);
        Eq("仅本地→采用", RestPush.ResolveBaseName(true, false, false), RestPush.BaseResolution.LocalOnly);
        Eq("仅远端→采用", RestPush.ResolveBaseName(false, true, false), RestPush.BaseResolution.RemoteOnly);
        Eq("两侧同sha→采用", RestPush.ResolveBaseName(true, true, true), RestPush.BaseResolution.BothSame);
        Eq("两侧异sha→拒绝", RestPush.ResolveBaseName(true, true, false), RestPush.BaseResolution.Ambiguous);

        // (2) 去重分区：null 集（去重不可用/truncated）必全传；命中集只减不改
        var blobs = new HashSet<string> { "aaa", "bbb", "ccc" };
        var (up1, sk1) = RestPush.PartitionBlobs(blobs, null);
        Eq("无集全传", (up1.Count, sk1), (3, 0));
        var (up2, sk2) = RestPush.PartitionBlobs(blobs, new HashSet<string> { "bbb", "ccc", "zzz" });
        Eq("命中跳过数", (up2.Count, sk2), (1, 2));
        Eq("保留者", string.Join(",", up2), "aaa");

        // (3) CI 聋哑：头树缺 workflows 且主干树有 → 警示；其余（含"仓本无 CI"）静默
        Eq("头缺主干有→警示", RestPush.CiDeafNeeded(false, true), true);
        Eq("两侧都有", RestPush.CiDeafNeeded(true, true), false);
        Eq("两侧都无(仓本无CI)", RestPush.CiDeafNeeded(false, false), false);
        Eq("头有主干无", RestPush.CiDeafNeeded(true, false), false);

        // (4) 限流嗅探（v2.16）：非限流 403 必 null（权限语义交各 op 专属文案）；429 无论 body 必归档；
        //     兼容 raw JSON body 与 ApiResult.Reason（"HTTP {st}：{message}" 纯文本）两种入参形态
        Eq("403 primary→非空", GitHubApi.RateLimitWhat(403, """{"message":"API rate limit exceeded for 1.2.3.4"}""") is not null, true);
        Eq("403 secondary 带分钟数", GitHubApi.RateLimitWhat(403, """{"message":"You have exceeded a secondary rate limit; wait 3 minutes and try again"}"""),
           "secondary rate limit，服务端要求等待约 3 分钟");
        Eq("403 abuse→非空", GitHubApi.RateLimitWhat(403, """{"message":"You have triggered an abuse detection mechanism"}""") is not null, true);
        Eq("403 权限拒→null", GitHubApi.RateLimitWhat(403, """{"message":"Resource not accessible by personal access token"}"""), null);
        Eq("404→null", GitHubApi.RateLimitWhat(404, "anything") is null, true);
        Eq("500→null", GitHubApi.RateLimitWhat(500, "anything") is null, true);
        Eq("429 空 body→归档", GitHubApi.RateLimitWhat(429, "") is not null, true);
        Eq("Reason 纯文本可嗅探", GitHubApi.RateLimitWhat(403, "HTTP 403：You have exceeded a secondary rate limit and have been temporarily blocked") is not null, true);

        // (5) 消歧适用面（v2.16）：v2.14 只盖裸名——斜杠名 feature/x 同吃 DWIM refs/heads/ 优先坑
        Eq("裸名入消歧", RestPush.NeedsDisambig("master"), true);
        Eq("斜杠名入消歧", RestPush.NeedsDisambig("feature/x"), true);
        Eq("origin/ 前缀豁免", RestPush.NeedsDisambig("origin/master"), false);
        Eq("refs/ 前缀豁免", RestPush.NeedsDisambig("refs/tags/v1.2.3"), false);
        Eq("40 位 sha 豁免", RestPush.NeedsDisambig(new string('a', 40)), false);
        Eq("64 位 sha 豁免", RestPush.NeedsDisambig(new string('b', 64)), false);

        Console.WriteLine(fail == 0 ? "push 逻辑锁通过。" : "push 逻辑锁有不符项（见上）。");
        return fail;
    }

    /// <summary>v2.17 标签面批的 pure 决策入锁：--labels 解析（trim / 空段拒 / 重复拒——
    /// OrdinalIgnoreCase，GitHub 标签唯一性大小写不敏感）与整组替换摘除清单（当前减新）。
    /// 与网络解耦——决策表本身即不变量。</summary>
    private static int LabelChecks()
    {
        Console.WriteLine("--- 标签锁（v2.17：--labels 解析 / 摘除清单）---");
        int fail = 0;
        void Eq<T>(string what, T actual, T expected)
        {
            if (!Equals(actual, expected)) { fail++; Console.WriteLine($"!! {what}：{actual} ≠ 期望 {expected}"); }
        }

        // (1) 解析：trim 合法；空串/空段/重复（含大小写变体）必拒
        Eq("解析 trim+分段", string.Join("|", Program.LabelsParse(" a , b ,c ")!), "a|b|c");
        Eq("解析 单名", string.Join("|", Program.LabelsParse("bug")!), "bug");
        Eq("空串拒（清空全部本批不做）", Program.LabelsParse("   "), null);
        Eq("空段拒", Program.LabelsParse("a,,b"), null);
        Eq("重复拒", Program.LabelsParse("a,b,a"), null);
        Eq("大小写重复拒（GitHub 唯一性不敏感）", Program.LabelsParse("Bug,bug"), null);
        Eq("仅空白段拒", Program.LabelsParse(" , "), null);

        // (2) 摘除清单：整组替换语义下"当前有而新清单没有"的必须被算出（弹窗披露的数据源）
        Eq("摘除=当前减新", string.Join("|", Program.LabelRemovals(["a", "b"], ["b", "c"])), "a");
        Eq("无摘除", Program.LabelRemovals(["a"], ["a", "b"]).Length, 0);
        Eq("摘除大小写不敏感", string.Join("|", Program.LabelRemovals(["Bug", "x"], ["bug"])), "x");
        Eq("当前空无摘除", Program.LabelRemovals([], ["bug"]).Length, 0);

        Console.WriteLine(fail == 0 ? "标签锁通过。" : "标签锁有不符项（见上）。");
        return fail;
    }

    /// <summary>v2.18 崩溃诊断保真锁：DescribeCrash 必须保住嵌套异常链的每一层类型名
    /// （TypeInitializationException 外层 Message 是通用占位句，类型名只在内层），
    /// 且有截断上限（审计行不失控）。缘起：10-30×2 / 11-01×4 六次 internal_gate_crash
    /// 全部只留一句话、无法归因——诊断信息本身成为不变量。</summary>
    private static int CrashDetailChecks()
    {
        Console.WriteLine("--- 崩溃诊断锁（v2.18：DescribeCrash 异常链保真 / 截断上限）---");
        int fail = 0;
        void Ok(bool cond, string what)
        {
            if (!cond) { fail++; Console.WriteLine($"!! {what}"); }
        }

        var deepest = new DllNotFoundException("libEGL");
        var inner = new InvalidOperationException("静态构造失败", deepest);
        var wrapped = new TypeInitializationException("Sluice.SomeType", inner);
        var s = Gate.DescribeCrash(wrapped);
        Ok(s.Contains("TypeInitializationException"), "丢外层类型名");
        Ok(s.Contains("InvalidOperationException"), "丢内层类型名（链断）");
        Ok(s.Contains("DllNotFoundException"), "丢最内层类型名（链断）");
        Ok(s.Contains(" <- "), "缺链分隔符");
        Ok(s.Contains("libEGL"), "丢最内层消息");

        Ok(Gate.DescribeCrash(new Exception(new string('x', 5000))).Length <= 602, "超长未截断（审计行失控）");

        Console.WriteLine(fail == 0 ? "崩溃诊断锁通过。" : "崩溃诊断锁有不符项（见上）。");
        return fail;
    }

    /// <summary>v2.23 审阅修复锁（d0f817c–8c3e770 五提交评审处置批）：哨兵 /TR 引号形态
    /// （P1——含空格路径静默失效的回归防线，实弹定案后锁形态）；notice 在位判定（P2——前缀精确，
    /// 旧路径残留不再误判绿）；暂停截止显示（P3——跨午夜必须带日期）；AGENTS.md 标记块替换
    /// （P3——END 限定 BEGIN 之后，前文引用标记字符串不得错切）；state/审计字符串形态
    /// （P2——哨兵伪造核对与 HasAuditRecord 靠三方逐字一致才对得上）。</summary>
    private static int ReviewFixChecks()
    {
        Console.WriteLine("--- 审阅修复锁（v2.23：/TR 引号 / notice 判定 / 暂停显示 / 标记块替换 / state 字符串形态）---");
        int fail = 0;
        void Ok(bool cond, string what)
        {
            if (!cond) { fail++; Console.WriteLine($"!! {what}"); }
        }
        void Eq<T>(string what, T actual, T expected)
        {
            if (!Equals(actual, expected)) { fail++; Console.WriteLine($"!! {what}：{actual} ≠ 期望 {expected}"); }
        }

        // (1) 哨兵 /TR：-File 路径恒带内层引号，schtasks 参数串里转义为 \"（v2.20 缺的正是这层——
        //     含空格路径下 powershell 把 "C:\Users\John" 当脚本名，任务每 5 分钟静默失败且无日志）
        Eq("SentinelTr 含空格路径", SetupCommand.SentinelTr("C:/Users/John Doe/AppData/Local/Sluice/sentinel.ps1"),
           "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"C:/Users/John Doe/AppData/Local/Sluice/sentinel.ps1\"");
        Eq("schtasks 参数串内层转义", SetupCommand.SchtasksCreateArgs("powershell.exe -File \"C:/a b/s.ps1\""),
           "/Create /F /SC MINUTE /MO 5 /TN \"Sluice Sentinel\" /TR \"powershell.exe -File \\\"C:/a b/s.ps1\\\"\"");

        // (2) notice 在位判定：前缀精确匹配——旧 exe 路径残留 / 无关含 notice 子串的条目都不得判绿
        Ok(SetupCommand.NoticeValuePresent("C:/pub/sluice.exe notice", "C:/pub/sluice.exe"), "在位应判绿");
        Ok(SetupCommand.NoticeValuePresent("\"C:/pub/sluice.exe notice\"", "C:/pub/sluice.exe"), "带引号存储形态应判绿");
        Ok(!SetupCommand.NoticeValuePresent("C:/old/deleted-path/sluice.exe notice", "C:/pub/sluice.exe"), "旧路径残留不得判绿");
        Ok(!SetupCommand.NoticeValuePresent("!gh auth git-credential", "C:/pub/sluice.exe"), "gh 桥接条目不得判绿");
        Ok(!SetupCommand.NoticeValuePresent("C:/x/my-notice-thing notice", "C:/pub/sluice.exe"), "含 notice 子串的无关条目不得判绿");

        // (3) 暂停截止显示：同日 HH:mm；跨午夜必须带日期（窗口 ≤8h，只写 HH:mm 会把"明天 03:00"读成"今天 03:00"）
        var noonish = DateTime.Now.Date.AddHours(12);
        Eq("同日只写时刻", SetupCommand.UntilLabel(noonish.ToUniversalTime()),
           noonish.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        var tomorrow = DateTime.Now.Date.AddDays(1).AddHours(9);
        Eq("跨午夜带日期", SetupCommand.UntilLabel(tomorrow.ToUniversalTime()),
           tomorrow.ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture));

        // (4) 标记块替换：END 只认 BEGIN 之后的——前文引用 END 字符串不得错切；损坏形态返回 null
        var doc = "前文引用了 <!-- sluice-rules END --> 字样。\n<!-- sluice-rules BEGIN\n旧正文\n<!-- sluice-rules END -->\n尾巴";
        Eq("前文有 END 引用时正确刷新", SetupCommand.ReplaceSnippetBlock(doc, "NEW\n"),
           "前文引用了 <!-- sluice-rules END --> 字样。\nNEW\n尾巴");
        Ok(SetupCommand.ReplaceSnippetBlock("<!-- sluice-rules BEGIN\n无尾", "NEW\n") is null, "有 BEGIN 无 END 应判损坏（null）");
        Ok(SetupCommand.ReplaceSnippetBlock("只有 <!-- sluice-rules END --> 没有 BEGIN", "NEW\n") is null, "无 BEGIN 走追加分支（null）");

        // (5) state/审计字符串形态：Begin 写入值、seal_pause 审计 result、哨兵 grep needle 三方一致的前提
        Eq("ToStateString 形态", SealCheck.SealPause.ToStateString(new DateTime(2026, 11, 3, 15, 0, 0, DateTimeKind.Utc)),
           "2026-11-03T15:00:00Z");

        Console.WriteLine(fail == 0 ? "审阅修复锁通过。" : "审阅修复锁有不符项（见上）。");
        return fail;
    }

    // ---------- 面一致锁（v2.11） ----------

    /// <summary>§9 op 白名单内置快照（与 plan-lite §9 首列一致；改 §9 必须同步本表——selftest 会红）。</summary>
    private static readonly string[] ExpectedOps =
    [
        "pr_comment", "pr_create", "pr_review", "push", "api", "pr_merge", "branch_delete",
        "pr_reopen", "issue_close", "workflow_dispatch",
        "issue_create", "pr_close", "issue_reopen", "pr_edit", "tag_create", "release_create",
        "issue_label",
    ];

    /// <summary>§9 内的只读 op（不进 CLI 写动词表与 MCP Actions；新增读 op 须在此登记）。</summary>
    private static readonly string[] ReadOps = ["api"];

    /// <summary>
    /// 面一致锁：plan-lite §9 op 列 ↔ CLI 写动词表（Program.WriteVerbs）↔ MCP Actions（含 AllowedFields）
    /// 三方对账。缘起：§9 自 v2.0 承诺 pr_review 而实现全无——漂移靠人眼评审才抓到，本锁让"表格说谎"变
    /// selftest FAIL。plan-lite.md 在运行目录向上 ≤4 级内 → 按文档实文核对；找不到（发布件单飞）→
    /// 按内置快照核对代码内部一致性。
    /// </summary>
    private static int SurfaceLock()
    {
        Console.WriteLine("--- 面一致锁（v2.11：§9 ↔ CLI 写动词表 ↔ MCP actions）---");
        int fail = 0;

        var ops = new HashSet<string>(ExpectedOps);
        var src = "内置 §9 快照（未找到 plan-lite.md，发布件场景）";
        for (var i = 0; i <= 4; i++)
        {
            var p = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                string.Concat(Enumerable.Repeat(".." + Path.DirectorySeparatorChar, i)), "plan-lite.md"));
            if (!File.Exists(p)) continue;
            var docOps = ParseSection9Ops(File.ReadAllLines(p));
            if (docOps.Count > 0) { ops = docOps; src = p; }
            else { fail++; Console.WriteLine($"!! 找到 {p} 但 §9 op 列解析为空——文档结构异常"); }
            break; // 找到文件就用它（无论解析结果——空=异常，上面已红）
        }
        Console.WriteLine($"来源：{src}");
        foreach (var ro in ReadOps) ops.Remove(ro);

        var cli = Program.WriteVerbs.Select(v => v.Op).ToHashSet();
        var mcp = McpServer.Actions.ToHashSet();
        var fields = McpServer.AllowedFields.Keys.ToHashSet();

        foreach (var miss in ops.Except(cli).OrderBy(x => x))
        { fail++; Console.WriteLine($"!! §9 承诺而 CLI 写动词表没有：{miss}——文档与实现漂移（v2.11 锁定的缺陷类）"); }
        foreach (var extra in cli.Except(ops).OrderBy(x => x))
        { fail++; Console.WriteLine($"!! CLI 写动词表有而 §9 没有：{extra}——补 §9 行或撤动词"); }
        foreach (var miss in ops.Except(mcp).OrderBy(x => x))
        { fail++; Console.WriteLine($"!! §9 承诺而 MCP action 没有：{miss}"); }
        foreach (var extra in mcp.Except(ops).OrderBy(x => x))
        { fail++; Console.WriteLine($"!! MCP action 有而 §9 没有：{extra}"); }
        foreach (var miss in mcp.Except(fields).OrderBy(x => x))
        { fail++; Console.WriteLine($"!! AllowedFields 缺 action：{miss}"); }
        foreach (var extra in fields.Except(mcp).OrderBy(x => x))
        { fail++; Console.WriteLine($"!! AllowedFields 多出 action：{extra}"); }

        Console.WriteLine(fail == 0
            ? $"面一致：§9 {ops.Count} 个写 op = CLI 写动词表 = MCP actions = AllowedFields。"
            : "面不一致（见上）。");
        return fail;
    }

    /// <summary>解析 plan-lite §9 表首列的 op 名（每行 "| `op` …" 的首个反引号段）。</summary>
    private static HashSet<string> ParseSection9Ops(string[] lines)
    {
        var ops = new HashSet<string>();
        var inSec = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (inSec) break;
                inSec = line.StartsWith("## 9. op", StringComparison.Ordinal);
                continue;
            }
            if (!inSec || !line.StartsWith("| `", StringComparison.Ordinal)) continue;
            var s = line.IndexOf('`');
            var e = line.IndexOf('`', s + 1);
            if (e > s + 1) ops.Add(line[(s + 1)..e]);
        }
        return ops;
    }

    /// <summary>
    /// v2.24 拒绝可见性锁（2026-11-03 Vitro #452 实弹：pr_merge 拒绝两次、理由两次沉没）：
    /// (1) isError 两族穷举——用户决策族不得标工具错误（宿主错误渲染吞指引），技术失败族必须标；
    /// (2) deny_reason 定界往返——Messages.Denied 的自由文本必须能被 ExtractDenyReason 完整取回
    ///     （文案漂移即红，不会静默失效）；(3) 预读归因——403 不得再归因"私有库"（匿名配额限流
    ///     才是高频真因），404 匿名两可 / 带凭据收窄的措辞面锁死。
    /// </summary>
    private static int DenyVisibilityChecks()
    {
        Console.WriteLine("--- v2.24 拒绝可见性锁（isError 两族 / deny_reason 往返 / 预读归因）---");
        int fail = 0;

        foreach (var code in new[]
                 {
                     ExitCodes.Success, ExitCodes.Denied, ExitCodes.Timeout, ExitCodes.PolicyReject,
                     ExitCodes.Cooldown, ExitCodes.ConsentFailed, ExitCodes.RateLimitWait, ExitCodes.PendingGate,
                 })
            if (McpServer.IsErrorOf(code))
            { fail++; Console.WriteLine($"!! exit {code} 属用户决策族，不应标 isError（宿主会按工具错误渲染，吞掉拒绝理由）"); }
        foreach (var code in new[]
                 {
                     ExitCodes.Internal, ExitCodes.SealViolation, ExitCodes.NotInitialized,
                     ExitCodes.CredentialExpired, ExitCodes.BadRequest, ExitCodes.HttpFailed, ExitCodes.Unknown,
                 })
            if (!McpServer.IsErrorOf(code))
            { fail++; Console.WriteLine($"!! exit {code} 属技术失败族，应标 isError"); }

        var reason = "452 不是没跑完吗？453 跑完了吧？";
        var denied = Messages.Denied("pr_merge", "rustin-beep/Vitro#452", "不批准", reason);
        var extracted = McpServer.ExtractDenyReason(denied.Prefix + " " + denied.Text);
        if (extracted != reason)
        { fail++; Console.WriteLine($"!! deny_reason 往返不一致：提取到 \"{extracted}\" ≠ \"{reason}\"（定界符漂移？）"); }

        var noReason = Messages.Denied("pr_merge", "rustin-beep/Vitro#452", "不批准", null);
        if (McpServer.ExtractDenyReason(noReason.Prefix + " " + noReason.Text) is not null)
        { fail++; Console.WriteLine("!! 无自由文本的拒绝消息不应提取出 deny_reason"); }

        if (Program.MetaUnreadableWhy(403, authed: false).Contains("私有"))
        { fail++; Console.WriteLine("!! 匿名 403 归因不得再写\"私有\"——高频真因是配额限流（2026-11-03 实弹）"); }
        if (!Program.MetaUnreadableWhy(404, authed: false).Contains("不可区分"))
        { fail++; Console.WriteLine("!! 匿名 404 须保留两可措辞（私有库/不存在匿名不可区分，v2.8 纪律）"); }
        if (!Program.MetaUnreadableWhy(404, authed: true).Contains("带凭据"))
        { fail++; Console.WriteLine("!! 带凭据 404 应收窄措辞（静默 vault 回退后两可不再成立）"); }
        if (!Program.MetaUnreadableWhy(-1, false).Contains("未发出") || !Program.MetaUnreadableWhy(0, false).Contains("超时"))
        { fail++; Console.WriteLine("!! -1/0 归因应为\"未发出/超时\""); }

        Console.WriteLine(fail == 0
            ? "拒绝可见性：isError 两族 / deny_reason 往返 / 预读归因全对齐。"
            : "拒绝可见性检查有不符项（见上）。");
        return fail;
    }
}
