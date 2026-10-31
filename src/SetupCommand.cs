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
        Console.WriteLine("最后一步（承重墙）：运行 sluice seal --fix 封堵环境里的直连凭据，");
        Console.WriteLine("之后 agent 的一切 GitHub 写操作都只能走 sluice。");
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
}
