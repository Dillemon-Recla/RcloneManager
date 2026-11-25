using System.ComponentModel;
using System.Windows;
using RcloneManager.Core.Engine;

namespace RcloneManager.UI;

public partial class RcloneSetupWindow : Window
{
    private readonly string _rootDir;
    private readonly string _pinnedVersion;
    private CancellationTokenSource? _cts;
    private bool _isRunning;
    private bool _completed;

    public string DownloadedExecutablePath { get; private set; } = string.Empty;

    public RcloneSetupWindow(string rootDir, string pinnedVersion, string reason)
    {
        InitializeComponent();
        _rootDir = rootDir;
        _pinnedVersion = RcloneReleaseManager.NormalizeVersion(pinnedVersion);

        TargetInfoText.Text = $"Release travada: {_pinnedVersion} ({RcloneReleaseManager.GetWindowsArchToken()}) — verificação SHA-256";
        ReasonText.Text = reason;
        Loaded += async (_, _) => await StartDownloadAsync();
    }

    private async Task StartDownloadAsync()
    {
        if (_isRunning)
            return;

        _isRunning = true;
        RetryButton.IsEnabled = false;
        ErrorText.Text = string.Empty;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        var progress = new Progress<RcloneDownloadProgress>(p =>
        {
            DownloadProgressBar.Value = p.Percent;
            PercentText.Text = $"{p.Percent:0}%";
            StageDetailText.Text = string.IsNullOrWhiteSpace(p.Detail) ? p.Stage : p.Detail;
        });

        try
        {
            var exePath = await RcloneReleaseManager.EnsurePinnedReleaseDownloadedAsync(
                _rootDir,
                _pinnedVersion,
                progress,
                _cts.Token);

            DownloadedExecutablePath = exePath;
            _completed = true;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            StageDetailText.Text = "Download cancelado.";
            ErrorText.Text = "Operação cancelada pelo usuário.";
            RetryButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            StageDetailText.Text = "Falha no provisionamento.";
            ErrorText.Text = ex.Message;
            RetryButton.IsEnabled = true;
        }
        finally
        {
            _isRunning = false;
        }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        await StartDownloadAsync();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            _cts?.Cancel();
            return;
        }

        DialogResult = false;
        Close();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isRunning && !_completed)
        {
            _cts?.Cancel();
        }
    }
}
