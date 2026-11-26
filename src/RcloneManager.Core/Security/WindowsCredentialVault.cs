using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

[assembly: SupportedOSPlatform("windows")]

namespace RcloneManager.Core.Security;

[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialMasterKeyStore : IMasterKeyStore
{
    public static WindowsCredentialMasterKeyStore Shared { get; } = new();

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int KeyLengthBytes = 32;
    private static readonly object VaultLock = new();

    public string DefaultTargetName => WindowsCredentialVault.DefaultTargetName;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL userCredential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    private static extern void CredFree(IntPtr cred);

    public bool KeyExists(string? targetName = null)
    {
        var target = ResolveTarget(targetName);
        lock (VaultLock)
        {
            if (!CredRead(target, CredTypeGeneric, 0, out var credPtr))
                return false;

            try
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                return cred.CredentialBlobSize == KeyLengthBytes && cred.CredentialBlob != IntPtr.Zero;
            }
            finally
            {
                CredFree(credPtr);
            }
        }
    }

    public byte[] GetOrCreateKey(out bool wasCreated, string? targetName = null)
    {
        var target = ResolveTarget(targetName);
        lock (VaultLock)
        {
            if (CredRead(target, CredTypeGeneric, 0, out var credPtr))
            {
                try
                {
                    var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
                    if (cred.CredentialBlobSize == KeyLengthBytes && cred.CredentialBlob != IntPtr.Zero)
                    {
                        var existing = new byte[KeyLengthBytes];
                        Marshal.Copy(cred.CredentialBlob, existing, 0, KeyLengthBytes);
                        wasCreated = false;
                        return existing;
                    }
                }
                finally
                {
                    CredFree(credPtr);
                }
            }

            var newKey = RandomNumberGenerator.GetBytes(KeyLengthBytes);
            WriteKeyInternal(newKey, target);
            wasCreated = true;
            return newKey;
        }
    }

    public byte[] RotateKey(string? targetName = null)
    {
        var target = ResolveTarget(targetName);
        lock (VaultLock)
        {
            var newKey = RandomNumberGenerator.GetBytes(KeyLengthBytes);
            WriteKeyInternal(newKey, target);
            return newKey;
        }
    }

    public bool DeleteKey(string? targetName = null)
    {
        var target = ResolveTarget(targetName);
        lock (VaultLock)
        {
            return CredDelete(target, CredTypeGeneric, 0);
        }
    }

    private string ResolveTarget(string? targetName) =>
        string.IsNullOrWhiteSpace(targetName) ? DefaultTargetName : targetName;

    private static void WriteKeyInternal(byte[] key, string targetName)
    {
        var blobPtr = Marshal.AllocHGlobal(key.Length);
        try
        {
            Marshal.Copy(key, 0, blobPtr, key.Length);
            var cred = new CREDENTIAL
            {
                Flags = 0,
                Type = CredTypeGeneric,
                TargetName = targetName,
                Comment = "Chave descartavel AES-256 do RcloneManager para protecao local de dados sensiveis",
                CredentialBlobSize = (uint)key.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = null,
                UserName = Environment.UserName
            };

            if (!CredWrite(ref cred, 0))
            {
                var err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err, $"Falha ao gravar chave no Gerenciador de Credenciais do Windows (Target='{targetName}', Win32={err}).");
            }
        }
        finally
        {
            for (var i = 0; i < key.Length; i++)
                Marshal.WriteByte(blobPtr, i, 0);
            Marshal.FreeHGlobal(blobPtr);
        }
    }
}

public static class WindowsCredentialVault
{
    public const string DefaultTargetName = "RcloneManager:MasterKey";

    public static bool KeyExists(string targetName = DefaultTargetName) =>
        WindowsCredentialMasterKeyStore.Shared.KeyExists(targetName);

    public static byte[] GetOrCreateMasterKey(out bool wasCreated, string targetName = DefaultTargetName) =>
        WindowsCredentialMasterKeyStore.Shared.GetOrCreateKey(out wasCreated, targetName);

    public static bool DeleteMasterKey(string targetName = DefaultTargetName) =>
        WindowsCredentialMasterKeyStore.Shared.DeleteKey(targetName);

    public static byte[] RegenerateMasterKey(string targetName = DefaultTargetName) =>
        WindowsCredentialMasterKeyStore.Shared.RotateKey(targetName);
}
