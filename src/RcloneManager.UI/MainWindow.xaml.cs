using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RcloneManager.Core.Engine;
using RcloneManager.Core.Models;
using RcloneManager.Core.Storage;
using RcloneManager.Core.Updater;
using RcloneManager.UI.Services;
using WinForms = System.Windows.Forms;
using WpfInput = System.Windows.Input;

namespace RcloneManager.UI;

public sealed class JobRowItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string NextRunText { get; set; } = string.Empty;
    public string LastRunText { get; set; } = string.Empty;
    public SyncJob RawJob { get; set; } = new();
}

public sealed class TransferRowItem
{
    public string FileName { get; set; } = string.Empty;
    public string SizeFormatted { get; set; } = string.Empty;
    public string TransferredFormatted { get; set; } = string.Empty;
    public string PercentText { get; set; } = string.Empty;
    public string SpeedFormatted { get; set; } = string.Empty;
    public string EtaFormatted { get; set; } = string.Empty;
}

public sealed class RemoteRowItem
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string AuthText { get; set; } = string.Empty;
    public string ParamsSummary { get; set; } = string.Empty;
    public RcloneRemoteDetail RawRemote { get; set; } = new();
}

public sealed class ProviderListItem
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public RcloneProviderInfo RawProvider { get; set; } = new();
    public override string ToString() => $"{Name} — {Description}";
}

public sealed record UiOption<T>(T Value, string Label)
{
    public override string ToString() => Label;

    public static IReadOnlyList<UiOption<RcloneOperation>> CreateOperationOptions() => new[]
    {
        new UiOption<RcloneOperation>(RcloneOperation.Sync, "Sincronizar (deixar destino igual à pasta de origem)"),
        new UiOption<RcloneOperation>(RcloneOperation.Copy, "Copiar novidades (sem apagar arquivos no destino)"),
        new UiOption<RcloneOperation>(RcloneOperation.Move, "Mover arquivos (apagar da origem após enviar)"),
        new UiOption<RcloneOperation>(RcloneOperation.Check, "Apenas verificar arquivos")
    };
}

public partial class MainWindow : Window
{
    private enum SimpleScheduleMode
    {
        ManualOnly,
        EveryInterval,
        DailyAtTime,
        WeeklyDays,
        OnStartup
    }

    private sealed record DestTargetOption(bool IsLocal, string RemoteName, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly ServiceApiClient _api = new(5580);
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _updateCheckTimer;
    private readonly TrayIconHelper _trayIcon;
    private readonly OverlayHost _overlayHost;

    private List<SyncJob> _cachedJobs = new();
    private List<string> _cachedRemotes = new();
    private List<RcloneRemoteDetail> _cachedRemoteDetails = new();
    private List<RcloneProviderInfo> _allProviders = new();
    private readonly Dictionary<string, Func<string>> _activeRemoteOptionReaders = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _currentRemoteParams = new(StringComparer.OrdinalIgnoreCase);
    private bool _isEditingExistingRemote;
    private bool _isUpdatingRemotesGrid;
    private bool _initialProvidersLoaded;
    private ServiceStatusDto? _lastStatus;
    private string? _selectedJobId;
    private string _editingJobId = string.Empty;
    private bool _isPopulatingJobForm;
    private bool _isClosingForReal;
    private bool _isUpdatingJobsGrid;
    private bool _initialFilterLoaded;
    private bool _initialRemotesLoaded;
    private bool _initialReleaseFieldsLoaded;
    private bool _isUpdatingCloseBehaviorUi;
    private bool _isClosePromptOpen;
    private bool _keepServiceOnClose = true;
    private bool _relaunchUiAfterUpdate;
    private bool _updateApplierLaunched;
    private bool _updateCheckInFlight;
    private PendingAppUpdateInfo? _stagedUpdate;
    private bool _pollInFlight;

    public MainWindow()
    {
        InitializeComponent();
        _overlayHost = new OverlayHost(OverlayContainer);

        InitializeJobFormOptions();
        InitializeCloseBehaviorUi();

        _trayIcon = new TrayIconHelper(
            onOpenWindow: () => Dispatcher.Invoke(ShowFromTray),
            onRunPrimaryJob: () => Dispatcher.Invoke(RunPrimaryJobFromTray),
            onStopJob: () => Dispatcher.Invoke(StopActiveJobFromTray),
            onExitUiOnly: () => Dispatcher.Invoke(ExitUiKeepingService),
            onExitAll: () => Dispatcher.Invoke(ExitUiAndService));

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _pollTimer.Tick += async (_, _) => await PollServiceAsync();

        _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(4) };
        _updateCheckTimer.Tick += async (_, _) => await CheckAppUpdateInBackgroundAsync();

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();

        // Se havia uma atualização já baixada da sessão anterior, aplica agora ao reabrir (caso nenhum backup esteja ativo)
        if (!AppAutoUpdater.IsDevelopmentBuild(workDir))
        {
            var pendingStartup = AppAutoUpdater.TryGetValidPendingUpdate(workDir);
            if (pendingStartup != null)
            {
                var busyWithActiveBackup = false;
                try
                {
                    var st = await _api.GetStatusAsync();
                    busyWithActiveBackup = st?.ActiveJob != null;
                }
                catch
                {
                    busyWithActiveBackup = false;
                }

                if (!busyWithActiveBackup)
                {
                    FooterInfoText.Text = $"Aplicando atualização {pendingStartup.VersionTag}...";
                    if (AppAutoUpdater.LaunchDetachedUpdateApplier(
                            workDir,
                            pendingStartup,
                            relaunchUiAfterUpdate: true,
                            restartBackgroundServiceIfNotRelaunchingUi: false))
                    {
                        _updateApplierLaunched = true;
                        _isClosingForReal = true;
                        Close();
                        return;
                    }
                }
                else
                {
                    SetStagedUpdateReady(pendingStartup);
                }
            }
        }

        var store = new AppConfigStore(workDir);
        var cfg = store.LoadOrCreateDefault();

        if (!cfg.FirstRunCompleted)
        {
            FooterInfoText.Text = "Primeira execução detectada — abrindo Assistente Inicial...";
            var wizard = new FirstRunWizardWindow(workDir);
            await _overlayHost.ShowAsync(wizard);
            cfg = store.LoadOrCreateDefault();
        }

        PinnedVersionBox.Text = cfg.PinnedRcloneVersion;
        LockVersionCheck.IsChecked = cfg.LockRcloneVersion;
        _initialReleaseFieldsLoaded = true;

        FooterInfoText.Text = "Verificando release do Rclone...";
        var check = await RcloneReleaseManager.CheckInstallationAsync(cfg, workDir);
        ReleaseStatusText.Text = $"{check.StatusMessage} ({check.ExecutablePath})";

        if (check.NeedsDownload)
        {
            FooterInfoText.Text = check.StatusMessage;
            var setupView = new RcloneSetupWindow(workDir, check.PinnedVersion, check.StatusMessage);

            if (await _overlayHost.ShowAsync(setupView) == true && !string.IsNullOrWhiteSpace(setupView.DownloadedExecutablePath))
            {
                cfg.RcloneExecutablePath = setupView.DownloadedExecutablePath;
                if (string.IsNullOrWhiteSpace(cfg.RcloneConfigPath))
                    cfg.RcloneConfigPath = AppConfigStore.DetectRcloneConfigPath(cfg.RcloneExecutablePath);
                store.Save(cfg);
                ReleaseStatusText.Text = $"Rclone {check.PinnedVersion} baixado e travado em '{cfg.RcloneExecutablePath}'.";
            }
            else
            {
                FooterInfoText.Text = "Provisionamento do Rclone cancelado. Você pode baixar/travar na aba 'Logs & Serviço'.";
            }
        }

        FooterInfoText.Text = "Verificando serviço em segundo plano...";
        var ok = await ServiceLauncher.EnsureServiceRunningAsync(_api);
        if (!ok)
        {
            ServiceStatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
            ServiceStatusText.Text = "Serviço offline (clique em 'Iniciar / Reconectar Serviço' na aba Logs & Serviço)";
            FooterInfoText.Text = "Não foi possível iniciar automaticamente o RcloneManager.Service.";
        }
        else
        {
            await PollServiceAsync();
            await LoadFilterAnalysisAsync();
            await RefreshRemotesAndAboutAsync();
            await RefreshRemotesTabDataAsync();
            await RefreshLogsAsync();
        }

        _pollTimer.Start();
        _updateCheckTimer.Start();
        _ = RunDelayedInitialUpdateCheckAsync();
    }

    private async Task RunDelayedInitialUpdateCheckAsync()
    {
        await Task.Delay(3500);
        await CheckAppUpdateInBackgroundAsync();
    }

    private async Task PollServiceAsync()
    {
        if (_pollInFlight)
            return;

        _pollInFlight = true;
        try
        {
            var status = await _api.GetStatusAsync();
            if (status == null)
            {
                _lastStatus = null;
                MarkServiceOffline();
                return;
            }

            _lastStatus = status;

            ServiceStatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
            ServiceStatusText.Text = $"Serviço Ativo (desde {status.ServiceStartedAt:HH:mm:ss})";

            if (status.LockRcloneVersion)
            {
                if (status.IsRcloneVersionValid)
                {
                    RcloneVersionText.Text = $"rclone {status.RcloneVersion} [Trava Ativa]";
                    RcloneVersionText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                }
                else
                {
                    RcloneVersionText.Text = $"rclone {status.RcloneVersion} [DIVERGENTE de {status.PinnedRcloneVersion}]";
                    RcloneVersionText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
                }
            }
            else
            {
                RcloneVersionText.Text = $"rclone {status.RcloneVersion} [Sem Trava]";
                RcloneVersionText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184));
            }

            ReleaseStatusText.Text = $"Instalado: {status.RcloneVersion} | Travado em: {status.PinnedRcloneVersion} ({(status.LockRcloneVersion ? "Trava Ativa" : "Livre")}) | Caminho: {status.RcloneExecutablePath}";
            if (!_initialReleaseFieldsLoaded && !PinnedVersionBox.IsKeyboardFocusWithin)
            {
                PinnedVersionBox.Text = status.PinnedRcloneVersion;
                LockVersionCheck.IsChecked = status.LockRcloneVersion;
                _initialReleaseFieldsLoaded = true;
            }

            RcdStatusText.Text = status.RcDaemonRunning
                ? $"Daemon RC: Ativo (porta {status.RcDaemonPort})"
                : "Daemon RC: Desligado (CLI sob demanda)";
            ToggleRcdButton.Content = status.RcDaemonRunning ? "Parar Daemon RC" : "Iniciar Daemon RC";

            WindowsStartupCheck.IsChecked = status.StartupWithWindowsEnabled;
            ServiceAdminStatusText.Text = status.WindowsServiceRunning
                ? "WinService Nativo: Ativo (porta 5580)"
                : (status.WindowsServiceInstalled
                    ? "WinService instalado (parado) | Sessão local ativa"
                    : "Modo Sessão do Usuário ativo (porta 5580)");
            VaultStatusText.Text = status.EncryptionKeyActive
                ? "Proteção local: Ativa"
                : "Proteção local: Automática";
            SyncBwComboSelection(status.CurrentBandwidthLimit);

            UpdateDashboard(status);
            UpdateJobsList(status.Jobs);

            if (!_initialFilterLoaded)
                await LoadFilterAnalysisAsync();

            if (!_initialRemotesLoaded)
                await RefreshRemotesAndAboutAsync();

            if (!_initialProvidersLoaded && MainTabs.SelectedItem == RemotesTab)
                await RefreshRemotesTabDataAsync();

