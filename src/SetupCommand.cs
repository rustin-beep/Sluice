using System.Text.Json;
using System.Text.Json.Nodes;

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
            var p = TokenStore.NewParams();
#if WINDOWS
            Console.WriteLine();
            Console.WriteLine("下一步将弹出 Windows Hello：创建/打开 vault 密钥并签名。");
            Console.WriteLine("这次验证会加密你的 token；此后每次写操作解密时都需要同样的生物验证（批准即解密）。");
            var sig = await HelloConsent.SignForSetupAsync(p.Challenge);
            if (sig is null)
            {
                Console.WriteLine("Windows Hello 未通过，setup 未完成（未保存任何内容）。");
                return 1;
            }
#else
            // Linux 强档（v2.38）：口令 = 同意因子（等价 Hello 生物事件的"agent 无从合成"位）。
            // 两次掩码输入设口令 → 签名挑战；此后每次批准在批准窗内输口令（批准即解密）。
            var sig = await PassConsent.SignForSetupAsync(p.Challenge, p.Salt);
            if (sig is null)
                return 1; // 未输入/两次不一致/过短——文案已由 PassConsent 给出
#endif
            TokenStore.Save(p, pat, sig);
        }
        else
        {
            // D1（2026-10-31）：config hello:false——vault 直接存静默格式，setup 全程无同意因子。
            // 代价：token 同用户进程可静默读取（owner 显式降级，见 Gate.HelloEnabled 注释）；
            // 改回 true 后重跑 setup 即恢复强档格式。Windows = DPAPI 包封；Linux = 0600 明文文件
            //（§1.5 ITokenStore 预案，同 L1 让步级）。
#if WINDOWS
            TokenStore.SaveDpapi(pat);
            Console.WriteLine("config hello:false——vault 已存为静默格式（DPAPI），批准不再需要 Windows Hello。");
#else
            TokenStore.Save0600(pat);
            Console.WriteLine("config hello:false——vault 已存为静默格式（0600 明文文件），批准不再需要口令。");
#endif
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
        Console.WriteLine("最后三步（承重墙 + 路标）：");
        Console.WriteLine("  1. sluice seal --fix   ——封堵环境里的直连凭据；");
        Console.WriteLine("  2. sluice setup --install ——安装 notice credential helper（git 直推 GitHub 时得到指引进 sluice 的消息，而非裸错）+ 当前工作区的 AGENTS.md 规则片段（agent 会话的路标；非 git 目录自动跳过，各工作区在仓库根补跑）；");
        Console.WriteLine("  3. sluice sentinel-install ——哨兵计划任务（每 5 分钟一扫，device flow 侧门最迟 5 分钟可见）。");
        return 0;
    }

    /// <summary>
    /// 逐字符掩码读取（* 回显，退格可删，ESC 清空放弃）。
    /// conhost 下无换行 Console.Write 不刷新显示（实测：提示行根本不出现），
    /// 且行缓冲 ReadLine 会把 PAT 明文留在屏幕滚动缓冲——逐字符读两个问题一起解决。
    /// stdin 被重定向（管道/脚本）时回退 ReadLine。
    /// v2.38 起 internal：Linux 口令叶（PassConsent 控制台面）复用同一读取器。
    /// </summary>
    internal static string ReadSecret()
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
            // 层 A：--fix 在干净环境也确保封堵键在位（生命周期内化——键缺失不是"违规"但属封堵面）
            if (fix && EnsureInteractiveNever() != 0) return ExitCodes.Internal;
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
        if (EnsureInteractiveNever() != 0) return ExitCodes.Internal;
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
            期内：git/gh 直推能力恢复（notice helper 摘除、GCM 无 UI 封堵键摘除、SSH 屏蔽块摘除）；sluice 写操作的 seal 门同步放行。
            到期：最迟 5 分钟内自动重新封堵（重装 notice + 封堵键 + 清理期内新登录的凭据）；提前结束用 sluice seal --resume。
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
                RemoveInteractiveNever(), // 层 A：暂停窗内 GCM 恢复可交互登录（与摘 notice 同一步骤）
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
        if (n == 0) acts.Add($"GCM 无 UI 封堵：已确保在位（{GcmInteractiveKey} = never）");

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
        var sentinelLine =
#if WINDOWS
            "· 删除哨兵计划任务 \"Sluice Sentinel\"";
#else
            "· 停用并删除哨兵 systemd 用户定时器（sluice-sentinel.timer）";
