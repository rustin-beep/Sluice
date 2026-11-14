using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Sluice;

/// <summary>
/// vault（§5，P7/P7c 定案）：master key = HKDF-SHA256(同意因子签名(challenge), salt, info)，
/// AES-GCM 包封 PAT。挑战与盐存 token.bin；同挑战签名确定性（Windows Hello = RSA PKCS1v15；
/// Linux 口令 = HMAC-SHA256(PBKDF2)）→ 每次解锁可复现。
/// 解密触发密钥的 UI 门与批准是同一次用户事件（§5：一次同意 = 解锁 + 授权）。
/// v2.38：签名来源按平台走叶（Consent.Shared/Consent.cs/Consent.Pass.cs），本文件 SLCV1
/// 布局与加解密零平台分支；静默降级档 = Windows SLCV2（DPAPI）/ Linux SLCF1（0600 文件）。
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
    /// 静默 vault 无同意因子无挑战——返回空数组（GateWindow 静默批准路径不消费挑战）。
    /// v2.7 实弹修复：92e902e 只改了批准后路径，漏了这里——迁移后写动词在 Gate.Request
    /// 构造期撞 ReadFile 的 V1 magic 校验，InvalidDataException 在弹窗前即崩（首个迁移实弹暴露）。</summary>
    public static byte[] PeekChallenge()
    {
        if (IsSilentVault) return [];
        var (p, _) = ReadFile();
        return p.Challenge;
    }

    /// <summary>读回 token.bin 中存的盐（Linux 口令叶的 PBKDF2 因子；仅 SLCV1 布局有盐）。</summary>
    internal static byte[] PeekSalt()
    {
        var (p, _) = ReadFile();
        return p.Salt;
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

    // ---------- D1（2026-10-31）：hello:false 静默 vault——降级档按平台分岔 ----------
    // owner 显式降级（config.json "hello": false）换免口令/指纹体验：批准点击即批准事件，
    // vault 同用户进程可静默读取——防线 = 批准窗 + seal（Windows 另有 InputFilter；Linux 无，L15 入册）。
    // 改回 true + 重跑 setup 可恢复强档格式。

    private static readonly byte[] MagicV2 = "SLCV2"u8.ToArray();

    /// <summary>进程启动时的 vault 形态快照（字段初始化在类型首次触及时运行——每个写动词都在
    /// 门控前触 TokenStore.Exists，必然早于任何批准/迁移）。**不能在落笔时读磁盘**：运行中
    /// V1→静默迁移发生在批准之后，迁移当次的批准事件仍是同意因子——audit 保真（2026-10-31 评审 B）。</summary>
    private static readonly bool StartedAsSilent = File.Exists(VaultPath) && IsSilentVault;

    /// <summary>本次批准事件的通道：静默 vault=桌面点击，V1 vault=Windows "hello" / Linux "pass"。
    /// §14 L1 让步（hello:false）入册后，"这次批准是到场因子还是点击"是审计唯一可事后
    /// 区分的维度——consent 字段按此记 click/hello/pass。</summary>
    public static string ConsentChannel => StartedAsSilent ? "click"
#if WINDOWS
        : "hello";
#else
        : "pass";
#endif

    /// <summary>当前 vault 是否为静默格式——决定批准后是否还需同意因子。
    /// Windows 认 SLCV2；Linux 认 SLCF1（Windows 的 SLCV2 文件在 Linux 上无法解，
    /// 不认 = 走 V1 路径撞 magic 校验，如实报"格式不正确"——vault 本就不跨机搬运）。</summary>
    public static bool IsSilentVault
    {
        get
        {
            try
            {
                var b = File.ReadAllBytes(VaultPath);
                if (b.Length <= 5) return false;
                var m = b.AsSpan(0, 5);
#if WINDOWS
                return m.SequenceEqual(MagicV2);
#else
                return m.SequenceEqual(MagicF1);
#endif
            }
            catch { return false; }
        }
    }

#if WINDOWS
    private static readonly byte[] DpapiEntropy = "sluice-dpapi-vault-v2"u8.ToArray();

    /// <summary>SLCV2 唯一写者（setup 直存与 V1→V2 迁移共用）：覆盖唯一凭据文件必须先验证再替换
    /// （v2.7 迁移路径实弹教训，v2.8 扩展到 setup 路径——写一半崩溃即损唯一凭据）：
    /// 写临时文件 → 回读解密比对（布局/熵/DPAPI 全链路实证，不靠推断）→ 原子替换。
    /// 任何一步失败保留原文件——迁移失败下次批准重试，vault 不因迁移而丢。</summary>
    public static void SaveDpapi(string pat)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VaultPath)!);
        var tmp = VaultPath + ".new";
        try
        {
            var blob = DpapiProtect(System.Text.Encoding.UTF8.GetBytes(pat), DpapiEntropy);
            File.WriteAllBytes(tmp, [.. MagicV2, .. blob]);
            var b = File.ReadAllBytes(tmp);
            if (b.Length <= 5 || !b.AsSpan(0, 5).SequenceEqual(MagicV2))
                throw new InvalidDataException("vault 布局校验失败");
            if (System.Text.Encoding.UTF8.GetString(DpapiUnprotect(b[5..], DpapiEntropy)) != pat)
                throw new InvalidDataException("vault 回读比对失败");
            File.Move(tmp, VaultPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理尽力而为 */ }
        }
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
#else
    // ---------- Linux 降级档：SLCF1 = 0600 明文文件（§1.5 ITokenStore 预案） ----------
    // 同 DPAPI/SLCV2 的 L1 让步级：同用户进程可读。agent 直读文件即得 PAT——与 Windows
    // DPAPI 静默可解同缺口（owner 显式降级换免口令；强档口令 vault 是默认）。
    private static readonly byte[] MagicF1 = "SLCF1"u8.ToArray();

    /// <summary>SLCF1 唯一写者：同 SaveDpapi 的 verify-then-move 纪律（写临时 → 回读比对 →
    /// 原子替换）；权限 0600 先落在临时文件上——rename 保 inode 保权限，任何窗口期都不是 0644。</summary>
    public static void Save0600(string pat)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VaultPath)!);
        var tmp = VaultPath + ".new";
        try
        {
            File.WriteAllBytes(tmp, [.. MagicF1, .. System.Text.Encoding.UTF8.GetBytes(pat)]);
#pragma warning disable CA1416 // 本块整体在 #if !WINDOWS 内；net10.0 TFM 声明全平台可达引发误报
            File.SetUnixFileMode(tmp, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
#pragma warning restore CA1416
            var b = File.ReadAllBytes(tmp);
            if (b.Length <= 5 || !b.AsSpan(0, 5).SequenceEqual(MagicF1))
                throw new InvalidDataException("vault 布局校验失败");
            if (System.Text.Encoding.UTF8.GetString(b[5..]) != pat)
                throw new InvalidDataException("vault 回读比对失败");
            File.Move(tmp, VaultPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 清理尽力而为 */ }
        }
    }

    private static string Load0600()
    {
        var b = File.ReadAllBytes(VaultPath);
        if (b.Length <= 5 || !b.AsSpan(0, 5).SequenceEqual(MagicF1))
            throw new InvalidDataException("token.bin 不是静默格式（SLCF1）");
        return System.Text.Encoding.UTF8.GetString(b[5..]);
    }
#endif

    /// <summary>批准/解锁后的统一取 token：静默 vault 直接取；强档 vault 用签名解，
    /// 且当 config 已关同意因子时顺手迁移为静默格式（等于最后一次到场事件），之后全程免口令/指纹。
    /// 迁移失败不阻断本次写（token 已解密且已批准）——下次再试即可。</summary>
    public static string LoadApproved(byte[]? signature)
    {
        if (IsSilentVault)
#if WINDOWS
            return LoadDpapi();
#else
            return Load0600();
#endif
        var token = Load(signature!);
        if (!Gate.HelloEnabled)
        {
            try
            {
#if WINDOWS
                SaveDpapi(token);
                Console.Error.WriteLine("[Sluice] vault 已迁移为静默格式（config hello:false）——后续批准不再需要 Windows Hello。");
#else
                Save0600(token);
                Console.Error.WriteLine("[Sluice] vault 已迁移为静默格式（config hello:false）——后续批准不再需要口令。");
#endif
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Sluice] vault 迁移静默格式失败（{ex.Message}）——本次继续执行，下次批准仍会要求同意因子并重试迁移。");
            }
        }
        return token;
    }
}
