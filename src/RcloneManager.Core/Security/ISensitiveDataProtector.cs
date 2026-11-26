namespace RcloneManager.Core.Security;

public interface ISensitiveDataProtector
{
    bool IsProtected(string? value);
    string EnsureKeyInitialized(out bool keyCreated, string? targetName = null);
    string RotateDisposableKey(string? targetName = null);
    bool DiscardKey(string? targetName = null);
    string Protect(string plainText, string? targetName = null);
    string Unprotect(string value, string? targetName = null);
}
