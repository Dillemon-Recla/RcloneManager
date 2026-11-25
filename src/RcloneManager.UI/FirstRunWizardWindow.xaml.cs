using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RcloneManager.Core.Engine;
using RcloneManager.Core.Logging;
using RcloneManager.Core.Models;
using RcloneManager.Core.Security;
using RcloneManager.Core.Storage;
using RcloneManager.UI.Services;
using WinForms = System.Windows.Forms;

namespace RcloneManager.UI;

internal sealed class WizardRemoteBlueprintItem
{
    public ExportedRemoteBlueprint Blueprint { get; init; } = new();
    public bool IsAuthenticated { get; set; }

    public override string ToString()
    {
        var status = IsAuthenticated ? "✓ Autenticado" : "Pendente de autenticação";
        return $"{Blueprint.Name}: ({Blueprint.Type}) — {status}";
    }
}

internal sealed class WizardJobReviewItem
{
    public SyncJob Job { get; init; } = new();

    public override string ToString()
    {
        var active = Job.Enabled ? "Ativa" : "Desativada";
        return $"[{active}] {Job.Name} | {Job.Operation}: {Job.SourcePath} -> {Job.DestinationPath} ({Job.Schedule.IntervalMinutes}m)";
    }
}

public partial class FirstRunWizardWindow : Window
{
    private readonly string _workDir;
    private readonly ServiceApiClient? _api;
    private readonly AppConfigStore _store;
    private readonly string? _initialImportFilePath;

    private AppManagerConfig _config;
    private readonly List<WizardRemoteBlueprintItem> _importedRemotes = new();
    private readonly List<WizardJobReviewItem> _reviewJobs = new();
    private int _selectedJobIndex = -1;
    private bool _isPopulatingJobFields;
    private int _step = 1;

    public FirstRunWizardWindow(string workDir, ServiceApiClient? api = null, string? initialImportFilePath = null)
    {
        InitializeComponent();
        _workDir = workDir;
        _api = api;
        _initialImportFilePath = initialImportFilePath;
        _store = new AppConfigStore(workDir);
        _config = _store.LoadOrCreateDefault();

        WizardJobOpCombo.ItemsSource = Enum.GetValues<RcloneOperation>();
        Loaded += FirstRunWizardWindow_Loaded;
    }

    private async void FirstRunWizardWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var keyTarget = SensitiveDataProtector.EnsureInitialized(out var created, _config.EncryptionKeyTarget);
        VaultStatusTitle.Text = created
            ? "Nova Chave AES-256 Gerada no Gerenciador de Credenciais"
            : "Chave AES-256 Ativa no Gerenciador de Credenciais";
        VaultStatusDetail.Text = $"Target: {keyTarget}";

        if (!string.IsNullOrWhiteSpace(_initialImportFilePath) && File.Exists(_initialImportFilePath))
        {
            await LoadImportedBundleAsync(_initialImportFilePath, rotateMasterKey: true);
        }
        else
        {
            SyncReviewJobsFromConfig();
        }

        WizardPinnedVersionBox.Text = _config.PinnedRcloneVersion;
        WizardLockVersionCheck.IsChecked = _config.LockRcloneVersion;

