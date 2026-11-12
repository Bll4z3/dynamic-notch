using System.IO;
using Forms = System.Windows.Forms;

namespace DynamicNotch.Services;

/// <summary>Notification-area icon: Minimal notch, Start with Windows, notification access, Quit.</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _minimal;
    private readonly Forms.ToolStripMenuItem _startup;
    private readonly Forms.ToolStripMenuItem _notifications;

    public event Action<bool>? MinimalToggled;
    public event Action<bool>? StartupToggled;
    public event Action? NotificationAccessRequested;
    public event Action? QuitRequested;
    public event Action? SettingsRequested;

    public TrayService()
    {
        _minimal = new Forms.ToolStripMenuItem("Minimal notch") { CheckOnClick = true };
        _minimal.Click += (_, _) => MinimalToggled?.Invoke(_minimal.Checked);

        _startup = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        _startup.Click += (_, _) => StartupToggled?.Invoke(_startup.Checked);

        _notifications = new Forms.ToolStripMenuItem("Enable app notifications…") { Visible = false };
        _notifications.Click += (_, _) => NotificationAccessRequested?.Invoke();

        var settings = new Forms.ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => SettingsRequested?.Invoke();

        var quit = new Forms.ToolStripMenuItem("Quit");
        quit.Click += (_, _) => QuitRequested?.Invoke();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange(new Forms.ToolStripItem[] { settings, _minimal, _startup, _notifications, new Forms.ToolStripSeparator(), quit });

        _icon = new Forms.NotifyIcon
        {
            Text = "Dynamic Notch",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
    }

    public void SetMinimal(bool on) => _minimal.Checked = on;
    public void SetStartup(bool on) => _startup.Checked = on;

    public void SetNotificationAccess(NotificationAccess access)
    {
        _notifications.Visible = access is NotificationAccess.Denied or NotificationAccess.NoPackageIdentity;
        _notifications.Text = access == NotificationAccess.NoPackageIdentity
            ? "App notifications need the packaged build"
            : "Enable app notifications…";
        _notifications.Enabled = access == NotificationAccess.Denied;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(path)) return new System.Drawing.Icon(path, 16, 16);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
