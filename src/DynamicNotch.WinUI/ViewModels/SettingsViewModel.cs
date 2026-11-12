using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using Microsoft.UI.Dispatching;
using DynamicNotch.Design;
using DynamicNotch.Services;

namespace DynamicNotch.ViewModels;

/// <summary>One entry in the Settings tab list. Add more tabs by adding entries with their own view model.</summary>
public sealed record SettingsTab(string Title, string Icon, object Content);

public sealed class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(SecretStore secrets, ClaudeService claude, PageRegistry pages, SettingsService settings, FocusTimerViewModel timer)
    {
        ApiKey = new ApiKeyTabViewModel(secrets, claude);
        Pages = new PagesTabViewModel(pages);
        Focus = new FocusTabViewModel(settings, timer);
        General = new GeneralTabViewModel(settings);
        Tabs = new ObservableCollection<SettingsTab>
        {
            new("Claude API key", Icons.Key, ApiKey),
            new("Pages", Icons.Shelf, Pages),
            new("Focus Timer", Icons.Timer, Focus),
            new("General", Icons.Settings, General),
        };
        _selected = Tabs[0];
    }

    public ObservableCollection<SettingsTab> Tabs { get; }
    public ApiKeyTabViewModel ApiKey { get; }
    public PagesTabViewModel Pages { get; }
    public FocusTabViewModel Focus { get; }
    public GeneralTabViewModel General { get; }

    private SettingsTab _selected;
    public SettingsTab SelectedTab
    {
        get => _selected;
        set { if (value != null && Set(ref _selected, value)) OnPropertyChanged(nameof(SelectedIndex)); }
    }
    public int SelectedIndex => Tabs.IndexOf(SelectedTab);
}

public enum KeyTestResult { None, Testing, Works, Rejected }

/// <summary>"Claude API key" tab: save / remove / paste / test. Never shows the full key once saved.</summary>
public sealed class ApiKeyTabViewModel : ObservableObject
{
    private readonly SecretStore _secrets;
    private readonly ClaudeService _claude;

    public ApiKeyTabViewModel(SecretStore secrets, ClaudeService claude)
    {
        _secrets = secrets;
        _claude = claude;
        var remove = new RelayCommand(Remove, () => _secrets.HasKey);
        RemoveCommand = remove;
        OpenKeyPageCommand = new RelayCommand(OpenKeyPage);
        _secrets.Changed += () => { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(HasKey)); remove.RaiseCanExecuteChanged(); };
    }

    public ICommand RemoveCommand { get; }
    public ICommand OpenKeyPageCommand { get; }

    public bool HasKey => _secrets.HasKey;
    public string StatusText => _secrets.Masked() is { } masked ? $"Key saved ({masked})" : "No key saved";

    private KeyTestResult _test;
    public KeyTestResult TestResult
    {
        get => _test;
        private set { if (Set(ref _test, value)) { OnPropertyChanged(nameof(TestText)); OnPropertyChanged(nameof(IsTesting)); } }
    }
    public bool IsTesting => TestResult == KeyTestResult.Testing;

    private string _testDetail = "";
    public string TestText => TestResult switch
    {
        KeyTestResult.Testing => "Testing…",
        KeyTestResult.Works => "Key works",
        KeyTestResult.Rejected => _testDetail,
        _ => "",
    };

    /// <summary>Saves the typed key (encrypted). Returns false when the box is empty.</summary>
    public bool Save(string typed)
    {
        if (string.IsNullOrWhiteSpace(typed)) return false;
        _secrets.Set(typed);
        TestResult = KeyTestResult.None;
        return true;
    }

    private void Remove()
    {
        _secrets.Remove();
        TestResult = KeyTestResult.None;
    }

    /// <summary>Tests the typed key if there is one, otherwise the saved key (max_tokens 1).</summary>
    public async Task TestAsync(string typed)
    {
        string? key = string.IsNullOrWhiteSpace(typed) ? _secrets.Get() : typed;
        if (string.IsNullOrWhiteSpace(key))
        {
            _testDetail = "Paste a key first";
            TestResult = KeyTestResult.Rejected;
            OnPropertyChanged(nameof(TestText));
            return;
        }
        TestResult = KeyTestResult.Testing;
        var error = await _claude.TestKeyAsync(key);
        _testDetail = error == null ? "" : error.Kind == ClaudeErrorKind.InvalidKey ? "Key rejected" : error.Message;
        TestResult = error == null ? KeyTestResult.Works : KeyTestResult.Rejected;
        OnPropertyChanged(nameof(TestText));
    }

    private static void OpenKeyPage()
    {
        try { Process.Start(new ProcessStartInfo(AppConfig.ApiKeyPageUrl) { UseShellExecute = true }); }
        catch { /* no default browser */ }
    }
}

