using RcloneManager.Core.Engine;

namespace RcloneManager.Core.Models;

public sealed class AppManagerConfig
{
    public string RcloneExecutablePath { get; set; } = string.Empty;
    public string RcloneConfigPath { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string PinnedRcloneVersion { get; set; } = RcloneReleaseManager.DefaultPinnedVersion;
    public bool LockRcloneVersion { get; set; } = true;
    public int ServiceApiPort { get; set; } = 5580;
    public int PersistentRcdPort { get; set; } = 5572;
    public bool AutoStartRcdDaemon { get; set; } = true;
    public string GlobalBandwidthLimit { get; set; } = "off";
    public List<SyncJob> Jobs { get; set; } = new();
}

public sealed class ActiveJobExecutionInfo
{
    public string JobId { get; set; } = string.Empty;
    public string JobName { get; set; } = string.Empty;
    public ExecutionStrategy ResolvedStrategy { get; set; }
    public string StrategyReason { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public int? ProcessId { get; set; }
    public int? ActiveRcPort { get; set; }
    public int? RcJobId { get; set; }
    public bool IsDryRun { get; set; }
}

public sealed class ServiceStatusDto
{
    public bool ServiceRunning { get; set; } = true;
    public DateTime ServiceStartedAt { get; set; }
    public DateTime ServerTimeUtc { get; set; } = DateTime.UtcNow;
    public string RcloneVersion { get; set; } = string.Empty;
    public string PinnedRcloneVersion { get; set; } = RcloneReleaseManager.DefaultPinnedVersion;
    public bool LockRcloneVersion { get; set; } = true;
    public bool IsRcloneVersionValid { get; set; }
    public string RcloneExecutablePath { get; set; } = string.Empty;
    public string RcloneConfigPath { get; set; } = string.Empty;
    public bool RcDaemonRunning { get; set; }
    public int RcDaemonPort { get; set; }
    public string CurrentBandwidthLimit { get; set; } = "off";
    public bool StartupWithWindowsEnabled { get; set; }
    public ActiveJobExecutionInfo? ActiveJob { get; set; }
    public RcloneStats? LiveStats { get; set; }
    public List<SyncJob> Jobs { get; set; } = new();
    public List<string> RecentServiceLogs { get; set; } = new();
}

public sealed class RunJobRequest
{
    public bool DryRun { get; set; }
    public ExecutionStrategy? OverrideStrategy { get; set; }
}

public sealed class BandwidthUpdateRequest
{
    public string Rate { get; set; } = "off";
}

public sealed class FilterFilePayload
{
    public string FilePath { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public sealed class UpdateReleaseConfigRequest
{
    public string PinnedRcloneVersion { get; set; } = RcloneReleaseManager.DefaultPinnedVersion;
    public bool LockRcloneVersion { get; set; } = true;
    public string? RcloneExecutablePath { get; set; }
}
