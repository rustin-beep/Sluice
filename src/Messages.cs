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

    // ---- push 无需推送（exit 0；远端已是目标提交） ----
    public static Msg PushUpToDate(string repo, string branch, string sha8) => new(
        "[Sluice·终态]",
        $"""
        已确认：{repo}@{branch} 已指向 {sha8}，本次无需推送；远端没有产生任何变更。
        无需重试，也无需再用其他命令验证。
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

    // ---- timeout（exit 3，retry=wait_user；时长自 C4 config 可配，文案跟随实际值） ----
    public static Msg Timeout(string op, string target, int seconds) => new(
        "[Sluice·终态]",
        $"""
        用户暂离，批准窗口 {seconds} 秒无响应，已自动拒绝。
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
        2. 允许的替代：rebase 到新基点后加 --base origin/<新基点> 走重放式更新（弹窗明示替换内容与丢弃清单），
           或推送到新特性分支（如 feat/<简述>）并创建 PR；
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

    // ---- api 读通道（§9）：GET 专用三态；stdout 是响应体数据，消息一律走 stderr 不污染 ----

    // GET 超时：请求可能已送达，但 GET 幂等、无副作用——归 http_failed(20/retry_ok) 家族。
    // §8.2 规定 exit 21 是"非幂等 POST 的专属档"，读通道不得借用。
    public static Msg ApiTimeout(string path) => new(
        "[Sluice]",
        $"""
        api GET {path} → 请求超时（可能已送达；GET 无副作用，重试不会造成重复效果）。
        本次未产生任何副作用；可间隔 30 秒后重试同一命令，连续 3 次失败则报告用户。
        """,
        ExitCodes.HttpFailed, "retry_ok", true);

    // 匿名读 401/403：资源需要认证——agent 可自行修正（加 --auth 重跑，届时弹 Hello）
    public static Msg ApiAuthRequired(string path, int st) => new(
        "[Sluice]",
        $"""
        api GET {path} → HTTP {st}：资源需要认证（私有或受限），匿名读不可用。
        本次未产生任何副作用（响应体已输出到 stdout）。
        修正：加 --auth 参数重新运行同一命令（将弹出 Windows Hello 验证身份后带 token 重读）。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // 其余非 2xx（404/429/…）：HTTP 层面已应答，事实在响应体
    public static Msg ApiHttpError(string path, int st, int bytes) => new(
        "[Sluice]",
        $"""
        api GET {path} → HTTP {st}（{bytes} B，响应体已输出到 stdout）。
        本次未产生任何副作用。404 = 资源不存在或路径有误；429 = 触发限流，稍等再试；
        其余请读响应体的 message 字段。修正路径或参数后重试。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- vault 与 Hello 密钥不配套（exit 8，retry=wait_user；区别于 consent_failed 的"用户取消"） ----
    public static Msg VaultBroken(string detail) => new(
        "[Sluice·终态]",
        $"""
        vault 状态损坏：{detail}。
        本次操作未执行，无任何变更；重试同一命令不会好转。
        请让用户重新运行 sluice setup 重建 vault 与 Hello 密钥的配对；在此之前 sluice 的写操作不可用。
        """,
        ExitCodes.NotInitialized, "wait_user", true);

    // ---- pr_create 被服务端 422 拒绝（exit 10，retry=after_fix；原样重跑必再被拒） ----
    public static Msg PrRejected(string repo, string head, string detail)
    {
        var owner = repo.Contains('/') ? repo[..repo.IndexOf('/')] : "owner";
        return new Msg(
            "[Sluice·终态]",
            $"""
            pr_create 被服务端拒绝（{detail}）。最常见原因：该 head→base 的 PR 已存在，或两分支间没有提交差异。
            本次操作未执行，GitHub 上没有产生任何变更。
            1. 不要原样重跑同一命令（同样的参数仍会被拒绝）；
            2. 先查已有 PR：sluice api "repos/{repo}/pulls?head={owner}:{head}" --auth；
            3. 已存在 → 使用已存在的那个 PR；确需新 PR → 更换 head 分支或先产生新提交后再提交。
            """,
            ExitCodes.BadRequest, "after_fix", true);
    }

    // ---- pr_merge 被服务端拒绝（exit 10，retry=after_fix；405/409/422 服务端明确拒） ----
    public static Msg PrMergeRejected(string target, string detail) => new(
        "[Sluice·终态]",
        $"""
        pr_merge 被服务端拒绝（{detail}）。常见原因：PR 不可合并（冲突未解 / required checks 未过 / 已被合并 / draft 状态）。
        本次操作未执行，GitHub 上没有产生任何变更。
        1. 先查可合并状态：sluice api "repos/<owner/repo>/pulls/<number>" --auth，读 "mergeable" 与 "mergeable_state" 字段；
        2. 冲突未解 → 解冲突推送后重试；checks 未过 → 等待或修复；已被合并 → 使用既成结果，不要再合并；
        3. 修正后重跑同一命令。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- pr_merge 403（exit 10，retry=after_fix；2026-10-30 评审拆分：保护/权限拒绝 ≠ 凭据过期，
    //      原归 9 会把 agent 引向重新 setup 的死路） ----
    public static Msg PrMergeForbidden(string target, string detail) => new(
        "[Sluice·终态]",
        $"""
        pr_merge 被服务端拒绝（{detail}）。常见原因：目标分支受保护（required checks 未过 / 禁用该合并方式）或 token 无合并权限。
        本次操作未执行，GitHub 上没有产生任何变更。
        1. 查状态：sluice api "repos/<owner/repo>/pulls/<number>" --auth，读 "mergeable_state" 字段；
        2. 分支保护与权限属仓库策略，不要尝试绕过；确需例外请转告用户处理；
        3. 若服务端 message 指向 token 权限（如 Resource not accessible），同样转告用户重新 setup——不要自行获取或更换凭据。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- pr_merge 404（exit 10，retry=after_fix；2026-10-30 评审：404 混入"不可合并"文案误导） ----
    public static Msg PrMergeNotFound(string target) => new(
        "[Sluice·终态]",
        $"""
        pr_merge 被服务端拒绝：{target} 不存在（HTTP 404）。编号有误、PR 已被删除，或已在别处合并后关闭。
        本次操作未执行，GitHub 上没有产生任何变更。
        修正：核对编号后重跑；列出当前 PR：sluice api "repos/<owner/repo>/pulls"；若 PR 已在别处合并则使用既成结果，不要再发起合并。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- pr_merge head 保真复核命中（exit 10，retry=after_fix；2026-10-30 评审 TOCTOU 加固：
    //      批准→执行间 head 被推进即中止，与 push 的 HeadMoved 同族） ----
    public static Msg PrMergeChanged(string target, string was8, string now8) => new(
        "[Sluice]",
        $"""
        pr_merge 未执行：批准期间 PR head 已变化（{was8} → {now8}），弹窗展示的内容已过期。
        本次未产生任何副作用（GitHub 上没有产生任何变更）。
        修正：重新运行同一命令——将以当前内容重新弹窗征求意见。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- pr_merge 合并前复核通道失败（exit 20，retry=retry_ok；复核 GET 无副作用、merge 未发出） ----
    public static Msg PrMergeVerifyFailed(string reason) => new(
        "[Sluice]",
        $"""
        pr_merge 未执行：合并前内容复核未完成（{reason}）。
        本次未产生任何副作用（复核用 GET 无副作用，合并请求未发出）。
        可间隔 30 秒后重试同一命令（将重新弹窗并重新复核）；连续 3 次失败则报告用户。
        """,
        ExitCodes.HttpFailed, "retry_ok", true);

    // ---- branch_delete 403（exit 10，retry=after_fix；2026-10-30 评审拆分：保护分支/无删除权限 ≠ 凭据过期） ----
    public static Msg BranchDeleteForbidden(string repo, string branch, string detail) => new(
        "[Sluice·终态]",
        $"""
        branch_delete 被服务端拒绝（{detail}）。常见原因：分支受保护或 token 无删除权限。
        本次操作未执行，GitHub 上没有产生任何变更（ref 未被动过）。
        受保护分支的删除属仓库策略，不要尝试绕过；确需删除请转告用户在仓库设置中处理。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- pr merge 成功但 --delete-head 失败（紧跟 Success 输出的附注；exit 0 不变——主操作已生效） ----
    public static Msg BranchDeleteFailed(string repo, string branch, string why) => new(
        "[Sluice]",
        $"""
        附注：PR 已合并，但 head 分支删除失败（{why}）。
        请修正后用 sluice branch delete --repo {repo} --branch {branch} 单独删除。
        """,
        ExitCodes.Success, "after_fix", true);

    // ---- push 非 ff（exit 10，retry=after_fix；2026-10-30 实战改档：非 ff 可由 agent 自行
    //      rebase 修复后重推同一分支，属"修正后可重试"而非"策略永禁"——原 policy_reject 定性不准，
    //      且指引缺 rebase 正解导致实战绕三圈。v2.6：末行"force 永禁"改写为指向重放通道——
    //      旧表述与新通道矛盾；裸 force（--force 参数、未声明 --base 的覆盖）仍永禁（§9.6）。 ----
    public static Msg PushNonFF(string repo, string branch, string remoteSha8) => new(
        "[Sluice]",
        $"""
        push 未执行：远端 {repo}@{branch}（头 {remoteSha8}）含本地没有的提交，非 fast-forward。
        本次未产生任何副作用（GitHub 上没有产生任何变更）。
        修正：git fetch 后把本地分支 rebase 到远端头（git rebase origin/{branch}；解冲突后 git rebase --continue），
        再重跑同一命令——rebase 后的线性历史可正常推送。
        若你已把分支 rebase 到新基点（如更新后的主干）、需原地更新同一 PR：加 --base origin/<新基点> 重跑同一命令，
        走重放式更新通道——弹窗明示旧头→新头与丢弃的远端提交清单，批准后替换。
        注意：--force 参数与任何未声明 --base 的覆盖推送仍被策略永禁。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- push 重放式更新：执行前远端头复核失败（exit 10，retry=after_fix；v2.6 §9.6 闸门④。
    //      批准→执行间远端头被推进（或分支被删）即中止——静默 force 会孤儿化窗口外的新提交；
    //      与 HeadMoved / PrMergeChanged 同族）。 ----
    public static Msg PushRemoteMoved(string repo, string branch, string detail) => new(
        "[Sluice]",
        $"""
        push 未执行：{repo}@{branch} {detail}。
        本次未产生任何副作用（GitHub 上没有产生任何变更）。
        修正：git fetch（若分支已被删除加 --prune）后重跑同一命令——将以当前远端状态重新弹窗，
        重新展示替换对象与丢弃清单。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- push 含 merge 提交（exit 10，retry=after_fix；列出问题提交 + rebase 正解。
    //      原「请用完整 git 推送」是死胡同指引——seal 封堵后环境无 git 推送凭据，删除（2026-10-30 实战）。 ----
    public static Msg PushHasMerge(string merges) => new(
        "[Sluice]",
        $"""
        push 未执行：推送范围含 merge 提交（多父提交），REST push 只支持线性历史。
        问题提交：{merges}
        本次未产生任何副作用（GitHub 上没有产生任何变更）。
        修正：把分支 rebase 成线性历史（git rebase <远端基点>；解冲突后 git rebase --continue，
        冲突解决会作为普通提交重放，无需保留 merge 提交），再重跑同一命令。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- selftest 数据源：渲染全部消息样例 ----
    public static (string Name, Msg Sample)[] AllSamples() =>
    [
        ("success",   Success("pr_comment", "rustin-beep/Vitro#5", 201, "https://github.com/rustin-beep/Vitro/issues/5#issuecomment-123")),
        ("pushuptodate", PushUpToDate("rustin-beep/Vitro", "feat/x", "9f3c2a1b")),
        ("denied",    Denied("pr_comment", "rustin-beep/Vitro#5", "内容需修改", "别带密钥")),
        ("timeout",   Timeout("pr_comment", "rustin-beep/Vitro#5", 60)),
        ("policy",    PolicyReject("push → 受保护分支 main / --force 参数 / 修改 .github/workflows")),
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
        ("apitimeout", ApiTimeout("repos/rustin-beep/Sluice/pulls")),
        ("apiauth",    ApiAuthRequired("repos/rustin-beep/Sluice/issues", 403)),
        ("apihttp",    ApiHttpError("repos/rustin-beep/Sluice/issues/999", 404, 122)),
        ("vaultbroken", VaultBroken("token.bin 存在，但 Hello 密钥 SluiceVault 缺失")),
        ("prrejected", PrRejected("rustin-beep/Sluice", "dev", "HTTP 422：A pull request already exists")),
        ("prmergerejected", PrMergeRejected("rustin-beep/Sluice#383", "HTTP 405：Pull Request is not mergeable")),
        ("prmergeforbidden", PrMergeForbidden("rustin-beep/Sluice#383", "HTTP 403：Merge commit is blocked by required status checks")),
        ("prmergenotfound", PrMergeNotFound("rustin-beep/Sluice#404")),
        ("prmergechanged", PrMergeChanged("rustin-beep/Sluice#383", "9f3c2a1b", "4d81e0c7")),
        ("prmergeverify", PrMergeVerifyFailed("复核请求超时")),
        ("branchdeleteforbidden", BranchDeleteForbidden("rustin-beep/Sluice", "release/1.x", "HTTP 403：Resource not accessible by personal access token")),
        ("nonff",  PushNonFF("rustin-beep/Sluice", "feat/demo-motion-d0d1-r2", "b9a84c5b")),
        ("remotemoved", PushRemoteMoved("rustin-beep/Sluice", "feat/x", "批准期间远端头已变化（规划期 b9a84c5b → 现在 4d81e0c7），弹窗展示的替换对象已过期")),
        ("hasmerge", PushHasMerge("9f3c2a1b Merge branch 'dev'（共 2 个）")),
    ];

    public static void Write(Msg m)
    {
        var full = m.Prefix + " " + m.Text.Replace("\n", Environment.NewLine);
        var tw = m.ToStderr ? Console.Error : Console.Out;
        tw.WriteLine(full);
    }
}
