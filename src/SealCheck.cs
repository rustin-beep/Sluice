using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Sluice;

/// <summary>
/// §6 侧门自查（承重墙）。预检集（每次写调用 ≤50ms）+ seal --fix。
/// 违规 → 不弹批准框，直接 seal_violation 终态。
/// </summary>
internal static partial class SealCheck
{
    public const string SshMarker = "sluice-seal-block";

    // ---------- 暂停窗口（v2.22，§6 seal --pause；状态文件 %LOCALAPPDATA%\Sluice\state.json） ----------

    /// <summary>
    /// 封堵暂停状态。Active = state.json 的 pause_until 尚未到点。到期后不主动清文件——
    /// 自愈收敛在 seal（no-args 检测过期即执行 resume）与哨兵（过期即跑 seal --resume + toast），
    /// 写路径的 Run() 只把过期当"未暂停"（fail-closed：封堵先行恢复，重装 notice 随后补上）。
    /// v2.23（§14 L11）：state.json 是同用户可写明文——伪造未来时间戳可静音检测层（哨兵静默、
    /// seal 门放行；写操作本身仍需批准窗，token 不因此泄出）。处置=哨兵/doctor 交叉核对
    /// audit.jsonl 的 seal_pause 记录（HasAuditRecord），无对应记录按疑似伪造告警。
    /// </summary>
    internal static class SealPause
    {
        private static string StatePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sluice", "state.json");

        public static bool Active
        {
            get
            {
                var until = Until();
                return until is { } t && t > DateTime.UtcNow;
            }
        }

