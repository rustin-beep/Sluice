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
        ("dispatched",        ExitCodes.Success,     "—"),         // v2.7：204 受理即完成，无需重发
        ("dispatchforbidden", ExitCodes.BadRequest, "after_fix"), // v2.7：PAT 缺 Actions:RW——用户动作，非 agent 可修
        ("dispatchnotfound",  ExitCodes.BadRequest, "after_fix"), // v2.7：workflow 不存在或 on: 未声明 dispatch
        ("dispatchrejected",  ExitCodes.BadRequest, "after_fix"), // v2.7：ref/inputs 校验拒
        ("apiredirected",     ExitCodes.BadRequest, "after_fix"), // v2.7：域外重定向不跟随，明说去向
    ];

    private static readonly HashSet<string> ValidRetry =
    ["never", "after_fix", "retry_ok", "wait_user", "wait_until", "change_approach", "—"];

    public static int Run()
    {
        Console.WriteLine("Sluice selftest —— §8 消息表穷举 / exit code 对齐");
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
                && !sample.Text.Contains("本次操作未执行"))
            { fail++; Console.WriteLine("!! 缺事实前置句\"本次操作未执行\""); }
            if (name == "internal2" && !sample.Text.Contains("可能已完成"))
            { fail++; Console.WriteLine("!! internal(后) 缺\"可能已完成\""); }
            if (name is "badreq" or "nonff" or "remotemoved" or "hasmerge" or "prmergechanged" or "prmergeverify"
                    or "apiredirected"
                    && !sample.Text.Contains("本次未产生任何副作用"))
            { fail++; Console.WriteLine("!! bad_request 家族缺\"未产生副作用\"句"); }
            if (name == "httpfail" && !sample.Text.Contains("无任何副作用"))
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

        Console.WriteLine(fail == 0
            ? "PASS：全部消息可单读终判，exit code 与 §8.3 对齐。"
            : $"FAIL：{fail} 处不符。");
        return fail == 0 ? 0 : 1;
    }
}
