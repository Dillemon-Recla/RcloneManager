using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RcloneManager.Core.Engine;
using RcloneManager.Core.Models;
using RcloneManager.Core.Onboarding;
using RcloneManager.Core.Storage;
using RcloneManager.UI.Services;
using MediaColor = System.Windows.Media.Color;
using WinForms = System.Windows.Forms;

namespace RcloneManager.UI;

internal sealed class WizardJobReviewItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public SyncJob Job { get; init; } = new();

    public bool IsEnabled
    {
        get => Job.Enabled;
        set
        {
            if (Job.Enabled != value)
            {
                Job.Enabled = value;
                NotifyChanged();
            }
        }
    }

    public string TitleText
    {
        get
        {
            var opLabel = Job.Operation switch
            {
                RcloneOperation.Sync => "Sincronizar",
                RcloneOperation.Copy => "Copiar novidades",
                RcloneOperation.Move => "Mover",
                RcloneOperation.Check => "Verificar",
                _ => Job.Operation.ToString()
            };
            return $"{Job.Name} ({opLabel})";
        }
    }

    public string RouteText => $"{Job.SourcePath}  →  {Job.DestinationPath}";

    public void NotifyChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TitleText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RouteText)));
    }
}

public partial class FirstRunWizardWindow : System.Windows.Controls.UserControl, IOverlayDialog
{
    private enum WizardStartMode
    {
        None,
        Scratch,
        Import
    }

    private sealed record WizardDestOption(bool IsLocal, string TargetValue, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record WizardFrequencyOption(bool IsScheduled, ScheduleType Type, int IntervalMinutes, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly Dictionary<string, string> DefaultAliasByProvider = new(StringComparer.OrdinalIgnoreCase)
    {
        ["drive"] = "gdrive",
        ["onedrive"] = "onedrive",
        ["dropbox"] = "dropbox",
        ["s3"] = "s3backup",
        ["sftp"] = "servidor_sftp",
        ["ftp"] = "servidor_ftp",
        ["smb"] = "rede_smb",
        ["webdav"] = "nextcloud"
    };

    private static readonly HashSet<string> KnownDefaultAliases = new(DefaultAliasByProvider.Values, StringComparer.OrdinalIgnoreCase);

    public Action<bool?>? CloseOverlay { get; set; }
    public bool CanDismissWithEscape => !_isConnectingProvider;

    private readonly IWizardOnboardingService _onboarding;
    private readonly string? _initialImportFilePath;
    private readonly List<WizardJobReviewItem> _reviewJobs = new();
    private readonly Dictionary<string, HashSet<string>> _verifiedRemotesByProvider = new(StringComparer.OrdinalIgnoreCase);

    private WizardStartMode _startMode = WizardStartMode.None;
    private bool _hasImportedFile;
    private bool _isStep2Ready;
    private bool _isConnectingProvider;
    private CancellationTokenSource? _authCts;
    private string? _verifiedLocalFolderPath;
    private string? _lastVerifiedRemoteAlias;
    private IReadOnlyList<RcloneRemoteDetail> _existingRemoteDetails = Array.Empty<RcloneRemoteDetail>();
    private int _selectedJobIndex = -1;
    private bool _isPopulatingJobFields;
    private bool _isInitializingUi;
    private bool _isUpdatingProviderControls;
    private int _step = 1;

    public FirstRunWizardWindow(
        string workDir,
        ServiceApiClient? api = null,
        string? initialImportFilePath = null,
        IWizardOnboardingService? onboardingService = null)
    {
        InitializeComponent();
        _onboarding = onboardingService ?? new WizardOnboardingService(workDir, api);
        _initialImportFilePath = initialImportFilePath;

        InitializeStep4Controls();
        Loaded += FirstRunWizardWindow_Loaded;
    }

    public void OnOverlayDismissed()
    {
        if (_isConnectingProvider)
            _authCts?.Cancel();
    }

    private void InitializeStep4Controls()
    {
        var opOptions = UiOption<RcloneOperation>.CreateOperationOptions();
        ScratchOperationCombo.ItemsSource = opOptions;
        ScratchOperationCombo.SelectedIndex = 0;

        WizardJobOpCombo.ItemsSource = opOptions;
        WizardJobOpCombo.SelectedIndex = 0;

        var freqOptions = new[]
        {
            new WizardFrequencyOption(true, ScheduleType.IntervalMinutes, 30, "A cada 30 minutos"),
            new WizardFrequencyOption(true, ScheduleType.IntervalMinutes, 60, "A cada 1 hora"),
            new WizardFrequencyOption(true, ScheduleType.IntervalMinutes, 120, "A cada 2 horas (Recomendado)"),
            new WizardFrequencyOption(true, ScheduleType.IntervalMinutes, 240, "A cada 4 horas"),
            new WizardFrequencyOption(true, ScheduleType.IntervalMinutes, 360, "A cada 6 horas"),
            new WizardFrequencyOption(true, ScheduleType.DailyAtTime, 1440, "1 vez por dia (às 14:00)"),
            new WizardFrequencyOption(false, ScheduleType.IntervalMinutes, 120, "Somente manual")
        };

        ScratchFrequencyCombo.ItemsSource = freqOptions;
        ScratchFrequencyCombo.SelectedIndex = 2; // 2 horas

        WizardImportFrequencyCombo.ItemsSource = freqOptions;
        WizardImportFrequencyCombo.SelectedIndex = 2;

        var serviceModeOptions = new[]
        {
            new UiOption<BackgroundServiceMode>(
                BackgroundServiceMode.UserStartup,
                "Iniciar com meu usuário do Windows (Recomendado — não pede Administrador)"),
            new UiOption<BackgroundServiceMode>(
                BackgroundServiceMode.WindowsService,
                "Instalar Serviço Nativo do Windows (Servidor / antes do login — pede Admin 1 vez)"),
            new UiOption<BackgroundServiceMode>(
                BackgroundServiceMode.ManualOnly,
                "Somente manual (funciona apenas quando eu abrir o RcloneManager)")
        };

        WizardServiceModeCombo.ItemsSource = serviceModeOptions;
        WizardServiceModeCombo.SelectedIndex = 0;

        WizardImportServiceModeCombo.ItemsSource = serviceModeOptions;
        WizardImportServiceModeCombo.SelectedIndex = 0;

        UpdateServiceModeHints();

        // Tooltips mostrando o caminho real de cada caixa de seleção
        FolderDocsCheck.ToolTip = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        FolderDesktopCheck.ToolTip = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        FolderPicturesCheck.ToolTip = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        FolderVideosCheck.ToolTip = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        FolderMusicCheck.ToolTip = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        FolderDownloadsCheck.ToolTip = Path.Combine(userProfile, "Downloads");
        FolderFullProfileCheck.ToolTip = userProfile;
    }

    private async void FirstRunWizardWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _isInitializingUi = true;
        try
        {
            WizardPinnedVersionBox.Text = _onboarding.CurrentConfig.PinnedRcloneVersion;
            WizardLockVersionCheck.IsChecked = _onboarding.CurrentConfig.LockRcloneVersion;
            WizardLocalDestFolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MeusBackups");
        }
        finally
        {
            _isInitializingUi = false;
        }

        ConfigureStep3ForProvider(GetSelectedProviderTag(), preserveSelectedAuthMode: false);

        if (!string.IsNullOrWhiteSpace(_initialImportFilePath) && File.Exists(_initialImportFilePath))
        {
            _startMode = WizardStartMode.Import;
            await LoadImportedBundleAsync(_initialImportFilePath);
        }
        else
        {
            SyncReviewJobsFromConfig();
            await RefreshStep2StatusAsync();
            await RefreshStep3RemotesAsync(verifyImportedConnections: false);
        }

        UpdateStartModeCardsVisual();
        UpdateStepUi();
    }

    private void StartScratchCard_Click(object sender, MouseButtonEventArgs e) => ApplyScratchChoice();

    private void SelectScratchButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ApplyScratchChoice();
    }

    private async void ApplyScratchChoice()
    {
        _startMode = WizardStartMode.Scratch;
        _hasImportedFile = false;
        _verifiedRemotesByProvider.Clear();
        _verifiedLocalFolderPath = null;
        _lastVerifiedRemoteAlias = null;
        _onboarding.ResetToCleanStart();

        _isInitializingUi = true;
        try
        {
            WizardPinnedVersionBox.Text = _onboarding.CurrentConfig.PinnedRcloneVersion;
            WizardLockVersionCheck.IsChecked = _onboarding.CurrentConfig.LockRcloneVersion;
        }
        finally
        {
            _isInitializingUi = false;
        }

        WizardImportSummaryText.Text = "Já usa o RcloneManager em outro PC? Carregue seu arquivo exportado (.json) para trazer todas as suas tarefas prontas e apenas validar suas contas.";
        WizardImportSummaryText.Foreground = new SolidColorBrush(MediaColor.FromRgb(148, 163, 184));

        SyncReviewJobsFromConfig();
        await RefreshStep2StatusAsync();
        await RefreshStep3RemotesAsync(verifyImportedConnections: false);

        ConfigureStep3ForProvider(GetSelectedProviderTag(), preserveSelectedAuthMode: true);
        UpdateStartModeCardsVisual();
        UpdateStepUi();
    }

