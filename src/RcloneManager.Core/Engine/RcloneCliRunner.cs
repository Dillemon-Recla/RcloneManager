using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RcloneManager.Core.Models;

namespace RcloneManager.Core.Engine;

public static class RcloneCliRunner
{
    public static int FindFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static async Task<(int ExitCode, string StdOut, string StdErr)> RunSimpleCommandAsync(
        string rcloneExePath,
        string? rcloneConfigPath,
        string workingDirectory,
        IEnumerable<string> args,
        CancellationToken ct = default,
        bool closeStdInImmediately = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = rcloneExePath,
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : AppContext.BaseDirectory,
            RedirectStandardInput = closeStdInImmediately,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(rcloneConfigPath) && File.Exists(rcloneConfigPath))
        {
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(rcloneConfigPath);
        }

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        process.Start();
        if (closeStdInImmediately)
        {
            try { process.StandardInput.Close(); } catch { }
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        return (process.ExitCode, stdOut, stdErr);
    }

    public static ProcessStartInfo BuildJobStartInfo(
        SyncJob job,
        AppManagerConfig appConfig,
        bool dryRunOverride,
        int? rcPort,
        out List<string> resolutionNotes)
    {
        resolutionNotes = new List<string>();
        var workDir = Directory.Exists(appConfig.WorkingDirectory)
            ? appConfig.WorkingDirectory
            : Path.GetDirectoryName(appConfig.RcloneExecutablePath) ?? AppContext.BaseDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = appConfig.RcloneExecutablePath,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        var verb = job.Operation switch
        {
            RcloneOperation.Sync => "sync",
            RcloneOperation.Copy => "copy",
            RcloneOperation.Move => "move",
            RcloneOperation.Check => "check",
            _ => "sync"
        };

        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(job.SourcePath);
        psi.ArgumentList.Add(job.DestinationPath);

        if (!string.IsNullOrWhiteSpace(appConfig.RcloneConfigPath) && File.Exists(appConfig.RcloneConfigPath))
        {
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(appConfig.RcloneConfigPath);
        }

        if (!string.IsNullOrWhiteSpace(job.FilterFilePath))
        {
            var resolvedFilterPath = Path.IsPathRooted(job.FilterFilePath)
                ? job.FilterFilePath
                : Path.Combine(workDir, job.FilterFilePath);

            if (File.Exists(resolvedFilterPath))
            {
                var (flagName, effectiveFilePath, autoMaxSize, notes) =
                    FilterFileHelper.ResolveForExecution(resolvedFilterPath, job.FilterMode, workDir);

                resolutionNotes.AddRange(notes);
                psi.ArgumentList.Add(flagName);
                psi.ArgumentList.Add(effectiveFilePath);

                if (string.IsNullOrWhiteSpace(job.MaxSize) && !string.IsNullOrWhiteSpace(autoMaxSize))
                {
                    psi.ArgumentList.Add("--max-size");
                    psi.ArgumentList.Add(autoMaxSize);
                    resolutionNotes.Add($"Aplicado --max-size {autoMaxSize} extraído do arquivo de filtro.");
                }
            }
            else
            {
                resolutionNotes.Add($"Aviso: arquivo de filtro '{resolvedFilterPath}' não encontrado.");
            }
        }

        if (!string.IsNullOrWhiteSpace(job.MaxSize))
        {
            psi.ArgumentList.Add("--max-size");
            psi.ArgumentList.Add(job.MaxSize.Trim());
        }

        var effectiveBwLimit = !string.IsNullOrWhiteSpace(job.BandwidthLimit)
            ? job.BandwidthLimit.Trim()
            : (!string.IsNullOrWhiteSpace(appConfig.GlobalBandwidthLimit) && appConfig.GlobalBandwidthLimit != "off"
                ? appConfig.GlobalBandwidthLimit.Trim()
                : string.Empty);

        if (!string.IsNullOrWhiteSpace(effectiveBwLimit) && !effectiveBwLimit.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("--bwlimit");
            psi.ArgumentList.Add(effectiveBwLimit);
        }

        if (job.SkipLinks)
            psi.ArgumentList.Add("--skip-links");
        if (job.CopyLinks)
            psi.ArgumentList.Add("--copy-links");
        if (job.FastList)
            psi.ArgumentList.Add("--fast-list");
        if (job.DryRun || dryRunOverride)
            psi.ArgumentList.Add("--dry-run");

        if (!string.IsNullOrWhiteSpace(job.LogFilePath))
        {
            var resolvedLog = Path.IsPathRooted(job.LogFilePath)
                ? job.LogFilePath
                : Path.Combine(workDir, job.LogFilePath);
            psi.ArgumentList.Add("--log-file");
            psi.ArgumentList.Add(resolvedLog);
            psi.ArgumentList.Add("--log-level");
            psi.ArgumentList.Add("INFO");
        }

        if (!string.IsNullOrWhiteSpace(job.ExtraFlags))
        {
            foreach (var token in SplitCommandLineTokens(job.ExtraFlags))
                psi.ArgumentList.Add(token);
        }

        if (rcPort.HasValue)
        {
            psi.ArgumentList.Add("--rc");
            psi.ArgumentList.Add($"--rc-addr=127.0.0.1:{rcPort.Value}");
            psi.ArgumentList.Add("--rc-no-auth");
            psi.ArgumentList.Add("--stats=1s");
        }

        return psi;
    }

    public static IEnumerable<string> SplitCommandLineTokens(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            yield break;

        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in input)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            yield return current.ToString();
    }
}
