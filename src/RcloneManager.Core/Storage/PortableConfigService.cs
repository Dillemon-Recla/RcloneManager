using System.Text.Json;
using RcloneManager.Core.Engine;
using RcloneManager.Core.Models;
using RcloneManager.Core.Security;

namespace RcloneManager.Core.Storage;

public sealed class PortableConfigService : IPortableConfigService
{
    private static readonly JsonSerializerOptions WriteJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions ReadJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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

    private readonly ISensitiveDataProtector _protector;

    public PortableConfigService(ISensitiveDataProtector protector)
    {
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
    }

    public void ExportPortableBundle(
        string destinationPath,
        AppManagerConfig config,
        IEnumerable<RcloneRemoteDetail>? remotes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(config);

        var remoteBlueprints = BuildRemoteBlueprints(config, remotes);
        var sanitizedJobs = config.Jobs.Select(SanitizeJobForExport).ToList();

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

        var json = JsonSerializer.Serialize(bundle, WriteJsonOptions);
        File.WriteAllText(destinationPath, json);
    }

    public ExportedConfigBundle ReadPortableBundle(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException($"Arquivo não encontrado: '{sourcePath}'.", sourcePath);

        var rawText = File.ReadAllText(sourcePath).Trim();
        if (_protector.IsProtected(rawText))
            throw new InvalidDataException("Arquivos cifrados com chave local não são portáteis. Exporte novamente no formato portátil padrão (.json).");

        using var doc = JsonDocument.Parse(rawText);
        var root = doc.RootElement;

        if (root.TryGetProperty("format", out var fmt) &&
            string.Equals(fmt.GetString(), "RcloneManager.PortableConfig", StringComparison.OrdinalIgnoreCase))
        {
            var bundle = JsonSerializer.Deserialize<ExportedConfigBundle>(rawText, ReadJsonOptions)
                         ?? throw new InvalidDataException("Falha ao ler ExportedConfigBundle.");
            bundle.Remotes ??= new List<ExportedRemoteBlueprint>();
            bundle.Jobs ??= new List<SyncJob>();
            foreach (var job in bundle.Jobs)
                job.ExtraFlags = _protector.IsProtected(job.ExtraFlags) ? string.Empty : SanitizeExtraFlags(job.ExtraFlags);
            return bundle;
        }

        var legacy = JsonSerializer.Deserialize<AppManagerConfig>(rawText, ReadJsonOptions);
        if (legacy?.Jobs == null)
            throw new InvalidDataException("O arquivo selecionado não é uma configuração válida do RcloneManager.");

        var jobs = legacy.Jobs.Select(j =>
        {
            var clean = j.Clone();
            clean.ExtraFlags = _protector.IsProtected(j.ExtraFlags) ? string.Empty : SanitizeExtraFlags(j.ExtraFlags);
            clean.LastRunAt = null;
            clean.NextRunAt = null;
            clean.LastStatus = JobRunStatus.Idle;
            clean.LastMessage = "Importado (aguardando revisão)";
            clean.LastDurationSeconds = null;
            return clean;
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

    public string SanitizeExtraFlags(string? extraFlags) => SanitizeExtraFlagsStatic(extraFlags);

    public static string SanitizeExtraFlagsStatic(string? extraFlags)
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
                        i++;
                    continue;
                }
            }
            clean.Add(tok);
        }

        return string.Join(' ', clean);
    }

    private SyncJob SanitizeJobForExport(SyncJob source)
    {
        var clean = source.Clone();
        clean.ExtraFlags = _protector.IsProtected(source.ExtraFlags)
            ? string.Empty
            : SanitizeExtraFlags(source.ExtraFlags);
        clean.LastRunAt = null;
        clean.NextRunAt = null;
        clean.LastStatus = JobRunStatus.Idle;
        clean.LastMessage = "Exportado sem credenciais (aguardando revisão)";
        clean.LastDurationSeconds = null;
        return clean;
    }

    private List<ExportedRemoteBlueprint> BuildRemoteBlueprints(
        AppManagerConfig config,
        IEnumerable<RcloneRemoteDetail>? remotes)
    {
        if (remotes == null)
            return InferRemotesFromJobs(config.Jobs);

        var blueprints = new List<ExportedRemoteBlueprint>();
        foreach (var r in remotes)
        {
            var safeParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in r.Parameters)
            {
                if (PortableAllowedRemoteParams.Contains(k) &&
                    !string.IsNullOrWhiteSpace(v) &&
                    !_protector.IsProtected(v))
                {
                    safeParams[k] = v;
                }
            }

            blueprints.Add(new ExportedRemoteBlueprint
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

        return blueprints;
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
        return colonIdx <= 1 ? null : trimmed[..colonIdx].Trim();
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
}
