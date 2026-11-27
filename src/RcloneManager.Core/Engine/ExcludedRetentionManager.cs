using System.Text;
using System.Text.Json;
using RcloneManager.Core.Logging;
using RcloneManager.Core.Models;

namespace RcloneManager.Core.Engine;

internal sealed class ExcludedRetentionJobRecord
{
    public DateTime? LastScannedUtc { get; set; }
    public Dictionary<string, DateTime> FirstExcludedUtcByPath { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class ExcludedRetentionStateFile
{
    public Dictionary<string, ExcludedRetentionJobRecord> Jobs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Rastreia localmente arquivos já presentes no destino que entraram nas regras de exclusão,
/// aplicando o prazo de carência (ex: 30 dias) antes de deletá-los sem mover arquivos na nuvem,
/// além de permitir o expurgo imediato sob demanda.
/// </summary>
public sealed class ExcludedRetentionManager
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _workingDirectory;
    private readonly ServiceLogBuffer _log;
    private readonly object _fileLock = new();

    public ExcludedRetentionManager(string workingDirectory, ServiceLogBuffer log)
    {
        _workingDirectory = workingDirectory;
        _log = log;
    }

    private string RuntimeDir => Path.Combine(_workingDirectory, ".rclone-manager-runtime");
    private string StateFilePath => Path.Combine(RuntimeDir, "excluded_retention.json");

    public bool ShouldAutoScanAfterSync(SyncJob job, TimeSpan minInterval)
    {
        if (!job.EnableExcludedGraceRetention || string.IsNullOrWhiteSpace(job.FilterFilePath))
            return false;

        lock (_fileLock)
        {
            var state = LoadStateUnsafe();
            if (!state.Jobs.TryGetValue(job.Id, out var rec) || !rec.LastScannedUtc.HasValue)
                return true;

            return DateTime.UtcNow - rec.LastScannedUtc.Value >= minInterval;
        }
    }

    public ExcludedRetentionStatusDto GetCachedStatus(SyncJob job)
    {
        lock (_fileLock)
        {
            var state = LoadStateUnsafe();
            state.Jobs.TryGetValue(job.Id, out var rec);
            return BuildStatusDto(job, rec, expiredPurgedNowCount: 0);
        }
    }

    public async Task<ExcludedRetentionStatusDto> ScanAndEvaluateAsync(
        SyncJob job,
        AppManagerConfig config,
        bool autoPurgeExpired,
        CancellationToken ct = default)
    {
        var workDir = Directory.Exists(config.WorkingDirectory) ? config.WorkingDirectory : _workingDirectory;
        var resolvedFilter = ResolveFilterPath(job.FilterFilePath, workDir);

        if (string.IsNullOrWhiteSpace(resolvedFilter) || !File.Exists(resolvedFilter))
        {
            return new ExcludedRetentionStatusDto
            {
                JobId = job.Id,
                JobName = job.Name,
                DestinationPath = job.DestinationPath,
                RetentionEnabled = job.EnableExcludedGraceRetention,
                GraceDays = Math.Max(1, job.ExcludedRetentionDays),
                StatusSummary = "Esta tarefa não possui um arquivo de filtro configurado."
            };
        }

        var invertedContent = FilterFileHelper.BuildInvertedExcludedOnlyFilterContent(resolvedFilter, job.FilterMode);
        if (string.IsNullOrWhiteSpace(invertedContent))
        {
            return new ExcludedRetentionStatusDto
            {
                JobId = job.Id,
                JobName = job.Name,
                DestinationPath = job.DestinationPath,
                RetentionEnabled = job.EnableExcludedGraceRetention,
                GraceDays = Math.Max(1, job.ExcludedRetentionDays),
                StatusSummary = "Nenhuma regra de exclusão ativa encontrada no arquivo de filtro."
            };
        }

        Directory.CreateDirectory(RuntimeDir);
        var invertedFilterPath = Path.Combine(RuntimeDir, $"{SanitizeFileToken(job.Id)}.excluded-only.filter");
        await File.WriteAllTextAsync(invertedFilterPath, invertedContent, new UTF8Encoding(false), ct);

        var remoteExcludedFiles = await ListRemoteExcludedFilesAsync(job, config, workDir, invertedFilterPath, ct);
        var nowUtc = DateTime.UtcNow;
        var graceDays = Math.Max(1, job.ExcludedRetentionDays);
        var expiredPaths = new List<string>();

        lock (_fileLock)
        {
            var state = LoadStateUnsafe();
            if (!state.Jobs.TryGetValue(job.Id, out var rec))
            {
                rec = new ExcludedRetentionJobRecord();
                state.Jobs[job.Id] = rec;
            }

            var updatedMap = new Dictionary<string, DateTime>(remoteExcludedFiles.Count, StringComparer.Ordinal);
            foreach (var relPath in remoteExcludedFiles)
            {
                if (!rec.FirstExcludedUtcByPath.TryGetValue(relPath, out var firstSeen))
                    firstSeen = nowUtc;

                updatedMap[relPath] = firstSeen;

                if (nowUtc >= firstSeen.AddDays(graceDays))
                    expiredPaths.Add(relPath);
            }

            rec.FirstExcludedUtcByPath = updatedMap;
            rec.LastScannedUtc = nowUtc;
            SaveStateUnsafe(state);
        }

        var purgedCount = 0;
        if (autoPurgeExpired && job.EnableExcludedGraceRetention && expiredPaths.Count > 0)
        {
            var expiredListFile = Path.Combine(RuntimeDir, $"{SanitizeFileToken(job.Id)}.expired-delete.files");
            await File.WriteAllLinesAsync(expiredListFile, expiredPaths, new UTF8Encoding(false), ct);

            var deleteArgs = new List<string>
            {
                "delete",
                job.DestinationPath,
                "--files-from-raw",
                expiredListFile,
                "--fast-list",
                "--tpslimit", "6",
                "--tpslimit-burst", "6"
            };

            var (exitCode, _, stdErr) = await RcloneCliRunner.RunSimpleCommandAsync(
                config.RcloneExecutablePath,
                config.RcloneConfigPath,
                workDir,
                deleteArgs,
                ct);

            if (exitCode == 0)
            {
                purgedCount = expiredPaths.Count;
                await TryRemoveEmptyExcludedDirsAsync(job, config, workDir, invertedFilterPath, ct);

                lock (_fileLock)
                {
                    var state = LoadStateUnsafe();
                    if (state.Jobs.TryGetValue(job.Id, out var rec))
                    {
                        foreach (var p in expiredPaths)
                            rec.FirstExcludedUtcByPath.Remove(p);
                        SaveStateUnsafe(state);
                    }
                }

                _log.Info($"[{job.Name}] Carência de {graceDays} dia(s) vencida: {purgedCount} arquivo(s) excluído(s) foram removidos automaticamente de '{job.DestinationPath}'.");
            }
            else
            {
                _log.Warn($"[{job.Name}] Falha ao remover itens com carência vencida: {stdErr.Trim()}");
            }
        }

        lock (_fileLock)
        {
            var state = LoadStateUnsafe();
            state.Jobs.TryGetValue(job.Id, out var rec);
            return BuildStatusDto(job, rec, purgedCount);
        }
    }

    public async Task<PurgeExcludedResultDto> PurgeAllExcludedNowAsync(
        SyncJob job,
        AppManagerConfig config,
        CancellationToken ct = default)
    {
        var workDir = Directory.Exists(config.WorkingDirectory) ? config.WorkingDirectory : _workingDirectory;
        var resolvedFilter = ResolveFilterPath(job.FilterFilePath, workDir);

        if (string.IsNullOrWhiteSpace(resolvedFilter) || !File.Exists(resolvedFilter))
        {
            return new PurgeExcludedResultDto
            {
                Success = false,
                Message = "Esta tarefa não possui arquivo de filtro configurado para expurgar."
            };
        }

        var invertedContent = FilterFileHelper.BuildInvertedExcludedOnlyFilterContent(resolvedFilter, job.FilterMode);
        if (string.IsNullOrWhiteSpace(invertedContent))
        {
            return new PurgeExcludedResultDto
            {
                Success = false,
                Message = "Nenhuma regra de exclusão encontrada no filtro."
            };
        }

        Directory.CreateDirectory(RuntimeDir);
        var invertedFilterPath = Path.Combine(RuntimeDir, $"{SanitizeFileToken(job.Id)}.excluded-only.filter");
        await File.WriteAllTextAsync(invertedFilterPath, invertedContent, new UTF8Encoding(false), ct);

        var remoteExcludedFiles = await ListRemoteExcludedFilesAsync(job, config, workDir, invertedFilterPath, ct);
        if (remoteExcludedFiles.Count == 0)
        {
            ClearJobRetentionRecord(job.Id);
            return new PurgeExcludedResultDto
            {
                Success = true,
                DeletedFilesCount = 0,
                Message = $"Nenhum item excluído pendente foi encontrado em '{job.DestinationPath}'."
            };
        }

        var topFolders = remoteExcludedFiles
            .Select(ExtractTopFolder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        var deleteArgs = new List<string>
        {
            "delete",
            job.DestinationPath,
            "--filter-from",
            invertedFilterPath,
            "--fast-list",
            "--tpslimit", "6",
            "--tpslimit-burst", "6"
        };

        _log.Info($"[{job.Name}] Expurgando imediatamente {remoteExcludedFiles.Count} arquivo(s) excluído(s) de '{job.DestinationPath}'...");
        var (exitCode, _, stdErr) = await RcloneCliRunner.RunSimpleCommandAsync(
            config.RcloneExecutablePath,
            config.RcloneConfigPath,
            workDir,
            deleteArgs,
            ct);

        if (exitCode != 0)
        {
            var errMsg = string.IsNullOrWhiteSpace(stdErr) ? $"rclone delete retornou código {exitCode}." : stdErr.Trim();
            _log.Error($"[{job.Name}] Falha no expurgo imediato: {errMsg}");
            return new PurgeExcludedResultDto
            {
                Success = false,
                Message = $"Falha ao expurgar itens excluídos: {errMsg}"
            };
        }

        await TryRemoveEmptyExcludedDirsAsync(job, config, workDir, invertedFilterPath, ct);
        ClearJobRetentionRecord(job.Id);

        var foldersLabel = topFolders.Count > 0 ? $" ({string.Join(", ", topFolders)})" : string.Empty;
        var msg = $"Expurgo concluído: {remoteExcludedFiles.Count} arquivo(s) excluído(s) removido(s) de '{job.DestinationPath}'{foldersLabel}.";
        _log.Info($"[{job.Name}] {msg}");

        return new PurgeExcludedResultDto
        {
            Success = true,
            DeletedFilesCount = remoteExcludedFiles.Count,
            AffectedTopFolders = topFolders,
            Message = msg
        };
    }

    private async Task<List<string>> ListRemoteExcludedFilesAsync(
        SyncJob job,
        AppManagerConfig config,
        string workDir,
        string invertedFilterPath,
        CancellationToken ct)
    {
        var args = new List<string>
        {
            "lsf",
            job.DestinationPath,
            "--files-only",
            "-R",
            "--fast-list",
            "--filter-from",
            invertedFilterPath,
            "--tpslimit", "6",
            "--tpslimit-burst", "6"
        };

        var (exitCode, stdOut, stdErr) = await RcloneCliRunner.RunSimpleCommandAsync(
            config.RcloneExecutablePath,
            config.RcloneConfigPath,
            workDir,
            args,
            ct);

        if (exitCode != 0)
        {
            // Se a pasta de destino ainda não existir no remote, retorna vazio sem erro fatal.
            if (stdErr.Contains("directory not found", StringComparison.OrdinalIgnoreCase) ||
                stdErr.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                return new List<string>();
            }

            throw new InvalidOperationException($"Falha ao verificar itens excluídos em '{job.DestinationPath}': {stdErr.Trim()}");
        }

        return stdOut
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static async Task TryRemoveEmptyExcludedDirsAsync(
        SyncJob job,
        AppManagerConfig config,
        string workDir,
        string invertedFilterPath,
        CancellationToken ct)
    {
        try
        {
            var rmdirsArgs = new List<string>
            {
                "rmdirs",
                job.DestinationPath,
                "--filter-from",
                invertedFilterPath,
                "--leave-root",
                "--tpslimit", "6",
                "--tpslimit-burst", "6"
            };

            await RcloneCliRunner.RunSimpleCommandAsync(
                config.RcloneExecutablePath,
                config.RcloneConfigPath,
                workDir,
                rmdirsArgs,
                ct);
        }
        catch
        {
            // Limpeza de pastas vazias é best-effort
        }
    }

    private ExcludedRetentionStatusDto BuildStatusDto(
        SyncJob job,
        ExcludedRetentionJobRecord? rec,
        int expiredPurgedNowCount)
    {
        var graceDays = Math.Max(1, job.ExcludedRetentionDays);
        var nowUtc = DateTime.UtcNow;
        var dto = new ExcludedRetentionStatusDto
        {
            JobId = job.Id,
            JobName = job.Name,
            DestinationPath = job.DestinationPath,
            RetentionEnabled = job.EnableExcludedGraceRetention,
            GraceDays = graceDays,
            LastScannedUtc = rec?.LastScannedUtc,
            TotalTrackedFiles = rec?.FirstExcludedUtcByPath.Count ?? 0,
            ExpiredPurgedNowCount = expiredPurgedNowCount
        };

        if (rec == null || !rec.LastScannedUtc.HasValue)
        {
            dto.StatusSummary = $"Carência automática de {graceDays} dia(s) ativa. Clique em 'Verificar na Nuvem' para inspecionar itens antigos já enviados.";
            return dto;
        }

        if (rec.FirstExcludedUtcByPath.Count == 0)
        {
            dto.StatusSummary = expiredPurgedNowCount > 0
                ? $"Limpo! {expiredPurgedNowCount} arquivo(s) com carência vencida foram removidos."
                : $"Nenhum item excluído pendente no destino (verificado em {rec.LastScannedUtc.Value.ToLocalTime():dd/MM HH:mm}).";
            return dto;
        }

        var groups = rec.FirstExcludedUtcByPath
            .GroupBy(kv => ExtractTopFolder(kv.Key), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var earliest = g.Min(x => x.Value);
                var expires = earliest.AddDays(graceDays);
                var daysLeft = Math.Max(0, (int)Math.Ceiling((expires - nowUtc).TotalDays));
                return new ExcludedFolderSummaryDto
                {
                    FolderName = g.Key,
                    FileCount = g.Count(),
                    FirstDetectedUtc = earliest,
                    ExpiresAtUtc = expires,
                    DaysRemaining = daysLeft
                };
            })
            .OrderByDescending(g => g.FileCount)
            .Take(8)
            .ToList();

        dto.TopFolders = groups;
        var minDays = groups.Min(g => g.DaysRemaining);
        var preview = string.Join(", ", groups.Take(4).Select(g => $"{g.FolderName} ({g.FileCount} arq., {g.DaysRemaining}d)"));

        dto.StatusSummary = job.EnableExcludedGraceRetention
            ? $"Em carência ({graceDays}d): {dto.TotalTrackedFiles} arquivo(s) no destino serão apagados em até {minDays} dia(s) — {preview}."
            : $"{dto.TotalTrackedFiles} arquivo(s) excluído(s) permanecem no destino (remoção automática desativada) — {preview}.";

        return dto;
    }

    private void ClearJobRetentionRecord(string jobId)
    {
        lock (_fileLock)
        {
            var state = LoadStateUnsafe();
            if (state.Jobs.TryGetValue(jobId, out var rec))
            {
                rec.FirstExcludedUtcByPath.Clear();
                rec.LastScannedUtc = DateTime.UtcNow;
            }
            else
            {
                state.Jobs[jobId] = new ExcludedRetentionJobRecord { LastScannedUtc = DateTime.UtcNow };
            }
            SaveStateUnsafe(state);
        }
    }

    private ExcludedRetentionStateFile LoadStateUnsafe()
    {
        try
        {
            if (File.Exists(StateFilePath))
            {
                var json = File.ReadAllText(StateFilePath);
                return JsonSerializer.Deserialize<ExcludedRetentionStateFile>(json, JsonOpts)
                       ?? new ExcludedRetentionStateFile();
            }
        }
        catch
        {
        }

        return new ExcludedRetentionStateFile();
    }

    private void SaveStateUnsafe(ExcludedRetentionStateFile state)
    {
        try
        {
            Directory.CreateDirectory(RuntimeDir);
            var json = JsonSerializer.Serialize(state, JsonOpts);
            File.WriteAllText(StateFilePath, json, new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    private static string ResolveFilterPath(string filterFilePath, string workDir)
    {
        if (string.IsNullOrWhiteSpace(filterFilePath))
            return string.Empty;

        return Path.IsPathRooted(filterFilePath)
            ? filterFilePath
            : Path.Combine(workDir, filterFilePath);
    }

    private static string ExtractTopFolder(string relativePath)
    {
        var slashIdx = relativePath.IndexOf('/');
        return slashIdx > 0 ? relativePath[..slashIdx] : "(raiz)";
    }

    private static string SanitizeFileToken(string input)
    {
        var chars = input.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        return new string(chars);
    }
}
