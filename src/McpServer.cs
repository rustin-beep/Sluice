using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Sluice;

/// <summary>
/// §9.7 MCP 前门：`sluice mcp` —— stdio 逐行 JSON-RPC 2.0，单一类型化工具 github_write。
/// 拓扑 = spawn-child：每次 tools/call 以绝对路径 spawn 一次 CLI 子进程，本进程永不接触
/// vault、零行安全/UI 代码（Gate 的 Avalonia lifetime 是"窗口关即退出"的一次性设计，
/// 常驻复用不可行——机制论证见 plan-lite §9.7）。红线：elicitation 永不替代闸门；
/// host 取消不杀子进程（杀活窗违禁，审计行是真相）。
/// </summary>
internal static class McpServer
{
    /// <summary>
    /// exit → retry 映射。函数性由 selftest 锁死：AllSamples 按 exit 分组 retry 必单值且与本表
    /// 一致——"同 exit 不同 retry"的新消息会让 selftest 当场 FAIL（§9.7 不变量，杜绝双表漂移）。
    /// </summary>
    internal static string RetryOf(int code) => code switch
    {
        ExitCodes.Success           => "—",
        ExitCodes.Internal          => "wait_user",
        ExitCodes.Denied            => "never",
        ExitCodes.Timeout           => "wait_user",
        ExitCodes.PolicyReject      => "change_approach",
        ExitCodes.Cooldown          => "wait_until",
        ExitCodes.ConsentFailed     => "wait_user",
        ExitCodes.SealViolation     => "wait_user",
        ExitCodes.NotInitialized    => "wait_user",
        ExitCodes.CredentialExpired => "wait_user",
        ExitCodes.BadRequest        => "after_fix",
        ExitCodes.HttpFailed        => "retry_ok",
        ExitCodes.Unknown           => "wait_user",
        ExitCodes.RateLimitWait     => "wait_until",
        _ => "wait_user", // 表外 code = 契约破裂，fail-safe 交回用户
    };

    /// <summary>§9 写 op 的 action 值域（v2.11 起与 plan-lite §9 / CLI 写动词表三方对账，selftest 面一致锁；
    /// v2.11 将 comment 更名 pr_comment——§9.7"与 §9 写 op 一一对应"的字面承诺自此零例外）。</summary>
    internal static readonly string[] Actions =
    [
        "pr_comment", "push", "pr_create", "pr_merge", "pr_reopen", "pr_close", "pr_edit",
        "issue_create", "issue_close", "issue_reopen", "pr_review",
        "tag_create", "release_create", "branch_delete", "workflow_dispatch",
    ];

    /// <summary>各 action 允许的参数名（与 CLI flag 同名映射；表外字段报错——防"传了被静默忽略"的误会）。</summary>
    internal static readonly Dictionary<string, string[]> AllowedFields = new()
    {
        ["pr_comment"]        = ["repo", "number", "body"],
        ["push"]              = ["repo", "branch", "repo_path", "base"],
        ["pr_create"]         = ["repo", "base", "head", "title", "body"],
        ["pr_merge"]          = ["repo", "number", "method", "delete_head"],
        ["pr_reopen"]         = ["repo", "number"],
        ["pr_close"]          = ["repo", "number"],
        ["pr_edit"]           = ["repo", "number", "title", "body"],
        ["issue_create"]      = ["repo", "title", "body"],
        ["issue_close"]       = ["repo", "number"],
        ["issue_reopen"]      = ["repo", "number"],
        ["pr_review"]         = ["repo", "number", "event", "body"],
        ["tag_create"]        = ["repo", "tag", "at"],
        ["release_create"]    = ["repo", "tag", "target", "name", "body", "prerelease"],
        ["branch_delete"]     = ["repo", "branch"],
        ["workflow_dispatch"] = ["repo", "workflow", "ref", "inputs"],
    };