/// <summary>"Pages" tab: one row per notch page with an on/off switch (reads/writes the PageRegistry).</summary>
public sealed class PagesTabViewModel : ObservableObject
{
    private readonly DispatcherQueueTimer _messageTimer;

    public PagesTabViewModel(PageRegistry registry)
    {
        Rows = PageRegistry.All.Select(p => new PageRowViewModel(p, registry, this)).ToList();
        _messageTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _messageTimer.IsRepeating = false;
        _messageTimer.Interval = TimeSpan.FromSeconds(2.5);
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); Message = ""; };
        registry.Changed += () => { foreach (var r in Rows) r.Refresh(); };
    }

    public IReadOnlyList<PageRowViewModel> Rows { get; }

    private string _message = "";
    public string Message { get => _message; private set => Set(ref _message, value); }

    internal void ShowMessage(string text)
    {
        Message = text;
        _messageTimer.Stop();
        _messageTimer.Start();
    }
}

public sealed class PageRowViewModel : ObservableObject
{
    private readonly PageRegistry _registry;
    private readonly PagesTabViewModel _owner;

    public PageRowViewModel(PageDefinition page, PageRegistry registry, PagesTabViewModel owner)
    {
        Page = page;
        _registry = registry;
        _owner = owner;
    }

    public PageDefinition Page { get; }
    public string Name => Page.Name;
    public string Description => Page.Description;
    public string Icon => Page.Icon;
    public bool UseClaudeLogo => Page.UseClaudeLogo;

    /// <summary>Bumped to make the switch shake when the last page can't be turned off.</summary>
    private int _shake;
    public int ShakeToken { get => _shake; private set => Set(ref _shake, value); }

    public bool IsEnabled
    {
        get => _registry.IsEnabled(Page.Id);
        set
        {
            if (_registry.TrySetEnabled(Page.Id, value)) return;
            // Refused (last page): keep the switch on, shake it, explain.
            ShakeToken++;
            _owner.ShowMessage("Keep at least one page on");
            DispatcherQueue.GetForCurrentThread().TryEnqueue(() => OnPropertyChanged(nameof(IsEnabled)));
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsEnabled));
}

