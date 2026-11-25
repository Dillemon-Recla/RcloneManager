using System.Diagnostics;
using RcloneManager.Core.Logging;
using RcloneManager.Core.Models;

namespace RcloneManager.Core.Engine;

public sealed class RcloneHybridEngine : IAsyncDisposable
{
    private readonly ServiceLogBuffer _log;
    private readonly object _stateLock = new();

    private Process? _rcdProcess;
    private int _rcdPort;
    private bool _rcdRunning;
    private string _cachedRcloneVersion = string.Empty;

    private ActiveJobExecutionInfo? _activeExecution;
    private RcloneStats? _liveStats;
    private CancellationTokenSource? _activeJobCts;
    private RcloneRcClient? _activeJobRcClient;
    private Process? _activeCliProcess;

    public RcloneHybridEngine(ServiceLogBuffer log)
    {
        _log = log;
    }

    public bool IsRcDaemonRunning
    {
        get { lock (_stateLock) return _rcdRunning && _rcdProcess is { HasExited: false }; }
    }

    public int RcDaemonPort
    {
        get { lock (_stateLock) return _rcdPort; }
    }

    public ActiveJobExecutionInfo? GetActiveExecution()
    {
        lock (_stateLock) return _activeExecution;
    }

    public RcloneStats? GetLiveStats()
    {
        lock (_stateLock) return _liveStats;
    }

    public void InvalidateVersionCache()
    {
        _cachedRcloneVersion = string.Empty;
    }

    public async Task<(bool IsValid, string ErrorMessage)> ValidatePinnedVersionAsync(AppManagerConfig config, CancellationToken ct = default)
    {
        if (!config.LockRcloneVersion)
            return (true, string.Empty);

        var pinned = RcloneReleaseManager.TryNormalizeVersion(config.PinnedRcloneVersion, out var norm)
            ? norm
            : RcloneReleaseManager.DefaultPinnedVersion;

        var actual = await GetRcloneVersionAsync(config, ct);
        if (!string.Equals(actual, pinned, StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"Execução bloqueada pela trava de versão: esperado '{pinned}', encontrado '{actual}' em '{config.RcloneExecutablePath}'.");
        }

        return (true, string.Empty);
    }