#endif
        var body = $"""
            卸载 Sluice 的环境侧安装件：
            · 摘除 notice credential helper（github.com 凭据链回到系统默认）
            · 摘除 GCM 无 UI 封堵键（credential.https://github.com.interactive）
            {sentinelLine}
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
            var acts = new List<string>
            {
                "notice helper：已摘除",
                RemoveInteractiveNever(), // 层 A：卸载不得留下被静默降级的 git（红线同理）
            };
#if WINDOWS
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("schtasks", "/Delete /TN \"Sluice Sentinel\" /F")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using (var p = System.Diagnostics.Process.Start(psi)!) p.WaitForExit(15000);
                acts.Add("哨兵：计划任务已删除");
            }
            catch { acts.Add("哨兵：任务删除失败——请手动 schtasks /Delete /TN \"Sluice Sentinel\" /F"); }
#else
            acts.AddRange(SentinelTearDown());
#endif
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
                · 宿主 hook 配置（ZCode / Claude Code 的 PreToolUse，如有）用 sluice setup --uninstall-hook 移除——hook 读封堵态放行只是 belt，配置残留本身要被说破；
                · 重新引入直推凭据时自觉最小化：fine-grained、只读优先、短有效期。
                """);
            return 0;
        });
    }

    // ---------- GCM 无 UI 封堵键（spec-guard-20261112 层 A：seal 生命周期内化） ----------

    /// <summary>host 作用域键（外科纪律与 notice 的空串断继承同族：只断 GitHub，不碰其他 host 的
    /// GCM 交互）。P-探针（2026-11-12，GCM 2.7.3）：置 never 后 git credential fill 得
    /// "Cannot prompt because user interactivity has been disabled"，毫秒级败、无 UI——错门
    /// 死得快且不挂起。人的逃生口（git -c credential.https://github.com.interactive=always 或
    /// GCM_INTERACTIVE=always）只进人文档（spec / README），禁止进任何 agent 可见文案
    /// （§8 纪律：不教绕过，与 device flow 点名禁令同款理由）。</summary>
    internal const string GcmInteractiveKey = "credential.https://github.com.interactive";

    /// <summary>确保 GCM 无 UI 封堵键在位（setup --install / seal --fix / seal --resume 三态共用；
    /// 幂等：--get 已为 never 则不动）。写失败照实报错——fail-closed 方向（宁可装不上也不静默缺键）。</summary>
    public static int EnsureInteractiveNever(bool quiet = false)
    {
        if (GitOut("--get", GcmInteractiveKey).Trim() == "never")
        {
            if (!quiet) Console.WriteLine($"[Sluice] GCM 无 UI 封堵键已在位：{GcmInteractiveKey} = never。");
            return 0;
        }
        // 单值键：先清再 add（用户手改过的其他值一并归位——本键生命周期归 seal 管）
        Git("config", "--global", "--unset-all", GcmInteractiveKey); // 失败=本就不在位
        if (Git("config", "--global", "--add", GcmInteractiveKey, "never") != 0)
        {
            Console.Error.WriteLine($"[Sluice] git config 写入失败（git 不在 PATH？）：{GcmInteractiveKey} = never 未生效——GCM 仍可对 github.com 弹登录窗。");
            return ExitCodes.Internal;
        }
        if (!quiet) Console.WriteLine($"[Sluice] 已置 GCM 无 UI 封堵键（{GcmInteractiveKey} = never）——GCM 对 github.com 拒答不弹窗。");
        return 0;
    }

    /// <summary>摘除封堵键（seal --pause / unseal：暂停窗内人可交互登录，与摘 notice helper 同步）。
    /// 返回善后清单行（调用方汇入 acts）。</summary>
    internal static string RemoveInteractiveNever()
    {
        Git("config", "--global", "--unset-all", GcmInteractiveKey); // 失败=本就不在位
        return $"GCM 无 UI 封堵：已摘除（{GcmInteractiveKey}）——GCM 恢复可交互登录";
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
            Console.Error.WriteLine($"[Sluice] 请把 {Platform.BinName} 发布/复制到无空格路径后重跑 setup --install。");
            return ExitCodes.Internal;
        }
        var fwd = exe.Replace('\\', '/');
        const string key = "credential.https://github.com.helper";

        // 层 A（spec-guard-20261112）：封堵键与 notice 同生命周期，且必须在位检查**之前**确保——
        // "notice 已在位而键缺失"（旧版安装 / pause 摘除后 resume）是漂移形态，早退路径也要治
        var g = EnsureInteractiveNever(quiet);
        if (g != 0) return g;

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

    // ---------- sluice setup --install 扩为环境安装器（v2.34 发现时机批，incident-deferred-20261109） ----------

    /// <summary>
    /// --install 从单一 notice 安装器扩为环境安装器：notice（②层，承重）成功后，对当前工作区顺带
    /// 装 §8.4 AGENTS.md 片段（①'层——agent 会话的路标）。缘起：2026-11-08 vivarium 实弹——①层被
    /// MCP 宿主的 deferred 机制降级（工具 schema 不在请求信封，结构性在场不成立）+ ①'缺位（--install-snippet
    /// 是独立可选动词，作者自己在活跃工作区也忘了跑），agent 直推 git 撞 notice 才找到门（audit 83 行
    /// op=notice，簇状分布 = 每个新会话的首次接触都重付一遍发现成本）。修法 = 封门时顺手立路标：
    /// CWD 是 git 工作区（有 .git）或已有 AGENTS.md 时默认装片段；否则跳过（不在任意目录撒 AGENTS.md），
    /// 退出阀 --no-snippet。notice 失败即停不装片段（②承重、①'是糖，承重失败时糖无意义）；
    /// 片段失败不回滚 notice（①'缺位只是发现成本，不是越权面）。
    /// </summary>
    public static int InstallEnv(bool noSnippet)
    {
        var n = InstallNotice();
        if (n != 0) return n;

        if (noSnippet) return 0;
        var cwd = Directory.GetCurrentDirectory();
        if (!Directory.Exists(Path.Combine(cwd, ".git")) && !File.Exists(Path.Combine(cwd, "AGENTS.md")))
        {
            Console.WriteLine("[Sluice] 当前目录非 git 工作区也无 AGENTS.md——片段未装（避免在任意目录建 AGENTS.md）。");
            Console.WriteLine("[Sluice] 在每个 agent 工作区（仓库根）各跑一次：sluice setup --install-snippet。");
            return 0;
        }
        return InstallSnippet();
    }

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

    internal static string GitOut(string flag, string key)
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
        6. 一切 GitHub API 读操作用 `sluice api`（issue 清单/按标签或关键词过滤用 `sluice issue list`；读有日志、可核对，防"我读过"幻觉）；git fetch/pull 使用环境中的只读凭据。
        7. `github_write` 返回 code=23（pending，附 receipt）：批准窗口已弹出、用户尚未决策，这不是失败。等待约 15 秒后用 `sluice_status` 工具（参数 receipt；无该工具时 CLI 等价 `sluice status <receipt>`）查终态（用户拒绝理由原文在此，必须遵守）；终态出现前禁止重发同一写操作——v2.31 起重发会被闸门去重拦截并并入原回执（不再另开新窗）。status 查询无副作用，可间隔重查。
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

    internal enum AgentsVisibility { BlockFound, AgentsWithoutBlock, RepoWithoutAgents, NotAWorkspace }

    /// <summary>从 <paramref name="startDir"/> 逐级向上找最近的 AGENTS.md（含标记块与否），搜索窗口
    /// 止于 .git 边界（与 ZCode 的工作区解析同窗口：CWD→项目根，仓库根本身是窗口终点）。每级先看
    /// AGENTS.md 再看 .git——根目录两者并存时 AGENTS.md 优先。纯文件系统探测（doctor 与 selftest 共用，
    /// v2.34 入锁）。不可读的 AGENTS.md 视为无块（fail 方向：提示人工处理）。</summary>
    internal static (AgentsVisibility V, string? Path) ResolveAgentsVisibility(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "AGENTS.md");
            if (File.Exists(agents))
            {
                var hasBlock = false;
                try { hasBlock = File.ReadAllText(agents).Contains(SnippetBegin, StringComparison.Ordinal); }
                catch { /* 不可读 → 按无块报，指引人工 */ }
                return (hasBlock ? AgentsVisibility.BlockFound : AgentsVisibility.AgentsWithoutBlock, agents);
            }
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return (AgentsVisibility.RepoWithoutAgents, dir.FullName);
            dir = dir.Parent;
        }
        return (AgentsVisibility.NotAWorkspace, null);
    }

    // ---------- sluice doctor（v2.21，V1.5 项：一次性健康汇总） ----------

    /// <summary>八项检查：版本戳 / vault 形态 / config.json / seal 预检 / notice helper / GCM 封堵键
    ///（v2.37）/ 哨兵任务 / AGENTS.md 片段（v2.34）。全绿 exit 0，有红 exit 1（脚本可判）。只读不修——修的动作各归各位
    /// （seal --fix / setup --install / setup --install-snippet / sentinel-install）。
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

        // vault：存在性 + 形态（V1 同意因子派生 / 静默降级档）
        if (!TokenStore.Exists)
            Item(false, "vault", "token.bin 不存在——运行 sluice setup");
        else if (TokenStore.IsSilentVault)
            Item(true, "vault",
#if WINDOWS
                "SLCV2 静默格式（DPAPI，批准=桌面点击，§14 L1 让步在册）");
