namespace RcloneManager.Core.Models;

public static class ConfigCloningExtensions
{
    public static JobSchedule Clone(this JobSchedule? source)
    {
        if (source == null)
            return new JobSchedule();

        return new JobSchedule
        {
            IsScheduled = source.IsScheduled,
            ScheduleType = source.ScheduleType,
            IntervalMinutes = source.IntervalMinutes,
            DailyTime = source.DailyTime,
            DaysOfWeek = source.DaysOfWeek?.ToList() ?? new List<DayOfWeek>(),
            RetryOnFailure = source.RetryOnFailure,
            MaxRetries = source.MaxRetries,
            RetryDelayMinutes = source.RetryDelayMinutes
        };
    }

    public static SyncJob Clone(this SyncJob source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new SyncJob
        {
            Id = source.Id,
            Name = source.Name,
            Enabled = source.Enabled,
            Operation = source.Operation,
            SourcePath = source.SourcePath,
            DestinationPath = source.DestinationPath,
            FilterFilePath = source.FilterFilePath,
            FilterMode = source.FilterMode,
            MaxSize = source.MaxSize,
            BandwidthLimit = source.BandwidthLimit,
            SkipLinks = source.SkipLinks,
            FastList = source.FastList,
            CopyLinks = source.CopyLinks,
            DryRun = source.DryRun,
            LogFilePath = source.LogFilePath,
            ExtraFlags = source.ExtraFlags,
            Strategy = source.Strategy,
            Schedule = source.Schedule.Clone(),
            LastRunAt = source.LastRunAt,
            NextRunAt = source.NextRunAt,
            LastStatus = source.LastStatus,
            LastMessage = source.LastMessage,
            LastDurationSeconds = source.LastDurationSeconds
        };
    }

    public static AppManagerConfig Clone(this AppManagerConfig source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new AppManagerConfig
        {
            RcloneExecutablePath = source.RcloneExecutablePath,
            RcloneConfigPath = source.RcloneConfigPath,
            WorkingDirectory = source.WorkingDirectory,
            PinnedRcloneVersion = source.PinnedRcloneVersion,
            LockRcloneVersion = source.LockRcloneVersion,
            FirstRunCompleted = source.FirstRunCompleted,
            EncryptionEnabled = source.EncryptionEnabled,
            EncryptionKeyTarget = source.EncryptionKeyTarget,
            ProtectedSecrets = source.ProtectedSecrets != null
                ? new Dictionary<string, string>(source.ProtectedSecrets, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ServiceApiPort = source.ServiceApiPort,
            PersistentRcdPort = source.PersistentRcdPort,
            AutoStartRcdDaemon = source.AutoStartRcdDaemon,
            GlobalBandwidthLimit = source.GlobalBandwidthLimit,
            Jobs = source.Jobs?.Select(j => j.Clone()).ToList() ?? new List<SyncJob>()
        };
    }
}