        await RefreshStep2StatusAsync();
        await RefreshStep3RemotesAsync();
        UpdateStepUi();
    }

    private async void WizardImportConfigButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.OpenFileDialog
        {
            Title = "Selecionar Configuração Exportada",
            Filter = "Configuração Portátil (*.json)|*.json|Todos os arquivos (*.*)|*.*",
            InitialDirectory = _workDir
        };

        if (dialog.ShowDialog() != WinForms.DialogResult.OK)
            return;

        await LoadImportedBundleAsync(dialog.FileName, rotateMasterKey: true);
    }

    private async Task LoadImportedBundleAsync(string filePath, bool rotateMasterKey)
    {
        try
        {
            var bundle = _store.ReadPortableBundle(filePath);

            if (rotateMasterKey)
            {
                var target = SensitiveDataProtector.RotateMasterKey(_config.EncryptionKeyTarget);
                VaultStatusTitle.Text = "Nova Chave AES-256 Gerada para a Configuração Importada";
                VaultStatusDetail.Text = $"Target: {target} (chave anterior substituída com segurança)";
            }

            _config.PinnedRcloneVersion = string.IsNullOrWhiteSpace(bundle.PinnedRcloneVersion)
                ? RcloneReleaseManager.DefaultPinnedVersion
                : bundle.PinnedRcloneVersion;
            _config.LockRcloneVersion = bundle.LockRcloneVersion;
            _config.ServiceApiPort = bundle.ServiceApiPort > 0 ? bundle.ServiceApiPort : 5580;
            _config.PersistentRcdPort = bundle.PersistentRcdPort > 0 ? bundle.PersistentRcdPort : 5572;
            _config.AutoStartRcdDaemon = bundle.AutoStartRcdDaemon;
            _config.GlobalBandwidthLimit = string.IsNullOrWhiteSpace(bundle.GlobalBandwidthLimit) ? "off" : bundle.GlobalBandwidthLimit;

            if (bundle.Jobs.Count > 0)
            {
                _config.Jobs = bundle.Jobs;
            }

            WizardPinnedVersionBox.Text = _config.PinnedRcloneVersion;
            WizardLockVersionCheck.IsChecked = _config.LockRcloneVersion;

            _importedRemotes.Clear();
            foreach (var r in bundle.Remotes)
            {
                _importedRemotes.Add(new WizardRemoteBlueprintItem
                {
                    Blueprint = r,
                    IsAuthenticated = false
                });
            }

            SyncReviewJobsFromConfig();

            var servicesText = _importedRemotes.Count > 0
                ? string.Join(", ", _importedRemotes.Select(r => $"{r.Blueprint.Name} ({r.Blueprint.Type})"))
                : "nenhum remote externo";

            WizardImportSummaryText.Text =
                $"Importado '{Path.GetFileName(filePath)}': {_config.Jobs.Count} tarefa(s) | Serviços apontados (sem credenciais): {servicesText}.";
            WizardImportSummaryText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));

            await RefreshStep2StatusAsync();
            await RefreshStep3RemotesAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Erro ao importar arquivo: {ex.Message}", "Importar no Wizard", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void WizardRotateKeyButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = System.Windows.MessageBox.Show(
            "Deseja gerar uma nova chave AES-256 aleatória no Gerenciador de Credenciais do Windows? Dados locais salvos futuramente usarão a nova chave.",
            "Gerar Nova Chave",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        var target = SensitiveDataProtector.RotateMasterKey(_config.EncryptionKeyTarget);
        VaultStatusTitle.Text = "Nova Chave AES-256 Gerada no Gerenciador de Credenciais";
        VaultStatusDetail.Text = $"Target: {target} (gerada agora às {DateTime.Now:HH:mm:ss})";
    }

    private void SyncReviewJobsFromConfig()
    {
        _reviewJobs.Clear();
        foreach (var j in _config.Jobs)
            _reviewJobs.Add(new WizardJobReviewItem { Job = j });

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

    private void WizardJobsReviewList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingJobFields)
            return;

        CommitCurrentJobEditsToMemory();
        var idx = WizardJobsReviewList.SelectedIndex;
        if (idx >= 0 && idx < _reviewJobs.Count)
            LoadJobIntoReviewForm(idx);
    }

    private void LoadJobIntoReviewForm(int index)
    {
        _selectedJobIndex = index;
        var job = _reviewJobs[index].Job;
        WizardJobNameBox.Text = job.Name;
        WizardJobOpCombo.SelectedItem = job.Operation;
        WizardJobEnabledCheck.IsChecked = job.Enabled;
        WizardSourceBox.Text = job.SourcePath;
        WizardDestBox.Text = job.DestinationPath;
        WizardIntervalBox.Text = job.Schedule.IntervalMinutes.ToString();
    }

    private void CommitCurrentJobEditsToMemory()
    {
        if (_selectedJobIndex < 0 || _selectedJobIndex >= _reviewJobs.Count)
            return;

        var job = _reviewJobs[_selectedJobIndex].Job;
        if (!string.IsNullOrWhiteSpace(WizardJobNameBox.Text))
            job.Name = WizardJobNameBox.Text.Trim();
        if (WizardJobOpCombo.SelectedItem is RcloneOperation op)
            job.Operation = op;
        job.Enabled = WizardJobEnabledCheck.IsChecked == true;
        if (!string.IsNullOrWhiteSpace(WizardSourceBox.Text))
            job.SourcePath = WizardSourceBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(WizardDestBox.Text))
            job.DestinationPath = WizardDestBox.Text.Trim();
        if (int.TryParse(WizardIntervalBox.Text.Trim(), out var mins) && mins > 0)
            job.Schedule.IntervalMinutes = mins;
    }

    private void WizardApplyJobEdit_Click(object sender, RoutedEventArgs e)
    {
        CommitCurrentJobEditsToMemory();
        RefreshJobsReviewListUi(_selectedJobIndex);
    }

    private async Task RefreshStep2StatusAsync()
    {
        var check = await RcloneReleaseManager.CheckInstallationAsync(_config, _workDir);
        WizardReleaseStatusText.Text = $"{check.StatusMessage}\nCaminho: {check.ExecutablePath}";
    }

    private async Task RefreshStep3RemotesAsync()
    {
        var existingSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_config.RcloneExecutablePath))
            {
                var log = new ServiceLogBuffer(_workDir);
                await using var engine = new RcloneHybridEngine(log);
                var (remotes, _) = await engine.ListRemotesHybridAsync(_config);
                foreach (var r in remotes)
                    existingSet.Add(r.TrimEnd(':'));

                WizardExistingRemotesText.Text = remotes.Count > 0
                    ? $"Remotes já presentes no rclone.conf desta máquina: {string.Join(", ", remotes)}"
                    : "Nenhum remote configurado no rclone.conf desta máquina ainda.";
            }
            else
            {
                WizardExistingRemotesText.Text = "Instale o Rclone na Etapa 2 para autenticar ou listar remotes.";
            }
        }
        catch (Exception ex)
        {
            WizardExistingRemotesText.Text = $"Não foi possível listar remotes agora: {ex.Message}";
        }

        if (_importedRemotes.Count > 0)
        {
            foreach (var item in _importedRemotes)
            {
                if (existingSet.Contains(item.Blueprint.Name))
                    item.IsAuthenticated = true;
            }

            WizardImportedRemotesPanel.Visibility = Visibility.Visible;
            WizardImportedRemotesList.ItemsSource = null;
            WizardImportedRemotesList.ItemsSource = _importedRemotes;

            var firstPending = _importedRemotes.FindIndex(r => !r.IsAuthenticated);
            WizardImportedRemotesList.SelectedIndex = firstPending >= 0 ? firstPending : 0;
        }
        else
        {
            WizardImportedRemotesPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void WizardImportedRemotesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WizardImportedRemotesList.SelectedItem is not WizardRemoteBlueprintItem item)
            return;

        var bp = item.Blueprint;
        WizardRemoteNameBox.Text = bp.Name;
        SelectWizardProvider(bp.Type);
        SelectWizardAuthMode(bp.SuggestedAuthMode);

        WizardClientIdBox.Text = bp.NonSensitiveParameters.TryGetValue("client_id", out var cid) ? cid : string.Empty;
        WizardHostBox.Text = bp.NonSensitiveParameters.TryGetValue("endpoint", out var ep)
            ? ep
            : (bp.NonSensitiveParameters.TryGetValue("host", out var h)
                ? h
                : (bp.NonSensitiveParameters.TryGetValue("url", out var u) ? u : string.Empty));
        WizardRemoteActionText.Text = item.IsAuthenticated
            ? $"'{bp.Name}:' já está autenticado no rclone.conf (pode reautenticar se desejar)."
            : $"Aguardando autenticação para '{bp.Name}:' ({bp.Type}).";
    }

    private void SelectWizardProvider(string providerName)
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

    private void SelectWizardAuthMode(RemoteAuthMode mode)
    {
        var targetTag = mode.ToString();
        foreach (ComboBoxItem item in WizardAuthModeCombo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), targetTag, StringComparison.Ordinal))
            {
                WizardAuthModeCombo.SelectedItem = item;
                return;
            }
        }
    }

    private void UpdateStepUi()
    {
        Step1Panel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4Panel.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        var activeBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235));
        var doneBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(5, 150, 105));
        var idleBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 41, 59));

        Step1Badge.Background = _step == 1 ? activeBrush : doneBrush;
        Step2Badge.Background = _step == 2 ? activeBrush : (_step > 2 ? doneBrush : idleBrush);
        Step3Badge.Background = _step == 3 ? activeBrush : (_step > 3 ? doneBrush : idleBrush);
        Step4Badge.Background = _step == 4 ? activeBrush : idleBrush;

        BackButton.IsEnabled = _step > 1;
        NextButton.Content = _step == 4 ? "Concluir e Salvar" : "Avançar ▶";
    }

    private async void WizardVerifyReleaseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var version = RcloneReleaseManager.NormalizeVersion(WizardPinnedVersionBox.Text);
            _config.PinnedRcloneVersion = version;
            _config.LockRcloneVersion = WizardLockVersionCheck.IsChecked == true;

            var check = await RcloneReleaseManager.CheckInstallationAsync(_config, _workDir);
            if (check.NeedsDownload)
            {
                var setupWin = new RcloneSetupWindow(_workDir, check.PinnedVersion, check.StatusMessage) { Owner = this };
                if (setupWin.ShowDialog() == true && !string.IsNullOrWhiteSpace(setupWin.DownloadedExecutablePath))
                    _config.RcloneExecutablePath = setupWin.DownloadedExecutablePath;
            }
            else
            {
                _config.RcloneExecutablePath = check.ExecutablePath;
            }

            if (string.IsNullOrWhiteSpace(_config.RcloneConfigPath))
                _config.RcloneConfigPath = AppConfigStore.DetectRcloneConfigPath(_config.RcloneExecutablePath);

            _store.Save(_config);
            await RefreshStep2StatusAsync();
            await RefreshStep3RemotesAsync();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Erro ao verificar release: {ex.Message}", "Assistente Inicial", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void WizardProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateWizardAuthPanels();
    private void WizardAuthModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateWizardAuthPanels();

    private void UpdateWizardAuthPanels()
    {
        if (!IsLoaded)
            return;

        var provider = (WizardProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "drive";
        var modeTag = (WizardAuthModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "OAuthBrowserLink";

        WizardDriveHintBorder.Visibility = provider == "drive" && modeTag == "OAuthBrowserLink"
            ? Visibility.Visible
            : Visibility.Collapsed;

        WizardClientIdGrid.Visibility = modeTag is "OAuthBrowserLink" or "ManualTokenJson"
            ? Visibility.Visible
            : Visibility.Collapsed;

        WizardTokenPanel.Visibility = modeTag == "ManualTokenJson"
            ? Visibility.Visible
            : Visibility.Collapsed;

        WizardKeyPanel.Visibility = modeTag == "KeyOrCredentials"
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void WizardAuthorizeTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(_config.RcloneExecutablePath))
        {
            System.Windows.MessageBox.Show("Baixe o Rclone na Etapa 2 primeiro.", "Assistente Inicial", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var provider = (WizardProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "drive";
        var args = $"authorize \"{provider}\"";
        if (!string.IsNullOrWhiteSpace(WizardClientIdBox.Text) && !string.IsNullOrWhiteSpace(WizardClientSecretBox.Text))
            args += $" \"{WizardClientIdBox.Text.Trim()}\" \"{WizardClientSecretBox.Text.Trim()}\"";

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k \"\"{_config.RcloneExecutablePath}\" {args}\"",
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

    private void WizardBrowseSource_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(WizardSourceBox.Text) ? WizardSourceBox.Text : string.Empty
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            WizardSourceBox.Text = dialog.SelectedPath;
    }

    private async void WizardCreateRemoteButton_Click(object sender, RoutedEventArgs e)
    {
        var remoteName = WizardRemoteNameBox.Text.Trim().TrimEnd(':');
        if (string.IsNullOrWhiteSpace(remoteName))
        {
            System.Windows.MessageBox.Show("Informe o nome do remote.", "Assistente Inicial", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var provider = (WizardProviderCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "drive";
        var modeTag = (WizardAuthModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "OAuthBrowserLink";
        var authMode = Enum.TryParse<RemoteAuthMode>(modeTag, out var parsed) ? parsed : RemoteAuthMode.KeyOrCredentials;

        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var importedMatch = _importedRemotes.FirstOrDefault(r => r.Blueprint.Name.Equals(remoteName, StringComparison.OrdinalIgnoreCase));
        if (importedMatch != null)
        {
            foreach (var (k, v) in importedMatch.Blueprint.NonSensitiveParameters)
                parameters[k] = v;
        }

        if (!string.IsNullOrWhiteSpace(WizardClientIdBox.Text))
            parameters["client_id"] = WizardClientIdBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(WizardClientSecretBox.Text))
            parameters["client_secret"] = WizardClientSecretBox.Text.Trim();

        if (authMode == RemoteAuthMode.KeyOrCredentials)
        {
            if (!string.IsNullOrWhiteSpace(WizardKeyFileBox.Text))
            {
                var keyField = provider == "drive" ? "service_account_file" : "key_file";
                parameters[keyField] = WizardKeyFileBox.Text.Trim();
            }
            if (!string.IsNullOrWhiteSpace(WizardHostBox.Text))
            {
                var hostField = provider == "s3" ? "endpoint" : (provider == "webdav" ? "url" : "host");
                parameters[hostField] = WizardHostBox.Text.Trim();
            }
            if (!string.IsNullOrWhiteSpace(WizardUserBox.Text))
            {
                var userField = provider == "s3" ? "access_key_id" : "user";
                parameters[userField] = WizardUserBox.Text.Trim();
            }
            if (!string.IsNullOrEmpty(WizardPassBox.Password))
            {
                var passField = provider == "s3" ? "secret_access_key" : "pass";
                parameters[passField] = WizardPassBox.Password;
            }
        }

        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var log = new ServiceLogBuffer(_workDir);
        await using var engine = new RcloneHybridEngine(log);

        try
        {
            var (curRemotes, _) = await engine.ListRemotesHybridAsync(_config);
            foreach (var r in curRemotes)
                existingNames.Add(r.TrimEnd(':'));
        }
        catch { }

        var req = new CreateOrUpdateRemoteRequest
        {
            Name = remoteName,
            Type = provider,
            Parameters = parameters,
            IsUpdate = existingNames.Contains(remoteName),
            AuthMode = authMode,
            ManualTokenJson = WizardTokenBox.Text.Trim(),
            TriggerOAuthBrowser = authMode == RemoteAuthMode.OAuthBrowserLink,
            ObscurePasswords = true
        };

        WizardCreateRemoteButton.IsEnabled = false;
        WizardRemoteActionText.Text = authMode == RemoteAuthMode.OAuthBrowserLink
            ? "Aguardando login OAuth no navegador..."
            : "Autenticando e testando remote...";

        try
        {
            await engine.CreateOrUpdateRemoteHybridAsync(_config, req);
            var test = await engine.TestRemoteConnectionAsync(_config, remoteName);

            WizardRemoteActionText.Text = test.Success
                ? $"Sucesso! {test.Message}"
                : $"Salvo, aviso no teste: {test.Message}";

            if (importedMatch != null)
                importedMatch.IsAuthenticated = true;

            await RefreshStep3RemotesAsync();
        }
        catch (Exception ex)
        {
            WizardRemoteActionText.Text = $"Erro: {ex.Message}";
            System.Windows.MessageBox.Show($"Erro ao autenticar remote: {ex.Message}", "Assistente Inicial", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            WizardCreateRemoteButton.IsEnabled = true;
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_step > 1)
        {
            _step--;
            UpdateStepUi();
        }
    }

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_step < 4)
        {
            _step++;
            UpdateStepUi();
            return;
        }

        await FinishWizardAsync(saveReviewedJobs: true);
    }

    private async void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        await FinishWizardAsync(saveReviewedJobs: false);
    }

    private async Task FinishWizardAsync(bool saveReviewedJobs)
    {
        if (saveReviewedJobs)
        {
            CommitCurrentJobEditsToMemory();
            _config.Jobs = _reviewJobs.Select(r => r.Job).ToList();
        }

        if (string.IsNullOrWhiteSpace(_config.RcloneConfigPath))
            _config.RcloneConfigPath = AppConfigStore.DetectRcloneConfigPath(_config.RcloneExecutablePath);

        _config.FirstRunCompleted = true;
        _store.Save(_config);

        if (_api != null)
        {
            try
            {
                await _api.ReloadServiceConfigAsync();
                if (saveReviewedJobs)
                    await _api.SetStartupAsync(WizardAutoStartupCheck.IsChecked == true);
            }
            catch
            {
            }
        }

        DialogResult = true;
        Close();
    }
}
