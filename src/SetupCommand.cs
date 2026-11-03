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

    // ---------- sluice setup --install（v2.19，§9 notice 安装器） ----------

    /// <summary>
    /// 把本 exe 注册为全局 gitconfig 的 github.com 专属 credential helper（notice 模式）。
    /// 外科手术式（与 SSH 屏蔽块同纪律）：只动 credential.https://github.com.helper——先置空串
    /// （清空该 URL 从全局继承的 helper 链——GCM 对 github.com 不再应答，其他 host 不受影响），
    /// 再 add 本 exe notice。git 直推 github.com 将看到 §8 指引文案而非凭据 UI/静默凭据。
    /// 边界：`git -c credential.helper=…` 显式覆盖可绕过（T2 形状，§9 原文照录，不防）。
    /// </summary>
    public static int InstallNotice()
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
        if (existing.Contains($"\"{fwd} notice\"", StringComparison.Ordinal) ||
            existing.Contains($"{fwd} notice", StringComparison.Ordinal))
        {
            Console.WriteLine($"[Sluice] notice helper 已在位：{key} 含 {fwd} notice，无需重复安装。");
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
            Console.Error.WriteLine("[Sluice] git config 写入失败（git 不在 PATH？），安装中止（未产生任何变更）。");
            return ExitCodes.Internal;
        }

        Console.WriteLine("[Sluice] 已安装 notice credential helper（仅作用 github.com；其他 host 的既有 helper 不受影响）。");
        Console.WriteLine($"""
            验证：printf 'protocol=https\nhost=github.com\n\n' | git credential fill
            → stderr 应出现 [Sluice·终态] 指引，且输出无 password= 行。
            卸载：git config --global --unset-all {key}
            """);
        return 0;
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
}