    /// <summary>
    /// 工具描述 = 结构性在场的实际载荷（§8.4 纪律：交代明确/防多想），随每个请求重发，
    /// 不受对话压缩影响——MCP 版的"AGENTS.md 第一行"。
    /// </summary>
    private const string ToolDescription =
        """
        GitHub 写操作闸门（Sluice）。本机所有 GitHub 写操作——评论、推送、创建/合并/重开/关闭/编辑 PR、
        评审 PR、创建/关闭/重开 issue、打 tag、发布 release、删除分支、触发 workflow——必须且只能经此
        工具执行；环境内无其他可用凭据，直接 git push / gh / GitHub API 调用必然失败且违反纪律。
        调用会弹出本机批准窗口，由用户人工批准或拒绝（无响应自动拒绝）。返回的 text 是终态说明，
        请按其指引行动；structuredContent.retry 是机器可读的下一步：never=禁止重试 /
        after_fix=修正参数后可重试 / retry_ok=可直接重试 / wait_user=等待用户指示 /
        wait_until=等待到点 / change_approach=改换方案。
        一次调用恰好执行一个写操作。只读查询不要用本工具：运行命令 sluice api <path>
        （如 sluice api repos/owner/repo/pulls）。
        """;

    private static readonly string InputSchema = """
        {
          "type": "object",
          "required": ["action", "repo"],
          "properties": {
            "action":       { "type": "string", "enum": ["pr_comment","push","pr_create","pr_merge","pr_reopen","pr_close","pr_edit","issue_create","issue_close","issue_reopen","pr_review","tag_create","release_create","branch_delete","workflow_dispatch"], "description": "写操作类型" },
            "repo":         { "type": "string", "description": "目标仓库 owner/name" },
            "number":       { "type": "integer", "description": "issue/PR 编号（pr_comment/pr_merge/pr_reopen/pr_close/pr_edit/issue_close/issue_reopen/pr_review 必填）" },
            "body":         { "type": "string", "description": "正文全文（pr_comment/pr_create/issue_create/release_create 必填；pr_edit/pr_review 可选）" },
            "title":        { "type": "string", "description": "标题（pr_create/issue_create 必填；pr_edit 可选——勘误）" },
            "base":         { "type": "string", "description": "pr_create：base 分支名；push：增量基点/重放式更新基点（如 origin/master）" },
            "head":         { "type": "string", "description": "PR head 分支（pr_create 必填）" },
            "branch":       { "type": "string", "description": "目标分支（push/branch_delete 必填）" },
            "repo_path":    { "type": "string", "description": "本地 git 仓库路径（push 可选，默认当前目录）" },
            "method":       { "type": "string", "enum": ["merge","squash","rebase"], "description": "合并方式（pr_merge 可选）" },
            "delete_head":  { "type": "boolean", "description": "合并后删除 head 分支（pr_merge 可选）" },
            "event":        { "type": "string", "enum": ["COMMENT","APPROVE","REQUEST_CHANGES"], "description": "评审动作（pr_review 必填；APPROVE 不能用于自己账户的 PR——服务端 422）" },
            "tag":          { "type": "string", "description": "tag 名（tag_create/release_create 必填）" },
            "at":           { "type": "string", "description": "tag 指向的分支名或完整 sha（tag_create 必填）" },
            "target":       { "type": "string", "description": "release_create 可选：分支名或完整 sha（缺省取默认分支头；以批准期解析的 sha 锚定）" },
            "name":         { "type": "string", "description": "release_create 可选：release 名称（缺省用 tag 名）" },
            "prerelease":   { "type": "boolean", "description": "release_create 可选：标记为预发布" },
            "workflow":     { "type": "string", "description": "workflow id 或 .yml 文件名（workflow_dispatch 必填）" },
            "ref":          { "type": "string", "description": "dispatch 目标分支/标签（workflow_dispatch 必填）" },
            "inputs":       { "type": "object", "description": "workflow inputs 键值对（workflow_dispatch 可选）" }
          }
        }
        """;

    // ---------- 入口 ----------