    private void StartImportCard_Click(object sender, MouseButtonEventArgs e)
    {
        _startMode = WizardStartMode.Import;
        UpdateStartModeCardsVisual();
        UpdateStepUi();

        if (!_hasImportedFile)
            PromptAndLoadImportFile();
    }

    private void WizardImportConfigButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _startMode = WizardStartMode.Import;
        UpdateStartModeCardsVisual();
        UpdateStepUi();
        PromptAndLoadImportFile();
    }

    private async void PromptAndLoadImportFile()
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "Selecionar Configuração Exportada (.json)",
            Filter = "Configuração Portátil (*.json)|*.json|Todos os arquivos (*.*)|*.*",
            InitialDirectory = _onboarding.WorkDirectory
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
        {
            UpdateStartModeCardsVisual();
            UpdateStepUi();
            return;
        }

        await LoadImportedBundleAsync(dialog.FileName);
    }

    private async Task LoadImportedBundleAsync(string filePath)
    {
        try
        {
            var summary = _onboarding.ImportPortableBundle(filePath);
            _startMode = WizardStartMode.Import;
            _hasImportedFile = true;
            _verifiedRemotesByProvider.Clear();
            _verifiedLocalFolderPath = null;
            _lastVerifiedRemoteAlias = null;

            _isInitializingUi = true;
            try
            {
                WizardPinnedVersionBox.Text = _onboarding.CurrentConfig.PinnedRcloneVersion;
                WizardLockVersionCheck.IsChecked = _onboarding.CurrentConfig.LockRcloneVersion;
            }
            finally
            {
                _isInitializingUi = false;
            }

            SyncReviewJobsFromConfig();

            WizardImportSummaryText.Text = summary;
            WizardImportSummaryText.Foreground = new SolidColorBrush(MediaColor.FromRgb(16, 185, 129));

            await RefreshStep2StatusAsync();
            await RefreshStep3RemotesAsync(verifyImportedConnections: false);

            UpdateStartModeCardsVisual();
            UpdateStepUi();
        }
        catch (Exception ex)
        {
            _hasImportedFile = false;
            UpdateStartModeCardsVisual();
            UpdateStepUi();
            await OverlayHost.ShowCurrentAlertAsync("Importar no Assistente", $"Erro ao importar arquivo: {ex.Message}", OverlayAlertKind.Error);
        }
    }

    private void UpdateStartModeCardsVisual()
    {
        var neutralBorder = new SolidColorBrush(MediaColor.FromRgb(51, 65, 85));
        var scratchActiveBorder = new SolidColorBrush(MediaColor.FromRgb(16, 185, 129));
        var importActiveBorder = new SolidColorBrush(MediaColor.FromRgb(37, 99, 235));
        var defaultBg = new SolidColorBrush(MediaColor.FromRgb(11, 18, 32));
        var selectedBg = new SolidColorBrush(MediaColor.FromRgb(17, 29, 51));

        var isScratch = _startMode == WizardStartMode.Scratch;
        var isImport = _startMode == WizardStartMode.Import;

        StartScratchCard.BorderBrush = isScratch ? scratchActiveBorder : neutralBorder;
        StartScratchCard.Background = isScratch ? selectedBg : defaultBg;
        ScratchSelectedPill.Visibility = isScratch ? Visibility.Visible : Visibility.Collapsed;
        SelectScratchButton.Content = isScratch ? "✓ Selecionado" : "Começar do Zero";

        StartImportCard.BorderBrush = isImport ? (_hasImportedFile ? scratchActiveBorder : importActiveBorder) : neutralBorder;
        StartImportCard.Background = isImport ? selectedBg : defaultBg;
        ImportSelectedPill.Visibility = isImport ? Visibility.Visible : Visibility.Collapsed;
        ImportSelectedPill.Background = _hasImportedFile ? scratchActiveBorder : importActiveBorder;
        ImportSelectedPillText.Text = _hasImportedFile ? "✓ Arquivo carregado" : "Aguardando arquivo";
        WizardImportConfigButton.Content = _hasImportedFile ? "Trocar Arquivo..." : "Escolher Arquivo...";
    }

    #region Etapa 4 — Caixas de Seleção (Scratch) & Revisão Visual (Import)

    private void RefreshStep4ModeAndTargets()
    {
        var isImport = _startMode == WizardStartMode.Import;
        Step4ScratchPanel.Visibility = isImport ? Visibility.Collapsed : Visibility.Visible;
        Step4ImportPanel.Visibility = isImport ? Visibility.Visible : Visibility.Collapsed;

        var destOptions = BuildAvailableDestOptions();

        if (!isImport)
        {
            ScratchDestTargetCombo.ItemsSource = destOptions;
            WizardDestOption? preferred = null;
            if (GetSelectedProviderTag().Equals("local", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_verifiedLocalFolderPath))
            {
                preferred = destOptions.FirstOrDefault(o => o.IsLocal);
            }
            else if (!string.IsNullOrWhiteSpace(_lastVerifiedRemoteAlias))
            {
                preferred = destOptions.FirstOrDefault(o => !o.IsLocal && o.TargetValue.Equals(_lastVerifiedRemoteAlias, StringComparison.OrdinalIgnoreCase));
            }
            ScratchDestTargetCombo.SelectedItem = preferred ?? destOptions.FirstOrDefault();
            UpdateScratchDestVisibility();
        }
        else
        {
            WizardImportDestTargetCombo.ItemsSource = destOptions;
            RefreshJobsReviewListUi(_selectedJobIndex >= 0 ? _selectedJobIndex : 0);
        }
    }

    private List<WizardDestOption> BuildAvailableDestOptions()
    {
        var list = new List<WizardDestOption>();
        var addedRemotes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (prov, aliases) in _verifiedRemotesByProvider)
        {
            foreach (var alias in aliases)
            {
                if (addedRemotes.Add(alias))
                    list.Add(new WizardDestOption(false, alias, $"☁ Nuvem: {alias} ({prov})"));
            }
        }

        foreach (var d in _existingRemoteDetails)
        {
            if (addedRemotes.Add(d.Name))
                list.Add(new WizardDestOption(false, d.Name, $"☁ Nuvem: {d.Name} ({d.Type})"));
        }

        foreach (var imp in _onboarding.ImportedRemotes)
        {
            if (addedRemotes.Add(imp.Blueprint.Name))
                list.Add(new WizardDestOption(false, imp.Blueprint.Name, $"☁ Nuvem: {imp.Blueprint.Name} ({imp.Blueprint.Type})"));
        }

        var localBase = !string.IsNullOrWhiteSpace(_verifiedLocalFolderPath)
            ? _verifiedLocalFolderPath
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MeusBackups");

        list.Add(new WizardDestOption(true, localBase, $"📁 Pasta Local: {localBase}"));
        return list;
    }

    private void ScratchDestTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateScratchDestVisibility();
        UpdateStepGateState();
    }

    private void UpdateScratchDestVisibility()
    {
        if (ScratchSubfolderPanel == null)
            return;

        var isLocal = (ScratchDestTargetCombo.SelectedItem as WizardDestOption)?.IsLocal == true;
        ScratchSubfolderPanel.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ScratchBaseFolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateStepGateState();
    }

    private void ScratchFolderCheck_Click(object sender, RoutedEventArgs e)
    {
        UpdateStepGateState();
    }

    private void FolderFullProfileCheck_Click(object sender, RoutedEventArgs e)
    {
        var fullChecked = FolderFullProfileCheck.IsChecked == true;
        ScratchStandardFoldersGrid.IsEnabled = !fullChecked;
        ScratchStandardFoldersGrid.Opacity = fullChecked ? 0.45 : 1.0;

        if (!fullChecked && !HasAnyStandardOrCustomFolderChecked())
        {
            FolderDocsCheck.IsChecked = true;
            FolderDesktopCheck.IsChecked = true;
            FolderPicturesCheck.IsChecked = true;
        }

        UpdateStepGateState();
    }

    private void FolderCustomCheck_Click(object sender, RoutedEventArgs e)
    {
        var customOn = FolderCustomCheck.IsChecked == true;
        ScratchCustomFolderBox.IsEnabled = customOn;
        ScratchBrowseCustomButton.IsEnabled = customOn;

        if (customOn && string.IsNullOrWhiteSpace(ScratchCustomFolderBox.Text))
            ScratchBrowseCustomButton_Click(sender, e);

        UpdateStepGateState();
    }

    private void ScratchCustomFolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateStepGateState();
    }

    private void ScratchBrowseCustomButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Escolha a pasta adicional que deseja incluir no backup",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(ScratchCustomFolderBox.Text) ? ScratchCustomFolderBox.Text : string.Empty
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            ScratchCustomFolderBox.Text = dialog.SelectedPath;
            FolderCustomCheck.IsChecked = true;
            ScratchCustomFolderBox.IsEnabled = true;
            ScratchBrowseCustomButton.IsEnabled = true;
        }

        UpdateStepGateState();
    }

    private bool HasAnyStandardOrCustomFolderChecked()
    {
        return FolderDocsCheck.IsChecked == true ||
               FolderDesktopCheck.IsChecked == true ||
               FolderPicturesCheck.IsChecked == true ||
               FolderVideosCheck.IsChecked == true ||
               FolderMusicCheck.IsChecked == true ||
               FolderDownloadsCheck.IsChecked == true ||
               (FolderCustomCheck.IsChecked == true && !string.IsNullOrWhiteSpace(ScratchCustomFolderBox.Text));
    }

    private bool IsScratchStep4Valid()
    {
        if (ScratchDestTargetCombo?.SelectedItem is not WizardDestOption)
            return false;

        if (FolderFullProfileCheck.IsChecked == true)
            return true;

        if (FolderCustomCheck.IsChecked == true && string.IsNullOrWhiteSpace(ScratchCustomFolderBox.Text))
            return false;

        return HasAnyStandardOrCustomFolderChecked();
    }

    private List<SyncJob> BuildJobsFromScratchSelections()
    {
        var jobs = new List<SyncJob>();
        var targetOpt = ScratchDestTargetCombo.SelectedItem as WizardDestOption;
        var isLocalDest = targetOpt?.IsLocal == true;
        var baseCloudFolder = ScratchBaseFolderBox.Text.Trim().Trim('/', '\\', ':');
        var operation = (ScratchOperationCombo.SelectedItem as UiOption<RcloneOperation>)?.Value ?? RcloneOperation.Sync;
        var freq = ScratchFrequencyCombo.SelectedItem as WizardFrequencyOption
                   ?? new WizardFrequencyOption(true, ScheduleType.IntervalMinutes, 120, "A cada 2 horas");

        var logFile = Path.Combine(_onboarding.WorkDirectory, "rclone_backup.log");
        var filterFile = WorkspacePathResolver.FindExistingFilterFile(_onboarding.WorkDirectory);

        string BuildDestForFolder(string subfolderSlug)
        {
            if (isLocalDest)
            {
                var rootLocal = targetOpt!.TargetValue;
                return Path.Combine(rootLocal, subfolderSlug);
            }

            var remote = targetOpt?.TargetValue ?? "gdrive";
            return string.IsNullOrWhiteSpace(baseCloudFolder)
                ? $"{remote}:{subfolderSlug}/"
                : $"{remote}:{baseCloudFolder}/{subfolderSlug}/";
        }

        SyncJob CreateScratchJob(string title, string sourcePath, string subfolderSlug, bool attachDefaultFilter = false)
        {
            var isDriveDest = !isLocalDest &&
                              (GetSelectedProviderTag().Equals("drive", StringComparison.OrdinalIgnoreCase) ||
                               (targetOpt?.Label.Contains("(drive)", StringComparison.OrdinalIgnoreCase) ?? false));

            return new SyncJob
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Name = title,
                Enabled = true,
                Operation = operation,
                SourcePath = sourcePath,
                DestinationPath = BuildDestForFolder(subfolderSlug),
                FilterFilePath = attachDefaultFilter && File.Exists(filterFile) ? filterFile : string.Empty,
                FilterMode = FilterRuleMode.AutoDetect,
                EnableExcludedGraceRetention = true,
                ExcludedRetentionDays = 30,
                SkipLinks = true,
                FastList = true,
                LogFilePath = logFile,
                ExtraFlags = isDriveDest
                    ? "--tpslimit 6 --tpslimit-burst 6 --drive-chunk-size 64M --retries 3 --low-level-retries 5"
                    : string.Empty,
                Strategy = ExecutionStrategy.HybridAuto,
                Schedule = new JobSchedule
                {
                    IsScheduled = freq.IsScheduled,
                    ScheduleType = freq.Type,
                    IntervalMinutes = freq.IntervalMinutes,
                    DailyTime = "14:00",
                    RetryOnFailure = true,
                    MaxRetries = 3,
                    RetryDelayMinutes = 5
                }
            };
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (FolderFullProfileCheck.IsChecked == true)
        {
            jobs.Add(CreateScratchJob("Backup — Pasta do Usuário", userProfile, "Usuario", attachDefaultFilter: true));
        }
        else
        {
            if (FolderDocsCheck.IsChecked == true)
                jobs.Add(CreateScratchJob("Backup — Documentos", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documentos"));

            if (FolderDesktopCheck.IsChecked == true)
                jobs.Add(CreateScratchJob("Backup — Área de Trabalho", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "AreaDeTrabalho"));

            if (FolderPicturesCheck.IsChecked == true)
                jobs.Add(CreateScratchJob("Backup — Imagens", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Imagens"));

            if (FolderVideosCheck.IsChecked == true)
                jobs.Add(CreateScratchJob("Backup — Vídeos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Videos"));

            if (FolderMusicCheck.IsChecked == true)
                jobs.Add(CreateScratchJob("Backup — Músicas", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Musicas"));

            if (FolderDownloadsCheck.IsChecked == true)
                jobs.Add(CreateScratchJob("Backup — Downloads", Path.Combine(userProfile, "Downloads"), "Downloads"));
        }

        if (FolderCustomCheck.IsChecked == true && !string.IsNullOrWhiteSpace(ScratchCustomFolderBox.Text))
        {
            var customPath = ScratchCustomFolderBox.Text.Trim();
            var folderLeaf = Path.GetFileName(customPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(folderLeaf))
                folderLeaf = "PastaExtra";
            jobs.Add(CreateScratchJob($"Backup — {folderLeaf}", customPath, folderLeaf));
        }

        return jobs;
    }

    private void SyncReviewJobsFromConfig()
    {
        _reviewJobs.Clear();
        foreach (var j in _onboarding.CurrentConfig.Jobs)
            _reviewJobs.Add(new WizardJobReviewItem { Job = j.Clone() });

        RefreshJobsReviewListUi(selectIndex: _reviewJobs.Count > 0 ? 0 : -1);
    }

    private void RefreshJobsReviewListUi(int selectIndex)
    {
        _isPopulatingJobFields = true;
        try
        {
            WizardJobsReviewList.ItemsSource = null;
            WizardJobsReviewList.ItemsSource = _reviewJobs;
            if (selectIndex >= 0 && selectIndex < _reviewJobs.Count)
            {
                WizardJobsReviewList.SelectedIndex = selectIndex;
                LoadJobIntoReviewForm(selectIndex);
            }
        }
        finally
        {
            _isPopulatingJobFields = false;
        }
    }

    private void ImportedJobItemCheck_Click(object sender, RoutedEventArgs e)
    {
        UpdateStepGateState();
    }

    private void WizardJobsReviewList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingJobFields)
            return;

        var idx = WizardJobsReviewList.SelectedIndex;
        if (idx >= 0 && idx < _reviewJobs.Count)
            LoadJobIntoReviewForm(idx);
        UpdateStepGateState();
    }

    private void LoadJobIntoReviewForm(int index)
    {
        _isPopulatingJobFields = true;
        try
        {
            _selectedJobIndex = index;
            var job = _reviewJobs[index].Job;
            if (WizardJobOpCombo.ItemsSource is IEnumerable<UiOption<RcloneOperation>> ops)
                WizardJobOpCombo.SelectedItem = ops.FirstOrDefault(o => o.Value == job.Operation) ?? ops.FirstOrDefault();

            WizardSourceBox.Text = job.SourcePath;

            var destOpts = WizardImportDestTargetCombo.ItemsSource as IReadOnlyList<WizardDestOption> ?? BuildAvailableDestOptions();
            WizardImportDestTargetCombo.ItemsSource = destOpts;

            var colonIdx = job.DestinationPath.IndexOf(':');
            var isCloudDest = colonIdx > 1 && !job.DestinationPath.StartsWith(@"\\", StringComparison.Ordinal);
            if (isCloudDest)
            {
                var rem = job.DestinationPath[..colonIdx];
                var sub = job.DestinationPath[(colonIdx + 1)..];
                WizardImportDestTargetCombo.SelectedItem =
                    destOpts.FirstOrDefault(o => !o.IsLocal && o.TargetValue.Equals(rem, StringComparison.OrdinalIgnoreCase))
                    ?? destOpts.FirstOrDefault();
                WizardDestBox.Text = sub;
            }
            else
            {
                WizardImportDestTargetCombo.SelectedItem = destOpts.FirstOrDefault(o => o.IsLocal) ?? destOpts.FirstOrDefault();
                WizardDestBox.Text = job.DestinationPath;
            }

            if (WizardImportFrequencyCombo.ItemsSource is IEnumerable<WizardFrequencyOption> freqs)
            {
                WizardFrequencyOption? matchFreq = !job.Schedule.IsScheduled
                    ? freqs.FirstOrDefault(f => !f.IsScheduled)
                    : (job.Schedule.ScheduleType == ScheduleType.DailyAtTime
                        ? freqs.FirstOrDefault(f => f.Type == ScheduleType.DailyAtTime)
                        : freqs.OrderBy(f => Math.Abs(f.IntervalMinutes - job.Schedule.IntervalMinutes)).FirstOrDefault(f => f.IsScheduled));
                WizardImportFrequencyCombo.SelectedItem = matchFreq ?? freqs.FirstOrDefault();
            }
        }
        finally
        {
            _isPopulatingJobFields = false;
        }
    }

    private void CommitCurrentImportedJobEdits()
    {
        if (_selectedJobIndex < 0 || _selectedJobIndex >= _reviewJobs.Count)
            return;

        var item = _reviewJobs[_selectedJobIndex];
        var job = item.Job;
        if (WizardJobOpCombo.SelectedItem is UiOption<RcloneOperation> opOpt)
            job.Operation = opOpt.Value;

        job.SourcePath = WizardSourceBox.Text.Trim();

        var destSub = WizardDestBox.Text.Trim();
        if (WizardImportDestTargetCombo.SelectedItem is WizardDestOption { IsLocal: false } cloudOpt)
        {
            var cleanSub = destSub.StartsWith(cloudOpt.TargetValue + ":", StringComparison.OrdinalIgnoreCase)
                ? destSub[(cloudOpt.TargetValue.Length + 1)..]
                : destSub.TrimStart(':');
            job.DestinationPath = string.IsNullOrEmpty(cleanSub) ? $"{cloudOpt.TargetValue}:" : $"{cloudOpt.TargetValue}:{cleanSub}";
        }
        else
        {
            job.DestinationPath = destSub;
        }

        if (WizardImportFrequencyCombo.SelectedItem is WizardFrequencyOption freqOpt)
        {
            job.Schedule.IsScheduled = freqOpt.IsScheduled;
            job.Schedule.ScheduleType = freqOpt.Type;
            job.Schedule.IntervalMinutes = freqOpt.IntervalMinutes;
        }

        item.NotifyChanged();
    }

    private void WizardJobField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isPopulatingJobFields)
            return;
        CommitCurrentImportedJobEdits();
        UpdateStepGateState();
    }

    private void WizardJobOpCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingJobFields)
            return;
        CommitCurrentImportedJobEdits();
        UpdateStepGateState();
    }

    private void WizardImportFrequencyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingJobFields)
            return;
        CommitCurrentImportedJobEdits();
    }

    private void WizardImportDestTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingJobFields)
            return;
        CommitCurrentImportedJobEdits();
        UpdateStepGateState();
    }

    private void WizardServiceModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateServiceModeHints();
    }

    private void WizardImportServiceModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateServiceModeHints();
    }

    private static string DescribeServiceMode(BackgroundServiceMode mode) => mode switch
    {
        BackgroundServiceMode.WindowsService =>
            "⚙ Serviço Nativo (services.msc): Liga automaticamente junto com o computador antes mesmo de você fazer login no Windows (ideal para servidores ou PCs sempre ligados). Ao clicar em Concluir, o Windows pedirá confirmação de Administrador (UAC) uma única vez.",
        BackgroundServiceMode.ManualOnly =>
            "⏸ Apenas Manual: Nenhum serviço será iniciado automaticamente com o Windows. Os agendamentos só rodarão quando você abrir o RcloneManager manualmente.",
        _ =>
            "✓ Recomendado para uso pessoal: Inicia sozinho e silenciosamente assim que você entra no Windows, mantendo os backups automáticos mesmo com a janela fechada. Não pede senha de Administrador (UAC)."
    };

    private void UpdateServiceModeHints()
    {
        if (WizardServiceModeHintText != null)
        {
            var mode = (WizardServiceModeCombo?.SelectedItem as UiOption<BackgroundServiceMode>)?.Value ?? BackgroundServiceMode.UserStartup;
            WizardServiceModeHintText.Text = DescribeServiceMode(mode);
            WizardServiceModeHintText.Foreground = mode == BackgroundServiceMode.WindowsService
                ? new SolidColorBrush(MediaColor.FromRgb(253, 230, 138))
                : new SolidColorBrush(MediaColor.FromRgb(148, 163, 184));
        }

        if (WizardImportServiceModeHintText != null)
        {
            var mode = (WizardImportServiceModeCombo?.SelectedItem as UiOption<BackgroundServiceMode>)?.Value ?? BackgroundServiceMode.UserStartup;
            WizardImportServiceModeHintText.Text = DescribeServiceMode(mode);
            WizardImportServiceModeHintText.Foreground = mode == BackgroundServiceMode.WindowsService
                ? new SolidColorBrush(MediaColor.FromRgb(253, 230, 138))
                : new SolidColorBrush(MediaColor.FromRgb(148, 163, 184));
        }
    }

    #endregion

    private void WizardReleaseField_Changed(object sender, TextChangedEventArgs e)
    {
        if (_isInitializingUi || !IsLoaded)
            return;

        _isStep2Ready = false;
        WizardReleaseStatusBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(245, 158, 11));
        WizardReleaseStatusText.Text = "Versão alterada. Clique em 'Baixar / Verificar Rclone Agora' para validar antes de avançar.";
        UpdateStepGateState();
    }

    private void WizardLockVersionCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_isInitializingUi || !IsLoaded)
            return;

        _ = RefreshStep2StatusAsync();
    }

    private async Task RefreshStep2StatusAsync()
    {
        try
        {
            _onboarding.CurrentConfig.PinnedRcloneVersion = RcloneReleaseManager.NormalizeVersion(WizardPinnedVersionBox.Text);
            _onboarding.CurrentConfig.LockRcloneVersion = WizardLockVersionCheck.IsChecked == true;

            var check = await _onboarding.CheckReleaseAsync();
            _isStep2Ready = !check.NeedsDownload && File.Exists(check.ExecutablePath);

            WizardReleaseStatusBorder.BorderBrush = _isStep2Ready
                ? new SolidColorBrush(MediaColor.FromRgb(16, 185, 129))
                : new SolidColorBrush(MediaColor.FromRgb(245, 158, 11));

            var prefix = _isStep2Ready ? "✓ Pronto: " : "Pendente: ";
            WizardReleaseStatusText.Text = $"{prefix}{check.StatusMessage}\nLocal: {check.ExecutablePath}";
        }
        catch (Exception ex)
        {
            _isStep2Ready = false;
            WizardReleaseStatusBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(239, 68, 68));
            WizardReleaseStatusText.Text = $"Versão inválida: {ex.Message}";
        }

        UpdateStepGateState();
    }

    private async Task RefreshStep3RemotesAsync(bool verifyImportedConnections)
    {
        var snapshot = await _onboarding.RefreshRemotesStateAsync(verifyImportedConnections);
        _existingRemoteDetails = snapshot.ExistingRemoteDetails;

        if (snapshot.ImportedRemotes.Count > 0)
        {
            WizardImportedRemotesPanel.Visibility = Visibility.Visible;
            WizardImportedRemotesList.ItemsSource = null;
            WizardImportedRemotesList.ItemsSource = snapshot.ImportedRemotes;
            WizardImportedRemotesList.SelectedIndex = snapshot.RecommendedSelectionIndex;
        }
        else
        {
            WizardImportedRemotesPanel.Visibility = Visibility.Collapsed;
        }

        UpdateExistingRemoteButtonVisibility();
        RefreshStep3StatusBanner();
        UpdateStepGateState();
    }

    private string GetSelectedProviderTag() =>
        (WizardProviderCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "drive";

    private string GetSelectedProviderDisplayName() =>
        (WizardProviderCombo?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Google Drive";

    private RemoteAuthMode GetSelectedAuthMode() =>
        WizardAuthModeCombo?.SelectedItem is UiOption<RemoteAuthMode> opt
            ? opt.Value
            : RemoteAuthMode.OAuthBrowserLink;

    private static bool IsOAuthProvider(string provider) =>
        provider.Equals("drive", StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("onedrive", StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("dropbox", StringComparison.OrdinalIgnoreCase);

    private void WizardProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingProviderControls)
            return;

        var provider = GetSelectedProviderTag();

        if (_onboarding.ImportedRemotes.Count == 0 && DefaultAliasByProvider.TryGetValue(provider, out var defaultAlias))
        {
            var currentAlias = WizardRemoteNameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(currentAlias) || KnownDefaultAliases.Contains(currentAlias))
                WizardRemoteNameBox.Text = defaultAlias;
        }

        ConfigureStep3ForProvider(provider, preserveSelectedAuthMode: false);
        UpdateExistingRemoteButtonVisibility();
        RefreshStep3StatusBanner();
        UpdateStepGateState();
    }

    private void WizardAuthModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingProviderControls)
            return;

        ConfigureStep3ForProvider(GetSelectedProviderTag(), preserveSelectedAuthMode: true);
    }

    private void WizardRemoteNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _isUpdatingProviderControls)
            return;

        UpdateExistingRemoteButtonVisibility();
        RefreshStep3StatusBanner();
        UpdateStepGateState();
    }

    private void WizardLocalDestFolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _isInitializingUi)
            return;

        if (!string.Equals(_verifiedLocalFolderPath, WizardLocalDestFolderBox.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            _verifiedLocalFolderPath = null;

        RefreshStep3StatusBanner();
        UpdateStepGateState();
    }

    private void ConfigureStep3ForProvider(string provider, bool preserveSelectedAuthMode)
    {
        if (WizardAuthModeCombo == null)
            return;

        _isUpdatingProviderControls = true;
        try
        {
            var previousMode = GetSelectedAuthMode();
            var isLocal = provider.Equals("local", StringComparison.OrdinalIgnoreCase);
            var isOAuth = IsOAuthProvider(provider);
            var providerDisplay = GetSelectedProviderDisplayName();

            RemoteAliasPanel.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
            RemoteAliasColumn.Width = isLocal ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            WizardLocalFolderPanel.Visibility = isLocal ? Visibility.Visible : Visibility.Collapsed;

            if (isLocal)
            {
                WizardAuthModeSection.Visibility = Visibility.Collapsed;
                WizardOAuthAdvancedExpander.Visibility = Visibility.Collapsed;
                WizardTokenPanel.Visibility = Visibility.Collapsed;
                WizardKeyFileSection.Visibility = Visibility.Collapsed;
                WizardServerCredsGrid.Visibility = Visibility.Collapsed;

                WizardProviderGuideText.Text = "Modo local selecionado: escolha a pasta ou HD externo onde os backups serão gravados e clique em 'Validar Pasta Local'.";
                WizardCreateRemoteButton.Content = "Validar Pasta Local";
                return;
            }

            if (isOAuth)
            {
                var authOptions = new List<UiOption<RemoteAuthMode>>
                {
                    new(RemoteAuthMode.OAuthBrowserLink, $"Entrar pelo navegador ({providerDisplay} — Recomendado)"),
                    new(RemoteAuthMode.ManualTokenJson, "Colar Token JSON manualmente")
                };

                if (provider.Equals("drive", StringComparison.OrdinalIgnoreCase))
                    authOptions.Add(new UiOption<RemoteAuthMode>(RemoteAuthMode.KeyOrCredentials, "Usar arquivo Service Account (.json)"));

                WizardAuthModeCombo.ItemsSource = authOptions;
                var modeToSelect = preserveSelectedAuthMode && authOptions.Any(o => o.Value == previousMode)
                    ? previousMode
                    : RemoteAuthMode.OAuthBrowserLink;
                WizardAuthModeCombo.SelectedItem = authOptions.First(o => o.Value == modeToSelect);

                WizardAuthModeSection.Visibility = Visibility.Visible;
                WizardServerCredsGrid.Visibility = Visibility.Collapsed;
                WizardOAuthAdvancedExpander.Visibility = modeToSelect is RemoteAuthMode.OAuthBrowserLink or RemoteAuthMode.ManualTokenJson
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                WizardTokenPanel.Visibility = modeToSelect == RemoteAuthMode.ManualTokenJson ? Visibility.Visible : Visibility.Collapsed;
                WizardKeyFileSection.Visibility = modeToSelect == RemoteAuthMode.KeyOrCredentials ? Visibility.Visible : Visibility.Collapsed;
                WizardKeyFileLabel.Text = "Arquivo JSON da Service Account do Google Drive *";

                WizardProviderGuideText.Text = modeToSelect switch
                {
                    RemoteAuthMode.OAuthBrowserLink =>
                        $"Ao clicar em 'Conectar & Validar {providerDisplay}', seu navegador abrirá para login. O assistente aguardará a conclusão e testará o acesso real antes de liberar o próximo passo.",
                    RemoteAuthMode.ManualTokenJson =>
                        $"Cole o Token JSON válido de {providerDisplay}. A conta será gravada e testada em seguida.",
                    _ =>
                        "Selecione o arquivo .json da Service Account para validar o acesso direto ao Google Drive."
                };

                WizardCreateRemoteButton.Content = $"Conectar & Validar {providerDisplay}";
                return;
            }

            WizardAuthModeCombo.ItemsSource = new[]
            {
                new UiOption<RemoteAuthMode>(RemoteAuthMode.KeyOrCredentials, "Credenciais do servidor")
            };
            WizardAuthModeCombo.SelectedIndex = 0;
            WizardAuthModeSection.Visibility = Visibility.Collapsed;
            WizardOAuthAdvancedExpander.Visibility = Visibility.Collapsed;
            WizardTokenPanel.Visibility = Visibility.Collapsed;
            WizardServerCredsGrid.Visibility = Visibility.Visible;

            switch (provider.ToLowerInvariant())
            {
                case "s3":
                    WizardKeyFileSection.Visibility = Visibility.Collapsed;
                    WizardHostLabel.Text = "Endpoint S3 (vazio = AWS)";
                    WizardUserLabel.Text = "Access Key ID *";
                    WizardPassLabel.Text = "Secret Access Key *";
                    WizardProviderGuideText.Text = "Informe o Access Key ID e Secret Access Key (e Endpoint caso use Cloudflare R2, MinIO ou Wasabi). O assistente aguardará o teste real de conexão.";
                    break;

                case "sftp":
                    WizardKeyFileSection.Visibility = Visibility.Visible;
                    WizardKeyFileLabel.Text = "Chave privada SSH (.pem / .key — opcional se informar senha)";
                    WizardHostLabel.Text = "Servidor / IP SSH *";
                    WizardUserLabel.Text = "Usuário SSH *";
                    WizardPassLabel.Text = "Senha SSH";
                    WizardProviderGuideText.Text = "Informe o endereço do servidor SSH/SFTP, usuário e senha (ou chave privada). A próxima etapa será liberada após testar a conexão SSH.";
                    break;

                case "ftp":
                    WizardKeyFileSection.Visibility = Visibility.Collapsed;
                    WizardHostLabel.Text = "Servidor FTP (Host) *";
                    WizardUserLabel.Text = "Usuário FTP *";
                    WizardPassLabel.Text = "Senha FTP *";
                    WizardProviderGuideText.Text = "Informe o endereço, usuário e senha do servidor FTP para testar e validar a conexão.";
                    break;

                case "smb":
                    WizardKeyFileSection.Visibility = Visibility.Collapsed;
                    WizardHostLabel.Text = "Computador / IP na Rede *";
                    WizardUserLabel.Text = "Usuário da Rede";
                    WizardPassLabel.Text = "Senha";
                    WizardProviderGuideText.Text = "Informe o nome ou IP do computador na rede Windows (SMB) e credenciais de acesso para validar.";
                    break;

                default:
                    WizardKeyFileSection.Visibility = Visibility.Collapsed;
                    WizardHostLabel.Text = "URL completa do WebDAV *";
                    WizardUserLabel.Text = "Usuário *";
                    WizardPassLabel.Text = "Senha / App Password *";
                    WizardProviderGuideText.Text = "Informe a URL WebDAV (ex.: Nextcloud), usuário e senha para testar o acesso.";
                    break;
            }

            WizardCreateRemoteButton.Content = $"Conectar & Validar {providerDisplay}";
        }
        finally
        {
            _isUpdatingProviderControls = false;
        }
    }

    private void UpdateExistingRemoteButtonVisibility()
    {
        if (WizardTestExistingButton == null)
            return;

        var provider = GetSelectedProviderTag();
        if (provider.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            WizardTestExistingButton.Visibility = Visibility.Collapsed;
            return;
        }

        var alias = WizardRemoteNameBox.Text.Trim().TrimEnd(':');
        var matchingSaved = _existingRemoteDetails.FirstOrDefault(
            d => d.Name.Equals(alias, StringComparison.OrdinalIgnoreCase) &&
                 d.Type.Equals(provider, StringComparison.OrdinalIgnoreCase));

        WizardTestExistingButton.Visibility = matchingSaved != null && !IsCurrentProviderSelectionVerified()
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private bool IsCurrentProviderSelectionVerified()
    {
        var provider = GetSelectedProviderTag();
        if (provider.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(_verifiedLocalFolderPath) &&
                   string.Equals(_verifiedLocalFolderPath, WizardLocalDestFolderBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        var alias = WizardRemoteNameBox.Text.Trim().TrimEnd(':');
        return !string.IsNullOrWhiteSpace(alias) &&
               _verifiedRemotesByProvider.TryGetValue(provider, out var names) &&
               names.Contains(alias);
    }

    private void RefreshStep3StatusBanner()
    {
        if (WizardRemoteActionText == null || WizardConnectionStatusBorder == null)
            return;

        if (_onboarding.ImportedRemotes.Count > 0)
        {
            var pending = _onboarding.ImportedRemotes.Count(r => !r.IsAuthenticated);
            if (pending == 0)
            {
                WizardConnectionStatusBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(16, 185, 129));
                WizardRemoteActionText.Foreground = new SolidColorBrush(MediaColor.FromRgb(16, 185, 129));
                WizardRemoteActionText.Text = "✓ Todas as contas apontadas na importação foram conectadas e validadas com sucesso!";
            }
            else
            {
                WizardConnectionStatusBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(245, 158, 11));
                WizardRemoteActionText.Foreground = new SolidColorBrush(MediaColor.FromRgb(253, 230, 138));
                WizardRemoteActionText.Text = $"Falta validar {pending} conta(s) da configuração importada antes de avançar.";
            }
            return;
        }

        var providerDisplay = GetSelectedProviderDisplayName();
        if (IsCurrentProviderSelectionVerified())
        {
            WizardConnectionStatusBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(16, 185, 129));
            WizardRemoteActionText.Foreground = new SolidColorBrush(MediaColor.FromRgb(16, 185, 129));
            WizardRemoteActionText.Text = GetSelectedProviderTag().Equals("local", StringComparison.OrdinalIgnoreCase)
                ? $"✓ Pasta local '{_verifiedLocalFolderPath}' verificada e pronta para receber backups!"
                : $"✓ Conta '{WizardRemoteNameBox.Text.Trim().TrimEnd(':')}' ({providerDisplay}) conectada e validada!";
        }
        else
        {
            var alias = WizardRemoteNameBox.Text.Trim().TrimEnd(':');
            var hasSavedSameType = _existingRemoteDetails.Any(
                d => d.Name.Equals(alias, StringComparison.OrdinalIgnoreCase) &&
                     d.Type.Equals(GetSelectedProviderTag(), StringComparison.OrdinalIgnoreCase));

            WizardConnectionStatusBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(51, 65, 85));
            WizardRemoteActionText.Foreground = new SolidColorBrush(MediaColor.FromRgb(148, 163, 184));
            WizardRemoteActionText.Text = hasSavedSameType
                ? $"A conta '{alias}' ({providerDisplay}) já existe neste PC — clique em 'Validar Conta Já Salva' (ou reconecte) para confirmar o acesso."
                : $"Aguardando conexão e validação para o provedor selecionado ({providerDisplay}).";
        }
    }

    private void WizardImportedRemotesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WizardImportedRemotesList.SelectedItem is not WizardRemoteBlueprintItem item)
            return;

        var bp = item.Blueprint;
        SelectWizardProvider(bp.Type);
        WizardRemoteNameBox.Text = bp.Name;
        ConfigureStep3ForProvider(bp.Type, preserveSelectedAuthMode: false);

        if (WizardAuthModeCombo.ItemsSource is IEnumerable<UiOption<RemoteAuthMode>> modes)
        {
            var matchMode = modes.FirstOrDefault(m => m.Value == bp.SuggestedAuthMode);
            if (matchMode != null)
                WizardAuthModeCombo.SelectedItem = matchMode;
        }

        WizardClientIdBox.Text = bp.NonSensitiveParameters.TryGetValue("client_id", out var cid) ? cid : string.Empty;
        WizardHostBox.Text = bp.NonSensitiveParameters.TryGetValue("endpoint", out var ep)
            ? ep
            : (bp.NonSensitiveParameters.TryGetValue("host", out var h)
                ? h
                : (bp.NonSensitiveParameters.TryGetValue("url", out var u) ? u : string.Empty));

        UpdateExistingRemoteButtonVisibility();
        RefreshStep3StatusBanner();
    }

    private void SelectWizardProvider(string providerName)
    {
        _isUpdatingProviderControls = true;
        try
        {
            foreach (ComboBoxItem item in WizardProviderCombo.Items)
            {
                if (string.Equals(item.Tag?.ToString(), providerName, StringComparison.OrdinalIgnoreCase))
                {
                    WizardProviderCombo.SelectedItem = item;
                    return;
                }
            }
        }
        finally
        {
            _isUpdatingProviderControls = false;
        }
    }

    private bool IsStepCompleted(int stepNumber)
    {
        return stepNumber switch
        {
            1 => _startMode == WizardStartMode.Scratch || (_startMode == WizardStartMode.Import && _hasImportedFile),
            2 => _isStep2Ready,
            3 => !_isConnectingProvider && (_onboarding.ImportedRemotes.Count > 0
                ? _onboarding.ImportedRemotes.All(r => r.IsAuthenticated)
                : IsCurrentProviderSelectionVerified()),
            4 => _startMode == WizardStartMode.Import
                ? (_reviewJobs.Any(r => r.Job.Enabled) &&
                   _reviewJobs.Where(r => r.Job.Enabled).All(r =>
                       !string.IsNullOrWhiteSpace(r.Job.SourcePath) &&
                       !string.IsNullOrWhiteSpace(r.Job.DestinationPath)))
                : IsScratchStep4Valid(),
            _ => false
        };
    }

    private void UpdateStepUi()
    {
        Step1Panel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4Panel.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        var activeBrush = new SolidColorBrush(MediaColor.FromRgb(37, 99, 235));
        var doneBrush = new SolidColorBrush(MediaColor.FromRgb(5, 150, 105));
        var lockedBrush = new SolidColorBrush(MediaColor.FromRgb(30, 41, 59));

        ApplyBadgeState(Step1Badge, Step1BadgeText, 1, "1. Como Começar", activeBrush, doneBrush, lockedBrush);
        ApplyBadgeState(Step2Badge, Step2BadgeText, 2, "2. Preparar Rclone", activeBrush, doneBrush, lockedBrush);
        ApplyBadgeState(Step3Badge, Step3BadgeText, 3, "3. Conectar Destino", activeBrush, doneBrush, lockedBrush);
        ApplyBadgeState(Step4Badge, Step4BadgeText, 4, "4. Escolher Pastas", activeBrush, doneBrush, lockedBrush);

        BackButton.IsEnabled = _step > 1 && !_isConnectingProvider;
        DismissWizardButton.IsEnabled = !_isConnectingProvider;
        NextButton.Content = _step == 4 ? "Concluir e Salvar" : "Avançar ▶";
        UpdateStepGateState();
    }

    private void ApplyBadgeState(
        Border badge,
        TextBlock label,
        int badgeStep,
        string baseTitle,
        System.Windows.Media.Brush activeBrush,
        System.Windows.Media.Brush doneBrush,
        System.Windows.Media.Brush lockedBrush)
    {
        if (_step == badgeStep)
        {
            badge.Background = activeBrush;
            badge.Opacity = 1.0;
            label.Text = baseTitle;
        }
        else if (badgeStep < _step && IsStepCompleted(badgeStep))
        {
            badge.Background = doneBrush;
            badge.Opacity = 1.0;
            label.Text = "✓ " + baseTitle;
        }
        else
        {
            badge.Background = lockedBrush;
            badge.Opacity = 0.5;
            label.Text = baseTitle;
        }
    }

    private void UpdateStepGateState()
    {
        if (NextButton == null || StepGateHintText == null)
            return;

        var ready = IsStepCompleted(_step);
        NextButton.IsEnabled = ready;

        var okBrush = new SolidColorBrush(MediaColor.FromRgb(16, 185, 129));
        var pendingBrush = new SolidColorBrush(MediaColor.FromRgb(245, 158, 11));

        StepGateHintText.Foreground = ready ? okBrush : pendingBrush;

        StepGateHintText.Text = _step switch
        {
            1 => _startMode switch
            {
                WizardStartMode.None => "Escolha 'Começar do zero' ou 'Importar configuração' acima para continuar.",
                WizardStartMode.Import when !_hasImportedFile => "Selecione um arquivo .json válido para liberar a próxima etapa.",
                WizardStartMode.Import => "✓ Arquivo de configuração carregado. Clique em 'Avançar ▶'.",
                _ => "✓ Modo 'Começar do zero' selecionado. Clique em 'Avançar ▶'."
            },
            2 => ready
                ? "✓ Rclone verificado e pronto neste computador. Clique em 'Avançar ▶'."
                : "Clique em 'Baixar / Verificar Rclone Agora' acima para liberar a próxima etapa.",
            3 => _isConnectingProvider
                ? "Aguardando a conclusão e validação da conexão..."
                : (_onboarding.ImportedRemotes.Count > 0
                    ? (ready
                        ? "✓ Todas as contas importadas foram validadas! Clique em 'Avançar ▶'."
                        : $"Conecte e valide todas as contas pendentes ({_onboarding.ImportedRemotes.Count(r => !r.IsAuthenticated)} restante(s)) para avançar.")
                    : (ready
                        ? $"✓ {GetSelectedProviderDisplayName()} validado com sucesso! Clique em 'Avançar ▶'."
                        : $"Conclua a conexão e validação de '{GetSelectedProviderDisplayName()}' para liberar a próxima etapa.")),
            4 => _startMode == WizardStartMode.Import
                ? (ready
                    ? "✓ Tarefas prontas! Clique em 'Concluir e Salvar' para finalizar."
                    : "Mantenha ao menos uma tarefa marcada com origem e destino preenchidos.")
                : (ready
                    ? "✓ Pastas selecionadas! Clique em 'Concluir e Salvar' para finalizar."
                    : "Marque pelo menos uma pasta acima para incluir no seu backup."),
            _ => string.Empty
        };
    }

    private async void WizardVerifyReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var version = RcloneReleaseManager.NormalizeVersion(WizardPinnedVersionBox.Text);
            var lockVersion = WizardLockVersionCheck.IsChecked == true;
            _onboarding.CurrentConfig.PinnedRcloneVersion = version;
            _onboarding.CurrentConfig.LockRcloneVersion = lockVersion;

            var check = await _onboarding.CheckReleaseAsync();
            var executablePath = check.ExecutablePath;

            if (check.NeedsDownload)
            {
                var setupWin = new RcloneSetupWindow(_onboarding.WorkDirectory, check.PinnedVersion, check.StatusMessage);
                if (await OverlayHost.ShowCurrentAsync(setupWin) == true && !string.IsNullOrWhiteSpace(setupWin.DownloadedExecutablePath))
                    executablePath = setupWin.DownloadedExecutablePath;
            }

            _onboarding.ApplyVerifiedRelease(version, lockVersion, executablePath);
            await RefreshStep2StatusAsync();
            await RefreshStep3RemotesAsync(verifyImportedConnections: false);
        }
        catch (Exception ex)
        {
            await OverlayHost.ShowCurrentAlertAsync("Assistente Inicial", $"Erro ao verificar o Rclone: {ex.Message}", OverlayAlertKind.Error);
        }
    }

    private async void WizardAuthorizeTerminal_Click(object sender, RoutedEventArgs e)
    {
        var rcloneExe = _onboarding.CurrentConfig.RcloneExecutablePath;
        if (!File.Exists(rcloneExe))
        {
            await OverlayHost.ShowCurrentAlertAsync("Assistente Inicial", "Conclua a Etapa 2 primeiro.");
            return;
        }

        var provider = GetSelectedProviderTag();
        var args = $"authorize \"{provider}\"";
        if (!string.IsNullOrWhiteSpace(WizardClientIdBox.Text) && !string.IsNullOrWhiteSpace(WizardClientSecretBox.Text))
            args += $" \"{WizardClientIdBox.Text.Trim()}\" \"{WizardClientSecretBox.Text.Trim()}\"";

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k \"\"{rcloneExe}\" {args}\"",
            UseShellExecute = true
        });
    }

    private void WizardBrowseKeyFile_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Filter = "Arquivos de Chave (*.json;*.pem;*.key;*.crt)|*.json;*.pem;*.key;*.crt|Todos (*.*)|*.*"
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            WizardKeyFileBox.Text = dialog.FileName;
    }

    private void WizardBrowseLocalDest_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Escolha a pasta ou HD externo onde os backups serão salvos",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(WizardLocalDestFolderBox.Text) ? WizardLocalDestFolderBox.Text : string.Empty
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            WizardLocalDestFolderBox.Text = dialog.SelectedPath;
    }

    private void WizardBrowseSource_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(WizardSourceBox.Text) ? WizardSourceBox.Text : string.Empty
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            WizardSourceBox.Text = dialog.SelectedPath;
    }

    private string? ValidateProviderInputs(string provider, RemoteAuthMode authMode)
    {
        if (provider.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            var localDir = WizardLocalDestFolderBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(localDir))
                return "Escolha ou digite o caminho da pasta local / HD externo.";
            return null;
        }

        var alias = WizardRemoteNameBox.Text.Trim().TrimEnd(':');
        if (string.IsNullOrWhiteSpace(alias))
            return "Informe um apelido para identificar esta conta (ex.: gdrive).";

        if (IsOAuthProvider(provider))
        {
            if (authMode == RemoteAuthMode.ManualTokenJson)
            {
                var token = WizardTokenBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(token))
                    return "Cole o Token JSON de autorização no campo indicado.";
                try { using var _ = JsonDocument.Parse(token); }
                catch { return "O conteúdo colado no campo de Token não é um JSON válido."; }
            }
            else if (authMode == RemoteAuthMode.KeyOrCredentials)
            {
                var saFile = WizardKeyFileBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(saFile) || !File.Exists(saFile))
                    return "Selecione um arquivo Service Account (.json) existente neste computador.";
            }
            return null;
        }

        var host = WizardHostBox.Text.Trim();
        var user = WizardUserBox.Text.Trim();
        var pass = WizardPassBox.Password;
        var keyFile = WizardKeyFileBox.Text.Trim();

        return provider.ToLowerInvariant() switch
        {
            "s3" when string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass) =>
                "Informe o Access Key ID e a Secret Access Key do seu bucket S3.",
            "sftp" when string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) =>
                "Informe o endereço do servidor (Host) e o usuário SSH.",
            "sftp" when string.IsNullOrEmpty(pass) && (string.IsNullOrWhiteSpace(keyFile) || !File.Exists(keyFile)) =>
                "Informe a senha SSH ou selecione um arquivo de chave privada válido.",
            "ftp" when string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass) =>
                "Informe o servidor, usuário e senha do FTP.",
            "smb" when string.IsNullOrWhiteSpace(host) =>
                "Informe o nome ou endereço IP do computador na rede (SMB).",
            "webdav" when string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(pass) =>
                "Informe a URL completa do WebDAV, usuário e senha.",
            _ => null
        };
    }

    private void SetConnectingWaitState(bool connecting, string? waitMessage = null)
    {
        _isConnectingProvider = connecting;
        WizardWaitingBanner.Visibility = connecting ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(waitMessage))
            WizardWaitingText.Text = waitMessage;

        WizardProviderCombo.IsEnabled = !connecting;
        WizardRemoteNameBox.IsEnabled = !connecting;
        WizardAuthModeCombo.IsEnabled = !connecting;
        WizardCreateRemoteButton.IsEnabled = !connecting;
        WizardTestExistingButton.IsEnabled = !connecting;
        BackButton.IsEnabled = _step > 1 && !connecting;
        DismissWizardButton.IsEnabled = !connecting;
        UpdateStepGateState();
    }

    private void CancelAuthWaitButton_Click(object sender, RoutedEventArgs e)
    {
        _authCts?.Cancel();
    }

    private async void WizardTestExistingButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = GetSelectedProviderTag();
        var providerDisplay = GetSelectedProviderDisplayName();
        var remoteName = WizardRemoteNameBox.Text.Trim().TrimEnd(':');

        _authCts?.Dispose();
        _authCts = new CancellationTokenSource();

        SetConnectingWaitState(true, $"Testando conexão real com a conta existente '{remoteName}' ({providerDisplay})...");
        try
        {
            var res = await _onboarding.VerifyExistingRemoteAsync(remoteName, provider, _authCts.Token);
            if (res.Success)
            {
                MarkRemoteVerified(provider, remoteName);
                await RefreshStep3RemotesAsync(verifyImportedConnections: false);
                WizardConnectionStatusBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(16, 185, 129));
                WizardRemoteActionText.Foreground = new SolidColorBrush(MediaColor.FromRgb(16, 185, 129));
                WizardRemoteActionText.Text = $"✓ Conta '{remoteName}' ({providerDisplay}) verificada com sucesso!";
            }
            else
            {
                UnmarkRemoteVerified(provider, remoteName);
                WizardConnectionStatusBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(239, 68, 68));
                WizardRemoteActionText.Foreground = new SolidColorBrush(MediaColor.FromRgb(252, 165, 165));
                WizardRemoteActionText.Text = $"Falha ao validar '{remoteName}': {res.Message}. Clique em 'Conectar & Validar' para refazer o login.";
            }
        }
        catch (OperationCanceledException)
        {
            WizardConnectionStatusBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(245, 158, 11));
            WizardRemoteActionText.Foreground = new SolidColorBrush(MediaColor.FromRgb(253, 230, 138));
            WizardRemoteActionText.Text = "Verificação cancelada pelo usuário.";
        }
        catch (Exception ex)
        {
            WizardConnectionStatusBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(239, 68, 68));
            WizardRemoteActionText.Foreground = new SolidColorBrush(MediaColor.FromRgb(252, 165, 165));
            WizardRemoteActionText.Text = $"Erro ao testar '{remoteName}': {ex.Message}";
        }
        finally
        {
            SetConnectingWaitState(false);
            UpdateExistingRemoteButtonVisibility();
            UpdateStepGateState();
        }
    }

    private async void WizardCreateRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        var provider = GetSelectedProviderTag();
        var providerDisplay = GetSelectedProviderDisplayName();
        var authMode = IsOAuthProvider(provider) ? GetSelectedAuthMode() : RemoteAuthMode.KeyOrCredentials;

        var validationError = ValidateProviderInputs(provider, authMode);
        if (validationError != null)
        {
            await OverlayHost.ShowCurrentAlertAsync("Dados obrigatórios", validationError, OverlayAlertKind.Warning);
            return;
        }

        if (provider.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            var targetFolder = WizardLocalDestFolderBox.Text.Trim();
            try
            {
                var fullPath = Path.GetFullPath(targetFolder);
                Directory.CreateDirectory(fullPath);
                _ = Directory.EnumerateFileSystemEntries(fullPath).Take(1).ToList();

                _verifiedLocalFolderPath = targetFolder;
                RefreshStep3StatusBanner();
                UpdateStepGateState();
            }
            catch (Exception ex)
            {
                _verifiedLocalFolderPath = null;
                WizardConnectionStatusBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(239, 68, 68));
                WizardRemoteActionText.Foreground = new SolidColorBrush(MediaColor.FromRgb(252, 165, 165));
                WizardRemoteActionText.Text = $"Não foi possível acessar ou criar a pasta '{targetFolder}': {ex.Message}";
                UpdateStepGateState();
            }
            return;
        }

        var remoteName = WizardRemoteNameBox.Text.Trim().TrimEnd(':');
        var input = new WizardRemoteAuthInput
        {
            RemoteName = remoteName,
            Provider = provider,
            AuthMode = authMode,
            ClientId = WizardClientIdBox.Text,
            ClientSecret = WizardClientSecretBox.Text,
            ManualTokenJson = WizardTokenBox.Text,
            KeyFilePath = WizardKeyFileBox.Text,
            HostOrEndpoint = WizardHostBox.Text,
            UserOrAccessKey = WizardUserBox.Text,
            PasswordOrSecretKey = WizardPassBox.Password
        };

        _authCts?.Dispose();
        _authCts = new CancellationTokenSource();

        var initialWaitText = authMode == RemoteAuthMode.OAuthBrowserLink
            ? $"Aguardando autorização no navegador para {providerDisplay}... Conclua o login na aba aberta."
            : $"Conectando e testando acesso em {providerDisplay}...";

        SetConnectingWaitState(true, initialWaitText);
        var stageProgress = new Progress<string>(msg => WizardWaitingText.Text = msg);

        try
        {
            var outcome = await _onboarding.AuthenticateAndTestRemoteAsync(input, stageProgress, _authCts.Token);
            await RefreshStep3RemotesAsync(verifyImportedConnections: false);

            if (outcome.TestSucceeded)
            {
                MarkRemoteVerified(provider, remoteName);
                WizardConnectionStatusBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(16, 185, 129));
                WizardRemoteActionText.Foreground = new SolidColorBrush(MediaColor.FromRgb(16, 185, 129));
                WizardRemoteActionText.Text = outcome.StatusMessage;
            }
            else
            {
                UnmarkRemoteVerified(provider, remoteName);
                WizardConnectionStatusBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(239, 68, 68));
                WizardRemoteActionText.Foreground = new SolidColorBrush(MediaColor.FromRgb(252, 165, 165));
                WizardRemoteActionText.Text = outcome.StatusMessage;
            }
        }
        catch (OperationCanceledException)
        {
            UnmarkRemoteVerified(provider, remoteName);
            WizardConnectionStatusBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(245, 158, 11));
            WizardRemoteActionText.Foreground = new SolidColorBrush(MediaColor.FromRgb(253, 230, 138));
            WizardRemoteActionText.Text = $"Conexão com {providerDisplay} cancelada. Clique em '{WizardCreateRemoteButton.Content}' quando quiser tentar novamente.";
        }
        catch (Exception ex)
        {
            UnmarkRemoteVerified(provider, remoteName);
            WizardConnectionStatusBorder.BorderBrush = new SolidColorBrush(MediaColor.FromRgb(239, 68, 68));
            WizardRemoteActionText.Foreground = new SolidColorBrush(MediaColor.FromRgb(252, 165, 165));
            WizardRemoteActionText.Text = $"Falha ao conectar em {providerDisplay}: {ex.Message}";
        }
        finally
        {
            SetConnectingWaitState(false);
            UpdateExistingRemoteButtonVisibility();
            UpdateStepGateState();
        }
    }

    private void MarkRemoteVerified(string provider, string remoteName)
    {
        if (!_verifiedRemotesByProvider.TryGetValue(provider, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _verifiedRemotesByProvider[provider] = set;
        }
        set.Add(remoteName);
        _lastVerifiedRemoteAlias = remoteName;

        var imported = _onboarding.ImportedRemotes.FirstOrDefault(
            r => r.Blueprint.Name.Equals(remoteName, StringComparison.OrdinalIgnoreCase) &&
                 r.Blueprint.Type.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (imported != null)
            imported.IsAuthenticated = true;
    }

    private void UnmarkRemoteVerified(string provider, string remoteName)
    {
        if (_verifiedRemotesByProvider.TryGetValue(provider, out var set))
            set.Remove(remoteName);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnectingProvider || _step <= 1)
            return;

        if (_step == 4 && _startMode == WizardStartMode.Import)
            CommitCurrentImportedJobEdits();

        _step--;
        UpdateStepUi();
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnectingProvider || !IsStepCompleted(_step))
        {
            UpdateStepGateState();
            return;
        }

        if (_step < 4)
        {
            _step++;
            UpdateStepUi();

            if (_step == 2)
            {
                await RefreshStep2StatusAsync();
            }
            else if (_step == 3)
            {
                SetConnectingWaitState(true, "Verificando estado das contas locais...");
                try
                {
                    await RefreshStep3RemotesAsync(verifyImportedConnections: _onboarding.ImportedRemotes.Count > 0);
                }
                finally
                {
                    SetConnectingWaitState(false);
                }
            }
            else if (_step == 4)
            {
                RefreshStep4ModeAndTargets();
            }

            UpdateStepUi();
            return;
        }

        await FinishWizardAsync();
    }

    private void DismissWizardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isConnectingProvider)
            return;
        CloseOverlay?.Invoke(false);
    }

    private async Task FinishWizardAsync()
    {
        List<SyncJob> finalJobs;
        BackgroundServiceMode selectedServiceMode;

        if (_startMode == WizardStartMode.Import)
        {
            CommitCurrentImportedJobEdits();
            finalJobs = _reviewJobs.Select(r => r.Job).ToList();
            selectedServiceMode = (WizardImportServiceModeCombo.SelectedItem as UiOption<BackgroundServiceMode>)?.Value
                                  ?? BackgroundServiceMode.UserStartup;
        }
        else
        {
            finalJobs = BuildJobsFromScratchSelections();
            selectedServiceMode = (WizardServiceModeCombo.SelectedItem as UiOption<BackgroundServiceMode>)?.Value
                                  ?? BackgroundServiceMode.UserStartup;
        }

        NextButton.IsEnabled = false;
        BackButton.IsEnabled = false;
        try
        {
            var (ok, note) = await _onboarding.CompleteWithServiceModeAsync(finalJobs, selectedServiceMode);
            if (!ok && !string.IsNullOrWhiteSpace(note))
            {
                await OverlayHost.ShowCurrentAlertAsync("Configuração do Serviço em 2º Plano", note, OverlayAlertKind.Warning);
            }
            CloseOverlay?.Invoke(true);
        }
        finally
        {
            NextButton.IsEnabled = true;
            BackButton.IsEnabled = _step > 1;
        }
    }
}
