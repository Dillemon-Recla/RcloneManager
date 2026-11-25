using System.Diagnostics;
using System.Text.Json;
using RcloneManager.Core.Engine;
using RcloneManager.Core.Models;

namespace RcloneManager.Core.Storage;

public sealed class AppConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _syncLock = new();

    public string ConfigFilePath { get; }
    public string BaseDirectory { get; }

    public AppConfigStore(string baseDirectory)
    {
        BaseDirectory = baseDirectory;
        ConfigFilePath = Path.Combine(baseDirectory, "rclone_manager_config.json");
    }

    public AppManagerConfig LoadOrCreateDefault()
    {
        lock (_syncLock)
        {
            if (File.Exists(ConfigFilePath))
            {
                try
                {
                    var json = File.ReadAllText(ConfigFilePath);
                    var loaded = JsonSerializer.Deserialize<AppManagerConfig>(json, JsonOptions);
                    if (loaded != null)
                    {
                        EnsurePathsPopulated(loaded);
                        return loaded;
                    }
                }
                catch
                {
                    // Se o JSON estiver corrompido, recria backup e gera novo padrão.
                    var brokenBackup = ConfigFilePath + $".corrupt.{DateTime.Now:yyyyMMddHHmmss}";
                    File.Copy(ConfigFilePath, brokenBackup, overwrite: true);
                }
            }

            var created = CreateDefaultConfig();
            SaveInternal(created);
            return created;
        }
    }

    public void Save(AppManagerConfig config)
    {
        lock (_syncLock)
        {
            SaveInternal(config);
        }
    }

    private void SaveInternal(AppManagerConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var tempFile = ConfigFilePath + ".tmp";
        File.WriteAllText(tempFile, json);
        File.Move(tempFile, ConfigFilePath, overwrite: true);
    }

    private void EnsurePathsPopulated(AppManagerConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.WorkingDirectory) || !Directory.Exists(config.WorkingDirectory))
            config.WorkingDirectory = BaseDirectory;

        if (string.IsNullOrWhiteSpace(config.RcloneExecutablePath) || !File.Exists(config.RcloneExecutablePath))
            config.RcloneExecutablePath = Path.Combine(BaseDirectory, "rclone.exe");

        if (string.IsNullOrWhiteSpace(config.RcloneConfigPath))
            config.RcloneConfigPath = DetectRcloneConfigPath(config.RcloneExecutablePath);
    }

    private AppManagerConfig CreateDefaultConfig()
    {
        var rcloneExe = Path.Combine(BaseDirectory, "rclone.exe");
        var rcloneConf = DetectRcloneConfigPath(rcloneExe);
        var filterFile = Path.Combine(BaseDirectory, "exclude_user_folder.txt");
        var logFile = Path.Combine(BaseDirectory, "rclone_backup.log");

        var config = new AppManagerConfig
        {
            WorkingDirectory = BaseDirectory,
            RcloneExecutablePath = rcloneExe,
            RcloneConfigPath = rcloneConf,
            ServiceApiPort = 5580,
            PersistentRcdPort = 5572,
            AutoStartRcdDaemon = true,
            GlobalBandwidthLimit = "off",
            Jobs = new List<SyncJob>()
        };

        // Importa automaticamente o cenário existente do usuário (script.bat + exclude_user_folder.txt)
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultSource = !string.IsNullOrWhiteSpace(userProfile) ? userProfile : @"C:\Users\dille";
        var analysis = FilterFileHelper.Analyze(filterFile);

        var importedJob = new SyncJob
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
            LastMessage = "Importado de script.bat (pronto para executar)"
        };

        config.Jobs.Add(importedJob);
        return config;
    }

    public static string DetectRcloneConfigPath(string rcloneExePath)
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
            // Fallback abaixo
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var defaultPath = Path.Combine(appData, "rclone", "rclone.conf");
        return File.Exists(defaultPath) ? defaultPath : string.Empty;
    }
}