    public static int Run(string[] args)
    {
        if (args is ["--print-config"]) { PrintConfig(); return 0; }

        // 显式 UTF-8 双流：不依赖 Console.*Encoding——宿主可能无控制台（GUI 宿主下 setter 会抛）
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            HandleLine(line, stdout);
        }
        return 0; // EOF = 宿主关闭，无状态可损失
    }

    private static void HandleLine(string line, StreamWriter stdout)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(line); }
        catch (JsonException)
        {
            Send(stdout, null, w => w.WriteError(-32700, "Parse error"));
            return;
        }
        using var root = doc;
        var r = root.RootElement;
        if (r.ValueKind != JsonValueKind.Object) { Send(stdout, null, w => w.WriteError(-32600, "Invalid Request")); return; }

        bool hasId = r.TryGetProperty("id", out var idEl);
        if (!r.TryGetProperty("method", out var methodEl) || methodEl.ValueKind != JsonValueKind.String)
        {
            if (hasId) Send(stdout, idEl, w => w.WriteError(-32600, "Invalid Request"));
            return; // 无 method 的通知一律忽略
        }
        var method = methodEl.GetString()!;
        r.TryGetProperty("params", out var paramsEl);
        var p = paramsEl.ValueKind == JsonValueKind.Object ? paramsEl : (JsonElement?)null;

        if (!hasId)
            return; // 通知（initialized/cancelled/…）：§9.7——cancelled 也不杀子进程，忽略即策略

        switch (method)
        {
            case "initialize":
                var ver = p?.TryGetProperty("protocolVersion", out var pv) == true && pv.ValueKind == JsonValueKind.String
                    ? pv.GetString() : "2025-06-18";
                Send(stdout, idEl, w =>
                {
                    w.WriteStartObject("result");
                    w.WriteString("protocolVersion", ver);
                    w.WriteStartObject("capabilities"); w.WriteStartObject("tools"); w.WriteEndObject(); w.WriteEndObject();
                    w.WriteStartObject("serverInfo");
                    w.WriteString("name", "sluice");
                    w.WriteString("version", typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "?");
                    w.WriteEndObject();
                    w.WriteEndObject();
                });
                break;
            case "ping":
                Send(stdout, idEl, w => { w.WriteStartObject("result"); w.WriteEndObject(); });
                break;
            case "tools/list":
                Send(stdout, idEl, w =>
                {
                    w.WriteStartObject("result");
                    w.WriteStartArray("tools");
                    w.WriteStartObject();
                    w.WriteString("name", "github_write");
                    w.WriteString("description", ToolDescription);
                    using var schema = JsonDocument.Parse(InputSchema);
                    w.WritePropertyName("inputSchema");
                    schema.RootElement.WriteTo(w);
                    w.WriteEndObject();
                    w.WriteEndArray();
                    w.WriteEndObject();
                });
                break;
            case "tools/call":
                CallTool(stdout, idEl, p);
                break;
            default:
                Send(stdout, idEl, w => w.WriteError(-32601, $"Method not found: {method}"));
                break;
        }
    }

    // ---------- tools/call：类型化参数 → spawn CLI 子进程 ----------

    private static void CallTool(StreamWriter stdout, JsonElement idEl, JsonElement? p)
    {
        var name = p?.TryGetProperty("name", out var n) == true && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        if (name != "github_write")
        {
            var m = Messages.BadRequest($"未知工具 \"{name}\"；本服务只提供 github_write（§9.7：单一类型化工具，不做透传）");
            SendToolResult(stdout, idEl, MsgText(m), m.ExitCode, isError: true);
            return;
        }
        var a = p?.TryGetProperty("arguments", out var ae) == true && ae.ValueKind == JsonValueKind.Object ? ae : default;

        var bodyPath = Path.Combine(Path.GetTempPath(), $"sluice-mcp-{Guid.NewGuid():N}.body");
        var inputsPath = Path.Combine(Path.GetTempPath(), $"sluice-mcp-{Guid.NewGuid():N}.inputs.json");
        try
        {
            if (!BuildArguments(a, bodyPath, inputsPath, out var argv, out var bodyContent, out var inputsContent, out var error))
            {
                SendToolResult(stdout, idEl, MsgText(error!), error!.ExitCode, isError: true);
                return;
            }
            if (bodyContent is not null) File.WriteAllText(bodyPath, bodyContent, new UTF8Encoding(false));
            if (inputsContent is not null) File.WriteAllText(inputsPath, inputsContent, new UTF8Encoding(false));

            var (text, code) = SpawnCli(argv);
            SendToolResult(stdout, idEl, text, code, isError: code != 0);
        }
        catch (Exception ex)
        {
            var m = Messages.BadRequest($"MCP 层异常：{ex.GetType().Name}: {ex.Message}");
            SendToolResult(stdout, idEl, MsgText(m), m.ExitCode, isError: true);
        }
        finally
        {
            try { if (File.Exists(bodyPath)) File.Delete(bodyPath); } catch { }
            try { if (File.Exists(inputsPath)) File.Delete(inputsPath); } catch { }
        }
    }

    /// <summary>
    /// 类型化 JSON 参数 → CLI argv（与 §9 flag 同名一一映射）。纯函数：文件路径由调用方给、内容由调用方写
    /// （selftest 零文件系统直测）。必填项缺省不在此拦——子进程 verb-aware ErrMissing 的文案与示例更准（单源）。
    /// </summary>
    internal static bool BuildArguments(JsonElement a, string bodyPath, string inputsPath,
        out string[] argv, out string? bodyContent, out string? inputsContent, out Msg? error)
    {
        argv = []; bodyContent = null; inputsContent = null; error = null;

        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("action", out var actionEl) || actionEl.ValueKind != JsonValueKind.String)
        {
            error = Messages.BadRequest($"arguments.action 缺失或非字符串；可用值：{string.Join(" / ", Actions)}");
            return false;
        }
        var action = actionEl.GetString()!;
        if (!AllowedFields.TryGetValue(action, out var allowed))
        {
            error = Messages.BadRequest($"未知 action \"{action}\"；可用值：{string.Join(" / ", Actions)}");
            return false;
        }
        foreach (var prop in a.EnumerateObject())
            if (prop.Name != "action" && !allowed.Contains(prop.Name))
            {
                error = Messages.BadRequest($"action={action} 不接受参数 \"{prop.Name}\"（允许：{string.Join(", ", allowed)}）——参数名错误或该参数属其他 action；本次未产生任何副作用");
                return false;
            }

        // 取值三分：缺省 null（省略 flag——必填缺省交子进程 verb-aware ErrMissing，其文案带示例，单源更准）；
        // 类型错返回哨兵（宿主传了类型错的值，CLI 收 argv 后无法分辨，必须在此拦）。
        const string Wrong = "\u0000wrong";
        string? Str(string name) => !a.TryGetProperty(name, out var v) ? null
            : v.ValueKind == JsonValueKind.String ? v.GetString() : Wrong;
        // number 宽容：接受 JSON number 或数字字符串（部分宿主把 integer 序列化成 string）
        string? Num(string name)
        {
            if (!a.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l.ToString();
            if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s)) return s.ToString();
            return Wrong;
        }
        bool Has(string name) => a.TryGetProperty(name, out _);

        var list = new List<string>();
        void Opt(string flag, string? value) { if (value is not null) { list.Add(flag); list.Add(value); } }

        switch (action)
        {
            case "pr_comment":
                list.Add("comment");
                Opt("--repo", Str("repo")); Opt("--number", Num("number"));
                if (Has("body")) { list.Add("--body-file"); list.Add(bodyPath); }
                break;
            case "push":
                list.Add("push");
                Opt("--repo", Str("repo")); Opt("--branch", Str("branch"));
                Opt("--repo-path", Str("repo_path")); Opt("--base", Str("base"));
                break;
            case "pr_create":
                list.AddRange(["pr", "create"]);
                Opt("--repo", Str("repo")); Opt("--base", Str("base"));
                Opt("--head", Str("head")); Opt("--title", Str("title"));
                if (Has("body")) { list.Add("--body-file"); list.Add(bodyPath); }
                break;
            case "pr_merge":
                list.AddRange(["pr", "merge"]);
                Opt("--repo", Str("repo")); Opt("--number", Num("number")); Opt("--method", Str("method"));
                if (a.TryGetProperty("delete_head", out var dh)
                    && (dh.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                        || (dh.ValueKind == JsonValueKind.String && dh.GetString() != "true" && dh.GetString() != "false")))
                { error = Messages.BadRequest("delete_head 应为布尔值——本次未产生任何副作用"); return false; }
                if (dh.ValueKind == JsonValueKind.True || (dh.ValueKind == JsonValueKind.String && dh.GetString() == "true"))
                    list.Add("--delete-head");
                break;
            case "pr_reopen":
                list.AddRange(["pr", "reopen"]);
                Opt("--repo", Str("repo")); Opt("--number", Num("number"));
                break;
            case "pr_close":
                list.AddRange(["pr", "close"]);
                Opt("--repo", Str("repo")); Opt("--number", Num("number"));
                break;
            case "pr_edit":
                list.AddRange(["pr", "edit"]);
                Opt("--repo", Str("repo")); Opt("--number", Num("number")); Opt("--title", Str("title"));
                if (Has("body")) { list.Add("--body-file"); list.Add(bodyPath); }
                break;
            case "issue_create":
                list.AddRange(["issue", "create"]);
                Opt("--repo", Str("repo")); Opt("--title", Str("title"));
                if (Has("body")) { list.Add("--body-file"); list.Add(bodyPath); }
                break;
            case "issue_close":
                list.AddRange(["issue", "close"]);
                Opt("--repo", Str("repo")); Opt("--number", Num("number"));
                break;
            case "issue_reopen":
                list.AddRange(["issue", "reopen"]);
                Opt("--repo", Str("repo")); Opt("--number", Num("number"));
                break;
            case "pr_review":
                list.Add("review");
                Opt("--repo", Str("repo")); Opt("--number", Num("number")); Opt("--event", Str("event"));
                if (Has("body")) { list.Add("--body-file"); list.Add(bodyPath); }
                break;
            case "tag_create":
                list.AddRange(["tag", "create"]);
                Opt("--repo", Str("repo")); Opt("--tag", Str("tag")); Opt("--at", Str("at"));
                break;
            case "release_create":
                list.AddRange(["release", "create"]);
                Opt("--repo", Str("repo")); Opt("--tag", Str("tag")); Opt("--target", Str("target")); Opt("--name", Str("name"));
                if (a.TryGetProperty("prerelease", out var pr))
                {
                    // v2.16 白名单化：原 `not(True|False) || (String && ≠true/false)` 使字符串值必被首支
                    // 拒绝、"字符串 true"接受分支不可达（死代码）；直接改 && 又会漏放 object/array
                    // （静默丢参）。白名单 = 真布尔或字符串 "true"/"false"（宿主 JSON 化常见形态），其余显式拒。
                    bool okBool = pr.ValueKind is JsonValueKind.True or JsonValueKind.False;
                    bool okStr = pr.ValueKind == JsonValueKind.String && pr.GetString() is "true" or "false";
                    if (!okBool && !okStr)
                    { error = Messages.BadRequest("prerelease 应为布尔值——本次未产生任何副作用"); return false; }
                    if (pr.ValueKind == JsonValueKind.True || (okStr && pr.GetString() == "true"))
                        list.Add("--prerelease");
                }
                if (Has("body")) { list.Add("--body-file"); list.Add(bodyPath); }
                break;
            case "branch_delete":
                list.AddRange(["branch", "delete"]);
                Opt("--repo", Str("repo")); Opt("--branch", Str("branch"));
                break;
            case "workflow_dispatch":
                list.AddRange(["workflow", "dispatch"]);
                Opt("--repo", Str("repo")); Opt("--workflow", Str("workflow")); Opt("--ref", Str("ref"));
                if (Has("inputs")) { list.Add("--inputs-file"); list.Add(inputsPath); }
                break;
        }
        argv = [.. list];

        // 类型错统一在此拦（Str/Num 哨兵位）——防"宿主传了类型错的值被当缺省处理"
        if (argv.Any(x => x == Wrong))
        {
            error = Messages.BadRequest($"action={action} 的参数类型错误（repo/branch/title/base 等应为字符串，number 应为整数）——本次未产生任何副作用");
            return false;
        }

        if (a.TryGetProperty("body", out var bodyEl))
        {
            if (bodyEl.ValueKind != JsonValueKind.String)
            { error = Messages.BadRequest("body 应为字符串（正文全文）——本次未产生任何副作用"); return false; }
            bodyContent = bodyEl.GetString();
        }
        if (a.TryGetProperty("inputs", out var inputsEl))
        {
            if (inputsEl.ValueKind != JsonValueKind.Object)
            { error = Messages.BadRequest("inputs 应为 JSON 对象（workflow inputs 键值对）——本次未产生任何副作用"); return false; }
            inputsContent = inputsEl.GetRawText();
        }
        return true;
    }

    /// <summary>
    /// spawn CLI 子进程（绝对路径杀 PATH 劫持；ArgumentList 结构化传参无 shell 引号面；双流捕获：
    /// §8 消息成功在 stdout、失败在 stderr）。不设超时——批准窗最长 600s + push 多请求序列；
    /// 宿主侧超时由宿主自理，本进程不杀子进程（杀活窗违禁，§9.7）。
    /// </summary>
    private static (string Text, int Code) SpawnCli(string[] argv)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath ?? "sluice.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (var arg in argv) psi.ArgumentList.Add(arg);
        psi.EnvironmentVariables["SLUICE_VIA"] = "mcp"; // §10 via 字段：纯标签非机密

        using var p = Process.Start(psi)!;
        p.StandardInput.Close(); // 子进程不读 stdin
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        var so = outTask.Result; var se = errTask.Result;

        // 主消息流：成功在 stdout、失败在 stderr（§8.2 ToStderr）；另一流非空则作附注（push heartbeat 等）
        var primary = (p.ExitCode == 0 ? so : se).TrimEnd();
        var secondary = (p.ExitCode == 0 ? se : so).TrimEnd();
        var text = primary.Length == 0 && secondary.Length > 0
            ? secondary
            : secondary.Length == 0 ? primary : primary + "\n\n[" + (p.ExitCode == 0 ? "stderr" : "stdout") + "]\n" + secondary;
        return (text.Length == 0 ? "[Sluice] 子进程无输出（exit " + p.ExitCode + "）" : text, p.ExitCode);
    }

    // ---------- JSON-RPC 输出 ----------

    private static string MsgText(Msg m) => (m.Prefix + " " + m.Text).TrimEnd();

    private static void Send(StreamWriter stdout, JsonElement? id, Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
            if (id is { } el) { w.WritePropertyName("id"); el.WriteTo(w); }
            else w.WriteNull("id");
            body(w);
            w.WriteEndObject();
        }
        stdout.WriteLine(Encoding.UTF8.GetString(ms.ToArray())); // 单行 JSON + \n，AutoFlush
    }

    private static void WriteError(this Utf8JsonWriter w, int code, string message)
    {
        w.WriteStartObject("error");
        w.WriteNumber("code", code);
        w.WriteString("message", message);
        w.WriteEndObject();
    }

    /// <summary>工具结果：text=子进程 §8 消息逐字（单源）；structuredContent={ok,code,retry,url?}；isError=!ok。</summary>
    private static void SendToolResult(StreamWriter stdout, JsonElement idEl, string text, int code, bool isError)
    {
        Send(stdout, idEl, w =>
        {
            w.WriteStartObject("result");
            w.WriteStartArray("content");
            w.WriteStartObject();
            w.WriteString("type", "text");
            w.WriteString("text", text);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteStartObject("structuredContent");
            w.WriteBoolean("ok", !isError);
            w.WriteNumber("code", code);
            w.WriteString("retry", RetryOf(code));
            var url = ExtractUrl(text);
            if (url is not null) w.WriteString("url", url);
            w.WriteEndObject();
            w.WriteBoolean("isError", isError);
            w.WriteEndObject();
        });
    }

    /// <summary>尽力提取结果 URL（消息里明文，如 https://github.com/...#issuecomment-…）；提取不到就省略，不改 §8 消息规范。</summary>
    private static string? ExtractUrl(string text)
    {
        const string Marker = "https://github.com/";
        var i = text.IndexOf(Marker, StringComparison.Ordinal);
        if (i < 0) return null;
        var j = i + Marker.Length;
        while (j < text.Length && char.IsAsciiLetterOrDigit(text[j])) j++; // 取 owner/repo 一段即可，够打开
        return j > i + Marker.Length ? text[i..j] : null;
    }

    // ---------- --print-config ----------

    private static void PrintConfig()
    {
        var exe = Environment.ProcessPath ?? "sluice.exe";
        // 手动转义（路径只需 \\ 与 "）：JsonSerializer.Serialize<T> 在 NativeAOT 下有 IL3050（零警告纪律）
        var exeJson = "\"" + exe.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        var cfg = $$"""
            {
              "mcpServers": {
                "sluice": {
                  "command": {{exeJson}},
                  "args": ["mcp"]
                }
              }
            }
            """;
        Console.WriteLine("""
            sluice mcp —— MCP 前门（§9.7）宿主配置片段
            绝对路径杀 PATH 劫持；工具 = 单一类型化 github_write；api 读走 CLI（sluice api <path>）。
            粘贴到宿主的 MCP 配置（ZCode：设置→MCP；Claude Code：.mcp.json / ~/.claude.json）：
            """);
        Console.WriteLine(cfg);
    }
}
