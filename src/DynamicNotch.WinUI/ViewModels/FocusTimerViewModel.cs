using DynamicNotch.Services;
using Microsoft.UI.Dispatching;

namespace DynamicNotch.ViewModels;

public enum TimerStatus { Idle, Running, Paused, Waiting, Done }

/// <summary>
/// The focus timer. Time is kept with timestamps (when the phase was last resumed + time banked before
/// that), never by counting ticks, so it stays exact while the notch is collapsed, the page is hidden,
/// the UI thread is busy or the PC sleeps. One timer is armed for the end of the phase; a 1-second
/// tick runs only while something on screen shows the seconds. If several phases elapsed unseen (sleep,
/// app closed), it catches up phase by phase from the exact end times.
///
/// Routine edits in Settings apply right away while the timer is idle or done; while a round is in
/// progress they're held and applied at the next phase change, so the current phase is never cut short.
/// State is saved to focus-timer.json on every change and restored on launch.
/// </summary>
public sealed class FocusTimerViewModel : ObservableObject
{
    private const string StateFile = "focus-timer.json";

    private readonly SettingsService _settings;
    private readonly DispatcherQueueTimer _endTimer, _tick;
    private List<FocusInterval> _round;
    private int _index;
    private TimerStatus _status = TimerStatus.Idle;
    private DateTimeOffset? _resumedAt;   // set while running
    private TimeSpan _banked;             // elapsed time of this phase before the last resume
    private bool _tickForPage, _tickForCompact;

    public FocusTimerViewModel(SettingsService settings)
    {
        _settings = settings;
        _round = FocusRoutine.Sanitize(settings.Current.FocusRoutine);
        var q = DispatcherQueue.GetForCurrentThread();
        _endTimer = q.CreateTimer();
        _endTimer.IsRepeating = false;
        _endTimer.Tick += (_, _) => { _endTimer.Stop(); CheckEnd(); };
        _tick = q.CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.Tick += (_, _) => { CheckEnd(); OnPropertyChanged(nameof(Remaining)); };
        Restore();
        settings.Changed += OnSettingsChanged;
        CheckEnd();
        Arm();
    }

    /// <summary>Raised when a phase ends (finished phase, the phase now current or null when the round is done).</summary>
    public event Action<FocusInterval, FocusInterval?>? PhaseEnded;

    public IReadOnlyList<FocusInterval> Round => _round;
    public int Index => _index;
    public FocusInterval Current => _round[Math.Clamp(_index, 0, _round.Count - 1)];
    public TimerStatus Status => _status;
    public bool IsActive => _status is TimerStatus.Running or TimerStatus.Paused;
    /// <summary>True when a routine edit is waiting for the next phase change.</summary>
    public bool RoutinePending { get; private set; }

    public TimeSpan Elapsed => _banked + (_resumedAt is { } r ? DateTimeOffset.Now - r : TimeSpan.Zero);
    public TimeSpan Remaining => _status == TimerStatus.Done ? TimeSpan.Zero : Clamp(Current.Duration - Elapsed);
    public double Progress => _status == TimerStatus.Done ? 1 : Math.Clamp(Elapsed.TotalSeconds / Math.Max(1, Current.Duration.TotalSeconds), 0, 1);
    /// <summary>When the current phase will end (running only).</summary>
    public DateTimeOffset? EndsAt => _status == TimerStatus.Running ? DateTimeOffset.Now + Remaining : null;

    /// <summary>"Focus · 2 of 4", "Short break · 2 of 4 done", "Long break", "Round complete".</summary>
    public string PhaseLabel
    {
        get
        {
            if (_status == TimerStatus.Done) return "Round complete";
            int total = FocusRoutine.FocusCount(_round);
            int done = _round.Take(_index).Count(i => i.Kind == IntervalKind.Focus);
            return Current.Kind switch
            {
                IntervalKind.Focus => $"Focus · {done + 1} of {total}",
                IntervalKind.ShortBreak => $"Short break · {done} of {total} done",
                _ => "Long break",
            };
        }
    }

    private static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    // ---- Commands ----------------------------------------------------------------------------------
    /// <summary>Start / Pause / Resume / Start next phase / Start again.</summary>
    public void Toggle()
    {
        switch (_status)
        {
            case TimerStatus.Running:
                _banked = Elapsed;
                _resumedAt = null;
                _status = TimerStatus.Paused;
                break;
            case TimerStatus.Done:
                ResetCore();
                goto default;
            default: // Idle, Paused, Waiting
                _resumedAt = DateTimeOffset.Now;
                _status = TimerStatus.Running;
                break;
        }
        Changed();
    }

    /// <summary>Back to the start of the round (idle). A held routine edit applies now.</summary>
    public void Reset()
    {
        ResetCore();
        Changed();
    }

    private void ResetCore()
    {
        _round = FocusRoutine.Sanitize(_settings.Current.FocusRoutine);
        RoutinePending = false;
        _index = 0;
        _banked = TimeSpan.Zero;
        _resumedAt = null;
        _status = TimerStatus.Idle;
    }

