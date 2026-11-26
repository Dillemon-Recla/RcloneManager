using RcloneManager.Core.Engine;
using RcloneManager.Core.Security;

namespace RcloneManager.Core.Models;

public sealed class AppManagerConfig
{
    public string RcloneExecutablePath { get; set; } = string.Empty;
    public string RcloneConfigPath { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string PinnedRcloneVersion { get; set; } = RcloneReleaseManager.DefaultPinnedVersion;
    public bool LockRcloneVersion { get; set; } = true;
    public bool FirstRunCompleted { get; set; }
    public bool EncryptionEnabled { get; set; } = true;
    public string EncryptionKeyTarget { get; set; } = WindowsCredentialVault.DefaultTargetName;
    public Dictionary<string, string> ProtectedSecrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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
    public bool FirstRunCompleted { get; set; }
    public bool EncryptionKeyActive { get; set; }
    public string EncryptionKeyTarget { get; set; } = WindowsCredentialVault.DefaultTargetName;
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

public sealed class ResetConfigRequest
{
    public bool DeleteCredentialKey { get; set; } = true;
    public bool DeleteLogsAndRuntime { get; set; } = true;
    public bool DeleteRcloneConf { get; set; }
}

public sealed class ExportedRemoteBlueprint
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public RemoteAuthMode SuggestedAuthMode { get; set; } = RemoteAuthMode.KeyOrCredentials;
    public Dictionary<string, string> NonSensitiveParameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool RequiresAuthentication { get; set; } = true;
}

public sealed class ExportedConfigBundle
{
    public int SchemaVersion { get; set; } = 1;
    public string Format { get; set; } = "RcloneManager.PortableConfig";
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
    public string PinnedRcloneVersion { get; set; } = RcloneReleaseManager.DefaultPinnedVersion;
    public bool LockRcloneVersion { get; set; } = true;
    public int ServiceApiPort { get; set; } = 5580;
    public int PersistentRcdPort { get; set; } = 5572;
    public bool AutoStartRcdDaemon { get; set; } = true;
    public string GlobalBandwidthLimit { get; set; } = "off";
    public List<ExportedRemoteBlueprint> Remotes { get; set; } = new();
    public List<SyncJob> Jobs { get; set; } = new();
}
