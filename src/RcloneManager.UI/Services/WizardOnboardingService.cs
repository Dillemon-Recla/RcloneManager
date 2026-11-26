using System.IO;
using RcloneManager.Core.Engine;
using RcloneManager.Core.Logging;
using RcloneManager.Core.Models;
using RcloneManager.Core.Onboarding;
using RcloneManager.Core.Storage;

namespace RcloneManager.UI.Services;

public sealed class WizardRemoteBlueprintItem
{
    public ExportedRemoteBlueprint Blueprint { get; init; } = new();
    public bool IsAuthenticated { get; set; }

    public override string ToString()
    {
        var status = IsAuthenticated ? "✓ Conectado e verificado" : "Pendente de conexão";
        return $"{Blueprint.Name} ({Blueprint.Type}) — {status}";
    }
}

public sealed record WizardRemotesSnapshot(
    string SummaryText,
    IReadOnlyList<WizardRemoteBlueprintItem> ImportedRemotes,
    int RecommendedSelectionIndex,
    int ConfiguredRemoteCount,
    IReadOnlyList<RcloneRemoteDetail> ExistingRemoteDetails);

public sealed record WizardRemoteAuthOutcome(
    bool TestSucceeded,
    string StatusMessage);

public interface IWizardOnboardingService
{
    string WorkDirectory { get; }
    AppManagerConfig CurrentConfig { get; }
    IReadOnlyList<WizardRemoteBlueprintItem> ImportedRemotes { get; }

    void ResetToCleanStart();
    string ImportPortableBundle(string filePath);
    Task<RcloneInstallationCheck> CheckReleaseAsync(CancellationToken ct = default);
    void ApplyVerifiedRelease(string pinnedVersion, bool lockVersion, string executablePath);
    Task<WizardRemotesSnapshot> RefreshRemotesStateAsync(bool verifyImportedConnections = false, CancellationToken ct = default);
    Task<WizardRemoteAuthOutcome> AuthenticateAndTestRemoteAsync(
        WizardRemoteAuthInput input,
        IProgress<string>? stageProgress = null,
        CancellationToken ct = default);
    Task<RemoteTestResult> VerifyExistingRemoteAsync(string remoteName, string expectedProvider, CancellationToken ct = default);
    Task CompleteAsync(IReadOnlyList<SyncJob>? reviewedJobs, bool autoStartupWithWindows, CancellationToken ct = default);
}

