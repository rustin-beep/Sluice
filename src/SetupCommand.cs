namespace Sluice;

internal static class SetupCommand
{
    // ---------- sluice setup（§5 凭据生命周期） ----------

    public static async Task<int> SetupAsync(string? tokenFile)
    {
        Console.WriteLine("Sluice setup —— 建立事件闸门凭据");
        Console.WriteLine("""
            请准备一张 fine-grained PAT（GitHub → Settings → Developer settings → Fine-grained tokens）：
              · Repository access：只选指定仓库（爆炸半径封顶的硬性要求，§3）
              · Permissions：Contents Read/Write、Pull requests Read/Write
              · 有效期：最短实用（建议 90 天）
            """);

        string pat;
        if (tokenFile is not null)
        {
            if (!File.Exists(tokenFile))
            {
                Console.WriteLine($"--token-file 不存在：{tokenFile}");
                return 1;
            }
            pat = File.ReadAllText(tokenFile).Trim();
            Console.WriteLine($"已从文件读取 PAT（{tokenFile}，{pat.Length} 字符）");
        }
        else
        {
            Console.WriteLine("粘贴 PAT（粘贴或键入后按回车；输入显示为 *，ESC 放弃）：");
            pat = ReadSecret();
        }
        if (string.IsNullOrEmpty(pat))
        {
            Console.WriteLine("未输入 token，setup 中止（未产生任何变更）。");
            return 1;
        }

        Console.WriteLine("校验 token（GET /user）…");
        var (ok, info) = await GitHubApi.ValidateTokenAsync(pat);
        if (!ok)
        {
            Console.WriteLine($"校验失败：{info}");
            Console.WriteLine("setup 中止，未保存任何内容。请确认 token 有效后重跑 sluice setup。");
            return 1;
        }
        Console.WriteLine($"校验通过：login = {info}");
        Console.WriteLine("（注意：本地无法完整校验 fine-grained PAT 的仓库授权范围，以 GitHub 服务端为准——未授权仓库的写请求会 403。）");

        if (Gate.HelloEnabled)
        {
            Console.WriteLine();
            Console.WriteLine("下一步将弹出 Windows Hello：创建/打开 vault 密钥并签名。");
            Console.WriteLine("这次验证会加密你的 token；此后每次写操作解密时都需要同样的生物验证（批准即解密）。");
            var p = TokenStore.NewParams();
            var sig = await HelloConsent.SignForSetupAsync(p.Challenge);
            if (sig is null)
            {
                Console.WriteLine("Windows Hello 未通过，setup 未完成（未保存任何内容）。");
                return 1;
            }

            TokenStore.Save(p, pat, sig);
        }
        else
        {
            // D1（2026-10-31）：config hello:false——vault 直接存 DPAPI 静默格式，setup 全程无 Hello。
            // 代价：token 同用户进程可静默解密（owner 显式降级，见 Gate.HelloEnabled 注释）；
            // 改回 true 后重跑 setup 即恢复 Hello 派生格式。
            TokenStore.SaveDpapi(pat);
            Console.WriteLine("config hello:false——vault 已存为静默格式（DPAPI），批准不再需要 Windows Hello。");
        }

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sluice");
        if (!File.Exists(Path.Combine(dir, "config.json")))
            File.WriteAllText(Path.Combine(dir, "config.json"), "{}" + Environment.NewLine);

        // token 已入 vault，明文副本使命结束：seal 无法枚举任意路径上的 token 文件，
        // 留着 = 封堵前提之外的侧门（P2，2026-10-30 评审）。删除失败必须醒目提示人工处理。
        if (tokenFile is not null)
        {
            try
            {
                File.Delete(tokenFile);
                Console.WriteLine($"已删除 --token-file 文件（明文 PAT 不应与 vault 并存）：{tokenFile}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠ 无法删除 --token-file 文件（{ex.Message}）——seal 不扫描该路径，");
                Console.WriteLine($"  请立即手动删除含明文 PAT 的文件：{tokenFile}");
            }
        }

        Console.WriteLine($"已保存 vault：{Path.Combine(dir, "token.bin")}");
        Console.WriteLine();
        Console.WriteLine("最后两步（承重墙）：");
        Console.WriteLine("  1. sluice seal --fix   ——封堵环境里的直连凭据；");
        Console.WriteLine("  2. sluice setup --install ——安装 notice credential helper（git 直推 GitHub 时得到指引进 sluice 的消息，而非裸错）。");
        return 0;
    }

