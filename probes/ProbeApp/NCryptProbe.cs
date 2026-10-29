using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ProbeApp;

internal static class NCryptProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BCRYPT_PKCS1_PADDING_INFO
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string pszAlgId;
        public BCRYPT_PKCS1_PADDING_INFO(string alg) { pszAlgId = alg; }
    }

    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptOpenStorageProvider(out IntPtr phProvider, string pszProviderName, int dwFlags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptOpenKey(IntPtr hProvider, out IntPtr phKey, string pszKeyName, int dwLegacySpec, int dwFlags);
    [DllImport("ncrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int NCryptEnumKeys(IntPtr hProvider, string? pszScope, out IntPtr ppKeyName, ref IntPtr ppEnumState, int dwFlags);
    [StructLayout(LayoutKind.Sequential)]
    private struct NCryptKeyName { public IntPtr pszName; public IntPtr pszAlgName; public int dwLegacySpec; public int dwFlags; }
    [DllImport("ncrypt.dll")]
    private static extern int NCryptSignHash(IntPtr hKey, ref BCRYPT_PKCS1_PADDING_INFO pPaddingInfo, byte[] pbHashValue, int cbHashValue, byte[] pbSignature, int cbSignature, out int pcbResult, int dwFlags);
    [DllImport("ncrypt.dll")]
    private static extern int NCryptSignHash(IntPtr hKey, IntPtr pPaddingInfo, byte[] pbHashValue, int cbHashValue, byte[] pbSignature, int cbSignature, out int pcbResult, int dwFlags);

    private const int NCRYPT_SILENT_FLAG = 0x40;

    public static async Task RunSignDetAsync()
    {
        const string credName = "SluiceVaultProbe";
        var open = await Windows.Security.Credentials.KeyCredentialManager.OpenAsync(credName);
        if (open.Status != Windows.Security.Credentials.KeyCredentialStatus.Success)
        {
            Program.Log($"[p7c] open FAILED: {open.Status}");
            return;
        }
        var cred = open.Credential;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("sluice-p7-challenge"));
        var buf = Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(hash);
        Program.Log("[p7c] RequestSignAsync #1 - Hello prompt, please touch");
        var r1 = await cred.RequestSignAsync(buf);
        Program.Log($"[p7c] #1 status={r1.Status}");
        if (r1.Status != Windows.Security.Credentials.KeyCredentialStatus.Success) return;
        Program.Log("[p7c] RequestSignAsync #2 - Hello prompt again, please touch");
        var r2 = await cred.RequestSignAsync(buf);
        Program.Log($"[p7c] #2 status={r2.Status}");
        if (r2.Status != Windows.Security.Credentials.KeyCredentialStatus.Success) return;
        Windows.Security.Cryptography.CryptographicBuffer.CopyToByteArray(r1.Result, out var b1);
        Windows.Security.Cryptography.CryptographicBuffer.CopyToByteArray(r2.Result, out var b2);
        Program.Log($"[p7c] sigLen={b1.Length} deterministic={b1.SequenceEqual(b2)}");
        Program.Log("[p7c] reading: deterministic=true -> vault key can be HKDF(HelloSign(challenge)), per-use UI gate for free");
    }

    public static async Task RunAsync()
    {
        const string credName = "SluiceVaultProbe";
        Windows.Security.Credentials.KeyCredential cred;
        var open = await Windows.Security.Credentials.KeyCredentialManager.OpenAsync(credName);
        if (open.Status == Windows.Security.Credentials.KeyCredentialStatus.Success)
        {
            cred = open.Credential;
            Program.Log("[p7] existing credential opened");
        }
        else
        {
            Program.Log("[p7] creating KeyCredential - system Hello prompt will appear");
            var create = await Windows.Security.Credentials.KeyCredentialManager.RequestCreateAsync(
                credName, Windows.Security.Credentials.KeyCredentialCreationOption.ReplaceExisting);
            if (create.Status != Windows.Security.Credentials.KeyCredentialStatus.Success)
            {
                Program.Log($"[p7] create FAILED: {create.Status}");
                return;
            }
            cred = create.Credential;
            Program.Log($"[p7] created name='{cred.Name}'");
        }

        var st = NCryptOpenStorageProvider(out var prov, "Microsoft Passport Key Storage Provider", 0);
        if (st != 0) { Program.Log($"[p7] OpenStorageProvider failed 0x{st:X8}"); return; }
        Program.Log("[p7] Passport KSP opened, enumerating keys...");
        var enumState = IntPtr.Zero;
        string? match = null;
        for (int i = 0; i < 50; i++)
        {
            st = NCryptEnumKeys(prov, null, out var namePtr, ref enumState, 0);
            if (st != 0) break;
            var kn = System.Runtime.InteropServices.Marshal.PtrToStructure<NCryptKeyName>(namePtr);
            var nm = System.Runtime.InteropServices.Marshal.PtrToStringUni(kn.pszName) ?? "";
            var alg = System.Runtime.InteropServices.Marshal.PtrToStringUni(kn.pszAlgName) ?? "";
            Program.Log($"[p7]   key[{i}] name='{nm}' alg='{alg}'");
            if (nm.Contains(credName, StringComparison.OrdinalIgnoreCase)) match = nm;
        }
        if (match == null) { Program.Log("[p7] no key matching credential name found"); return; }
        st = NCryptOpenKey(prov, out var key, match, 0, 0);
        if (st != 0) { Program.Log($"[p7] OpenKey('{match}') failed 0x{st:X8}"); return; }
        Program.Log("[p7] NCrypt key opened in Passport KSP (from this second process, no UI so far)");

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("sluice-p7-challenge"));
        var info = new BCRYPT_PKCS1_PADDING_INFO("SHA256");

        st = NCryptSignHash(key, ref info, hash, hash.Length, null, 0, out var len, NCRYPT_SILENT_FLAG);
        Program.Log($"[p7] SILENT(RSA-PKCS1) size-query status=0x{st:X8} len={len}");
        if (st == 0)
        {
            var sig = new byte[len];
            st = NCryptSignHash(key, ref info, hash, hash.Length, sig, sig.Length, out _, NCRYPT_SILENT_FLAG);
            Program.Log($"[p7] SILENT(RSA-PKCS1) sign status=0x{st:X8}");
            if (st == 0)
            {
                var sig2 = new byte[len];
                _ = NCryptSignHash(key, ref info, hash, hash.Length, sig2, sig2.Length, out _, NCRYPT_SILENT_FLAG);
                Program.Log($"[p7] deterministic-signature(PKCS1v15) = {sig.SequenceEqual(sig2)}");
            }
        }

        st = NCryptSignHash(key, IntPtr.Zero, hash, hash.Length, null, 0, out len, NCRYPT_SILENT_FLAG);
        Program.Log($"[p7] SILENT(ECDSA) size-query status=0x{st:X8}");

        st = NCryptSignHash(key, ref info, hash, hash.Length, null, 0, out len, 0);
        Program.Log($"[p7] NO-FLAG(RSA-PKCS1) size-query status=0x{st:X8} (if a Hello prompt appeared, touch/cancel it)");
        Program.Log("[p7] done. reading: SILENT ok = silent same-user key use possible (Passport KSP cannot gate); SILENT fail + NO-FLAG ok = UI-gated per use");
    }
}
