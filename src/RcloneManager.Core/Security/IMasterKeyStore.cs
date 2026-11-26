using System.Security.Cryptography;

namespace RcloneManager.Core.Security;

public interface IMasterKeyStore
{
    string DefaultTargetName { get; }
    bool KeyExists(string? targetName = null);
    byte[] GetOrCreateKey(out bool wasCreated, string? targetName = null);
    byte[] RotateKey(string? targetName = null);
    bool DeleteKey(string? targetName = null);
}

/// <summary>
/// Implementação em memória de <see cref="IMasterKeyStore"/> para testes isolados e cenários desacoplados do SO.
/// </summary>
public sealed class InMemoryMasterKeyStore : IMasterKeyStore
{
    private const int KeyLengthBytes = 32;
    private readonly Dictionary<string, byte[]> _keys = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public string DefaultTargetName => WindowsCredentialVault.DefaultTargetName;

    public bool KeyExists(string? targetName = null)
    {
        var target = ResolveTarget(targetName);
        lock (_sync)
        {
            return _keys.TryGetValue(target, out var key) && key.Length == KeyLengthBytes;
        }
    }

    public byte[] GetOrCreateKey(out bool wasCreated, string? targetName = null)
    {
        var target = ResolveTarget(targetName);
        lock (_sync)
        {
            if (_keys.TryGetValue(target, out var existing) && existing.Length == KeyLengthBytes)
            {
                wasCreated = false;
                return existing.ToArray();
            }

            var created = RandomNumberGenerator.GetBytes(KeyLengthBytes);
            _keys[target] = created.ToArray();
            wasCreated = true;
            return created;
        }
    }

    public byte[] RotateKey(string? targetName = null)
    {
        var target = ResolveTarget(targetName);
        lock (_sync)
        {
            if (_keys.TryGetValue(target, out var previous))
                CryptographicOperations.ZeroMemory(previous);

            var rotated = RandomNumberGenerator.GetBytes(KeyLengthBytes);
            _keys[target] = rotated.ToArray();
            return rotated;
        }
    }

    public bool DeleteKey(string? targetName = null)
    {
        var target = ResolveTarget(targetName);
        lock (_sync)
        {
            if (!_keys.Remove(target, out var removed))
                return false;

            CryptographicOperations.ZeroMemory(removed);
            return true;
        }
    }

    private string ResolveTarget(string? targetName) =>
        string.IsNullOrWhiteSpace(targetName) ? DefaultTargetName : targetName;
}
