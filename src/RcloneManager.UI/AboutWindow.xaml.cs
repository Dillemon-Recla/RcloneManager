using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using RcloneManager.Core.Models;
using RcloneManager.Core.Updater;
using RcloneManager.UI.Services;

namespace RcloneManager.UI;

public partial class AboutWindow : System.Windows.Controls.UserControl, IOverlayDialog
{
    public Action<bool?>? CloseOverlay { get; set; }
    public bool CloseOnBackdropClick => true;

    private readonly string _workDir;

    public AboutWindow(ServiceStatusDto? status, string workDir)
    {
        InitializeComponent();
        _workDir = workDir;

        AppVersionText.Text = $"v{AppAutoUpdater.FormatVersion(AppAutoUpdater.GetCurrentVersion())}";

        RuntimeInfoText.Text = $"Runtime: {RuntimeInformation.FrameworkDescription}";
        OsInfoText.Text = $"Sistema: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

        if (status != null)
        {
            var lockLabel = status.LockRcloneVersion
                ? (status.IsRcloneVersionValid ? $"Travado em {status.PinnedRcloneVersion}" : $"DIVERGENTE (travado em {status.PinnedRcloneVersion})")
                : "Sem trava";
            RcloneInfoText.Text = $"Rclone: {status.RcloneVersion} ({lockLabel})";
        }
        else
        {
            RcloneInfoText.Text = "Rclone: serviço local offline no momento";
        }
    }

    private async void OpenLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        var licensePath = Path.Combine(_workDir, "LICENSE");
        if (File.Exists(licensePath))
        {
            OpenTarget(licensePath);
            return;
        }

        await OverlayHost.ShowCurrentAlertAsync(
            "Licença MIT",
            "MIT License — Copyright (c) 2026 Dillemon Recla.\n\nPermite uso, cópia, modificação e distribuição livre mantendo o aviso de copyright e a licença.");
    }

    private void OpenGitHubButton_Click(object sender, RoutedEventArgs e)
    {
        OpenTarget("https://github.com/Dillemon-Recla/RcloneManager");
    }

    private void OpenRcloneSiteButton_Click(object sender, RoutedEventArgs e)
    {
        OpenTarget("https://rclone.org/");
    }

    private void OpenSponsorsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenTarget("https://github.com/sponsors/Dillemon-Recla");
    }

    private void OpenKofiButton_Click(object sender, RoutedEventArgs e)
    {
        OpenTarget("https://ko-fi.com/dillemonrecla");
    }

    private void CopyPixButton_Click(object sender, RoutedEventArgs e)
    {
        const string pixKey = "95b9aa86-3c99-421d-9c86-6a47a3b09d7b";
        System.Windows.Clipboard.SetText(pixKey);
        CopiedPixText.Text = "Chave Pix copiada!";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseOverlay?.Invoke(true);
    }

    private static void OpenTarget(string target)
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
            _ = OverlayHost.ShowCurrentAlertAsync(
                "Não foi possível abrir",
                $"Não foi possível abrir '{target}': {ex.Message}",
                OverlayAlertKind.Warning);
        }
    }
}