    public async Task<string> GetRcloneVersionAsync(AppManagerConfig config, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_cachedRcloneVersion))
            return _cachedRcloneVersion;

        if (IsRcDaemonRunning)
        {
            try
            {
                using var rc = new RcloneRcClient(_rcdPort);
                var ver = await rc.GetVersionAsync(ct);
                if (!string.IsNullOrWhiteSpace(ver))
                {
                    _cachedRcloneVersion = ver;
                    return ver;
                }
            }
            catch
            {
                // Fallback para CLI
            }
        }

        if (File.Exists(config.RcloneExecutablePath))
        {
            try
            {
                var (exitCode, stdOut, _) = await RcloneCliRunner.RunSimpleCommandAsync(
                    config.RcloneExecutablePath,
                    config.RcloneConfigPath,
                    config.WorkingDirectory,
                    new[] { "version" },
                    ct);
                if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdOut))
                {
                    var firstLine = stdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
                    _cachedRcloneVersion = firstLine.Replace("rclone ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
                    return _cachedRcloneVersion;
                }
            }
            catch
            {
                // Ignora caso não seja possível executar agora
            }
        }

        return "Desconhecida";
    }

    public async Task<bool> StartRcDaemonAsync(AppManagerConfig config, CancellationToken ct = default)
    {
        if (IsRcDaemonRunning)
            return true;

        if (!File.Exists(config.RcloneExecutablePath))
        {
            _log.Error($"rclone.exe não encontrado em '{config.RcloneExecutablePath}'.");
            return false;
        }

        var (versionValid, versionError) = await ValidatePinnedVersionAsync(config, ct);
        if (!versionValid)
        {
            _log.Error(versionError);
            return false;
        }

        var port = config.PersistentRcdPort > 0 ? config.PersistentRcdPort : 5572;
        var psi = new ProcessStartInfo
        {
            FileName = config.RcloneExecutablePath,
            WorkingDirectory = Directory.Exists(config.WorkingDirectory) ? config.WorkingDirectory : AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("rcd");
        psi.ArgumentList.Add($"--rc-addr=127.0.0.1:{port}");
        psi.ArgumentList.Add("--rc-no-auth");

        if (!string.IsNullOrWhiteSpace(config.RcloneConfigPath) && File.Exists(config.RcloneConfigPath))
        {
            psi.ArgumentList.Add("--config");
            psi.ArgumentList.Add(config.RcloneConfigPath);
        }

        if (!string.IsNullOrWhiteSpace(config.GlobalBandwidthLimit) &&
            !config.GlobalBandwidthLimit.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("--bwlimit");
            psi.ArgumentList.Add(config.GlobalBandwidthLimit);
        }

        try
        {
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.Exited += (_, _) =>
            {
                lock (_stateLock)
                {
                    _rcdRunning = false;
                }
                _log.Warn($"Daemon rclone rcd (porta {port}) foi encerrado.");
            };

            proc.Start();
            // Drena stdout/stderr em background para evitar deadlock de buffer
            _ = Task.Run(async () =>
            {
                try { await proc.StandardOutput.ReadToEndAsync(); } catch { }
            });
            _ = Task.Run(async () =>
            {
                try { await proc.StandardError.ReadToEndAsync(); } catch { }
            });

            using var client = new RcloneRcClient(port);
            for (var i = 0; i < 15; i++)
            {
                await Task.Delay(200, ct);
                if (proc.HasExited)
                    break;
                if (await client.PingAsync(ct))
                {
                    lock (_stateLock)
                    {
                        _rcdProcess = proc;
                        _rcdPort = port;
                        _rcdRunning = true;
                    }
                    _log.Info($"Daemon rclone rcd iniciado na porta {port} (PID {proc.Id}).");
                    return true;
                }
            }

            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
            proc.Dispose();
            _log.Warn($"rclone rcd não respondeu ao ping RC na porta {port}. Modo CLI assumirá quando necessário.");
            return false;
        }
        catch (Exception ex)
        {
            _log.Error($"Falha ao iniciar rclone rcd: {ex.Message}");
            return false;
        }
    }

    public async Task StopRcDaemonAsync()
    {
        Process? proc;
        int port;
        lock (_stateLock)
        {
            proc = _rcdProcess;
            port = _rcdPort;
            _rcdProcess = null;
            _rcdRunning = false;
        }

        if (proc != null)
        {
            try
            {
                if (!proc.HasExited)
                {
                    using var rc = new RcloneRcClient(port, TimeSpan.FromSeconds(2));
                    await rc.QuitAsync();
                    if (!proc.WaitForExit(1500))
                        proc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            }
            finally
            {
                proc.Dispose();
                _log.Info("Daemon rclone rcd parado.");
            }
        }
    }

    public async Task<(List<string> Remotes, string SourceUsed)> ListRemotesHybridAsync(AppManagerConfig config, CancellationToken ct = default)
    {
        if (IsRcDaemonRunning)
        {
            try
            {
                using var rc = new RcloneRcClient(_rcdPort);
                var remotes = await rc.ListRemotesAsync(ct);
                return (remotes, $"RC API (porta {_rcdPort})");
            }
            catch (Exception ex)
            {
                _log.Warn($"ListRemotes via RC falhou ({ex.Message}); alternando para CLI.");
            }
        }

        var (exitCode, stdOut, stdErr) = await RcloneCliRunner.RunSimpleCommandAsync(
            config.RcloneExecutablePath,
            config.RcloneConfigPath,
            config.WorkingDirectory,
            new[] { "listremotes" },
            ct);

        if (exitCode != 0)
            throw new InvalidOperationException($"rclone listremotes falhou: {stdErr}");

        var list = stdOut
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        return (list, "CLI (rclone listremotes)");
    }

    public async Task<RcloneAboutInfo?> GetRemoteAboutAsync(AppManagerConfig config, string remoteFs, CancellationToken ct = default)
    {
        if (IsRcDaemonRunning)
        {
            try
            {
                using var rc = new RcloneRcClient(_rcdPort, TimeSpan.FromSeconds(10));
                return await rc.GetAboutAsync(remoteFs, ct);
            }
            catch
            {
                // fallback CLI
            }
        }

        var (exitCode, stdOut, _) = await RcloneCliRunner.RunSimpleCommandAsync(
            config.RcloneExecutablePath,
            config.RcloneConfigPath,
            config.WorkingDirectory,
            new[] { "about", remoteFs, "--json" },
            ct);

        if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdOut))
        {
            return System.Text.Json.JsonSerializer.Deserialize<RcloneAboutInfo>(stdOut);
        }

        return null;
    }

    public async Task<string> SetLiveBandwidthLimitAsync(AppManagerConfig config, string rate, CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(rate) ? "off" : rate.Trim();
        var appliedTargets = new List<string>();

        RcloneRcClient? activeRc;
        lock (_stateLock) activeRc = _activeJobRcClient;

        if (activeRc != null)
        {
            try
            {
                await activeRc.SetBandwidthLimitAsync(normalized, ct);
                appliedTargets.Add("job ativo");
            }
            catch (Exception ex)
            {
                _log.Warn($"Não foi possível ajustar bwlimit no job ativo: {ex.Message}");
            }
        }

        if (IsRcDaemonRunning)
        {
            try
            {
                using var rc = new RcloneRcClient(_rcdPort);
                await rc.SetBandwidthLimitAsync(normalized, ct);
                appliedTargets.Add("daemon rcd");
            }
            catch (Exception ex)
            {
                _log.Warn($"Não foi possível ajustar bwlimit no daemon rcd: {ex.Message}");
            }
        }

        var msg = appliedTargets.Count > 0
            ? $"Limite de banda '{normalized}' aplicado via RC em: {string.Join(", ", appliedTargets)}."
            : $"Limite de banda '{normalized}' salvo para as próximas execuções via CLI (--bwlimit).";
        _log.Info(msg);
        return msg;
    }

    /// <summary>
    /// Avalia caso a caso qual estratégia usar para o job:
    /// - Se usa filtro por arquivo, max-size, skip-links, fast-list, log-file ou extraFlags: usa CLI + RC Monitoring.
    /// - Se é tarefa simples sem flags exclusivas de CLI e daemon rcd está ativo: pode usar RC Daemon API.
    /// </summary>
    public (ExecutionStrategy Resolved, string Reason) ResolveStrategy(SyncJob job, ExecutionStrategy? overrideStrategy)
    {
        var requested = overrideStrategy ?? job.Strategy;
        if (requested != ExecutionStrategy.HybridAuto)
        {
            return requested switch
            {
                ExecutionStrategy.CliWithRcMonitoring => (ExecutionStrategy.CliWithRcMonitoring, "Selecionado manualmente: CLI + Monitoramento RC em tempo real"),
                ExecutionStrategy.RcDaemonApi when IsRcDaemonRunning => (ExecutionStrategy.RcDaemonApi, "Selecionado manualmente: API HTTP do daemon rclone rcd"),
                ExecutionStrategy.RcDaemonApi => (ExecutionStrategy.CliWithRcMonitoring, "Daemon rcd desligado; fallback automático para CLI + Monitoramento RC"),
                ExecutionStrategy.PureCli => (ExecutionStrategy.PureCli, "Selecionado manualmente: CLI puro (sem porta RC)"),
                _ => (ExecutionStrategy.CliWithRcMonitoring, "CLI + Monitoramento RC")
            };
        }

        var requiresCliFlags =
            !string.IsNullOrWhiteSpace(job.FilterFilePath) ||
            !string.IsNullOrWhiteSpace(job.MaxSize) ||
            !string.IsNullOrWhiteSpace(job.LogFilePath) ||
            !string.IsNullOrWhiteSpace(job.ExtraFlags) ||
            job.SkipLinks ||
            job.FastList ||
            job.CopyLinks;

        if (requiresCliFlags)
        {
            return (
                ExecutionStrategy.CliWithRcMonitoring,
                "Híbrido: CLI + RC ao vivo (necessário para --filter-from/--max-size/--skip-links/--fast-list, com telemetria via API RC)"
            );
        }

        if (IsRcDaemonRunning)
        {
            return (
                ExecutionStrategy.RcDaemonApi,
                "Híbrido: API HTTP RC Daemon (operação direta sem flags complexas)"
            );
        }

        return (
            ExecutionStrategy.CliWithRcMonitoring,
            "Híbrido: CLI + RC ao vivo (daemon rcd inativo)"
        );
    }

    public async Task<(bool Success, bool Cancelled, string Message, double DurationSeconds)> ExecuteJobAsync(
        SyncJob job,
        AppManagerConfig config,
        bool dryRunOverride,
        ExecutionStrategy? overrideStrategy,
        CancellationToken serviceShutdownToken)
    {
        if (!File.Exists(config.RcloneExecutablePath))
            return (false, false, $"Executável rclone.exe não encontrado em '{config.RcloneExecutablePath}'.", 0);

        var (versionValid, versionError) = await ValidatePinnedVersionAsync(config, serviceShutdownToken);
        if (!versionValid)
        {
            _log.Error(versionError);
            return (false, false, versionError, 0);
        }

        CancellationTokenSource jobCts;
        lock (_stateLock)
        {
            if (_activeExecution != null)
                return (false, false, $"Já existe uma tarefa em execução: '{_activeExecution.JobName}'.", 0);

            jobCts = CancellationTokenSource.CreateLinkedTokenSource(serviceShutdownToken);
            _activeJobCts = jobCts;
        }

        var sw = Stopwatch.StartNew();
        var (resolvedStrategy, reason) = ResolveStrategy(job, overrideStrategy);
        var isDryRun = job.DryRun || dryRunOverride;

        _log.Info($"Iniciando tarefa '{job.Name}' ({job.Operation}, DryRun={isDryRun}) | Estratégia: {reason}");

        try
        {
            if (resolvedStrategy == ExecutionStrategy.RcDaemonApi)
            {
                try
                {
                    return await ExecuteViaRcDaemonAsync(job, isDryRun, reason, sw, jobCts.Token);
                }
                catch (Exception ex) when (!jobCts.IsCancellationRequested)
                {
                    _log.Warn($"Execução via RC Daemon falhou ({ex.Message}); aplicando fallback híbrido para CLI + Monitoramento RC.");
                    resolvedStrategy = ExecutionStrategy.CliWithRcMonitoring;
                    reason = "Fallback automático de RC Daemon para CLI + RC ao vivo";
                }
            }

            var useRcMonitor = resolvedStrategy == ExecutionStrategy.CliWithRcMonitoring;
            return await ExecuteViaCliAsync(job, config, isDryRun, useRcMonitor, reason, sw, jobCts.Token);
        }
        finally
        {
            sw.Stop();
            lock (_stateLock)
            {
                _activeExecution = null;
                _liveStats = null;
                _activeJobRcClient?.Dispose();
                _activeJobRcClient = null;
                _activeCliProcess = null;
                _activeJobCts?.Dispose();
                _activeJobCts = null;
            }
        }
    }

    private async Task<(bool Success, bool Cancelled, string Message, double DurationSeconds)> ExecuteViaRcDaemonAsync(
        SyncJob job,
        bool isDryRun,
        string reason,
        Stopwatch sw,
        CancellationToken ct)
    {
        var endpoint = job.Operation switch
        {
            RcloneOperation.Sync => "/sync/sync",
            RcloneOperation.Copy => "/sync/copy",
            RcloneOperation.Move => "/sync/move",
            RcloneOperation.Check => "/operations/check",
            _ => "/sync/sync"
        };

        using var rc = new RcloneRcClient(_rcdPort, TimeSpan.FromSeconds(10));
        var rcJobId = await rc.StartAsyncOperationAsync(endpoint, job.SourcePath, job.DestinationPath, isDryRun, ct);

        lock (_stateLock)
        {
            _activeExecution = new ActiveJobExecutionInfo
            {
                JobId = job.Id,
                JobName = job.Name,
                ResolvedStrategy = ExecutionStrategy.RcDaemonApi,
                StrategyReason = reason,
                StartedAt = DateTime.Now,
                ActiveRcPort = _rcdPort,
                RcJobId = rcJobId,
                IsDryRun = isDryRun
            };
            _activeJobRcClient = new RcloneRcClient(_rcdPort);
        }

        while (true)
        {
            if (ct.IsCancellationRequested)
            {
                try { await rc.StopJobAsync(rcJobId, CancellationToken.None); } catch { }
                return (false, true, "Cancelado pelo usuário ou serviço.", sw.Elapsed.TotalSeconds);
            }

            try
            {
                var stats = await rc.GetStatsAsync($"job/{rcJobId}", ct) ?? await rc.GetStatsAsync(null, ct);
                lock (_stateLock) _liveStats = stats;
            }
            catch
            {
                // Ignora falha transitória de polling
            }

            var (finished, success, error) = await rc.GetJobStatusAsync(rcJobId, ct);
            if (finished)
            {
                var finalStats = await rc.GetStatsAsync($"job/{rcJobId}", CancellationToken.None);
                lock (_stateLock) _liveStats = finalStats;

                if (success)
                {
                    var msg = $"Concluído via RC API em {sw.Elapsed.TotalSeconds:F1}s (DryRun={isDryRun}).";
                    _log.Info($"Tarefa '{job.Name}': {msg}");
                    return (true, false, msg, sw.Elapsed.TotalSeconds);
                }
                else
                {
                    var msg = string.IsNullOrWhiteSpace(error) ? "Falha na execução via RC API." : error;
                    _log.Error($"Tarefa '{job.Name}' falhou: {msg}");
                    return (false, false, msg, sw.Elapsed.TotalSeconds);
                }
            }

            await Task.Delay(1000, ct);
        }
    }

    private async Task<(bool Success, bool Cancelled, string Message, double DurationSeconds)> ExecuteViaCliAsync(
        SyncJob job,
        AppManagerConfig config,
        bool isDryRun,
        bool enableRcMonitor,
        string reason,
        Stopwatch sw,
        CancellationToken ct)
    {
        int? rcPort = enableRcMonitor ? RcloneCliRunner.FindFreeTcpPort() : null;
        var psi = RcloneCliRunner.BuildJobStartInfo(job, config, isDryRun, rcPort, out var notes);

        foreach (var note in notes)
            _log.Info($"[{job.Name}] {note}");

        using var process = new Process { StartInfo = psi };
        process.Start();

        RcloneRcClient? rcClient = rcPort.HasValue ? new RcloneRcClient(rcPort.Value, TimeSpan.FromSeconds(3)) : null;

        lock (_stateLock)
        {
            _activeCliProcess = process;
            _activeJobRcClient = rcClient;
            _activeExecution = new ActiveJobExecutionInfo
            {
                JobId = job.Id,
                JobName = job.Name,
                ResolvedStrategy = enableRcMonitor ? ExecutionStrategy.CliWithRcMonitoring : ExecutionStrategy.PureCli,
                StrategyReason = reason,
                StartedAt = DateTime.Now,
                ProcessId = process.Id,
                ActiveRcPort = rcPort,
                IsDryRun = isDryRun
            };
        }

        var errorLines = new List<string>();
        var stdOutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    _log.Info($"[rclone] {line}");
            }
        }, CancellationToken.None);

        var stdErrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    lock (errorLines)
                    {
                        if (errorLines.Count < 30)
                            errorLines.Add(line);
                    }
                    if (line.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || line.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase))
                        _log.Error($"[rclone] {line}");
                    else
                        _log.Info($"[rclone] {line}");
                }
            }
        }, CancellationToken.None);

        try
        {
            while (!process.HasExited)
            {
                if (ct.IsCancellationRequested)
                {
                    await TerminateProcessGracefullyAsync(process, rcClient);
                    return (false, true, "Execução interrompida pelo usuário/serviço.", sw.Elapsed.TotalSeconds);
                }

                if (rcClient != null)
                {
                    try
                    {
                        var stats = await rcClient.GetStatsAsync(null, ct);
                        if (stats != null)
                        {
                            lock (_stateLock) _liveStats = stats;
                        }
                    }
                    catch
                    {
                        // A porta RC do CLI pode levar 1-2s para subir ou fechar antes do processo sair.
                    }
                }

                await Task.Delay(1000, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            await TerminateProcessGracefullyAsync(process, rcClient);
            return (false, true, "Execução cancelada.", sw.Elapsed.TotalSeconds);
        }

        await Task.WhenAll(stdOutTask, stdErrTask);
        var exitCode = process.ExitCode;

        if (exitCode == 0)
        {
            var stats = _liveStats;
            var transferredInfo = stats != null ? $" ({stats.Transfers} arquivo(s), {FormatBytes(stats.Bytes)})" : string.Empty;
            var msg = $"Concluído com sucesso em {sw.Elapsed.TotalSeconds:F1}s{transferredInfo}.";
            _log.Info($"Tarefa '{job.Name}': {msg}");
            return (true, false, msg, sw.Elapsed.TotalSeconds);
        }

        string tailError;
        lock (errorLines)
        {
            tailError = errorLines.LastOrDefault() ?? $"Rclone retornou código de saída {exitCode}.";
        }
        var failMsg = $"ExitCode {exitCode}: {tailError}";
        _log.Error($"Tarefa '{job.Name}' falhou: {failMsg}");
        return (false, false, failMsg, sw.Elapsed.TotalSeconds);
    }

    private static async Task TerminateProcessGracefullyAsync(Process process, RcloneRcClient? rcClient)
    {
        try
        {
            if (rcClient != null)
            {
                await rcClient.QuitAsync();
                if (process.WaitForExit(2000))
                    return;
            }
        }
        catch
        {
            // Prossegue para kill
        }

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    public bool RequestStopActiveJob()
    {
        lock (_stateLock)
        {
            if (_activeJobCts == null)
                return false;

            _log.Warn($"Solicitação de parada recebida para tarefa '{_activeExecution?.JobName}'.");
            _activeJobCts.Cancel();
            return true;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double val = bytes;
        var idx = 0;
        while (val >= 1024 && idx < units.Length - 1)
        {
            val /= 1024;
            idx++;
        }
        return $"{val:0.##} {units[idx]}";
    }

    public async ValueTask DisposeAsync()
    {
        RequestStopActiveJob();
        await StopRcDaemonAsync();
    }
}
