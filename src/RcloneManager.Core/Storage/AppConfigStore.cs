using System.Runtime.Versioning;
using System.Text.Json;
using RcloneManager.Core.Models;
using RcloneManager.Core.Security;

namespace RcloneManager.Core.Storage;

[SupportedOSPlatform("windows")]
public sealed class AppConfigStore : IAppConfigRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _syncLock = new();
    private readonly IConfigCryptoTransformer _cryptoTransformer;
    private readonly IDefaultConfigFactory _defaultFactory;
    private readonly IPortableConfigService _portableService;
    private readonly IConfigResetService _resetService;

    public string ConfigFilePath { get; }
    public string BaseDirectory { get; }

    public AppConfigStore(
        string baseDirectory,
        ISensitiveDataProtector? protector = null,
        IConfigCryptoTransformer? cryptoTransformer = null,
        IDefaultConfigFactory? defaultFactory = null,
        IPortableConfigService? portableService = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);

        BaseDirectory = baseDirectory;
        ConfigFilePath = Path.Combine(baseDirectory, "rclone_manager_config.json");

        var resolvedProtector = protector ?? AesGcmDataProtector.Shared;
        _cryptoTransformer = cryptoTransformer ?? new ConfigCryptoTransformer(resolvedProtector);
        _defaultFactory = defaultFactory ?? DefaultConfigFactory.Shared;
        _portableService = portableService ?? new PortableConfigService(resolvedProtector);
        _resetService = new ConfigResetService(this, _cryptoTransformer, _defaultFactory, resolvedProtector);
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
                        _cryptoTransformer.UnprotectInPlace(loaded);
                        _defaultFactory.EnsurePathsPopulated(loaded, BaseDirectory);
                        return loaded;
                    }
                }
                catch
                {
                    var brokenBackup = ConfigFilePath + $".corrupt.{DateTime.Now:yyyyMMddHHmmss}";
                    File.Copy(ConfigFilePath, brokenBackup, overwrite: true);
                }
            }

            var created = _defaultFactory.CreateDefault(BaseDirectory);
            SaveInternal(created, rotateDisposableKey: false);
            return created;
        }
    }

    public void Save(AppManagerConfig config, bool rotateDisposableKey = false)
    {
        ArgumentNullException.ThrowIfNull(config);

        lock (_syncLock)
        {
            SaveInternal(config, rotateDisposableKey);
        }
    }

    public void ExportPortableConfig(
        string destinationPath,
        AppManagerConfig config,
        IEnumerable<RcloneRemoteDetail>? remotes) =>
        _portableService.ExportPortableBundle(destinationPath, config, remotes);

    public ExportedConfigBundle ReadPortableBundle(string sourcePath) =>
        _portableService.ReadPortableBundle(sourcePath);

    public static string SanitizeExtraFlags(string? extraFlags) =>
        PortableConfigService.SanitizeExtraFlagsStatic(extraFlags);

    public AppManagerConfig ResetConfig(
        bool deleteCredentialKey = true,
        bool deleteLogsAndRuntime = true,
        bool deleteRcloneConf = false)
    {
        lock (_syncLock)
        {
            return _resetService.Reset(deleteCredentialKey, deleteLogsAndRuntime, deleteRcloneConf);
        }
    }

    public static string DetectRcloneConfigPath(string rcloneExePath) =>
        DefaultConfigFactory.Shared.DetectRcloneConfigPath(rcloneExePath);

    private void SaveInternal(AppManagerConfig config, bool rotateDisposableKey)
    {
        var protectedCopy = _cryptoTransformer.ProtectForStorage(config, rotateDisposableKey);
        var json = JsonSerializer.Serialize(protectedCopy, JsonOptions);
        var tempFile = ConfigFilePath + ".tmp";
        File.WriteAllText(tempFile, json);
        File.Move(tempFile, ConfigFilePath, overwrite: true);
    }
}
