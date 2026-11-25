using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using RcloneManager.Core.Engine;
using RcloneManager.Core.Models;
using RcloneManager.Core.Storage;
using RcloneManager.UI.Services;
using WinForms = System.Windows.Forms;

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

public partial class MainWindow : Window
{
    private readonly ServiceApiClient _api = new(5580);
    private readonly DispatcherTimer _pollTimer;
    private readonly TrayIconHelper _trayIcon;

    private List<SyncJob> _cachedJobs = new();
    private List<string> _cachedRemotes = new();
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
                MarkServiceOffline();
                return;
            }

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
            SyncBwComboSelection(status.CurrentBandwidthLimit);

            UpdateDashboard(status);
            UpdateJobsList(status.Jobs);

            if (!_initialFilterLoaded)
                await LoadFilterAnalysisAsync();

            if (!_initialRemotesLoaded)
                await RefreshRemotesAndAboutAsync();

            if (MainTabs.SelectedIndex == 3)
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
            MainTabs.SelectedIndex = 0;
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
            MainTabs.SelectedIndex = 0;
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
