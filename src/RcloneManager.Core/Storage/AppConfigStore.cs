using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using RcloneManager.Core.Engine;
using RcloneManager.Core.Models;
using RcloneManager.Core.Security;

namespace RcloneManager.Core.Storage;

[SupportedOSPlatform("windows")]
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
                        EnsureEncryptionInitialized(loaded);
                        DecryptSensitiveFieldsInPlace(loaded);
                        EnsurePathsPopulated(loaded);
                        return loaded;
                    }
                }
                catch
                {
                    var brokenBackup = ConfigFilePath + $".corrupt.{DateTime.Now:yyyyMMddHHmmss}";
                    File.Copy(ConfigFilePath, brokenBackup, overwrite: true);
                }
            }

            var created = CreateDefaultConfig();
            EnsureEncryptionInitialized(created);
            SaveInternal(created);
            return created;
        }
    }

    public void Save(AppManagerConfig config)
    {
        lock (_syncLock)
        {
            EnsureEncryptionInitialized(config);
            SaveInternal(config);
        }
    }

    private static readonly HashSet<string> PortableAllowedRemoteParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "scope", "team_drive", "host", "port", "endpoint", "region",
        "url", "provider", "nounc", "root_folder_id", "vendor", "domain"
    };

    private static readonly HashSet<string> OAuthProviderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "drive", "onedrive", "dropbox", "box", "google photos", "googlephotos",
        "pcloud", "yandex", "mailru", "hidrive", "zoho", "jottacloud"
    };

    private static readonly string[] SensitiveFlagSubstrings =
    {
        "pass", "secret", "token", "key", "auth", "header"
    };

    public void ExportPortableConfig(
        string destinationPath,
        AppManagerConfig config,
        IEnumerable<RcloneRemoteDetail>? remotes)
    {
        var remoteBlueprints = new List<ExportedRemoteBlueprint>();
        if (remotes != null)
        {
            foreach (var r in remotes)
            {
                var safeParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (k, v) in r.Parameters)
                {
                    if (PortableAllowedRemoteParams.Contains(k) &&
                        !string.IsNullOrWhiteSpace(v) &&
                        !SensitiveDataProtector.IsEncrypted(v))
                    {
                        safeParams[k] = v;
                    }
                }

                remoteBlueprints.Add(new ExportedRemoteBlueprint
                {
                    Name = r.Name,
                    Type = r.Type,
                    SuggestedAuthMode = OAuthProviderNames.Contains(r.Type)
                        ? RemoteAuthMode.OAuthBrowserLink
                        : RemoteAuthMode.KeyOrCredentials,
                    NonSensitiveParameters = safeParams,
                    RequiresAuthentication = true
                });
            }
        }
        else
        {
            foreach (var inferred in InferRemotesFromJobs(config.Jobs))
                remoteBlueprints.Add(inferred);
        }

        var sanitizedJobs = config.Jobs.Select(j => new SyncJob
        {
            Id = j.Id,
            Name = j.Name,
            Enabled = j.Enabled,
            Operation = j.Operation,
            SourcePath = j.SourcePath,
            DestinationPath = j.DestinationPath,
            FilterFilePath = j.FilterFilePath,
            FilterMode = j.FilterMode,
            MaxSize = j.MaxSize,
            BandwidthLimit = j.BandwidthLimit,
            SkipLinks = j.SkipLinks,
            FastList = j.FastList,
            CopyLinks = j.CopyLinks,
            DryRun = j.DryRun,
            LogFilePath = j.LogFilePath,
            ExtraFlags = SanitizeExtraFlags(j.ExtraFlags),
            Strategy = j.Strategy,
            Schedule = new JobSchedule
            {
                IsScheduled = j.Schedule.IsScheduled,
                ScheduleType = j.Schedule.ScheduleType,
                IntervalMinutes = j.Schedule.IntervalMinutes,
                DailyTime = j.Schedule.DailyTime,
                DaysOfWeek = j.Schedule.DaysOfWeek?.ToList() ?? new List<DayOfWeek>(),
                RetryOnFailure = j.Schedule.RetryOnFailure,
                MaxRetries = j.Schedule.MaxRetries,
                RetryDelayMinutes = j.Schedule.RetryDelayMinutes
            },
            LastRunAt = null,
            NextRunAt = null,
            LastStatus = JobRunStatus.Idle,
            LastMessage = "Exportado sem credenciais (aguardando revisão)",
            LastDurationSeconds = null
        }).ToList();

        var bundle = new ExportedConfigBundle
        {
            SchemaVersion = 1,
            Format = "RcloneManager.PortableConfig",
            ExportedAtUtc = DateTime.UtcNow,
            PinnedRcloneVersion = string.IsNullOrWhiteSpace(config.PinnedRcloneVersion)
                ? RcloneReleaseManager.DefaultPinnedVersion
                : config.PinnedRcloneVersion,
            LockRcloneVersion = config.LockRcloneVersion,
            ServiceApiPort = config.ServiceApiPort,
            PersistentRcdPort = config.PersistentRcdPort,
            AutoStartRcdDaemon = config.AutoStartRcdDaemon,
            GlobalBandwidthLimit = config.GlobalBandwidthLimit,
            Remotes = remoteBlueprints,
            Jobs = sanitizedJobs
        };

        var json = JsonSerializer.Serialize(bundle, JsonOptions);
        File.WriteAllText(destinationPath, json);
    }

    public ExportedConfigBundle ReadPortableBundle(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Arquivo não encontrado: '{sourcePath}'.", sourcePath);

        var rawText = File.ReadAllText(sourcePath).Trim();
        if (SensitiveDataProtector.IsEncrypted(rawText))
            throw new InvalidDataException("Arquivos cifrados com chave de outra máquina não são portáteis. Exporte novamente no formato portátil padrão (.json).");

        var readOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        using var doc = JsonDocument.Parse(rawText);
        var root = doc.RootElement;

        if (root.TryGetProperty("format", out var fmt) &&
            string.Equals(fmt.GetString(), "RcloneManager.PortableConfig", StringComparison.OrdinalIgnoreCase))
        {
            var bundle = JsonSerializer.Deserialize<ExportedConfigBundle>(rawText, readOpts)
                         ?? throw new InvalidDataException("Falha ao ler ExportedConfigBundle.");
            bundle.Remotes ??= new List<ExportedRemoteBlueprint>();
            bundle.Jobs ??= new List<SyncJob>();
            foreach (var job in bundle.Jobs)
                job.ExtraFlags = SensitiveDataProtector.IsEncrypted(job.ExtraFlags) ? string.Empty : SanitizeExtraFlags(job.ExtraFlags);
            return bundle;
        }

        // Compatibilidade com rclone_manager_config.json legado: remove qualquer segredo/enc:v1: e infere remotes dos jobs
        var legacy = JsonSerializer.Deserialize<AppManagerConfig>(rawText, readOpts);
        if (legacy == null || legacy.Jobs == null)
            throw new InvalidDataException("O arquivo selecionado não é uma configuração válida do RcloneManager.");

        var jobs = legacy.Jobs.Select(j => new SyncJob
        {
            Id = j.Id,
            Name = j.Name,
            Enabled = j.Enabled,
            Operation = j.Operation,
            SourcePath = j.SourcePath,
            DestinationPath = j.DestinationPath,
            FilterFilePath = j.FilterFilePath,
            FilterMode = j.FilterMode,
            MaxSize = j.MaxSize,
            BandwidthLimit = j.BandwidthLimit,
            SkipLinks = j.SkipLinks,
            FastList = j.FastList,
            CopyLinks = j.CopyLinks,
            DryRun = j.DryRun,
            LogFilePath = j.LogFilePath,
            ExtraFlags = SensitiveDataProtector.IsEncrypted(j.ExtraFlags) ? string.Empty : SanitizeExtraFlags(j.ExtraFlags),
            Strategy = j.Strategy,
            Schedule = j.Schedule ?? new JobSchedule(),
            LastStatus = JobRunStatus.Idle,
            LastMessage = "Importado (aguardando revisão)"
        }).ToList();

        return new ExportedConfigBundle
        {
            SchemaVersion = 1,
            Format = "RcloneManager.PortableConfig",
            ExportedAtUtc = DateTime.UtcNow,
            PinnedRcloneVersion = string.IsNullOrWhiteSpace(legacy.PinnedRcloneVersion)
                ? RcloneReleaseManager.DefaultPinnedVersion
                : legacy.PinnedRcloneVersion,
            LockRcloneVersion = legacy.LockRcloneVersion,
            ServiceApiPort = legacy.ServiceApiPort,
            PersistentRcdPort = legacy.PersistentRcdPort,
            AutoStartRcdDaemon = legacy.AutoStartRcdDaemon,
            GlobalBandwidthLimit = legacy.GlobalBandwidthLimit,
            Remotes = InferRemotesFromJobs(jobs),
            Jobs = jobs
        };
    }

    public static string SanitizeExtraFlags(string? extraFlags)
    {
        if (string.IsNullOrWhiteSpace(extraFlags))
            return string.Empty;

        var tokens = RcloneCliRunner.SplitCommandLineTokens(extraFlags).ToList();
        var clean = new List<string>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];
            if (tok.StartsWith('-'))
            {
                var eqIdx = tok.IndexOf('=');
                var flagName = (eqIdx >= 0 ? tok[..eqIdx] : tok).TrimStart('-');
                if (SensitiveFlagSubstrings.Any(s => flagName.Contains(s, StringComparison.OrdinalIgnoreCase)))
                {
                    if (eqIdx < 0 && i + 1 < tokens.Count && !tokens[i + 1].StartsWith('-'))
                        i++; // Ignora também o valor associado à flag sensível
                    continue;
                }
            }
            clean.Add(tok);
        }

        return string.Join(' ', clean);
    }

    private static List<ExportedRemoteBlueprint> InferRemotesFromJobs(IEnumerable<SyncJob> jobs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ExportedRemoteBlueprint>();

        foreach (var job in jobs)
        {
            foreach (var endpoint in new[] { job.SourcePath, job.DestinationPath })
            {
                var remoteName = ExtractRemoteName(endpoint);
                if (string.IsNullOrWhiteSpace(remoteName) || !seen.Add(remoteName))
                    continue;

                var guessedType = GuessProviderFromRemoteName(remoteName);
                list.Add(new ExportedRemoteBlueprint
                {
                    Name = remoteName,
                    Type = guessedType,
                    SuggestedAuthMode = OAuthProviderNames.Contains(guessedType)
                        ? RemoteAuthMode.OAuthBrowserLink
                        : RemoteAuthMode.KeyOrCredentials,
                    RequiresAuthentication = true
                });
            }
        }

        return list;
    }

    private static string? ExtractRemoteName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var trimmed = path.Trim();
        var colonIdx = trimmed.IndexOf(':');
        if (colonIdx <= 1) // Ignora letras de drive do Windows como C:\
            return null;

        return trimmed[..colonIdx].Trim();
    }

    private static string GuessProviderFromRemoteName(string name)
    {
        if (name.Contains("gdrive", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("drive", StringComparison.OrdinalIgnoreCase))
            return "drive";
        if (name.Contains("onedrive", StringComparison.OrdinalIgnoreCase))
            return "onedrive";
        if (name.Contains("dropbox", StringComparison.OrdinalIgnoreCase))
            return "dropbox";
        if (name.Contains("s3", StringComparison.OrdinalIgnoreCase))
            return "s3";
        if (name.Contains("sftp", StringComparison.OrdinalIgnoreCase))
            return "sftp";
        return "drive";
    }

    public AppManagerConfig ResetConfig(bool deleteCredentialKey, bool deleteLogsAndRuntime, bool deleteRcloneConf)
    {
        lock (_syncLock)
        {
            var currentConfPath = string.Empty;
            if (File.Exists(ConfigFilePath))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<AppManagerConfig>(File.ReadAllText(ConfigFilePath), JsonOptions);
                    if (existing != null)
                    {
                        DecryptSensitiveFieldsInPlace(existing);
                        currentConfPath = existing.RcloneConfigPath;
                    }
                }
                catch
                {
                }

                File.Delete(ConfigFilePath);
            }

            if (deleteLogsAndRuntime)
            {
                var runtimeDir = Path.Combine(BaseDirectory, ".rclone-manager-runtime");
                if (Directory.Exists(runtimeDir))
                {
                    try { Directory.Delete(runtimeDir, recursive: true); } catch { }
                }

                foreach (var logName in new[] { "rclone_manager_service.log", "rclone_backup.log" })
                {
                    var lp = Path.Combine(BaseDirectory, logName);
                    if (File.Exists(lp))
                    {
                        try { File.Delete(lp); } catch { }
                    }
                }
            }

            if (deleteRcloneConf)
            {
                var rcloneExe = WorkspacePathResolver.FindRcloneExecutable(BaseDirectory, RcloneReleaseManager.DefaultPinnedVersion);
                var confToDelete = !string.IsNullOrWhiteSpace(currentConfPath) && File.Exists(currentConfPath)
                    ? currentConfPath
                    : DetectRcloneConfigPath(rcloneExe);

                if (!string.IsNullOrWhiteSpace(confToDelete) && File.Exists(confToDelete))
                {
                    File.Copy(confToDelete, confToDelete + $".bak.{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
                    File.Delete(confToDelete);
                }
            }

            if (deleteCredentialKey)
            {
                WindowsCredentialVault.DeleteMasterKey(WindowsCredentialVault.DefaultTargetName);
            }

            var fresh = CreateDefaultConfig();
            fresh.FirstRunCompleted = false;
            EnsureEncryptionInitialized(fresh);
            SaveInternal(fresh);
            return fresh;
        }
    }

    private void SaveInternal(AppManagerConfig config)
    {
        var serializable = CloneForProtectedSerialization(config);
        var json = JsonSerializer.Serialize(serializable, JsonOptions);
        var tempFile = ConfigFilePath + ".tmp";
        File.WriteAllText(tempFile, json);
        File.Move(tempFile, ConfigFilePath, overwrite: true);
    }

    private static void EnsureEncryptionInitialized(AppManagerConfig config)
    {
        if (!config.EncryptionEnabled)
            return;

        if (string.IsNullOrWhiteSpace(config.EncryptionKeyTarget))
            config.EncryptionKeyTarget = WindowsCredentialVault.DefaultTargetName;

        SensitiveDataProtector.EnsureInitialized(out _, config.EncryptionKeyTarget);
    }

    private static void DecryptSensitiveFieldsInPlace(AppManagerConfig config)
    {
        var target = string.IsNullOrWhiteSpace(config.EncryptionKeyTarget)
            ? WindowsCredentialVault.DefaultTargetName
            : config.EncryptionKeyTarget;

        try
        {
            if (SensitiveDataProtector.IsEncrypted(config.RcloneConfigPath))
                config.RcloneConfigPath = SensitiveDataProtector.DecryptString(config.RcloneConfigPath, target);

            if (config.ProtectedSecrets != null)
            {
                foreach (var key in config.ProtectedSecrets.Keys.ToList())
                {
                    var val = config.ProtectedSecrets[key];
                    if (SensitiveDataProtector.IsEncrypted(val))
                        config.ProtectedSecrets[key] = SensitiveDataProtector.DecryptString(val, target);
                }
            }

            foreach (var job in config.Jobs)
            {
                if (SensitiveDataProtector.IsEncrypted(job.ExtraFlags))
                    job.ExtraFlags = SensitiveDataProtector.DecryptString(job.ExtraFlags, target);
            }
        }
        catch
        {
            // Caso a chave tenha sido excluída externamente, mantém campo vazio em vez de quebrar startup.
            if (SensitiveDataProtector.IsEncrypted(config.RcloneConfigPath))
                config.RcloneConfigPath = string.Empty;
            foreach (var job in config.Jobs)
            {
                if (SensitiveDataProtector.IsEncrypted(job.ExtraFlags))
                    job.ExtraFlags = string.Empty;
            }
        }
    }

    private static AppManagerConfig CloneForProtectedSerialization(AppManagerConfig src)
    {
        var target = string.IsNullOrWhiteSpace(src.EncryptionKeyTarget)
            ? WindowsCredentialVault.DefaultTargetName
            : src.EncryptionKeyTarget;

        string Protect(string val) =>
            src.EncryptionEnabled && !string.IsNullOrWhiteSpace(val)
                ? SensitiveDataProtector.EncryptString(val, target)
                : val;

        var protectedSecrets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (src.ProtectedSecrets != null)
        {
            foreach (var (k, v) in src.ProtectedSecrets)
                protectedSecrets[k] = Protect(v);
        }

        return new AppManagerConfig
        {
            RcloneExecutablePath = src.RcloneExecutablePath,
            RcloneConfigPath = Protect(src.RcloneConfigPath),
            WorkingDirectory = src.WorkingDirectory,
            PinnedRcloneVersion = src.PinnedRcloneVersion,
            LockRcloneVersion = src.LockRcloneVersion,
            FirstRunCompleted = src.FirstRunCompleted,
            EncryptionEnabled = src.EncryptionEnabled,
            EncryptionKeyTarget = target,
            ProtectedSecrets = protectedSecrets,
            ServiceApiPort = src.ServiceApiPort,
            PersistentRcdPort = src.PersistentRcdPort,
            AutoStartRcdDaemon = src.AutoStartRcdDaemon,
            GlobalBandwidthLimit = src.GlobalBandwidthLimit,
            Jobs = src.Jobs.Select(j => new SyncJob
            {
                Id = j.Id,
                Name = j.Name,
                Enabled = j.Enabled,
                Operation = j.Operation,
                SourcePath = j.SourcePath,
                DestinationPath = j.DestinationPath,
                FilterFilePath = j.FilterFilePath,
                FilterMode = j.FilterMode,
                MaxSize = j.MaxSize,
                BandwidthLimit = j.BandwidthLimit,
                SkipLinks = j.SkipLinks,
                FastList = j.FastList,
                CopyLinks = j.CopyLinks,
                DryRun = j.DryRun,
                LogFilePath = j.LogFilePath,
                ExtraFlags = Protect(j.ExtraFlags),
                Strategy = j.Strategy,
                Schedule = new JobSchedule
                {
                    IsScheduled = j.Schedule.IsScheduled,
                    ScheduleType = j.Schedule.ScheduleType,
                    IntervalMinutes = j.Schedule.IntervalMinutes,
                    DailyTime = j.Schedule.DailyTime,
                    DaysOfWeek = j.Schedule.DaysOfWeek?.ToList() ?? new List<DayOfWeek>(),
                    RetryOnFailure = j.Schedule.RetryOnFailure,
                    MaxRetries = j.Schedule.MaxRetries,
                    RetryDelayMinutes = j.Schedule.RetryDelayMinutes
                },
                LastRunAt = j.LastRunAt,
                NextRunAt = j.NextRunAt,
                LastStatus = j.LastStatus,
                LastMessage = j.LastMessage,
                LastDurationSeconds = j.LastDurationSeconds
            }).ToList()
        };
    }

    private void EnsurePathsPopulated(AppManagerConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.PinnedRcloneVersion))
            config.PinnedRcloneVersion = RcloneReleaseManager.DefaultPinnedVersion;

        if (string.IsNullOrWhiteSpace(config.EncryptionKeyTarget))
            config.EncryptionKeyTarget = WindowsCredentialVault.DefaultTargetName;

        config.ProtectedSecrets ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(config.WorkingDirectory) || !Directory.Exists(config.WorkingDirectory))
            config.WorkingDirectory = BaseDirectory;

        if (string.IsNullOrWhiteSpace(config.RcloneExecutablePath) || !File.Exists(config.RcloneExecutablePath))
            config.RcloneExecutablePath = WorkspacePathResolver.FindRcloneExecutable(BaseDirectory, config.PinnedRcloneVersion);

        if (string.IsNullOrWhiteSpace(config.RcloneConfigPath))
            config.RcloneConfigPath = DetectRcloneConfigPath(config.RcloneExecutablePath);
    }

    private AppManagerConfig CreateDefaultConfig()
    {
        var rcloneExe = WorkspacePathResolver.FindRcloneExecutable(BaseDirectory, RcloneReleaseManager.DefaultPinnedVersion);
        var rcloneConf = DetectRcloneConfigPath(rcloneExe);
        var filterFile = WorkspacePathResolver.FindExistingFilterFile(BaseDirectory);
        var logFile = Path.Combine(BaseDirectory, "rclone_backup.log");

        var config = new AppManagerConfig
        {
            WorkingDirectory = BaseDirectory,
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
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var defaultPath = Path.Combine(appData, "rclone", "rclone.conf");
        return File.Exists(defaultPath) ? defaultPath : string.Empty;
    }
}
