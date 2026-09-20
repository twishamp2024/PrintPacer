using System.Drawing;
using System.Windows.Forms;
using PrintPacer.Core.Engine;
using Serilog;

namespace PrintPacer.App.Platform;

/// <summary>
/// Tray presence and user notifications.
///
/// Notifications go through the shell's notification-area API. On Windows 10 and 11 the
/// shell renders these as ordinary toast notifications, so they appear in the Action Centre
/// alongside everything else, honour Focus Assist and Quiet Hours, and require no packaged
/// identity or Start Menu AppUserModelID registration. That last point matters for an
/// unattended overnight tool: there is nothing to get out of step between the installer and
/// the running application.
///
/// The tray icon also keeps the application reachable when its window is closed, which is
/// the normal state of affairs while a job is running overnight.
/// </summary>
public sealed class TrayNotifier : INotifier, IDisposable
{
    private static readonly ILogger Log = Serilog.Log.ForContext<TrayNotifier>();

    private readonly NotifyIcon _icon;
    private readonly Func<bool> _notificationsEnabled;

    /// <summary>Raised when the user asks to see the window again.</summary>
    public event Action? ShowRequested;

    /// <summary>Raised when the user chooses Exit from the tray menu.</summary>
    public event Action? ExitRequested;

    public TrayNotifier(Func<bool> notificationsEnabled)
    {
        _notificationsEnabled = notificationsEnabled;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open PrintPacer", null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = "PrintPacer",
            ContextMenuStrip = menu,
        };

        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    /// <summary>Short status shown on hover, so progress is visible without opening anything.</summary>
    public void SetStatus(string status)
    {
        // The shell truncates the tooltip at 63 characters and silently fails beyond that.
        var text = status.Length > 62 ? status[..59] + "..." : status;
        try { _icon.Text = text; }
        catch (Exception ex) { Log.Debug(ex, "Could not update the tray tooltip"); }
    }

    public void Notify(Notification notification)
    {
        if (!_notificationsEnabled()) return;

        var kind = notification.Kind switch
        {
            NotificationKind.Problem => ToolTipIcon.Error,
            NotificationKind.Warning => ToolTipIcon.Warning,
            _ => ToolTipIcon.Info,
        };

        try
        {
            _icon.ShowBalloonTip(15_000, notification.Title, notification.Message, kind);
            Log.Debug("Notified: {Title} - {Message}", notification.Title, notification.Message);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not show a notification");
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/printpacer.ico");
            var stream = Application.GetResourceStream(uri)?.Stream;
            if (stream is not null) return new Icon(stream);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Falling back to the default application icon");
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
