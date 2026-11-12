using System.Windows.Input;
using Microsoft.UI.Xaml;
using DynamicNotch.Design;
using DynamicNotch.Services;

namespace DynamicNotch.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly NotificationService _notifications;
    private readonly Queue<PillRequest> _pillQueue = new();
    private readonly DispatcherTimer _pillTimer;

    public MainViewModel(SettingsService settings, MusicViewModel music, ClipboardViewModel clipboard,
                         ShelfViewModel shelf, AskViewModel ask, StatusViewModel status, DevicesViewModel devices,
                         PageRegistry pages, NotificationService notifications, UsageViewModel usage, FocusTimerViewModel focus)
    {
        _settings = settings;
        _notifications = notifications;
        Usage = usage;
        Focus = focus;
        Music = music;
        Clipboard = clipboard;
        Shelf = shelf;
        Ask = ask;
        ask.OpenSettingsRequested += () => OpenSettingsRequested?.Invoke();
        Status = status;
        Devices = devices;
        Pages = pages;
        pages.Changed += OnPagesChanged;
        OpenSettingsCommand = new RelayCommand(() => OpenSettingsRequested?.Invoke());

        _pillTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Motion.PillDurationMs) };
        _pillTimer.Tick += (_, _) => DismissPill();

        DismissPillCommand = new RelayCommand(() =>
        {
            var click = CurrentPill?.OnClick;
            DismissPill();
            click?.Invoke();
        });
        EnableNotificationsCommand = new RelayCommand(NotificationService.OpenPermissionSettings);

        _notifications.AccessChanged += _ =>
        {
            OnPropertyChanged(nameof(ShowNotificationPrompt));
            OnPropertyChanged(nameof(NotificationPromptText));
        };
    }

    public MusicViewModel Music { get; }
    public ClipboardViewModel Clipboard { get; }
    public ShelfViewModel Shelf { get; }
    public AskViewModel Ask { get; }
    public StatusViewModel Status { get; }
    public DevicesViewModel Devices { get; }
    public UsageViewModel Usage { get; }
    public FocusTimerViewModel Focus { get; }

    public ICommand OpenSettingsCommand { get; }
    /// <summary>The app opens the (single) Settings window.</summary>
    public event Action? OpenSettingsRequested;

    public ICommand DismissPillCommand { get; }
    public ICommand EnableNotificationsCommand { get; }

    // ---- Pages (enabled pages only; see PageRegistry) ---------------------------------------------
    public PageRegistry Pages { get; }

    /// <summary>Number of pages currently switched on.</summary>
    public int PageCount => Pages.Enabled.Count;

    private int _pageIndex;
    /// <summary>Index into the ENABLED pages (0 .. PageCount-1).</summary>
    public int PageIndex
    {
        get => _pageIndex;
        set
        {
            if (Set(ref _pageIndex, Math.Clamp(value, 0, Math.Max(0, PageCount - 1))))
                RaisePageFlags();
        }
    }

    public PageId CurrentPage => Pages.Enabled.Count > 0 ? Pages.Enabled[Math.Clamp(PageIndex, 0, Pages.Enabled.Count - 1)] : PageId.Music;
    public bool IsMusicPage => CurrentPage == PageId.Music;
    public bool IsShelfPage => CurrentPage == PageId.Clipboard;
    public bool IsAskPage => CurrentPage == PageId.Ask;

    /// <summary>Go to a page if it's switched on. Returns false when it's off.</summary>
    public bool GoTo(PageId id)
    {
        int i = Pages.IndexOf(id);
        if (i < 0) return false;
        PageIndex = i;
        return true;
    }

    /// <summary>
    /// Raised after pages were switched on/off, before PageIndex changes. Args: the index the old page
    /// would have in the new layout (for a smooth spring), and whether the page you were on was removed.
    /// </summary>
    public event Action<double, bool>? PagesReconfigured;

    private PageId _lastPage = PageId.Music;

    private void RaisePageFlags()
    {
        _lastPage = CurrentPage;
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(IsMusicPage));
        OnPropertyChanged(nameof(IsShelfPage));
        OnPropertyChanged(nameof(IsAskPage));
    }

    private void OnPagesChanged()
    {
        var enabled = Pages.Enabled;
        var old = _lastPage;
        int index = Pages.IndexOf(old);
        bool removed = index < 0;
        double from = index;
        if (removed)
        {
            // Nearest enabled page in display order (the one after, else the one before).
            var all = PageRegistry.All.Select(p => p.Id).ToList();
            int pos = all.IndexOf(old);
            var next = all.Skip(pos + 1).FirstOrDefault(enabled.Contains, (PageId)(-1));
            var target = (int)next >= 0 ? next : all.Take(pos).LastOrDefault(enabled.Contains, enabled[0]);
            index = Pages.IndexOf(target);
            from = Math.Clamp(all.Take(pos).Count(enabled.Contains) - 0.5, 0, enabled.Count - 1); // where the old page sat
        }
        OnPropertyChanged(nameof(PageCount));
        PagesReconfigured?.Invoke(from, removed);
        _pageIndex = -1; // force the change notification even if the number is the same
        PageIndex = index;
    }

    // ---- Settings ----------------------------------------------------------------------------
    public bool MinimalNotch
    {
        get => _settings.Current.MinimalNotch;
        set
        {
            if (value == MinimalNotch) return;
            _settings.Update(s => s.MinimalNotch = value);
            OnPropertyChanged();
        }
    }

    // ---- Notification permission prompt --------------------------------------------------------
    public bool ShowNotificationPrompt =>
        _settings.Current.ShowAppNotifications &&
        _notifications.Access is NotificationAccess.Denied or NotificationAccess.NoPackageIdentity;

    public string NotificationPromptText => _notifications.Access == NotificationAccess.NoPackageIdentity
        ? "Notifications need the MSIX build"
        : "Turn on app notifications";

    // ---- Pills -------------------------------------------------------------------------------
    private PillRequest? _currentPill;
    public PillRequest? CurrentPill
    {
        get => _currentPill;
        private set { if (Set(ref _currentPill, value)) OnPropertyChanged(nameof(HasPill)); }
    }
    public bool HasPill => CurrentPill != null;

    public void EnqueuePill(PillRequest pill)
    {
        var s = _settings.Current;
        bool allowed = pill.Source switch
        {
            PillSource.App => s.ShowAppNotifications,
            PillSource.Device => s.ShowDevicePills,
            PillSource.Network or PillSource.Vpn => s.ShowNetworkPills,
            PillSource.Power => s.ShowBatteryPills,
            _ => true,
        };
        if (!allowed) return;
        if (_pillQueue.Count > 6) _pillQueue.Dequeue(); // don't build an endless backlog
        _pillQueue.Enqueue(pill);
        if (CurrentPill == null) ShowNext();
    }

    public void DismissPill()
    {
        _pillTimer.Stop();
        CurrentPill = null;
        if (_pillQueue.Count > 0)
        {
            // Small gap so the collapse/pop reads as two separate events.
            var gap = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            gap.Tick += (_, _) => { gap.Stop(); if (CurrentPill == null) ShowNext(); };
            gap.Start();
        }
    }

    /// <summary>Pause the auto-dismiss while the pointer is over the pill.</summary>
    public void HoldPill(bool hold)
    {
        if (CurrentPill == null) return;
        if (hold) _pillTimer.Stop();
        else { _pillTimer.Stop(); _pillTimer.Start(); }
    }

    private void ShowNext()
    {
        if (_pillQueue.Count == 0) return;
        CurrentPill = _pillQueue.Dequeue();
        _pillTimer.Stop();
        _pillTimer.Start();
    }
}
