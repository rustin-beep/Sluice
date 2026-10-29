namespace Sluice;

/// <summary>§8.2 全部默认消息。唯一文案源，selftest 按本文件穷举对齐。</summary>
internal sealed record Msg(string Prefix, string Text, int ExitCode, string Retry, bool ToStderr)
{
    public bool IsFinal => Prefix == "[Sluice·终态]";
}

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Internal = 1;
    public const int Denied = 2;
    public const int Timeout = 3;
    public const int PolicyReject = 4;
    public const int Cooldown = 5;
    public const int ConsentFailed = 6;
    public const int SealViolation = 7;
    public const int NotInitialized = 8;
    public const int CredentialExpired = 9;
    public const int BadRequest = 10;
    public const int HttpFailed = 20;
    public const int Unknown = 21;
}

internal static class Messages
{
    // ---- 成功（stdout，exit 0） ----
    public static Msg Success(string op, string target, int httpCode, string url) => new(
        "[Sluice]",
        $"""
        已完成：{op} → {target}（HTTP {httpCode}）
        结果：{url}
        操作已生效，无需再用其他命令验证。
        """,
        ExitCodes.Success, "—", false);

    // ---- denied（exit 2，retry=never） ----
    public static Msg Denied(string op, string target, string preset, string? freeText) => new(
        "[Sluice·终态]",
        $"""
        用户已明确拒绝：{op} → {target}。
        本次操作未执行，GitHub 上没有产生任何变更。
        必须遵守：
        1. 不要重试本操作，也不要用其他命令、API、工具或凭据路径达到相同效果；
        2. 不要尝试获取、刷新或申请任何 GitHub 凭据或授权（包括 gh auth login、device flow、打开浏览器授权页）；
        3. 如认为该操作仍有必要：用一句话向用户说明目的，等待用户明确指示，不要自行重试；
        4. 若任务其余部分不依赖本操作，继续执行其余部分。
        {(freeText is { Length: > 0 } ? $"用户拒绝理由：\"{freeText}\"——请按此理解；retry 语义以上方预设为准。" : "")}
        {PresetGuidance(preset)}
        """,
        ExitCodes.Denied, "never", true);

    /// <summary>§7 拒绝预设每档绑定的“下一步”文案，防 agent 猜。</summary>
    public static string PresetGuidance(string preset) => preset switch
    {
        "目标不对" => "用户补充：目标仓库或对象不正确；请与用户确认正确目标后再提交新请求。",
        "内容需修改" => "用户补充：内容需要修改；请按用户意见修改后重新提交。",
        "我自己处理" => "用户补充：用户将自己完成此操作；不要代为重试或代为准备。",
        "任务重新讨论" => "用户补充：任务需要重新讨论；请先与用户对齐后再决定是否需要此操作。",
        _ => "", // “不批准”：主体文案已完整
    };

    // ---- timeout（exit 3，retry=wait_user） ----
    public static Msg Timeout(string op, string target) => new(
        "[Sluice·终态]",
        $"""
        用户暂离，批准窗口 60 秒无响应，已自动拒绝。
        本次操作未执行，GitHub 上没有产生任何变更。
        1. 不要立即重试（重试只会再次弹窗打扰用户）；
        2. 不要换其他方式完成同一操作；
        3. 继续任务其余部分；收尾汇报时注明："有一项操作待用户批准未执行：{op} → {target}"。
        """,
        ExitCodes.Timeout, "wait_user", true);

    // ---- policy_reject（exit 4，retry=change_approach） ----
    public static Msg PolicyReject(string what) => new(
        "[Sluice·终态]",
        $"""
        该操作被策略永久禁止：{what}。此类操作不存在人工批准途径。
        本次操作未执行。
        1. 任何形式的重试（含变体：--force、--no-verify、改 remote、直连 API）都会被直接拒绝且不弹窗；
        2. 允许的替代：推送到新特性分支（如 feat/<简述>）并创建 PR；
        3. 若确需例外，向用户说明，由用户本人修改策略。
        """,
        ExitCodes.PolicyReject, "change_approach", true);

    // ---- cooldown（exit 5，retry=wait_until） ----
    public static Msg Cooldown(DateTime untilLocal) => new(
        "[Sluice·终态]",
        $"""
        冷却期内：因短时间内多次被拒，Sluice 在 {untilLocal:HH:mm} 前自动拒绝一切写操作（不弹窗）。
        本次操作未执行。
        1. {untilLocal:HH:mm} 前不要提交任何写操作；
        2. 不要尝试任何绕过——环境内没有可用凭据，一切直连写法都会失败；
        3. 可继续只读工作；到点后任务仍需要时，重新提交一次即可。
        """,
        ExitCodes.Cooldown, "wait_until", true);

    // ---- consent_failed（exit 6，retry=wait_user） ----
    public static Msg ConsentFailed(string op, string target) => new(
        "[Sluice·终态]",
        $"""
        用户未通过 Windows Hello（取消或连续失败）。本次操作未执行，无任何变更。
        不要重试；等用户口头让你再试时，重新运行同一命令（{op} → {target}）。
        """,
        ExitCodes.ConsentFailed, "wait_user", true);

