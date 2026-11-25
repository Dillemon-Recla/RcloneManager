using System.Diagnostics;
using System.Text.Json;
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
    private List<RcloneProviderInfo>? _cachedProviders;

    private const string MaskedSecretPlaceholder = "••••••••";
    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "pass", "password", "client_secret", "secret_access_key",
        "key_pem", "sas_url", "account_key", "jwt", "oauth_token"
    };
    private static readonly string[] PopularProviderOrder =
    {
        "drive", "onedrive", "dropbox", "s3", "sftp", "ftp", "smb", "webdav", "mega", "local"
    };

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
        _cachedProviders = null;
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

    public async Task<(List<RcloneRemoteDetail> Remotes, string SourceUsed)> GetRemotesDetailsHybridAsync(
        AppManagerConfig config,
        CancellationToken ct = default)
    {
        Dictionary<string, Dictionary<string, string>>? dump = null;
        var sourceUsed = string.Empty;

        if (IsRcDaemonRunning)
        {
            try
            {
                using var rc = new RcloneRcClient(_rcdPort);
                dump = await rc.GetConfigDumpAsync(ct);
                sourceUsed = $"RC API (porta {_rcdPort})";
            }
            catch (Exception ex)
            {
                _log.Warn($"ConfigDump via RC falhou ({ex.Message}); alternando para CLI.");
            }
        }

        if (dump == null)
        {
            var (exitCode, stdOut, stdErr) = await RcloneCliRunner.RunSimpleCommandAsync(
                config.RcloneExecutablePath,
                config.RcloneConfigPath,
                config.WorkingDirectory,
                new[] { "config", "dump" },
                ct);

            if (exitCode != 0)
                throw new InvalidOperationException($"rclone config dump falhou: {stdErr}");

            dump = string.IsNullOrWhiteSpace(stdOut)
                ? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                : JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(stdOut,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            sourceUsed = "CLI (rclone config dump)";
        }

        var list = new List<RcloneRemoteDetail>();
        foreach (var (remoteName, rawParams) in dump.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var maskedParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var keys = new List<string>();
            var hasToken = false;

            foreach (var (k, v) in rawParams)
            {
                keys.Add(k);
                if (k.Equals("token", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v))
                    hasToken = true;

                maskedParams[k] = IsSensitiveParameter(k) && !string.IsNullOrWhiteSpace(v)
                    ? MaskedSecretPlaceholder
                    : v;
            }

            rawParams.TryGetValue("type", out var remoteType);
            list.Add(new RcloneRemoteDetail
            {
                Name = remoteName,
                Type = remoteType ?? string.Empty,
                HasToken = hasToken,
                Parameters = maskedParams,
                RawParameterKeys = keys
            });
        }

        return (list, sourceUsed);
    }

    public async Task<(List<RcloneProviderInfo> Providers, string SourceUsed)> GetProvidersHybridAsync(
        AppManagerConfig config,
        CancellationToken ct = default)
    {
        if (_cachedProviders != null)
            return (_cachedProviders, "Cache em memória");

        List<RcloneProviderInfo>? providers = null;
        var sourceUsed = string.Empty;

        if (IsRcDaemonRunning)
        {
            try
            {
                using var rc = new RcloneRcClient(_rcdPort, TimeSpan.FromSeconds(10));
                providers = await rc.GetProvidersAsync(ct);
                sourceUsed = $"RC API (porta {_rcdPort})";
            }
            catch (Exception ex)
            {
                _log.Warn($"Providers via RC falhou ({ex.Message}); alternando para CLI.");
            }
        }

        if (providers == null)
        {
            var (exitCode, stdOut, stdErr) = await RcloneCliRunner.RunSimpleCommandAsync(
                config.RcloneExecutablePath,
                config.RcloneConfigPath,
                config.WorkingDirectory,
                new[] { "config", "providers" },
                ct);

            if (exitCode != 0)
                throw new InvalidOperationException($"rclone config providers falhou: {stdErr}");

            providers = JsonSerializer.Deserialize<List<RcloneProviderInfo>>(stdOut,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<RcloneProviderInfo>();
            sourceUsed = "CLI (rclone config providers)";
        }

        var sorted = providers
            .OrderBy(p =>
            {
                var idx = Array.IndexOf(PopularProviderOrder, p.Name.ToLowerInvariant());
                return idx >= 0 ? idx : 999;
            })
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _cachedProviders = sorted;
        return (sorted, sourceUsed);
    }

    public async Task<string> CreateOrUpdateRemoteHybridAsync(
        AppManagerConfig config,
        CreateOrUpdateRemoteRequest req,
        CancellationToken ct = default)
    {
        var remoteName = req.Name.Trim().TrimEnd(':');
        if (!IsValidRemoteName(remoteName))
            throw new ArgumentException("Nome de remote inválido. Use letras, números, '-', '_' ou '.' sem ':'.");

        if (!req.IsUpdate && string.IsNullOrWhiteSpace(req.Type))
            throw new ArgumentException("Selecione o tipo de provedor (ex: drive, s3, sftp, local).");

        var cleanParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, val) in req.Parameters)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(val))
                continue;
            if (val == MaskedSecretPlaceholder)
                continue;
            cleanParams[key.Trim()] = val.Trim();
        }

        var effectiveAuthMode = req.TriggerOAuthBrowser && req.AuthMode == RemoteAuthMode.KeyOrCredentials
            ? RemoteAuthMode.OAuthBrowserLink
            : req.AuthMode;

        if (effectiveAuthMode == RemoteAuthMode.ManualTokenJson)
        {
            var tokenRaw = req.ManualTokenJson?.Trim();
            if (string.IsNullOrWhiteSpace(tokenRaw))
            {
                if (!req.IsUpdate)
                    throw new ArgumentException("Cole o Token JSON no campo dedicado para autenticar via Token Manual.");
            }
            else
            {
                try
                {
                    using var _ = JsonDocument.Parse(tokenRaw);
                }
                catch (JsonException ex)
                {
                    throw new ArgumentException($"O Token JSON informado não é um JSON válido: {ex.Message}");
                }
                cleanParams["token"] = tokenRaw;
            }
        }

        if (!req.IsUpdate && effectiveAuthMode == RemoteAuthMode.OAuthBrowserLink)
        {
            var args = new List<string> { "config", "create", remoteName, req.Type.Trim() };
            foreach (var (k, v) in cleanParams)
            {
                args.Add($"{k}={v}");
            }
            if (req.ObscurePasswords)
                args.Add("--obscure");

            using var oauthCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            oauthCts.CancelAfter(TimeSpan.FromMinutes(3));

            _log.Info($"Iniciando criação de remote '{remoteName}' ({req.Type}) via Link (CLI + OAuth no navegador)...");
            var (exitCode, _, stdErr) = await RcloneCliRunner.RunSimpleCommandAsync(
                config.RcloneExecutablePath,
                config.RcloneConfigPath,
                config.WorkingDirectory,
                args,
                oauthCts.Token,
                closeStdInImmediately: true);

            if (exitCode != 0)
                throw new InvalidOperationException($"Falha ao criar remote '{remoteName}': {stdErr}");

            return $"Remote '{remoteName}:' criado e autenticado via Link (OAuth no navegador).";
        }

        var usedStrategy = string.Empty;
        if (IsRcDaemonRunning)
        {
            try
            {
                using var rc = new RcloneRcClient(_rcdPort, TimeSpan.FromSeconds(15));
                if (req.IsUpdate)
                {
                    await rc.UpdateRemoteAsync(remoteName, cleanParams, req.ObscurePasswords, ct);
                    usedStrategy = $"RC API (/config/update, porta {_rcdPort})";
                }
                else
                {
                    await rc.CreateRemoteAsync(remoteName, req.Type.Trim(), cleanParams, req.ObscurePasswords, ct);
                    usedStrategy = $"RC API (/config/create, porta {_rcdPort})";
                }
            }
            catch (Exception ex)
            {
                _log.Warn($"Operação /config/{(req.IsUpdate ? "update" : "create")} via RC falhou ({ex.Message}); alternando para CLI.");
            }
        }

        if (string.IsNullOrEmpty(usedStrategy))
        {
            var args = req.IsUpdate
                ? new List<string> { "config", "update", remoteName }
                : new List<string> { "config", "create", remoteName, req.Type.Trim() };

            foreach (var (k, v) in cleanParams)
            {
                args.Add($"{k}={v}");
            }
            if (req.ObscurePasswords)
                args.Add("--obscure");
            args.Add("--non-interactive");

            var (exitCode, _, stdErr) = await RcloneCliRunner.RunSimpleCommandAsync(
                config.RcloneExecutablePath,
                config.RcloneConfigPath,
                config.WorkingDirectory,
                args,
                ct,
                closeStdInImmediately: true);

            if (exitCode != 0)
                throw new InvalidOperationException($"rclone config {(req.IsUpdate ? "update" : "create")} falhou: {stdErr}");

            usedStrategy = $"CLI (rclone config {(req.IsUpdate ? "update" : "create")})";
        }

        if (req.IsUpdate && effectiveAuthMode == RemoteAuthMode.OAuthBrowserLink)
        {
            var oauthMsg = await ReconnectRemoteOAuthAsync(config, remoteName, ct);
            return $"{usedStrategy} + {oauthMsg}";
        }

        var modeDesc = effectiveAuthMode == RemoteAuthMode.ManualTokenJson
            ? "Token JSON Manual"
            : "Chave / Credenciais";
        _log.Info($"Remote '{remoteName}' salvo ({modeDesc}) via {usedStrategy}.");
        return $"Remote '{remoteName}:' salvo ({modeDesc}) via {usedStrategy}.";
    }

    public async Task<string> ReconnectRemoteOAuthAsync(
        AppManagerConfig config,
        string remoteName,
        CancellationToken ct = default)
    {
        var cleanName = remoteName.Trim().TrimEnd(':');
        if (!IsValidRemoteName(cleanName))
            throw new ArgumentException("Nome de remote inválido.");

        using var oauthCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        oauthCts.CancelAfter(TimeSpan.FromMinutes(3));

        _log.Info($"Abrindo reconexão OAuth no navegador para '{cleanName}:'...");
        var (exitCode, _, stdErr) = await RcloneCliRunner.RunSimpleCommandAsync(
            config.RcloneExecutablePath,
            config.RcloneConfigPath,
            config.WorkingDirectory,
            new[] { "config", "reconnect", $"{cleanName}:" },
            oauthCts.Token,
            closeStdInImmediately: true);

        if (exitCode != 0)
            throw new InvalidOperationException($"rclone config reconnect falhou: {stdErr}");

        _log.Info($"Remote '{cleanName}:' reautenticado com sucesso.");
        return $"Reautenticação OAuth concluída para '{cleanName}:'.";
    }

    public async Task<string> DeleteRemoteHybridAsync(
        AppManagerConfig config,
        string remoteName,
        CancellationToken ct = default)
    {
        var cleanName = remoteName.Trim().TrimEnd(':');
        if (!IsValidRemoteName(cleanName))
            throw new ArgumentException("Nome de remote inválido.");

        if (IsRcDaemonRunning)
        {
            try
            {
                using var rc = new RcloneRcClient(_rcdPort);
                await rc.DeleteRemoteAsync(cleanName, ct);
                _log.Info($"Remote '{cleanName}' excluído via RC API.");
                return $"Remote '{cleanName}:' excluído via RC API (/config/delete).";
            }
            catch (Exception ex)
            {
                _log.Warn($"Exclusão via RC falhou ({ex.Message}); alternando para CLI.");
            }
        }

        var (exitCode, _, stdErr) = await RcloneCliRunner.RunSimpleCommandAsync(
            config.RcloneExecutablePath,
            config.RcloneConfigPath,
            config.WorkingDirectory,
            new[] { "config", "delete", cleanName },
            ct);

        if (exitCode != 0)
            throw new InvalidOperationException($"rclone config delete falhou: {stdErr}");

        _log.Info($"Remote '{cleanName}' excluído via CLI.");
        return $"Remote '{cleanName}:' excluído via CLI (rclone config delete).";
    }

    public async Task<RemoteTestResult> TestRemoteConnectionAsync(
        AppManagerConfig config,
        string remoteName,
        CancellationToken ct = default)
    {
        var cleanName = remoteName.Trim().TrimEnd(':');
        var remoteFs = $"{cleanName}:";

        using var testCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        testCts.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            var (exitCode, stdOut, stdErr) = await RcloneCliRunner.RunSimpleCommandAsync(
                config.RcloneExecutablePath,
                config.RcloneConfigPath,
                config.WorkingDirectory,
                new[] { "lsd", remoteFs, "--max-depth", "1" },
                testCts.Token);

            if (exitCode != 0)
            {
                return new RemoteTestResult
                {
                    Success = false,
                    Message = string.IsNullOrWhiteSpace(stdErr) ? $"Falha com código {exitCode}." : stdErr.Trim()
                };
            }

            var lines = stdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
            RcloneAboutInfo? about = null;
            try { about = await GetRemoteAboutAsync(config, remoteFs, testCts.Token); } catch { }

            return new RemoteTestResult
            {
                Success = true,
                TopLevelEntriesCount = lines,
                About = about,
                Message = $"Conexão com '{remoteFs}' OK ({lines} pasta(s) na raiz)."
            };
        }
        catch (OperationCanceledException)
        {
            return new RemoteTestResult
            {
                Success = false,
                Message = $"Tempo limite excedido ao conectar em '{remoteFs}'."
            };
        }
    }

    private static bool IsSensitiveParameter(string key) => SensitiveKeys.Contains(key);

    private static bool IsValidRemoteName(string name)
    {
        return !string.IsNullOrWhiteSpace(name) &&
               !name.Contains(':') &&
               name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ' ');
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
