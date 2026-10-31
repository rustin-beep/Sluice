using System.Runtime.InteropServices;
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

    /// <summary>读回 token.bin 中存的挑战（弹批准窗前取，批准时签名用）。
    /// SLCV2 无 Hello 无挑战——返回空数组（GateWindow 静默批准路径不消费挑战）。
    /// v2.7 实弹修复：92e902e 只改了批准后路径，漏了这里——迁移后写动词在 Gate.Request
    /// 构造期撞 ReadFile 的 V1 magic 校验，InvalidDataException 在弹窗前即崩（首个迁移实弹暴露）。</summary>
    public static byte[] PeekChallenge()
    {
        if (IsDpapiVault) return [];
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

    // ---------- D1（2026-10-31）：hello:false 静默 vault（SLCV2 = DPAPI 包封） ----------
    // owner 显式降级（config.json "hello": false）换免指纹体验：批准点击即批准事件，
    // vault 同用户进程可静默解密——防线 = 批准窗 + InputFilter 注入拦截 + seal。
    // Entropy 非密钥（二进制可见），仅让"随手 Unprotect"多一步。改回 true + 重跑 setup 可恢复 V1。

    private static readonly byte[] MagicV2 = "SLCV2"u8.ToArray();
    private static readonly byte[] DpapiEntropy = "sluice-dpapi-vault-v2"u8.ToArray();

    /// <summary>当前 vault 是否为静默（DPAPI）格式——决定批准后是否还需 Hello。</summary>
    public static bool IsDpapiVault
    {
        get
        {
            try
            {
                var b = File.ReadAllBytes(VaultPath);
                return b.Length > 5 && b.AsSpan(0, 5).SequenceEqual(MagicV2);
            }
            catch { return false; }
        }
    }

    public static void SaveDpapi(string pat)
    {
        var blob = DpapiProtect(System.Text.Encoding.UTF8.GetBytes(pat), DpapiEntropy);
        Directory.CreateDirectory(Path.GetDirectoryName(VaultPath)!);
        File.WriteAllBytes(VaultPath, [.. MagicV2, .. blob]);
    }

    private static string LoadDpapi()
    {
        var b = File.ReadAllBytes(VaultPath);
        if (b.Length <= 5 || !b.AsSpan(0, 5).SequenceEqual(MagicV2))
            throw new InvalidDataException("token.bin 不是静默格式（SLCV2）");
        var plain = DpapiUnprotect(b[5..], DpapiEntropy);
        return System.Text.Encoding.UTF8.GetString(plain);
    }

    // DPAPI（crypt32 直调，CurrentUser 作用域）：不引 System.Security.Cryptography.ProtectedData
    // 包——零依赖纪律（同 MessageBeep 先例），NativeAOT 友好。
    private const uint LMEM_FIXED = 0;
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1; // 静默：绝不弹 DPAPI 自己的 UI

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalAlloc(uint uFlags, nuint cb);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] DpapiProtect(byte[] plain, byte[] entropy)
    {
        var pin = ToBlob(plain);
        var pent = ToBlob(entropy);
        try
        {
            var @out = new DATA_BLOB();
            if (!CryptProtectData(ref pin, null, ref pent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref @out))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            return FromBlob(@out, free: true);
        }
        finally { FreeBlob(pin); FreeBlob(pent); }
    }

    private static byte[] DpapiUnprotect(byte[] blob, byte[] entropy)
    {
        var pin = ToBlob(blob);
        var pent = ToBlob(entropy);
        try
        {
            var @out = new DATA_BLOB();
            if (!CryptUnprotectData(ref pin, IntPtr.Zero, ref pent, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref @out))
                throw new CryptographicException(Marshal.GetLastWin32Error());
            return FromBlob(@out, free: true);
        }
        finally { FreeBlob(pin); FreeBlob(pent); }
    }

    private static DATA_BLOB ToBlob(byte[] data)
    {
        var p = LocalAlloc(LMEM_FIXED, (nuint)data.Length);
        if (p == IntPtr.Zero) throw new OutOfMemoryException("LocalAlloc");
        Marshal.Copy(data, 0, p, data.Length);
        return new DATA_BLOB { cbData = data.Length, pbData = p };
    }

    private static byte[] FromBlob(DATA_BLOB blob, bool free)
    {
        var r = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, r, 0, blob.cbData);
        if (free) LocalFree(blob.pbData);
        return r;
    }

    private static void FreeBlob(DATA_BLOB blob) => LocalFree(blob.pbData);

    /// <summary>批准/解锁后的统一取 token：静默 vault 直接解；Hello vault 用签名解，
    /// 且当 config 已关闭 Hello 时顺手迁移为静默格式（等于最后一次生物事件），之后全程免指纹。
    /// 迁移失败不阻断本次写（token 已解密且已批准）——下次再试即可。</summary>
    public static string LoadApproved(byte[]? signature)
    {
        if (IsDpapiVault) return LoadDpapi();
        var token = Load(signature!);
        if (!Gate.HelloEnabled)
        {
            try
            {
                MigrateToDpapi(token);
                Console.Error.WriteLine("[Sluice] vault 已迁移为静默格式（config hello:false）——后续批准不再需要 Windows Hello。");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Sluice] vault 迁移静默格式失败（{ex.Message}）——本次继续执行，下次批准仍会弹 Hello 重试迁移。");
            }
        }
        return token;
    }

    /// <summary>迁移 = 覆盖唯一凭据文件，必须先验证再替换（v2.7 加固，92e902e 实弹教训）：
    /// 写临时文件 → 回读解密比对（布局/熵/DPAPI 全链路实证，不靠推断）→ 原子替换。
    /// 任何一步失败保留 V1 原文件——下次批准重试迁移，vault 不因迁移而丢。</summary>
    private static void MigrateToDpapi(string token)
    {
        var tmp = VaultPath + ".migrate";
        try
        {
            var blob = DpapiProtect(System.Text.Encoding.UTF8.GetBytes(token), DpapiEntropy);
            File.WriteAllBytes(tmp, [.. MagicV2, .. blob]);
            var b = File.ReadAllBytes(tmp);
            if (b.Length <= 5 || !b.AsSpan(0, 5).SequenceEqual(MagicV2))
                throw new InvalidDataException("迁移文件布局校验失败");
            var roundTrip = System.Text.Encoding.UTF8.GetString(DpapiUnprotect(b[5..], DpapiEntropy));
            if (roundTrip != token)
                throw new InvalidDataException("迁移回读比对失败");
            File.Move(tmp, VaultPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理尽力而为 */ }
        }
    }
}
