using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace RcloneManager.Core.Security;

public static class WindowsCredentialVault
{
    public const string DefaultTargetName = "RcloneManager:MasterKey";

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int KeyLengthBytes = 32;
    private static readonly object VaultLock = new();

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

    public static bool KeyExists(string targetName = DefaultTargetName)
    {
        lock (VaultLock)
        {
            if (CredRead(targetName, CredTypeGeneric, 0, out var credPtr))
            {
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
            return false;
        }
    }

    public static byte[] GetOrCreateMasterKey(out bool wasCreated, string targetName = DefaultTargetName)
    {
        lock (VaultLock)
        {
            if (CredRead(targetName, CredTypeGeneric, 0, out var credPtr))
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
            WriteKeyInternal(newKey, targetName);
            wasCreated = true;
            return newKey;
        }
    }

    public static bool DeleteMasterKey(string targetName = DefaultTargetName)
    {
        lock (VaultLock)
        {
            return CredDelete(targetName, CredTypeGeneric, 0);
        }
    }

    public static byte[] RegenerateMasterKey(string targetName = DefaultTargetName)
    {
        lock (VaultLock)
        {
            var newKey = RandomNumberGenerator.GetBytes(KeyLengthBytes);
            WriteKeyInternal(newKey, targetName);
            return newKey;
        }
    }

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
                Comment = "Chave AES-256 do RcloneManager para criptografia de dados sensiveis",
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
