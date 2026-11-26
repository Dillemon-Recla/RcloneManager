using System.Windows;
using RcloneManager.UI.Services;

namespace RcloneManager.UI;

public partial class ResetConfigWindow : System.Windows.Controls.UserControl, IOverlayDialog
{
    public Action<bool?>? CloseOverlay { get; set; }
    public bool CloseOnBackdropClick => true;

    public bool DeleteLogsAndRuntime { get; private set; }
    public bool DeleteCredentialKey => true;
    public bool DeleteRcloneConf { get; private set; }
    public bool ReopenWizard { get; private set; } = true;

    public ResetConfigWindow()
    {
        InitializeComponent();
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (DeleteRcloneConfCheck.IsChecked == true)
        {
            var extraConfirm = await OverlayHost.ShowCurrentConfirmAsync(
                "Apagar contas conectadas?",
                "Você marcou para apagar também as contas configuradas no 'rclone.conf' (ex.: Google Drive). Um backup .bak será criado antes, mas será necessário reconectar suas contas. Deseja continuar?",
                confirmText: "Sim, apagar tudo",
                cancelText: "Voltar",
                isDanger: true);

            if (!extraConfirm)
                return;
        }

        DeleteLogsAndRuntime = DeleteLogsRuntimeCheck.IsChecked == true;
        DeleteRcloneConf = DeleteRcloneConfCheck.IsChecked == true;
        ReopenWizard = ReopenWizardCheck.IsChecked == true;

        CloseOverlay?.Invoke(true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseOverlay?.Invoke(false);
    }
}
