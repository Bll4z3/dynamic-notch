using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using DynamicNotch.Design;
using DynamicNotch.Services;

namespace DynamicNotch.ViewModels;

/// <summary>One entry in the Settings tab list. Add more tabs by adding entries with their own view model.</summary>
public sealed record SettingsTab(string Title, string Icon, object Content);

public sealed class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(SecretStore secrets, ClaudeService claude, PageRegistry pages)
    {
        ApiKey = new ApiKeyTabViewModel(secrets, claude);
        Pages = new PagesTabViewModel(pages);
        Tabs = new ObservableCollection<SettingsTab>
        {
            new("Claude API key", Icons.Key, ApiKey),
            new("Pages", Icons.Shelf, Pages),
        };
        _selected = Tabs[0];
    }

    public ObservableCollection<SettingsTab> Tabs { get; }
    public ApiKeyTabViewModel ApiKey { get; }
    public PagesTabViewModel Pages { get; }

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
        RemoveCommand = new RelayCommand(Remove, () => _secrets.HasKey);
        OpenKeyPageCommand = new RelayCommand(OpenKeyPage);
        _secrets.Changed += () => { OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(HasKey)); };
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
    private readonly System.Windows.Threading.DispatcherTimer _messageTimer;

    public PagesTabViewModel(PageRegistry registry)
    {
        Rows = PageRegistry.All.Select(p => new PageRowViewModel(p, registry, this)).ToList();
        _messageTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
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
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                new Action(() => OnPropertyChanged(nameof(IsEnabled))));
        }
    }

    internal void Refresh() => OnPropertyChanged(nameof(IsEnabled));
}
