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

    public static (bool Installed, bool Running) GetWindowsServiceState()
    {
        try
        {
            var (code, output) = RunScCommand($"query {WindowsServiceName}");
            if (code != 0)
                return (false, false);

            var running = output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase) ||
                          output.Contains("4  RUNNING", StringComparison.OrdinalIgnoreCase);
            return (true, running);
        }
        catch
        {
            return (false, false);
        }
    }

    public static (int ExitCode, string Output) InstallAndStartWindowsService(string exePath, string workDir)
    {
        var sb = new StringBuilder();
        var binPath = $"\"{exePath}\" --windows-service --workdir \"{workDir}\"";
        var escapedBin = binPath.Replace("\"", "\\\"");

        var (installed, running) = GetWindowsServiceState();
        if (running)
        {
            var (_, stopOut) = RunScCommand($"stop {WindowsServiceName}");
            sb.AppendLine(stopOut);
            Thread.Sleep(1500);
        }

        int code;
        string createOrConfigOut;
        if (installed)
        {
            (code, createOrConfigOut) = RunScCommand(
                $"config {WindowsServiceName} binPath= \"{escapedBin}\" start= auto DisplayName= \"Rclone Manager Service\"");
        }
        else
        {
            (code, createOrConfigOut) = RunScCommand(
                $"create {WindowsServiceName} binPath= \"{escapedBin}\" start= auto DisplayName= \"Rclone Manager Service\"");
        }
        sb.AppendLine(createOrConfigOut);

        if (code != 0)
            return (code, sb.ToString().Trim());

        RunScCommand($"description {WindowsServiceName} \"Agendador automático e motor híbrido de backups do RcloneManager.\"");
        RunScCommand($"failure {WindowsServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000");

        var (startCode, startOut) = RunScCommand($"start {WindowsServiceName}");
        sb.AppendLine(startOut);

        // Código 1056 significa que uma instância do serviço já está em execução
        var finalCode = (startCode == 0 || startCode == 1056) ? 0 : startCode;
        return (finalCode, sb.ToString().Trim());
    }
}
