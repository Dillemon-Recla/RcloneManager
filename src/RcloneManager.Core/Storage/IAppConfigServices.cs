using RcloneManager.Core.Models;

namespace RcloneManager.Core.Storage;

public interface IConfigCryptoTransformer
{
    bool HasSensitiveData(AppManagerConfig config);
    AppManagerConfig ProtectForStorage(AppManagerConfig source, bool rotateDisposableKey = false);
    void UnprotectInPlace(AppManagerConfig config);
}

public interface IDefaultConfigFactory
{
    AppManagerConfig CreateDefault(string baseDirectory);
    void EnsurePathsPopulated(AppManagerConfig config, string baseDirectory);
    string DetectRcloneConfigPath(string rcloneExePath);
}

public interface IPortableConfigService
{
    void ExportPortableBundle(string destinationPath, AppManagerConfig config, IEnumerable<RcloneRemoteDetail>? remotes);
    ExportedConfigBundle ReadPortableBundle(string sourcePath);
    string SanitizeExtraFlags(string? extraFlags);
}

public interface IConfigResetService
{
    AppManagerConfig Reset(bool deleteCredentialKey = true, bool deleteLogsAndRuntime = true, bool deleteRcloneConf = false);
}

public interface IAppConfigRepository
{
    string BaseDirectory { get; }
    string ConfigFilePath { get; }
    AppManagerConfig LoadOrCreateDefault();
    void Save(AppManagerConfig config, bool rotateDisposableKey = false);
}
