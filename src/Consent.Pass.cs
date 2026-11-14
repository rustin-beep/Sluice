#if !WINDOWS
namespace Sluice;

/// <summary>
/// IConsent 的 Linux 叶（§1.5 预留"polkit 风格口令/指纹"，v2.38 落地为口令）：
/// 签名 = PassKdf.Sign(挑战, 口令, vault盐)——同一挑战确定性，与 Hello 签名同位复用 SLCV1 全套。
/// 输错口令 ≠ 取消：本叶回环验证（TokenStore.Load 试解，PAT 即取即弃不出叶边界外传播），
/// 错口令返回 null，走与 Hello 取消相同的"回窗再决定"面。
///
/// 面（surface）纪律——口令只从两处收：
///   · 门控路径（写动词 / seal --pause / unseal）：只在 GateWindow 口令框收。MCP spawn 的子进程
///     stdin 被重定向，终端读口令 = agent 可喂口令（T1.5 击穿），禁止走终端。
///   · 控制台读路径（api --auth）：用户自己终端里的掩码提示——信任级同 Windows 侧
///     Hello-from-console（WinRT 凭据 UI 同样从控制台进程弹出）。
/// 残余风险（§14 L15）：X11 下同用户进程可 XTest 注入击键，键盘记录 + 注入组合可破——
/// Windows 有 Hello broker 隔断重放腿（L5 实测），Linux 无等价物；Wayland 下跨客户端注入不成立。
/// </summary>
internal sealed class PassConsent : IConsent
{
    /// <summary>控制台面（api --auth）：最多 3 次掩码输入（口令错可重试，同 Hello 自身 PIN 重试位），
    /// ESC/空输入 = 取消。stdin 被重定向（agent 上下文）时读到什么都是错口令——fail-closed。</summary>
    public Task<byte[]?> SignAsync(byte[] challenge)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Console.WriteLine($"vault 口令（第 {attempt}/3 次，输入显示为 *，ESC 取消）：");
            var pass = SetupCommand.ReadSecret();
            if (pass.Length == 0) break; // 用户取消
            var sig = SignVerified(challenge, pass);
            if (sig is not null) return Task.FromResult<byte[]?>(sig);
            Console.WriteLine("口令不正确。");
        }
        return Task.FromResult<byte[]?>(null);
    }

    /// <summary>窗口面：GateWindow 口令框调用（口令已在手，只做签名+回环验证）。
    /// 错口令 = null；vault 文件损坏（InvalidDataException 等）如实上抛 = 内部错误面。</summary>
    public static byte[]? SignVerified(byte[] challenge, string passphrase)
    {
        var sig = PassKdf.Sign(challenge, passphrase, TokenStore.PeekSalt());
        try { _ = TokenStore.Load(sig); return sig; }
        catch (System.Security.Cryptography.CryptographicException) { return null; } // 口令不符（AES-GCM tag 校验失败）
    }

    /// <summary>setup 用：两次掩码输入设口令（≥8 字符且两次一致），对挑战签名。
    /// 取消/不符 = null（setup 中止）。盐来自 NewParams（vault 尚未落盘，不能 PeekSalt）。</summary>
    public static Task<byte[]?> SignForSetupAsync(byte[] challenge, byte[] salt)
    {
        Console.WriteLine("设置 vault 口令——此后每次批准需在批准窗内输入此口令（批准即解密，等价 Windows Hello 的生物事件）：");
        var p1 = SetupCommand.ReadSecret();
        if (p1.Length == 0) { Console.WriteLine("未输入口令，setup 中止。"); return Task.FromResult<byte[]?>(null); }
        if (p1.Length < 8) { Console.WriteLine("口令至少 8 字符，setup 中止。"); return Task.FromResult<byte[]?>(null); }
        Console.WriteLine("再输入一次确认：");
        var p2 = SetupCommand.ReadSecret();
        if (p2.Length == 0 || p1 != p2) { Console.WriteLine("两次输入不一致，setup 中止。"); return Task.FromResult<byte[]?>(null); }
        return Task.FromResult<byte[]?>(PassKdf.Sign(challenge, p1, salt));
    }
}
#endif