/// <summary>
/// "Focus Timer" tab: the routine as an editable list (type, minutes, reorder, delete, add), plus the
/// auto-start and sound switches. Every valid edit is saved straight to settings.json; the timer picks
/// it up live (right away when idle, at the next phase while a round is running).
/// </summary>
public sealed class FocusTabViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly FocusTimerViewModel _timer;
    private readonly DispatcherQueueTimer _messageTimer;

    public FocusTabViewModel(SettingsService settings, FocusTimerViewModel timer)
    {
        _settings = settings;
        _timer = timer;
        Items = FocusRoutine.Sanitize(settings.Current.FocusRoutine);
        _messageTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _messageTimer.IsRepeating = false;
        _messageTimer.Interval = TimeSpan.FromSeconds(2.5);
        _messageTimer.Tick += (_, _) => { _messageTimer.Stop(); Message = ""; };
        timer.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "" or nameof(FocusTimerViewModel.RoutinePending)) OnPropertyChanged(nameof(PendingNote));
        };
    }

    /// <summary>The routine being edited (a copy; saved on every change).</summary>
    public List<FocusInterval> Items { get; private set; }

    /// <summary>Raised after the list changed (argument: index of a newly added row, or -1).</summary>
    public event Action<int>? ItemsChanged;

    public string TotalText
    {
        get
        {
            int n = FocusRoutine.FocusCount(Items);
            return $"Total: {FocusRoutine.Duration(FocusRoutine.Total(Items))} · {n} focus {(n == 1 ? "session" : "sessions")}";
        }
    }

    public string PendingNote => _timer.RoutinePending ? "The timer is running: changes apply at the next phase." : "";

    private string _message = "";
    public string Message { get => _message; private set => Set(ref _message, value); }

    public void ShowMessage(string text)
    {
        Message = text;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    public bool AutoStart
    {
        get => _settings.Current.FocusAutoStart;
        set { if (value != AutoStart) { _settings.Update(s => s.FocusAutoStart = value); OnPropertyChanged(); } }
    }

    public bool PlaySound
    {
        get => _settings.Current.FocusSound;
        set
        {
            if (value == PlaySound) return;
            _settings.Update(s => s.FocusSound = value);
            OnPropertyChanged();
            if (value) Chime.Play(); // a preview of what it sounds like
        }
    }

    public bool CanAdd => Items.Count < FocusRoutine.MaxIntervals;

    public void Add(IntervalKind kind)
    {
        if (!CanAdd) { ShowMessage($"Up to {FocusRoutine.MaxIntervals} intervals"); return; }
        Items.Add(new FocusInterval(kind, kind switch { IntervalKind.Focus => 25, IntervalKind.ShortBreak => 5, _ => 15 }));
        Save(Items.Count - 1);
    }

    /// <summary>Returns false (and changes nothing) when it's the last interval.</summary>
    public bool Remove(int index)
    {
        if (Items.Count <= 1) { ShowMessage("Keep at least one interval"); return false; }
        if (index < 0 || index >= Items.Count) return false;
        Items.RemoveAt(index);
        Save();
        return true;
    }

    public void Move(int index, int delta)
    {
        int to = index + delta;
        if (index < 0 || index >= Items.Count || to < 0 || to >= Items.Count) return;
        (Items[index], Items[to]) = (Items[to], Items[index]);
        Save();
    }

    public void CycleKind(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        Items[index].Kind = Items[index].Kind switch
        {
            IntervalKind.Focus => IntervalKind.ShortBreak,
            IntervalKind.ShortBreak => IntervalKind.LongBreak,
            _ => IntervalKind.Focus,
        };
        Save();
    }

    /// <summary>Validated minutes: null / empty / out of range is refused (returns the value kept).</summary>
    public int SetMinutes(int index, int? minutes)
    {
        if (index < 0 || index >= Items.Count) return 0;
        var item = Items[index];
        if (minutes is not { } m || m < FocusRoutine.MinMinutes || m > FocusRoutine.MaxMinutes)
        {
            ShowMessage($"Use {FocusRoutine.MinMinutes}–{FocusRoutine.MaxMinutes} minutes");
            if (minutes is { } bad && bad > 0) { item.Minutes = Math.Clamp(bad, FocusRoutine.MinMinutes, FocusRoutine.MaxMinutes); Save(); }
            return item.Minutes;
        }
        if (item.Minutes == m) return m;
        item.Minutes = m;
        Save();
        return m;
    }

    public void ResetDefaults()
    {
        Items = FocusRoutine.Defaults();
        Save();
    }

    private void Save(int added = -1)
    {
        Items = FocusRoutine.Sanitize(Items);
        var copy = Items.Select(i => i.Clone()).ToList();
        _settings.Update(s => s.FocusRoutine = copy);
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(PendingNote));
        ItemsChanged?.Invoke(added);
    }
}

/// <summary>"General" tab: app-wide behaviour switches.</summary>
public sealed class GeneralTabViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    public GeneralTabViewModel(SettingsService settings) => _settings = settings;

    public bool HideWhenFullscreen
    {
        get => _settings.Current.HideWhenFullscreen;
        set { if (value != HideWhenFullscreen) { _settings.Update(s => s.HideWhenFullscreen = value); OnPropertyChanged(); } }
    }
}
