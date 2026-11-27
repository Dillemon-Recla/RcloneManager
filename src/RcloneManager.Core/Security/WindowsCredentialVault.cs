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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const uint CryptProtectUiForbidden = 0x1;
    private const uint CryptProtectLocalMachine = 0x4;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        uint dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public bool KeyExists(string? targetName = null)
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
                        return true;
                }
                finally
                {
                    CredFree(credPtr);
                }
            }

            return TryReadMachineMirror(target) != null;
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
                        TryWriteMachineMirror(existing, target);
                        wasCreated = false;
                        return existing;
                    }
                }
                finally
                {
                    CredFree(credPtr);
                }
            }

            var mirrored = TryReadMachineMirror(target);
            if (mirrored is { Length: KeyLengthBytes })
            {
                try { WriteKeyInternal(mirrored, target); } catch { }
                wasCreated = false;
                return mirrored;
            }

            var newKey = RandomNumberGenerator.GetBytes(KeyLengthBytes);
            WriteKeyInternal(newKey, target);
            TryWriteMachineMirror(newKey, target);
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
            TryWriteMachineMirror(newKey, target);
            return newKey;
        }
    }

    public bool DeleteKey(string? targetName = null)
    {
        var target = ResolveTarget(targetName);
        lock (VaultLock)
        {
            var deletedMirror = TryDeleteMachineMirror(target);
            var deletedCred = CredDelete(target, CredTypeGeneric, 0);
            return deletedCred || deletedMirror;
        }
    }

    private static string GetMachineMirrorFilePath(string target)
    {
        var sanitized = new string(target.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RcloneManager");
        return Path.Combine(dir, $"vault_{sanitized}.dpapi");
    }

    private static void TryWriteMachineMirror(byte[] key, string target)
    {
        try
        {
            var filePath = GetMachineMirrorFilePath(target);
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var inPtr = Marshal.AllocHGlobal(key.Length);
            try
            {
                Marshal.Copy(key, 0, inPtr, key.Length);
                var dataIn = new DATA_BLOB { cbData = key.Length, pbData = inPtr };
                if (CryptProtectData(ref dataIn, "RcloneManager.ServiceKey", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CryptProtectUiForbidden | CryptProtectLocalMachine, out var dataOut))
                {
                    try
                    {
                        var encrypted = new byte[dataOut.cbData];
                        Marshal.Copy(dataOut.pbData, encrypted, 0, dataOut.cbData);
                        File.WriteAllBytes(filePath, encrypted);
                    }
                    finally
                    {
                        if (dataOut.pbData != IntPtr.Zero)
                            LocalFree(dataOut.pbData);
                    }
                }
            }
            finally
            {
                for (var i = 0; i < key.Length; i++)
                    Marshal.WriteByte(inPtr, i, 0);
                Marshal.FreeHGlobal(inPtr);
            }
        }
        catch
        {
            // Espelho DPAPI LocalMachine é auxiliar para execução sob conta SYSTEM (WinService)
        }
    }

    private static byte[]? TryReadMachineMirror(string target)
    {
        try
        {
            var filePath = GetMachineMirrorFilePath(target);
            if (!File.Exists(filePath))
                return null;

            var encrypted = File.ReadAllBytes(filePath);
            if (encrypted.Length == 0)
                return null;

            var inPtr = Marshal.AllocHGlobal(encrypted.Length);
            try
            {
                Marshal.Copy(encrypted, 0, inPtr, encrypted.Length);
                var dataIn = new DATA_BLOB { cbData = encrypted.Length, pbData = inPtr };
                if (!CryptUnprotectData(ref dataIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CryptProtectUiForbidden | CryptProtectLocalMachine, out var dataOut))
                {
                    return null;
                }

                try
                {
                    if (dataOut.cbData != KeyLengthBytes || dataOut.pbData == IntPtr.Zero)
                        return null;

                    var plain = new byte[KeyLengthBytes];
                    Marshal.Copy(dataOut.pbData, plain, 0, KeyLengthBytes);
                    return plain;
                }
                finally
                {
                    if (dataOut.pbData != IntPtr.Zero)
                        LocalFree(dataOut.pbData);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(inPtr);
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool TryDeleteMachineMirror(string target)
    {
        try
        {
            var filePath = GetMachineMirrorFilePath(target);
            if (!File.Exists(filePath))
                return false;

            File.Delete(filePath);
            return true;
        }
        catch
        {
            return false;
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
