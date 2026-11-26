using RcloneManager.Core.Models;
using RcloneManager.Core.Security;

namespace RcloneManager.Core.Storage;

public sealed class ConfigCryptoTransformer : IConfigCryptoTransformer
{
    private readonly ISensitiveDataProtector _protector;

    public ConfigCryptoTransformer(ISensitiveDataProtector protector)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public bool HasSensitiveData(AppManagerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!string.IsNullOrWhiteSpace(config.RcloneConfigPath))
            return true;

        if (config.ProtectedSecrets is { Count: > 0 } &&
            config.ProtectedSecrets.Values.Any(v => !string.IsNullOrWhiteSpace(v)))
            return true;

        return config.Jobs?.Any(j => !string.IsNullOrWhiteSpace(j.ExtraFlags)) == true;
    }

    public AppManagerConfig ProtectForStorage(AppManagerConfig source, bool rotateDisposableKey = false)
    {
        ArgumentNullException.ThrowIfNull(source);

        var clone = source.Clone();
        var target = ResolveTarget(clone.EncryptionKeyTarget);
        clone.EncryptionKeyTarget = target;

        if (!clone.EncryptionEnabled)
            return clone;

        if (rotateDisposableKey)
        {
            _protector.RotateDisposableKey(target);
        }
        else if (HasSensitiveData(clone))
        {
            _protector.EnsureKeyInitialized(out _, target);
        }

        string ProtectField(string value) =>
            string.IsNullOrWhiteSpace(value)
                ? value
                : _protector.Protect(UnprotectIfNeeded(value, target), target);

        clone.RcloneConfigPath = ProtectField(clone.RcloneConfigPath);

        foreach (var key in clone.ProtectedSecrets.Keys.ToList())
            clone.ProtectedSecrets[key] = ProtectField(clone.ProtectedSecrets[key]);

        foreach (var job in clone.Jobs)
            job.ExtraFlags = ProtectField(job.ExtraFlags);

        return clone;
    }

    public void UnprotectInPlace(AppManagerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var target = ResolveTarget(config.EncryptionKeyTarget);
        config.EncryptionKeyTarget = target;

        config.RcloneConfigPath = SafeUnprotect(config.RcloneConfigPath, target);

        if (config.ProtectedSecrets != null)
        {
            foreach (var key in config.ProtectedSecrets.Keys.ToList())
                config.ProtectedSecrets[key] = SafeUnprotect(config.ProtectedSecrets[key], target);
        }

        if (config.Jobs != null)
        {
            foreach (var job in config.Jobs)
                job.ExtraFlags = SafeUnprotect(job.ExtraFlags, target);
        }
    }

    private string UnprotectIfNeeded(string value, string target)
    {
        if (!_protector.IsProtected(value))
            return value;

        return SafeUnprotect(value, target);
    }

    private string SafeUnprotect(string value, string target)
    {
        if (string.IsNullOrEmpty(value) || !_protector.IsProtected(value))
            return value;

        try
        {
            return _protector.Unprotect(value, target);
        }
        catch
        {
            // Chave descartável ausente ou substituída externamente: limpa apenas o segredo ilegível sem quebrar o boot.
            return string.Empty;
        }
    }

    private static string ResolveTarget(string? configuredTarget) =>
        string.IsNullOrWhiteSpace(configuredTarget)
            ? WindowsCredentialVault.DefaultTargetName
            : configuredTarget;
}
