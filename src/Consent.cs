#if WINDOWS
using Windows.Security.Cryptography;

namespace Sluice;

/// <summary>
/// IConsent 的 Windows 叶（§1.5）：Hello KeyCredential 签名。
/// P7 实测：NCRYPT_SILENT_FLAG 下签名返回 NTE_SILENT_CONTEXT——静默签名不存在，
/// RequestSignAsync 的 UI 门即批准事件；签名同时是 vault 解密因子（§5）。
/// v2.38 起整文件圈在 WINDOWS 符号内（WinRT 投影随 -windows TFM）；接口与异常在 Consent.Shared.cs。
/// </summary>
internal sealed class HelloConsent : IConsent
{
    private const string KeyName = "SluiceVault";

    public async Task<byte[]?> SignAsync(byte[] challenge)
    {
        var open = await Windows.Security.Credentials.KeyCredentialManager.OpenAsync(KeyName);
        if (open.Status != Windows.Security.Credentials.KeyCredentialStatus.Success)
            throw new VaultKeyMissingException(
                $"Hello 密钥 {KeyName} 不可用（{open.Status}）——vault 若在而密钥缺失，"
                + "多为系统重置 Windows Hello 数据所致，需重跑 setup 重建配对");
        var buf = CryptographicBuffer.CreateFromByteArray(challenge);
        var r = await open.Credential.RequestSignAsync(buf);
        if (r.Status != Windows.Security.Credentials.KeyCredentialStatus.Success)
            return null; // 用户取消 / 连续失败 → consent_failed
        CryptographicBuffer.CopyToByteArray(r.Result, out var sig);
        return sig;
    }

    /// <summary>setup 用：确保键存在（必要时创建），再签 vault 挑战。两步都可能弹 Hello。</summary>
    public static async Task<byte[]?> SignForSetupAsync(byte[] challenge)
    {
        var open = await Windows.Security.Credentials.KeyCredentialManager.OpenAsync(KeyName);
        Windows.Security.Credentials.KeyCredential cred;
        if (open.Status == Windows.Security.Credentials.KeyCredentialStatus.Success)
        {
            cred = open.Credential;
        }
        else
        {
            var create = await Windows.Security.Credentials.KeyCredentialManager.RequestCreateAsync(
                KeyName, Windows.Security.Credentials.KeyCredentialCreationOption.ReplaceExisting);
            if (create.Status != Windows.Security.Credentials.KeyCredentialStatus.Success)
                return null;
            cred = create.Credential;
        }
        var buf = CryptographicBuffer.CreateFromByteArray(challenge);
        var r = await cred.RequestSignAsync(buf);
        if (r.Status != Windows.Security.Credentials.KeyCredentialStatus.Success)
            return null;
        CryptographicBuffer.CopyToByteArray(r.Result, out var sig);
        return sig;
    }
}
#endif
