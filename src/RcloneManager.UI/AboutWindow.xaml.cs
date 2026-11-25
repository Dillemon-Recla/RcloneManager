using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using RcloneManager.Core.Models;

namespace RcloneManager.UI;

public partial class AboutWindow : Window
{
    private readonly string _workDir;

    public AboutWindow(ServiceStatusDto? status, string workDir)
    {
        InitializeComponent();
        _workDir = workDir;

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        AppVersionText.Text = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.0.0";

        RuntimeInfoText.Text = $"Runtime: {RuntimeInformation.FrameworkDescription}";
        OsInfoText.Text = $"Sistema: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";

        if (status != null)
        {
            var lockLabel = status.LockRcloneVersion
                ? (status.IsRcloneVersionValid ? $"Travado em {status.PinnedRcloneVersion}" : $"DIVERGENTE (travado em {status.PinnedRcloneVersion})")
                : "Sem trava";
            RcloneInfoText.Text = $"Rclone: {status.RcloneVersion} ({lockLabel}) — Serviço 127.0.0.1:5580";
        }
        else
        {
            RcloneInfoText.Text = "Rclone: serviço local offline no momento";
        }
    }

    private void OpenLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        var licensePath = Path.Combine(_workDir, "LICENSE");
        if (File.Exists(licensePath))
        {
            OpenTarget(licensePath);
            return;
        }

        System.Windows.MessageBox.Show(
            "MIT License — Copyright (c) 2026 Dillemon Recla.\n\nPermite uso, cópia, modificação e distribuição livre mantendo o aviso de copyright e a licença.",
            "Licença MIT",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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

    private void CopyPixButton_Click(object sender, RoutedEventArgs e)
    {
        const string pixKey = "95b9aa86-3c99-421d-9c86-6a47a3b09d7b";
        System.Windows.Clipboard.SetText(pixKey);
        CopiedPixText.Text = "Chave Pix copiada!";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
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
            System.Windows.MessageBox.Show($"Não foi possível abrir '{target}': {ex.Message}", "RcloneManager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
