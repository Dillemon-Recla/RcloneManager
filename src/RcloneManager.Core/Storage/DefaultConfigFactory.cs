using System.Diagnostics;
using RcloneManager.Core.Engine;
using RcloneManager.Core.Models;
using RcloneManager.Core.Security;

namespace RcloneManager.Core.Storage;

public sealed class DefaultConfigFactory : IDefaultConfigFactory
{
    public static DefaultConfigFactory Shared { get; } = new();

    public AppManagerConfig CreateDefault(string baseDirectory)
    {
        var rcloneExe = WorkspacePathResolver.FindRcloneExecutable(baseDirectory, RcloneReleaseManager.DefaultPinnedVersion);
        var rcloneConf = DetectRcloneConfigPath(rcloneExe);
        var filterFile = WorkspacePathResolver.FindExistingFilterFile(baseDirectory);
        var logFile = Path.Combine(baseDirectory, "rclone_backup.log");

        var config = new AppManagerConfig
        {
            WorkingDirectory = baseDirectory,
            RcloneExecutablePath = rcloneExe,
            RcloneConfigPath = rcloneConf,
            PinnedRcloneVersion = RcloneReleaseManager.DefaultPinnedVersion,
            LockRcloneVersion = true,
            FirstRunCompleted = false,
            EncryptionEnabled = true,
            EncryptionKeyTarget = WindowsCredentialVault.DefaultTargetName,
            ProtectedSecrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ServiceApiPort = 5580,
            PersistentRcdPort = 5572,
            AutoStartRcdDaemon = true,
            GlobalBandwidthLimit = "off",
            Jobs = new List<SyncJob>()
        };

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultSource = !string.IsNullOrWhiteSpace(userProfile) ? userProfile : @"C:\Users\Public";
        var analysis = FilterFileHelper.Analyze(filterFile);

        config.Jobs.Add(new SyncJob
        {
            Id = "backup-pc-gdrive",
            Name = "Backup Usuário -> Google Drive (BackupPC)",
            Enabled = true,
            Operation = RcloneOperation.Sync,
            SourcePath = defaultSource,
            DestinationPath = "gdrive:BackupPC/",
            FilterFilePath = File.Exists(filterFile) ? filterFile : string.Empty,
            FilterMode = FilterRuleMode.AutoDetect,
            MaxSize = analysis.ExtractedMaxSize ?? "50M",
            SkipLinks = true,
            FastList = true,
            CopyLinks = false,
            DryRun = false,
            LogFilePath = logFile,
            ExtraFlags = "--retries 3 --low-level-retries 5",
            Strategy = ExecutionStrategy.HybridAuto,
            Schedule = new JobSchedule
            {
                IsScheduled = true,
                ScheduleType = ScheduleType.IntervalMinutes,
                IntervalMinutes = 120,
                DailyTime = "14:00",
                RetryOnFailure = true,
                MaxRetries = 2,
                RetryDelayMinutes = 5
            },
            NextRunAt = DateTime.Now.AddMinutes(120),
            LastStatus = JobRunStatus.Idle,
            LastMessage = "Tarefa padrão pronta para revisão"
        });

        return config;
    }

    public void EnsurePathsPopulated(AppManagerConfig config, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(config.PinnedRcloneVersion))
            config.PinnedRcloneVersion = RcloneReleaseManager.DefaultPinnedVersion;

        if (string.IsNullOrWhiteSpace(config.EncryptionKeyTarget))
            config.EncryptionKeyTarget = WindowsCredentialVault.DefaultTargetName;

        config.ProtectedSecrets ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        config.Jobs ??= new List<SyncJob>();

        if (string.IsNullOrWhiteSpace(config.WorkingDirectory) || !Directory.Exists(config.WorkingDirectory))
            config.WorkingDirectory = baseDirectory;

        if (string.IsNullOrWhiteSpace(config.RcloneExecutablePath) || !File.Exists(config.RcloneExecutablePath))
            config.RcloneExecutablePath = WorkspacePathResolver.FindRcloneExecutable(baseDirectory, config.PinnedRcloneVersion);

        if (string.IsNullOrWhiteSpace(config.RcloneConfigPath))
            config.RcloneConfigPath = DetectRcloneConfigPath(config.RcloneExecutablePath);
    }

    public string DetectRcloneConfigPath(string rcloneExePath)
    {
        try
        {
            if (File.Exists(rcloneExePath))
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = rcloneExePath,
                        Arguments = "config file",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                process.Start();
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(3000);

                var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.EndsWith(".conf", StringComparison.OrdinalIgnoreCase) && File.Exists(trimmed))
                        return trimmed;
                }
            }
        }
        catch
        {
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var defaultPath = Path.Combine(appData, "rclone", "rclone.conf");
        return File.Exists(defaultPath) ? defaultPath : string.Empty;
    }
}
