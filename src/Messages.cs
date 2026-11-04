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
    public const int RateLimitWait = 22;
    public const int PendingGate = 23; // v2.25：MCP 前门"批准窗未决"——人还在决策，非终态
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

    // ---- push 成功（exit 0；v2.10：REST 推送不回写本地 origin/{branch} 引用——成功消息必附
    //      fetch 提示，掐掉"首次成功 → rebase → 再推必踩 NonFF/ReplayUnresolved"这条确定性路径；
    //      v2.14：skipped>0 时附去重行——远端已有未重传的 blob 数一目了然） ----
    public static Msg PushDone(string repo, string branch, string newSha, bool replay, int skipped = 0) => new(
        "[Sluice]",
        $"""
        已完成：push → {repo}@{branch}（HTTP 200{(replay ? "，重放式更新" : "")}）
        结果：https://github.com/{repo}/commit/{newSha}
        {(skipped > 0 ? $"去重：{skipped} 个 blob 远端默认分支已有，未重传。\n" : "")}注意：本地 origin/{branch} 未更新（REST 推送不回写本地引用）——后续 rebase / 重推同分支前先 git fetch origin {branch}。
        操作已在远端生效，无需再用其他命令验证。
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
    // v2.24：自由文本理由以 DenyReasonOpen/Close 定界——McpServer.ExtractDenyReason 据此把
    // deny_reason 提进 structuredContent（机器可读，防宿主把 isError 结果按错误渲染后理由沉没）。
    public const string DenyReasonOpen = "用户拒绝理由：\"";
    public const string DenyReasonClose = "\"——请按此理解";

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
        {(freeText is { Length: > 0 } ? $"{DenyReasonOpen}{freeText}{DenyReasonClose}；retry 语义以上方预设为准。" : "")}
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

    // ---- pending（exit 23，retry=wait_until；v2.25 MCP 前门专用，CLI 不产此码） ----
    // 缘起 2026-11-04 实弹：宿主客户端 ~30s 放弃工具调用，而人读窗+打拒绝理由要 40-90s——
    // 终态永远落在客户端离场之后，deny_reason 一世沉没（agent"从不记忆"的根源）。
    // 服务端在 mcp_pending_sec（默认 15s，< 一切已知客户端超时）未决即回本消息 + receipt，
    // 子进程（含活窗）继续跑，终态落审计凭 receipt 可查。非终态（前缀 [Sluice]）。
    public static Msg PendingGate(string receipt, int pollAfterSec, int settleBySec) => new(
        "[Sluice]",
        $"""
        批准窗口已弹出，等待用户决策（最长 {settleBySec} 秒内必有终态：批准执行 / 拒绝 / 超时自动拒绝）。
        本次调用不再阻塞等待——约 {pollAfterSec} 秒后用工具 sluice_status 查终态（参数 receipt：{receipt}；
        无该工具时 CLI 等价：sluice status {receipt}），即可查得终态（含用户拒绝理由原文）。结果未定前：
        1. 禁止重发同一写操作（窗口已开，重发只会再弹一窗，不是重试途径）；
        2. 禁止换其他方式完成同一操作；
        3. 查询无副作用，可间隔重查直至出现 [Sluice·终态]。
        """,
        ExitCodes.PendingGate, "wait_until", false);

    /// <summary>v2.26：MCP server 映像过期（磁盘 exe 已更新，本进程是 rename-swap 部署后的僵尸）。
    /// 旧语义会静默丢终态（2026-11-04 实弹：四个 v2.24 旧 server 继续服务，拒绝理由随宿主超时沉没），
    /// 必须显式自曝换重启，不得继续按旧语义服务写门。</summary>
    public static Msg ServerStale() => new(
        "[Sluice·终态]",
        """
        Sluice MCP 服务器已过期：磁盘上的 sluice.exe 已被更新，本进程仍在按旧版语义运行——
        继续使用会丢失用户决策结果（含拒绝理由）。
        本次调用未执行，无任何副作用。
        请把本消息原文转告用户：重启本会话（或重连 MCP server）后重新发起同一操作即可。
        """,
        ExitCodes.Internal, "wait_user", true);

    /// <summary>status 查询：receipt 尚无终态行（窗口仍开着或人仍在打字）——继续等继续查。</summary>
    public static Msg StatusPending(string receipt) => new(
        "[Sluice]",
        $"""
        {receipt} 尚无终态：批准窗口仍未决（用户可能在阅读或填写拒绝理由）。
        再等 15 秒后重跑同一查询；终态必然落盘（最长至批准窗超时）。查询无副作用。
        """,
        ExitCodes.PendingGate, "wait_until", false);

    /// <summary>status 查得已批准族终态（approved / up_to_date 等）——事实句照审计 result 原样。</summary>
    public static Msg StatusDone(string op, string target, string result) => new(
        "[Sluice·终态]",
        $"""
        终态已查得：{op} → {target} 已获批准并执行。
        审计结果：{result}
        操作已生效，无需再用其他命令验证；也无需再查 status。
        """,
        ExitCodes.Success, "—", false);

    /// <summary>status 查得其余终态（denied/timeout 之外的技术失败族）——事实照录，语义按决策字面。</summary>
    public static Msg StatusOther(string op, string target, string decision, string result) => new(
        "[Sluice·终态]",
        $"""
        终态已查得：{op} → {target}
        决策：{decision}
        审计结果：{result}
        该结果不在 denied/timeout/approved 三族内——按决策字面处理（结果未知族交回用户，勿重发）。
        """,
        ExitCodes.Unknown, "wait_user", true);

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

    // ---- notice credential helper（v2.19，§9 消息型 helper）：git 索要凭据的那一刻拦下并指路。
    //      不持凭据不供凭据（stdout 永不输出 username=/password=），只打 stderr 文案后非零退出。
    //      exit 4 / change_approach：与 policy_reject 同族——"直推 git"这条路被永久禁止，消息点名允许的替代。 ----
    public static Msg Notice(string host) => new(
        "[Sluice·终态]",
        $"""
        git 向 {host} 索要凭据被拒：本环境的 GitHub 凭据只存在 Sluice 闸门内，git 直连拿不到任何凭据。
        1. push 场景：改用 sluice push --remote origin --refspec <分支>——将弹出批准窗口由用户决定（不要加 --force）；
        2. 不要尝试 gh auth login、device flow、浏览器授权或任何获取/写入凭据的方式；
        3. fetch/pull 公开库无需凭据不受影响；私有库只读请让用户处理（sluice api 可查，读通道无需批准）。
        """,
        ExitCodes.PolicyReject, "change_approach", true);

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

    // ---- api 403 匿名配额耗尽（exit 10，retry=after_fix；v2.10：原实现 403 一律归"资源需要
    //      认证（私有或受限）"——诊断分类 bug，配额与仓库可见性无关。2026-10-31 实测
    //      "API rate limit exceeded for <ip>" 被误报为私有，逼 agent 换路） ----
    public static Msg ApiRateLimitedAnon(string path, int st) => new(
        "[Sluice]",
        $"""
        api GET {path} → HTTP {st}：匿名读配额耗尽（GitHub rate limit），与仓库是否私有无关。
        本次未产生任何副作用（响应体已输出到 stdout）。
        修正：加 --auth 参数重新运行同一命令——认证请求走独立且更高的配额，不受匿名额度影响。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- api 403 限流·等待档（exit 22，retry=wait_until；v2.10：带 token 的 primary 耗尽与
    //      secondary/abuse 拦截原归 credential_expired，把"等 N 分钟"开成"重新 setup"死方子。
    //      exit 22 新档：v2.9 函数性不变量下 wait_until 不能寄生在 20（retry_ok）上——selftest 当场拦下） ----
    public static Msg ApiRateLimitedWait(string path, int st, string what) => new(
        "[Sluice]",
        $"""
        api GET {path} → HTTP {st}：GitHub 限流（{what}）——凭据与权限均无问题，不要重新 setup、不要换路径绕行。
        本次未产生任何副作用（响应体已输出到 stdout）。
        修正：按上述窗口等待后重新运行同一命令；期间不要密集重试（会延长拦截）。
        """,
        ExitCodes.RateLimitWait, "wait_until", true);

    // ---- 写通道 403/429 限流（exit 22，retry=wait_until；v2.16：api 读通道 v2.10 同族扩到写通道
    //      ——POST/PATCH 密集创建恰是 secondary 高发面，被诊成"无写权限/凭据失效"会引向改参
    //      立即重试或重发 PAT，密集重试反而延长封锁窗） ----
    public static Msg WriteRateLimited(string op, string target, int st, string what) => new(
        "[Sluice·终态]",
        $"""
        {op} → {target} 被限流拦截（HTTP {st}）：{what}——凭据与权限均无问题，不要重新 setup、不要改参数立即重试。
        本次操作未执行，GitHub 上没有产生任何变更。
        修正：按上述窗口等待后重新运行同一命令；期间不要密集重试（会延长拦截）。
        """,
        ExitCodes.RateLimitWait, "wait_until", true);

    // ---- push 序列限流（exit 22，retry=wait_until；v2.16：ref 预查/序列中段撞 403 secondary/429
    //      ——原 ref 预查 403 一律 credential_expired 死方子。不谎称零副作用：已传 blob 是服务端
    //      内容寻址对象，但分支未动、无可见变更、重传自动去重） ----
    public static Msg PushRateLimited(string repo, string branch, string what) => new(
        "[Sluice·终态]",
        $"""
        push → {repo}@{branch} 被限流拦截：{what}——凭据与权限均无问题，不要重新 setup。
        分支未动（update-ref 是唯一写点，未到达或已被拒）；已上传的 blob/tree 是内容寻址幂等对象，无引用不可见，重传自动去重。
        修正：按上述窗口等待后重新运行同一命令；期间不要密集重试（会延长拦截）。
        """,
        ExitCodes.RateLimitWait, "wait_until", true);

    // ---- 写 op 403 通用禁止档（exit 10，retry=after_fix；v2.16：pr_comment/pr_create 的 403 原与
    //      401 在 GitHubApi 层混归 Unauthorized→credential_expired 死路；v2.5 已为 pr_merge/
    //      branch_delete 拆过同款，本批补齐） ----
    public static Msg WriteForbidden(string op, string target, string detail) => new(
        "[Sluice·终态]",
        $"""
        {op} → {target} 被服务端拒绝（HTTP 403）：{detail}。
        403 = 权限/策略拒（PAT 缺对应写权限、分支保护、组织策略），不是凭据失效——重新 setup 是死路。
        本次操作未执行，GitHub 上没有产生任何变更。
        修正：对照 README 的 PAT 权限矩阵核对后重跑同一命令；若属分支保护/组织策略，需用户调整设置。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- pr_comment 被拒（exit 10，retry=after_fix；v2.16：原经 InternalPost 谎记 internal——
    //      服务端 404/422 拒绝不是 sluice 内部错误） ----
    public static Msg CommentRejected(string target, string detail) => new(
        "[Sluice·终态]",
        $"""
        pr_comment → {target} 被服务端拒绝：{detail}。常见：编号不存在（404）/ 422 校验拒。
        本次操作未执行，GitHub 上没有产生任何变更。
        修正：核对 --repo 与 --number（issues API 对 PR 编号同样应答，两者都可评论）后重跑。
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

    // ---- push 重放前置缺口（exit 10，retry=after_fix；v2.10 §9.6 闸门⑤。2026-10-31 实战：
    //      worktree 新建分支首推成功（REST 不回写本地引用）→ rebase 后带 --base 重推 →
    //      规划期无 origin/{branch} 未入重放 → 静默落 NonFF，开出"加 --base"这条已满足的死指令。
    //      丢弃清单需要本地对象，fetch 是销毁侧披露的价格——正解唯一且 agent 可自行完成） ----
    public static Msg PushReplayUnresolved(string repo, string branch) => new(
        "[Sluice]",
        $"""
        push 未执行：远端 {repo}@{branch} 已存在且已声明 --base，但本地没有 origin/{branch} 引用——规划期按"新建分支"入窗，与远端现状不符；丢弃的远端提交清单需要本地对象，无法构造重放披露。
        本次未产生任何副作用（GitHub 上没有产生任何变更）。
        修正：git fetch origin {branch} 后重新运行同一命令（--base 保持不变）——届时走重放式更新通道，弹窗明示旧头→新头与丢弃清单，批准后替换。
        背景：REST 推送不回写本地 origin/{branch} 引用，首次推送成功后本地引用仍缺——属预期路径而非异常。
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

    // ---- push 基点不在远端（exit 10，retry=after_fix；v2.12 缺陷一主案：校验前移到任何上传之前，
    //      失败零上传零副作用——原在 blob 全传完后才炸并谎报"内部错误/可能已完成"） ----
    public static Msg PushBaseMissing(string detail) => new(
        "[Sluice]",
        $"""
        push 未执行：{detail}。
        本次未产生任何副作用（分支未动；基点校验先于一切对象上传）。
        修正：先把基点所在分支推上远端（或等其所在 PR 合并），或改用远端已有的祖先提交作 --base，然后重跑同一命令。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- push 超限（exit 10，retry=after_fix；v2.12 前置到规划期——不烧批准；执行期保留为范围收窄后的兜底） ----
    public static Msg PushTooLarge(string detail) => new(
        "[Sluice]",
        $"""
        push 未执行：{detail}。
        本次未产生任何副作用。
        修正：分批推送——先以 --base 指向更早的远端基点推前一段，git fetch 后再推剩余段（fast-forward）；或精简历史后重跑同一命令。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- push 序列步骤被服务端拒绝（exit 10，retry=after_fix；v2.12 缺陷三：原 Other→Internal(1)
    //      无 Reason 槽位、wait_user——把一批 after_fix 可自修错误错标成内部错误。现渲染服务端原话；
    //      "分支未动"是可证事实（update-ref 是唯一写点且未执行） ----
    public static Msg PushStepRejected(string? detail) => new(
        "[Sluice]",
        $"""
        push 未执行：推送序列被服务端拒绝（{detail ?? "HTTP 非 2xx"}）。
        本次未产生任何副作用：分支未动（update-ref 未执行）；已上传对象为内容寻址孤儿（无引用、不可见，服务端将回收）。
        修正：按上方服务端原话处理（权限/保护类 → 转告用户；校验类 → 核对参数）后重跑同一命令。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- push blob sha 不符（exit 20，retry=retry_ok；v2.12：内容寻址对象同内容必同 sha——不符
    //      = 服务端异常窗（Vitro 4×blob-500 同族）；分支未动、blob 幂等重传安全） ----
    public static Msg PushBlobMismatch(string detail) => new(
        "[Sluice]",
        $"""
        push 未执行：{detail}——内容寻址对象同内容必同 sha，属服务端异常（重试安全）。
        本次无任何副作用：分支未动；blob 为幂等对象。
        可间隔 30 秒后重试同一命令（将重新弹窗批准）；连续 3 次失败则报告用户。
        """,
        ExitCodes.HttpFailed, "retry_ok", true);

    // ---- pr reopen 被服务端拒绝（exit 10，retry=after_fix；403/404/422 透传服务端 message。
    //      2026-10-31 Vitro 复盘 #2：PR 误关后只能开新 PR，讨论上下文断链——reopen 是正解，
    //      也是 CI 卡死时比 workflow dispatch 更常用的补救（reopened 是 pull_request 默认触发 type）） ----
    public static Msg PrReopenRejected(string target, string detail) => new(
        "[Sluice·终态]",
        $"""
        pr_reopen 被服务端拒绝（{detail}）。常见原因：PR 已是开放状态；head 分支已被删除；PR 已合并；token 无 Pull requests 写权限。
        本次操作未执行，GitHub 上没有产生任何变更。
        1. 已开放/已合并 → 使用既成状态，不要重跑本命令；
        2. head 分支已删 → 先 push 重建同名分支（--base 给基点），再重跑本命令；
        3. 查状态：sluice api "repos/<owner/repo>/pulls/<number>" --auth 读 state / head.ref / merged 字段。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- issue close 被服务端拒绝（exit 10，retry=after_fix；2026-10-31 Vitro 复盘 #3：
    //      销案闭环——评论留证之后能落锤，不再"关闭等手动"） ----
    public static Msg IssueCloseRejected(string target, string detail) => new(
        "[Sluice·终态]",
        $"""
        issue_close 被服务端拒绝（{detail}）。常见原因：编号是 PR 而非 issue；token 无 Issues 写权限；编号不存在。
        本次操作未执行，GitHub 上没有产生任何变更。
        修正：核对编号与类型后重跑；PR 的生命周期走 pr merge / pr reopen / branch delete，不走 issue close。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- issue create 被服务端拒绝（exit 10，retry=after_fix；v2.11 生命周期对称批：立案闭环） ----
    public static Msg IssueCreateRejected(string target, string detail) => new(
        "[Sluice·终态]",
        $"""
        issue_create 被服务端拒绝（{detail}）。常见原因：token 无 Issues 写权限；仓库不存在或不可写；422 校验（标题为空等）。
        本次操作未执行，GitHub 上没有产生任何变更。
        1. 权限缺失 → 转告用户在 fine-grained PAT 开 Issues: Read and write 后重跑 sluice setup；不要自行获取或更换凭据；
        2. 404 → 核对仓库全名后重跑；
        3. 修正后重跑同一命令（title/body 可调整）。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- pr close 被服务端拒绝（exit 10，retry=after_fix；v2.11：pr_reopen 的镜像） ----
    public static Msg PrCloseRejected(string target, string detail) => new(
        "[Sluice·终态]",
        $"""
        pr_close 被服务端拒绝（{detail}）。常见原因：PR 已是关闭状态或已合并；token 无 Pull requests 写权限；编号不存在。
        本次操作未执行，GitHub 上没有产生任何变更。
        1. 已关闭/已合并 → 使用既成状态，不要重跑本命令；
        2. 查状态：sluice api "repos/<owner/repo>/pulls/<number>" --auth 读 state / merged 字段。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- issue reopen 被服务端拒绝（exit 10，retry=after_fix；v2.11：issue_close 的镜像） ----
    public static Msg IssueReopenRejected(string target, string detail) => new(
        "[Sluice·终态]",
        $"""
        issue_reopen 被服务端拒绝（{detail}）。常见原因：编号是 PR 而非 issue；token 无 Issues 写权限；编号不存在；issue 被锁定（422 原话）。
        本次操作未执行，GitHub 上没有产生任何变更。
        修正：核对编号与类型后重跑；PR 的生命周期走 pr merge / pr reopen，不走 issue reopen。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- issue label 被服务端拒绝（exit 10，retry=after_fix；v2.17 标签面批：整组替换被拒，
    //      既有标签原样保留——set 语义的对称事实句） ----
    public static Msg IssueLabelRejected(string target, string detail) => new(
        "[Sluice·终态]",
        $"""
        issue_label 被服务端拒绝（{detail}）。常见原因：标签名在仓库不存在（据报 404）；编号不存在；token 无 Issues 写权限。
        本次操作未执行，GitHub 上没有产生任何变更——目标当前标签原样保留。
        1. 未知名 → 先建标签（网页/gh label create，sluice 本批不做标签实体管理）或修正拼写后重跑；
        2. PR 编号同样可挂标签（端点共用）——被拒时先核对权限而非类型；
        3. 查现状：sluice api "repos/<owner/repo>/issues/<number>" --auth 读 labels 字段。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- pr edit 被服务端拒绝（exit 10，retry=after_fix；v2.11：勘误闭环。旧值未被动过） ----
    public static Msg PrEditRejected(string target, string detail) => new(
        "[Sluice·终态]",
        $"""
        pr_edit 被服务端拒绝（{detail}）。常见原因：token 无 Pull requests 写权限；编号不存在；422 校验（新标题为空等）。
        本次操作未执行，GitHub 上没有产生任何变更——PR 的旧标题/正文原样保留。
        修正：核对参数后重跑同一命令。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- pr review 被服务端拒绝（exit 10，retry=after_fix；v2.11：APPROVE 自批禁令在此说破） ----
    public static Msg PrReviewRejected(string target, string detail) => new(
        "[Sluice·终态]",
        $"""
        pr_review 被服务端拒绝（{detail}）。常见原因：APPROVE 自己账户的 PR（GitHub 服务端禁止——agents 与 owner 同账户时 APPROVE 仅对他人 PR 有效）；token 无 Pull requests 写权限；PR 已关闭（服务端拒新评审）；422 校验。
        本次操作未执行，GitHub 上没有产生任何变更。
        1. 自批 → 改用 REQUEST_CHANGES / COMMENT 表达意见，或转告用户在网页上批准；
        2. PR 已关闭 → 用 sluice pr reopen 复活后重试，或用 sluice comment 补充意见；
        3. 查状态：sluice api "repos/<owner/repo>/pulls/<number>" --auth 读 state / merged 字段。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- tag create 被服务端拒绝（exit 10，retry=after_fix；v2.13 发版面批） ----
    public static Msg TagRejected(string target, string detail) => new(
        "[Sluice·终态]",
        $"""
        tag_create 被服务端拒绝（{detail}）。常见原因：tag 已存在（Reference already exists——使用既有 tag，勿重试；sluice 不提供 tag 移动/覆盖）；token 无 Contents 写权限；tag 名含非法字符；仓库不存在或不可写。
        本次操作未执行，GitHub 上没有产生任何变更。
        修正：已存在 → 使用既有 tag；权限缺失 → 转告用户在 PAT 开 Contents: Read and write 后重跑 sluice setup；名称问题 → 换合法 tag 名后重跑同一命令。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- release create 被服务端拒绝（exit 10，retry=after_fix；v2.13 发版面批） ----
    public static Msg ReleaseRejected(string target, string detail) => new(
        "[Sluice·终态]",
        $"""
        release_create 被服务端拒绝（{detail}）。常见原因：该 tag 已有 release（already_exists——用 sluice api "repos/<owner/repo>/releases" 查既有 release 使用之；确需重发请先在网页删除旧 release 或换新 tag）；token 无 Contents 写权限；仓库不存在或不可写。
        本次操作未执行，GitHub 上没有产生任何变更（未发通知、未触发 webhook）。
        修正：按对应项处理后重跑同一命令；不要自行获取或更换凭据。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- workflow dispatch 已受理（exit 0；HTTP 204 无响应体——受理即完成，run 创建是异步的） ----
    public static Msg WorkflowDispatched(string repo, string wf, string rf) => new(
        "[Sluice]",
        $"""
        已完成：workflow_dispatch → {repo} · {wf} @ {rf}（HTTP 204）
        触发事件已受理，无需重发；run 通常数秒内创建（https://github.com/{repo}/actions）。
        如需观察进度（非必须验证）：sluice api "repos/{repo}/actions/runs?event=workflow_dispatch&per_page=1" --auth。
        """,
        ExitCodes.Success, "—", false);

    // ---- workflow dispatch 403（exit 10；fine-grained PAT 缺 Actions 写权限——用户动作，非 agent 可修） ----
    public static Msg DispatchForbidden(string repo, string wf, string detail) => new(
        "[Sluice·终态]",
        $"""
        workflow_dispatch 被服务端拒绝（{detail}）。最常见原因：fine-grained PAT 未开 Actions: Read and write 权限。
        本次操作未执行，GitHub 上没有产生任何变更。
        请转告用户：GitHub → Settings → Developer settings → Fine-grained tokens → 该 token → Repository permissions →
        Actions 设为 Read and write 并保存，然后重跑 sluice setup；不要自行获取或更换凭据。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- workflow dispatch 404（exit 10；workflow 不存在或未声明 dispatch 触发器） ----
    public static Msg DispatchNotFound(string repo, string wf) => new(
        "[Sluice·终态]",
        $"""
        workflow_dispatch 被服务端拒绝：{repo} 的 workflow "{wf}" 不存在，或其 on: 未声明 workflow_dispatch 触发器（HTTP 404）。
        本次操作未执行，GitHub 上没有产生任何变更。
        修正：核对 id/文件名：sluice api "repos/{repo}/actions/workflows"；再读该 workflow 文件确认 on: 块含 workflow_dispatch。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- workflow dispatch 422（exit 10；ref 不存在 / inputs 与声明不符） ----
    public static Msg DispatchRejected(string detail) => new(
        "[Sluice·终态]",
        $"""
        workflow_dispatch 被服务端拒绝（{detail}）。常见原因：--ref 指向的分支/标签不存在；inputs 与 workflow 声明不匹配。
        本次操作未执行，GitHub 上没有产生任何变更。
        修正：核对 ref 与 inputs 后重跑同一命令。
        """,
        ExitCodes.BadRequest, "after_fix", true);

    // ---- api 读通道遇到不可跟随的重定向（exit 10；跟随仅限 GitHub 域——这不是开放代理） ----
    public static Msg ApiRedirected(string path, string detail) => new(
        "[Sluice]",
        $"""
        api GET {path} → 重定向，且不可跟随（{detail}）。
        本次未产生任何副作用（响应体已输出到 stdout）。sluice 仅跟随 api.github.com / *.githubusercontent.com 域内的重定向（≤3 跳）。
        修正：此类资源请经浏览器或按响应体/Location 提示处理；不要尝试把绝对 URL 传给 sluice api。
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
        ("notice",    Notice("github.com")),
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
        ("pushbasemissing", PushBaseMissing("--base 基点提交 21028006 不在远端（HTTP 404：Not Found）——增量基点必须是远端已有对象（增量 base_tree 的锚）")),
        ("pushtoolarge", PushTooLarge("提交数 341 超过 200 上限，请分批")),
        ("pushsteprejected", PushStepRejected("tree 创建 HTTP 422：Invalid request")),
        ("pushblobmismatch", PushBlobMismatch("blob 上传返回 sha 51d0c7a3 与内容寻址不符（本地 9f3c2a1b）")),
        ("prreopenrejected", PrReopenRejected("rustin-beep/Vitro#389", "HTTP 422：head branch was deleted")),
        ("issuecloserejected", IssueCloseRejected("rustin-beep/Vitro#369", "HTTP 422：Can't close a pull request through the issues API")),
        ("issuecreaterejected", IssueCreateRejected("rustin-beep/Sluice 新建 issue", "HTTP 403：Resource not accessible by personal access token")),
        ("prcloserejected", PrCloseRejected("rustin-beep/Vitro#389", "HTTP 422：Pull Request is not open")),
        ("issuereopenrejected", IssueReopenRejected("rustin-beep/Sluice#369", "HTTP 422：Issue is locked")),
        ("issuelabelrejected", IssueLabelRejected("rustin-beep/Sluice#409", "HTTP 404：Label does not exist")),
        ("preditrejected", PrEditRejected("rustin-beep/Sluice#383", "HTTP 403：Resource not accessible by personal access token")),
        ("prreviewrejected", PrReviewRejected("rustin-beep/Vitro#390", "HTTP 422：Can not approve your own pull request")),
        ("tagrejected", TagRejected("rustin-beep/Sluice·v0.6.0", "HTTP 422：Reference already exists")),
        ("releaserejected", ReleaseRejected("rustin-beep/Sluice·v0.6.0", "HTTP 422：already_exists")),
        ("dispatched", WorkflowDispatched("rustin-beep/Vitro", "ci.yml", "master")),
        ("dispatchforbidden", DispatchForbidden("rustin-beep/Vitro", "ci.yml", "HTTP 403：Resource not accessible by personal access token")),
        ("dispatchnotfound", DispatchNotFound("rustin-beep/Vitro", "noci.yml")),
        ("dispatchrejected", DispatchRejected("HTTP 422：Reference does not exist")),
        ("apiredirected", ApiRedirected("repos/rustin-beep/Vitro/actions/jobs/1/logs", "去向 github.com 不在跟随白名单（api.github.com / *.githubusercontent.com）")),
        ("pushdone", PushDone("rustin-beep/Sluice", "feat/x", "9f3c2a1b7d4e5f60718293a4b5c6d7e8f9a0b1c2", false)),
        ("replayunresolved", PushReplayUnresolved("rustin-beep/Sluice", "feat/demo-motion-d0d1-r2")),
        ("mcppending",  PendingGate("a1b2c3d4e5f60718", 15, 75)),
        ("statuspending", StatusPending("a1b2c3d4e5f60718")),
        ("statusdone",  StatusDone("pr_merge", "rustin-beep/Vitro#452", "executed:https://github.com/rustin-beep/Vitro/commit/69f3a989")),
        ("statusother", StatusOther("workflow_dispatch", "rustin-beep/Vitro ci.yml@x", "not_found", "http_404")),
        ("apirateanon", ApiRateLimitedAnon("repos/rustin-beep/Vitro/issues/52", 403)),
        ("apiratewait", ApiRateLimitedWait("repos/rustin-beep/Vitro/issues/52", 403, "secondary rate limit，服务端要求等待约 2 分钟")),
        ("writeratelimited", WriteRateLimited("pr_close", "rustin-beep/Vitro#390", 403, "secondary rate limit，服务端要求等待约 2 分钟")),
        ("pushratelimited", PushRateLimited("rustin-beep/Vitro", "feat/x", "secondary rate limit，服务端要求等待约 2 分钟")),
        ("writeforbidden", WriteForbidden("pr_create", "rustin-beep/Vitro", "HTTP 403：Resource not accessible by personal access token")),
        ("commentrejected", CommentRejected("rustin-beep/Vitro#52", "HTTP 404：Not Found")),
        ("serverstale", ServerStale()), // v2.26：僵尸 server 自曝（exit 1/wait_user——重启会话后重试）
    ];

    public static void Write(Msg m)
    {
        var full = m.Prefix + " " + m.Text.Replace("\n", Environment.NewLine);
        var tw = m.ToStderr ? Console.Error : Console.Out;
        tw.WriteLine(full);
    }
}
