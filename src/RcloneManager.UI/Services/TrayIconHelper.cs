using System.Runtime.InteropServices;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace RcloneManager.UI.Services;

public sealed class TrayIconHelper : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly Drawing.Icon _generatedIcon;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public TrayIconHelper(
        Action onOpenWindow,
        Action onRunPrimaryJob,
        Action onStopJob,
        Action onExitUiOnly,
        Action onExitAll)
    {
        _generatedIcon = LoadAppTrayIcon();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Abrir Painel RcloneManager", null, (_, _) => onOpenWindow());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Executar Backup Principal Agora", null, (_, _) => onRunPrimaryJob());
        menu.Items.Add("Parar Execução Atual", null, (_, _) => onStopJob());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Fechar apenas Interface (manter Serviço)", null, (_, _) => onExitUiOnly());
        menu.Items.Add("Encerrar Interface + Serviço", null, (_, _) => onExitAll());

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _generatedIcon,
            Text = "RcloneManager - Serviço e Agendador",
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => onOpenWindow();
    }

    public void UpdateStatusText(string text)
    {
        // NotifyIcon.Text tem limite de 63/127 chars dependendo do Windows; truncamos em 63 por segurança.
        var safe = text.Length > 63 ? text[..63] : text;
        _notifyIcon.Text = safe;
    }

    public void ShowBalloon(string title, string message, WinForms.ToolTipIcon icon = WinForms.ToolTipIcon.Info)
    {
        _notifyIcon.ShowBalloonTip(3500, title, message, icon);
    }

    private static Drawing.Icon LoadAppTrayIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/rclone_manager.ico"));
            if (res?.Stream is not null)
            {
                using (res.Stream)
                {
                    return new Drawing.Icon(res.Stream, WinForms.SystemInformation.SmallIconSize);
                }
            }
        }
        catch
        {
            // Fallback seguro caso o stream de recurso não esteja disponível
        }

        return CreateCloudIcon();
    }

    private static Drawing.Icon CreateCloudIcon()
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using var g = Drawing.Graphics.FromImage(bmp);
        g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Drawing.Color.Transparent);

        using var bgBrush = new Drawing.SolidBrush(Drawing.Color.FromArgb(37, 99, 235));
        g.FillEllipse(bgBrush, 1, 1, 30, 30);

        using var cloudBrush = new Drawing.SolidBrush(Drawing.Color.White);
        g.FillEllipse(cloudBrush, 7, 13, 10, 10);
        g.FillEllipse(cloudBrush, 12, 9, 11, 11);
        g.FillEllipse(cloudBrush, 17, 14, 9, 9);
        g.FillRectangle(cloudBrush, 11, 16, 11, 7);

        var hIcon = bmp.GetHicon();
        try
        {
            using var tempIcon = Drawing.Icon.FromHandle(hIcon);
            return (Drawing.Icon)tempIcon.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _generatedIcon.Dispose();
    }
}
