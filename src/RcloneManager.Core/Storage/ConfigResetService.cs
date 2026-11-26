using System.Text.Json;
using RcloneManager.Core.Engine;
using RcloneManager.Core.Models;
using RcloneManager.Core.Security;

namespace RcloneManager.Core.Storage;

public sealed class ConfigResetService : IConfigResetService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IAppConfigRepository _repository;
    private readonly IConfigCryptoTransformer _cryptoTransformer;
    private readonly IDefaultConfigFactory _defaultFactory;
    private readonly ISensitiveDataProtector _protector;

    public ConfigResetService(
        IAppConfigRepository repository,
        IConfigCryptoTransformer cryptoTransformer,
        IDefaultConfigFactory defaultFactory,
        ISensitiveDataProtector protector)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _cryptoTransformer = cryptoTransformer ?? throw new ArgumentNullException(nameof(cryptoTransformer));
        _defaultFactory = defaultFactory ?? throw new ArgumentNullException(nameof(defaultFactory));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public AppManagerConfig Reset(
        bool deleteCredentialKey = true,
        bool deleteLogsAndRuntime = true,
        bool deleteRcloneConf = false)
    {
        var currentConfPath = TryReadCurrentRcloneConfPath();

        if (File.Exists(_repository.ConfigFilePath))
            File.Delete(_repository.ConfigFilePath);

        if (deleteLogsAndRuntime)
            CleanLogsAndRuntime(_repository.BaseDirectory);

        if (deleteRcloneConf)
            BackupAndDeleteRcloneConf(_repository.BaseDirectory, currentConfPath);

        if (deleteCredentialKey)
            _protector.DiscardKey();

        var fresh = _defaultFactory.CreateDefault(_repository.BaseDirectory);
        fresh.FirstRunCompleted = false;
        _repository.Save(fresh, rotateDisposableKey: deleteCredentialKey);
        return fresh;
    }

    private string TryReadCurrentRcloneConfPath()
    {
        if (!File.Exists(_repository.ConfigFilePath))
            return string.Empty;

        try
        {
            var existing = JsonSerializer.Deserialize<AppManagerConfig>(File.ReadAllText(_repository.ConfigFilePath), JsonOptions);
            if (existing == null)
                return string.Empty;

            _cryptoTransformer.UnprotectInPlace(existing);
            return existing.RcloneConfigPath;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void CleanLogsAndRuntime(string baseDirectory)
    {
        var runtimeDir = Path.Combine(baseDirectory, ".rclone-manager-runtime");
        if (Directory.Exists(runtimeDir))
        {
            try { Directory.Delete(runtimeDir, recursive: true); } catch { }
        }

        foreach (var logName in new[] { "rclone_manager_service.log", "rclone_backup.log" })
        {
            var logPath = Path.Combine(baseDirectory, logName);
            if (File.Exists(logPath))
            {
                try { File.Delete(logPath); } catch { }
            }
        }
    }

    private void BackupAndDeleteRcloneConf(string baseDirectory, string currentConfPath)
    {
        var rcloneExe = WorkspacePathResolver.FindRcloneExecutable(baseDirectory, RcloneReleaseManager.DefaultPinnedVersion);
        var confToDelete = !string.IsNullOrWhiteSpace(currentConfPath) && File.Exists(currentConfPath)
            ? currentConfPath
            : _defaultFactory.DetectRcloneConfigPath(rcloneExe);

        if (!string.IsNullOrWhiteSpace(confToDelete) && File.Exists(confToDelete))
        {
            File.Copy(confToDelete, confToDelete + $".bak.{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            File.Delete(confToDelete);
        }
    }
}