    /// <summary>Dev (`--focus-demo`): jump into the round, running, with <paramref name="remaining"/> left in that phase.</summary>
    public void DevJump(int index, TimeSpan remaining)
    {
        ResetCore();
        _index = Math.Clamp(index, 0, _round.Count - 1);
        _banked = Clamp(Current.Duration - remaining);
        _resumedAt = DateTimeOffset.Now;
        _status = TimerStatus.Running;
        Changed();
    }

    /// <summary>Who needs the per-second tick: the open page and/or the compact readout on the collapsed notch.</summary>
    public void SetTickDemand(bool page, bool compact)
    {
        _tickForPage = page;
        _tickForCompact = compact;
        Arm();
    }

    // ---- Timekeeping -------------------------------------------------------------------------------
    private void CheckEnd()
    {
        if (_status != TimerStatus.Running || _resumedAt == null) return;
        FocusInterval? finished = null;
        while (_status == TimerStatus.Running && _resumedAt is { } r && DateTimeOffset.Now - r + _banked >= Current.Duration)
        {
            var endAt = r + (Current.Duration - _banked); // exact end, so catching up keeps the schedule
            finished = Current;
            ApplyPendingRoutine();
            if (_index + 1 >= _round.Count)
            {
                _index = _round.Count - 1;
                _banked = Current.Duration;
                _resumedAt = null;
                _status = TimerStatus.Done;
                break;
            }
            _index++;
            _banked = TimeSpan.Zero;
            if (_settings.Current.FocusAutoStart) _resumedAt = endAt;
            else { _resumedAt = null; _status = TimerStatus.Waiting; }
        }
        if (finished == null) return;
        Changed();
        if (_settings.Current.FocusSound) Chime.Play();
        PhaseEnded?.Invoke(finished, _status == TimerStatus.Done ? null : Current);
    }

    private void ApplyPendingRoutine()
    {
        if (!RoutinePending) return;
        RoutinePending = false;
        _round = FocusRoutine.Sanitize(_settings.Current.FocusRoutine);
        // Same position in the new routine; past its end means the round is over.
        if (_index >= _round.Count) _index = _round.Count - 1;
    }

    /// <summary>Arms the end-of-phase timer and the 1 s tick (only while running and something shows seconds).</summary>
    private void Arm()
    {
        _endTimer.Stop();
        bool running = _status == TimerStatus.Running;
        if (running)
        {
            _endTimer.Interval = Remaining + TimeSpan.FromMilliseconds(30);
            _endTimer.Start();
        }
        bool tick = running && (_tickForPage || _tickForCompact);
        if (tick && !_tick.IsRunning) _tick.Start();
        else if (!tick) _tick.Stop();
    }

    private void Changed()
    {
        Arm();
        Save();
        OnPropertyChanged(""); // everything the views read
    }

    private void OnSettingsChanged()
    {
        var fresh = FocusRoutine.Sanitize(_settings.Current.FocusRoutine);
        if (SameRoutine(fresh, _round))
        {
            if (RoutinePending) { RoutinePending = false; Save(); OnPropertyChanged(nameof(RoutinePending)); } // edited back to the running routine
            return;
        }
        if (_status is TimerStatus.Idle or TimerStatus.Done)
        {
            ResetCore();
            Changed();
        }
        else if (!RoutinePending)
        {
            RoutinePending = true;
            Save();
            OnPropertyChanged(nameof(RoutinePending));
        }
    }

    private static bool SameRoutine(IReadOnlyList<FocusInterval> a, IReadOnlyList<FocusInterval> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Kind == p.Second.Kind && p.First.Minutes == p.Second.Minutes);

    // ---- Persistence (simple: a few fields, timestamps make it exact across restarts) -----------------
    private sealed class State
    {
        public List<FocusInterval>? Round { get; set; }
        public int Index { get; set; }
        public TimerStatus Status { get; set; }
        public double BankedSeconds { get; set; }
        public DateTimeOffset? ResumedAt { get; set; }
        public bool RoutinePending { get; set; }
    }

    private void Save() => SettingsService.WriteJson(StateFile, new State
    {
        Round = _round, Index = _index, Status = _status, BankedSeconds = _banked.TotalSeconds, ResumedAt = _resumedAt, RoutinePending = RoutinePending,
    });

    private void Restore()
    {
        try
        {
            var s = SettingsService.ReadJson<State>(StateFile);
            if (s?.Round is not { Count: > 0 } || !Enum.IsDefined(s.Status)) return;
            _round = FocusRoutine.Sanitize(s.Round);
            _index = Math.Clamp(s.Index, 0, _round.Count - 1);
            _status = s.Status;
            _banked = TimeSpan.FromSeconds(Math.Max(0, s.BankedSeconds));
            _resumedAt = s.Status == TimerStatus.Running ? s.ResumedAt ?? DateTimeOffset.Now : null;
            if (_resumedAt > DateTimeOffset.Now) _resumedAt = DateTimeOffset.Now; // clock went backwards
            RoutinePending = !SameRoutine(_round, FocusRoutine.Sanitize(_settings.Current.FocusRoutine));
            if ((_status is TimerStatus.Idle or TimerStatus.Done) && RoutinePending) ResetCore();
        }
        catch { ResetCore(); }
    }
}
