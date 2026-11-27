using System.Text.Json.Serialization;

namespace RcloneManager.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RcloneOperation
{
    Sync,
    Copy,
    Move,
    Check
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FilterRuleMode
{
    AutoDetect,
    FilterFrom,
    ExcludeFrom
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ExecutionStrategy
{
    HybridAuto,
    CliWithRcMonitoring,
    RcDaemonApi,
    PureCli
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScheduleType
{
    IntervalMinutes,
    DailyAtTime,
    WeeklyDays,
    OnServiceStart
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobRunStatus
{
    Idle,
    Running,
    Success,
    Failed,
    Cancelled
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BackgroundServiceMode
{
    UserStartup,
    WindowsService,
    ManualOnly
}

public sealed class JobSchedule
{
    public bool IsScheduled { get; set; } = true;
    public ScheduleType ScheduleType { get; set; } = ScheduleType.IntervalMinutes;
    public int IntervalMinutes { get; set; } = 60;
    public string DailyTime { get; set; } = "14:00";
    public List<DayOfWeek> DaysOfWeek { get; set; } = new();
    public bool RetryOnFailure { get; set; } = true;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMinutes { get; set; } = 5;
}

public sealed class SyncJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Nova Tarefa Rclone";
    public bool Enabled { get; set; } = true;
    public RcloneOperation Operation { get; set; } = RcloneOperation.Sync;
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;

    public string FilterFilePath { get; set; } = string.Empty;
    public FilterRuleMode FilterMode { get; set; } = FilterRuleMode.AutoDetect;
    public bool EnableExcludedGraceRetention { get; set; } = true;
    public int ExcludedRetentionDays { get; set; } = 30;

    public string MaxSize { get; set; } = string.Empty;
    public string BandwidthLimit { get; set; } = string.Empty;
    public bool SkipLinks { get; set; } = true;
    public bool FastList { get; set; } = true;
    public bool CopyLinks { get; set; }
    public bool DryRun { get; set; }
    public string LogFilePath { get; set; } = "rclone_backup.log";
    public string ExtraFlags { get; set; } = string.Empty;

    public ExecutionStrategy Strategy { get; set; } = ExecutionStrategy.HybridAuto;
    public JobSchedule Schedule { get; set; } = new();

    public DateTime? LastRunAt { get; set; }
    public DateTime? NextRunAt { get; set; }
    public JobRunStatus LastStatus { get; set; } = JobRunStatus.Idle;
    public string LastMessage { get; set; } = "Aguardando execução";
    public double? LastDurationSeconds { get; set; }
}