#else
                "SLCF1 静默格式（0600 明文文件，批准=桌面点击，§14 L1 让步在册）");
#endif
        else
        {
            try
            {
                var ch = TokenStore.PeekChallenge();
                Item(ch.Length > 0, "vault", ch.Length > 0
#if WINDOWS
                    ? "V1 Hello 派生格式（批准=Windows Hello）"
#else
                    ? "V1 口令派生格式（批准=批准窗内输口令）"
#endif
                    : "V1 格式但挑战缺失——重跑 setup");
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

        // GCM 无 UI 封堵键（v2.37 审阅 P3-6）：键被摘（pause 后崩溃 / 人手动改）不再静默全绿——
        // 正是"三个月后静默失效"形态。写门不拦（定性：UX 层键非凭据面——侧门另由 notice/seal 面
        // hold，键缺失的代价是首错回到弹窗挂起，不是凭据泄漏）；drift 由本项报红 +
        // 下次 --install/--fix 自愈。暂停语义与 notice 项同款三态
        var interactive = GitOut("--get", GcmInteractiveKey).Trim();
        if (paused)
            Item(true, "GCM 封堵键", "已摘除（暂停窗口内预期状态——resume 后自动重装）");
        else if (expiring)
            Item(true, "GCM 封堵键", "摘除中——暂停已过期，resume/哨兵将自动重装（≤5 分钟）");
        else
            Item(interactive == "never", "GCM 封堵键", interactive == "never"
                ? "在位（interactive=never，GCM 对 github.com 拒答不弹窗）"
                : $"不在位（现值「{interactive}」）——sluice seal --fix 重置");

        // 哨兵任务（Windows = schtasks 计划任务；Linux = systemd 用户定时器）
        bool taskOk;
        try
        {
#if WINDOWS
            var psi = new System.Diagnostics.ProcessStartInfo("schtasks", "/Query /TN \"Sluice Sentinel\"")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
#else
            var psi = new System.Diagnostics.ProcessStartInfo("systemctl", "--user is-enabled sluice-sentinel.timer")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
#endif
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(15000);
            taskOk = p.ExitCode == 0;
        }
        catch { taskOk = false; }
        Item(taskOk, "哨兵任务", taskOk
#if WINDOWS
            ? "已注册（每 5 分钟）" : "未注册——sluice sentinel-install");
#else
            ? "已注册（每 5 分钟，systemd 用户定时器）"
            : "未注册——sluice sentinel-install（需 systemd；WSL 需 /etc/wsl.conf [boot] systemd=true）");
#endif

        // AGENTS.md 片段（v2.34 发现时机批，§8.4 ①'层）：agent 会话的路标，缺位 = 每个新会话重付
        // 发现成本（incident-deferred-20261109：①层被宿主 deferred 降级时，①' 是仅存的提示层）。
        // 非 git 工作区中性跳过（✓ 不冒充红）；暂停窗口不影响本项（片段与凭据封堵正交）。
        var (av, apath) = ResolveAgentsVisibility(Directory.GetCurrentDirectory());
        switch (av)
        {
            case AgentsVisibility.BlockFound:
                Item(true, "AGENTS.md 片段", $"在位（{apath}）");
                break;
            case AgentsVisibility.AgentsWithoutBlock:
                Item(false, "AGENTS.md 片段", $"{apath} 无规则标记块——在该仓库根运行 sluice setup --install-snippet");
                break;
            case AgentsVisibility.RepoWithoutAgents:
                Item(false, "AGENTS.md 片段", $"git 工作区（{apath}）无 AGENTS.md——在仓库根运行 sluice setup --install-snippet（新建）");
                break;
            default:
                Item(true, "AGENTS.md 片段", "非 git 工作区，跳过（片段装在 agent 工作区才有效）");
                break;
        }

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
#if WINDOWS
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
#else
        // Linux 叶（v2.38）：systemd 用户定时器 + notify-send——架构镜像 schtasks + ps1 + toast。
        // systemd 不可用（WSL 未开 systemd 等）→ 如实失败并指路，不静默降级。
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sluice");
        Directory.CreateDirectory(dataDir);
        var sh = Path.Combine(dataDir, "sentinel.sh");
        File.WriteAllText(sh, SentinelShScript.Replace("__EXE__", exe));
        // 0755：timer 以本用户跑 service，service 跑脚本——属主可执行即可，组/他只读
#pragma warning disable CA1416 // Linux 叶在 #if !WINDOWS 内；net10.0 TFM 声明全平台可达引发误报
        File.SetUnixFileMode(sh, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite
            | System.IO.UnixFileMode.UserExecute | System.IO.UnixFileMode.GroupRead | System.IO.UnixFileMode.GroupExecute
            | System.IO.UnixFileMode.OtherRead | System.IO.UnixFileMode.OtherExecute);
#pragma warning restore CA1416
        Console.WriteLine($"[Sluice] 已写入哨兵脚本：{sh}");

        var unitDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "systemd", "user");
        Directory.CreateDirectory(unitDir);
        File.WriteAllText(Path.Combine(unitDir, "sluice-sentinel.service"), SentinelUnitServiceText(sh));
        File.WriteAllText(Path.Combine(unitDir, "sluice-sentinel.timer"), SentinelUnitTimerText());
        Console.WriteLine($"[Sluice] 已写入 unit 文件：{unitDir}/sluice-sentinel.{{service,timer}}");

        var rl = SystemCapture("--user daemon-reload");
        var en = rl != 0 ? rl : SystemCapture("--user enable --now sluice-sentinel.timer");
        if (en != 0)
        {
            Console.Error.WriteLine($"[Sluice] systemd 用户定时器启用失败（daemon-reload/enable exit {en}）。");
            Console.Error.WriteLine("[Sluice] 请确认 systemd 可用（WSL：/etc/wsl.conf 加 [boot]\\nsystemd=true 后 wsl --shutdown 重启）；");
            Console.Error.WriteLine("[Sluice] unit 与脚本已写好，恢复 systemd 后重跑 sluice sentinel-install。");
            return ExitCodes.Internal;
        }

        Console.WriteLine("""
            [Sluice] 已启用 systemd 用户定时器 sluice-sentinel.timer（每 5 分钟）。
            违规时 notify-send 弹通知并写 sentinel.log；处理：sluice seal --fix。
            移除：systemctl --user disable --now sluice-sentinel.timer
            注意：哨兵是检测不是阻止——最迟 5 分钟看见（§6 device flow 时间窗）。
            """);
        return 0;
