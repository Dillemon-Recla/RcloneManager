using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace RcloneManager.Core.Security;

[SupportedOSPlatform("windows")]
public static class SensitiveDataProtector
{
    public const string EncryptedPrefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static string EnsureInitialized(out bool keyCreated, string targetName = WindowsCredentialVault.DefaultTargetName)
    {
        var key = WindowsCredentialVault.GetOrCreateMasterKey(out keyCreated, targetName);
        CryptographicOperations.ZeroMemory(key);
        return targetName;
    }

    public static string RotateMasterKey(string targetName = WindowsCredentialVault.DefaultTargetName)
    {
        var key = WindowsCredentialVault.RegenerateMasterKey(targetName);
        CryptographicOperations.ZeroMemory(key);
        return targetName;
    }

    public static bool IsEncrypted(string? value)
    {
        return !string.IsNullOrEmpty(value) && value.StartsWith(EncryptedPrefix, StringComparison.Ordinal);
    }

    public static string EncryptString(string plainText, string targetName = WindowsCredentialVault.DefaultTargetName)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        if (IsEncrypted(plainText))
            return plainText;

        var key = WindowsCredentialVault.GetOrCreateMasterKey(out _, targetName);
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

    public static string DecryptString(string value, string targetName = WindowsCredentialVault.DefaultTargetName)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (!IsEncrypted(value))
            return value;

        var payloadBase64 = value[EncryptedPrefix.Length..];
        var combined = Convert.FromBase64String(payloadBase64);
        if (combined.Length < NonceSize + TagSize)
            throw new CryptographicException("Payload criptografado inválido ou corrompido.");

        var key = WindowsCredentialVault.GetOrCreateMasterKey(out _, targetName);
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

    public static string EncryptJsonPayload(string json, string targetName = WindowsCredentialVault.DefaultTargetName)
    {
        return EncryptString(json, targetName);
    }

    public static string DecryptJsonPayload(string payload, string targetName = WindowsCredentialVault.DefaultTargetName)
    {
        return DecryptString(payload.Trim(), targetName);
    }
}
