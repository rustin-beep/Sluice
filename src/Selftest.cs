using System.Text.Json.Nodes;

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
        ("commenteditrejected", ExitCodes.BadRequest, "after_fix"), // v2.39：旧正文未动，核对评论 id 后重跑；他人评论不能改（正解再发一条）
        ("prreviewrejected",  ExitCodes.BadRequest, "after_fix"), // v2.11：APPROVE 自批 422 说破 → 改 RQC/COMMENT
        ("tagrejected",       ExitCodes.BadRequest, "after_fix"), // v2.13：Reference already exists → 用既有 tag 勿重试
        ("releaserejected",   ExitCodes.BadRequest, "after_fix"), // v2.13：already_exists → 查既有 release；未发通知未触发 webhook
        ("dispatched",        ExitCodes.Success,     "—"),         // v2.7：204 受理即完成，无需重发
        ("dispatchforbidden", ExitCodes.BadRequest, "after_fix"), // v2.7：PAT 缺 Actions:RW——用户动作，非 agent 可修
        ("dispatchnotfound",  ExitCodes.BadRequest, "after_fix"), // v2.7：workflow 不存在或 on: 未声明 dispatch
        ("dispatchrejected",  ExitCodes.BadRequest, "after_fix"), // v2.7：ref/inputs 校验拒
        ("apiredirected",     ExitCodes.BadRequest, "after_fix"), // v2.7：域外重定向不跟随，明说去向
        ("pushdone",        ExitCodes.Success,      "—"),          // v2.10：push 成功附本地引用状态说明
        ("pushdonewb",      ExitCodes.Success,      "—"),          // v2.33：回写成功态（refs 行=已回写）
        ("pushdonediv",     ExitCodes.Success,      "—"),          // v2.33：平行重打哨兵态（分叉提示+用户 fetch 指引）
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
        ("statusorphan",     ExitCodes.Unknown,      "wait_user"),  // v2.31：孤儿 pending 点名——终态不会再来，查证后重发（不再无限"尚未决"）
        ("statushttpfailed", ExitCodes.HttpFailed,   "retry_ok"),   // v2.31：http_failed 收割入族（同 live 20/retry_ok，消解两路径指引相反）
        ("statusabandoned",  ExitCodes.BadRequest,   "after_fix"),  // v2.32：入闸后早退的水位兜底行（审阅 P1 对偶面——同早退现场 after_fix）
        ("statusmergetail",     ExitCodes.Success,   "—"),          // v2.32：--delete-head 收尾行（合并已生效，删除成功）
        ("statusmergetailfail", ExitCodes.Success,   "—"),          // v2.32：收尾删除失败不动摇已生效的合并（审阅 P3.4）
        ("dedupblocked",     ExitCodes.PendingGate,  "wait_until"), // v2.31：同目标未决去重——并入原回执（对调用方仍是 pending 语义，收割链可达终态）
        ("dedupblockedcli",  ExitCodes.Cooldown,     "wait_until"), // v2.31：未决窗在 CLI 前门（无回执不挂 23——否则造出永查无终态的回执）
    ];

    private static readonly HashSet<string> ValidRetry =
    ["never", "after_fix", "retry_ok", "wait_user", "wait_until", "change_approach", "—"];

    public static int Run()
    {
        Console.WriteLine("Sluice selftest —— §8 消息表穷举 / exit code 对齐 / §9.7 MCP 映射 / §9 面一致锁 / v2.14+v2.16 push 逻辑锁 / v2.17 标签锁 / v2.18 崩溃诊断锁 / v2.23 审阅修复锁 / v2.24 拒绝可见性锁 / v2.26+v2.28 僵尸自曝锁 / v2.27 收割一致锁 / v2.29 issue 清单锁 / v2.30 匿名回退锁 / v2.31 pending 行与去重锁 / v2.32 审阅修复锁 / v2.33 平行链锚点与回写锁 / v2.34 发现时机锁 / v2.36 hook-guard 锁");
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
            if (name is "httpfail" or "pushblobmismatch" or "statushttpfailed" && !sample.Text.Contains("无任何副作用"))
            { fail++; Console.WriteLine("!! http_failed 缺\"无任何副作用\"句"); }
            if (name == "dedupblockedcli" && !sample.Text.Contains("本次操作未执行"))
            { fail++; Console.WriteLine("!! dedup CLI 版缺事实前置句\"本次操作未执行\""); }
            if (name == "statusorphan" && !sample.Text.Contains("结果未知"))
            { fail++; Console.WriteLine("!! 孤儿点名缺\"结果未知\"查证语义"); }
            if (name == "statusabandoned" && !sample.Text.Contains("无任何副作用"))
            { fail++; Console.WriteLine("!! abandoned 收割缺\"无任何副作用\"句"); }
            if (name == "statusmergetailfail" && !sample.Text.Contains("不影响合并结果"))
            { fail++; Console.WriteLine("!! 收尾失败版缺\"不影响合并结果\"事实句"); }
            if (name == "dedupblocked" && !sample.Text.Contains("同目标"))
            { fail++; Console.WriteLine("!! dedup 措辞应为\"同目标\"（target 级键是设计裁定，不得声称\"同操作\"）"); }
            // v2.33：push 成功三态的本地引用状态句——回写成功/分叉哨兵/未回写各有专属措辞，
            // 缺句即"引用状态不可判"（Bug 2 事故形态：agent 下次推送仍以为要 fetch）
            if (name == "pushdonewb" && !sample.Text.Contains("已回写"))
            { fail++; Console.WriteLine("!! 回写成功态缺\"已回写\"句"); }
            if (name == "pushdonediv" && !sample.Text.Contains("提交对象不在本地"))
            { fail++; Console.WriteLine("!! 分叉哨兵态缺\"提交对象不在本地\"（平行链信号）句"); }
            if (name == "pushdone" && !sample.Text.Contains("origin/"))
            { fail++; Console.WriteLine("!! push 成功消息缺本地 origin/{branch} 引用状态说明"); }

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
        fail += IssueListChecks();
        fail += AnonFallbackChecks();
        fail += PendingDedupChecks();
        fail += ReviewFix232Checks();
        fail += DiscoveryTimingChecks();
        fail += HookGuardChecks();
        fail += LinuxPortChecks();

        Console.WriteLine(fail == 0
            ? "PASS：全部消息可单读终判，exit code 与 §8.3 对齐。"
            : $"FAIL：{fail} 处不符。");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>v2.38 Linux 移植锁：①PassKdf 签名链契约（确定性 / 口令敏感 / 32B / 迭代数常量——
    /// 迭代数变了 = KDF 换代 = vault 需重建，必须显式改锁而非静默漂移）；②gh hosts.yml 双平台候选
    /// （Windows gh 写 %APPDATA%\GitHub CLI\，Linux gh 写 ~/.config/gh\——BCL ApplicationData 映射）；
    /// ③哨兵 systemd unit 文本（ExecStart 恒引号——与 HookGuardCommand/SentinelTr 同族"宁恒引号"
    /// 纪律；5min 周期与 schtasks 面对齐）；④平台展示名单源（BinName/ConsentName）。</summary>
    private static int LinuxPortChecks()
    {
        Console.WriteLine("--- v2.38 Linux 移植锁（PassKdf / hosts.yml 候选 / 哨兵 unit 文本 / 平台名单源）---");
        int fail = 0;
        void Eq<T>(string what, T actual, T expected)
        {
            if (!Equals(actual, expected)) { fail++; Console.WriteLine($"!! {what}：{actual} ≠ 期望 {expected}"); }
        }

        // (1) PassKdf 契约
        var salt = Convert.FromHexString("a1b2c3d4".PadRight(64, '0'));
        var challenge = Convert.FromHexString("e5f6a7b8".PadRight(64, '1'));
        var s1 = PassKdf.Sign(challenge, "correct horse", salt);
        var s2 = PassKdf.Sign(challenge, "correct horse", salt);
        Eq("签名长度 32B（HMAC-SHA256）", s1.Length, 32);
        Eq("同输入确定性（SLCV1 解密可复现的前提）", s2.SequenceEqual(s1), true);
        Eq("口令敏感（换口令必换签名）",
            PassKdf.Sign(challenge, "wrong horse", salt).SequenceEqual(s1), false);
        Eq("迭代数常量 600k（换代须显式改锁）", PassKdf.Pbkdf2Iterations, 600_000);

        // (2) gh hosts.yml 双平台候选
        var cands = SealCheck.GhHostsCandidates();
        Eq("候选数", cands.Count, 2);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
#if WINDOWS
        Eq("Windows 候选（%APPDATA%\\GitHub CLI\\hosts.yml）", cands[0], Path.Combine(appData, "GitHub CLI", "hosts.yml"));
#else
        Eq("Linux 候选（~/.config/gh/hosts.yml）", cands[1], Path.Combine(appData, "gh", "hosts.yml"));
#endif

        // (3) 哨兵 systemd unit 文本（逐字锁——改文本 = 改行为面，评审可见）
        Eq("service unit·ExecStart 恒引号", SetupCommand.SentinelUnitServiceText("/home/u/.local/share/Sluice/sentinel.sh"), """
            [Unit]
            Description=Sluice Sentinel seal preflight (plan-lite §6)

            [Service]
            Type=oneshot
            ExecStart="/home/u/.local/share/Sluice/sentinel.sh"
            SuccessExitStatus=7
            """);
        Eq("timer unit·5min 对齐 schtasks 面", SetupCommand.SentinelUnitTimerText(), """
            [Unit]
            Description=Sluice Sentinel timer (5 min)

            [Timer]
            OnBootSec=2min
            OnUnitActiveSec=5min
            AccuracySec=30s
            Persistent=true

            [Install]
            WantedBy=timers.target
            """);

        // (4) 平台展示名单源（文案参数化的两侧锚点）
#if WINDOWS
        Eq("BinName·Windows", Platform.BinName, "sluice.exe");
        Eq("ConsentName·Windows", Platform.ConsentName, "Windows Hello");
#else
        Eq("BinName·Linux", Platform.BinName, "sluice");
        Eq("ConsentName·Linux", Platform.ConsentName, "口令");
#endif
        Console.WriteLine(fail == 0 ? "v2.38 Linux 移植：KDF 契约 / 候选路径 / 哨兵 unit / 名单源全对齐。" : $"v2.38 Linux 移植锁：{fail} 处不符。");
        return fail;
    }

    /// <summary>v2.34 发现时机锁（incident-deferred-20261109）：①notice 元信息提取——host/path 入审计
    /// target，username/password 一概不取（遥测补强后 ①' 缺位才能归因到工作区）；②AGENTS.md 可见性
    /// 解析四态（temp 目录树）——doctor 第七项与 setup --install 工作区判定所依赖的窗口语义。</summary>
    private static int DiscoveryTimingChecks()
    {
        Console.WriteLine("--- 发现时机锁（v2.34：ParseCredentialMeta / ResolveAgentsVisibility）---");
        int fail = 0;
        void Eq<T>(string what, T actual, T expected)
        {
            if (!Equals(actual, expected)) { fail++; Console.WriteLine($"!! {what}：{actual} ≠ 期望 {expected}"); }
        }

        // (1) notice 元信息：host/path 提取；其余键（凭据形状）一概不取
        var (h, p) = Program.ParseCredentialMeta(
            ["protocol=https", "host=github.com", "path=rustin-beep/vivarium.git", "username=alice", "password=secret"]);
        Eq("host 提取", h, "github.com");
        Eq("path 提取", p, "rustin-beep/vivarium.git");
        var (h2, p2) = Program.ParseCredentialMeta(["host=ghe.corp.example"]);
        Eq("缺省·host 回退", h2, "ghe.corp.example");
        Eq("缺省·path null", p2, null);
        var (h3, p3) = Program.ParseCredentialMeta([]);
        Eq("空输入·host 默认", h3, "github.com");

        // (2) ResolveAgentsVisibility 四态（temp 树）：块在位（含子目录向上）/ 有 AGENTS 无块 / 有 .git 无 AGENTS / 非工作区
        var root = Path.Combine(Path.GetTempPath(), "sluice-v234-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var repoA = Directory.CreateDirectory(Path.Combine(root, "repoA"))!.FullName;
            Directory.CreateDirectory(Path.Combine(repoA, ".git"));
            File.WriteAllText(Path.Combine(repoA, "AGENTS.md"), "# 项目\n" + SetupCommand.Snippet);
            Eq("块在位·根", SetupCommand.ResolveAgentsVisibility(repoA).V, SetupCommand.AgentsVisibility.BlockFound);
            var sub = Directory.CreateDirectory(Path.Combine(repoA, "sub", "deep"))!.FullName;
            Eq("块在位·子目录向上", SetupCommand.ResolveAgentsVisibility(sub).V, SetupCommand.AgentsVisibility.BlockFound);

            var repoB = Directory.CreateDirectory(Path.Combine(root, "repoB"))!.FullName;
            Directory.CreateDirectory(Path.Combine(repoB, ".git"));
            File.WriteAllText(Path.Combine(repoB, "AGENTS.md"), "# 项目自述，无规则块\n");
            Eq("有 AGENTS 无块", SetupCommand.ResolveAgentsVisibility(repoB).V, SetupCommand.AgentsVisibility.AgentsWithoutBlock);

            var repoC = Directory.CreateDirectory(Path.Combine(root, "repoC"))!.FullName;
            Directory.CreateDirectory(Path.Combine(repoC, ".git"));
            Eq("有 .git 无 AGENTS", SetupCommand.ResolveAgentsVisibility(repoC).V, SetupCommand.AgentsVisibility.RepoWithoutAgents);

            var plain = Directory.CreateDirectory(Path.Combine(root, "plain"))!.FullName;
            Eq("非工作区", SetupCommand.ResolveAgentsVisibility(plain).V, SetupCommand.AgentsVisibility.NotAWorkspace);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }

        Console.WriteLine(fail == 0
            ? "v2.34 发现时机：notice meta / AGENTS 可见性四态全对齐。"
            : "v2.34 发现时机检查有不符项（见上）。");
        return fail;
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
        // v2.28：双哨兵 (-1,-1)——启动时即不可解析、此后也取不到，相等比较会放行（永不判过期），
        // 与"文件消失同判过期 fail-closed"的声称矛盾；startup 侧哨兵前置短路
        Eq("双哨兵(-1,-1)同判过期", McpServer.ImageChanged((-1, DateTime.MinValue), (-1, DateTime.MinValue)), true);

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
        Console.WriteLine("--- push 逻辑锁（v2.14/v2.16：基点消歧 / blob 去重 / CI 聋哑 / 限流嗅探 / 消歧适用面；v2.33：新建分支锚点决策）---");
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

        // (6) 新建分支锚点决策（v2.33，平行链事故根治）：默认分支头是 HEAD 祖先 → 收窄（复用远端
        //     原 SHA 不重打）；声明基点恰等于默认头 → 维持声明（锚同物无收窄）；tip 缺失/非祖先
        //     （分叉或独立历史）→ 维持声明（特性分支意图）/ 全历史兜底。变异自证方向：若把
        //     DefaultTip 臂改为恒 Declared，PR #4 事故形态（陈旧基点整段重打）将静默回归。
        Eq("无基点+tip祖先→收窄", RestPush.ResolveNewBranchAnchor(false, false, true), RestPush.NewBranchAnchor.DefaultTip);
        Eq("陈旧基点+tip祖先→收窄", RestPush.ResolveNewBranchAnchor(true, false, true), RestPush.NewBranchAnchor.DefaultTip);
        Eq("基点即tip→维持", RestPush.ResolveNewBranchAnchor(true, true, true), RestPush.NewBranchAnchor.Declared);
        Eq("tip非祖先+有基点→维持(特性分支意图)", RestPush.ResolveNewBranchAnchor(true, false, false), RestPush.NewBranchAnchor.Declared);
        Eq("tip非祖先+无基点→全历史", RestPush.ResolveNewBranchAnchor(false, false, false), RestPush.NewBranchAnchor.FullHistory);
        Eq("tip非祖先+基点即tip(同物)→维持", RestPush.ResolveNewBranchAnchor(true, true, false), RestPush.NewBranchAnchor.Declared);

        Console.WriteLine(fail == 0 ? "push 逻辑锁通过。" : "push 逻辑锁有不符项（见上）。");
        return fail;
    }

    /// <summary>v2.17 标签面批的 pure 决策入锁：--labels 解析（trim / 空段拒 / 重复拒——
    /// OrdinalIgnoreCase，GitHub 标签唯一性大小写不敏感）与整组替换摘除清单（当前减）。
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

    /// <summary>v2.30 匿名读回退判定入锁：401/403/404/429（认证可见性/限流族）触发复读；
    /// 2xx/3xx/422/5xx/超时/未发出不触发（复读无意义或与凭据无关）。与网络/vault 解耦——
    /// 触发面本身就是不变量（扩大触发面 = 每次对应失败多一次 token 请求与审计行）。</summary>
    private static int AnonFallbackChecks()
    {
        Console.WriteLine("--- 匿名读回退锁（v2.30：401/403/404/429 触发，其余不触发）---");
        int fail = 0;
        void Eq<T>(string what, T actual, T expected)
        {
            if (!Equals(actual, expected)) { fail++; Console.WriteLine($"!! {what}：{actual} ≠ 期望 {expected}"); }
        }

        foreach (var st in new[] { 401, 403, 404, 429 })
            Eq($"HTTP {st} 触发复读", Program.AnonReadShouldFallback(st), true);
        foreach (var st in new[] { 200, 204, 301, 302, 422, 500, 0, -1 })
            Eq($"HTTP {st} 不触发", Program.AnonReadShouldFallback(st), false);

        Console.WriteLine(fail == 0 ? "匿名读回退锁通过。" : "匿名读回退锁有不符项（见上）。");
        return fail;
    }

    /// <summary>v2.29 issue list 锁（读侧清单面）：查询拼装（清单/搜索两形态、标签逐名编码、
    /// state 限定符映射、含空格标签引号短语）与渲染（PR 隐去计数、--with-prs 标记、state_reason
    /// 附加、标题 120 截断不切代理对、标签 join、搜索 total_count 透传）。与网络解耦——
    /// 决策表本身即不变量。缘起：agent 实弹"issue 只能逐个查看、不能按标签或关键词搜索"。</summary>
    private static int IssueListChecks()
    {
        Console.WriteLine("--- issue 清单锁（v2.29：查询拼装 / 渲染）---");
        int fail = 0;
        void Eq<T>(string what, T actual, T expected)
        {
            if (!Equals(actual, expected)) { fail++; Console.WriteLine($"!! {what}：{actual} ≠ 期望 {expected}"); }
        }

        // (1) 清单路径：全参显式（不依赖端点缺省）；标签逐名 percent-encode（CJK 名是常态）
        Eq("清单缺省参", Program.IssueListPath("o/r", "open", [], 30, 1),
           "repos/o/r/issues?state=open&per_page=30&page=1&sort=created&direction=desc");
        Eq("清单全参+标签编码",
           Program.IssueListPath("rustin-beep/Vitro", "closed", ["bug", "根因压测"], 100, 3),
           "repos/rustin-beep/Vitro/issues?state=closed&per_page=100&page=3&sort=created&direction=desc"
               + "&labels=bug," + Uri.EscapeDataString("根因压测"));

        // (2) 搜索路径：q 整体编码（解码后核结构——锁拼装语义而非编码字节序）
        var sp = Program.IssueSearchPath("o/r", "open", ["needs review"], "内存泄漏 step", 50, 2);
        Eq("搜索端点前缀", sp.StartsWith("search/issues?q=", StringComparison.Ordinal), true);
        var q = Uri.UnescapeDataString(sp[(sp.IndexOf("?q=", StringComparison.Ordinal) + 3)..].Split('&')[0]);
        Eq("搜索 repo 限定", q.Contains("repo:o/r", StringComparison.Ordinal), true);
        Eq("搜索恒带 type:issue", q.Contains("type:issue", StringComparison.Ordinal), true);
        Eq("搜索 state 映射", q.Contains("state:open", StringComparison.Ordinal), true);
        Eq("含空格标签引号短语", q.Contains("label:\"needs review\"", StringComparison.Ordinal), true);
        Eq("关键词原样缀尾", q.EndsWith(" 内存泄漏 step", StringComparison.Ordinal), true);
        Eq("搜索分页参数", sp.Contains("per_page=50&page=2", StringComparison.Ordinal), true);
        Eq("无空格标签不加引号",
           Uri.UnescapeDataString(Program.IssueSearchPath("o/r", "open", ["bug"], "x", 30, 1)
               .Split('&')[0])["search/issues?q=".Length..].Contains(" label:bug", StringComparison.Ordinal), true);

        // (3) 渲染：PR 隐去计数 / --with-prs 标记 / state_reason / 截断 / 标签 / 搜索形态 total_count
        var longTitle = new string('标', 130);
        var body = """
            [{"number":479,"state":"open","title":"台账","labels":[{"name":"bug"},{"name":"根因压测"}]},
             {"number":480,"state":"closed","state_reason":"not planned","title":"PR 条目","labels":[],"pull_request":{"url":"x"}},
             {"number":481,"state":"open","title":"长标题","labels":[]}]
            """.Replace("长标题", longTitle);
        var (lines, issues, prs, total) = Program.RenderIssueList(body, withPrs: false);
        Eq("issue 计数", issues, 2);
        Eq("PR 计数（隐去但计入）", prs, 1);
        Eq("清单形态 total=-1", total, -1L);
        Eq("隐去 PR 行数", lines.Length, 2);
        Eq("行格式（标签 join）", lines[0], "#479 [open] 台账 {bug, 根因压测}");
        var (lines2, _, _, _) = Program.RenderIssueList(body, withPrs: true);
        Eq("--with-prs 行数", lines2.Length, 3);
        Eq("--with-prs 带 [PR] 标", lines2[1], "#480 [closed·not planned][PR] PR 条目");
        Eq("长标题截断=120字+…", (lines[1].Count(c => c == '标'), lines[1].EndsWith("…", StringComparison.Ordinal)), (120, true));
        var sbody = """{"total_count":7,"items":[{"number":1,"state":"open","title":"t","labels":[]}]}""";
        var (slines, sissues, sprs, stotal) = Program.RenderIssueList(sbody, withPrs: false);
        Eq("搜索形态 total_count 透传", stotal, 7L);
        Eq("搜索形态行数", (slines.Length, sissues, sprs), (1, 1, 0));

        Console.WriteLine(fail == 0 ? "issue 清单锁通过。" : "issue 清单锁有不符项（见上）。");
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
        "issue_create", "pr_close", "issue_reopen", "pr_edit", "comment_edit", "tag_create", "release_create",
        "issue_label",
    ];

    /// <summary>§9 内的只读 op（不进 CLI 写动词表与 MCP Actions；新增读 op 须在此登记）。</summary>
    private static readonly string[] ReadOps = ["api", "issue_list"];

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

    /// <summary>
    /// v2.31+v2.32 pending 行 + 去重锁（E0-20261108 实弹：三窗堆叠 6 连点、未决零痕迹、
    /// http_failed 收割与 live 指引相反；v2.32 审阅变异探针补洞）。全部纯函数直测，零文件系统。
    /// v2.32 新增：S6b（CLI 行 dedup_blocked 不得置 matchedTerminalSeen——变异 `!= "dedup_blocked"`
    /// 改恒真曾使 selftest 仍 PASS，放行的正是 E0 要挡的第二扇窗）；S8 败者自赎（同 req 相互解析）；
    /// S9 占位-扫描竞态（文件行序即全序，最早占位者胜出）；S10 push 重放形态 target 关联（@ 界标）。
    /// </summary>
    private static int PendingDedupChecks()
    {
        Console.WriteLine("--- v2.31+v2.32 pending 行与去重锁（FindPendingCore / receipt 往返 / 收割映射 / result 解析）---");
        int fail = 0;
        void Eq(string what, object? actual, object? expected)
        {
            if (!Equals(actual, expected)) { fail++; Console.WriteLine($"!! {what}：{actual} ≠ 期望 {expected}"); }
        }

        var now = DateTime.UtcNow;
        var stale = TimeSpan.FromSeconds(180);
        const string Rpo = "rustin-beep/vivarium";
        DedupGate.Row R(int secAgo, string? req, string decision, string op = "push", string target = "v3-libc",
            string? repo = Rpo, string result = "") =>
            new(now - TimeSpan.FromSeconds(secAgo), req, op, repo, target, decision, result);

        // rowsDesc 参数约定 = 逆序（新行在前）
        // S1 fresh MCP pending → 命中返回原 req
        Eq("S1 fresh pending", DedupGate.FindPendingCore(
            [R(10, "a1b2c3d4e5f60718", "pending")], "push", Rpo, "v3-libc", now, stale), "a1b2c3d4e5f60718");
        // S2 同 req 更晚已有终态 → 已收割，放行
        Eq("S2 终态收割", DedupGate.FindPendingCore(
            [R(5, "a1b2c3d4e5f60718", "approved"), R(60, "a1b2c3d4e5f60718", "pending")],
            "push", Rpo, "v3-libc", now, stale), null);
        // S3 fresh CLI pending（req=null）→ "cli"
        Eq("S3 CLI pending", DedupGate.FindPendingCore(
            [R(10, null, "pending")], "push", Rpo, "v3-libc", now, stale), "cli");
        // S4 CLI pending 后同目标更晚有真终态 → 按目标关联放行（快速决策不得锁目标到超龄）
        Eq("S4 CLI 终态关联", DedupGate.FindPendingCore(
            [R(5, null, "denied"), R(60, null, "pending")], "push", Rpo, "v3-libc", now, stale), null);
        // S5 超龄孤儿 → 放行（死窗不得永久占坑）
        Eq("S5 孤儿放行", DedupGate.FindPendingCore(
            [R(400, "a1b2c3d4e5f60718", "pending")], "push", Rpo, "v3-libc", now, stale), null);
        // S6 MCP 行：他人的 dedup_blocked 不解析原 pending（原窗仍活）
        Eq("S6 dedup 不解析他人", DedupGate.FindPendingCore(
            [R(5, "ffffffffffffffff", "dedup_blocked"), R(60, "a1b2c3d4e5f60718", "pending")],
            "push", Rpo, "v3-libc", now, stale), "a1b2c3d4e5f60718");
        // S6b（v2.32 审阅变异洞）：CLI 行的 dedup_blocked 同样不得当终态——若置 matchedTerminalSeen，
        // CLI 活窗未决时一条 dedup_blocked 就会放行下一个请求开第二扇窗（E0 复燃）
        Eq("S6b CLI dedup 不解析", DedupGate.FindPendingCore(
            [R(5, null, "dedup_blocked"), R(60, null, "pending")], "push", Rpo, "v3-libc", now, stale), "cli");
        // S7 不同目标/不同 op 的 pending → 不跨界
        Eq("S7 目标隔离", DedupGate.FindPendingCore(
            [R(10, "a1b2c3d4e5f60718", "pending", target: "v3-frontend")], "push", Rpo, "v3-libc", now, stale), null);
        Eq("S7 op 隔离", DedupGate.FindPendingCore(
            [R(10, "a1b2c3d4e5f60718", "pending", op: "branch_delete")], "push", Rpo, "v3-libc", now, stale), null);
        // S8（v2.32 败者自赎）：dedup_blocked 只解析**同 req** 的 pending——竞态败者自赎后，
        // 仍能看见更早的胜者 pending（它才是持窗者）
        Eq("S8 败者自赎见胜者", DedupGate.FindPendingCore(
            [R(5, "bebebabe00000000", "dedup_blocked"), R(6, "bebebabe00000000", "pending"),
             R(60, "a1b2c3d4e5f60718", "pending")], "push", Rpo, "v3-libc", now, stale), "a1b2c3d4e5f60718");
        // S9（v2.32 占位-扫描）：并发双发，文件行序即全序。desc = [B(晚), A(早)]：
        // A 携 ownNonce 扫描 → B 更晚不胜我 → A 胜出（null = 本请求开窗）；
        // B 携 ownNonce 扫描 → A 更早 → B 退让给 A 的回执；第三方（无 nonce）→ 见最早持窗者 A
        var race = new[]
        {
            R(10, "bbbbbbbbbbbbbbbb", "pending", result: "window:bbbbbbbb"),
            R(11, "aaaaaaaaaaaaaaaa", "pending", result: "window:aaaaaaaa"),
        };
        Eq("S9 早占位者胜出", DedupGate.FindPendingCore(race, "push", Rpo, "v3-libc", now, stale,
            ownNonce: "aaaaaaaa"), null);
        Eq("S9 晚占位者退让", DedupGate.FindPendingCore(race, "push", Rpo, "v3-libc", now, stale,
            ownNonce: "bbbbbbbb"), "aaaaaaaaaaaaaaaa");
        Eq("S9 第三方见最早", DedupGate.FindPendingCore(race, "push", Rpo, "v3-libc", now, stale), "aaaaaaaaaaaaaaaa");
        // S10（v2.32 审阅 P3.1）：push 重放终态行 target="branch@旧→新" 须解析裸分支名的 CLI pending；
        // "@" 界标防前缀误撞（v3 分支的终态不得解析 v3-libc 的 pending）
        Eq("S10 重放终态关联 CLI", DedupGate.FindPendingCore(
            [R(5, null, "approved", target: "v3-libc@a1b2c3d4→e5f60718"), R(60, null, "pending", target: "v3-libc")],
            "push", Rpo, "v3-libc", now, stale), null);
        Eq("S10 @界标防误撞", DedupGate.FindPendingCore(
            [R(5, null, "approved", target: "v3@x→y"), R(60, null, "pending", target: "v3-libc")],
            "push", Rpo, "v3-libc", now, stale), "cli");

        // receipt 定界往返（server 超时路径与 dedup 快路径都以消息为准——定界漂移 = 收割链断）
        var rc = "a1b2c3d4e5f60718";
        var pg = Messages.PendingGate(rc, 15, 75);
        Eq("PendingGate 回执往返", McpServer.ExtractReceipt(pg.Prefix + " " + pg.Text), rc);
        var db = Messages.DedupBlocked("push", "v3-libc", rc, 75);
        Eq("DedupBlocked 回执往返", McpServer.ExtractReceipt(db.Prefix + " " + db.Text), rc);
        var dbc = Messages.DedupBlockedCli("push", "v3-libc", 60);
        Eq("DedupBlockedCli 无回执", McpServer.ExtractReceipt(dbc.Prefix + " " + dbc.Text), null);

        // 收割映射：http_failed/abandoned/delete_head_* 入族；pending/dedup_blocked 必须不进
        // StatusFamily（RunStatus 在 switch 前单列处理行龄/链式——进了表反而会被 StatusOther 吞掉）
        Eq("StatusFamily http_failed", Program.StatusFamily("http_failed"), ExitCodes.HttpFailed);
        Eq("StatusFamily abandoned", Program.StatusFamily("abandoned"), ExitCodes.BadRequest);
        Eq("StatusFamily delete_head 成", Program.StatusFamily("delete_head_executed"), ExitCodes.Success);
        Eq("StatusFamily delete_head 败", Program.StatusFamily("delete_head_failed"), ExitCodes.Success);
        Eq("StatusFamily pending 不入表", Program.StatusFamily("pending"), ExitCodes.Unknown);
        Eq("StatusFamily dedup 不入表", Program.StatusFamily("dedup_blocked"), ExitCodes.Unknown);

        // result 解析（收割链与描述提取）
        Eq("DedupInnerOf", Program.DedupInnerOf("pending:a1b2c3d4e5f60718"), "a1b2c3d4e5f60718");
        Eq("DedupInnerOf cli", Program.DedupInnerOf("pending:cli"), "cli");
        Eq("DedupInnerOf 非形态", Program.DedupInnerOf("not_sent:x"), null);
        Eq("DedupInnerOf 空值", Program.DedupInnerOf("pending:"), null);
        Eq("NotSentDetailOf 有描述", Program.NotSentDetailOf("not_sent:连接被重置"), "连接被重置");
        Eq("NotSentDetailOf 旧行", Program.NotSentDetailOf("not_sent"), "");
        Eq("NotSentDetailOf verify 形态", Program.NotSentDetailOf("timeout_retry_safe_get"), "");

        Console.WriteLine(fail == 0
            ? "pending 行与去重：FindPendingCore 十四景 / receipt 往返 / 收割映射 / result 解析全对齐。"
            : "pending 去重检查有不符项（见上）。");
        return fail;
    }

    /// <summary>
    /// v2.32 审阅修复批锁：① AGENTS.md 片段三源一致（rule 7 曾只改 SetupCommand 一源——
    /// SetupCommand 自称"selftest 不锁散文，靠评审"，评审抓到后本锁把该豁免收窄到
    /// "AGENTS.md 实际块 == Snippet 常量"这一机械可比面）；② --search 空页页脚（审阅 P3.3）；
    /// ③ 标题截断代理对（审阅 P3.7——实现本就正确，缺的是样例锁）。
    /// </summary>
    private static int ReviewFix232Checks()
    {
        Console.WriteLine("--- v2.32 审阅修复锁（AGENTS 三源 / 搜索空页脚 / 代理对截断）---");
        int fail = 0;
        void Eq(string what, object? actual, object? expected)
        {
            if (!Equals(actual, expected)) { fail++; Console.WriteLine($"!! {what}：{actual} ≠ 期望 {expected}"); }
        }

        // ① AGENTS.md 标记块 == SetupCommand.Snippet（找到仓库根的 AGENTS.md 才锁；发布件场景跳过）
        var src = "未找到 AGENTS.md（发布件场景跳过三源锁）";
        for (var i = 0; i <= 4; i++)
        {
            var p = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                string.Concat(Enumerable.Repeat(".." + Path.DirectorySeparatorChar, i)), "AGENTS.md"));
            if (!File.Exists(p)) continue;
            src = p;
            var text = File.ReadAllText(p);
            var b = text.IndexOf(SetupCommand.SnippetBegin, StringComparison.Ordinal);
            var e = b >= 0 ? text.IndexOf(SetupCommand.SnippetEnd, b + 1, StringComparison.Ordinal) : -1;
            if (b < 0 || e < 0)
            { fail++; Console.WriteLine("!! AGENTS.md 标记块缺失/损坏（BEGIN 后无 END）"); break; }
            var block = text[(b + SetupCommand.SnippetBegin.Length)..e].Trim();
            var want = SetupCommand.Snippet.Trim();
            var wb = want.IndexOf(SetupCommand.SnippetBegin, StringComparison.Ordinal);
            var we = want.IndexOf(SetupCommand.SnippetEnd, wb + 1, StringComparison.Ordinal);
            var wantInner = want[(wb + SetupCommand.SnippetBegin.Length)..we].Trim();
            // 容忍行尾空白/换行风格差异，语义逐行对齐
            static string Norm(string s) => string.Join("\n", s.Split('\n').Select(l => l.TrimEnd()));
            if (Norm(block) != Norm(wantInner))
            {
                fail++;
                Console.WriteLine("!! AGENTS.md 实际块 ≠ SetupCommand.Snippet（rule 7 三源断裂复发——跑 sluice setup --install-snippet 或手工同步）");
            }
            break;
        }
        Console.WriteLine($"AGENTS 三源来源：{src}");

        // ② --search 空页页脚（零命中与页码越界同形，统一点名；旧版渲染"显示第 1–0 条"）
        Eq("空页·零命中", Program.SearchFooter(1, 30, 0, 0), "无匹配结果（第 1 页为空：关键词无命中，或页码越界——回第 1 页核对）");
        Eq("空页·页码越界", Program.SearchFooter(9, 30, 0, 257), "无匹配结果（第 9 页为空：关键词无命中，或页码越界——回第 1 页核对）");
        Eq("正常翻页", Program.SearchFooter(1, 30, 30, 95), "显示第 1–30 条 · 下一页：--page 2");
        Eq("末页", Program.SearchFooter(4, 30, 5, 95), "显示第 91–95 条 · 已是末页");

        // ③ 标题截断代理对：切点 120 落在高代理位时回退 119（不产出断代理对）；BMP 边界照切
        var sur = new string('标', 119) + "🎮尾"; // 长度 122，[119]=高代理位
        var bodySur = $$"""[{"number":1,"state":"open","title":"{{sur}}","labels":[]}]""";
        var (linesSur, _, _, _) = Program.RenderIssueList(bodySur, withPrs: false);
        if (!linesSur[0].Contains("…") || linesSur[0].Contains("🎮"))
        { fail++; Console.WriteLine("!! 代理对截断：应在 119 位回退切点（🎮 整体让位于 …）"); }
        if (linesSur[0].Any(char.IsSurrogate))
        { fail++; Console.WriteLine("!! 截断产出断代理对（渲染乱码）"); }
        var bmp = new string('标', 120) + "尾"; // 长度 121，[119]=BMP——切 120
        var bodyBmp = $$"""[{"number":1,"state":"open","title":"{{bmp}}","labels":[]}]""";
        var (linesBmp, _, _, _) = Program.RenderIssueList(bodyBmp, withPrs: false);
        if (!linesBmp[0].Contains("…") || linesBmp[0].Contains("尾"))
        { fail++; Console.WriteLine("!! BMP 边界：应切 120 位（尾字让位于 …）"); }

        Console.WriteLine(fail == 0
            ? "v2.32 审阅修复：AGENTS 三源 / 搜索空页脚 / 代理对截断全对齐。"
            : "v2.32 审阅修复检查有不符项（见上）。");
        return fail;
    }

    /// <summary>
    /// v2.36 hook-guard 锁（spec-guard-20261112 层 B）：模式矩阵源自 2026-11-12 临时件
    /// hook-guard.sh 的 29 例实弹矩阵——产品化替换后，矩阵从"脚本现场测试"升为"回归不变量"。
    /// v2.37 审阅处置批（P2-1/P3-7/P2-2）扩：正则改分词语义（命令位锚定）后的补例
    /// （分隔符后随 / -X 紧贴 / gh api -f 隐式 POST / push 不在子命令位 / gh 不在命令位 /
    /// 前导空格 / env / 带值选项 -c / 多行）、暂停窗内仍拦的语义翻转、repo-local 空值不误报。
    /// 另锁：宿主 JSON 提取（fail-open 三态）、notice 在位判定（任意路径）、hook 命令引号形态
    /// （含空格路径，/TR 引号教训同族）、repo-local credential.helper 清查判定面。全部纯函数，
    /// 零文件系统、零 git。
    /// </summary>
    private static int HookGuardChecks()
    {
        int fail = 0;
        void Eq<T>(string what, T actual, T expected)
        {
            if (!Equals(actual, expected)) { fail++; Console.WriteLine($"!! {what}：{actual} ≠ 期望 {expected}"); }
        }
        void Case(string cmd, bool wantDeny)
        {
            var got = HookGuard.DenyCommand(cmd, out _);
            if (got != wantDeny) { fail++; Console.WriteLine($"!! 矩阵不符：{(wantDeny ? "应拦" : "应放")}「{cmd}」实际{(got ? "拦" : "放")}"); }
        }

        // 拦（13）：git push 族（含 -C / 分隔符后 / --dry-run 训练面一致）+ gh 登录流与写动词族 + gh api 写方法 + curl 写形态
        Case("git push", true);
        Case("git push origin main", true);
        Case("git -C D:/x push", true);
        Case("cd /d/code/x && git push", true);
        Case("git push --dry-run", true);
        Case("gh auth login", true);
        Case("gh auth login --web", true);
        Case("gh pr merge", true);
        Case("gh issue close", true);
        Case("gh api -X POST repos/o/r/issues", true);
        Case("gh api --method=DELETE repos/o/r/comments/1", true);
        Case("curl -X POST api.github.com/repos/o/r/issues", true);
        Case("curl -d api.github.com/repos/o/r", true);

        // v2.37 审阅补例（7）：漏拦修复（分隔符后随命令 / -X 紧贴 / gh api -f 隐式 POST）与
        // 误拦修复（push 不在子命令位 / gh 不在命令位 / 前导空格 / 分隔符后的 gh）
        Case("git push; git status", true);
        Case("curl -XPOST https://api.github.com/repos/o/r/issues", true);
        Case("gh api -f title=x repos/o/r/issues", true);
        Case("git log --grep push", false);
        Case("""echo "gh pr merge" >> note.md""", false);
        Case("  git push", true);
        Case("cd /d/x && gh pr merge", true);
        Case("env git push", true);          // spec §2.3 原形态（分词版回归）
        Case("git -c http.proxy=x push", true); // 带值全局选项 -c 后子命令位仍是 push
        Case("ls\ngit push", true);          // 多行脚本：换行即段界

        // 放（12）：一切只读 / 无关 / 空命令（echo 内嵌 "git push" 字面量不拦——引号内非命令位）
        Case("git status", false);
        Case("git log --oneline -5", false);
        Case("git fetch origin", false);
        Case("git clone https://github.com/o/r", false);
        Case("gh pr view 5", false);
        Case("gh issue list --limit 30", false);
        Case("gh auth status", false);
        Case("gh api repos/o/r/pulls", false);
        Case("curl https://api.github.com/repos/o/r", false);
        Case("ls", false);
        Case("""echo "git push" >> note.md""", false);
        Case("", false);

        // 未封态（2）：notice 不在位且非暂停（unseal）→ 放行（层是糖，状态跟 sluice 状态机走）
        if (HookGuard.Decide("git push", noticePresent: false, paused: false, out _)) { fail++; Console.WriteLine("!! 未封态应放行：git push"); }
        if (HookGuard.Decide("gh auth login", noticePresent: false, paused: false, out _)) { fail++; Console.WriteLine("!! 未封态应放行：gh auth login"); }

        // 暂停边界（v2.37 审阅 P3-7 修订）：暂停窗内**仍拦**（GCM 已恢复交互，agent 直推 =
        // 登录窗 + 挂起原样复发；人登录走自己终端不经宿主）；过期/未暂停 + notice 在位照拦
        if (!HookGuard.Decide("git push", noticePresent: true, paused: true, out _)) { fail++; Console.WriteLine("!! 暂停窗内仍应拦：git push"); }
        if (!HookGuard.Decide("git push", noticePresent: true, paused: false, out _)) { fail++; Console.WriteLine("!! 过期暂停应恢复拦截：git push"); }
        if (!HookGuard.Decide("gh auth login", noticePresent: false, paused: true, out _)) { fail++; Console.WriteLine("!! 暂停窗内仍应拦：gh auth login（notice 已摘但暂停未过期）"); }

        // 宿主 JSON 提取（fail-open 三态）：合法 / 缺 tool_input / 垃圾输入
        if (HookGuard.ExtractCommand("""{"tool_name":"Bash","tool_input":{"command":"git push"}}""") != "git push")
        { fail++; Console.WriteLine("!! ExtractCommand：合法载荷应取出 command"); }
        if (HookGuard.ExtractCommand("""{"tool_name":"Bash"}""") is not null)
        { fail++; Console.WriteLine("!! ExtractCommand：缺 tool_input 应 null（放行）"); }
        if (HookGuard.ExtractCommand("not json at all") is not null)
        { fail++; Console.WriteLine("!! ExtractCommand：垃圾输入应 null（放行）"); }

        // notice 在位判定（任意路径——比 doctor 的 ProcessPath 精确匹配宽一档，路径漂移期仍封堵）
        if (!HookGuard.AnyNoticeHelperPresent("\nD:/old/publish/sluice.exe notice"))
        { fail++; Console.WriteLine("!! AnyNoticeHelperPresent：任意路径的 notice 值应判在位"); }
        if (HookGuard.AnyNoticeHelperPresent("D:/x/sluice.exe noticex"))
        { fail++; Console.WriteLine("!! AnyNoticeHelperPresent：非 notice 值不应误判"); }
        if (HookGuard.AnyNoticeHelperPresent(""))
        { fail++; Console.WriteLine("!! AnyNoticeHelperPresent：空输出应判不在位"); }

        // hook 命令引号形态（v2.23 /TR 引号教训同族：恒带内层引号 + 正斜杠）
        Eq("hook 命令·反斜杠含空格", SetupCommand.HookGuardCommand(@"C:\Users\John Doe\sluice.exe"),
            "\"C:/Users/John Doe/sluice.exe\" hook-guard");
        Eq("hook 命令·无空格也恒引号", SetupCommand.HookGuardCommand(@"D:\Sluice\publish\sluice.exe"),
            "\"D:/Sluice/publish/sluice.exe\" hook-guard");

        // hook 条目摘除（v0.7.11 实弹抓到的倒数循环 j++ 笔误入锁——uninstall 崩溃正是本函数前身）：
        // 内置模式与临时 .sh 都摘、壳空连条目摘、无关 hook 一概不动
        var pre = new JsonArray(
            (JsonNode)new JsonObject
            {
                ["matcher"] = "Bash",
                ["hooks"] = new JsonArray((JsonNode)new JsonObject
                    { ["type"] = "command", ["command"] = "\"D:/x/sluice.exe\" hook-guard" }),
            },
            (JsonNode)new JsonObject
            {
                ["matcher"] = "Bash",
                ["hooks"] = new JsonArray((JsonNode)new JsonObject
                    { ["type"] = "command", ["command"] = "\"C:/bash.exe\" \"C:/Users/u/hook-guard.sh\"" }),
            },
            (JsonNode)new JsonObject
            {
                ["matcher"] = "Write",
                ["hooks"] = new JsonArray((JsonNode)new JsonObject
                    { ["type"] = "command", ["command"] = "my-linter --check" }),
            });
        Eq("摘除数（内置+临时件，无关不动）", SetupCommand.RemoveHookGuardEntries(pre), 2);
        Eq("存活条目数（两条壳已摘）", pre.Count, 1);
        Eq("存活条目是无关 hook", pre[0]!["matcher"]!.GetValue<string>(), "Write");

        // repo-local credential.helper 清查（层 A 深扫增项；v2.37 审阅 P2-2 增空值锁）：
        // 判定面四态——无作用域/github.com 作用域报违规（文本须带 config 路径，人工指路要用），
        // 其他 host 作用域不碰，空值（断继承惯用法，sluice 全局同款）不误报
        var vl = SealCheck.RepoLocalHelperViolationsIn("[credential]\n\thelper = manager\n".Split('\n'), @"D:\repo\.git\config");
        if (vl.Count != 1 || !vl[0].Contains(@"D:\repo\.git\config")) { fail++; Console.WriteLine("!! repo-local：无 URL 作用域 helper 应报违规且文本带 config 路径"); }
        var vg = SealCheck.RepoLocalHelperViolationsIn("[credential \"https://github.com\"]\n\thelper = !gh auth git-credential\n".Split('\n'), "cfg");
        if (vg.Count != 1) { fail++; Console.WriteLine("!! repo-local：github.com 作用域 helper 应报违规"); }
        var vo = SealCheck.RepoLocalHelperViolationsIn("[credential \"https://gitlab.com\"]\n\thelper = manager\n[user]\n\tname = x\n".Split('\n'), "cfg");
        if (vo.Count != 0) { fail++; Console.WriteLine("!! repo-local：其他 host 作用域不应误报"); }
        var ve = SealCheck.RepoLocalHelperViolationsIn("[credential]\n\thelper =\n\thelper = \"\"\n".Split('\n'), "cfg");
        if (ve.Count != 0) { fail++; Console.WriteLine("!! repo-local：空值 helper（断继承惯用法）不应误报"); }

        Console.WriteLine(fail == 0
            ? "v2.36+v2.37 hook-guard：40 例矩阵（29 基线 + 审阅补例）/ JSON 提取 / 在位判定 / 引号形态 / repo-local 清查全对齐。"
            : "v2.36+v2.37 hook-guard 检查有不符项（见上）。");
        return fail;
    }
}