#endif
    }

#if !WINDOWS
    /// <summary>卸载侧：停用定时器 + 删 unit 文件 + daemon-reload（unseal 用；sentinel.sh 保留——
    /// 与 Windows 侧不删 sentinel.ps1 同款残留形态）。逐项如实回报，失败不掩。</summary>
    private static List<string> SentinelTearDown()
    {
        var acts = new List<string>();
        var unitDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "systemd", "user");
        _ = SystemCapture("--user disable --now sluice-sentinel.timer"); // 未装/已停 → 非零，照删文件
        try
        {
            File.Delete(Path.Combine(unitDir, "sluice-sentinel.service"));
            File.Delete(Path.Combine(unitDir, "sluice-sentinel.timer"));
            _ = SystemCapture("--user daemon-reload");
            acts.Add("哨兵：systemd 定时器已停用并删除 unit 文件");
        }
        catch (Exception ex)
        {
            acts.Add($"哨兵：unit 文件删除失败（{ex.Message}）——请手动 systemctl --user disable --now sluice-sentinel.timer 并删 {unitDir}/sluice-sentinel.*");
        }
        return acts;
    }

    /// <summary>systemctl 捕获式执行（doctor/uninstall 共用；失败 = 非零退出码）。</summary>
    private static int SystemCapture(string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("systemctl", args)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            p.WaitForExit(15000);
            return p.ExitCode;
        }
        catch { return -1; }
    }
