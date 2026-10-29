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

    // ---------- 预检（§6：凭据管理器 / gh hosts.yml / .git-credentials / 环境变量 / SSH / gitconfig） ----------

    public static List<string> Run()
    {
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
}
