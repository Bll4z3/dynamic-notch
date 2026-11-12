using DynamicNotch.Services;
using Microsoft.UI.Dispatching;

namespace DynamicNotch.ViewModels;

/// <summary>
/// Claude Usage page state and polling. Fetches once at startup, then about every
/// <see cref="AppConfig.UsagePollSeconds"/> while the page is visible, and never while it's hidden.
/// Errors and 429s back off (doubling, capped), honouring Retry-After. A transient failure keeps the
/// last good numbers on screen; a sign-in or format problem shows "Usage unavailable" with a reason.
/// </summary>
public sealed class UsageViewModel : ObservableObject
{
    private readonly DispatcherQueueTimer _timer;
    private Func<CancellationToken, Task<UsageResult>> _fetch;
    private CancellationTokenSource? _cts;
    private DateTimeOffset _nextDue = DateTimeOffset.MinValue;
    private int _failures;
    private bool _visible, _enabled = true, _busy;

    public UsageViewModel(ClaudeUsageService service)
    {
        _fetch = service.FetchAsync;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => { _timer.Stop(); _ = RefreshAsync(); };
    }

    /// <summary>Dev: replace the network call (e.g. canned JSON for `--usage-demo`).</summary>
    public void UseFetcher(Func<CancellationToken, Task<UsageResult>> fetch) => _fetch = fetch;

    private UsageSnapshot? _snapshot;
    public UsageSnapshot? Snapshot { get => _snapshot; private set => Set(ref _snapshot, value); }

    private string? _unavailable = null;
    /// <summary>Reason shown in the "Usage unavailable" state (null when numbers are shown or loading).</summary>
    public string? UnavailableReason { get => _unavailable; private set => Set(ref _unavailable, value); }

    private string? _staleNote;
    /// <summary>Set when a refresh failed but older numbers are still shown.</summary>
    public string? StaleNote { get => _staleNote; private set => Set(ref _staleNote, value); }

    public bool IsLoading => Snapshot == null && UnavailableReason == null;

    /// <summary>
    /// The page is switched on/off (called once at startup too). Off = no requests at all; on with no
    /// data yet = one fetch now (the single fetch made while the page isn't visible).
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled) { _timer.Stop(); _cts?.Cancel(); }
        else if (Snapshot == null && UnavailableReason == null) _ = RefreshAsync();
    }

    /// <summary>True while the notch is open on this page: polling runs only then.</summary>
    public bool IsVisible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            if (value) Schedule(); else _timer.Stop();
            OnPropertyChanged();
        }
    }

    private void Schedule()
    {
        _timer.Stop();
        if (!_visible || !_enabled || _busy) return;
        var wait = _nextDue - DateTimeOffset.Now;
        if (wait <= TimeSpan.Zero) { _ = RefreshAsync(); return; }
        _timer.Interval = wait;
        _timer.Start();
    }

    private async Task RefreshAsync()
    {
        if (_busy || !_enabled) return;
        _busy = true;
        _cts = new CancellationTokenSource();
        UsageResult result;
        try { result = await _fetch(_cts.Token); }
        catch (OperationCanceledException) { _busy = false; return; }
        catch (Exception ex)
        {
            App.LogError(ex); // never contains the token: the service doesn't throw with it
            result = UsageResult.Fail(UsageFailure.BadData, "Something went wrong");
        }
        finally { _cts.Dispose(); _cts = null; }
        _busy = false;
        Apply(result);
        Schedule();
    }

    private void Apply(UsageResult r)
    {
        var poll = TimeSpan.FromSeconds(AppConfig.UsagePollSeconds);
        if (r.Snapshot is { } s)
        {
            _failures = 0;
            _nextDue = DateTimeOffset.Now + poll;
            UnavailableReason = null;
            StaleNote = null;
            Snapshot = s;
            OnPropertyChanged(nameof(IsLoading));
            return;
        }

        // Back off: 2, 4, 8 … minutes (capped), never sooner than the server asked.
        _failures++;
        var backoff = TimeSpan.FromSeconds(Math.Min(AppConfig.UsageMaxBackoffSeconds, AppConfig.UsagePollSeconds * Math.Pow(2, _failures)));
        if (r.RetryAfter is { } ra && ra > backoff) backoff = ra;
        _nextDue = DateTimeOffset.Now + backoff;

        bool transient = r.Failure is UsageFailure.Offline or UsageFailure.RateLimited or UsageFailure.Server;
        if (transient && Snapshot != null)
        {
            StaleNote = r.Reason;
        }
        else
        {
            Snapshot = null;
            StaleNote = null;
            UnavailableReason = r.Reason;
        }
        OnPropertyChanged(nameof(IsLoading));
    }
}