        /// <summary>解析出的截止时刻，不做过期过滤（null = 无文件/无键/不可解析；已过期照样返回，
        /// 过期自愈与 doctor 的"恢复中"显示都依赖这个值）。过期文件不在此清——见类注释。</summary>
        public static DateTime? Until()
        {
            try
            {
                if (!File.Exists(StatePath)) return null;
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(StatePath));
                return doc.RootElement.TryGetProperty("pause_until", out var v)
                    && DateTime.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var t)
                    ? t : null;
            }
            catch { return null; }
        }

        /// <summary>state.json 值、seal_pause 审计 result、哨兵 ps1 的 grep needle 三方共用的字符串形态
        ///（逐字一致是 HasAuditRecord 与哨兵交叉核对能对上的前提——形态入 selftest 锁）。</summary>
        public static string ToStateString(DateTime untilUtc) =>
            untilUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>v2.23（§14 L11）：该 pause_until 值是否有对应的 seal_pause 审计记录。
        /// 抬高伪造门槛（一行写 state.json 静音检测层 → 还需照形态伪造审计）而非建立信任——
        /// audit.jsonl 同为同用户可写（L1/L4），完整方案（vault 同源密钥签名）不做，入册。</summary>
        public static bool HasAuditRecord(DateTime untilUtc)
        {
            try
            {
                var audit = Path.Combine(Path.GetDirectoryName(StatePath)!, "audit.jsonl");
                if (!File.Exists(audit)) return false;
                return File.ReadAllText(audit).Contains($"pause_until {ToStateString(untilUtc)}", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>写入暂停状态并返回实际生效的截止时刻（v2.23：调用方批准窗拖延时，此前算的
        /// 预估值会早于实际——state/审计/✓ 输出一律以本返回值为准，弹窗正文才用预估值）。</summary>
        public static DateTime Begin(int minutes)
        {
            var until = DateTime.UtcNow.AddMinutes(minutes);
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, $$"""{"pause_until":"{{ToStateString(until)}}"}""");
            return until;
        }

        public static void Clear()
        {
            try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
        }
    }

    // ---------- 预检（§6：凭据管理器 / gh hosts.yml / .git-credentials / 环境变量 / SSH / gitconfig） ----------

    public static List<string> Run(string? repoHint = null)
    {
        if (SealPause.Active) return []; // 暂停窗口内：封堵解除（§6 seal --pause）；到期由 seal 自愈/哨兵恢复
        var v = new List<string>();

        foreach (var (target, _) in CredEnum())
            if (target.Contains("github", StringComparison.OrdinalIgnoreCase))
                v.Add($"凭据管理器：{target}");

        var hostsYml = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GitHub CLI", "hosts.yml");
        if (File.Exists(hostsYml))
        {
            var txt = File.ReadAllText(hostsYml);
            if (txt.Contains("oauth_token", StringComparison.Ordinal))
                v.Add("gh：hosts.yml 含 oauth_token（gh 已登录）");
        }

        var gitCred = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".git-credentials");
        if (File.Exists(gitCred))
            v.Add("git：~/.git-credentials 明文凭据文件存在");

        foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
                v.Add($"环境变量：{name} 已设置（agent 进程会继承它，一查一个准）");

        v.AddRange(SshViolations());

        v.AddRange(GitConfigViolations());

        v.AddRange(RepoLocalHelperViolations(repoHint));

        return v;
    }

    [GeneratedRegex(@"(ghp_[A-Za-z0-9]{8}|gho_[A-Za-z0-9]{8}|github_pat_[A-Za-z0-9_]{8})")]
    private static partial Regex TokenPattern();

    // 凭据管理器走 CredEnumerateW：cmdkey /list 输出是本地化文本（中文系统为"目标:"），
    // 文本解析在非英文系统必坏（实测 2026-10-29）；API 拿到的本就是 Unicode 目标名。
    private static List<(string Target, int Type)> CredEnum()
    {
        var result = new List<(string, int)>();
        if (!CredEnumerateW(null, 0, out var count, out var pCreds))
            return result;
        try
        {
            for (uint i = 0; i < count; i++)
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(Marshal.ReadIntPtr(pCreds, (int)(i * IntPtr.Size)));
                var target = Marshal.PtrToStringUni(cred.TargetName);
                if (target is not null)
                    result.Add((target, cred.Type));
            }
        }
        finally
        {
            CredFree(pCreds);
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredEnumerateW(string? filter, uint flags, out uint count, out IntPtr pCredentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string targetName, int type, int flags);

    private static string SshDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    private static List<string> SshViolations()
    {
        var v = new List<string>();
        var dir = SshDir;
        if (!Directory.Exists(dir)) return v;

        var hasKey = Directory.EnumerateFiles(dir, "id_*")
            .Any(f => !f.EndsWith(".pub", StringComparison.OrdinalIgnoreCase)
                      && !f.Contains(".pub.", StringComparison.Ordinal));
        if (!hasKey) return v;

        var cfg = Path.Combine(dir, "config");
        var blocked = File.Exists(cfg) &&
                      File.ReadAllText(cfg).Contains(SshMarker, StringComparison.Ordinal);
        if (!blocked)
            v.Add("SSH：~/.ssh 有私钥且 github.com 未屏蔽（git push git@github.com 可绕开 PAT）——seal --fix 将写入屏蔽块");
        return v;
    }

    private static List<string> GitConfigViolations()
    {
        var v = new List<string>();
        foreach (var cfg in GlobalGitConfigs())
        {
            if (!File.Exists(cfg)) continue;
            var txt = File.ReadAllText(cfg);
            foreach (var m in TokenPattern().Matches(txt).OfType<Match>())
                v.Add($"gitconfig：{cfg} 内嵌 token（{m.Value}…）——请人工移除");
            if (txt.Contains("insteadof", StringComparison.OrdinalIgnoreCase) &&
                txt.Contains("github", StringComparison.OrdinalIgnoreCase))
                v.Add($"gitconfig：{cfg} 含 url.insteadOf 重写且涉及 github——请人工确认");
        }
        return v;
    }

    private static IEnumerable<string> GlobalGitConfigs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, ".gitconfig");
        yield return Path.Combine(home, ".config", "git", "config");
    }

    // ---------- repo-local credential.helper 清查（spec-guard-20261112 层 A 深扫增项） ----------

    /// <summary>全局空串断继承断不掉 local 链：helper 列表按 system→global→local 顺序**拼接**，
    /// 空串只清它之前出现过的条目——repo-local helper 恰排在空串之后，GCM 可从仓库本地配置
    /// 回到链里。发现即报违规、人工处置（不知用户为何写它，不自动改）。哨兵上下文（CWD=
    /// System32）与非 git 目录自然跳过。v2.37 审阅 P2-2：扫描面随操作目标——push 传
    /// <paramref name="repoHint"/>（--repo-path 所指仓库，`sluice push --repo-path B` 在 A 目录
    /// 下不再漏检 B）；其余（seal/doctor/非 push 写动词）缺省 CWD。</summary>
    private static List<string> RepoLocalHelperViolations(string? repoHint = null)
    {
        var v = new List<string>();
        var cfg = FindRepoGitConfig(new DirectoryInfo(repoHint ?? Environment.CurrentDirectory));
        if (cfg is null) return v;
        try { v.AddRange(RepoLocalHelperViolationsIn(File.ReadAllLines(cfg), cfg)); }
        catch { /* 不可读 → 按无违规（fail 方向：清查是疑点面，不误拦） */ }
        return v;
    }

    /// <summary>gitconfig 行 → 违规（纯函数，selftest 锁）：无 URL 作用域的 [credential] helper
    /// 对一切 host 生效（含 github.com）→ 违规；[credential "URL"] 仅当 URL 指向 github.com →
    /// 违规；其他 URL 作用域（如公司 GitLab 专属 helper）不碰。<paramref name="cfgPath"/> 进违规
    /// 文本（人工处置要指到文件）。**空值跳过**（v2.37 审阅 P2-2）：`helper =`（空串）是断继承
    /// 惯用法——sluice 自己在全局用的同款，是防御不是门。</summary>
    internal static List<string> RepoLocalHelperViolationsIn(IEnumerable<string> lines, string cfgPath)
    {
        var v = new List<string>();
        // 当前 section：null = 非 credential；"" = 无 URL 作用域；"url" = 作用域 URL
        string? scope = null;
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.Length == 0 || l.StartsWith('#') || l.StartsWith(';')) continue;
            if (l.StartsWith('['))
            {
                var head = l.Trim('[', ']').Trim();
                if (head.Equals("credential", StringComparison.OrdinalIgnoreCase)) scope = "";
                else if (head.StartsWith("credential ", StringComparison.OrdinalIgnoreCase))
                    scope = head["credential ".Length..].Trim().Trim('"');
                else scope = null;
                continue;
            }
            var eq = l.IndexOf('=');
            if (eq < 0 || scope is null) continue;
            var k = l[..eq].Trim();
            if (!k.Equals("helper", StringComparison.OrdinalIgnoreCase)) continue;
            var val = l[(eq + 1)..].Trim().Trim('"');
            if (val.Length == 0) continue; // 空值 = 断继承（sluice 全局同款惯用法），非门
            if (scope.Length == 0 || scope.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            {
                var label = scope.Length == 0
                    ? "[credential]（无 URL 作用域，对一切 host 生效）"
                    : $"[credential \"{scope}\"]";
                v.Add($"repo-local：{cfgPath} 的 {label} helper = {val}" +
                    "——全局空串断不掉 local 链（拼接序在其后），GCM 可由此回到 github.com；人工处置面：编辑该文件移除/收窄后重跑 sluice seal 复查");
            }
        }
        return v;
    }

    /// <summary>向上找仓库 .git（与 git 的解析窗口一致），再定位其 config：常规仓库 .git/config；
    /// worktree（.git 是文件）读 gitdir 后再读 commondir 指回的主 .git/config。找不到 → null。</summary>
    private static string? FindRepoGitConfig(DirectoryInfo? dir)
    {
        while (dir is not null)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git))
            {
                var c = Path.Combine(git, "config");
                return File.Exists(c) ? c : null;
            }
            if (File.Exists(git))
            {
                // worktree/子模块：.git 文件内容 "gitdir: <路径>"
                string[] body;
                try { body = File.ReadAllLines(git); } catch { return null; }
                var gd = body.FirstOrDefault(x => x.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase))
                    ?["gitdir:".Length..].Trim();
                if (gd is null || gd.Length == 0) return null;
                if (!Path.IsPathRooted(gd)) gd = Path.GetFullPath(Path.Combine(dir.FullName, gd));
                // worktree：真正的 credential 配置在 commondir 指回的主 .git 里
                var common = gd;
                try
                {
                    var cdFile = Path.Combine(gd, "commondir");
                    if (File.Exists(cdFile))
                    {
                        var cd = File.ReadAllText(cdFile).Trim();
                        common = Path.GetFullPath(Path.Combine(gd, cd));
                    }
                }
                catch { /* commondir 不可读 → 就用 gitdir 本身 */ }
                var cfg2 = Path.Combine(common, "config");
                return File.Exists(cfg2) ? cfg2 : null;
            }
            dir = dir.Parent;
        }
        return null;
    }

    // ---------- seal --fix ----------

    public static List<string> Fix(List<string> violations)
    {
        var done = new List<string>();

        foreach (var (target, type) in CredEnum())
            if (target.Contains("github", StringComparison.OrdinalIgnoreCase))
            {
                var ok = CredDeleteW(target, type, 0);
                done.Add(ok ? $"凭据管理器：已删除 {target}" : $"凭据管理器：删除失败（{target}）——请手动删除");
            }

        var hostsYml = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GitHub CLI", "hosts.yml");
        if (File.Exists(hostsYml))
        {
            var bak = hostsYml + ".sluice-bak";
            File.Move(hostsYml, bak, overwrite: true);
            done.Add($"gh：hosts.yml 已改名 → {bak}（gh 已登出；如需恢复改名回来）");
        }

        var gitCred = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".git-credentials");
        if (File.Exists(gitCred))
        {
            var bak = gitCred + ".sluice-bak";
            File.Move(gitCred, bak, overwrite: true);
            done.Add($"git：~/.git-credentials 已改名 → {bak}");
        }

        if (violations.Any(x => x.StartsWith("SSH")))
            done.AddRange(WriteSshBlock());

        // v2.37 审阅 P2-2：repo-local helper 属人工处置面——fix 不自动改（不知用户为何写它），
        // 如实指路人工编辑 + 复查。此前无此分支 = "文案让跑 --fix、fix 修不了、复查永 7" 的指令环
        if (violations.Any(x => x.StartsWith("repo-local：", StringComparison.Ordinal)))
            done.Add("repo-local credential.helper：无法自动处理——按上方 ✗ 行给出的 .git/config 路径人工移除/收窄 helper 后，重跑 sluice seal 复查");

        done.Add("环境变量 GH_TOKEN/GITHUB_TOKEN：无法自动清除（随父进程继承）——请从 shell profile / IDE 配置移除");
        done.Add("VS Code GitHub session（如有）：请在 VS Code 内手动登出（仅检测，不自动处理）");

        return done;
    }

    private static List<string> WriteSshBlock()
    {
        var dir = SshDir;
        Directory.CreateDirectory(dir);
        var cfg = Path.Combine(dir, "config");
        if (File.Exists(cfg) && File.ReadAllText(cfg).Contains(SshMarker, StringComparison.Ordinal))
            return ["SSH：屏蔽块已在位，跳过"];
        File.AppendAllText(cfg, """

            # >>> sluice-seal-block (github.com over SSH blocked by Sluice) >>>
            Host github.com
                PubkeyAuthentication no
            # <<< sluice-seal-block <<<

            """);
        return ["SSH：已向 ~/.ssh/config 写入 github.com 屏蔽块（PubkeyAuthentication no）"];
    }

    /// <summary>摘除 SSH 屏蔽块（seal --pause / unseal）。只删标记块内的行，用户自有配置不动；
    /// resume 走 seal --fix 的违规检测重建（有私钥且无标记 → 重写块）。</summary>
    public static List<string> StripSshBlock()
    {
        var cfg = Path.Combine(SshDir, "config");
        if (!File.Exists(cfg)) return ["SSH：无 config，无需摘除"];
        var lines = File.ReadAllLines(cfg);
        if (!lines.Any(l => l.Contains(SshMarker, StringComparison.Ordinal)))
            return ["SSH：屏蔽块不在位，无需摘除"];
        var outLines = new List<string>();
        bool inBlock = false;
        foreach (var l in lines)
        {
            if (l.Contains($">>> {SshMarker}", StringComparison.Ordinal)) { inBlock = true; continue; }
            if (l.Contains($"<<< {SshMarker}", StringComparison.Ordinal)) { inBlock = false; continue; }
            if (!inBlock) outLines.Add(l);
        }
        // 去掉块前后的空行堆积（块自带首尾空行）
        while (outLines.Count > 0 && string.IsNullOrWhiteSpace(outLines[^1])) outLines.RemoveAt(outLines.Count - 1);
        File.WriteAllLines(cfg, outLines);
        return ["SSH：已摘除 github.com 屏蔽块（直推能力恢复）"];
    }
}