            if (MainTabs.SelectedItem == LogsTab)
                await RefreshLogsAsync();
        }
        catch
        {
            MarkServiceOffline();
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    private void MarkServiceOffline()
    {
        ServiceStatusDot.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
        ServiceStatusText.Text = "Serviço Offline — aguardando reconexão";
        RcdStatusText.Text = "Daemon RC: indisponível";
        StopActiveButton.IsEnabled = false;
        _trayIcon.UpdateStatusText("RcloneManager - Serviço Offline");
    }

    private void UpdateDashboard(ServiceStatusDto status)
    {
        if (status.ActiveJob != null)
        {
            var modeTag = status.ActiveJob.IsDryRun ? " [DRY-RUN]" : string.Empty;
            ActiveJobTitleText.Text = $"{status.ActiveJob.JobName}{modeTag}";
            ActiveStrategyText.Text = status.ActiveJob.StrategyReason;
            StopActiveButton.IsEnabled = true;
            _trayIcon.UpdateStatusText($"RcloneManager: Executando {status.ActiveJob.JobName}");
        }
        else
        {
            ActiveJobTitleText.Text = "Nenhuma tarefa em execução";
            var nextJob = status.Jobs
                .Where(j => j.Enabled && j.NextRunAt.HasValue)
                .OrderBy(j => j.NextRunAt)
                .FirstOrDefault();

            ActiveStrategyText.Text = nextJob != null
                ? $"Próximo agendamento: '{nextJob.Name}' às {nextJob.NextRunAt:dd/MM HH:mm}"
                : "Modo Híbrido pronto (CLI + RC)";
            StopActiveButton.IsEnabled = false;
            _trayIcon.UpdateStatusText("RcloneManager - Serviço Ativo");
        }

        var stats = status.LiveStats;
        if (stats != null)
        {
            SpeedValueText.Text = FormatSpeed(stats.Speed);
            OverallProgressBar.Value = stats.ProgressPercent;
            ProgressValueText.Text = $"{stats.ProgressPercent:0.0}% ({FormatBytes(stats.Bytes)} / {FormatBytes(stats.TotalBytes)})";
            TransfersValueText.Text = $"{stats.Transfers} / {stats.TotalTransfers} transf. | {stats.Checks} verif.";
            var etaStr = stats.Eta.HasValue ? FormatDuration(stats.Eta.Value) : "--";
            ErrorsEtaText.Text = $"Erros: {stats.Errors} | ETA: {etaStr}";

            var rows = (stats.Transferring ?? new List<RcloneTransferItem>())
                .Select(t => new TransferRowItem
                {
                    FileName = t.Name,
                    SizeFormatted = FormatBytes(t.Size),
                    TransferredFormatted = FormatBytes(t.Bytes),
                    PercentText = $"{t.Percentage}%",
                    SpeedFormatted = FormatSpeed(t.Speed),
                    EtaFormatted = t.Eta.HasValue ? FormatDuration(t.Eta.Value) : "--"
                })
                .ToList();

            TransferringGrid.ItemsSource = rows;
        }
        else
        {
            SpeedValueText.Text = "0 B/s";
            OverallProgressBar.Value = 0;
            ProgressValueText.Text = "0% (0 B / 0 B)";
            TransfersValueText.Text = "0 / 0 transf.";
            ErrorsEtaText.Text = "Erros: 0 | ETA: --";
            TransferringGrid.ItemsSource = null;
        }
    }

    private void UpdateJobsList(List<SyncJob> jobs)
    {
        _cachedJobs = jobs;
        var rows = jobs.Select(j => new JobRowItem
        {
            Id = j.Id,
            Name = j.Enabled ? j.Name : $"[Desativada] {j.Name}",
            StatusText = FormatJobStatus(j.LastStatus),
            NextRunText = j.NextRunAt.HasValue ? j.NextRunAt.Value.ToString("dd/MM HH:mm") : "Manual",
            LastRunText = j.LastRunAt.HasValue ? j.LastRunAt.Value.ToString("dd/MM HH:mm") : "Nunca",
            RawJob = j
        }).ToList();

        _isUpdatingJobsGrid = true;
        try
        {
            var previousId = _selectedJobId;
            JobsGrid.ItemsSource = rows;

            if (!string.IsNullOrWhiteSpace(previousId))
            {
                var match = rows.FirstOrDefault(r => r.Id == previousId);
                if (match != null)
                {
                    JobsGrid.SelectedItem = match;
                }
            }
            else if (rows.Count > 0)
            {
                JobsGrid.SelectedIndex = 0;
                PopulateJobForm(rows[0].RawJob);
            }
        }
        finally
        {
            _isUpdatingJobsGrid = false;
        }
    }

    private static string FormatJobStatus(JobRunStatus status) => status switch
    {
        JobRunStatus.Idle => "Pronta",
        JobRunStatus.Running => "Executando",
        JobRunStatus.Success => "Concluída",
        JobRunStatus.Failed => "Falhou",
        JobRunStatus.Cancelled => "Cancelada",
        _ => status.ToString()
    };

    private void InitializeJobFormOptions()
    {
        JobOperationCombo.ItemsSource = UiOption<RcloneOperation>.CreateOperationOptions();

        JobStrategyCombo.ItemsSource = new[]
        {
            new UiOption<ExecutionStrategy>(ExecutionStrategy.HybridAuto, "Automático (Recomendado)"),
            new UiOption<ExecutionStrategy>(ExecutionStrategy.CliWithRcMonitoring, "Monitoramento ao vivo"),
            new UiOption<ExecutionStrategy>(ExecutionStrategy.RcDaemonApi, "Serviço em segundo plano"),
            new UiOption<ExecutionStrategy>(ExecutionStrategy.PureCli, "Modo simples")
        };

        JobFilterModeCombo.ItemsSource = new[]
        {
            new UiOption<FilterRuleMode>(FilterRuleMode.AutoDetect, "Detectar automaticamente (Recomendado)"),
            new UiOption<FilterRuleMode>(FilterRuleMode.FilterFrom, "Regras de inclusão e exclusão"),
            new UiOption<FilterRuleMode>(FilterRuleMode.ExcludeFrom, "Lista simples de exclusão")
        };

        JobScheduleModeCombo.ItemsSource = new[]
        {
            new UiOption<SimpleScheduleMode>(SimpleScheduleMode.EveryInterval, "Automaticamente a cada intervalo de tempo"),
            new UiOption<SimpleScheduleMode>(SimpleScheduleMode.DailyAtTime, "Todos os dias em um horário fixo"),
            new UiOption<SimpleScheduleMode>(SimpleScheduleMode.WeeklyDays, "Em dias específicos da semana"),
            new UiOption<SimpleScheduleMode>(SimpleScheduleMode.OnStartup, "Ao ligar o computador / iniciar serviço"),
            new UiOption<SimpleScheduleMode>(SimpleScheduleMode.ManualOnly, "Somente manual (quando eu clicar em Executar)")
        };

        JobIntervalPresetCombo.ItemsSource = new[]
        {
            new UiOption<int>(15, "15 minutos"),
            new UiOption<int>(30, "30 minutos"),
            new UiOption<int>(60, "1 hora"),
            new UiOption<int>(120, "2 horas"),
            new UiOption<int>(240, "4 horas"),
            new UiOption<int>(360, "6 horas"),
            new UiOption<int>(720, "12 horas"),
            new UiOption<int>(-1, "Outro intervalo personalizado...")
        };

        JobMaxSizePresetCombo.ItemsSource = new[]
        {
            new UiOption<string>(string.Empty, "Sem limite (enviar todos os tamanhos)"),
            new UiOption<string>("10M", "Até 10 MB"),
            new UiOption<string>("50M", "Até 50 MB"),
            new UiOption<string>("100M", "Até 100 MB"),
            new UiOption<string>("500M", "Até 500 MB"),
            new UiOption<string>("1G", "Até 1 GB"),
            new UiOption<string>("5G", "Até 5 GB"),
            new UiOption<string>("*", "Outro valor personalizado...")
        };

        JobBwPresetCombo.ItemsSource = new[]
        {
            new UiOption<string>(string.Empty, "Sem limite (velocidade total)"),
            new UiOption<string>("1M", "1 MB/s"),
            new UiOption<string>("2M", "2 MB/s"),
            new UiOption<string>("5M", "5 MB/s"),
            new UiOption<string>("10M", "10 MB/s"),
            new UiOption<string>("20M", "20 MB/s"),
            new UiOption<string>("50M", "50 MB/s"),
            new UiOption<string>("*", "Outro valor personalizado...")
        };

        PopulateDestTargetCombo();
        SelectOption(JobOperationCombo, RcloneOperation.Sync);
        SelectOption(JobStrategyCombo, ExecutionStrategy.HybridAuto);
        SelectOption(JobFilterModeCombo, FilterRuleMode.AutoDetect);
        SelectOption(JobScheduleModeCombo, SimpleScheduleMode.EveryInterval);
        SelectOption(JobIntervalPresetCombo, 60);
        SelectOption(JobMaxSizePresetCombo, string.Empty);
        SelectOption(JobBwPresetCombo, string.Empty);
        UpdateSchedulePanelsVisibility();
        UpdateOperationHint();
    }

    private static void SelectOption<T>(System.Windows.Controls.ComboBox combo, T value)
    {
        if (combo.ItemsSource is not IEnumerable<UiOption<T>> items)
            return;

        var match = items.FirstOrDefault(i => EqualityComparer<T>.Default.Equals(i.Value, value));
        if (match != null)
            combo.SelectedItem = match;
    }

    private static T GetSelectedOption<T>(System.Windows.Controls.ComboBox combo, T fallback)
    {
        return combo.SelectedItem is UiOption<T> opt ? opt.Value : fallback;
    }

    private void PopulateDestTargetCombo(string? ensureRemoteName = null)
    {
        var remoteNames = new List<string>();
        foreach (var raw in _cachedRemotes)
        {
            var clean = raw.Trim().TrimEnd(':');
            if (!string.IsNullOrWhiteSpace(clean) && !remoteNames.Contains(clean, StringComparer.OrdinalIgnoreCase))
                remoteNames.Add(clean);
        }

        if (!string.IsNullOrWhiteSpace(ensureRemoteName))
        {
            var cleanEnsure = ensureRemoteName.Trim().TrimEnd(':');
            if (!string.IsNullOrWhiteSpace(cleanEnsure) && !remoteNames.Contains(cleanEnsure, StringComparer.OrdinalIgnoreCase))
                remoteNames.Add(cleanEnsure);
        }

        var options = remoteNames
            .Select(r => new DestTargetOption(false, r, $"☁ Nuvem: {r}"))
            .ToList();
        options.Add(new DestTargetOption(true, string.Empty, "📁 Pasta neste computador / Rede"));

        var previous = JobDestTargetCombo.SelectedItem as DestTargetOption;
        JobDestTargetCombo.ItemsSource = options;

        if (previous != null)
        {
            var match = previous.IsLocal
                ? options.FirstOrDefault(o => o.IsLocal)
                : options.FirstOrDefault(o => !o.IsLocal && o.RemoteName.Equals(previous.RemoteName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                JobDestTargetCombo.SelectedItem = match;
                return;
            }
        }

        JobDestTargetCombo.SelectedIndex = 0;
    }

    private static bool TrySplitRemoteDestination(string path, out string remoteName, out string subpath)
    {
        remoteName = string.Empty;
        subpath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
            return false;

        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("/", StringComparison.Ordinal) ||
            trimmed.StartsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        // Letra de unidade local do Windows (ex: C:\ ou D:/)
        if (trimmed.Length >= 2 &&
            char.IsAsciiLetter(trimmed[0]) &&
            trimmed[1] == ':' &&
            (trimmed.Length == 2 || trimmed[2] == '\\' || trimmed[2] == '/'))
        {
            return false;
        }

        var colonIdx = trimmed.IndexOf(':');
        if (colonIdx <= 0)
            return false;

        remoteName = trimmed[..colonIdx].Trim();
        subpath = trimmed[(colonIdx + 1)..];
        return !string.IsNullOrWhiteSpace(remoteName);
    }

    private void SetDestinationUi(string destinationPath)
    {
        if (TrySplitRemoteDestination(destinationPath, out var remoteName, out var subpath))
        {
            PopulateDestTargetCombo(remoteName);
            if (JobDestTargetCombo.ItemsSource is IEnumerable<DestTargetOption> opts)
            {
                JobDestTargetCombo.SelectedItem = opts.FirstOrDefault(
                    o => !o.IsLocal && o.RemoteName.Equals(remoteName, StringComparison.OrdinalIgnoreCase));
            }
            JobDestBox.Text = subpath;
        }
        else if (string.IsNullOrWhiteSpace(destinationPath) && _cachedRemotes.Count > 0)
        {
            var firstRemote = _cachedRemotes[0].Trim().TrimEnd(':');
            PopulateDestTargetCombo(firstRemote);
            if (JobDestTargetCombo.ItemsSource is IEnumerable<DestTargetOption> opts)
            {
                JobDestTargetCombo.SelectedItem = opts.FirstOrDefault(
                    o => !o.IsLocal && o.RemoteName.Equals(firstRemote, StringComparison.OrdinalIgnoreCase));
            }
            JobDestBox.Text = string.Empty;
        }
        else
        {
            PopulateDestTargetCombo();
            if (JobDestTargetCombo.ItemsSource is IEnumerable<DestTargetOption> opts)
            {
                JobDestTargetCombo.SelectedItem = opts.FirstOrDefault(o => o.IsLocal);
            }
            JobDestBox.Text = destinationPath;
        }

        UpdateDestinationControlsState();
    }

    private string BuildDestinationFromUi()
    {
        var text = JobDestBox.Text.Trim();
        if (JobDestTargetCombo.SelectedItem is DestTargetOption { IsLocal: false } cloudTarget)
        {
            var prefix = cloudTarget.RemoteName + ":";
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                text = text[prefix.Length..];
            text = text.TrimStart(':');
            return string.IsNullOrEmpty(text) ? prefix : $"{prefix}{text}";
        }

        return text;
    }

    private void UpdateDestinationControlsState()
    {
        if (!IsLoaded && JobDestSubfolderLabel == null)
            return;

        var isLocal = (JobDestTargetCombo.SelectedItem as DestTargetOption)?.IsLocal == true;
        JobDestSubfolderLabel.Text = isLocal
            ? "Caminho da pasta de destino"
            : "Pasta dentro da nuvem (ex: BackupPC)";
        BrowseDestFolderButton.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;

        var finalDest = BuildDestinationFromUi();
        JobDestPreviewText.Text = string.IsNullOrWhiteSpace(finalDest)
            ? string.Empty
            : $"Destino configurado: {finalDest}";
    }

    private void JobOperationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateOperationHint();
    }

    private void UpdateOperationHint()
    {
        if (JobOperationHintText == null)
            return;

        var op = GetSelectedOption(JobOperationCombo, RcloneOperation.Sync);
        JobOperationHintText.Text = op switch
        {
            RcloneOperation.Sync => "Mantém o destino igual à pasta de origem (arquivos apagados no PC também saem do destino).",
            RcloneOperation.Copy => "Seguro para acumular backups: envia arquivos novos/alterados sem apagar nada no destino.",
            RcloneOperation.Move => "Envia os arquivos para o destino e apaga os originais da pasta do computador após concluir.",
            RcloneOperation.Check => "Apenas compara se os arquivos de origem e destino estão idênticos, sem alterar nada.",
            _ => string.Empty
        };
    }

    private void JobDestTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingJobForm)
            return;
        UpdateDestinationControlsState();
    }

    private void JobDestBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isPopulatingJobForm)
            return;
        UpdateDestinationControlsState();
    }

    private void BrowseDestFolderButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Selecione a pasta de destino para salvar os arquivos",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(JobDestBox.Text) ? JobDestBox.Text : string.Empty
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            JobDestBox.Text = dialog.SelectedPath;
    }

    private void JobScheduleModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSchedulePanelsVisibility();
    }

    private void JobIntervalPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomIntervalPanel == null)
            return;

        var preset = GetSelectedOption(JobIntervalPresetCombo, 60);
        if (preset > 0)
        {
            CustomIntervalPanel.Visibility = Visibility.Collapsed;
            if (!_isPopulatingJobForm)
                JobIntervalBox.Text = preset.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            CustomIntervalPanel.Visibility = Visibility.Visible;
        }
    }

    private void UpdateSchedulePanelsVisibility()
    {
        if (ScheduleIntervalPanel == null)
            return;

        var mode = GetSelectedOption(JobScheduleModeCombo, SimpleScheduleMode.EveryInterval);
        ScheduleIntervalPanel.Visibility = mode == SimpleScheduleMode.EveryInterval ? Visibility.Visible : Visibility.Collapsed;
        ScheduleTimePanel.Visibility = mode is SimpleScheduleMode.DailyAtTime or SimpleScheduleMode.WeeklyDays
            ? Visibility.Visible
            : Visibility.Collapsed;
        ScheduleDaysPanel.Visibility = mode == SimpleScheduleMode.WeeklyDays ? Visibility.Visible : Visibility.Collapsed;
        JobRetryCheck.Visibility = mode == SimpleScheduleMode.ManualOnly ? Visibility.Collapsed : Visibility.Visible;
    }

    private void JobMaxSizePresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JobMaxSizeBox == null)
            return;

        var val = GetSelectedOption(JobMaxSizePresetCombo, string.Empty);
        if (val == "*")
        {
            JobMaxSizeBox.Visibility = Visibility.Visible;
        }
        else
        {
            JobMaxSizeBox.Visibility = Visibility.Collapsed;
            if (!_isPopulatingJobForm)
                JobMaxSizeBox.Text = val;
        }
    }

    private void JobBwPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JobBwLimitBox == null)
            return;

        var val = GetSelectedOption(JobBwPresetCombo, string.Empty);
        if (val == "*")
        {
            JobBwLimitBox.Visibility = Visibility.Visible;
        }
        else
        {
            JobBwLimitBox.Visibility = Visibility.Collapsed;
            if (!_isPopulatingJobForm)
                JobBwLimitBox.Text = val;
        }
    }

    private static void SetPresetStringCombo(System.Windows.Controls.ComboBox combo, System.Windows.Controls.TextBox customBox, string rawValue)
    {
        var trimmed = rawValue?.Trim() ?? string.Empty;
        customBox.Text = trimmed;

        if (combo.ItemsSource is IEnumerable<UiOption<string>> opts)
        {
            var exact = opts.FirstOrDefault(o => o.Value != "*" && string.Equals(o.Value, trimmed, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                combo.SelectedItem = exact;
                customBox.Visibility = Visibility.Collapsed;
                return;
            }

            combo.SelectedItem = opts.FirstOrDefault(o => o.Value == "*");
            customBox.Visibility = Visibility.Visible;
        }
    }

    private void OpenFilterInEditorButton_Click(object sender, RoutedEventArgs e)
    {
        var filterPath = JobFilterPathBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(filterPath))
            FilterFilePathBox.Text = filterPath;

        MainTabs.SelectedItem = FiltersTab;
        _ = LoadFilterAnalysisAsync();
    }

    private void JobsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingJobsGrid)
            return;

        if (JobsGrid.SelectedItem is JobRowItem row)
        {
            if (_selectedJobId != row.Id)
            {
                PopulateJobForm(row.RawJob);
            }
        }
    }

    private void PopulateJobForm(SyncJob job)
    {
        _isPopulatingJobForm = true;
        try
        {
            _selectedJobId = job.Id;
            _editingJobId = job.Id;
            JobNameBox.Text = job.Name;
            JobEnabledCheck.IsChecked = job.Enabled;

            SelectOption(JobOperationCombo, job.Operation);
            SelectOption(JobStrategyCombo, job.Strategy);
            SelectOption(JobFilterModeCombo, job.FilterMode);

            JobSourceBox.Text = job.SourcePath;
            SetDestinationUi(job.DestinationPath);

            JobFilterPathBox.Text = job.FilterFilePath;
            JobExcludedRetentionCheck.IsChecked = job.EnableExcludedGraceRetention;
            JobExcludedGraceDaysBox.Text = (job.ExcludedRetentionDays > 0 ? job.ExcludedRetentionDays : 30).ToString(CultureInfo.InvariantCulture);
            SetPresetStringCombo(JobMaxSizePresetCombo, JobMaxSizeBox, job.MaxSize);
            SetPresetStringCombo(JobBwPresetCombo, JobBwLimitBox, job.BandwidthLimit);

            JobLogFileBox.Text = job.LogFilePath;
            JobExtraFlagsBox.Text = job.ExtraFlags;
            JobSkipLinksCheck.IsChecked = job.SkipLinks;
            JobFastListCheck.IsChecked = job.FastList;
            JobCopyLinksCheck.IsChecked = job.CopyLinks;
            JobDryRunCheck.IsChecked = job.DryRun;

            var intervalMins = job.Schedule.IntervalMinutes > 0 ? job.Schedule.IntervalMinutes : 60;
            JobIntervalBox.Text = intervalMins.ToString(CultureInfo.InvariantCulture);
            if (JobIntervalPresetCombo.ItemsSource is IEnumerable<UiOption<int>> intervalOpts)
            {
                var exactPreset = intervalOpts.FirstOrDefault(o => o.Value == intervalMins);
                if (exactPreset != null)
                {
                    JobIntervalPresetCombo.SelectedItem = exactPreset;
                    CustomIntervalPanel.Visibility = Visibility.Collapsed;
                }
                else
                {
                    JobIntervalPresetCombo.SelectedItem = intervalOpts.FirstOrDefault(o => o.Value == -1);
                    CustomIntervalPanel.Visibility = Visibility.Visible;
                }
            }

            var mode = !job.Schedule.IsScheduled
                ? SimpleScheduleMode.ManualOnly
                : job.Schedule.ScheduleType switch
                {
                    ScheduleType.DailyAtTime => SimpleScheduleMode.DailyAtTime,
                    ScheduleType.WeeklyDays => SimpleScheduleMode.WeeklyDays,
                    ScheduleType.OnServiceStart => SimpleScheduleMode.OnStartup,
                    _ => SimpleScheduleMode.EveryInterval
                };
            SelectOption(JobScheduleModeCombo, mode);
            UpdateSchedulePanelsVisibility();

            JobDailyTimeBox.Text = job.Schedule.DailyTime;

            var days = job.Schedule.DaysOfWeek ?? new List<DayOfWeek>();
            DayMonCheck.IsChecked = days.Contains(DayOfWeek.Monday);
            DayTueCheck.IsChecked = days.Contains(DayOfWeek.Tuesday);
            DayWedCheck.IsChecked = days.Contains(DayOfWeek.Wednesday);
            DayThuCheck.IsChecked = days.Contains(DayOfWeek.Thursday);
            DayFriCheck.IsChecked = days.Contains(DayOfWeek.Friday);
            DaySatCheck.IsChecked = days.Contains(DayOfWeek.Saturday);
            DaySunCheck.IsChecked = days.Contains(DayOfWeek.Sunday);

            JobRetryCheck.IsChecked = job.Schedule.RetryOnFailure;
            JobMaxRetriesBox.Text = job.Schedule.MaxRetries.ToString(CultureInfo.InvariantCulture);
            JobRetryDelayBox.Text = job.Schedule.RetryDelayMinutes.ToString(CultureInfo.InvariantCulture);
            UpdateOperationHint();
        }
        finally
        {
            _isPopulatingJobForm = false;
        }
    }

    private SyncJob ReadJobFromForm()
    {
        var days = new List<DayOfWeek>();
        if (DayMonCheck.IsChecked == true) days.Add(DayOfWeek.Monday);
        if (DayTueCheck.IsChecked == true) days.Add(DayOfWeek.Tuesday);
        if (DayWedCheck.IsChecked == true) days.Add(DayOfWeek.Wednesday);
        if (DayThuCheck.IsChecked == true) days.Add(DayOfWeek.Thursday);
        if (DayFriCheck.IsChecked == true) days.Add(DayOfWeek.Friday);
        if (DaySatCheck.IsChecked == true) days.Add(DayOfWeek.Saturday);
        if (DaySunCheck.IsChecked == true) days.Add(DayOfWeek.Sunday);

        var presetInterval = GetSelectedOption(JobIntervalPresetCombo, 60);
        var interval = presetInterval > 0
            ? presetInterval
            : (int.TryParse(JobIntervalBox.Text.Trim(), out var iv) && iv > 0 ? iv : 60);

        var maxRetries = int.TryParse(JobMaxRetriesBox.Text.Trim(), out var mr) && mr >= 0 ? mr : 3;
        var retryDelay = int.TryParse(JobRetryDelayBox.Text.Trim(), out var rd) && rd > 0 ? rd : 5;

        var scheduleMode = GetSelectedOption(JobScheduleModeCombo, SimpleScheduleMode.EveryInterval);
        var isScheduled = scheduleMode != SimpleScheduleMode.ManualOnly;
        var scheduleType = scheduleMode switch
        {
            SimpleScheduleMode.DailyAtTime => ScheduleType.DailyAtTime,
            SimpleScheduleMode.WeeklyDays => ScheduleType.WeeklyDays,
            SimpleScheduleMode.OnStartup => ScheduleType.OnServiceStart,
            _ => ScheduleType.IntervalMinutes
        };

        var maxSizePreset = GetSelectedOption(JobMaxSizePresetCombo, string.Empty);
        var maxSize = maxSizePreset == "*" ? JobMaxSizeBox.Text.Trim() : maxSizePreset;

        var bwPreset = GetSelectedOption(JobBwPresetCombo, string.Empty);
        var bwLimit = bwPreset == "*" ? JobBwLimitBox.Text.Trim() : bwPreset;
        var graceDays = int.TryParse(JobExcludedGraceDaysBox.Text.Trim(), out var gd) && gd > 0 ? gd : 30;

        return new SyncJob
        {
            Id = string.IsNullOrWhiteSpace(_editingJobId) ? Guid.NewGuid().ToString("N")[..8] : _editingJobId.Trim(),
            Name = string.IsNullOrWhiteSpace(JobNameBox.Text) ? "Tarefa Sem Nome" : JobNameBox.Text.Trim(),
            Enabled = JobEnabledCheck.IsChecked == true,
            Operation = GetSelectedOption(JobOperationCombo, RcloneOperation.Sync),
            Strategy = GetSelectedOption(JobStrategyCombo, ExecutionStrategy.HybridAuto),
            SourcePath = JobSourceBox.Text.Trim(),
            DestinationPath = BuildDestinationFromUi(),
            FilterFilePath = JobFilterPathBox.Text.Trim(),
            FilterMode = GetSelectedOption(JobFilterModeCombo, FilterRuleMode.AutoDetect),
            EnableExcludedGraceRetention = JobExcludedRetentionCheck.IsChecked == true,
            ExcludedRetentionDays = graceDays,
            MaxSize = maxSize,
            BandwidthLimit = bwLimit,
            LogFilePath = string.IsNullOrWhiteSpace(JobLogFileBox.Text) ? "rclone_backup.log" : JobLogFileBox.Text.Trim(),
            ExtraFlags = JobExtraFlagsBox.Text.Trim(),
            SkipLinks = JobSkipLinksCheck.IsChecked == true,
            FastList = JobFastListCheck.IsChecked == true,
            CopyLinks = JobCopyLinksCheck.IsChecked == true,
            DryRun = JobDryRunCheck.IsChecked == true,
            Schedule = new JobSchedule
            {
                IsScheduled = isScheduled,
                ScheduleType = scheduleType,
                IntervalMinutes = interval,
                DailyTime = string.IsNullOrWhiteSpace(JobDailyTimeBox.Text) ? "14:00" : JobDailyTimeBox.Text.Trim(),
                DaysOfWeek = days,
                RetryOnFailure = JobRetryCheck.IsChecked == true,
                MaxRetries = maxRetries,
                RetryDelayMinutes = retryDelay
            }
        };
    }

    private void NewJobButton_Click(object sender, RoutedEventArgs e)
    {
        var firstRemote = _cachedRemotes.FirstOrDefault()?.Trim().TrimEnd(':');
        var defaultDest = !string.IsNullOrWhiteSpace(firstRemote)
            ? $"{firstRemote}:BackupDocumentos/"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Backups");

        var fresh = new SyncJob
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = "Backup de Documentos",
            SourcePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            DestinationPath = defaultDest,
            LogFilePath = "rclone_backup.log"
        };
        JobsGrid.SelectedItem = null;
        PopulateJobForm(fresh);
        _selectedJobId = null;
        FooterInfoText.Text = "Escolha as pastas e clique em 'Salvar Tarefa'.";
    }

    private async void SaveJobButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var job = ReadJobFromForm();
            var saved = await _api.SaveJobAsync(job);
            if (saved != null)
            {
                _selectedJobId = saved.Id;
                _editingJobId = saved.Id;
                FooterInfoText.Text = $"Tarefa '{saved.Name}' salva! Próxima execução: {(saved.NextRunAt.HasValue ? saved.NextRunAt.Value.ToString("dd/MM/yyyy HH:mm") : "Manual")}.";
                await PollServiceAsync();
            }
        }
        catch (Exception ex)
        {
            await _overlayHost.ShowAlertAsync("Erro ao salvar tarefa", ex.Message, OverlayAlertKind.Error);
        }
    }

    private async void RunSelectedJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedJobId))
            return;

        try
        {
            await _api.RunJobAsync(_selectedJobId, dryRun: false);
            FooterInfoText.Text = $"Execução iniciada para '{JobNameBox.Text}'.";
            MainTabs.SelectedItem = DashboardTab;
            await PollServiceAsync();
        }
        catch (Exception ex)
        {
            await _overlayHost.ShowAlertAsync("Erro ao iniciar tarefa", ex.Message, OverlayAlertKind.Error);
        }
    }

    private async void DryRunSelectedJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedJobId))
            return;

        try
        {
            await _api.RunJobAsync(_selectedJobId, dryRun: true);
            FooterInfoText.Text = $"Simulação iniciada para '{JobNameBox.Text}'. Nenhum arquivo será alterado.";
            MainTabs.SelectedItem = DashboardTab;
            await PollServiceAsync();
        }
        catch (Exception ex)
        {
            await _overlayHost.ShowAlertAsync("Erro ao simular tarefa", ex.Message, OverlayAlertKind.Error);
        }
    }

    private async void StopSelectedJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedJobId))
            return;

        try
        {
            await _api.StopJobAsync(_selectedJobId);
            FooterInfoText.Text = "Solicitação de parada enviada.";
        }
        catch (Exception ex)
        {
            FooterInfoText.Text = $"Erro ao parar tarefa: {ex.Message}";
        }
    }

    private async void PurgeJobExcludedButton_Click(object sender, RoutedEventArgs e)
    {
        var targetJob = _cachedJobs.FirstOrDefault(j => j.Id == _selectedJobId) ?? _cachedJobs.FirstOrDefault();
        if (targetJob == null)
        {
            await _overlayHost.ShowAlertAsync("Nenhuma tarefa selecionada", "Selecione uma tarefa para expurgar os itens excluídos.");
            return;
        }

        await RunInteractivePurgeExcludedAsync(targetJob);
    }

    private SyncJob? ResolveTargetJobForCurrentFilter()
    {
        var filterBox = FilterFilePathBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(filterBox))
        {
            var fileName = Path.GetFileName(filterBox);
            var byFilter = _cachedJobs.FirstOrDefault(j =>
                !string.IsNullOrWhiteSpace(j.FilterFilePath) &&
                (j.FilterFilePath.Equals(filterBox, StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileName(j.FilterFilePath).Equals(fileName, StringComparison.OrdinalIgnoreCase)));
            if (byFilter != null)
                return byFilter;
        }

        return _cachedJobs.FirstOrDefault(j => j.Id == _selectedJobId && !string.IsNullOrWhiteSpace(j.FilterFilePath))
               ?? _cachedJobs.FirstOrDefault(j => !string.IsNullOrWhiteSpace(j.FilterFilePath))
               ?? _cachedJobs.FirstOrDefault();
    }

    private async void ScanExcludedRetentionButton_Click(object sender, RoutedEventArgs e)
    {
        var targetJob = ResolveTargetJobForCurrentFilter();
        if (targetJob == null)
        {
            await _overlayHost.ShowAlertAsync("Sem tarefa vinculada", "Crie uma tarefa com destino configurado antes de verificar itens na nuvem.");
            return;
        }

        ScanExcludedRetentionButton.IsEnabled = false;
        ExcludedRetentionSummaryText.Text = $"Consultando '{targetJob.DestinationPath}' para localizar itens que entraram na exclusão...";
        try
        {
            var status = await _api.GetExcludedRetentionAsync(targetJob.Id, scan: true);
            if (status != null)
            {
                ExcludedRetentionSummaryText.Text = status.StatusSummary;
                FooterInfoText.Text = status.StatusSummary;
            }
        }
        catch (Exception ex)
        {
            ExcludedRetentionSummaryText.Text = $"Falha ao verificar na nuvem: {ex.Message}";
        }
        finally
        {
            ScanExcludedRetentionButton.IsEnabled = true;
        }
    }

    private async void PurgeExcludedFromFilterTabButton_Click(object sender, RoutedEventArgs e)
    {
        var targetJob = ResolveTargetJobForCurrentFilter();
        if (targetJob == null)
        {
            await _overlayHost.ShowAlertAsync("Sem tarefa vinculada", "Configure uma tarefa antes de expurgar itens excluídos.");
            return;
        }

        await RunInteractivePurgeExcludedAsync(targetJob);
    }

    private async Task RunInteractivePurgeExcludedAsync(SyncJob job)
    {
        if (string.IsNullOrWhiteSpace(job.FilterFilePath))
        {
            await _overlayHost.ShowAlertAsync(
                "Sem Lista de Exclusão",
                $"A tarefa '{job.Name}' não tem um arquivo de filtro configurado.");
            return;
        }

        PurgeJobExcludedButton.IsEnabled = false;
        PurgeExcludedFromFilterTabButton.IsEnabled = false;
        FooterInfoText.Text = $"Verificando itens excluídos existentes em '{job.DestinationPath}'...";
        ExcludedRetentionSummaryText.Text = $"Inspecionando '{job.DestinationPath}' antes do expurgo...";

        try
        {
            var status = await _api.GetExcludedRetentionAsync(job.Id, scan: true);
            if (status == null || status.TotalTrackedFiles == 0)
            {
                var cleanMsg = $"Nenhum item bloqueado pelo filtro foi encontrado em '{job.DestinationPath}'. O destino já está limpo!";
                ExcludedRetentionSummaryText.Text = cleanMsg;
                FooterInfoText.Text = cleanMsg;
                await _overlayHost.ShowAlertAsync("Destino Já Limpo", cleanMsg);
                return;
            }

            ExcludedRetentionSummaryText.Text = status.StatusSummary;
            var folderLines = status.TopFolders.Count > 0
                ? string.Join("\n", status.TopFolders.Select(f => $"  • {f.FolderName}: {f.FileCount} arquivo(s) (carência restante: {f.DaysRemaining} dia(s))"))
                : $"  • {status.TotalTrackedFiles} arquivo(s)";

            var confirm = await _overlayHost.ShowConfirmAsync(
                "Expurgar Excluídos na Hora",
                $"Foram encontrados {status.TotalTrackedFiles} arquivo(s) já enviados em '{job.DestinationPath}' que hoje estão na lista de exclusão:\n\n" +
                $"{folderLines}\n\n" +
                $"Pela regra normal eles teriam até {status.GraceDays} dia(s) de carência. Deseja apagá-los do destino IMEDIATAMENTE?",
                confirmText: "Expurgar Agora",
                cancelText: "Manter Carência",
                isDanger: true);

            if (!confirm)
            {
                FooterInfoText.Text = "Expurgo cancelado — itens mantidos no período de carência.";
                return;
            }

            FooterInfoText.Text = $"Expurgando {status.TotalTrackedFiles} arquivo(s) excluído(s) de '{job.DestinationPath}'...";
            ExcludedRetentionSummaryText.Text = $"Removendo {status.TotalTrackedFiles} arquivo(s) excluído(s) na nuvem...";

            var purgeRes = await _api.PurgeExcludedNowAsync(job.Id);
            if (purgeRes is { Success: true })
            {
                ExcludedRetentionSummaryText.Text = purgeRes.Message;
                FooterInfoText.Text = purgeRes.Message;
                await _overlayHost.ShowAlertAsync("Expurgo Concluído", purgeRes.Message);
            }
            else
            {
                var err = purgeRes?.Message ?? "Não foi possível concluir o expurgo.";
                ExcludedRetentionSummaryText.Text = err;
                await _overlayHost.ShowAlertAsync("Falha no Expurgo", err, OverlayAlertKind.Error);
            }
        }
        catch (Exception ex)
        {
            ExcludedRetentionSummaryText.Text = $"Erro durante expurgo: {ex.Message}";
            await _overlayHost.ShowAlertAsync("Erro no Expurgo", ex.Message, OverlayAlertKind.Error);
        }
        finally
        {
            PurgeJobExcludedButton.IsEnabled = true;
            PurgeExcludedFromFilterTabButton.IsEnabled = true;
        }
    }

    private async void DeleteSelectedJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedJobId))
            return;

        var confirm = await _overlayHost.ShowConfirmAsync(
            "Excluir Tarefa",
            $"Deseja excluir a tarefa '{JobNameBox.Text}'?",
            confirmText: "Excluir",
            cancelText: "Cancelar",
            isDanger: true);

        if (!confirm)
            return;

        try
        {
            await _api.DeleteJobAsync(_selectedJobId);
            _selectedJobId = null;
            await PollServiceAsync();
            FooterInfoText.Text = "Tarefa removida.";
        }
        catch (Exception ex)
        {
            await _overlayHost.ShowAlertAsync("Erro ao excluir tarefa", ex.Message, OverlayAlertKind.Error);
        }
    }

    private async void QuickRunPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        var primary = _cachedJobs.FirstOrDefault(j => j.Enabled) ?? _cachedJobs.FirstOrDefault();
        if (primary == null)
        {
            await _overlayHost.ShowAlertAsync("Nenhuma tarefa configurada", "Crie uma tarefa na aba 'Tarefas & Agendador' antes de iniciar um backup.");
            return;
        }

        try
        {
            await _api.RunJobAsync(primary.Id, dryRun: false);
            FooterInfoText.Text = $"Backup '{primary.Name}' iniciado em 2º plano!";
            await PollServiceAsync();
        }
        catch (Exception ex)
        {
            await _overlayHost.ShowAlertAsync("Erro ao executar tarefa", ex.Message, OverlayAlertKind.Error);
        }
    }

    private async void StopActiveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var status = await _api.GetStatusAsync();
            if (status?.ActiveJob != null)
            {
                await _api.StopJobAsync(status.ActiveJob.JobId);
                FooterInfoText.Text = $"Parada solicitada para '{status.ActiveJob.JobName}'.";
            }
        }
        catch (Exception ex)
        {
            FooterInfoText.Text = $"Erro ao parar: {ex.Message}";
        }
    }

    private async void ToggleRcdButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var resp = await _api.ToggleRcDaemonAsync();
            FooterInfoText.Text = resp is { Running: true }
                ? $"Daemon rclone rcd iniciado na porta {resp.Port}."
                : "Daemon rclone rcd desligado.";
            await PollServiceAsync();
        }
        catch (Exception ex)
        {
            await _overlayHost.ShowAlertAsync("Erro no serviço", $"Erro ao alternar daemon RC: {ex.Message}", OverlayAlertKind.Error);
        }
    }

    private async void ApplyBwButton_Click(object sender, RoutedEventArgs e)
    {
        var item = BwLimitCombo.SelectedItem as ComboBoxItem;
        var rate = item?.Tag?.ToString() ?? item?.Content?.ToString() ?? "off";
        try
        {
            await _api.SetBandwidthLimitAsync(rate);
            FooterInfoText.Text = $"Limite de velocidade ajustado para '{item?.Content ?? rate}'.";
        }
        catch (Exception ex)
        {
            await _overlayHost.ShowAlertAsync("Erro ao limitar velocidade", ex.Message, OverlayAlertKind.Error);
        }
    }

    private async void RefreshRemotesButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRemotesAndAboutAsync();
    }

    private async void RefreshDestRemotesButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRemotesAndAboutAsync();
        PopulateDestTargetCombo();
        UpdateDestinationControlsState();
        FooterInfoText.Text = _cachedRemotes.Count > 0
            ? $"Serviços na nuvem atualizados ({string.Join(", ", _cachedRemotes)})."
            : "Nenhum serviço na nuvem configurado ainda (você pode criar um na aba Remotes ou salvar em pasta local).";
    }

    private async Task RefreshRemotesAndAboutAsync()
    {
        try
        {
            _initialRemotesLoaded = true;
            var remResp = await _api.GetRemotesAsync();
            if (remResp != null)
            {
                _cachedRemotes = remResp.Remotes;
                PopulateDestTargetCombo();
                UpdateDestinationControlsState();
                RemotesListText.Text = _cachedRemotes.Count > 0
                    ? string.Join(", ", _cachedRemotes)
                    : "Nenhum remote configurado";

                var firstRemote = _cachedRemotes.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstRemote))
                {
                    var about = await _api.GetRemoteAboutAsync(firstRemote);
                    if (about != null)
                    {
                        var used = about.Used.HasValue ? FormatBytes(about.Used.Value) : "?";
                        var total = about.Total.HasValue ? FormatBytes(about.Total.Value) : "?";
                        var free = about.Free.HasValue ? FormatBytes(about.Free.Value) : "?";
                        DriveAboutText.Text = $"{firstRemote} {used} usado / {total} total ({free} livres)";
                    }
                    else
                    {
                        DriveAboutText.Text = $"{firstRemote} pronto (verificação de cota offline/indisponível)";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            RemotesListText.Text = "Falha ao listar remotes";
            DriveAboutText.Text = ex.Message;
        }
    }

    private async void AnalyzeFilterButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadFilterAnalysisAsync();
    }

    private async Task LoadFilterAnalysisAsync()
    {
        try
        {
            _initialFilterLoaded = true;
            var resp = await _api.AnalyzeFilterAsync(FilterFilePathBox.Text.Trim());
            if (resp != null)
            {
                FilterEditorBox.Text = resp.Content;
                RenderFilterAnalysis(resp.Analysis);
                var job = ResolveTargetJobForCurrentFilter();
                if (job != null)
                {
                    try
                    {
                        var cached = await _api.GetExcludedRetentionAsync(job.Id, scan: false);
                        if (cached != null)
                            ExcludedRetentionSummaryText.Text = cached.StatusSummary;
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            FilterDiagnosticsText.Text = $"Erro ao analisar arquivo de filtros: {ex.Message}";
        }
    }

    private async void OptimizeFilterButton_Click(object sender, RoutedEventArgs e)
    {
        var path = FilterFilePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return;

        var confirm = await _overlayHost.ShowConfirmAsync(
            "Otimizar Regras de Filtro",
            "Isso criará uma cópia de segurança (.bak) do arquivo atual e organizará automaticamente as regras de exclusão e inclusão. Deseja continuar?",
            confirmText: "Otimizar Agora");

        if (!confirm)
            return;

        try
        {
            var resp = await _api.OptimizeFilterAsync(path);
            if (resp != null)
            {
                FilterEditorBox.Text = resp.Content;
                RenderFilterAnalysis(resp.Analysis);
                FooterInfoText.Text = "Arquivo de filtro otimizado com sucesso (backup .bak preservado)!";
            }
        }
        catch (Exception ex)
        {
            await _overlayHost.ShowAlertAsync("Erro ao otimizar arquivo", ex.Message, OverlayAlertKind.Error);
        }
    }

    private async void SaveFilterButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var resp = await _api.SaveFilterAsync(FilterFilePathBox.Text.Trim(), FilterEditorBox.Text);
            if (resp != null)
            {
                RenderFilterAnalysis(resp.Analysis);
                FooterInfoText.Text = "Arquivo de filtros salvo.";
            }
        }
        catch (Exception ex)
        {
            await _overlayHost.ShowAlertAsync("Erro ao salvar arquivo", ex.Message, OverlayAlertKind.Error);
        }
    }

    private void RenderFilterAnalysis(FilterAnalysisResult analysis)
    {
        if (!analysis.FileExists)
        {
            FilterStatusBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
            FilterDiagnosticsText.Text = $"Arquivo não encontrado: {analysis.FilePath}";
            return;
        }

        if (analysis.Warnings.Count == 0)
        {
            FilterStatusBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
            FilterDiagnosticsText.Text = $"OK! Modo recomendado: {analysis.RecommendedMode}. Nenhum erro de sintaxe ou precedência encontrado.";
        }
        else
        {
            FilterStatusBadge.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11));
            FilterDiagnosticsText.Text =
                $"Diagnóstico ({analysis.Warnings.Count} alerta(s) — Modo recomendado: {analysis.RecommendedMode}):\n• " +
                string.Join("\n• ", analysis.Warnings) +
                "\n(Obs.: Durante a execução, o motor híbrido já sanitiza automaticamente em cópia temporária até você clicar em 'Corrigir & Otimizar').";
        }
    }

    private async void RefreshLogsButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshLogsAsync();
    }

    private async void LogControls_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            await RefreshLogsAsync();
    }

    private async Task RefreshLogsAsync()
    {
        try
        {
            var resp = await _api.GetLogsAsync(300);
            if (resp == null)
                return;

            var useServiceLogs = LogSourceCombo.SelectedIndex == 1;
            var lines = useServiceLogs ? resp.ServiceLogs : resp.RcloneLogLines;

            var filterIdx = LogFilterCombo.SelectedIndex;
            if (filterIdx == 1)
            {
                lines = lines.Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                                         l.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else if (filterIdx == 2)
            {
                lines = lines.Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ||
                                         l.Contains("WARN", StringComparison.OrdinalIgnoreCase) ||
                                         l.Contains("NOTICE", StringComparison.OrdinalIgnoreCase) ||
                                         l.Contains("CRITICAL", StringComparison.OrdinalIgnoreCase)).ToList();
            }

            LogsTextBox.Text = string.Join(Environment.NewLine, lines);
            if (AutoScrollLogsCheck.IsChecked == true)
                LogsTextBox.ScrollToEnd();
        }
        catch
        {
            // Ignora falha silenciosa ao ler logs enquanto offline
        }
    }

    private async void WindowsStartupCheck_Click(object sender, RoutedEventArgs e)
    {
        var enabled = WindowsStartupCheck.IsChecked == true;
        try
        {
            var resp = await _api.SetStartupAsync(enabled);
            FooterInfoText.Text = resp is { StartupWithWindowsEnabled: true }
                ? "Serviço configurado para iniciar automaticamente com o Windows."
                : "Inicialização automática com o Windows desativada.";
        }
        catch (Exception ex)
        {
            await _overlayHost.ShowAlertAsync("Erro na inicialização automática", ex.Message, OverlayAlertKind.Error);
        }
    }

    private async void ReconnectServiceButton_Click(object sender, RoutedEventArgs e)
    {
        FooterInfoText.Text = "Iniciando / reconectando ao serviço...";
        var ok = await ServiceLauncher.EnsureServiceRunningAsync(_api);
        FooterInfoText.Text = ok
            ? "Serviço conectado!"
            : "Não foi possível conectar ao RcloneManager.Service.";
        await PollServiceAsync();
    }

    private async void ShutdownServiceButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = await _overlayHost.ShowConfirmAsync(
            "Encerrar Serviço em 2º Plano",
            "Encerrar o serviço interromperá o agendador automático e qualquer backup em andamento. Deseja continuar?",
            confirmText: "Encerrar Serviço",
            isDanger: true);

        if (!confirm)
            return;

        try
        {
            await _api.ShutdownServiceAsync();
            MarkServiceOffline();
            FooterInfoText.Text = "Serviço encerrado.";
        }
        catch (Exception ex)
        {
            FooterInfoText.Text = $"Erro ao encerrar serviço: {ex.Message}";
        }
    }

    private async void InstallWindowsServiceButton_Click(object sender, RoutedEventArgs e)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        FooterInfoText.Text = "Instalando e iniciando Serviço Nativo do Windows (confirme o UAC)...";
        var (ok, _, msg) = await ServiceLauncher.TryInstallAndStartWindowsServiceAsync(workDir, _api);
        if (ok)
        {
            try { await _api.SetServiceModeAsync(BackgroundServiceMode.WindowsService); } catch { }
        }
        FooterInfoText.Text = msg;
        await PollServiceAsync();
    }

    private async void UninstallWindowsServiceButton_Click(object sender, RoutedEventArgs e)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        FooterInfoText.Text = "Removendo Serviço Nativo do Windows (confirme o UAC)...";
        var (ok, msg) = await ServiceLauncher.TryUninstallWindowsServiceAsync(workDir, _api);
        if (ok)
        {
            try { await _api.SetServiceModeAsync(BackgroundServiceMode.UserStartup); } catch { }
        }
        FooterInfoText.Text = msg;
        await PollServiceAsync();
    }

    private async void ApplyPinnedReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        string targetVersion;
        try
        {
            targetVersion = RcloneReleaseManager.NormalizeVersion(PinnedVersionBox.Text);
            PinnedVersionBox.Text = targetVersion;
        }
        catch (ArgumentException ex)
        {
            await _overlayHost.ShowAlertAsync("Versão Inválida", ex.Message, OverlayAlertKind.Warning);
            return;
        }

        ApplyPinnedReleaseButton.IsEnabled = false;
        try
        {
            var lockEnabled = LockVersionCheck.IsChecked == true;
            var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
            var store = new AppConfigStore(workDir);
            var cfg = store.LoadOrCreateDefault();
            cfg.PinnedRcloneVersion = targetVersion;
            cfg.LockRcloneVersion = lockEnabled;

            var serviceOnline = false;
            try
            {
                var st = await _api.GetStatusAsync();
                serviceOnline = st is { ServiceRunning: true };
            }
            catch
            {
                serviceOnline = false;
            }

            if (serviceOnline)
            {
                await _api.StopRcdAsync();
            }

            var check = await RcloneReleaseManager.CheckInstallationAsync(cfg, workDir);
            var expectedExe = RcloneReleaseManager.GetExpectedExecutablePath(workDir, targetVersion);

            if (check.NeedsDownload || !File.Exists(expectedExe))
            {
                var setupView = new RcloneSetupWindow(
                    workDir,
                    targetVersion,
                    $"Provisionando e travando a release {targetVersion}...");

                if (await _overlayHost.ShowAsync(setupView) != true || string.IsNullOrWhiteSpace(setupView.DownloadedExecutablePath))
                {
                    FooterInfoText.Text = "Alteração de release cancelada.";
                    return;
                }

                cfg.RcloneExecutablePath = setupView.DownloadedExecutablePath;
            }
            else
            {
                cfg.RcloneExecutablePath = expectedExe;
            }

            if (string.IsNullOrWhiteSpace(cfg.RcloneConfigPath))
                cfg.RcloneConfigPath = AppConfigStore.DetectRcloneConfigPath(cfg.RcloneExecutablePath);

            store.Save(cfg);

            if (serviceOnline)
            {
                await _api.UpdateReleaseConfigAsync(new UpdateReleaseConfigRequest
                {
                    PinnedRcloneVersion = targetVersion,
                    LockRcloneVersion = lockEnabled,
                    RcloneExecutablePath = cfg.RcloneExecutablePath
                });
                await PollServiceAsync();
            }
            else
            {
                ReleaseStatusText.Text = $"Pronto: {targetVersion} em '{cfg.RcloneExecutablePath}'.";
            }

            FooterInfoText.Text = $"Release {targetVersion} aplicada ({(lockEnabled ? "travada" : "sem trava")}).";
        }
        catch (Exception ex)
        {
            await _overlayHost.ShowAlertAsync("Erro ao baixar/travar release", ex.Message, OverlayAlertKind.Error);
        }
        finally
        {
            ApplyPinnedReleaseButton.IsEnabled = true;
        }
    }

    private void RunElevatedServiceCommand(string flag, string successMsg)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "RcloneManager.Service.exe"),
            Path.Combine(workDir, "RcloneManager.Service.exe"),
            Path.Combine(workDir, "src", "RcloneManager.Service", "bin", "Release", "net10.0", "RcloneManager.Service.exe"),
            Path.Combine(workDir, "src", "RcloneManager.Service", "bin", "Debug", "net10.0", "RcloneManager.Service.exe")
        };

        var exe = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(exe))
        {
            _ = _overlayHost.ShowAlertAsync("Serviço não encontrado", "RcloneManager.Service.exe não foi encontrado.", OverlayAlertKind.Error);
            return;
        }

        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{flag} --workdir \"{workDir}\"",
                UseShellExecute = true,
                Verb = "runas"
            });
            proc?.WaitForExit(10000);
            FooterInfoText.Text = proc is { ExitCode: 0 }
                ? successMsg
                : $"Comando '{flag}' retornou código {proc?.ExitCode}.";
        }
        catch (Win32Exception)
        {
            FooterInfoText.Text = "Operação administrativa cancelada pelo usuário.";
        }
        catch (Exception ex)
        {
            _ = _overlayHost.ShowAlertAsync("Erro na operação do serviço", ex.Message, OverlayAlertKind.Error);
        }
    }

    private void BrowseSourceButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Selecione a pasta de origem para sincronização/backup",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(JobSourceBox.Text) ? JobSourceBox.Text : string.Empty
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            JobSourceBox.Text = dialog.SelectedPath;
    }

    private void BrowseFilterButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Filter = "Arquivos de texto e filtros (*.txt;*.filter)|*.txt;*.filter|Todos os arquivos (*.*)|*.*",
            InitialDirectory = ServiceLauncher.ResolveWorkspaceDirectory()
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            JobFilterPathBox.Text = dialog.FileName;
    }

    private void BrowseEditorFilterButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Filter = "Arquivos de texto e filtros (*.txt;*.filter)|*.txt;*.filter|Todos os arquivos (*.*)|*.*",
            InitialDirectory = ServiceLauncher.ResolveWorkspaceDirectory()
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            FilterFilePathBox.Text = dialog.FileName;
            _ = LoadFilterAnalysisAsync();
        }
    }

    private void SyncBwComboSelection(string? currentRate)
    {
        if (BwLimitCombo.IsDropDownOpen)
            return;

        var target = string.IsNullOrWhiteSpace(currentRate) ? "off" : currentRate.Trim();
        foreach (ComboBoxItem item in BwLimitCombo.Items)
        {
            var tagOrContent = item.Tag?.ToString() ?? item.Content?.ToString();
            if (string.Equals(tagOrContent, target, StringComparison.OrdinalIgnoreCase))
            {
                if (BwLimitCombo.SelectedItem != item)
                    BwLimitCombo.SelectedItem = item;
                return;
            }
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private async void RunPrimaryJobFromTray()
    {
        var primary = _cachedJobs.FirstOrDefault(j => j.Enabled) ?? _cachedJobs.FirstOrDefault();
        if (primary == null)
            return;

        try
        {
            await _api.RunJobAsync(primary.Id, dryRun: false);
            _trayIcon.ShowBalloon("RcloneManager", $"Executando '{primary.Name}' em segundo plano.");
        }
        catch
        {
        }
    }

    private async void StopActiveJobFromTray()
    {
        try
        {
            var status = await _api.GetStatusAsync();
            if (status?.ActiveJob != null)
            {
                await _api.StopJobAsync(status.ActiveJob.JobId);
                _trayIcon.ShowBalloon("RcloneManager", $"Parada solicitada para '{status.ActiveJob.JobName}'.");
            }
        }
        catch
        {
        }
    }

    private void ExitUiKeepingService()
    {
        _keepServiceOnClose = true;
        _isClosingForReal = true;
        Close();
    }

    private async void ExitUiAndService()
    {
        _keepServiceOnClose = false;
        try { await _api.ShutdownServiceAsync(); } catch { }
        _isClosingForReal = true;
        Close();
    }

    private void InitializeCloseBehaviorUi()
    {
        CloseBehaviorCombo.ItemsSource = new[]
        {
            new UiOption<CloseWindowBehavior>(CloseWindowBehavior.AskEveryTime, "Perguntar sempre"),
            new UiOption<CloseWindowBehavior>(CloseWindowBehavior.MinimizeToTray, "Minimizar para a Bandeja"),
            new UiOption<CloseWindowBehavior>(CloseWindowBehavior.CloseKeepService, "Fechar janela (manter 2º plano)")
        };

        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var prefs = UiPreferencesStore.Load(workDir);
        SyncCloseBehaviorUi(prefs.CloseBehavior);
        AutoUpdateAppCheck.IsChecked = prefs.AutoUpdateInBackground;
    }

    private void SyncCloseBehaviorUi(CloseWindowBehavior behavior)
    {
        _isUpdatingCloseBehaviorUi = true;
        try
        {
            SelectOption(CloseBehaviorCombo, behavior);
            MenuCloseAskItem.Header = (behavior == CloseWindowBehavior.AskEveryTime ? "✓ " : "   ") + "Perguntar sempre";
            MenuCloseTrayItem.Header = (behavior == CloseWindowBehavior.MinimizeToTray ? "✓ " : "   ") + "Minimizar para a Bandeja";
            MenuCloseExitUiItem.Header = (behavior == CloseWindowBehavior.CloseKeepService ? "✓ " : "   ") + "Fechar Janela (manter 2º plano)";
        }
        finally
        {
            _isUpdatingCloseBehaviorUi = false;
        }
    }

    private void SaveCloseBehaviorPreference(CloseWindowBehavior behavior)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var prefs = UiPreferencesStore.Load(workDir);
        prefs.CloseBehavior = behavior;
        UiPreferencesStore.Save(workDir, prefs);
        SyncCloseBehaviorUi(behavior);

        FooterInfoText.Text = behavior switch
        {
            CloseWindowBehavior.MinimizeToTray => "Preferência salva: ao fechar [X], o aplicativo minimizará para a Bandeja.",
            CloseWindowBehavior.CloseKeepService => "Preferência salva: ao fechar [X], a janela fechará mantendo o serviço em 2º plano.",
            _ => "Preferência salva: o aplicativo perguntará o que fazer ao clicar em fechar [X]."
        };
    }

    private void AutoUpdateAppCheck_Click(object sender, RoutedEventArgs e)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var prefs = UiPreferencesStore.Load(workDir);
        prefs.AutoUpdateInBackground = AutoUpdateAppCheck.IsChecked == true;
        UiPreferencesStore.Save(workDir, prefs);

        FooterInfoText.Text = prefs.AutoUpdateInBackground
            ? "Atualização automática em segundo plano ativada."
            : "Atualização automática em segundo plano desativada.";

        if (prefs.AutoUpdateInBackground)
            _ = CheckAppUpdateInBackgroundAsync();
    }

    private void SetStagedUpdateReady(PendingAppUpdateInfo pending)
    {
        _stagedUpdate = pending;
        UpdateReadyButton.Content = $"✦ Atualização {pending.VersionTag} pronta";
        UpdateReadyButton.Visibility = Visibility.Visible;
        UpdateReadyButton.ToolTip = $"Nova versão {pending.VersionTag} já baixada. Será instalada automaticamente ao fechar (ou clique para reiniciar agora).";
    }

    private async Task CheckAppUpdateInBackgroundAsync()
    {
        if (_updateCheckInFlight)
            return;

        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var prefs = UiPreferencesStore.Load(workDir);
        if (!prefs.AutoUpdateInBackground)
            return;

        // Em ambiente de desenvolvimento (src/.../bin/Debug), não sobrescreve os binários automaticamente
        if (AppAutoUpdater.IsDevelopmentBuild(workDir))
            return;

        _updateCheckInFlight = true;
        try
        {
            var res = await AppAutoUpdater.CheckAndStageUpdateAsync(
                workDir,
                downloadAndStageIfAvailable: true);

            if (res.IsStagedAndReady && res.PendingUpdate != null)
            {
                SetStagedUpdateReady(res.PendingUpdate);
                FooterInfoText.Text = res.StatusMessage;
            }
        }
        catch
        {
            // Falha silenciosa em verificação de fundo
        }
        finally
        {
            _updateCheckInFlight = false;
        }
    }

    private async void UpdateReadyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stagedUpdate == null)
            return;

        var notesPreview = string.IsNullOrWhiteSpace(_stagedUpdate.ReleaseNotes)
            ? "Melhorias de estabilidade e correções."
            : (_stagedUpdate.ReleaseNotes.Length > 420
                ? _stagedUpdate.ReleaseNotes[..420].Trim() + "..."
                : _stagedUpdate.ReleaseNotes.Trim());

        var restartNow = await _overlayHost.ShowConfirmAsync(
            $"Atualização {_stagedUpdate.VersionTag} Pronta",
            $"A nova versão {_stagedUpdate.VersionTag} já foi baixada e verificada em segundo plano.\n\n" +
            $"Novidades:\n{notesPreview}\n\n" +
            "Ela será aplicada automaticamente quando você fechar o aplicativo, ou você pode reiniciar agora.",
            confirmText: "Reiniciar e Atualizar Agora",
            cancelText: "Aplicar ao Fechar");

        if (!restartNow)
            return;

        if (_lastStatus?.ActiveJob != null)
        {
            await _overlayHost.ShowAlertAsync(
                "Backup em Andamento",
                $"A tarefa '{_lastStatus.ActiveJob.JobName}' está enviando arquivos agora. A atualização será aplicada automaticamente assim que o backup terminar e você fechar ou reabrir o aplicativo.",
                OverlayAlertKind.Warning);
            return;
        }

        _relaunchUiAfterUpdate = true;
        _keepServiceOnClose = false;
        _isClosingForReal = true;
        Close();
    }

    private async void MenuCheckAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updateCheckInFlight)
        {
            FooterInfoText.Text = "Verificação de atualização já está em andamento...";
            return;
        }

        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var isDev = AppAutoUpdater.IsDevelopmentBuild(workDir);

        _updateCheckInFlight = true;
        FooterInfoText.Text = "Verificando atualizações nas Releases do GitHub...";
        try
        {
            var progress = new Progress<string>(msg => FooterInfoText.Text = msg);
            var res = await AppAutoUpdater.CheckAndStageUpdateAsync(
                workDir,
                downloadAndStageIfAvailable: !isDev,
                statusProgress: progress);

            FooterInfoText.Text = res.StatusMessage;

            if (!res.UpdateAvailable)
            {
                await _overlayHost.ShowAlertAsync("Atualizações do RcloneManager", res.StatusMessage);
                return;
            }

            if (isDev)
            {
                var openPage = await _overlayHost.ShowConfirmAsync(
                    $"Nova Versão {res.LatestVersionTag} Disponível",
                    $"Você está executando a partir do código-fonte de desenvolvimento (v{res.CurrentVersion}).\n" +
                    $"Na versão portátil publicada, o pacote .zip é baixado e aplicado automaticamente.\n\nDeseja abrir a página da Release no GitHub?",
                    confirmText: "Abrir Release no GitHub",
                    cancelText: "Fechar");

                if (openPage && !string.IsNullOrWhiteSpace(res.ReleaseUrl))
                    OpenShellTarget(res.ReleaseUrl);
                return;
            }

            if (res.IsStagedAndReady && res.PendingUpdate != null)
            {
                SetStagedUpdateReady(res.PendingUpdate);
                UpdateReadyButton_Click(sender, e);
            }
            else
            {
                await _overlayHost.ShowAlertAsync("Atualizações do RcloneManager", res.StatusMessage, OverlayAlertKind.Warning);
            }
        }
        catch (Exception ex)
        {
            await _overlayHost.ShowAlertAsync("Erro ao verificar atualizações", ex.Message, OverlayAlertKind.Error);
        }
        finally
        {
            _updateCheckInFlight = false;
        }
    }

    private void TryLaunchPendingUpdateOnExit()
    {
        if (_updateApplierLaunched)
            return;

        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        if (AppAutoUpdater.IsDevelopmentBuild(workDir))
            return;

        var pending = _stagedUpdate ?? AppAutoUpdater.TryGetValidPendingUpdate(workDir);
        if (pending == null)
            return;

        // Não interrompe um backup ativo em 2º plano ao fechar só a janela; ficará pronto para a próxima abertura
        if (!_relaunchUiAfterUpdate && _keepServiceOnClose && _lastStatus?.ActiveJob != null)
            return;

        if (AppAutoUpdater.LaunchDetachedUpdateApplier(
                workDir,
                pending,
                relaunchUiAfterUpdate: _relaunchUiAfterUpdate,
                restartBackgroundServiceIfNotRelaunchingUi: _keepServiceOnClose))
        {
            _updateApplierLaunched = true;
        }
    }

    private void CloseBehaviorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingCloseBehaviorUi)
            return;

        var selected = GetSelectedOption(CloseBehaviorCombo, CloseWindowBehavior.AskEveryTime);
        SaveCloseBehaviorPreference(selected);
    }

    private void MenuCloseAsk_Click(object sender, RoutedEventArgs e) =>
        SaveCloseBehaviorPreference(CloseWindowBehavior.AskEveryTime);

    private void MenuCloseTray_Click(object sender, RoutedEventArgs e) =>
        SaveCloseBehaviorPreference(CloseWindowBehavior.MinimizeToTray);

    private void MenuCloseExitUi_Click(object sender, RoutedEventArgs e) =>
        SaveCloseBehaviorPreference(CloseWindowBehavior.CloseKeepService);

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosingForReal)
        {
            TryLaunchPendingUpdateOnExit();
            _pollTimer.Stop();
            _updateCheckTimer.Stop();
            _trayIcon.Dispose();
            _api.Dispose();
            return;
        }

        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var behavior = UiPreferencesStore.Load(workDir).CloseBehavior;

        if (behavior == CloseWindowBehavior.AskEveryTime)
        {
            e.Cancel = true;
            if (!_isClosePromptOpen)
                _ = PromptCloseOverlayAsync();
            return;
        }

        if (behavior == CloseWindowBehavior.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloon("RcloneManager", "O aplicativo continua rodando na bandeja do sistema.");
            return;
        }

        // CloseKeepService -> fecha a UI mantendo o serviço
        _keepServiceOnClose = true;
        _isClosingForReal = true;
        TryLaunchPendingUpdateOnExit();
        _pollTimer.Stop();
        _updateCheckTimer.Stop();
        _trayIcon.Dispose();
        _api.Dispose();
    }

    private async Task PromptCloseOverlayAsync()
    {
        _isClosePromptOpen = true;
        try
        {
            var prompt = new ClosePromptWindow();
            if (await _overlayHost.ShowAsync(prompt) != true)
                return;

            var behavior = prompt.SelectedBehavior;
            if (prompt.RememberChoice)
                SaveCloseBehaviorPreference(behavior);

            if (behavior == CloseWindowBehavior.MinimizeToTray)
            {
                Hide();
                _trayIcon.ShowBalloon("RcloneManager", "O aplicativo continua rodando na bandeja do sistema.");
                return;
            }

            ExitUiKeepingService();
        }
        finally
        {
            _isClosePromptOpen = false;
        }
    }

    private void Window_PreviewKeyDown(object sender, WpfInput.KeyEventArgs e)
    {
        if (_overlayHost.HasActiveOverlay)
        {
            if (e.Key == WpfInput.Key.Escape && _overlayHost.TryDismissTop())
                e.Handled = true;
            return;
        }

        if (WpfInput.Keyboard.Modifiers != WpfInput.ModifierKeys.Control)
            return;

        if (e.Key == WpfInput.Key.N)
        {
            e.Handled = true;
            MenuNewJob_Click(sender, e);
        }
        else if (e.Key == WpfInput.Key.S)
        {
            e.Handled = true;
            MenuSaveCurrent_Click(sender, e);
        }
    }

    private void MenuNewJob_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = JobsTab;
        NewJobButton_Click(sender, e);
    }

    private void MenuSaveCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (MainTabs.SelectedItem == JobsTab)
        {
            SaveJobButton_Click(sender, e);
        }
        else if (MainTabs.SelectedItem == RemotesTab)
        {
            SaveRemoteButton_Click(sender, e);
        }
        else if (MainTabs.SelectedItem == FiltersTab)
        {
            SaveFilterButton_Click(sender, e);
        }
        else
        {
            FooterInfoText.Text = "Selecione a aba 'Tarefas & Agendador', 'Remotes' ou 'Filtros' para salvar alterações.";
        }
    }

    private void MenuOpenWorkspace_Click(object sender, RoutedEventArgs e)
    {
        OpenShellTarget(ServiceLauncher.ResolveWorkspaceDirectory());
    }

    private void MenuOpenConfigFile_Click(object sender, RoutedEventArgs e)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var configPath = Path.Combine(workDir, "rclone_manager_config.json");
        if (!File.Exists(configPath))
        {
            var store = new AppConfigStore(workDir);
            store.LoadOrCreateDefault();
        }
        OpenShellTarget(configPath);
    }

    private void MenuOpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var svcLog = Path.Combine(workDir, "rclone_manager_service.log");
        var rcloneLog = Path.Combine(workDir, "rclone_backup.log");

        var targetFile = File.Exists(svcLog) ? svcLog : (File.Exists(rcloneLog) ? rcloneLog : null);
        if (targetFile != null)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{targetFile}\"",
                UseShellExecute = true
            });
            return;
        }

        OpenShellTarget(workDir);
    }

    private async void MenuExportConfig_Click(object sender, RoutedEventArgs e)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var store = new AppConfigStore(workDir);
        var cfg = store.LoadOrCreateDefault();

        using var dialog = new WinForms.SaveFileDialog
        {
            Title = "Exportar Configuração Portátil (sem credenciais ou chaves)",
            Filter = "JSON (*.json)|*.json",
            FileName = $"rclone_manager_config_{DateTime.Now:yyyyMMdd}.json",
            InitialDirectory = workDir
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        try
        {
            List<RcloneRemoteDetail>? remotes = null;
            try
            {
                var details = await _api.GetRemotesDetailsAsync();
                remotes = details?.Remotes;
            }
            catch
            {
                if (_cachedRemoteDetails.Count > 0)
                    remotes = _cachedRemoteDetails;
            }

            store.ExportPortableConfig(dialog.FileName, cfg, remotes);
            FooterInfoText.Text = $"Configuração portátil exportada para '{dialog.FileName}'.";
            await _overlayHost.ShowAlertAsync(
                "Exportação Concluída",
                $"Configuração exportada para:\n{dialog.FileName}\n\n" +
                "Por segurança, tokens e senhas NÃO foram incluídos neste arquivo.\n" +
                "Ao importar em outro computador, o Assistente Inicial guiará a conexão das contas.");
        }
        catch (Exception ex)
        {
            await _overlayHost.ShowAlertAsync("Erro ao exportar configuração", ex.Message, OverlayAlertKind.Error);
        }
    }

    private async void MenuImportConfig_Click(object sender, RoutedEventArgs e)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "Importar Configuração pelo Assistente",
            Filter = "Configuração RcloneManager (*.json)|*.json|Todos os arquivos (*.*)|*.*",
            InitialDirectory = workDir
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        var wizard = new FirstRunWizardWindow(workDir, _api, dialog.FileName);

        if (await _overlayHost.ShowAsync(wizard) == true)
        {
            _selectedJobId = null;
            _initialProvidersLoaded = false;
            await PollServiceAsync();
            await RefreshRemotesTabDataAsync();
            await RefreshRemotesAndAboutAsync();
            FooterInfoText.Text = $"Configuração de '{Path.GetFileName(dialog.FileName)}' importada e aplicada com sucesso.";
        }
    }

    private async void MenuRunWizard_Click(object sender, RoutedEventArgs e)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var wizard = new FirstRunWizardWindow(workDir, _api);
        if (await _overlayHost.ShowAsync(wizard) == true)
        {
            await PollServiceAsync();
            await RefreshRemotesTabDataAsync();
            await RefreshRemotesAndAboutAsync();
            FooterInfoText.Text = "Configurações do Assistente Inicial aplicadas.";
        }
    }

    private async void MenuResetConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ResetConfigWindow();
        if (await _overlayHost.ShowAsync(dialog) != true)
            return;

        try
        {
            var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
            var serviceOnline = _lastStatus is { ServiceRunning: true };

            if (serviceOnline)
            {
                await _api.ResetConfigAsync(new ResetConfigRequest
                {
                    DeleteCredentialKey = dialog.DeleteCredentialKey,
                    DeleteLogsAndRuntime = dialog.DeleteLogsAndRuntime,
                    DeleteRcloneConf = dialog.DeleteRcloneConf
                });
            }
            else
            {
                var store = new AppConfigStore(workDir);
                store.ResetConfig(dialog.DeleteCredentialKey, dialog.DeleteLogsAndRuntime, dialog.DeleteRcloneConf);
            }

            UiPreferencesStore.Reset(workDir);
            SyncCloseBehaviorUi(CloseWindowBehavior.AskEveryTime);
            AutoUpdateAppCheck.IsChecked = true;

            _selectedJobId = null;
            _initialProvidersLoaded = false;
            await PollServiceAsync();
            await RefreshRemotesTabDataAsync();
            await RefreshRemotesAndAboutAsync();
            await RefreshLogsAsync();

            FooterInfoText.Text = "Configurações resetadas com sucesso.";

            if (dialog.ReopenWizard)
            {
                var wizard = new FirstRunWizardWindow(workDir, _api);
                if (await _overlayHost.ShowAsync(wizard) == true)
                {
                    await PollServiceAsync();
                    await RefreshRemotesTabDataAsync();
                    await RefreshRemotesAndAboutAsync();
                }
            }
        }
        catch (Exception ex)
        {
            await _overlayHost.ShowAlertAsync("Erro ao resetar configurações", ex.Message, OverlayAlertKind.Error);
        }
    }

    private void MenuMinimizeToTray_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _trayIcon.ShowBalloon("RcloneManager", "Minimizado para a bandeja do sistema.");
    }

    private void MenuCloseUiOnly_Click(object sender, RoutedEventArgs e)
    {
        ExitUiKeepingService();
    }

    private async void MenuExitAll_Click(object sender, RoutedEventArgs e)
    {
        var confirm = await _overlayHost.ShowConfirmAsync(
            "Sair do RcloneManager",
            "Isso encerrará tanto esta janela quanto o serviço de agendamento em segundo plano. Deseja continuar?",
            confirmText: "Encerrar Tudo",
            isDanger: true);

        if (confirm)
            ExitUiAndService();
    }

    private void MenuOpenDocs_Click(object sender, RoutedEventArgs e)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var docsDir = Path.Combine(workDir, "docs");
        var readme = Path.Combine(workDir, "README.md");

        if (Directory.Exists(docsDir))
            OpenShellTarget(docsDir);
        else if (File.Exists(readme))
            OpenShellTarget(readme);
        else
            OpenShellTarget("https://github.com/Dillemon-Recla/RcloneManager");
    }

    private void MenuOpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        OpenShellTarget("https://github.com/Dillemon-Recla/RcloneManager");
    }

    private void MenuOpenRcloneDocs_Click(object sender, RoutedEventArgs e)
    {
        OpenShellTarget("https://rclone.org/docs/");
    }

    private async void MenuCheckRelease_Click(object sender, RoutedEventArgs e)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var store = new AppConfigStore(workDir);
        var cfg = store.LoadOrCreateDefault();
        var check = await RcloneReleaseManager.CheckInstallationAsync(cfg, workDir);

        if (check.NeedsDownload)
        {
            MainTabs.SelectedItem = LogsTab;
            ApplyPinnedReleaseButton_Click(sender, e);
            return;
        }

        await _overlayHost.ShowAlertAsync(
            "Verificação de Release do Rclone",
            $"{check.StatusMessage}\n\nCaminho: {check.ExecutablePath}\nVersão Instalada: {check.InstalledVersion}\nRelease Travada: {check.PinnedVersion} ({(check.IsLocked ? "Ativa" : "Livre")})");
    }

    private void MenuCopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var info = string.Join(Environment.NewLine, new[]
        {
            "=== RcloneManager Diagnóstico ===",
            $"Data/Hora: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"Diretório de Trabalho: {workDir}",
            $"Runtime: {RuntimeInformation.FrameworkDescription}",
            $"SO: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            $"Serviço Local: {(_lastStatus != null ? $"Ativo (desde {_lastStatus.ServiceStartedAt:HH:mm:ss})" : "Offline")}",
            $"Rclone Versão: {_lastStatus?.RcloneVersion ?? "Desconhecida"}",
            $"Trava Release: {(_lastStatus != null ? $"{_lastStatus.PinnedRcloneVersion} (Lock={_lastStatus.LockRcloneVersion}, Valid={_lastStatus.IsRcloneVersionValid})" : "N/A")}",
            $"Daemon RC: {(_lastStatus?.RcDaemonRunning == true ? $"Ativo (porta {_lastStatus.RcDaemonPort})" : "Desligado")}",
            $"Tarefas Configuradas: {_lastStatus?.Jobs.Count ?? _cachedJobs.Count}"
        });

        System.Windows.Clipboard.SetText(info);
        FooterInfoText.Text = "Informações de diagnóstico copiadas para a Área de Transferência.";
    }

    private async void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow(_lastStatus, ServiceLauncher.ResolveWorkspaceDirectory());
        await _overlayHost.ShowAsync(about);
    }

    private static void OpenShellTarget(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _ = OverlayHost.ShowCurrentAlertAsync("Não foi possível abrir", $"Não foi possível abrir '{target}': {ex.Message}", OverlayAlertKind.Warning);
        }
    }

    #region Gerenciamento Visual de Remotes (rclone.conf)

    private static readonly HashSet<string> KnownOAuthProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "drive", "onedrive", "dropbox", "box", "google photos", "googlephotos",
        "pcloud", "yandex", "mailru", "hidrive", "zoho", "jottacloud"
    };

    private async Task RefreshRemotesTabDataAsync()
    {
        try
        {
            if (!_initialProvidersLoaded)
            {
                var provResp = await _api.GetProvidersAsync();
                if (provResp != null)
                {
                    _allProviders = provResp.Providers;
                    _initialProvidersLoaded = true;
                    PopulateProviderCombo(ProviderSearchBox.Text, selectProviderName: _isEditingExistingRemote ? null : "drive");
                }
            }

            var detailsResp = await _api.GetRemotesDetailsAsync();
            if (detailsResp != null)
            {
                _cachedRemoteDetails = detailsResp.Remotes;
                RemoteConfigPathText.Text = string.IsNullOrWhiteSpace(detailsResp.ConfigPath)
                    ? $"Arquivo: padrão do Rclone | Fonte: {detailsResp.SourceUsed}"
                    : $"Arquivo: {detailsResp.ConfigPath} | Fonte: {detailsResp.SourceUsed}";

                var rows = _cachedRemoteDetails.Select(r => new RemoteRowItem
                {
                    Name = r.Name,
                    Type = r.Type,
                    AuthText = r.HasToken ? "OAuth ✓" : "Configurado",
                    ParamsSummary = string.Join(", ", r.Parameters.Where(kv => kv.Key != "type" && kv.Key != "token").Select(kv => $"{kv.Key}={kv.Value}")),
                    RawRemote = r
                }).ToList();

                _isUpdatingRemotesGrid = true;
                try
                {
                    var selectedName = (RemotesGrid.SelectedItem as RemoteRowItem)?.Name;
                    RemotesGrid.ItemsSource = rows;
                    if (!string.IsNullOrWhiteSpace(selectedName))
                    {
                        var match = rows.FirstOrDefault(x => x.Name.Equals(selectedName, StringComparison.OrdinalIgnoreCase));
                        if (match != null)
                            RemotesGrid.SelectedItem = match;
                    }
                }
                finally
                {
                    _isUpdatingRemotesGrid = false;
                }
            }
        }
        catch (Exception ex)
        {
            RemoteTestResultText.Text = $"Erro ao carregar remotes: {ex.Message}";
        }
    }

    private void PopulateProviderCombo(string? filterText, string? selectProviderName = null)
    {
        var currentSelected = selectProviderName ?? (RemoteProviderCombo.SelectedItem as ProviderListItem)?.Name;
        var q = filterText?.Trim() ?? string.Empty;

        var filtered = _allProviders
            .Where(p => string.IsNullOrEmpty(q) ||
                        p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        p.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(p => new ProviderListItem
            {
                Name = p.Name,
                Description = p.Description,
                RawProvider = p
            })
            .ToList();

        RemoteProviderCombo.ItemsSource = filtered;
        if (!string.IsNullOrWhiteSpace(currentSelected))
        {
            var match = filtered.FirstOrDefault(p => p.Name.Equals(currentSelected, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                RemoteProviderCombo.SelectedItem = match;
                return;
            }
        }

        if (filtered.Count > 0)
            RemoteProviderCombo.SelectedIndex = 0;
    }

    private void ProviderSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isEditingExistingRemote && _allProviders.Count > 0)
            PopulateProviderCombo(ProviderSearchBox.Text);
    }

    private void RemotesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingRemotesGrid)
            return;

        if (RemotesGrid.SelectedItem is not RemoteRowItem row)
            return;

        _isEditingExistingRemote = true;
        RemoteEditorTitleText.Text = $"Editar Remote: {row.Name}:";
        RemoteNameBox.Text = row.Name;
        RemoteNameBox.IsReadOnly = true;
        RemoteProviderCombo.IsEnabled = false;
        ProviderSearchBox.Text = string.Empty;

        _currentRemoteParams = new Dictionary<string, string>(row.RawRemote.Parameters, StringComparer.OrdinalIgnoreCase);
        RemoteManualTokenBox.Text = string.Empty;
        SelectRemoteAuthMode(RemoteAuthMode.KeyOrCredentials);

        if (_allProviders.Count > 0)
            PopulateProviderCombo(string.Empty, row.Type);

        RebuildRemoteOptionsPanel();
        UpdateRemoteAuthGuidance();
    }

    private void NewRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        _isEditingExistingRemote = false;
        RemotesGrid.SelectedItem = null;
        RemoteEditorTitleText.Text = "Criar Novo Remote";
        RemoteNameBox.Text = string.Empty;
        RemoteNameBox.IsReadOnly = false;
        RemoteProviderCombo.IsEnabled = true;
        RemoteManualTokenBox.Text = string.Empty;
        _currentRemoteParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (_allProviders.Count > 0)
            PopulateProviderCombo(ProviderSearchBox.Text, "drive");

        RebuildRemoteOptionsPanel();
        UpdateRemoteAuthGuidance();
    }

    private void RemoteProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RemoteProviderCombo.SelectedItem is not ProviderListItem item)
            return;

        RemoteProviderDescText.Text = $"{item.Name} — {item.Description}";
        if (!_isEditingExistingRemote)
        {
            SelectRemoteAuthMode(KnownOAuthProviders.Contains(item.Name)
                ? RemoteAuthMode.OAuthBrowserLink
                : RemoteAuthMode.KeyOrCredentials);
        }

        RebuildRemoteOptionsPanel();
        UpdateRemoteAuthGuidance();
    }

    private void RemoteAuthModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateRemoteAuthGuidance();
    }

    private RemoteAuthMode GetSelectedRemoteAuthMode()
    {
        var tag = (RemoteAuthModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return Enum.TryParse<RemoteAuthMode>(tag, out var parsed)
            ? parsed
            : RemoteAuthMode.KeyOrCredentials;
    }

    private void SelectRemoteAuthMode(RemoteAuthMode mode)
    {
        var targetTag = mode.ToString();
        foreach (ComboBoxItem item in RemoteAuthModeCombo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), targetTag, StringComparison.Ordinal))
            {
                RemoteAuthModeCombo.SelectedItem = item;
                return;
            }
        }
    }

    private void UpdateRemoteAuthGuidance()
    {
        if (!IsLoaded)
            return;

        var providerName = (RemoteProviderCombo.SelectedItem as ProviderListItem)?.Name ?? string.Empty;
        var mode = GetSelectedRemoteAuthMode();

        DriveClientIdHintBorder.Visibility =
            providerName.Equals("drive", StringComparison.OrdinalIgnoreCase) && mode == RemoteAuthMode.OAuthBrowserLink
                ? Visibility.Visible
                : Visibility.Collapsed;

        ManualTokenPanel.Visibility = mode == RemoteAuthMode.ManualTokenJson
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OpenDriveClientIdGuide_Click(object sender, RoutedEventArgs e)
    {
        OpenShellTarget("https://rclone.org/drive/#making-your-own-client-id");
    }

    private void GenerateTokenViaAuthorize_Click(object sender, RoutedEventArgs e)
    {
        if (RemoteProviderCombo.SelectedItem is not ProviderListItem provItem)
            return;

        HarvestCurrentRemoteOptions();
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var store = new AppConfigStore(workDir);
        var cfg = store.LoadOrCreateDefault();

        if (!File.Exists(cfg.RcloneExecutablePath))
        {
            _ = _overlayHost.ShowAlertAsync("Rclone não encontrado", $"rclone.exe não encontrado em '{cfg.RcloneExecutablePath}'.", OverlayAlertKind.Error);
            return;
        }

        var args = $"authorize \"{provItem.Name}\"";
        if (_currentRemoteParams.TryGetValue("client_id", out var cid) && !string.IsNullOrWhiteSpace(cid) &&
            _currentRemoteParams.TryGetValue("client_secret", out var csec) && !string.IsNullOrWhiteSpace(csec))
        {
            args += $" \"{cid}\" \"{csec}\"";
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k \"\"{cfg.RcloneExecutablePath}\" {args}\"",
            WorkingDirectory = Directory.Exists(cfg.WorkingDirectory) ? cfg.WorkingDirectory : workDir,
            UseShellExecute = true
        });
        FooterInfoText.Text = "Janela do 'rclone authorize' aberta. Copie o JSON exibido no terminal e cole no campo de Token Manual.";
    }

    private void RemoteShowAdvancedCheck_Click(object sender, RoutedEventArgs e)
    {
        HarvestCurrentRemoteOptions();
        RebuildRemoteOptionsPanel();
    }

    private void HarvestCurrentRemoteOptions()
    {
        foreach (var (key, reader) in _activeRemoteOptionReaders)
        {
            var val = reader();
            if (!string.IsNullOrEmpty(val))
                _currentRemoteParams[key] = val;
            else
                _currentRemoteParams.Remove(key);
        }
    }

    private void RebuildRemoteOptionsPanel()
    {
        _activeRemoteOptionReaders.Clear();
        RemoteOptionsPanel.Children.Clear();

        if (RemoteProviderCombo.SelectedItem is not ProviderListItem item || item.RawProvider.Options == null)
            return;

        var showAdvanced = RemoteShowAdvancedCheck.IsChecked == true;
        var visibleOptions = item.RawProvider.Options
            .Where(o => o.Hide == 0 && (showAdvanced || !o.Advanced || _currentRemoteParams.ContainsKey(o.Name)))
            .ToList();

        if (visibleOptions.Count == 0)
        {
            RemoteOptionsPanel.Children.Add(new TextBlock
            {
                Text = "Este provedor não exige opções básicas adicionais. Marque 'Mostrar opções avançadas' se precisar personalizar parâmetros extras.",
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 6)
            });
            return;
        }

        foreach (var opt in visibleOptions)
        {
            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            var labelText = opt.Required ? $"{opt.Name} *" : opt.Name;
            if (opt.Advanced)
                labelText += " [avançado]";

            block.Children.Add(new TextBlock
            {
                Text = labelText,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252))
            });

            if (!string.IsNullOrWhiteSpace(opt.Help))
            {
                var helpSummary = opt.Help.Replace("\r", "").Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(helpSummary))
                {
                    block.Children.Add(new TextBlock
                    {
                        Text = helpSummary,
                        FontSize = 11,
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 1, 0, 4)
                    });
                }
            }

            _currentRemoteParams.TryGetValue(opt.Name, out var existingVal);

            if (opt.IsPassword)
            {
                var pwdBox = new System.Windows.Controls.PasswordBox
                {
                    Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(11, 18, 32)),
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(248, 250, 252)),
                    BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 5, 8, 5)
                };
                if (!string.IsNullOrWhiteSpace(existingVal))
                {
                    block.Children.Add(new TextBlock
                    {
                        Text = "(Valor já configurado; deixe em branco para manter o atual)",
                        FontSize = 10.5,
                        Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(56, 189, 248)),
                        Margin = new Thickness(0, 0, 0, 2)
                    });
                }
                block.Children.Add(pwdBox);
                _activeRemoteOptionReaders[opt.Name] = () => pwdBox.Password;
            }
            else
            {
                var txtBox = new System.Windows.Controls.TextBox
                {
                    Text = existingVal ?? string.Empty,
                    ToolTip = string.IsNullOrWhiteSpace(opt.DefaultStr) ? null : $"Padrão: {opt.DefaultStr}"
                };

                if (opt.Examples is { Count: > 0 })
                {
                    var examplesCombo = new System.Windows.Controls.ComboBox
                    {
                        Margin = new Thickness(0, 0, 0, 4)
                    };
                    var items = new List<string> { "(Selecionar opção rápida ou digitar abaixo)" };
                    items.AddRange(opt.Examples.Select(ex => string.IsNullOrWhiteSpace(ex.Help) ? ex.Value : $"{ex.Value} — {ex.Help}"));
                    examplesCombo.ItemsSource = items;
                    examplesCombo.SelectedIndex = 0;

                    var examplesList = opt.Examples;
                    examplesCombo.SelectionChanged += (_, _) =>
                    {
                        var idx = examplesCombo.SelectedIndex - 1;
                        if (idx >= 0 && idx < examplesList.Count)
                            txtBox.Text = examplesList[idx].Value;
                    };
                    block.Children.Add(examplesCombo);
                }

                var isFileOption = opt.Name.EndsWith("_file", StringComparison.OrdinalIgnoreCase) ||
                                   opt.Name.Contains("key_file", StringComparison.OrdinalIgnoreCase) ||
                                   opt.Name.Contains("cert", StringComparison.OrdinalIgnoreCase);

                if (isFileOption)
                {
                    var fileGrid = new Grid();
                    fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    fileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    Grid.SetColumn(txtBox, 0);
                    fileGrid.Children.Add(txtBox);

                    var browseBtn = new System.Windows.Controls.Button
                    {
                        Content = "Procurar...",
                        Margin = new Thickness(6, 0, 0, 0)
                    };
                    browseBtn.Click += (_, _) =>
                    {
                        using var dlg = new WinForms.OpenFileDialog
                        {
                            Filter = "Chaves e Certificados (*.json;*.pem;*.key;*.crt)|*.json;*.pem;*.key;*.crt|Todos os arquivos (*.*)|*.*"
                        };
                        if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                            txtBox.Text = dlg.FileName;
                    };
                    Grid.SetColumn(browseBtn, 1);
                    fileGrid.Children.Add(browseBtn);
                    block.Children.Add(fileGrid);
                }
                else
                {
                    block.Children.Add(txtBox);
                }
                _activeRemoteOptionReaders[opt.Name] = () => txtBox.Text.Trim();
            }

            RemoteOptionsPanel.Children.Add(block);
        }
    }

    private async void SaveRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        var remoteName = RemoteNameBox.Text.Trim().TrimEnd(':');
        if (string.IsNullOrWhiteSpace(remoteName))
        {
            await _overlayHost.ShowAlertAsync("Remotes", "Informe um nome para o serviço (ex.: gdrive).", OverlayAlertKind.Warning);
            return;
        }

        if (RemoteProviderCombo.SelectedItem is not ProviderListItem provItem)
        {
            await _overlayHost.ShowAlertAsync("Remotes", "Selecione o provedor/tipo do serviço.", OverlayAlertKind.Warning);
            return;
        }

        HarvestCurrentRemoteOptions();
        var authMode = GetSelectedRemoteAuthMode();

        var req = new CreateOrUpdateRemoteRequest
        {
            Name = remoteName,
            Type = provItem.Name,
            Parameters = new Dictionary<string, string>(_currentRemoteParams, StringComparer.OrdinalIgnoreCase),
            IsUpdate = _isEditingExistingRemote,
            AuthMode = authMode,
            ManualTokenJson = RemoteManualTokenBox.Text.Trim(),
            TriggerOAuthBrowser = authMode == RemoteAuthMode.OAuthBrowserLink,
            ObscurePasswords = true
        };

        SaveRemoteButton.IsEnabled = false;
        RemoteSaveStatusText.Text = authMode == RemoteAuthMode.OAuthBrowserLink
            ? "Executando rclone config e aguardando autenticação OAuth no navegador..."
            : "Salvando configuração no rclone.conf...";

        try
        {
            var resp = await _api.SaveRemoteAsync(req);
            RemoteSaveStatusText.Text = string.Empty;
            RemoteTestResultText.Text = resp?.Message ?? $"Remote '{remoteName}:' salvo com sucesso.";
            FooterInfoText.Text = RemoteTestResultText.Text;

            await RefreshRemotesTabDataAsync();
            await RefreshRemotesAndAboutAsync();
        }
        catch (Exception ex)
        {
            RemoteSaveStatusText.Text = $"Erro: {ex.Message}";
            await _overlayHost.ShowAlertAsync("Falha ao salvar remote", ex.Message, OverlayAlertKind.Error);
        }
        finally
        {
            SaveRemoteButton.IsEnabled = true;
        }
    }

    private async void ReconnectRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        var name = (RemotesGrid.SelectedItem as RemoteRowItem)?.Name ?? RemoteNameBox.Text.Trim().TrimEnd(':');
        if (string.IsNullOrWhiteSpace(name))
        {
            await _overlayHost.ShowAlertAsync("Remotes", "Selecione um serviço na lista para reautenticar.");
            return;
        }

        ReconnectRemoteButton.IsEnabled = false;
        RemoteSaveStatusText.Text = $"Abrindo navegador para reautenticar '{name}:' via OAuth...";
        try
        {
            var resp = await _api.ReconnectRemoteAsync(name);
            RemoteSaveStatusText.Text = string.Empty;
            RemoteTestResultText.Text = resp?.Message ?? $"Reautenticação concluída para '{name}:'.";
            FooterInfoText.Text = RemoteTestResultText.Text;
            await RefreshRemotesTabDataAsync();
        }
        catch (Exception ex)
        {
            RemoteSaveStatusText.Text = string.Empty;
            await _overlayHost.ShowAlertAsync($"Erro ao reautenticar '{name}:'", ex.Message, OverlayAlertKind.Error);
        }
        finally
        {
            ReconnectRemoteButton.IsEnabled = true;
        }
    }

    private async void TestRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        var name = (RemotesGrid.SelectedItem as RemoteRowItem)?.Name ?? RemoteNameBox.Text.Trim().TrimEnd(':');
        if (string.IsNullOrWhiteSpace(name))
        {
            await _overlayHost.ShowAlertAsync("Remotes", "Selecione um serviço na lista para testar a conexão.");
            return;
        }

        TestRemoteButton.IsEnabled = false;
        RemoteTestResultText.Text = $"Testando conexão com '{name}:'...";
        try
        {
            var res = await _api.TestRemoteAsync(name);
            if (res == null)
            {
                RemoteTestResultText.Text = "Sem resposta do serviço.";
                return;
            }

            if (res.Success)
            {
                var aboutText = res.About?.Free.HasValue == true
                    ? $" | Espaço livre: {FormatBytes(res.About.Free.Value)} de {FormatBytes(res.About.Total ?? 0)}"
                    : string.Empty;
                RemoteTestResultText.Text = res.Message + aboutText;
            }
            else
            {
                RemoteTestResultText.Text = $"Falha no teste de '{name}:': {res.Message}";
            }
        }
        catch (Exception ex)
        {
            RemoteTestResultText.Text = $"Erro ao testar '{name}:': {ex.Message}";
        }
        finally
        {
            TestRemoteButton.IsEnabled = true;
        }
    }

    private async void DeleteRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        var name = (RemotesGrid.SelectedItem as RemoteRowItem)?.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            await _overlayHost.ShowAlertAsync("Remotes", "Selecione um serviço na lista para excluir.");
            return;
        }

        var confirm = await _overlayHost.ShowConfirmAsync(
            "Excluir Serviço Conectado",
            $"Deseja realmente excluir '{name}:' da configuração local?\n(Isso não apaga seus arquivos na nuvem, apenas remove o acesso salvo neste PC).",
            confirmText: "Excluir",
            isDanger: true);

        if (!confirm)
            return;

        try
        {
            var resp = await _api.DeleteRemoteAsync(name);
            RemoteTestResultText.Text = resp?.Message ?? $"Remote '{name}:' removido.";
            FooterInfoText.Text = RemoteTestResultText.Text;
            NewRemoteButton_Click(sender, e);
            await RefreshRemotesTabDataAsync();
            await RefreshRemotesAndAboutAsync();
        }
        catch (Exception ex)
        {
            await _overlayHost.ShowAlertAsync("Erro ao excluir remote", ex.Message, OverlayAlertKind.Error);
        }
    }

    private async void RefreshRemotesTabButton_Click(object sender, RoutedEventArgs e)
    {
        _initialProvidersLoaded = false;
        await RefreshRemotesTabDataAsync();
        await RefreshRemotesAndAboutAsync();
    }

    private void OpenTerminalConfigButton_Click(object sender, RoutedEventArgs e)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var store = new AppConfigStore(workDir);
        var cfg = store.LoadOrCreateDefault();

        if (!File.Exists(cfg.RcloneExecutablePath))
        {
            _ = _overlayHost.ShowAlertAsync("Rclone não encontrado", $"rclone.exe não encontrado em '{cfg.RcloneExecutablePath}'.", OverlayAlertKind.Error);
            return;
        }

        var args = !string.IsNullOrWhiteSpace(cfg.RcloneConfigPath)
            ? $"config --config \"{cfg.RcloneConfigPath}\""
            : "config";

        Process.Start(new ProcessStartInfo
        {
            FileName = cfg.RcloneExecutablePath,
            Arguments = args,
            WorkingDirectory = Directory.Exists(cfg.WorkingDirectory) ? cfg.WorkingDirectory : workDir,
            UseShellExecute = true
        });
        FooterInfoText.Text = "Terminal interativo 'rclone config' aberto. Clique em 'Atualizar' na aba Remotes após concluir.";
    }

    #endregion

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double val = bytes;
        var idx = 0;
        while (val >= 1024 && idx < units.Length - 1)
        {
            val /= 1024;
            idx++;
        }
        return $"{val:0.##} {units[idx]}";
    }

    private static string FormatSpeed(double bytesPerSec)
    {
        return $"{FormatBytes((long)Math.Max(0, bytesPerSec))}/s";
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
            return "--";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes}m {ts.Seconds:D2}s";
        return $"{ts.Seconds}s";
    }
}
