using System.Collections.Concurrent;
using System.Globalization;
using RcloneManager.Core.Engine;
using RcloneManager.Core.Logging;
using RcloneManager.Core.Models;
using RcloneManager.Core.Storage;

namespace RcloneManager.Core.Scheduling;

public sealed class JobScheduler
{
    private readonly IAppConfigRepository _store;
    private readonly RcloneHybridEngine _engine;
    private readonly ServiceLogBuffer _log;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private readonly ConcurrentQueue<(string JobId, bool DryRun, ExecutionStrategy? OverrideStrategy)> _manualQueue = new();
    private readonly ConcurrentDictionary<string, int> _retryCounters = new();
    private readonly HashSet<string> _startupTriggeredJobs = new();
    private readonly object _configLock = new();

    private AppManagerConfig _config;
    private bool _startupJobsEvaluated;

    public JobScheduler(IAppConfigRepository store, RcloneHybridEngine engine, ServiceLogBuffer log)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _config = _store.LoadOrCreateDefault();
        InitializeNextRuns();
    }

    public AppManagerConfig GetConfigSnapshot()
    {
        lock (_configLock)
        {
            return _config.Clone();
        }
    }

    public AppManagerConfig UpdateReleaseSettings(string pinnedVersion, bool lockVersion, string? executablePath)
    {
        var normalized = RcloneReleaseManager.NormalizeVersion(pinnedVersion);
        lock (_configLock)
        {
            _config.PinnedRcloneVersion = normalized;
            _config.LockRcloneVersion = lockVersion;
            if (!string.IsNullOrWhiteSpace(executablePath))
                _config.RcloneExecutablePath = executablePath;

            if (string.IsNullOrWhiteSpace(_config.RcloneConfigPath))
                _config.RcloneConfigPath = AppConfigStore.DetectRcloneConfigPath(_config.RcloneExecutablePath);

            _store.Save(_config);
            _engine.InvalidateVersionCache();
            _log.Info($"Configuração de release atualizada: Versão={normalized}, Travada={lockVersion}, Exe='{_config.RcloneExecutablePath}'.");
            return GetConfigSnapshot();
        }
    }

    public void MarkFirstRunCompleted()
    {
        lock (_configLock)
        {
            _config.FirstRunCompleted = true;
            _store.Save(_config);
            _log.Info("Assistente inicial (First-Run Wizard) concluído.");
        }
    }

    public void ReloadFromDisk()
    {
        lock (_configLock)
        {
            _config = _store.LoadOrCreateDefault();
            _retryCounters.Clear();
            _startupTriggeredJobs.Clear();
            _engine.InvalidateVersionCache();
            InitializeNextRuns();
            _log.Info("Configuração do agendador recarregada do disco.");
        }
    }

    public List<SyncJob> GetJobs()
    {
        lock (_configLock)
        {
            return _config.Jobs.Select(CloneJob).ToList();
        }
    }

    public SyncJob UpsertJob(SyncJob job)
    {
        lock (_configLock)
        {
            if (string.IsNullOrWhiteSpace(job.Id))
                job.Id = Guid.NewGuid().ToString("N")[..8];

            var existingIdx = _config.Jobs.FindIndex(j => j.Id == job.Id);
            if (existingIdx >= 0)
            {
                var prev = _config.Jobs[existingIdx];
                job.LastRunAt = prev.LastRunAt;
                job.LastStatus = prev.LastStatus == JobRunStatus.Running ? prev.LastStatus : prev.LastStatus;
                job.LastMessage = prev.LastMessage;
                job.LastDurationSeconds = prev.LastDurationSeconds;
                job.NextRunAt = CalculateNextRunAt(job, DateTime.Now);
                _config.Jobs[existingIdx] = job;
                _log.Info($"Tarefa '{job.Name}' ({job.Id}) atualizada.");
            }
            else
            {
                job.NextRunAt = CalculateNextRunAt(job, DateTime.Now);
                _config.Jobs.Add(job);
                _log.Info($"Nova tarefa '{job.Name}' ({job.Id}) criada.");
            }

            _store.Save(_config);
            return CloneJob(job);
        }
    }

    public bool DeleteJob(string jobId)
    {
        lock (_configLock)
        {
            var removed = _config.Jobs.RemoveAll(j => j.Id == jobId) > 0;
            if (removed)
            {
                _store.Save(_config);
                _log.Info($"Tarefa '{jobId}' removida.");
            }
            return removed;
        }
    }

    public bool EnqueueManualRun(string jobId, bool dryRun = false, ExecutionStrategy? overrideStrategy = null)
    {
        lock (_configLock)
        {
            var job = _config.Jobs.FirstOrDefault(j => j.Id == jobId);
            if (job == null)
                return false;
        }

        _manualQueue.Enqueue((jobId, dryRun, overrideStrategy));
        _log.Info($"Execução manual enfileirada para '{jobId}' (DryRun={dryRun}).");
        return true;
    }

    public bool StopRunningJob(string? jobId = null)
    {
        var active = _engine.GetActiveExecution();
        if (active == null)
            return false;

        if (!string.IsNullOrWhiteSpace(jobId) && active.JobId != jobId)
            return false;

        return _engine.RequestStopActiveJob();
    }

    public async Task<string> UpdateBandwidthLimitAsync(string rate, CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(rate) ? "off" : rate.Trim();
        AppManagerConfig snapshot;
        lock (_configLock)
        {
            _config.GlobalBandwidthLimit = normalized;
            _store.Save(_config);
            snapshot = GetConfigSnapshot();
        }

        return await _engine.SetLiveBandwidthLimitAsync(snapshot, normalized, ct);
    }

    public async Task TickAsync(CancellationToken ct)
    {
        // 1. Verifica execuções manuais na fila
        while (_manualQueue.TryDequeue(out var req))
        {
            await RunSingleJobSafeAsync(req.JobId, req.DryRun, req.OverrideStrategy, isScheduledRun: false, ct);
        }

        // 2. Avalia jobs OnServiceStart apenas uma vez no arranque
        if (!_startupJobsEvaluated)
        {
            _startupJobsEvaluated = true;
            List<string> startupIds;
            lock (_configLock)
            {
                startupIds = _config.Jobs
                    .Where(j => j.Enabled && j.Schedule.IsScheduled && j.Schedule.ScheduleType == ScheduleType.OnServiceStart)
                    .Select(j => j.Id)
                    .ToList();
            }

            foreach (var id in startupIds)
            {
                if (_startupTriggeredJobs.Add(id))
                {
                    await RunSingleJobSafeAsync(id, false, null, isScheduledRun: true, ct);
                }
            }
        }

        // 3. Verifica tarefas agendadas cujo horário venceu
        var now = DateTime.Now;
        List<string> dueJobIds;
        lock (_configLock)
        {
            dueJobIds = _config.Jobs
                .Where(j => j.Enabled &&
                            j.Schedule.IsScheduled &&
                            j.Schedule.ScheduleType != ScheduleType.OnServiceStart &&
                            j.NextRunAt.HasValue &&
                            j.NextRunAt.Value <= now)
                .Select(j => j.Id)
                .ToList();
        }

        foreach (var id in dueJobIds)
        {
            await RunSingleJobSafeAsync(id, false, null, isScheduledRun: true, ct);
        }
    }

    private async Task RunSingleJobSafeAsync(
        string jobId,
        bool dryRun,
        ExecutionStrategy? overrideStrategy,
        bool isScheduledRun,
        CancellationToken ct)
    {
        await _executionLock.WaitAsync(ct);
        try
        {
            SyncJob? jobSnapshot;
            AppManagerConfig configSnapshot;
            lock (_configLock)
            {
                var job = _config.Jobs.FirstOrDefault(j => j.Id == jobId);
                if (job == null)
                    return;

                job.LastStatus = JobRunStatus.Running;
                job.LastMessage = dryRun ? "Executando simulação (Dry-Run)..." : "Executando sincronização...";
                _store.Save(_config);

                jobSnapshot = CloneJob(job);
                configSnapshot = GetConfigSnapshot();
            }

            var (success, cancelled, message, duration) =
                await _engine.ExecuteJobAsync(jobSnapshot, configSnapshot, dryRun, overrideStrategy, ct);

            lock (_configLock)
            {
                var job = _config.Jobs.FirstOrDefault(j => j.Id == jobId);
                if (job == null)
                    return;

                var now = DateTime.Now;
                job.LastRunAt = now;
                job.LastDurationSeconds = duration;
                job.LastMessage = message;

                if (cancelled)
                {
                    job.LastStatus = JobRunStatus.Cancelled;
                    _retryCounters.TryRemove(job.Id, out _);
                    job.NextRunAt = CalculateNextRunAt(job, now);
                }
                else if (success)
                {
                    job.LastStatus = JobRunStatus.Success;
                    _retryCounters.TryRemove(job.Id, out _);
                    job.NextRunAt = CalculateNextRunAt(job, now);
                }
                else
                {
                    job.LastStatus = JobRunStatus.Failed;
                    if (isScheduledRun && job.Schedule.RetryOnFailure && job.Schedule.MaxRetries > 0)
                    {
                        var attempt = _retryCounters.AddOrUpdate(job.Id, 1, (_, cur) => cur + 1);
                        if (attempt <= job.Schedule.MaxRetries)
                        {
                            var delay = Math.Max(1, job.Schedule.RetryDelayMinutes);
                            job.NextRunAt = now.AddMinutes(delay);
                            job.LastMessage = $"{message} (Retentativa {attempt}/{job.Schedule.MaxRetries} em {delay} min)";
                            _log.Warn($"Agendada retentativa {attempt}/{job.Schedule.MaxRetries} para '{job.Name}' às {job.NextRunAt:HH:mm:ss}.");
                        }
                        else
                        {
                            _retryCounters.TryRemove(job.Id, out _);
                            job.NextRunAt = CalculateNextRunAt(job, now);
                        }
                    }
                    else
                    {
                        job.NextRunAt = CalculateNextRunAt(job, now);
                    }
                }

                _store.Save(_config);
            }
        }
        finally
        {
            _executionLock.Release();
        }
    }

    private void InitializeNextRuns()
    {
        lock (_configLock)
        {
            var now = DateTime.Now;
            var changed = false;
            foreach (var job in _config.Jobs)
            {
                if (job.LastStatus == JobRunStatus.Running)
                {
                    job.LastStatus = JobRunStatus.Idle;
                    job.LastMessage = "Serviço reiniciado; pronto para execução";
                    changed = true;
                }

                if (job.Enabled && job.Schedule.IsScheduled && (!job.NextRunAt.HasValue || job.NextRunAt.Value < now))
                {
                    job.NextRunAt = CalculateNextRunAt(job, now);
                    changed = true;
                }
            }

            if (changed)
                _store.Save(_config);
        }
    }

    public static DateTime? CalculateNextRunAt(SyncJob job, DateTime referenceNow)
    {
        if (!job.Enabled || !job.Schedule.IsScheduled)
            return null;

        switch (job.Schedule.ScheduleType)
        {
            case ScheduleType.IntervalMinutes:
                var mins = Math.Max(1, job.Schedule.IntervalMinutes);
                return referenceNow.AddMinutes(mins);

            case ScheduleType.DailyAtTime:
                var dailyTime = ParseTimeOrDefault(job.Schedule.DailyTime);
                var todayCandidate = referenceNow.Date + dailyTime;
                return todayCandidate > referenceNow ? todayCandidate : todayCandidate.AddDays(1);

            case ScheduleType.WeeklyDays:
                var weeklyTime = ParseTimeOrDefault(job.Schedule.DailyTime);
                var allowedDays = job.Schedule.DaysOfWeek is { Count: > 0 }
                    ? job.Schedule.DaysOfWeek.ToHashSet()
                    : Enum.GetValues<DayOfWeek>().ToHashSet();

                for (var offset = 0; offset <= 7; offset++)
                {
                    var candidate = referenceNow.Date.AddDays(offset) + weeklyTime;
                    if (candidate > referenceNow && allowedDays.Contains(candidate.DayOfWeek))
                        return candidate;
                }
                return referenceNow.AddDays(1);

            case ScheduleType.OnServiceStart:
                return null;

            default:
                return referenceNow.AddHours(1);
        }
    }

    private static TimeSpan ParseTimeOrDefault(string? raw)
    {
        if (!string.IsNullOrWhiteSpace(raw) &&
            TimeSpan.TryParseExact(raw.Trim(), new[] { @"hh\:mm", @"h\:mm", @"hh\:mm\:ss" }, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }
        return new TimeSpan(14, 0, 0);
    }

    private static SyncJob CloneJob(SyncJob src) => src.Clone();
}