#endif

    /// <summary>哨兵 /TR 值：-File 路径恒带引号（LOCALAPPDATA 常含空格——Windows 用户名含空格即中招）。
    /// 形态入 selftest 锁（v2.23：v2.20 缺的正是这层引号，含空格机器上任务静默失败）。</summary>
    internal static string SentinelTr(string ps1) =>
        $"powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"";

    /// <summary>schtasks /Create 参数串：/TR 的内层引号以 \" 转义（schtasks 按 argv 规则解析后
    /// 存为 Command/Arguments 两个字段，实弹验证见 SentinelInstall 注释）。转义入 selftest 锁。</summary>
    internal static string SchtasksCreateArgs(string tr) =>
        $"/Create /F /SC MINUTE /MO 5 /TN \"Sluice Sentinel\" /TR \"{tr.Replace("\"", "\\\"")}\"";

    // ---------- 哨兵 Linux 叶文本（v2.38；纯函数两 TFM 皆编，selftest 锁——沿 SentinelTr 先例） ----------

    /// <summary>service unit（oneshot 跑哨兵脚本；ExecStart 恒带引号——与 HookGuardCommand/SentinelTr
    /// 同族"宁恒引号不赌路径"纪律，systemd 支持 ExecStart 首参数引号包裹；SuccessExitStatus=7——
    /// 违规轮以 7 退出是**检测成功**而非服务故障，不标红 job 免误报噪音，信号由 sentinel.log/toast 承载）。
    /// 文本入 selftest 锁。</summary>
    internal static string SentinelUnitServiceText(string scriptPath) => $"""
        [Unit]
        Description=Sluice Sentinel seal preflight (plan-lite §6)

        [Service]
        Type=oneshot
        ExecStart="{scriptPath}"
        SuccessExitStatus=7
        """;

    /// <summary>timer unit：OnUnitActiveSec=5min 对齐 schtasks /SC MINUTE /MO 5；Persistent 补错过的
    /// 停机窗口（WSL 常态关机，重开后立即补跑一次）。文本入 selftest 锁。</summary>
    internal static string SentinelUnitTimerText() => """
        [Unit]
        Description=Sluice Sentinel timer (5 min)

        [Timer]
        OnBootSec=2min
        OnUnitActiveSec=5min
        AccuracySec=30s
        Persistent=true

        [Install]
        WantedBy=timers.target
        """;

    /// <summary>哨兵脚本 Linux 叶（__EXE__ 由安装器替换为绝对路径）。逐句镜像 sentinel.ps1 语义：
    /// 日志 1MB 滚动（保留一代）；notify-send 不可用则降级写日志；暂停窗口未到期 → 审计交叉核对
    /// （§14 L11，needle = "pause_until &lt;ISO8601Z&gt;"，与 ToStateString 逐字同形态）后静默放行、
    /// 无对应 seal_pause 记录 = 疑似伪造 → 告警；已过期 → seal --resume 自愈 + 通知；正常 → seal
    /// 预检，exit 7 时提取 ✗ 行通知。POSIX sh（非 bash）——dash 兼容，无 bash 依赖。</summary>
    private const string SentinelShScript = """
        #!/bin/sh
        # Sluice 哨兵 Linux 叶（§6）——由 sluice sentinel-install 生成；检测逻辑单源在 sluice seal
        EXE='__EXE__'
        DIR="${XDG_DATA_HOME:-$HOME/.local/share}/Sluice"
        LOG="$DIR/sentinel.log"
        # 日志滚动（超 1MB 改名 .1，保留一代）——违规持续期间每 5 分钟全量追加，无上限会失控
        if [ -f "$LOG" ] && [ "$(wc -c < "$LOG")" -gt 1048576 ]; then mv -f "$LOG" "$LOG.1"; fi

        toast() {
          # systemd 用户服务的 PATH 受限——command -v 找不到时退绝对路径再试一次（装了 libnotify-bin 即在）
          NS=$(command -v notify-send 2>/dev/null || echo /usr/bin/notify-send)
          if [ -x "$NS" ]; then
            "$NS" -a Sluice -u critical "$1" "$2" && return 0
          fi
          echo "$(date -Is) toast 失败：notify-send 不可用（libnotify-bin）" >> "$LOG"
        }

        # 暂停窗口（seal --pause）：未到期 → 交叉核对审计后静默放行；已过期 → seal --resume 自愈 + 通知
        STATE="$DIR/state.json"
        if [ -f "$STATE" ]; then
          UNTIL=$(sed -n 's/.*"pause_until"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$STATE" 2>/dev/null)
          if [ -n "$UNTIL" ]; then
            NOW=$(date -u +%Y-%m-%dT%H:%M:%SZ)
            if [ "$UNTIL" \> "$NOW" ]; then
              # state.json 同用户可写——伪造未来时间戳即可静音哨兵；pause_until 值须有逐字同形态的
              # seal_pause 审计记录（ToStateString），无对应记录 = 疑似伪造 → 告警（抬门槛非建信任）
              if grep -Fq "pause_until $UNTIL" "$DIR/audit.jsonl" 2>/dev/null; then exit 0; fi
              echo "$(date -Is) 疑似伪造暂停：state.json 记 $UNTIL，audit.jsonl 无对应 seal_pause 审计记录" >> "$LOG"
              toast 'Sluice 哨兵：暂停状态可疑' 'state.json 的暂停无对应 seal_pause 审计记录——疑似被同用户进程伪造。运行 sluice seal --resume 立即恢复封堵。'
              exit 0
            fi
            OUT=$("$EXE" seal --resume 2>&1)
            echo "$(date -Is) 暂停到期，自动恢复：
        $OUT" >> "$LOG"
            toast 'Sluice：暂停窗口结束' '已自动重新封堵（notice 重装 + seal --fix）。残留请看 sentinel.log。'
            exit 0
          fi
        fi

        OUT=$("$EXE" seal 2>&1)
        RC=$?
        [ "$RC" -ne 7 ] && exit 0
        VIOL=$(printf '%s\n' "$OUT" | grep '✗')
        [ -z "$VIOL" ] && VIOL='检测到未封堵的 GitHub 凭据（详见 sentinel.log）'
        echo "$(date -Is) seal violations:
        $OUT" >> "$LOG"
        toast 'Sluice 哨兵：检测到未封堵的 GitHub 凭据' "运行 sluice seal --fix 处理。$(printf '%s' "$VIOL" | tr '\n' '|')"
        exit 7
        """;

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

    // ---------- setup --install-hook / --uninstall-hook（spec-guard-20261112 层 B §2.2，宿主注册表驱动） ----------

    /// <summary>hook 命令值（纯函数，selftest 锁）：exe 绝对路径恒带内层引号——含空格路径的坑与
    /// 哨兵 /TR 引号（v2.23 P1）同族，配置值经宿主 shell 分词，宁恒引号不赌路径；正斜杠形态
    /// （与 notice helper 一致）。</summary>
    internal static string HookGuardCommand(string exe) =>
        $"\"{exe.Replace('\\', '/')}\" hook-guard";

    /// <summary>宿主注册表（§2.2：不铆钉单一平台，出现等价 PreToolUse-deny 语义的宿主才收）。
    /// zcode 形状 = hooks.enabled + hooks.events.PreToolUse；claude-code 形状 = hooks.PreToolUse。</summary>
    private static (string Name, string Path, bool ZcodeShape)[] HookHosts(string home) =>
    [
        ("zcode",       Path.Combine(home, ".zcode", "cli", "config.json"), true),
        ("claude-code", Path.Combine(home, ".claude", "settings.json"),     false),
    ];

    private static (string Name, string Path, bool ZcodeShape)[]? SelectHookHosts(string sel) => sel switch
    {
        "all" => HookHosts(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        "zcode" or "claude-code" => HookHosts(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            .Where(h => h.Name == sel).ToArray(),
        _ => null,
    };

    public static int InstallHook(string sel)
    {
        var hosts = SelectHookHosts(sel);
        if (hosts is null)
        {
            Console.Error.WriteLine($"[Sluice] 用法：sluice setup --install-hook [zcode|claude-code|all]（收到 {sel}）");
            return ExitCodes.BadRequest;
        }
        var exe = Environment.ProcessPath;
        if (exe is null || !File.Exists(exe))
        {
            Console.Error.WriteLine("[Sluice] 无法定位自身 exe，安装中止（未产生任何变更）。");
            return ExitCodes.Internal;
        }

        var bad = 0;
        var anyHooked = false;
        foreach (var h in hosts)
        {
            var c = InstallHookFor(h, exe, out var hooked);
            if (c != 0) bad++;
            anyHooked |= hooked;
        }

        // 临时件收编（spec §3：产品化接管即删——.sh 内文案与 Messages 单源的漂移面随之消灭）。
        // v2.37 审阅 P2-3：仅在**装成至少一个宿主且无错**时才删——`--install-hook claude-code`
        // 而该宿主未装（全部跳过）或配置解析失败时，删脚本 = 拆了旧的没装新的
        if (bad == 0 && anyHooked) TryDeleteTempHookScript();
        else
        {
            var sh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sluice", "hook-guard.sh");
            if (File.Exists(sh))
                Console.WriteLine($"[Sluice] 临时件 {sh} 保留（本次未装成任何宿主——收编条件不满足，避免拆旧不装新）。");
        }
        return bad == 0 ? 0 : ExitCodes.Internal;
    }

    private static void TryDeleteTempHookScript()
    {
        var sh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sluice", "hook-guard.sh");
        if (!File.Exists(sh)) return;
        try { File.Delete(sh); Console.WriteLine($"[Sluice] 临时件已删除（产品化接管）：{sh}"); }
        catch (Exception ex) { Console.WriteLine($"⚠ 临时件删除失败（{ex.Message}）——请手动删除 {sh}"); }
    }

    /// <summary>幂等安装 / exe 路径漂移刷新（notice 的 --unset-all→--add 同纪律）：命中既有
    /// hook-guard 条目 → 刷新 command 为当前 exe；无 → 追加；顺路摘除临时 .sh 时代的旧条目。
    /// hooked = 宿主侧确有本 exe 的 hook 在位（新装/刷新/已在位）——临时件收编条件用它。</summary>
    private static int InstallHookFor((string Name, string Path, bool ZcodeShape) host, string exe, out bool hooked)
    {
        var (name, path, zcodeShape) = host;
        hooked = false;
        if (!Directory.Exists(Path.GetDirectoryName(path)!))
        {
            Console.WriteLine($"[Sluice] {name}：未检出（配置目录不存在），跳过——装好该宿主后重跑本命令。");
            return 0;
        }
        JsonObject root;
        try
        {
            root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                  ?? throw new InvalidOperationException("根节点不是 JSON 对象")
                : new JsonObject();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Sluice] {name}：配置解析失败（{ex.Message}）——该文件未改动，人工核对。");
            return 1;
        }

        var pre = EnsurePreToolUseArray(root, zcodeShape);
        // v2.37 审阅 P3-5：用户显式 hooks.enabled=false = 全局关 hooks——不翻 true（翻了会连带
        // 激活用户自有的其他 hook 条目）；条目照装（装是明确指令），但如实说破它不会生效
        var disabled = zcodeShape && root["hooks"]!["enabled"] is JsonValue v
            && v.TryGetValue<bool>(out var en) && !en;
        var cmd = HookGuardCommand(exe);
        var oursPresent = false;
        var refreshed = false;
        var legacyTaken = 0;

        for (var i = pre.Count - 1; i >= 0; i--)
        {
            if (pre[i] is not JsonObject entry || entry["hooks"] is not JsonArray ha) continue;
            var touched = false;
            for (var j = ha.Count - 1; j >= 0; j--)
            {
                if (ha[j] is not JsonObject hook || hook["command"]?.GetValue<string>() is not { } c) continue;
                if (c.Contains("hook-guard.sh", StringComparison.Ordinal))
                {
                    ha.RemoveAt(j); legacyTaken++; touched = true; // 临时件条目收编
                    continue;
                }
                if (c.EndsWith(" hook-guard", StringComparison.Ordinal))
                {
                    if (c == cmd) { oursPresent = true; continue; }
                    hook["command"] = cmd;                          // exe 路径漂移 → 刷新
                    if (zcodeShape) hook["timeoutMs"] = 3000;
                    refreshed = true; touched = true;
                }
            }
            if (touched && ha.Count == 0) pre.RemoveAt(i); // 壳被摘空 → 条目一并摘
        }

        if (!oursPresent && !refreshed)
            pre.Add((JsonNode)NewHookEntry(cmd, zcodeShape)); // JsonNode 转型走非泛型 Add（AOT/trim 告警）

        try { File.WriteAllText(path, root.ToJsonString(Indent)); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Sluice] {name}：配置写回失败（{ex.Message}）——hook 未装上，该文件可能未变。");
            return 1;
        }

        Console.WriteLine(refreshed
            ? $"[Sluice] {name}：hook 已刷新（exe 路径漂移 → {cmd}；{path}）。"
            : oursPresent
                ? $"[Sluice] {name}：hook 已在位，无需改动（{path}）。"
                : $"[Sluice] {name}：已安装 PreToolUse hook（matcher Bash → {cmd}；{path}）。");
        if (disabled)
            Console.WriteLine($"[Sluice] {name}：注意 hooks.enabled=false 在位——尊重全局关闭，本次条目不会生效（如需启用请手动改 true）。");
        if (legacyTaken > 0)
            Console.WriteLine($"[Sluice] {name}：顺路收编 {legacyTaken} 条临时 hook-guard.sh 旧条目（替换为内置模式）。");
        Console.WriteLine("[Sluice] 宿主在会话启动时读配置——本命令对已开着的会话不生效，自下一个新会话起拦截生效。");
        hooked = true;
        return 0;
    }

    public static int UninstallHook(string sel)
    {
        var hosts = SelectHookHosts(sel);
        if (hosts is null)
        {
            Console.Error.WriteLine($"[Sluice] 用法：sluice setup --uninstall-hook [zcode|claude-code|all]（收到 {sel}）");
            return ExitCodes.BadRequest;
        }
        var bad = 0;
        foreach (var (name, path, _) in hosts)
        {
            if (!File.Exists(path)) { Console.WriteLine($"[Sluice] {name}：无配置文件，跳过。"); continue; }
            JsonObject root;
            try { root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                  ?? throw new InvalidOperationException("根节点不是 JSON 对象"); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Sluice] {name}：配置解析失败（{ex.Message}）——该文件未改动。");
                bad++;
                continue;
            }
            // 两种宿主形状都摘（卸载不该假设当年装的是哪种形状）
            var arrays = new List<JsonArray>();
            if (root["hooks"] is JsonObject hooks)
            {
                if (hooks["PreToolUse"] is JsonArray a1) arrays.Add(a1);
                if (hooks["events"] is JsonObject ev && ev["PreToolUse"] is JsonArray a2) arrays.Add(a2);
            }
            var removed = 0;
            foreach (var arr in arrays)
                removed += RemoveHookGuardEntries(arr);
            if (removed > 0)
            {
                // v2.37 审阅 P3-5：摘空后的骨架清理——空 PreToolUse 数组、空 events、空 hooks
                // 一并摘；enabled 保留（无从判源——可能先于本安装存在，且空对象无行为影响）
                if (root["hooks"] is JsonObject hooks2)
                {
                    if (hooks2["events"] is JsonObject ev2 && ev2["PreToolUse"] is JsonArray a2 && a2.Count == 0)
                    { ev2.Remove("PreToolUse"); if (ev2.Count == 0) hooks2.Remove("events"); }
                    if (hooks2["PreToolUse"] is JsonArray a3 && a3.Count == 0) hooks2.Remove("PreToolUse");
                    if (hooks2.Count == 0) root.Remove("hooks");
                }
                try { File.WriteAllText(path, root.ToJsonString(Indent)); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Sluice] {name}：配置写回失败（{ex.Message}）。");
                    bad++;
                    continue;
                }
                Console.WriteLine($"[Sluice] {name}：已移除 {removed} 条 hook-guard 配置（{path}）。");
            }
            else Console.WriteLine($"[Sluice] {name}：无 hook-guard 配置，无需移除。");
        }
        TryDeleteTempHookScript();
        return bad == 0 ? 0 : ExitCodes.Internal;
    }

    /// <summary>hook-guard 条目摘除（纯函数，selftest 锁——v0.7.11 实弹抓到的倒数循环 j++ 笔误
    /// 正是本函数前身）：摘 marker 命中（内置模式 "… hook-guard" 或临时件 "…hook-guard.sh"）的
    /// hook 对象；hooks 数组摘空则连 matcher 条目一并摘；无关 hook / 无关条目一概不动。
    /// 返回摘除的 hook 对象数。</summary>
    internal static int RemoveHookGuardEntries(JsonArray pre)
    {
        var removed = 0;
        for (var i = pre.Count - 1; i >= 0; i--)
        {
            if (pre[i] is not JsonObject entry || entry["hooks"] is not JsonArray ha) continue;
            var touched = false;
            for (var j = ha.Count - 1; j >= 0; j--)
                if (ha[j] is JsonObject hook && hook["command"]?.GetValue<string>() is { } c
                    && (c.EndsWith(" hook-guard", StringComparison.Ordinal)
                        || c.Contains("hook-guard.sh", StringComparison.Ordinal)))
                { ha.RemoveAt(j); removed++; touched = true; }
            if (touched && ha.Count == 0) pre.RemoveAt(i);
        }
        return removed;
    }

    private static JsonSerializerOptions Indent { get; } = new() { WriteIndented = true };

    private static JsonArray EnsurePreToolUseArray(JsonObject root, bool zcodeShape)
    {
        var hooks = root["hooks"] as JsonObject ?? (JsonObject)(root["hooks"] = new JsonObject());
        if (!zcodeShape)
            return hooks["PreToolUse"] as JsonArray ?? (JsonArray)(hooks["PreToolUse"] = new JsonArray());
        // v2.37 审阅 P3-5：enabled 只在缺省时置 true——显式 false 是用户的全局开关，不覆盖
        if (hooks["enabled"] is null) hooks["enabled"] = true;
        var events = hooks["events"] as JsonObject ?? (JsonObject)(hooks["events"] = new JsonObject());
        return events["PreToolUse"] as JsonArray ?? (JsonArray)(events["PreToolUse"] = new JsonArray());
    }

    private static JsonObject NewHookEntry(string cmd, bool zcodeShape)
    {
        var hook = new JsonObject { ["type"] = "command", ["command"] = cmd };
        if (zcodeShape) { hook["timeoutMs"] = 3000; hook["statusMessage"] = "Sluice guard"; }
        // 显式 JsonNode 转型走非泛型 Add（泛型 Add<T> 带 RequiresDynamicCode 注解，AOT 告警）
        return new JsonObject { ["matcher"] = "Bash", ["hooks"] = new JsonArray((JsonNode)hook) };
    }
}
