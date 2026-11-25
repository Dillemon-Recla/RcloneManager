using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using RcloneManager.Core.Storage;

namespace RcloneManager.Service;

[SupportedOSPlatform("windows")]
public static class ServiceControlHelper
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "RcloneManagerService";
    public const string WindowsServiceName = "RcloneManagerService";

    public static string ResolveWorkspaceDirectory(string[] args)
    {
        string? explicitWorkDir = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--workdir", StringComparison.OrdinalIgnoreCase))
            {
                explicitWorkDir = args[i + 1];
                break;
            }
        }

        return WorkspacePathResolver.ResolveRootDirectory(explicitWorkDir);
    }

    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var val = key?.GetValue(StartupValueName) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public static void SetStartupEnabled(bool enabled, string workspaceDirectory)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "RcloneManager.Service.exe");
            var command = $"\"{exePath}\" --background --workdir \"{workspaceDirectory}\"";
            key.SetValue(StartupValueName, command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }
    }

    public static List<string> ReadTailLines(string filePath, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return new List<string>();

        var result = new List<string>();
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            var queue = new Queue<string>(maxLines + 1);
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (line == null)
                    break;
                if (queue.Count >= maxLines)
                    queue.Dequeue();
                queue.Enqueue(line);
            }

            result.AddRange(queue);
        }
        catch (Exception ex)
        {
            result.Add($"[Erro ao ler log '{filePath}': {ex.Message}]");
        }

        return result;
    }

    public static (int ExitCode, string Output) RunScCommand(string arguments)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        proc.Start();
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(10000);
        return (proc.ExitCode, (stdout + Environment.NewLine + stderr).Trim());
    }
}
