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
                && !sample.Text.Contains("本次操作未执行"))
            { fail++; Console.WriteLine("!! 缺事实前置句\"本次操作未执行\""); }
            if (name == "internal2" && !sample.Text.Contains("可能已完成"))
            { fail++; Console.WriteLine("!! internal(后) 缺\"可能已完成\""); }
            if (name == "badreq" && !sample.Text.Contains("本次未产生任何副作用"))
            { fail++; Console.WriteLine("!! bad_request 缺\"未产生副作用\"句"); }
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
