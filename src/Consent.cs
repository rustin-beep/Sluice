using Windows.Security.Cryptography;

namespace Sluice;

/// <summary>
/// IConsent 的 Windows 叶（§1.5）：Hello KeyCredential 签名。
/// P7 实测：NCRYPT_SILENT_FLAG 下签名返回 NTE_SILENT_CONTEXT——静默签名不存在，
/// RequestSignAsync 的 UI 门即批准事件；签名同时是 vault 解密因子（§5）。
/// </summary>
internal interface IConsent
{
    Task<byte[]?> SignAsync(byte[] challenge);
}

internal sealed class HelloConsent : IConsent
{
    private const string KeyName = "SluiceVault";

    public async Task<byte[]?> SignAsync(byte[] challenge)
    {
        var open = await Windows.Security.Credentials.KeyCredentialManager.OpenAsync(KeyName);
        if (open.Status != Windows.Security.Credentials.KeyCredentialStatus.Success)
            return null; // vault 建过则键必在；不在 = 状态损坏，按未通过处理
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
