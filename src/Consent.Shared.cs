using System.Security.Cryptography;

namespace Sluice;

/// <summary>
/// IConsent（§1.5 平台叶）：批准事件的用户到场因子，签名同时是 vault 解密因子（§5 单次事件 = 解锁+授权）。
/// Windows 叶 = HelloConsent（WinRT，Consent.cs，WINDOWS 符号内）；Linux 叶 = PassConsent（口令，Consent.Pass.cs）。
/// </summary>
internal interface IConsent
{
    Task<byte[]?> SignAsync(byte[] challenge);
}

/// <summary>
/// vault 文件在而同意因子缺失/不可用（Windows：系统重置 Hello 数据、更换账户等）。
/// 与"用户取消"（null 返回）分开归因：这不是用户意愿，重试无用，需重跑 setup。
/// Linux 叶不会抛（口令无 OS 键概念；口令错 = null 走取消面）。
/// </summary>
public sealed class VaultKeyMissingException(string msg) : Exception(msg);

/// <summary>
/// 平台叶工厂：按编译目标给出 IConsent 实现。供控制台读路径（api --auth）用；
/// 门控路径的口令在 GateWindow 内收（见 PassConsent 类注释的面纪律），不经此工厂。
/// </summary>
internal static class ConsentLeaf
{
    public static IConsent Create() =>
#if WINDOWS
        new HelloConsent();
#else
        new PassConsent();
#endif
}

/// <summary>
/// Linux 口令 vault 的签名链（纯函数，两 TFM 皆编——selftest 可在 Windows 面锁确定性向量）：
/// 签名 = HMAC-SHA256(PBKDF2-SHA256(口令, vault盐, 600k), 挑战)。
/// 与 Hello 签名同位（HKDF 的 IKM，长度任意、确定性），SLCV1 布局/加解密零改动复用。
/// 迭代数 600k = OWASP 2023 对 PBKDF2-SHA256 的建议值；每次批准一次（~0.3s），与 Hello ~1s 同量级。
/// 常量写死：升级迭代数 = 换 KDF = 需重跑 setup 重建 vault，v1 不做协商字段。
/// </summary>
internal static class PassKdf
{
    internal const int Pbkdf2Iterations = 600_000;

    public static byte[] Sign(byte[] challenge, string passphrase, byte[] salt)
    {
        var k = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        using var h = new HMACSHA256(k);
        return h.ComputeHash(challenge);
    }
}
