using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.ApplicationModel;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DynamicNotch.Services;

public enum NotificationAccess
{
    Unknown,
    /// <summary>Running without package identity (plain debug run) — the API is unavailable.</summary>
    NoPackageIdentity,
    Denied,
    Allowed,
    Unavailable,
}

/// <summary>
/// Mirrors new Windows toast notifications as pills using UserNotificationListener. Requires
/// package identity (MSIX) and the user's permission; when either is missing, the rest of the
/// app keeps working and <see cref="Access"/> explains why.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private UserNotificationListener? _listener;
    private readonly HashSet<uint> _seen = new();
    private DispatcherTimer? _poll;
    private bool _eventMode;
    private bool _busy;

    public NotificationAccess Access { get; private set; } = NotificationAccess.Unknown;

    public event Action<PillRequest>? Pill;
    public event Action<NotificationAccess>? AccessChanged;

    public static bool HasPackageIdentity()
    {
        try { return Package.Current?.Id != null; }
        catch { return false; }
    }

    /// <summary>Must be called on the UI thread (RequestAccessAsync shows a consent prompt).</summary>
    public async Task StartAsync()
    {
        if (!HasPackageIdentity()) { SetAccess(NotificationAccess.NoPackageIdentity); return; }
        try
        {
            _listener = UserNotificationListener.Current;
            var status = await _listener.RequestAccessAsync();
            if (status != UserNotificationListenerAccessStatus.Allowed)
            {
                SetAccess(NotificationAccess.Denied);
                return;
            }
            SetAccess(NotificationAccess.Allowed);

            foreach (var n in await _listener.GetNotificationsAsync(NotificationKinds.Toast)) _seen.Add(n.Id);

            try
            {
                // Event delivery is only supported for some app types/builds; fall back to a light poll.
                _listener.NotificationChanged += OnNotificationChanged;
                _eventMode = true;
            }
            catch
            {
                _eventMode = false;
                _poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                _poll.Tick += async (_, _) => await CheckForNewAsync();
                _poll.Start();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Notification listener unavailable: {ex.Message}");
            SetAccess(NotificationAccess.Unavailable);
        }
    }

    /// <summary>Re-check after the user changes the permission in Settings.</summary>
    public async Task RetryAsync()
    {
        Dispose();
        _seen.Clear();
        await StartAsync();
    }

    public static void OpenPermissionSettings()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-notifications") { UseShellExecute = true }); }
        catch { }
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind != UserNotificationChangedKind.Added) return;
        Application.Current?.Dispatcher.InvokeAsync(async () => await CheckForNewAsync());
    }

    private async Task CheckForNewAsync()
    {
        if (_listener == null || _busy) return;
        _busy = true;
        try
        {
            var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var current = new HashSet<uint>();
            foreach (var n in list)
            {
                current.Add(n.Id);
                if (_seen.Contains(n.Id)) continue;
                var pill = await ToPillAsync(n);
                if (pill != null) Pill?.Invoke(pill);
            }
            _seen.Clear();
            _seen.UnionWith(current);
        }
        catch (UnauthorizedAccessException)
        {
            SetAccess(NotificationAccess.Denied);
            _poll?.Stop();
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Notification read failed: {ex.Message}"); }
        finally { _busy = false; }
    }

    private static async Task<PillRequest?> ToPillAsync(UserNotification n)
    {
        string appName = "";
        BitmapSource? logo = null;
        try
        {
            appName = n.AppInfo?.DisplayInfo?.DisplayName ?? "";
            var logoRef = n.AppInfo?.DisplayInfo?.GetLogo(new Windows.Foundation.Size(64, 64));
            if (logoRef != null) logo = await LoadAsync(logoRef);
        }
        catch { /* some Win32 senders have no logo */ }

        var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        var texts = binding?.GetTextElements().Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
                    ?? new List<string>();
        if (texts.Count == 0 && string.IsNullOrEmpty(appName)) return null;

        string title = texts.Count > 0 ? texts[0] : appName;
        string body = texts.Count > 1 ? string.Join(" · ", texts.Skip(1)) : "";
        return new PillRequest(PillSource.App, title, body, Logo: logo, AppName: appName);
    }

    private static async Task<BitmapSource?> LoadAsync(Windows.Storage.Streams.RandomAccessStreamReference r)
    {
        using var ras = await r.OpenReadAsync();
        using var s = ras.AsStreamForRead();
        var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        ms.Position = 0;
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = 64;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void SetAccess(NotificationAccess a)
    {
        Access = a;
        AccessChanged?.Invoke(a);
    }

    public void Dispose()
    {
        _poll?.Stop();
        _poll = null;
        if (_listener != null && _eventMode)
        {
            try { _listener.NotificationChanged -= OnNotificationChanged; } catch { }
        }
        _eventMode = false;
    }
}