public sealed class WizardOnboardingService : IWizardOnboardingService
{
    private static readonly HashSet<string> OAuthProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "drive", "onedrive", "dropbox", "box", "google photos", "googlephotos",
        "pcloud", "yandex", "mailru", "hidrive", "zoho", "jottacloud"
    };

    private readonly IAppConfigRepository _repository;
    private readonly IPortableConfigService _portableService;
    private readonly IDefaultConfigFactory _defaultFactory;
    private readonly ServiceApiClient? _api;
    private readonly List<WizardRemoteBlueprintItem> _importedRemotes = new();

    public string WorkDirectory => _repository.BaseDirectory;
    public AppManagerConfig CurrentConfig { get; }
    public IReadOnlyList<WizardRemoteBlueprintItem> ImportedRemotes => _importedRemotes;

    public WizardOnboardingService(
        string workDirectory,
        ServiceApiClient? api = null,
        IAppConfigRepository? repository = null,
        IPortableConfigService? portableService = null,
        IDefaultConfigFactory? defaultFactory = null)
    {
        var defaultStore = repository as AppConfigStore ?? (repository == null ? new AppConfigStore(workDirectory) : null);
        _repository = repository ?? defaultStore!;
        _portableService = portableService ?? new PortableConfigService(RcloneManager.Core.Security.AesGcmDataProtector.Shared);
        _defaultFactory = defaultFactory ?? DefaultConfigFactory.Shared;
        _api = api;

        CurrentConfig = _repository.LoadOrCreateDefault();
    }

    public void ResetToCleanStart()
    {
        _importedRemotes.Clear();
        var clean = _defaultFactory.CreateDefault(WorkDirectory);
        CurrentConfig.PinnedRcloneVersion = clean.PinnedRcloneVersion;
        CurrentConfig.LockRcloneVersion = clean.LockRcloneVersion;
        CurrentConfig.AutoStartRcdDaemon = clean.AutoStartRcdDaemon;
        CurrentConfig.GlobalBandwidthLimit = clean.GlobalBandwidthLimit;
        CurrentConfig.Jobs = clean.Jobs.Select(j => j.Clone()).ToList();
    }

    public string ImportPortableBundle(string filePath)
    {
        var bundle = _portableService.ReadPortableBundle(filePath);

        CurrentConfig.PinnedRcloneVersion = string.IsNullOrWhiteSpace(bundle.PinnedRcloneVersion)
            ? RcloneReleaseManager.DefaultPinnedVersion
            : bundle.PinnedRcloneVersion;
        CurrentConfig.LockRcloneVersion = bundle.LockRcloneVersion;
        CurrentConfig.ServiceApiPort = bundle.ServiceApiPort > 0 ? bundle.ServiceApiPort : 5580;
        CurrentConfig.PersistentRcdPort = bundle.PersistentRcdPort > 0 ? bundle.PersistentRcdPort : 5572;
        CurrentConfig.AutoStartRcdDaemon = bundle.AutoStartRcdDaemon;
        CurrentConfig.GlobalBandwidthLimit = string.IsNullOrWhiteSpace(bundle.GlobalBandwidthLimit)
            ? "off"
            : bundle.GlobalBandwidthLimit;

        if (bundle.Jobs.Count > 0)
            CurrentConfig.Jobs = bundle.Jobs.Select(j => j.Clone()).ToList();

        _importedRemotes.Clear();
        foreach (var remote in bundle.Remotes)
        {
            _importedRemotes.Add(new WizardRemoteBlueprintItem
            {
                Blueprint = remote,
                IsAuthenticated = false
            });
        }

        var servicesText = _importedRemotes.Count > 0
            ? string.Join(", ", _importedRemotes.Select(r => $"{r.Blueprint.Name} ({r.Blueprint.Type})"))
            : "nenhum serviço externo";

        return $"Importado '{Path.GetFileName(filePath)}': {CurrentConfig.Jobs.Count} tarefa(s) | Contas apontadas: {servicesText}.";
    }

    public Task<RcloneInstallationCheck> CheckReleaseAsync(CancellationToken ct = default) =>
        RcloneReleaseManager.CheckInstallationAsync(CurrentConfig, WorkDirectory, ct);

    public void ApplyVerifiedRelease(string pinnedVersion, bool lockVersion, string executablePath)
    {
        CurrentConfig.PinnedRcloneVersion = RcloneReleaseManager.NormalizeVersion(pinnedVersion);
        CurrentConfig.LockRcloneVersion = lockVersion;

        if (!string.IsNullOrWhiteSpace(executablePath))
            CurrentConfig.RcloneExecutablePath = executablePath;

        if (string.IsNullOrWhiteSpace(CurrentConfig.RcloneConfigPath))
            CurrentConfig.RcloneConfigPath = _defaultFactory.DetectRcloneConfigPath(CurrentConfig.RcloneExecutablePath);

        _repository.Save(CurrentConfig, rotateDisposableKey: false);
    }

    public async Task<WizardRemotesSnapshot> RefreshRemotesStateAsync(
        bool verifyImportedConnections = false,
        CancellationToken ct = default)
    {
        var detailsList = new List<RcloneRemoteDetail>();
        string summaryText;

        if (!File.Exists(CurrentConfig.RcloneExecutablePath))
        {
            summaryText = "Conclua a Etapa 2 primeiro para habilitar a conexão das contas.";
            return new WizardRemotesSnapshot(summaryText, _importedRemotes, -1, 0, detailsList);
        }

        if (string.IsNullOrWhiteSpace(CurrentConfig.RcloneConfigPath))
            CurrentConfig.RcloneConfigPath = _defaultFactory.DetectRcloneConfigPath(CurrentConfig.RcloneExecutablePath);

        try
        {
            var log = new ServiceLogBuffer(WorkDirectory);
            await using var engine = new RcloneHybridEngine(log);
            var (remotes, _) = await engine.GetRemotesDetailsHybridAsync(CurrentConfig, ct);
            detailsList = remotes;

            summaryText = detailsList.Count > 0
                ? $"Contas salvas neste computador: {string.Join(", ", detailsList.Select(d => $"{d.Name} ({d.Type})"))}"
                : "Nenhuma conta conectada neste computador ainda.";

            if (verifyImportedConnections && _importedRemotes.Count > 0)
            {
                foreach (var item in _importedRemotes)
                {
                    if (item.IsAuthenticated)
                        continue;

                    var match = detailsList.FirstOrDefault(
                        d => d.Name.Equals(item.Blueprint.Name, StringComparison.OrdinalIgnoreCase) &&
                             d.Type.Equals(item.Blueprint.Type, StringComparison.OrdinalIgnoreCase));

                    if (match != null)
                    {
                        if (OAuthProviders.Contains(match.Type) && !match.HasToken && !match.RawParameterKeys.Contains("service_account_file", StringComparer.OrdinalIgnoreCase))
                            continue;

                        var test = await engine.TestRemoteConnectionAsync(CurrentConfig, match.Name, ct);
                        if (test.Success)
                            item.IsAuthenticated = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            summaryText = $"Não foi possível ler as contas locais agora: {ex.Message}";
        }

        var recommendedIndex = -1;
        if (_importedRemotes.Count > 0)
        {
            var firstPending = _importedRemotes.FindIndex(r => !r.IsAuthenticated);
            recommendedIndex = firstPending >= 0 ? firstPending : 0;
        }

        return new WizardRemotesSnapshot(
            summaryText,
            _importedRemotes,
            recommendedIndex,
            detailsList.Count,
            detailsList);
    }

    public async Task<RemoteTestResult> VerifyExistingRemoteAsync(
        string remoteName,
        string expectedProvider,
        CancellationToken ct = default)
    {
        if (!File.Exists(CurrentConfig.RcloneExecutablePath))
            return new RemoteTestResult { Success = false, Message = "O Rclone ainda não está instalado." };

        if (string.IsNullOrWhiteSpace(CurrentConfig.RcloneConfigPath))
            CurrentConfig.RcloneConfigPath = _defaultFactory.DetectRcloneConfigPath(CurrentConfig.RcloneExecutablePath);

        var cleanName = remoteName.Trim().TrimEnd(':');
        var log = new ServiceLogBuffer(WorkDirectory);
        await using var engine = new RcloneHybridEngine(log);

        var (details, _) = await engine.GetRemotesDetailsHybridAsync(CurrentConfig, ct);
        var match = details.FirstOrDefault(d => d.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            return new RemoteTestResult
            {
                Success = false,
                Message = $"A conta '{cleanName}' ainda não foi configurada."
            };
        }

        if (!match.Type.Equals(expectedProvider, StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteTestResult
            {
                Success = false,
                Message = $"O apelido '{cleanName}' está salvo como '{match.Type}', e não como '{expectedProvider}'."
            };
        }

        return await engine.TestRemoteConnectionAsync(CurrentConfig, cleanName, ct);
    }

    public async Task<WizardRemoteAuthOutcome> AuthenticateAndTestRemoteAsync(
        WizardRemoteAuthInput input,
        IProgress<string>? stageProgress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(CurrentConfig.RcloneExecutablePath))
            throw new InvalidOperationException("Conclua a Etapa 2 para preparar o Rclone antes de conectar uma conta.");

        if (string.IsNullOrWhiteSpace(CurrentConfig.RcloneConfigPath))
            CurrentConfig.RcloneConfigPath = _defaultFactory.DetectRcloneConfigPath(CurrentConfig.RcloneExecutablePath);

        if (_api != null)
        {
            try { await _api.StopRcdAsync(ct); } catch { }
        }

        var normalizedName = input.RemoteName.Trim().TrimEnd(':');
        var log = new ServiceLogBuffer(WorkDirectory);
        await using var engine = new RcloneHybridEngine(log);

        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var (details, _) = await engine.GetRemotesDetailsHybridAsync(CurrentConfig, ct);
            foreach (var d in details)
                existingNames.Add(d.Name);
        }
        catch
        {
        }

        var existedBefore = existingNames.Contains(normalizedName);
        var importedMatch = _importedRemotes.FirstOrDefault(
            r => r.Blueprint.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));

        var request = RemoteAuthRequestBuilder.Build(input, importedMatch?.Blueprint, existingNames);

        stageProgress?.Report(input.AuthMode == RemoteAuthMode.OAuthBrowserLink
            ? $"Aguardando você autorizar o acesso no navegador ({input.Provider})... Conclua o login na janela aberta."
            : $"Salvando credenciais de '{normalizedName}' ({input.Provider})...");

        await engine.CreateOrUpdateRemoteHybridAsync(CurrentConfig, request, ct);

        stageProgress?.Report($"Verificando conexão real com '{normalizedName}:' ({input.Provider})...");
        var test = await engine.TestRemoteConnectionAsync(CurrentConfig, request.Name, ct);

        if (!test.Success)
        {
            if (importedMatch != null)
                importedMatch.IsAuthenticated = false;

            if (!existedBefore)
            {
                try { await engine.DeleteRemoteHybridAsync(CurrentConfig, normalizedName, CancellationToken.None); } catch { }
            }

            return new WizardRemoteAuthOutcome(
                false,
                $"Não foi possível validar a conexão com '{normalizedName}' ({input.Provider}): {test.Message}");
        }

        if (importedMatch != null)
            importedMatch.IsAuthenticated = true;

        return new WizardRemoteAuthOutcome(
            true,
            $"✓ Conta '{normalizedName}' ({input.Provider}) conectada e verificada com sucesso!");
    }

    public async Task CompleteAsync(
        IReadOnlyList<SyncJob>? reviewedJobs,
        bool autoStartupWithWindows,
        CancellationToken ct = default)
    {
        if (reviewedJobs != null)
            CurrentConfig.Jobs = reviewedJobs.Select(j => j.Clone()).ToList();

        if (string.IsNullOrWhiteSpace(CurrentConfig.RcloneConfigPath))
            CurrentConfig.RcloneConfigPath = _defaultFactory.DetectRcloneConfigPath(CurrentConfig.RcloneExecutablePath);

        CurrentConfig.FirstRunCompleted = true;

        // Rotaciona atomicamente a chave descartável no momento em que a configuração revisada é persistida.
        _repository.Save(CurrentConfig, rotateDisposableKey: true);

        if (_api != null)
        {
            try
            {
                await _api.ReloadServiceConfigAsync(ct);
                if (reviewedJobs != null)
                    await _api.SetStartupAsync(autoStartupWithWindows, ct);
            }
            catch
            {
            }
        }
    }
}
