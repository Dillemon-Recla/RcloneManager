using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using MediaColor = System.Windows.Media.Color;
using WpfApp = System.Windows.Application;
using WpfButton = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace RcloneManager.UI.Services;

public enum OverlayAlertKind
{
    Info,
    Warning,
    Error
}

public interface IOverlayDialog
{
    Action<bool?>? CloseOverlay { get; set; }
    bool CloseOnBackdropClick => false;
    bool CanDismissWithEscape => true;
    void OnOverlayDismissed() { }
}

public sealed class OverlayHost
{
    private sealed class LayerEntry
    {
        public required Grid LayerGrid { get; init; }
        public required FrameworkElement Content { get; init; }
        public required TaskCompletionSource<bool?> Tcs { get; init; }
    }

    public static OverlayHost? Current { get; private set; }

    private readonly Grid _container;
    private readonly List<LayerEntry> _stack = new();

    public bool HasActiveOverlay => _stack.Count > 0;

    public OverlayHost(Grid container)
    {
        _container = container;
        Current = this;
    }

    public static Task<bool?> ShowCurrentAsync(FrameworkElement content)
    {
        return Current != null ? Current.ShowAsync(content) : Task.FromResult<bool?>(false);
    }

    public static Task ShowCurrentAlertAsync(string title, string message, OverlayAlertKind kind = OverlayAlertKind.Info)
    {
        return Current != null ? Current.ShowAlertAsync(title, message, kind) : Task.CompletedTask;
    }

    public static Task<bool> ShowCurrentConfirmAsync(
        string title,
        string message,
        string confirmText = "Confirmar",
        string cancelText = "Cancelar",
        bool isDanger = false)
    {
        return Current != null
            ? Current.ShowConfirmAsync(title, message, confirmText, cancelText, isDanger)
            : Task.FromResult(false);
    }

    public Task<bool?> ShowAsync(FrameworkElement content)
    {
        var tcs = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var layer = new Grid
        {
            Background = new SolidColorBrush(MediaColor.FromArgb(205, 6, 11, 22))
        };

        var card = new Border
        {
            Background = new SolidColorBrush(MediaColor.FromRgb(15, 23, 42)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(71, 85, 105)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(28),
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 34,
                ShadowDepth = 8,
                Opacity = 0.68
            },
            Child = content
        };

        // Impede que cliques dentro do card fechem o backdrop
        card.MouseLeftButtonDown += (_, e) => e.Handled = true;

        var entry = new LayerEntry
        {
            LayerGrid = layer,
            Content = content,
            Tcs = tcs
        };

        void CompleteLayer(bool? result)
        {
            if (!_stack.Remove(entry))
                return;

            if (result != true && content is IOverlayDialog dlgDismiss)
                dlgDismiss.OnOverlayDismissed();

            _container.Children.Remove(layer);
            if (_stack.Count == 0)
                _container.Visibility = Visibility.Collapsed;

            tcs.TrySetResult(result);
        }

        if (content is IOverlayDialog overlayDialog)
        {
            overlayDialog.CloseOverlay = CompleteLayer;
            layer.MouseLeftButtonDown += (_, _) =>
            {
                if (overlayDialog.CloseOnBackdropClick)
                    CompleteLayer(false);
            };
        }

        layer.Children.Add(card);
        _stack.Add(entry);
        _container.Children.Add(layer);
        _container.Visibility = Visibility.Visible;

        content.Dispatcher.BeginInvoke(() =>
        {
            content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });

        return tcs.Task;
    }

    public bool TryDismissTop()
    {
        if (_stack.Count == 0)
            return false;

        var top = _stack[^1];
        if (top.Content is IOverlayDialog dlg)
        {
            if (!dlg.CanDismissWithEscape)
                return true;

            dlg.CloseOverlay?.Invoke(false);
            return true;
        }

        _stack.RemoveAt(_stack.Count - 1);
        _container.Children.Remove(top.LayerGrid);
        if (_stack.Count == 0)
            _container.Visibility = Visibility.Collapsed;
        top.Tcs.TrySetResult(false);
        return true;
    }

    public async Task ShowAlertAsync(string title, string message, OverlayAlertKind kind = OverlayAlertKind.Info)
    {
        var accentColor = kind switch
        {
            OverlayAlertKind.Error => MediaColor.FromRgb(239, 68, 68),
            OverlayAlertKind.Warning => MediaColor.FromRgb(245, 158, 11),
            _ => MediaColor.FromRgb(56, 189, 248)
        };

        var dialog = new InlineMessageOverlay(
            title,
            message,
            accentColor,
            confirmText: "Entendi",
            cancelText: null,
            isDanger: false);

        await ShowAsync(dialog);
    }

    public async Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string confirmText = "Confirmar",
        string cancelText = "Cancelar",
        bool isDanger = false)
    {
        var accentColor = isDanger
            ? MediaColor.FromRgb(239, 68, 68)
            : MediaColor.FromRgb(56, 189, 248);

        var dialog = new InlineMessageOverlay(
            title,
            message,
            accentColor,
            confirmText,
            cancelText,
            isDanger);

        return await ShowAsync(dialog) == true;
    }

    private sealed class InlineMessageOverlay : Border, IOverlayDialog
    {
        public Action<bool?>? CloseOverlay { get; set; }
        public bool CloseOnBackdropClick => true;

        public InlineMessageOverlay(
            string title,
            string message,
            MediaColor accentColor,
            string confirmText,
            string? cancelText,
            bool isDanger)
        {
            Width = 440;
            Padding = new Thickness(20);

            var root = new StackPanel();

            var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var accentDot = new Border
            {
                Width = 4,
                CornerRadius = new CornerRadius(2),
                Background = new SolidColorBrush(accentColor),
                Margin = new Thickness(0, 2, 10, 2)
            };
            Grid.SetColumn(accentDot, 0);
            headerGrid.Children.Add(accentDot);

            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(248, 250, 252)),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(titleBlock, 1);
            headerGrid.Children.Add(titleBlock);
            root.Children.Add(headerGrid);

            var msgScroll = new ScrollViewer
            {
                MaxHeight = 260,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 0, 18)
            };
            msgScroll.Content = new TextBlock
            {
                Text = message,
                FontSize = 12.5,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(203, 213, 225)),
                TextWrapping = TextWrapping.Wrap
            };
            root.Children.Add(msgScroll);

            var btnRow = new StackPanel
            {
                Orientation = WpfOrientation.Horizontal,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };

            if (!string.IsNullOrWhiteSpace(cancelText))
            {
                var cancelBtn = new WpfButton
                {
                    Content = cancelText,
                    Margin = new Thickness(0, 0, 8, 0),
                    MinWidth = 90
                };
                cancelBtn.Click += (_, _) => CloseOverlay?.Invoke(false);
                btnRow.Children.Add(cancelBtn);
            }

            var okBtn = new WpfButton
            {
                Content = confirmText,
                MinWidth = 100
            };
            var styleKey = isDanger ? "DangerButtonStyle" : "PrimaryButtonStyle";
            if (WpfApp.Current?.TryFindResource(styleKey) is Style btnStyle)
                okBtn.Style = btnStyle;
            okBtn.Click += (_, _) => CloseOverlay?.Invoke(true);
            btnRow.Children.Add(okBtn);

            root.Children.Add(btnRow);
            Child = root;
        }
    }
}