    /// <summary>
    /// 逐字符掩码读取（* 回显，退格可删，ESC 清空放弃）。
    /// conhost 下无换行 Console.Write 不刷新显示（实测：提示行根本不出现），
    /// 且行缓冲 ReadLine 会把 PAT 明文留在屏幕滚动缓冲——逐字符读两个问题一起解决。
    /// stdin 被重定向（管道/脚本）时回退 ReadLine。
    /// </summary>
    private static string ReadSecret()
    {
        if (Console.IsInputRedirected)
            return Console.ReadLine()?.Trim() ?? "";
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var ki = Console.ReadKey(intercept: true);
            if (ki.Key == ConsoleKey.Enter) { Console.WriteLine(); return sb.ToString().Trim(); }
            if (ki.Key == ConsoleKey.Escape) { Console.WriteLine(); return ""; }
            if (ki.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); }
                continue;
            }
            if (ki.KeyChar == '\0') continue; // 修饰键/功能键
            sb.Append(ki.KeyChar);
            Console.Write('*');
        }
    }

    // ---------- sluice seal [--fix]（§6） ----------

    public static int Seal(bool fix)
    {
        Console.WriteLine("Sluice seal —— 侧门自查（检测 agent 可静默取用的 GitHub 写凭据）");
        Console.WriteLine();

        // 自愈（v2.22）：state.json 有已过期的暂停窗口 → 先执行恢复（重装 notice + seal --fix）
        // 再呈现真实状态——人跑 seal 或哨兵 tick 都会走到这里，到期恢复不依赖人记得
        if (SealCheck.SealPause.Until() is { } expired && expired <= DateTime.UtcNow)
        {
            Console.WriteLine($"[Sluice] 暂停窗口已过期（{UntilLabel(expired)} 到期）——自动执行恢复：");
            SealResume();
            Console.WriteLine();
        }
        if (SealCheck.SealPause.Active)
        {
            Console.WriteLine($"⏸ 封堵暂停中（至 {UntilLabel(SealCheck.SealPause.Until()!.Value)}）——期内直推能力开放、写操作 seal 门放行。");
            Console.WriteLine("结束暂停立即恢复：sluice seal --resume（或等哨兵在到期后 5 分钟内自动恢复）。");
            return 0;
        }

        var v = SealCheck.Run();
        if (v.Count == 0)
        {
            Console.WriteLine("✓ 环境干净：未发现未封堵的 GitHub 凭据。闸门对绕过路径闭合。");
            return 0;
        }

        Console.WriteLine($"发现 {v.Count} 处违规：");
        foreach (var item in v)
            Console.WriteLine($"  ✗ {item}");
        Console.WriteLine();

        if (!fix)
        {
            Console.WriteLine("运行 sluice seal --fix 自动处理（凭据管理器 / gh hosts.yml / .git-credentials / SSH 屏蔽块）。");
            Console.WriteLine("在处理完成前，sluice 的一切写操作将以 seal_violation 拒绝服务。");
            // 脏环境即非零：脚本可判脏（exit 7 = seal_violation，与写路径同码）
            return ExitCodes.SealViolation;
        }

        Console.WriteLine("执行 --fix …");
        foreach (var action in SealCheck.Fix(v))
            Console.WriteLine($"  - {action}");
        Console.WriteLine();

        var after = SealCheck.Run();
        if (after.Count == 0)
        {
            Console.WriteLine("✓ 复查通过：环境已封堵。");
            return 0;
        }
        Console.WriteLine($"复查仍有 {after.Count} 处（需人工处理）：");
        foreach (var item in after)
            Console.WriteLine($"  ✗ {item}");
        return ExitCodes.SealViolation;
    }

    // ---------- seal --pause / --resume / unseal（v2.22，§6 + §9.5 人类工作流） ----------

    /// <summary>暂停截止时刻的本地显示（v2.23 审阅修复）：跨午夜必须带日期——窗口最长 8 小时，
    /// 只写 HH:mm 会把"明天 03:00"读成"今天 03:00"；同日只写 HH:mm 保持简洁。</summary>
    internal static string UntilLabel(DateTime untilUtc)
    {
        var local = untilUtc.ToLocalTime();
        return local.Date == DateTime.Now.Date
            ? local.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : local.ToString("MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>非 GitHub 动作复用写动作闸门（§6：与一切写动作同待遇——agent 也能发起，同样要过批准窗）。
    /// 拒绝/超时/Hello 失败/vault 损坏/崩溃的收敛与写路径同形；批准 → onApproved()。</summary>
    private static int RunLocalGated(string op, string target, string body, string? warning, Func<int> onApproved)
    {
        byte[] challenge;
        try { challenge = TokenStore.PeekChallenge(); }
        catch { challenge = []; }
        var outcome = Gate.Run(new Gate.Request(op, "(本机)", target, body, "—", challenge, warning));
        switch (outcome)
        {
            case Gate.Outcome.Denied d:
                Audit.Append(op, null, target, null, "denied", d.Preset, d.FreeText, consent: "none");
                Messages.Write(Messages.Denied(op, target, d.Preset, d.FreeText));
                return ExitCodes.Denied;
            case Gate.Outcome.TimedOut:
                Audit.Append(op, null, target, null, "timeout");
                Messages.Write(Messages.Timeout(op, target, Gate.TimeoutSeconds));
                return ExitCodes.Timeout;
            case Gate.Outcome.ConsentFailed:
                Audit.Append(op, null, target, null, "consent_failed", consent: "hello_failed");
                Messages.Write(Messages.ConsentFailed(op, target));
                return ExitCodes.ConsentFailed;
            case Gate.Outcome.VaultKeyMissing vk:
                Audit.Append(op, null, target, null, "vault_broken");
                Messages.Write(Messages.VaultBroken(vk.Detail));
                return ExitCodes.NotInitialized;
            case Gate.Outcome.Crashed c:
                Audit.Append(op, null, target, null, "internal_gate_crash", result: c.Error);
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
            case Gate.Outcome.Approved:
                return onApproved();
            default:
                Messages.Write(Messages.Internal(afterExecutionStarted: false));
                return ExitCodes.Internal;
        }
    }

    /// <summary>§6 显式暂停窗口：批准后摘 notice helper + SSH 屏蔽块，state.json 记 pause_until；
    /// 到期自愈 = seal no-args 检测过期先 resume + 哨兵 ps1 检测过期跑 seal --resume（最迟 5 分钟）。</summary>
    public static int SealPauseCmd(string minutesArg)
    {
        if (!int.TryParse(minutesArg, out var minutes) || minutes is < 1 or > 480)
        {
            Console.Error.WriteLine("[Sluice] 用法：sluice seal --pause <分钟>（范围 1–480）");
            return ExitCodes.BadRequest;
        }
        if (!TokenStore.Exists) { Messages.Write(Messages.NotInitialized()); return ExitCodes.NotInitialized; }
        if (SealCheck.SealPause.Active)
        {
            Console.Error.WriteLine($"[Sluice] 已在暂停窗口内（至 {UntilLabel(SealCheck.SealPause.Until()!.Value)}）——如需延长，先 seal --resume 再重新暂停。");
            return ExitCodes.BadRequest;
        }
        var until = DateTime.UtcNow.AddMinutes(minutes); // 弹窗正文用的预估（v2.23：批准拖延时实际值偏后，
                                                          // 批准后以 Begin 返回的实际值为准）
        var body = $"""
            暂停侧门封堵 {minutes} 分钟（至本地约 {UntilLabel(until)}）。
            期内：git/gh 直推能力恢复（notice helper 摘除、SSH 屏蔽块摘除）；sluice 写操作的 seal 门同步放行。
            到期：最迟 5 分钟内自动重新封堵（重装 notice + 清理期内新登录的凭据）；提前结束用 sluice seal --resume。
            这是有意的防护降级窗口——期内你重新提供的任何 GitHub 凭据，同用户进程（含 agent）都可静默使用。
            """;
        return RunLocalGated("seal_pause", $"暂停 {minutes} 分钟", body,
            "暂停 = 防护降级窗口：期内 agent 可静默使用你重新提供的任何 GitHub 凭据",
            () =>
        {
            var actual = SealCheck.SealPause.Begin(minutes);
            Git("config", "--global", "--unset-all", "credential.https://github.com.helper"); // 失败=本就不在位
            var acts = new List<string>
            {
                $"状态：state.json 已记 pause_until（至 {UntilLabel(actual)}）",
                "notice helper：已摘除（github.com 凭据链回到既有继承）",
            };
            acts.AddRange(SealCheck.StripSshBlock());
            // 审计 result 与 state.json 值逐字同形态（ToStateString）——哨兵/doctor 的伪造核对靠它对上
            Audit.Append("seal_pause", null, $"{minutes} 分钟", null, "approved",
                result: $"pause_until {SealCheck.SealPause.ToStateString(actual)}");
            Console.WriteLine($"✓ 封堵已暂停（至 {UntilLabel(actual)}）：");
            foreach (var a in acts) Console.WriteLine($"  - {a}");
            Console.WriteLine("到期后最迟 5 分钟自动恢复（哨兵）；手动立即恢复：sluice seal --resume。");
            return 0;
        });
    }

    /// <summary>结束暂停、立即重新封堵。恢复防护是安全侧动作——不过闸门（开门才要批准，关门不用）。</summary>
    public static int SealResume()
    {
        Console.WriteLine("Sluice seal --resume —— 结束暂停窗口，立即重新封堵");
        var acts = new List<string>();
        var wasActive = SealCheck.SealPause.Active;
        SealCheck.SealPause.Clear();
        acts.Add(wasActive ? "状态：暂停窗口已清除（提前结束）" : "状态：暂停窗口已清除（到期自愈）");

        var n = InstallNotice(quiet: true);
        acts.Add(n == 0 ? "notice helper：已确认在位/重装（github.com 链 = [空串断继承, notice]）"
                        : "notice helper：重装失败——请手动 sluice setup --install");

        var viol = SealCheck.Run();
        if (viol.Count > 0)
        {
            foreach (var item in viol) Console.WriteLine($"  ✗ 暂停期内引入：{item}");
            acts.AddRange(SealCheck.Fix(viol));
        }
        else acts.Add("seal 预检：无违规（暂停期内未引入新凭据）");

        var after = SealCheck.Run();
        Audit.Append("seal_resume", null, null, null, after.Count == 0 ? "approved" : "seal_violation",
            result: string.Join("；", after));
        Console.WriteLine();
        foreach (var a in acts) Console.WriteLine($"  - {a}");
        Console.WriteLine(after.Count == 0
            ? "✓ 环境已重新封堵。"
            : $"仍有 {after.Count} 处需人工处理（见上 ✗）。");
        return after.Count == 0 ? 0 : ExitCodes.SealViolation;
    }

    /// <summary>§6 卸载路径（红线：卸载后环境不得静默回到"全权 token 躺 keyring"状态）。
    /// 与 pause/unseal 同过闸门——它是摘除防护的动作。vault/config 不自动删（含加密 token，删除由人决定）。</summary>
    public static int Unseal()
    {
        if (!TokenStore.Exists) { Messages.Write(Messages.NotInitialized()); return ExitCodes.NotInitialized; }
        var body = """
            卸载 Sluice 的环境侧安装件：
            · 摘除 notice credential helper（github.com 凭据链回到系统默认）
            · 删除哨兵计划任务 "Sluice Sentinel"
            · 摘除 ~/.ssh/config 的 github.com 屏蔽块
            · 清除暂停状态（如有）
            vault（token.bin）与 config.json 保留，不自动删除。
            卸载后本机回到无闸门状态——重新登录 gh / 存凭据前请想清楚爆炸半径（建议只读 fine-grained PAT）。
            """;
        return RunLocalGated("unseal", "卸载侧门封堵", body,
            "卸载 = 移除闸门与封堵：此后 agent 直推是否发生不再有批准层与检测层",
            () =>
        {
            Git("config", "--global", "--unset-all", "credential.https://github.com.helper");
            var acts = new List<string> { "notice helper：已摘除" };
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("schtasks", "/Delete /TN \"Sluice Sentinel\" /F")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using (var p = System.Diagnostics.Process.Start(psi)!) p.WaitForExit(15000);
                acts.Add("哨兵：计划任务已删除");
            }
            catch { acts.Add("哨兵：任务删除失败——请手动 schtasks /Delete /TN \"Sluice Sentinel\" /F"); }
            acts.AddRange(SealCheck.StripSshBlock());
            SealCheck.SealPause.Clear();
            acts.Add("暂停状态：已清除（如有）");
            Audit.Append("unseal", null, null, null, "approved");
            Console.WriteLine("✓ 卸载完成：");
            foreach (var a in acts) Console.WriteLine($"  - {a}");
            Console.WriteLine($"""
                善后清单（§6 红线：卸载不得静默回到"全权 token 躺 keyring"）：
                · vault 与配置仍在 {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sluice")}（含加密 token）——彻底清除请手动删除该目录；
                · 工作区 AGENTS.md 的 Sluice 规则片段（如有）请手动移除标记块；
                · 重新引入直推凭据时自觉最小化：fine-grained、只读优先、短有效期。
                """);
            return 0;
        });
    }

    // ---------- sluice setup --install（v2.19，§9 notice 安装器） ----------

    /// <summary>
    /// 把本 exe 注册为全局 gitconfig 的 github.com 专属 credential helper（notice 模式）。
    /// 外科手术式（与 SSH 屏蔽块同纪律）：只动 credential.https://github.com.helper——先置空串
    /// （清空该 URL 从全局继承的 helper 链——GCM 对 github.com 不再应答，其他 host 不受影响），
    /// 再 add 本 exe notice。git 直推 github.com 将看到 §8 指引文案而非凭据 UI/静默凭据。
    /// 边界：`git -c credential.helper=…` 显式覆盖可绕过（T2 形状，§9 原文照录，不防）。
    /// </summary>
    public static int InstallNotice(bool quiet = false)
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(exe))
        {
            Console.Error.WriteLine("[Sluice] 无法定位自身 exe，安装中止（未产生任何变更）。");
            return ExitCodes.Internal;
        }
        if (exe.Contains(' '))
        {
            // gitconfig 的 helper 值经 shell 分词——含空格路径需引号转义，宁拒不装错
            Console.Error.WriteLine($"[Sluice] 发布路径含空格（{exe}），helper 值转义易错，安装中止。");
            Console.Error.WriteLine("[Sluice] 请把 sluice.exe 发布/复制到无空格路径后重跑 setup --install。");
            return ExitCodes.Internal;
        }
        var fwd = exe.Replace('\\', '/');
        const string key = "credential.https://github.com.helper";

        var existing = GitOut("--get-all", key);
        if (NoticeValuePresent(existing, fwd))
        {
            if (!quiet) Console.WriteLine($"[Sluice] notice helper 已在位：{key} 含 {fwd} notice，无需重复安装。");
            return 0;
        }

        // 序列（多值键不能用普通 set——gh 曾写入多条）：unset-all 清旧值（键不存在时非零，容忍）→
        // add 空串（清 github.com 从系统层继承的 GCM helper——外科手术式，其他 host 不受影响）→ add 本 exe。
        // 实弹教训（2026-11-03）：gh 写入的 "!gh auth git-credential" 桥接条目一并清掉——gh 登录状态下
        // 它会给 git 直供凭据，正是 §6 要封的侧门面；GCM 弹登录 UI 的诱导面也随空串一起消灭。
        Git("config", "--global", "--unset-all", key);
        if (Git("config", "--global", "--add", key, "") != 0 ||
            Git("config", "--global", "--add", key, $"{fwd} notice") != 0)
        {
            // 照实说（v2.23 审阅修复）：此刻 --unset-all 已执行、空串 --add 可能已落盘——
            // github.com helper 链处于半装状态（fail-closed 方向），不是"未产生任何变更"。
            Console.Error.WriteLine($"[Sluice] git config 写入失败（git 不在 PATH？）。注意：{key} 可能已处于半装状态——");
            Console.Error.WriteLine("    旧值已清（--unset-all），空串断继承可能已写入而 notice 未加（fail-closed 方向：github.com 直连拿不到凭据）。");
            Console.Error.WriteLine($"    重跑 sluice setup --install 可完成安装；或 git config --global --unset-all {key} 手动回退。");
            return ExitCodes.Internal;
        }

        if (!quiet) Console.WriteLine("[Sluice] 已安装 notice credential helper（仅作用 github.com；其他 host 的既有 helper 不受影响）。");
        if (!quiet)
            Console.WriteLine($"""
                验证：printf 'protocol=https\nhost=github.com\n\n' | git credential fill
                → stderr 应出现 [Sluice·终态] 指引，且输出无 password= 行。
                卸载：git config --global --unset-all {key}
                """);
        return 0;
    }

    /// <summary>git config --get-all 输出中是否存在指向 <paramref name="forwardSlashExe"/> 的 notice 值。
    /// v2.23 单源化（幂等检查与 doctor 共用）：子串匹配同时覆盖带引号与不带引号两种存储形态
    ///（"fwd notice" 是 "\"fwd notice\"" 的子串）；doctor 原来的 Contains("notice") 连指向已删除
    /// 旧 exe 路径的残留条目都判绿——doctor 的职责恰是发现漂移，必须前缀精确。</summary>
    internal static bool NoticeValuePresent(string getAllOutput, string forwardSlashExe) =>
        getAllOutput.Contains($"{forwardSlashExe} notice", StringComparison.Ordinal);

    private static int Git(params string[] args)
    {
        try
        {
            // 空串参数必须显式成 ""（join 后消失会让 git 误把 key 当 value）；
            // 含空格的参数（helper 值 "<exe> notice"）必须整体加引号，否则 git 收到多余参数
            var cmd = string.Join(' ', args.Select(a =>
                a.Length == 0 ? "\"\"" : a.Contains(' ') ? $"\"{a}\"" : a));
            var psi = new System.Diagnostics.ProcessStartInfo("git", cmd)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(15000);
            return p?.ExitCode ?? -1;
        }
        catch { return -1; }
    }

    private static string GitOut(string flag, string key)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", $"config --global {flag} {key}")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi);
            var s = p?.StandardOutput.ReadToEnd();
            p?.WaitForExit(15000);
            return s ?? "";
        }
        catch { return ""; }
    }

    // ---------- sluice setup --install-snippet（v2.21，§8.4 AGENTS.md 片段安装器） ----------

    /// <summary>片段正文 = plan-lite §8.4（v2.21 扩全动词面 + github_write 双前门表述；v2.25 增规则 7）。
    /// 与 §8.2/Messages 同纪律：改 §8.4 必须同步本常量——selftest 不锁散文，靠评审。标记块幂等。</summary>
    internal const string Snippet = """
        <!-- sluice-rules BEGIN（由 sluice setup --install-snippet 维护，手改会被覆盖） -->
        ## Sluice 规则（必须遵守）
        1. 一切 GitHub 写操作（评论 / PR 创建·合并·重开·关闭·编辑·评审 / push / issue 创建·关闭·重开·标签 / tag / release / 分支删除 / workflow 手动触发）只能经 `sluice` CLI 或 `github_write` 工具（MCP）执行；直接 git push / gh / curl 写 GitHub 会因无凭据失败（git 直连会收到 [Sluice·终态] notice 指引）。
        2. 输出中出现 `[Sluice·终态]`：这是最终结果。严格按消息内"要求"与 retry 语义执行；凡 retry=never / wait_user / wait_until / change_approach：禁止重试、禁止换方法、禁止尝试获取凭据或发起任何授权流程（含 gh auth login / device flow / 浏览器授权页）。
        3. 仅 retry=after_fix / retry=retry_ok 允许按消息说明修正或重试同一命令。
        4. 收到 seal_violation：把消息原文转告用户，暂停所有写操作。
        5. 非零退出且输出中没有任何 `[Sluice]` 字样：视为结果未知（Sluice 可能崩溃于执行中途），按"先查证、勿重发"处理。
        6. 一切 GitHub API 读操作用 `sluice api`（读有日志、可核对，防"我读过"幻觉）；git fetch/pull 使用环境中的只读凭据。
        7. `github_write` 返回 code=23（pending，附 receipt）：批准窗口已弹出、用户尚未决策，这不是失败。等待约 15 秒后用 `sluice_status` 工具（参数 receipt；无该工具时 CLI 等价 `sluice status <receipt>`）查终态（用户拒绝理由原文在此，必须遵守）；终态出现前禁止重发同一写操作（重发只会再弹一窗）。status 查询无副作用，可间隔重查。
        <!-- sluice-rules END -->
        """;

    internal const string SnippetBegin = "<!-- sluice-rules BEGIN";
    internal const string SnippetEnd = "<!-- sluice-rules END -->";

    /// <summary>标记块替换（纯函数，v2.23 入 selftest 锁）：END 只认 BEGIN **之后**的第一个——
    /// AGENTS.md 前文若引用了标记字符串不得错切（v2.20 原全文搜索会把前文的 END 引用当块尾）。
    /// 返回 null = 无 BEGIN（调用方走追加分支）或有 BEGIN 而其后无 END（手改损坏，调用方报错）。</summary>
    internal static string? ReplaceSnippetBlock(string txt, string block)
    {
        var s = txt.IndexOf(SnippetBegin, StringComparison.Ordinal);
        if (s < 0) return null;
        var e = txt.IndexOf(SnippetEnd, s, StringComparison.Ordinal);
        if (e < 0) return null;
        e += SnippetEnd.Length;
        // 吃掉 END 行尾换行，块自带结尾换行
        return txt[..s] + block + txt[e..].TrimStart('\r', '\n');
    }

    /// <summary>把 §8.4 片段写进当前工作区 AGENTS.md。标记块幂等（重跑=刷新，手改会被覆盖）；
    /// 无 AGENTS.md 则新建。装完提示用户把文件提交进仓库（§8.4 的可见性对人才有效）。</summary>
    public static int InstallSnippet()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), "AGENTS.md");
        var block = Snippet + Environment.NewLine;
        if (File.Exists(path))
        {
            var txt = File.ReadAllText(path);
            if (txt.Contains(SnippetBegin, StringComparison.Ordinal))
            {
                var updated = ReplaceSnippetBlock(txt, block);
                if (updated is null)
                {
                    Console.Error.WriteLine("[Sluice] AGENTS.md 标记块损坏（有 BEGIN 而其后无 END），请人工修复后重跑。");
                    return ExitCodes.Internal;
                }
                File.WriteAllText(path, updated);
                Console.WriteLine($"[Sluice] 已刷新既有片段（{path}）。");
            }
            else
            {
                File.AppendAllText(path, Environment.NewLine + Environment.NewLine + block);
                Console.WriteLine($"[Sluice] 已追加片段到 {path}。");
            }
        }
        else
        {
            File.WriteAllText(path, block);
            Console.WriteLine($"[Sluice] 已新建 {path} 并写入片段。");
        }
        Console.WriteLine("[Sluice] 记得把 AGENTS.md 提交进仓库——片段对 agent 的约束力来自项目可见性。");
        return 0;
    }

    // ---------- sluice doctor（v2.21，V1.5 项：一次性健康汇总） ----------

    /// <summary>六项检查：版本戳 / vault 形态 / config.json / seal 预检 / notice helper / 哨兵任务。
    /// 全绿 exit 0，有红 exit 1（脚本可判）。只读不修——修的动作各归各位（seal --fix / setup --install / sentinel-install）。
    /// v2.23：暂停窗口内语义翻转——notice 摘除是 pause 的预期效果（原建议 setup --install 与暂停语义
    /// 直接冲突，照做=把刚摘的 helper 装回去）；seal 预检显示"暂停中"而非误导性的"无违规"；
    /// 暂停值无对应 seal_pause 审计记录按疑似伪造报红（§14 L11）。</summary>
    public static int Doctor()
    {
        int bad = 0;
        void Item(bool ok, string name, string detail)
        {
            Console.WriteLine($"  {(ok ? "✓" : "✗")} {name}{(detail.Length > 0 ? $"：{detail}" : "")}");
            if (!ok) bad++;
        }

        Console.WriteLine("Sluice doctor —— 只读健康检查（修复动作见各项指引）");
        var v = typeof(Program).Assembly.GetName().Version;
        Console.WriteLine($"  · sluice v{v?.ToString(3)} · {Program.BuildStamp}");

        // vault：存在性 + 形态（V1 Hello 派生 / SLCV2 静默）
        if (!TokenStore.Exists)
            Item(false, "vault", "token.bin 不存在——运行 sluice setup");
        else if (TokenStore.IsDpapiVault)
            Item(true, "vault", "SLCV2 静默格式（DPAPI，批准=桌面点击，§14 L1 让步在册）");
        else
        {
            try
            {
                var ch = TokenStore.PeekChallenge();
                Item(ch.Length > 0, "vault", ch.Length > 0 ? "V1 Hello 派生格式（批准=Windows Hello）" : "V1 格式但挑战缺失——重跑 setup");
            }
            catch (Exception ex) { Item(false, "vault", $"读取异常（{ex.GetType().Name}）——重跑 setup"); }
        }

        // config.json 可解析
        try
        {
            var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sluice", "config.json");
            if (!File.Exists(cfg)) Item(true, "config", "缺省（默认值生效）");
            else
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfg));
                Item(true, "config", "可解析");
            }
        }
        catch (Exception ex) { Item(false, "config", $"解析失败（{ex.Message}）——人工检查"); }

        // seal 预检——暂停窗口内/过期未愈/正常三态（v2.23），正常态才跑写路径同款轻量检查
        var pauseUntil = SealCheck.SealPause.Until();
        var paused = pauseUntil > DateTime.UtcNow;                       // null（无暂停）→ false
        var expiring = pauseUntil is not null && !paused;               // 过期未愈：哨兵 ≤5 分钟自愈
        if (paused)
        {
            var auditOk = SealCheck.SealPause.HasAuditRecord(pauseUntil!.Value);
            Item(auditOk, "seal 预检", auditOk
                ? $"暂停中（至 {UntilLabel(pauseUntil.Value)}）——期内检测放行（seal --pause），resume 后自动恢复"
                : $"暂停中（至 {UntilLabel(pauseUntil.Value)}）但 state.json 无对应 seal_pause 审计记录——疑似伪造（§14 L11），sluice seal --resume 立即恢复封堵");
        }
        else if (expiring)
        {
            Item(true, "seal 预检", $"暂停已过期（{UntilLabel(pauseUntil!.Value)} 到期）——自动恢复中：哨兵最迟 5 分钟 / 手动 seal --resume 立即");
        }
        else
        {
            var viol = SealCheck.Run();
            Item(viol.Count == 0, "seal 预检", viol.Count == 0 ? "无违规" : $"{viol.Count} 处违规——sluice seal --fix");
        }

        // notice helper——ProcessPath 前缀精确匹配（v2.23：原 Contains("notice") 连指向已删除旧 exe
        // 的残留条目都判绿）；暂停期内"不在位"是预期（pause 摘除了它），不得建议重装
        const string key = "credential.https://github.com.helper";
        var helpers = GitOut("--get-all", key);
        var fwd = Environment.ProcessPath?.Replace('\\', '/');
        var noticeIn = fwd is not null && NoticeValuePresent(helpers, fwd);
        var ghBridge = helpers.Contains("!gh", StringComparison.Ordinal);
        if (paused)
            Item(!noticeIn, "notice helper", noticeIn
                ? "暂停期内在位——直推将被它拦下（与暂停目的冲突），安装与暂停状态不一致，请核对"
                : "已摘除（暂停窗口内的预期状态——seal --resume 后自动重装）");
        else if (expiring)
            Item(true, "notice helper", "摘除中——暂停已过期，resume/哨兵将自动重装（≤5 分钟）");
        else
            Item(noticeIn && !ghBridge, "notice helper", noticeIn
                ? (ghBridge ? "在位但 gh 桥接条目重现——重跑 setup --install" : "在位（仅作用 github.com）")
                : "不在位——sluice setup --install");

        // 哨兵任务
        bool taskOk;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks", "/Query /TN \"Sluice Sentinel\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(15000);
            taskOk = p.ExitCode == 0;
        }
        catch { taskOk = false; }
        Item(taskOk, "哨兵任务", taskOk ? "已注册（每 5 分钟）" : "未注册——sluice sentinel-install");

        Console.WriteLine(bad == 0 ? "结论：全绿。" : $"结论：{bad} 项需处理（见上 ✗）。");
        return bad == 0 ? 0 : 1;
    }

    // ---------- sluice sentinel-install（v2.20，§6 哨兵） ----------

    /// <summary>
    /// 建 5 分钟间隔计划任务跑 seal 预检（SealCheck.Run 同一套轻量检查，非深扫），违规弹 Windows
    /// 通知。作用（§6）：封 device flow 的"当场成功"时间窗——agent 走 gh auth login 拿新 token 后
    /// 用 gh 直连，全程不经 sluice，最迟 5 分钟被看见。是检测不是阻止（N4 本地不可阻止）。
    /// 复用映射定案（§11.5）：schtasks + PowerShell + toast，C# 侧零检测代码——本方法只写 .ps1
    /// 与注册任务；检查逻辑单源在 sluice seal（预检 = 与写路径 50ms 自查同一套）。
    /// </summary>
    public static int SentinelInstall()
    {
        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(exe))
        {
            Console.Error.WriteLine("[Sluice] 无法定位自身 exe，安装中止（未产生任何变更）。");
            return ExitCodes.Internal;
        }

        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sluice");
        Directory.CreateDirectory(dir);
        var ps1 = Path.Combine(dir, "sentinel.ps1");
        // UTF-8 带 BOM：PS 5.1 对无 BOM 文件按 ANSI 解码，脚本里的中文与 ✗ 字面量会乱码
        File.WriteAllText(ps1, SentinelScript.Replace("__EXE__", exe.Replace('\\', '/')),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Console.WriteLine($"[Sluice] 已写入哨兵脚本：{ps1}");

        // 计划任务：每 5 分钟，当前用户交互会话（默认 /RU 调用者）。
        // v2.23 修复：-File 路径恒带引号、/TR 内层引号经 \" 转义——LOCALAPPDATA 路径常含空格
        //（Windows 用户名含空格即中招；v2.20 原版不转义，此类机器上任务每 5 分钟静默失败且
        // 连 sentinel.log 都不会有——原注释声称"均校验过不含空格"但并无任何校验）。
        // 转义方案 2026-11-03 实弹：含空格路径注册 → XML 拆分为 Command=powershell.exe +
        // Arguments=…-File "带空格路径" → /Run 执行成功（marker 落盘）。
        var psi = new System.Diagnostics.ProcessStartInfo("schtasks", SchtasksCreateArgs(SentinelTr(ps1)))
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using (var p = System.Diagnostics.Process.Start(psi)!)
        {
            p.WaitForExit(20000);
            var err = p.StandardError.ReadToEnd().Trim();
            if (p.ExitCode != 0)
            {
                Console.Error.WriteLine($"[Sluice] 计划任务注册失败（exit {p.ExitCode}）：{err}");
                Console.Error.WriteLine("[Sluice] 安装中止（脚本已写好，可手动注册，见上 ps1 路径）。");
                return ExitCodes.Internal;
            }
        }

        Console.WriteLine("""
            [Sluice] 已注册计划任务 "Sluice Sentinel"（每 5 分钟，登录会话内运行）。
            违规时弹 Windows 通知并写 sentinel.log；处理：sluice seal --fix。
            移除：schtasks /Delete /TN "Sluice Sentinel" /F
            注意：哨兵是检测不是阻止——最迟 5 分钟看见（§6 device flow 时间窗）。
            """);
        return 0;
    }

    /// <summary>哨兵 /TR 值：-File 路径恒带引号（LOCALAPPDATA 常含空格——Windows 用户名含空格即中招）。
    /// 形态入 selftest 锁（v2.23：v2.20 缺的正是这层引号，含空格机器上任务静默失败）。</summary>
    internal static string SentinelTr(string ps1) =>
        $"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"";

    /// <summary>schtasks /Create 参数串：/TR 的内层引号以 \" 转义（schtasks 按 argv 规则解析后
    /// 存为 Command/Arguments 两个字段，实弹验证见 SentinelInstall 注释）。转义入 selftest 锁。</summary>
    internal static string SchtasksCreateArgs(string tr) =>
        $"/Create /F /SC MINUTE /MO 5 /TN \"Sluice Sentinel\" /TR \"{tr.Replace("\"", "\\\"")}\"";

    /// <summary>哨兵脚本（__EXE__ 由安装器替换为绝对路径）。UTF-8 控制台先设好否则 ✗ 行匹配乱码；
    /// toast 双路：BurntToast 模块（装了就用）→ WinRT 原生（PowerShell AUMID，零依赖兜底）。
    /// v2.22 暂停感知：state.json 未到期 → 静默；已过期 → seal --resume 自愈 + toast（到期恢复不靠人记得）。
    /// v2.23：sentinel.log 超 1MB 滚动为 .1（违规持续期间每 5 分钟全量追加，无上限会失控）；
    /// 暂停态伪造核对（§14 L11）——state.json 的 pause_until 无对应 seal_pause 审计记录 → 疑似伪造告警
    ///（state.json 是同用户可写明文，一行写入未来时间戳即可静音检测层；audit.jsonl 同为可写，
    /// 本核对抬高门槛而非建立信任——检测层看住检测层）。</summary>
    private const string SentinelScript = """
        # Sluice 哨兵（§6）——由 sluice sentinel-install 生成；检测逻辑单源在 sluice seal（轻量预检）
        $ErrorActionPreference = 'SilentlyContinue'
        try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
        $exe = '__EXE__'
        $dir = Join-Path $env:LOCALAPPDATA 'Sluice'
        $log = Join-Path $dir 'sentinel.log'
        # v2.23：日志滚动（超 1MB 改名 .1，保留一代）——违规持续期间每 5 分钟追加全量输出，无上限会失控
        if ((Get-Item $log -ErrorAction SilentlyContinue).Length -gt 1MB) { Move-Item -Force $log "$log.1" }

        function Show-Toast($title, $body) {
            $shown = $false
            try {
                if (Get-Module -ListAvailable BurntToast) {
                    Import-Module BurntToast
                    New-BurntToastNotification -Text $title, $body
                    $shown = $true
                }
            } catch {}
            if (-not $shown) {
                try {
                    [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null
                    [Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType=WindowsRuntime] | Out-Null
                    $esc = [System.Security.SecurityElement]::Escape($body)
                    $xml = New-Object Windows.Data.Xml.Dom.XmlDocument
                    [void]$xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>$title</text><text>$esc</text></binding></visual></toast>")
                    $toast = New-Object Windows.UI.Notifications.ToastNotification $xml
                    [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe').Show($toast)
                } catch { Add-Content -Encoding UTF8 $log "$(Get-Date -Format s) toast 失败：$($_.Exception.Message)" }
            }
        }

        # 暂停窗口（v2.22 seal --pause）：未到期 → 交叉核对审计后静默放行；已过期 → seal --resume 自愈（重装 notice + seal --fix）+ toast
        $state = Join-Path $dir 'state.json'
        if (Test-Path $state) {
            try {
                $j = Get-Content -Raw -Encoding UTF8 $state | ConvertFrom-Json
                $until = [DateTime]::Parse($j.pause_until, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AdjustToUniversal)
                if ($until -gt [DateTime]::UtcNow) {
                    # v2.23（§14 L11）：state.json 同用户可写——伪造未来时间戳即可静音哨兵。
                    # pause_until 值须有逐字同形态的 seal_pause 审计记录（needle 由 C# 侧 ToStateString 生成）；
                    # 无对应记录 = 疑似伪造 → toast + 日志（audit 也可伪造，本核对是抬门槛不是建信任）
                    $audit = Join-Path $dir 'audit.jsonl'
                    $needle = "pause_until $($j.pause_until)"
                    $hit = $false
                    if (Test-Path $audit) { $hit = [bool](Select-String -Path $audit -SimpleMatch -Encoding UTF8 -Pattern $needle) }
                    if (-not $hit) {
                        Add-Content -Encoding UTF8 $log "$(Get-Date -Format s) 疑似伪造暂停：state.json 记 $($j.pause_until)，audit.jsonl 无对应 seal_pause 审计记录"
                        Show-Toast 'Sluice 哨兵：暂停状态可疑' 'state.json 的暂停无对应 seal_pause 审计记录——疑似被同用户进程伪造。运行 sluice seal --resume 立即恢复封堵。'
                    }
                    exit 0
                }
                $r = & $exe seal --resume 2>&1
                Add-Content -Encoding UTF8 $log "$(Get-Date -Format s) 暂停到期，自动恢复：`n$r`n"
                Show-Toast 'Sluice：暂停窗口结束' '已自动重新封堵（notice 重装 + seal --fix）。残留请看 sentinel.log。'
                exit 0
            } catch { Add-Content -Encoding UTF8 $log "$(Get-Date -Format s) state.json 解析异常（按正常预检继续）：$($_.Exception.Message)" }
        }

        $out = & $exe seal 2>&1
        if ($LASTEXITCODE -ne 7) { exit 0 }
        $viol = ($out | Where-Object { "$_" -match '✗' }) -join "`n"
        if (-not $viol) { $viol = '检测到未封堵的 GitHub 凭据（详见 sentinel.log）' }
        Add-Content -Encoding UTF8 $log "$(Get-Date -Format s) seal violations:`n$out`n"
        Show-Toast 'Sluice 哨兵：检测到未封堵的 GitHub 凭据' "运行 sluice seal --fix 处理。$($viol.Replace("`n", ' | '))"
        exit 7
        """;
}
