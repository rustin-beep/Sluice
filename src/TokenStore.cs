using System.Security.Cryptography;

namespace Sluice;

/// <summary>
/// vault（§5，P7/P7c 定案）：master key = HKDF-SHA256(HelloSign(challenge), salt, info)，
/// AES-GCM 包封 PAT。挑战与盐存 token.bin；同挑战签名确定性（RSA PKCS1v15）→ 每次解锁可复现。
/// 解密触发密钥的 UI 门与批准是同一次生物事件（§5：一次 Hello = 解锁 + 授权）。
/// </summary>
internal static class TokenStore
{
    private const string Info = "sluice-vault-v1"; // 用途域分隔
    private static readonly byte[] Magic = "SLCV1"u8.ToArray();
    private static string VaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sluice", "token.bin");

    public static bool Exists => File.Exists(VaultPath);

    public sealed record VaultParams(byte[] Salt, byte[] Challenge);

    public static VaultParams NewParams()
    {
        var salt = RandomNumberGenerator.GetBytes(32);
        var challenge = RandomNumberGenerator.GetBytes(32);
        return new VaultParams(salt, challenge);
    }

    /// <summary>读回 token.bin 中存的挑战（弹批准窗前取，批准时签名用）。</summary>
    public static byte[] PeekChallenge()
    {
        var (p, _) = ReadFile();
        return p.Challenge;
    }

    public static void Save(VaultParams p, string pat, byte[] helloSignature)
    {
        var key = DeriveKey(helloSignature, p.Salt);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = System.Text.Encoding.UTF8.GetBytes(pat);
        var ct = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16))
            gcm.Encrypt(nonce, plain, ct, tag);

        Directory.CreateDirectory(Path.GetDirectoryName(VaultPath)!);
        using var fs = new FileStream(VaultPath, FileMode.Create, FileAccess.Write);
        fs.Write(Magic);
        fs.Write(p.Salt);
        fs.Write(p.Challenge);
        fs.Write(nonce);
        fs.Write(ct);
        fs.Write(tag);
    }

    public static string Load(byte[] helloSignature)
    {
        var (p, payload) = ReadFile();
        var key = DeriveKey(helloSignature, p.Salt);
        var ct = payload[..^16];
        var tag = payload[^16..];
        var plain = new byte[ct.Length];
        using var gcm = new AesGcm(key, 16);
        gcm.Decrypt(p.Nonce!, ct, tag, plain); // 失败 = 签名不符/文件损坏 → CryptographicException
        return System.Text.Encoding.UTF8.GetString(plain);
    }

    private sealed record FileParams(byte[] Salt, byte[] Challenge, byte[]? Nonce);

    private static (FileParams, byte[] Payload) ReadFile()
    {
        var b = File.ReadAllBytes(VaultPath);
        if (b.Length < 5 + 32 + 32 + 12 + 16 || !b.AsSpan(0, 5).SequenceEqual(Magic))
            throw new InvalidDataException("token.bin 格式不正确");
        var salt = b[5..37];
        var challenge = b[37..69];
        var nonce = b[69..81];
        var payload = b[81..];
        return (new FileParams(salt, challenge, nonce), payload);
    }

    private static byte[] DeriveKey(byte[] signature, byte[] salt) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, signature, 32, salt,
            System.Text.Encoding.UTF8.GetBytes(Info));
}
