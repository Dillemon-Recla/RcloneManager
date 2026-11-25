using System.Windows;

namespace RcloneManager.UI;

public partial class ResetConfigWindow : Window
{
    public bool DeleteLogsAndRuntime { get; private set; }
    public bool DeleteCredentialKey { get; private set; }
    public bool DeleteRcloneConf { get; private set; }
    public bool ReopenWizard { get; private set; } = true;

    public ResetConfigWindow()
    {
        InitializeComponent();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeleteRcloneConfCheck.IsChecked == true)
        {
            var extraConfirm = System.Windows.MessageBox.Show(
                "Você marcou para apagar o 'rclone.conf' (remotes configurados como Google Drive). Um backup .bak será criado antes, mas será necessário reconfigurar seus remotes. Continuar?",
                "Confirmação Adicional",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (extraConfirm != MessageBoxResult.Yes)
                return;
        }

        DeleteLogsAndRuntime = DeleteLogsRuntimeCheck.IsChecked == true;
        DeleteCredentialKey = DeleteVaultKeyCheck.IsChecked == true;
        DeleteRcloneConf = DeleteRcloneConfCheck.IsChecked == true;
        ReopenWizard = ReopenWizardCheck.IsChecked == true;

        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
