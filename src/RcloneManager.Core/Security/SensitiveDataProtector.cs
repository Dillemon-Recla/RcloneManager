using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RcloneManager.Core.Security;

[SupportedOSPlatform("windows")]
public sealed class AesGcmDataProtector : ISensitiveDataProtector
{
    public const string EncryptedPrefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static AesGcmDataProtector Shared { get; } = new(WindowsCredentialMasterKeyStore.Shared);

    private readonly IMasterKeyStore _keyStore;

    public AesGcmDataProtector(IMasterKeyStore keyStore)
    {
        _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
    }

    public bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(EncryptedPrefix, StringComparison.Ordinal);

    public string EnsureKeyInitialized(out bool keyCreated, string? targetName = null)
    {
        var resolvedTarget = ResolveTarget(targetName);
        var key = _keyStore.GetOrCreateKey(out keyCreated, resolvedTarget);
        CryptographicOperations.ZeroMemory(key);
        return resolvedTarget;
    }

    public string RotateDisposableKey(string? targetName = null)
    {
        var resolvedTarget = ResolveTarget(targetName);
        var key = _keyStore.RotateKey(resolvedTarget);
        CryptographicOperations.ZeroMemory(key);
        return resolvedTarget;
    }

    public bool DiscardKey(string? targetName = null) =>
        _keyStore.DeleteKey(ResolveTarget(targetName));

    public string Protect(string plainText, string? targetName = null)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        if (IsProtected(plainText))
            return plainText;

        var key = _keyStore.GetOrCreateKey(out _, ResolveTarget(targetName));
        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(plainText);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipherBytes = new byte[plainBytes.Length];
            var tag = new byte[TagSize];

            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

            var combined = new byte[NonceSize + TagSize + cipherBytes.Length];
            Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
            Buffer.BlockCopy(tag, 0, combined, NonceSize, TagSize);
            Buffer.BlockCopy(cipherBytes, 0, combined, NonceSize + TagSize, cipherBytes.Length);

            return EncryptedPrefix + Convert.ToBase64String(combined);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public string Unprotect(string value, string? targetName = null)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (!IsProtected(value))
            return value;

        var payloadBase64 = value[EncryptedPrefix.Length..];
        var combined = Convert.FromBase64String(payloadBase64);
        if (combined.Length < NonceSize + TagSize)
            throw new CryptographicException("Payload criptografado inválido ou corrompido.");

        var key = _keyStore.GetOrCreateKey(out _, ResolveTarget(targetName));
        try
        {
            var nonce = combined.AsSpan(0, NonceSize);
            var tag = combined.AsSpan(NonceSize, TagSize);
            var cipherBytes = combined.AsSpan(NonceSize + TagSize);
            var plainBytes = new byte[cipherBytes.Length];

            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

            return Encoding.UTF8.GetString(plainBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private string ResolveTarget(string? targetName) =>
        string.IsNullOrWhiteSpace(targetName) ? _keyStore.DefaultTargetName : targetName;
}

[SupportedOSPlatform("windows")]
public static class SensitiveDataProtector
{
    public const string EncryptedPrefix = AesGcmDataProtector.EncryptedPrefix;

    public static string EnsureInitialized(out bool keyCreated, string targetName = WindowsCredentialVault.DefaultTargetName) =>
        AesGcmDataProtector.Shared.EnsureKeyInitialized(out keyCreated, targetName);

    public static string RotateMasterKey(string targetName = WindowsCredentialVault.DefaultTargetName) =>
        AesGcmDataProtector.Shared.RotateDisposableKey(targetName);

    public static bool IsEncrypted(string? value) =>
        AesGcmDataProtector.Shared.IsProtected(value);

    public static string EncryptString(string plainText, string targetName = WindowsCredentialVault.DefaultTargetName) =>
        AesGcmDataProtector.Shared.Protect(plainText, targetName);

    public static string DecryptString(string value, string targetName = WindowsCredentialVault.DefaultTargetName) =>
        AesGcmDataProtector.Shared.Unprotect(value, targetName);

    public static string EncryptJsonPayload(string json, string targetName = WindowsCredentialVault.DefaultTargetName) =>
        AesGcmDataProtector.Shared.Protect(json, targetName);

    public static string DecryptJsonPayload(string payload, string targetName = WindowsCredentialVault.DefaultTargetName) =>
        AesGcmDataProtector.Shared.Unprotect(payload.Trim(), targetName);
}
