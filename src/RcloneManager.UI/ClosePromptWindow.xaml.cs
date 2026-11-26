using System.Windows;
using System.Windows.Controls;
using RcloneManager.UI.Services;

namespace RcloneManager.UI;

public partial class ClosePromptWindow : System.Windows.Controls.UserControl, IOverlayDialog
{
    public Action<bool?>? CloseOverlay { get; set; }
    public bool CloseOnBackdropClick => true;

    public CloseWindowBehavior SelectedBehavior { get; private set; } = CloseWindowBehavior.MinimizeToTray;
    public bool RememberChoice => RememberChoiceCheck.IsChecked == true;

    public ClosePromptWindow()
    {
        InitializeComponent();
    }

    private void MinimizeToTrayButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedBehavior = CloseWindowBehavior.MinimizeToTray;
        CloseOverlay?.Invoke(true);
    }

    private void CloseUiOnlyButton_Click(object sender, RoutedEventArgs e)
    {
        SelectedBehavior = CloseWindowBehavior.CloseKeepService;
        CloseOverlay?.Invoke(true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseOverlay?.Invoke(false);
    }
}