    // ---- seal_violation（exit 7，retry=wait_user） ----
    public static Msg SealViolation(string detail) => new(
        "[Sluice·终态]",
        $"""
        环境自查失败：检测到未封堵的 GitHub 凭据（{detail}）。
        为守住"写动作必有批准"的保证，Sluice 在用户处理前拒绝一切写操作。
        本次操作未执行。
        请把本消息原文转告用户，提醒其运行 sluice seal --fix；在此之前停止所有 git/gh 写操作。
        """,
        ExitCodes.SealViolation, "wait_user", true);

    // ---- not_initialized（exit 8，retry=wait_user） ----
    public static Msg NotInitialized() => new(
        "[Sluice]",
        """
        尚未初始化：找不到 Sluice 凭据文件。
        请让用户运行 sluice setup 完成配置；在完成前不要以其他方式执行写操作。
        """,
        ExitCodes.NotInitialized, "wait_user", true);

    // ---- credential_expired（exit 9，retry=wait_user） ----
    public static Msg CredentialExpired(string detail) => new(
        "[Sluice·终态]",
        $"""
        凭据失效或权限不足（{detail}：fine-grained PAT 过期，或目标仓库不在授权列表）。
        请转告用户运行 sluice setup 更新凭据；不要自行处理。
        """,
        ExitCodes.CredentialExpired, "wait_user", true);

    // ---- bad_request（exit 10，retry=after_fix） ----
    public static Msg BadRequest(string detail) => new(
        "[Sluice]",
        $"""
        用法错误：{detail}
        修正参数后重新运行同一命令即可；本次未产生任何副作用。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- http_failed（exit 20，retry=retry_ok；请求未发出） ----
    public static Msg HttpFailed(string reason) => new(
        "[Sluice]",
        $"""
        GitHub 连接失败（请求未发出，无任何副作用）：{reason}。
        可间隔 30 秒后重试同一命令；连续 3 次失败则报告用户。
        """,
        ExitCodes.HttpFailed, "retry_ok", true);

    // ---- unknown（exit 21，retry=wait_user；请求已发出但结果不明） ----
    public static Msg Unknown(string reason) => new(
        "[Sluice·终态]",
        $"""
        GitHub 已收到请求但结果未知（{reason}）。此操作可能已经生效（例如评论可能已发出）。
        1. 不要直接重发——如果已生效，重发就是重复操作；
        2. 先查证：运行 sluice api 查看目标下是否已有此内容，或请用户打开对应页面确认；
        3. 查证后：未生效 → 可重发一次；已生效 → 不要再发。
        """,
        ExitCodes.Unknown, "wait_user", true);

    // ---- internal（exit 1，retry=wait_user；按发生阶段两型） ----
    public static Msg Internal(bool afterExecutionStarted) => new(
        "[Sluice·终态]",
        afterExecutionStarted
            ? """
              Sluice 内部错误（执行开始后）：操作可能已完成，但收尾失败（审计可能缺行）。
              请用户在 GitHub 上核对是否已生效；把上一条命令原文转告用户。
              """
            : """
              Sluice 内部错误（执行开始前），本次操作未执行，无任何变更。
              请把上一条命令原文转告用户。
              """,
        ExitCodes.Internal, "wait_user", true);

    // ---- selftest 数据源：渲染全部消息样例 ----
    public static (string Name, Msg Sample)[] AllSamples() =>
    [
        ("success",   Success("pr_comment", "rustin-beep/Vitro#5", 201, "https://github.com/rustin-beep/Vitro/issues/5#issuecomment-123")),
        ("denied",    Denied("pr_comment", "rustin-beep/Vitro#5", "内容需修改", "别带密钥")),
        ("timeout",   Timeout("pr_comment", "rustin-beep/Vitro#5")),
        ("policy",    PolicyReject("push → 受保护分支 main / force push / 修改 .github/workflows")),
        ("cooldown",  Cooldown(DateTime.Today.AddMinutes(10))),
        ("consent",   ConsentFailed("pr_comment", "rustin-beep/Vitro#5")),
        ("seal",      SealViolation("凭据管理器：git:https://github.com")),
        ("notinit",   NotInitialized()),
        ("credexp",   CredentialExpired("HTTP 401")),
        ("badreq",    BadRequest("pr_comment 缺少 --number")),
        ("httpfail",  HttpFailed("could not resolve host / TLS 握手失败")),
        ("unknown",   Unknown("超时/连接中断/HTTP 5xx")),
        ("internal1", Internal(afterExecutionStarted: false)),
        ("internal2", Internal(afterExecutionStarted: true)),
    ];

    public static void Write(Msg m)
    {
        var full = m.Prefix + " " + m.Text.Replace("\n", Environment.NewLine);
        var tw = m.ToStderr ? Console.Error : Console.Out;
        tw.WriteLine(full);
    }
}
