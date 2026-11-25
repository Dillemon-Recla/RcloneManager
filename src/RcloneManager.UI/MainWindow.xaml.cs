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
using RcloneManager.Core.Security;
using RcloneManager.Core.Storage;
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

public partial class MainWindow : Window
{
    private readonly ServiceApiClient _api = new(5580);
    private readonly DispatcherTimer _pollTimer;
    private readonly TrayIconHelper _trayIcon;

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
    private bool _isClosingForReal;
    private bool _isUpdatingJobsGrid;
    private bool _initialFilterLoaded;
    private bool _initialRemotesLoaded;
    private bool _initialReleaseFieldsLoaded;
    private bool _pollInFlight;

    public MainWindow()
    {
        InitializeComponent();

        JobOperationCombo.ItemsSource = Enum.GetValues<RcloneOperation>();
        JobStrategyCombo.ItemsSource = Enum.GetValues<ExecutionStrategy>();
        JobFilterModeCombo.ItemsSource = Enum.GetValues<FilterRuleMode>();
        JobScheduleTypeCombo.ItemsSource = Enum.GetValues<ScheduleType>();

        _trayIcon = new TrayIconHelper(
            onOpenWindow: () => Dispatcher.Invoke(ShowFromTray),
            onRunPrimaryJob: () => Dispatcher.Invoke(RunPrimaryJobFromTray),
            onStopJob: () => Dispatcher.Invoke(StopActiveJobFromTray),
            onExitUiOnly: () => Dispatcher.Invoke(ExitUiKeepingService),
            onExitAll: () => Dispatcher.Invoke(ExitUiAndService));

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _pollTimer.Tick += async (_, _) => await PollServiceAsync();

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var store = new AppConfigStore(workDir);
        var cfg = store.LoadOrCreateDefault();

        var keyTarget = SensitiveDataProtector.EnsureInitialized(out var keyCreated, cfg.EncryptionKeyTarget);
        VaultStatusText.Text = keyCreated
            ? $"Cofre AES-256: Chave gerada ({keyTarget})"
            : $"Cofre AES-256: Ativo ({keyTarget})";

        if (!cfg.FirstRunCompleted)
        {
            FooterInfoText.Text = "Primeira execução detectada — abrindo Assistente Inicial...";
            var wizard = new FirstRunWizardWindow(workDir)
            {
                Owner = this
            };
            wizard.ShowDialog();
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
            var setupWindow = new RcloneSetupWindow(workDir, check.PinnedVersion, check.StatusMessage)
            {
                Owner = this
            };

            if (setupWindow.ShowDialog() == true && !string.IsNullOrWhiteSpace(setupWindow.DownloadedExecutablePath))
            {
                cfg.RcloneExecutablePath = setupWindow.DownloadedExecutablePath;
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
            VaultStatusText.Text = status.EncryptionKeyActive
                ? $"Cofre AES-256: Ativo ({status.EncryptionKeyTarget})"
                : "Cofre AES-256: Chave ausente";
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
            StatusText = j.LastStatus.ToString(),
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
        _selectedJobId = job.Id;
        JobIdBox.Text = job.Id;
        JobNameBox.Text = job.Name;
        JobEnabledCheck.IsChecked = job.Enabled;
        JobOperationCombo.SelectedItem = job.Operation;
        JobStrategyCombo.SelectedItem = job.Strategy;
        JobSourceBox.Text = job.SourcePath;
        JobDestBox.Text = job.DestinationPath;
        JobFilterPathBox.Text = job.FilterFilePath;
        JobFilterModeCombo.SelectedItem = job.FilterMode;
        JobMaxSizeBox.Text = job.MaxSize;
        JobBwLimitBox.Text = job.BandwidthLimit;
        JobLogFileBox.Text = job.LogFilePath;
        JobExtraFlagsBox.Text = job.ExtraFlags;
        JobSkipLinksCheck.IsChecked = job.SkipLinks;
        JobFastListCheck.IsChecked = job.FastList;
        JobCopyLinksCheck.IsChecked = job.CopyLinks;
        JobDryRunCheck.IsChecked = job.DryRun;

        JobScheduledCheck.IsChecked = job.Schedule.IsScheduled;
        JobScheduleTypeCombo.SelectedItem = job.Schedule.ScheduleType;
        JobIntervalBox.Text = job.Schedule.IntervalMinutes.ToString(CultureInfo.InvariantCulture);
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

        var interval = int.TryParse(JobIntervalBox.Text.Trim(), out var iv) && iv > 0 ? iv : 60;
        var maxRetries = int.TryParse(JobMaxRetriesBox.Text.Trim(), out var mr) && mr >= 0 ? mr : 3;
        var retryDelay = int.TryParse(JobRetryDelayBox.Text.Trim(), out var rd) && rd > 0 ? rd : 5;

        return new SyncJob
        {
            Id = string.IsNullOrWhiteSpace(JobIdBox.Text) ? Guid.NewGuid().ToString("N")[..8] : JobIdBox.Text.Trim(),
            Name = string.IsNullOrWhiteSpace(JobNameBox.Text) ? "Tarefa Sem Nome" : JobNameBox.Text.Trim(),
            Enabled = JobEnabledCheck.IsChecked == true,
            Operation = JobOperationCombo.SelectedItem is RcloneOperation op ? op : RcloneOperation.Sync,
            Strategy = JobStrategyCombo.SelectedItem is ExecutionStrategy st ? st : ExecutionStrategy.HybridAuto,
            SourcePath = JobSourceBox.Text.Trim(),
            DestinationPath = JobDestBox.Text.Trim(),
            FilterFilePath = JobFilterPathBox.Text.Trim(),
            FilterMode = JobFilterModeCombo.SelectedItem is FilterRuleMode fm ? fm : FilterRuleMode.AutoDetect,
            MaxSize = JobMaxSizeBox.Text.Trim(),
            BandwidthLimit = JobBwLimitBox.Text.Trim(),
            LogFilePath = JobLogFileBox.Text.Trim(),
            ExtraFlags = JobExtraFlagsBox.Text.Trim(),
            SkipLinks = JobSkipLinksCheck.IsChecked == true,
            FastList = JobFastListCheck.IsChecked == true,
            CopyLinks = JobCopyLinksCheck.IsChecked == true,
            DryRun = JobDryRunCheck.IsChecked == true,
            Schedule = new JobSchedule
            {
                IsScheduled = JobScheduledCheck.IsChecked == true,
                ScheduleType = JobScheduleTypeCombo.SelectedItem is ScheduleType sch ? sch : ScheduleType.IntervalMinutes,
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
        var fresh = new SyncJob
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            Name = "Nova Tarefa de Sincronização",
            SourcePath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            DestinationPath = _cachedRemotes.FirstOrDefault() ?? "gdrive:BackupDocs/",
            LogFilePath = "rclone_backup.log"
        };
        JobsGrid.SelectedItem = null;
        PopulateJobForm(fresh);
        FooterInfoText.Text = "Preencha os dados e clique em 'Salvar Tarefa e Recalcular Agendamento'.";
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
                JobIdBox.Text = saved.Id;
                FooterInfoText.Text = $"Tarefa '{saved.Name}' salva! Próxima execução: {(saved.NextRunAt.HasValue ? saved.NextRunAt.Value.ToString("dd/MM/yyyy HH:mm") : "Manual")}.";
                await PollServiceAsync();
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Erro ao salvar tarefa: {ex.Message}", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Error);
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
            System.Windows.MessageBox.Show($"Erro ao iniciar tarefa: {ex.Message}", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DryRunSelectedJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedJobId))
            return;

        try
        {
            await _api.RunJobAsync(_selectedJobId, dryRun: true);
            FooterInfoText.Text = $"Simulação (Dry-Run) iniciada para '{JobNameBox.Text}'. Nenhum arquivo será alterado.";
            MainTabs.SelectedItem = DashboardTab;
            await PollServiceAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Erro ao simular tarefa: {ex.Message}", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private async void DeleteSelectedJobButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedJobId))
            return;

        var confirm = System.Windows.MessageBox.Show(
            $"Deseja excluir a tarefa '{JobNameBox.Text}'?",
            "Confirmar Exclusão",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
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
            System.Windows.MessageBox.Show($"Erro ao excluir tarefa: {ex.Message}", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void QuickRunPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        var primary = _cachedJobs.FirstOrDefault(j => j.Enabled) ?? _cachedJobs.FirstOrDefault();
        if (primary == null)
        {
            System.Windows.MessageBox.Show("Nenhuma tarefa configurada ainda.", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Information);
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
            System.Windows.MessageBox.Show($"Erro ao executar tarefa: {ex.Message}", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Error);
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
            System.Windows.MessageBox.Show($"Erro ao alternar daemon RC: {ex.Message}", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ApplyBwButton_Click(object sender, RoutedEventArgs e)
    {
        var rate = (BwLimitCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "off";
        try
        {
            await _api.SetBandwidthLimitAsync(rate);
            FooterInfoText.Text = $"Limite de banda ajustado para '{rate}'.";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Erro ao aplicar limite de banda: {ex.Message}", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RefreshRemotesButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRemotesAndAboutAsync();
    }

    private async void RefreshDestRemotesButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshRemotesAndAboutAsync();
        if (_cachedRemotes.Count > 0 && string.IsNullOrWhiteSpace(JobDestBox.Text))
        {
            JobDestBox.Text = _cachedRemotes[0] + "BackupPC/";
        }
        else if (_cachedRemotes.Count > 0)
        {
            FooterInfoText.Text = $"Remotes disponíveis: {string.Join(", ", _cachedRemotes)}";
        }
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
                RemotesListText.Text = _cachedRemotes.Count > 0
                    ? $"{string.Join(", ", _cachedRemotes)} ({remResp.SourceUsed})"
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

        var confirm = System.Windows.MessageBox.Show(
            "Isso criará um backup (.bak) do arquivo atual e reordenará as exclusões para o topo antes dos includes (+), corrigindo também a sintaxe '[-- max-size ...]' para '- *'. Deseja continuar?",
            "Otimizar Regras de Filtro",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
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
            System.Windows.MessageBox.Show($"Erro ao otimizar arquivo: {ex.Message}", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Error);
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
            System.Windows.MessageBox.Show($"Erro ao salvar arquivo: {ex.Message}", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Error);
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
            System.Windows.MessageBox.Show($"Erro ao alterar inicialização: {ex.Message}", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Error);
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
        var confirm = System.Windows.MessageBox.Show(
            "Encerrar o serviço em 2º plano interromperá o agendador e qualquer tarefa em andamento. Continuar?",
            "Encerrar Serviço",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
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

    private void InstallWindowsServiceButton_Click(object sender, RoutedEventArgs e)
    {
        RunElevatedServiceCommand("--install-service", "Serviço do Windows registrado via sc.exe.");
    }

    private void UninstallWindowsServiceButton_Click(object sender, RoutedEventArgs e)
    {
        RunElevatedServiceCommand("--uninstall-service", "Serviço do Windows removido via sc.exe.");
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
            System.Windows.MessageBox.Show(ex.Message, "Versão Inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                var setupWindow = new RcloneSetupWindow(
                    workDir,
                    targetVersion,
                    $"Provisionando e travando a release {targetVersion}...")
                {
                    Owner = this
                };

                if (setupWindow.ShowDialog() != true || string.IsNullOrWhiteSpace(setupWindow.DownloadedExecutablePath))
                {
                    FooterInfoText.Text = "Alteração de release cancelada.";
                    return;
                }

                cfg.RcloneExecutablePath = setupWindow.DownloadedExecutablePath;
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
            System.Windows.MessageBox.Show($"Erro ao baixar/travar release: {ex.Message}", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Error);
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
            System.Windows.MessageBox.Show("RcloneManager.Service.exe não encontrado.", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Error);
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
            System.Windows.MessageBox.Show($"Erro ao executar operação de serviço: {ex.Message}", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Error);
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
            if (string.Equals(item.Content?.ToString(), target, StringComparison.OrdinalIgnoreCase))
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
        _isClosingForReal = true;
        Close();
    }

    private async void ExitUiAndService()
    {
        try { await _api.ShutdownServiceAsync(); } catch { }
        _isClosingForReal = true;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isClosingForReal)
        {
            _pollTimer.Stop();
            _trayIcon.Dispose();
            _api.Dispose();
            return;
        }

        var choice = System.Windows.MessageBox.Show(
            "Deseja minimizar para a Bandeja do Sistema (Sim) ou fechar a interface mantendo apenas o Serviço em 2º plano (Não)?\n\n" +
            "• Sim = Minimizar para a Bandeja\n" +
            "• Não = Fechar Interface (Serviço continua ativo)\n" +
            "• Cancelar = Voltar à janela",
            "Fechar RcloneManager",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (choice == MessageBoxResult.Yes)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloon("RcloneManager", "O aplicativo continua rodando na bandeja do sistema.");
            return;
        }

        // MessageBoxResult.No -> fecha a UI mantendo o serviço
        _isClosingForReal = true;
        _pollTimer.Stop();
        _trayIcon.Dispose();
        _api.Dispose();
    }

    private void Window_PreviewKeyDown(object sender, WpfInput.KeyEventArgs e)
    {
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
            System.Windows.MessageBox.Show(
                $"Configuração exportada para:\n{dialog.FileName}\n\n" +
                "Por segurança, tokens OAuth, senhas e a chave AES-256 do Windows Credential Manager NÃO foram incluídos.\n" +
                "Ao importar este arquivo em outra instalação, o Assistente Inicial gerará uma nova chave e conduzirá a autenticação dos serviços apontados.",
                "Exportação Concluída",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Erro ao exportar configuração: {ex.Message}", "Exportar Configuração", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void MenuImportConfig_Click(object sender, RoutedEventArgs e)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "Importar Configuração pelo Assistente (Wizard)",
            Filter = "Configuração RcloneManager (*.json)|*.json|Todos os arquivos (*.*)|*.*",
            InitialDirectory = workDir
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        var wizard = new FirstRunWizardWindow(workDir, _api, dialog.FileName)
        {
            Owner = this
        };

        if (wizard.ShowDialog() == true)
        {
            _selectedJobId = null;
            _initialProvidersLoaded = false;
            await PollServiceAsync();
            await RefreshRemotesTabDataAsync();
            await RefreshRemotesAndAboutAsync();
            FooterInfoText.Text = $"Configuração de '{Path.GetFileName(dialog.FileName)}' importada, autenticada e revisada com nova chave no Cofre do Windows.";
        }
    }

    private async void MenuRunWizard_Click(object sender, RoutedEventArgs e)
    {
        var workDir = ServiceLauncher.ResolveWorkspaceDirectory();
        var wizard = new FirstRunWizardWindow(workDir, _api) { Owner = this };
        if (wizard.ShowDialog() == true)
        {
            await PollServiceAsync();
            await RefreshRemotesTabDataAsync();
            await RefreshRemotesAndAboutAsync();
            FooterInfoText.Text = "Configurações do Assistente Inicial aplicadas.";
        }
    }

    private async void MenuResetConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ResetConfigWindow { Owner = this };
        if (dialog.ShowDialog() != true)
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

            _selectedJobId = null;
            _initialProvidersLoaded = false;
            await PollServiceAsync();
            await RefreshRemotesTabDataAsync();
            await RefreshRemotesAndAboutAsync();
            await RefreshLogsAsync();

            FooterInfoText.Text = "Configurações resetadas com sucesso.";

            if (dialog.ReopenWizard)
            {
                var wizard = new FirstRunWizardWindow(workDir, _api) { Owner = this };
                if (wizard.ShowDialog() == true)
                {
                    await PollServiceAsync();
                    await RefreshRemotesTabDataAsync();
                    await RefreshRemotesAndAboutAsync();
                }
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Erro ao resetar configurações: {ex.Message}", "Resetar Configurações", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void MenuExitAll_Click(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(
            "Isso encerrará a interface e o serviço de agendamento em segundo plano. Continuar?",
            "Sair do RcloneManager",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
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

        System.Windows.MessageBox.Show(
            $"{check.StatusMessage}\n\nCaminho: {check.ExecutablePath}\nVersão Instalada: {check.InstalledVersion}\nRelease Travada: {check.PinnedVersion} ({(check.IsLocked ? "Ativa" : "Livre")})",
            "Verificação de Release do Rclone",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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

    private void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow(_lastStatus, ServiceLauncher.ResolveWorkspaceDirectory())
        {
            Owner = this
        };
        about.ShowDialog();
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
            System.Windows.MessageBox.Show($"Não foi possível abrir '{target}': {ex.Message}", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            System.Windows.MessageBox.Show($"rclone.exe não encontrado em '{cfg.RcloneExecutablePath}'.", "Remotes", MessageBoxButton.OK, MessageBoxImage.Error);
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
            System.Windows.MessageBox.Show("Informe um nome para o remote.", "Remotes", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (RemoteProviderCombo.SelectedItem is not ProviderListItem provItem)
        {
            System.Windows.MessageBox.Show("Selecione o provedor/tipo do remote.", "Remotes", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            System.Windows.MessageBox.Show($"Falha ao salvar remote: {ex.Message}", "Remotes", MessageBoxButton.OK, MessageBoxImage.Error);
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
            System.Windows.MessageBox.Show("Selecione um remote na lista para reautenticar.", "Remotes", MessageBoxButton.OK, MessageBoxImage.Information);
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
            System.Windows.MessageBox.Show($"Erro ao reautenticar '{name}:': {ex.Message}", "Remotes", MessageBoxButton.OK, MessageBoxImage.Error);
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
            System.Windows.MessageBox.Show("Selecione um remote na lista para testar.", "Remotes", MessageBoxButton.OK, MessageBoxImage.Information);
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
            System.Windows.MessageBox.Show("Selecione um remote na lista para excluir.", "Remotes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"Deseja realmente excluir o remote '{name}:' do rclone.conf?\n(Isso não apaga os arquivos na nuvem, apenas remove a configuração local).",
            "Excluir Remote",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
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
            System.Windows.MessageBox.Show($"Erro ao excluir remote: {ex.Message}", "Remotes", MessageBoxButton.OK, MessageBoxImage.Error);
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
            System.Windows.MessageBox.Show($"rclone.exe não encontrado em '{cfg.RcloneExecutablePath}'.", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Error);
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
