using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.Services.Palette;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace DynamicNotch.ViewModels;

/// <summary>
/// The command palette's brain: turns what's typed into ranked results from every enabled function
/// (PaletteFunctions), live as you type. Instant functions (maths, units, apps, commands…) answer on the
/// keystroke; slower ones (file search, definitions, first currency rates) run in the background a moment
/// later and merge in when they arrive, if the text hasn't changed. Remembers what you run for the empty-box
/// "recent" list. Ask Claude streams its answer into <see cref="AskText"/>.
/// </summary>
public sealed class CommandPaletteViewModel : ObservableObject
{
    private const int MaxResults = 8;

    /// <summary>
    /// What the providers read, copied on the UI thread before the search runs on a worker (the clipboard and
    /// screenshot collections, settings lists and recents belong to the UI thread and change under it).
    /// </summary>
    private sealed record Snap(IReadOnlyList<(string text, ClipItemViewModel item)> Clips, IReadOnlyList<ScreenshotItem> Shots,
        IReadOnlyList<PaletteRecent> Recent, IReadOnlyList<PaletteShortcut> Shortcuts, IReadOnlySet<string> Disabled)
    {
        public bool Enabled(string fn) => !Disabled.Contains(fn);
    }

    private Snap TakeSnap() => new(
        _clipboard.Items.Where(i => i.Content.Text != null).Select(i => (i.Content.Text!, i)).ToList(),
        _screenshots.Items.ToList(),
        _recent.ToList(),
        _settings.Current.Palette.Shortcuts.ToList(),
        _settings.Current.Palette.Disabled.ToHashSet());
    private const string RecentFile = "palette-recent.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly SettingsService _settings;
    private readonly ClipboardViewModel _clipboard;
    private readonly ScreenshotShelfViewModel _screenshots;
    private readonly ClaudeService _claude;
    private readonly QuickTimerViewModel _timer;
    private readonly AppIndex _apps = new();
    private readonly CurrencyRates _rates = new();
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly DispatcherQueueTimer _slow;
    private List<PaletteRecent> _recent;
    private volatile IReadOnlyList<PaletteRecent>? _recentSnapshot;
    private CancellationTokenSource? _slowCts, _askCts;
    private int _version;
    private string? _armed; // a confirm-twice system command waiting for its second Enter

    public CommandPaletteViewModel(SettingsService settings, ClipboardViewModel clipboard, ScreenshotShelfViewModel screenshots, ClaudeService claude, QuickTimerViewModel timer)
    {
        _settings = settings;
        _clipboard = clipboard;
        _screenshots = screenshots;
        _claude = claude;
        _timer = timer;
        _recent = SettingsService.ReadJson<List<PaletteRecent>>(RecentFile) ?? new();
        _recentSnapshot = _recent.ToList();
        _apps.Loaded += () => { if (_query.Trim().Length > 0) Update(); };
        _apps.RefreshIfStale(); // warm the list at start-up, so the first search already finds apps
        _slow = _ui.CreateTimer();
        _slow.IsRepeating = false;
        _slow.Interval = TimeSpan.FromMilliseconds(120);
        _slow.Tick += (_, _) => { _slow.Stop(); _ = RunSlowAsync(_version, _query, TakeSnap()); };
    }

    // ---- What the window / view listen to ----------------------------------------------------------------
    /// <summary>Open a notch feature: "quick", "notifications", "settings", "page:Music", "ask", "screenshot:{path}".</summary>
    public event Action<string>? FeatureRequested;
    /// <summary>Paste this text into the window the user was in (the palette closes first).</summary>
    public event Action<string>? PasteRequested;
    public event Action? ResultsChanged;
    public event Action? AskChanged;

    public IReadOnlyList<PaletteResult> Results { get; private set; } = Array.Empty<PaletteResult>();

    private string _query = "";
    public string Query
    {
        get => _query;
        set
        {
            value ??= "";
            if (_query == value) return;
            _query = value;
            _armed = null;
            if (IsAskMode && !value.StartsWith('?')) { CancelAsk(); AskText = ""; }
            Update();
        }
    }

    // ---- Ask Claude ---------------------------------------------------------------------------------------
    private string _askText = "";
    public string AskText { get => _askText; private set { _askText = value; AskChanged?.Invoke(); } }
    public bool IsAsking { get; private set; }
    public bool IsAskMode { get; private set; }
    public string AskQuestion { get; private set; } = "";

    /// <summary>Sends <paramref name="question"/> to Claude (with your saved key) and streams the reply into <see cref="AskText"/>.</summary>
    public async Task AskAsync(string question)
    {
        question = question.Trim().TrimStart('?').Trim();
        if (question.Length == 0 || !Enabled("ask")) return;
        CancelAsk();
        var cts = _askCts = new CancellationTokenSource();
        IsAskMode = true;
        IsAsking = true;
        AskQuestion = question;
        var sb = new StringBuilder();
        AskText = "";
        try
        {
            await _claude.StreamAsync(new[] { new ChatMessage("user", question) }, chunk =>
            {
                lock (sb) sb.Append(chunk);
                _ui.TryEnqueue(() => { if (!cts.IsCancellationRequested) { lock (sb) AskText = sb.ToString(); } });
            }, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (ClaudeException ex) { AskText = ex.Kind == ClaudeErrorKind.NoKey ? "Add your Claude API key in Settings ▸ General ▸ Claude API key to ask from here." : ex.Message; }
        catch (Exception ex) { AskText = $"Couldn't reach Claude: {ex.Message}"; }
        finally
        {
            if (ReferenceEquals(_askCts, cts)) { IsAsking = false; AskChanged?.Invoke(); }
        }
    }

    private void CancelAsk()
    {
        _askCts?.Cancel();
        _askCts = null;
        IsAsking = false;
        IsAskMode = false;
    }

    // ---- Lifecycle -------------------------------------------------------------------------------------------
    /// <summary>The palette is opening: an empty box, fresh app list and rates in the background.</summary>
    public void Opened()
    {
        CancelAsk();
        _askText = "";
        _query = "";
        _armed = null;
        _apps.RefreshIfStale();
        _ = _rates.RefreshIfStaleAsync();
        Update();
    }

    public void Closed() => CancelAsk();

    public bool Enabled(string function) => !_settings.Current.Palette.Disabled.Contains(function);

    /// <summary>Settings ▸ Command Palette ▸ Glow intensity (1 = default).</summary>
    public double GlowIntensity => Math.Clamp(_settings.Current.Palette.GlowIntensity, 0, 2);

    /// <summary>Raised when any setting changes (the view re-reads <see cref="GlowIntensity"/>).</summary>
    public event Action? SettingsChanged
    {
        add => _settings.Changed += value;
        remove => _settings.Changed -= value;
    }

    // ---- Query → results ---------------------------------------------------------------------------------------
    /// <summary>
    /// Every keystroke: the instant providers run on a worker (never the UI thread, so typing never waits on a
    /// search); the result is published only if no newer key arrived meanwhile. Slow providers (files,
    /// definitions, first currency rates) wait for a 120 ms pause in typing and are cancelled by the next key.
    /// </summary>
    private void Update()
    {
        int version = ++_version;
        _slowCts?.Cancel();
        _slow.Stop();
        string q = _query.Trim();
        var snap = TakeSnap();
        SlowPending = NeedsSlow(q, snap);
        _ = Task.Run(() => Instant(q, snap)).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) _ui.TryEnqueue(DispatcherQueuePriority.High, () => { if (version == _version) Publish(t.Result); });
            else if (t.Exception is { } ex) App.LogError(ex);
        }, TaskScheduler.Default);
        if (SlowPending) _slow.Start();
    }

    /// <summary>True while slower results (files, definitions, rates) for the current query may still be added.</summary>
    public bool SlowPending { get; private set; }

    /// <summary>The slow search for <paramref name="version"/> is over: the results are final (the view may shrink now).</summary>
    private void SlowDone(int version, bool published)
    {
        if (version != _version || !SlowPending) return;
        SlowPending = false;
        if (!published) ResultsChanged?.Invoke();
    }

    private void Publish(List<PaletteResult> list)
    {
        Results = list.OrderByDescending(r => r.IsAnswer).ThenByDescending(r => r.Score).Take(MaxResults).ToList();
        ResultsChanged?.Invoke();
    }

    private bool NeedsSlow(string q, Snap s) =>
        q.Length >= 2 && !q.StartsWith('/') && !q.StartsWith('?') &&
        (s.Enabled("files") || s.Enabled("define") && q.StartsWith("define ", StringComparison.OrdinalIgnoreCase) || s.Enabled("currency") && _rates.Matches(q, out _) && !_rates.HasRates);

    private async Task RunSlowAsync(int version, string query, Snap snap)
    {
        var cts = _slowCts = new CancellationTokenSource();
        string q = query.Trim();
        var extra = new List<PaletteResult>();
        try
        {
            if (snap.Enabled("currency") && _rates.Matches(q, out _) && !_rates.HasRates)
            {
                await _rates.RefreshIfStaleAsync();
                var again = await Task.Run(() => Instant(q, snap));
                if (version == _version) { SlowPending = false; Publish(again); } // rates arrived: the conversion can show now
                return;
            }
            if (snap.Enabled("define") && q.StartsWith("define ", StringComparison.OrdinalIgnoreCase))
                extra.AddRange(await DefineAsync(q[7..].Trim(), cts.Token));
            else if (snap.Enabled("files") && !Regex.IsMatch(q, @"^[\d\s.+\-*/^()%]+$"))
            {
                foreach (var hit in await FileSearch.SearchAsync(q, 6, cts.Token))
                {
                    var h = hit;
                    extra.Add(new PaletteResult
                    {
                        Title = h.Name, Subtitle = Path.GetDirectoryName(h.Path), Function = "files", Icon = h.IsFolder ? Icons.Folder : Icons.File,
                        Score = 40 + Fuzzy(h.Name, q) * 0.3, Hint = "Open · Ctrl+Enter folder",
                        ImageLoader = () => ShellImage.LoadAsync(h.Path, 32),
                        Run = alt => Task.FromResult(OpenPath(h.Path, alt)),
                        Recent = new PaletteRecent { Kind = h.IsFolder ? "folder" : "file", Title = h.Name, Subtitle = Path.GetDirectoryName(h.Path), Target = h.Path },
                    });
                }
            }
        }
        catch (OperationCanceledException) { return; }
        catch { }
        if (version != _version || cts.IsCancellationRequested) return;
        if (extra.Count == 0) { SlowDone(version, published: false); return; }
        var merged = await Task.Run(() => Instant(q, snap).Concat(extra).ToList());
        if (version == _version) { SlowPending = false; Publish(merged); }
    }

    /// <summary>Everything that can answer right away.</summary>
    private List<PaletteResult> Instant(string q, Snap s)
    {
        var r = new List<PaletteResult>();
        if (q.Length == 0) { AddRecent(r, s); return r; }
        if (q.StartsWith('/')) { AddCommandList(r, q[1..].Trim(), s); return r; }
        if (q.StartsWith('?'))
        {
            if (s.Enabled("ask")) r.Add(new PaletteResult
            {
                Title = q.Length > 1 ? $"Ask Claude: {q[1..].Trim()}" : "Ask Claude…", Subtitle = "Enter sends it. The answer shows here.", Function = "ask", Icon = Icons.Sparkle, Score = 1000, Hint = "Ask",
                Run = async _ => { await AskAsync(q); return PaletteOutcome.Stay; },
            });
            return r;
        }
        string lower = q.ToLowerInvariant();

        if (s.Enabled("shortcuts")) AddShortcuts(r, lower, s);
        if (s.Enabled("calc") && MathEval.TryEvaluate(q) is double v) r.Add(Answer("calc", Icons.Calculator, $"= {MathEval.Format(v)}", q, MathEval.Format(v).Replace(",", "")));
        if (s.Enabled("units") && UnitConvert.TryConvert(q) is { } u) r.Add(Answer("units", Icons.Ruler, u.text, u.detail, MathEval.Format(Math.Round(u.value, 6)).Replace(",", "")));
        if (s.Enabled("currency") && _rates.Matches(q, out var cur) && _rates.Convert(cur.amount, cur.from, cur.to) is { } c)
            r.Add(Answer("currency", Icons.Coins, $"{c.value.ToString("#,0.00", CultureInfo.InvariantCulture)} {cur.to}", $"{MathEval.Format(cur.amount)} {cur.from} · {c.age}", c.value.ToString("0.00", CultureInfo.InvariantCulture)));
        if (s.Enabled("color")) AddColour(r, q);
        if (s.Enabled("time")) AddTime(r, lower);
        if (s.Enabled("timer")) AddTimer(r, lower);
        if (s.Enabled("web")) AddWeb(r, q);
        if (s.Enabled("url")) AddUrl(r, q);
        if (s.Enabled("system")) AddSystem(r, lower);
        if (s.Enabled("notch")) AddNotch(r, lower);
        if (s.Enabled("screenshots")) AddScreenshots(r, lower, s);
        if (s.Enabled("clipboard")) AddClipboard(r, lower, s);
        if (s.Enabled("extras")) AddExtras(r, lower);
        if (s.Enabled("apps")) AddApps(r, lower);
        if (s.Enabled("define") && lower.StartsWith("define ") && lower.Length > 7)
            r.Add(new PaletteResult { Title = $"Define “{q[7..].Trim()}”", Subtitle = "Looking it up…", Function = "define", Icon = Icons.Book, Score = 5, Hint = "" });
        // Always a way out: ask Claude (Tab) or search the web.
        if (s.Enabled("ask") && q.Length >= 3) r.Add(new PaletteResult { Title = $"Ask Claude about “{q}”", Subtitle = "Tab", Function = "ask", Icon = Icons.Sparkle, Score = 2, Hint = "Tab", Run = async _ => { await AskAsync(q); return PaletteOutcome.Stay; } });
        if (s.Enabled("web") && !r.Any(x => x.Function == "web")) r.Add(Web("Google", "https://www.google.com/search?q=", q, 1));
        return r;
    }

    private static PaletteResult Answer(string fn, string icon, string title, string detail, string copy) => new()
    {
        Title = title, Subtitle = detail, Function = fn, Icon = icon, Score = 500, Hint = "Enter to copy", IsAnswer = true,
        Run = _ => Task.FromResult(Copy(copy)),
    };

    // ---- Providers ---------------------------------------------------------------------------------------
    private void AddRecent(List<PaletteResult> r, Snap s)
    {
        int i = 0;
        foreach (var e in s.Recent.OrderByDescending(e => e.Last).Take(5))
            if (FromRecent(e, s) is { } res) { res.Score = 100 - i++; r.Add(res); }
        r.Add(new PaletteResult { Title = "Browse all commands", Subtitle = "Type / to see everything the palette can do", Function = "commands", Icon = Icons.Command, Score = 0, Hint = "/", Fill = "/", Run = _ => Task.FromResult(PaletteOutcome.Fill) });
    }

    private PaletteResult? FromRecent(PaletteRecent e, Snap s)
    {
        switch (e.Kind)
        {
            case "app":
                return AppResult(new AppEntry(e.Title, e.Target), 0);
            case "file" or "folder":
                if (!File.Exists(e.Target) && !Directory.Exists(e.Target)) return null;
                return new PaletteResult { Title = e.Title, Subtitle = e.Subtitle, Function = "files", Icon = e.Kind == "folder" ? Icons.Folder : Icons.File, Hint = "Open", ImageLoader = () => ShellImage.LoadAsync(e.Target, 32), Run = alt => Task.FromResult(OpenPath(e.Target, alt)), Recent = e };
            case "url":
                return new PaletteResult { Title = e.Title, Subtitle = e.Subtitle, Function = "url", Icon = Icons.Globe, Hint = "Open", Run = _ => Task.FromResult(OpenUri(e.Target)), Recent = e };
            case "system":
                var cmd = SystemCommands.All.FirstOrDefault(s => s.Id == e.Target);
                return cmd == null ? null : SystemResult(cmd, 0);
            case "feature":
                return new PaletteResult { Title = e.Title, Subtitle = e.Subtitle, Function = "notch", Icon = Icons.Command, Hint = "Open", Run = _ => { FeatureRequested?.Invoke(e.Target); return Task.FromResult(PaletteOutcome.Close); }, Recent = e };
            case "shortcut":
                var sc = s.Shortcuts.FirstOrDefault(x => x.Keyword == e.Target);
                return sc == null ? null : ShortcutResult(sc, 0);
        }
        return null;
    }

    private void AddCommandList(List<PaletteResult> r, string filter, Snap s)
    {
        int i = 0;
        foreach (var f in PaletteFunctions.All)
        {
            if (!s.Enabled(f.Id)) continue;
            if (filter.Length > 0 && Fuzzy(f.Name, filter) <= 0 && !f.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var fn = f;
            r.Add(new PaletteResult
            {
                Title = f.Name, Subtitle = $"{f.Description}  ·  e.g. {f.Example}", Function = "commands", Icon = f.Icon, Score = 100 - i++, Hint = f.Fill != null ? "Try it" : "",
                Fill = fn.Fill, Run = _ => Task.FromResult(fn.Fill != null ? PaletteOutcome.Fill : PaletteOutcome.Stay),
            });
        }
    }

    private void AddShortcuts(List<PaletteResult> r, string q, Snap snap)
    {
        foreach (var s in snap.Shortcuts)
        {
            double score = Math.Max(s.Keyword.Equals(q, StringComparison.OrdinalIgnoreCase) ? 900 : Fuzzy(s.Keyword, q) * 2, Fuzzy(s.Name, q));
            if (score > 0) r.Add(ShortcutResult(s, score + 50));
        }
    }

    private PaletteResult ShortcutResult(PaletteShortcut s, double score) => new()
    {
        Title = string.IsNullOrWhiteSpace(s.Name) ? s.Keyword : s.Name, Subtitle = $"{s.Keyword} · {s.Target}", Function = "shortcuts", Icon = Icons.Star, Score = score, Hint = "Run",
        ImageLoader = s.Kind is ShortcutKind.App or ShortcutKind.File or ShortcutKind.Folder ? () => ShellImage.LoadAsync(s.Target, 32) : null,
        Run = _ => Task.FromResult(RunShortcut(s)),
        Recent = new PaletteRecent { Kind = "shortcut", Title = s.Name, Target = s.Keyword },
    };

    private static PaletteOutcome RunShortcut(PaletteShortcut s)
    {
        try
        {
            switch (s.Kind)
            {
                case ShortcutKind.Url:
                    return OpenUri(s.Target.Contains("://") ? s.Target : "https://" + s.Target);
                case ShortcutKind.Command:
                    Process.Start(new ProcessStartInfo("cmd.exe", "/c " + s.Target) { UseShellExecute = false, CreateNoWindow = true });
                    return PaletteOutcome.Close;
                default:
                    Process.Start(new ProcessStartInfo(s.Target) { UseShellExecute = true });
                    return PaletteOutcome.Close;
            }
        }
        catch { return PaletteOutcome.Stay; }
    }

    private void AddApps(List<PaletteResult> r, string q)
    {
        var recents = _recentSnapshot;
        foreach (var a in _apps.Apps)
        {
            double s = Fuzzy(a.Name, q);
            if (s <= 0 || s < 55 && q.Length < 5) continue; // short queries: no loose letters-in-order matches
            var recent = recents?.FirstOrDefault(e => e.Kind == "app" && e.Target == a.Id);
            // Strong matches (start of the name, a word, initials) outrank files; loose letters-in-order ones don't.
            r.Add(AppResult(a, s + (s >= 55 ? 60 : 0) + (recent?.Count ?? 0) * 4));
        }
    }

    private PaletteResult AppResult(AppEntry a, double score) => new()
    {
        Title = a.Name, Subtitle = "App", Function = "apps", Icon = Icons.AppWindow, Score = score, Hint = "Open",
        ImageLoader = () => _apps.IconAsync(a.Id),
        Run = _ => { AppIndex.Launch(a.Id); return Task.FromResult(PaletteOutcome.Close); },
        Recent = new PaletteRecent { Kind = "app", Title = a.Name, Subtitle = "App", Target = a.Id },
    };

    private static readonly (string prefix, string name, string url)[] Engines =
    {
        ("g", "Google", "https://www.google.com/search?q="), ("ddg", "DuckDuckGo", "https://duckduckgo.com/?q="),
        ("yt", "YouTube", "https://www.youtube.com/results?search_query="), ("w", "Wikipedia", "https://en.wikipedia.org/w/index.php?search="),
        ("wiki", "Wikipedia", "https://en.wikipedia.org/w/index.php?search="), ("gh", "GitHub", "https://github.com/search?q="),
        ("r", "Reddit", "https://www.reddit.com/search/?q="), ("maps", "Google Maps", "https://www.google.com/maps/search/"),
        ("img", "Google Images", "https://www.google.com/search?tbm=isch&q="),
    };

    private void AddWeb(List<PaletteResult> r, string q)
    {
        int space = q.IndexOf(' ');
        if (space <= 0) return;
        string prefix = q[..space].ToLowerInvariant(), term = q[(space + 1)..].Trim();
        var e = Engines.FirstOrDefault(x => x.prefix == prefix);
        if (e.name == null) return;
        r.Add(Web(e.name, e.url, term, 950));
    }

    private static PaletteResult Web(string engine, string baseUrl, string term, double score) => new()
    {
        Title = term.Length > 0 ? $"Search {engine} for “{term}”" : $"Search {engine}…", Subtitle = "Opens in your browser", Function = "web", Icon = Icons.Globe, Score = score, Hint = "Search",
        Run = _ => Task.FromResult(term.Length == 0 ? PaletteOutcome.Stay : OpenUri(baseUrl + Uri.EscapeDataString(term))),
    };

    private static readonly Regex UrlLike = new(@"^(https?://)?([\w-]+\.)+[a-z]{2,}(:\d+)?(/\S*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void AddUrl(List<PaletteResult> r, string q)
    {
        if (q.Contains(' ') || !UrlLike.IsMatch(q)) return;
        string url = q.Contains("://") ? q : "https://" + q;
        r.Add(new PaletteResult
        {
            Title = $"Open {q}", Subtitle = url, Function = "url", Icon = Icons.Globe, Score = 920, Hint = "Open",
            Run = _ => Task.FromResult(OpenUri(url)),
            Recent = new PaletteRecent { Kind = "url", Title = q, Subtitle = url, Target = url },
        });
    }

    private void AddSystem(List<PaletteResult> r, string q)
    {
        foreach (var cmd in SystemCommands.All)
        {
            double s = cmd.Keywords.Max(k => k.Equals(q, StringComparison.OrdinalIgnoreCase) ? 800 : Fuzzy(k, q));
            s = Math.Max(s, Fuzzy(cmd.Name, q));
            if (s > 0) r.Add(SystemResult(cmd, s + 40));
        }
    }

    private PaletteResult SystemResult(SystemCommand cmd, double score)
    {
        var res = new PaletteResult
        {
            Title = cmd.Name, Subtitle = _armed == cmd.Id ? "Press Enter again to confirm" : cmd.Description, Function = "system", Icon = Icons.Power, Score = score,
            Hint = cmd.Confirm ? (_armed == cmd.Id ? "Confirm" : "Enter twice") : "Run",
            Recent = cmd.Confirm ? null : new PaletteRecent { Kind = "system", Title = cmd.Name, Subtitle = cmd.Description, Target = cmd.Id },
            Run = _ =>
            {
                if (cmd.Confirm && _armed != cmd.Id) { _armed = cmd.Id; Publish(Instant(_query.Trim(), TakeSnap())); return Task.FromResult(PaletteOutcome.Stay); }
                _armed = null;
                cmd.Run();
                return Task.FromResult(PaletteOutcome.Close);
            },
        };
        return res;
    }

    private static readonly (string[] words, string title, string target)[] Features =
    {
        (new[] { "quick actions", "quick", "controls", "toggles" }, "Quick actions", "quick"),
        (new[] { "notifications", "notification history", "notifs" }, "Notifications", "notifications"),
        (new[] { "settings", "dynamic notch settings", "preferences" }, "Dynamic Notch settings", "settings"),
        (new[] { "ask claude", "claude", "chat" }, "Ask Claude", "ask"),
        (new[] { "music", "now playing", "player" }, "Music", "page:Music"),
        (new[] { "clipboard", "shelf" }, "Clipboard + Shelf", "page:Clipboard"),
        (new[] { "focus", "pomodoro", "focus timer" }, "Focus timer", "page:Focus"),
        (new[] { "usage", "claude usage", "limits" }, "Claude usage", "page:Usage"),
        (new[] { "screenshots", "screenshot shelf" }, "Screenshot shelf", "page:Screenshots"),
    };

    private void AddNotch(List<PaletteResult> r, string q)
    {
        foreach (var (words, title, target) in Features)
        {
            double s = words.Max(w => Fuzzy(w, q));
            if (s <= 0) continue;
            var t = target;
            r.Add(new PaletteResult
            {
                Title = title, Subtitle = "Dynamic Notch", Function = "notch", Icon = Icons.Command, Score = s + 45, Hint = "Open",
                Run = _ => { FeatureRequested?.Invoke(t); return Task.FromResult(PaletteOutcome.Close); },
                Recent = new PaletteRecent { Kind = "feature", Title = title, Subtitle = "Dynamic Notch", Target = t },
            });
        }
    }

    private void AddScreenshots(List<PaletteResult> r, string q, Snap snap)
    {
        bool all = q is "ss" or "screenshot" or "screenshots" || q.StartsWith("ss ");
        string term = q.StartsWith("ss ") ? q[3..] : q;
        foreach (var item in snap.Shots)
        {
            string name = Path.GetFileNameWithoutExtension(item.Path);
            double s = all && term is "ss" or "screenshot" or "screenshots" or "" ? 300 : Fuzzy(name, term);
            if (s <= 0) continue;
            var it = item;
            r.Add(new PaletteResult
            {
                Title = name, Subtitle = $"Screenshot · {it.Time:d MMM HH:mm}", Function = "screenshots", Icon = Icons.Screenshot, Score = s + 30, Hint = "Show · Ctrl+Enter copy",
                Image = it.Thumbnail,
                Run = async alt =>
                {
                    if (alt) { await _screenshots.CopyAsync(it); return PaletteOutcome.Copied; }
                    FeatureRequested?.Invoke("screenshot:" + it.Path);
                    return PaletteOutcome.Close;
                },
            });
        }
    }

    private void AddClipboard(List<PaletteResult> r, string q, Snap snap)
    {
        bool all = q == "clip" || q == "clipboard" || q.StartsWith("clip ");
        string term = q.StartsWith("clip ") ? q[5..].Trim() : q;
        if (!all && term.Length < 3) return;
        foreach (var (text, item) in snap.Clips)
        {
            double s = all && term.Length == 0 ? 200 : text.Contains(term, StringComparison.OrdinalIgnoreCase) ? 35 + (all ? 200 : 0) : 0;
            if (s <= 0) continue;
            string one = Regex.Replace(text.Trim(), @"\s+", " ");
            var it = item;
            r.Add(new PaletteResult
            {
                Title = one.Length > 80 ? one[..80] + "…" : one, Subtitle = "From your clipboard history", Function = "clipboard", Icon = Icons.Clipboard, Score = s, Hint = "Paste · Ctrl+Enter copy",
                Run = alt =>
                {
                    it.CopyCommand.Execute(null);
                    if (alt) return Task.FromResult(PaletteOutcome.Copied);
                    PasteRequested?.Invoke(text);
                    return Task.FromResult(PaletteOutcome.Close);
                },
            });
        }
    }

    private void AddTimer(List<PaletteResult> r, string q)
    {
        if (_timer.IsActive && (q.StartsWith("timer") || q.StartsWith("stopwatch") || q.StartsWith("stop")))
            r.Add(new PaletteResult
            {
                Title = _timer.IsCountdown ? $"Stop timer · {_timer.Display} left" : $"Stop stopwatch · {_timer.Display}", Subtitle = "It's showing on the notch", Function = "timer", Icon = Icons.Timer, Score = 880, Hint = "Stop",
                Run = _ => { _timer.Stop(); return Task.FromResult(PaletteOutcome.Close); },
            });
        if (q.StartsWith("stopwatch"))
        {
            r.Add(new PaletteResult { Title = "Start a stopwatch", Subtitle = "Counts up on the notch", Function = "timer", Icon = Icons.Timer, Score = 860, Hint = "Start", Run = _ => { _timer.StartStopwatch(); return Task.FromResult(PaletteOutcome.Close); } });
            return;
        }
        if (!q.StartsWith("timer") || q.StartsWith("timer stop")) return;
        var d = QuickTimerViewModel.ParseDuration(q.Length > 5 ? q[5..] : "");
        r.Add(new PaletteResult
        {
            Title = d is { } dur ? $"Start a {Pretty(dur)} timer" : "Timer: type how long (5m, 90s, 1h 30m)", Subtitle = d != null ? "Counts down on the notch; a pill tells you when it's done" : "e.g. timer 25m",
            Function = "timer", Icon = Icons.Timer, Score = 870, Hint = d != null ? "Start" : "",
            Run = _ => { if (d is not { } x) return Task.FromResult(PaletteOutcome.Stay); _timer.StartCountdown(x); return Task.FromResult(PaletteOutcome.Close); },
        });
    }

    private static string Pretty(TimeSpan t)
    {
        var parts = new List<string>();
        if (t.Hours > 0 || t.Days > 0) parts.Add($"{(int)t.TotalHours} h");
        if (t.Minutes > 0) parts.Add($"{t.Minutes} min");
        if (t.Seconds > 0) parts.Add($"{t.Seconds} s");
        return string.Join(" ", parts);
    }

    private static readonly Regex Hex = new(@"^#?([0-9a-f]{3}|[0-9a-f]{6}|[0-9a-f]{8})$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Rgb = new(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void AddColour(List<PaletteResult> r, string q)
    {
        Color? c = null;
        var m = Hex.Match(q);
        if (m.Success && (q.StartsWith('#') || m.Groups[1].Value.Length == 6 && m.Groups[1].Value.Any(ch => char.IsLetter(ch))))
        {
            string h = m.Groups[1].Value;
            if (h.Length == 3) h = string.Concat(h.Select(ch => $"{ch}{ch}"));
            uint v = uint.Parse(h, NumberStyles.HexNumber);
            c = h.Length == 8 ? Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v) : Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
        else if (Rgb.Match(q) is { Success: true } rm)
            c = Color.FromArgb(255, (byte)Math.Min(255, int.Parse(rm.Groups[1].Value)), (byte)Math.Min(255, int.Parse(rm.Groups[2].Value)), (byte)Math.Min(255, int.Parse(rm.Groups[3].Value)));
        if (c is not { } col) return;
        string hex = $"#{col.R:X2}{col.G:X2}{col.B:X2}";
        string rgb = $"rgb({col.R}, {col.G}, {col.B})";
        var (hh, ss, ll) = Hsl(col);
        string hsl = $"hsl({hh:0}, {ss:0}%, {ll:0}%)";
        r.Add(new PaletteResult { Title = hex, Subtitle = $"{rgb} · {hsl}", Function = "color", Swatch = col, Score = 600, IsAnswer = true, Hint = "Enter to copy", Run = _ => Task.FromResult(Copy(hex)) });
        r.Add(new PaletteResult { Title = rgb, Subtitle = "Copy as rgb()", Function = "color", Swatch = col, Score = 590, Hint = "Copy", Run = _ => Task.FromResult(Copy(rgb)) });
        r.Add(new PaletteResult { Title = hsl, Subtitle = "Copy as hsl()", Function = "color", Swatch = col, Score = 580, Hint = "Copy", Run = _ => Task.FromResult(Copy(hsl)) });
    }

    private static (double h, double s, double l) Hsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, h = 0, s = 0;
        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h *= 60;
        }
        return (h, s * 100, l * 100);
    }

    private static readonly Dictionary<string, string> Cities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["london"] = "GMT Standard Time", ["uk"] = "GMT Standard Time", ["dublin"] = "GMT Standard Time", ["lisbon"] = "GMT Standard Time",
        ["paris"] = "Romance Standard Time", ["madrid"] = "Romance Standard Time", ["brussels"] = "Romance Standard Time",
        ["berlin"] = "W. Europe Standard Time", ["rome"] = "W. Europe Standard Time", ["amsterdam"] = "W. Europe Standard Time", ["zurich"] = "W. Europe Standard Time", ["stockholm"] = "W. Europe Standard Time",
        ["athens"] = "GTB Standard Time", ["istanbul"] = "Turkey Standard Time", ["cairo"] = "Egypt Standard Time", ["johannesburg"] = "South Africa Standard Time",
        ["moscow"] = "Russian Standard Time", ["dubai"] = "Arabian Standard Time", ["karachi"] = "Pakistan Standard Time", ["lahore"] = "Pakistan Standard Time", ["islamabad"] = "Pakistan Standard Time",
        ["delhi"] = "India Standard Time", ["mumbai"] = "India Standard Time", ["bangalore"] = "India Standard Time", ["india"] = "India Standard Time",
        ["dhaka"] = "Bangladesh Standard Time", ["bangkok"] = "SE Asia Standard Time", ["jakarta"] = "SE Asia Standard Time", ["singapore"] = "Singapore Standard Time",
        ["hong kong"] = "China Standard Time", ["beijing"] = "China Standard Time", ["shanghai"] = "China Standard Time", ["china"] = "China Standard Time",
        ["tokyo"] = "Tokyo Standard Time", ["japan"] = "Tokyo Standard Time", ["seoul"] = "Korea Standard Time", ["sydney"] = "AUS Eastern Standard Time", ["melbourne"] = "AUS Eastern Standard Time",
        ["perth"] = "W. Australia Standard Time", ["auckland"] = "New Zealand Standard Time", ["honolulu"] = "Hawaiian Standard Time", ["anchorage"] = "Alaskan Standard Time",
        ["los angeles"] = "Pacific Standard Time", ["la"] = "Pacific Standard Time", ["san francisco"] = "Pacific Standard Time", ["seattle"] = "Pacific Standard Time", ["vancouver"] = "Pacific Standard Time",
        ["denver"] = "Mountain Standard Time", ["phoenix"] = "US Mountain Standard Time", ["chicago"] = "Central Standard Time", ["dallas"] = "Central Standard Time", ["houston"] = "Central Standard Time", ["mexico city"] = "Central Standard Time (Mexico)",
        ["new york"] = "Eastern Standard Time", ["nyc"] = "Eastern Standard Time", ["toronto"] = "Eastern Standard Time", ["miami"] = "Eastern Standard Time", ["boston"] = "Eastern Standard Time", ["washington"] = "Eastern Standard Time",
        ["sao paulo"] = "E. South America Standard Time", ["rio"] = "E. South America Standard Time", ["buenos aires"] = "Argentina Standard Time", ["lagos"] = "W. Central Africa Standard Time", ["nairobi"] = "E. Africa Standard Time",
        ["riyadh"] = "Arab Standard Time", ["tehran"] = "Iran Standard Time", ["kabul"] = "Afghanistan Standard Time", ["kathmandu"] = "Nepal Standard Time",
    };

    private static void AddTime(List<PaletteResult> r, string q)
    {
        var tm = Regex.Match(q, @"^(?:time|what time is it|what's the time)\s+(?:in|at)\s+(.+)$");
        if (tm.Success && Cities.TryGetValue(tm.Groups[1].Value.Trim(), out var tzId))
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
                var t = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, tz);
                double diff = (t.Offset - DateTimeOffset.Now.Offset).TotalHours;
                string city = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(tm.Groups[1].Value.Trim());
                string rel = diff == 0 ? "same time as you" : $"{(diff > 0 ? "+" : "−")}{Math.Abs(diff):0.#} h from you";
                r.Add(Answer("time", Icons.Clock, t.ToString("HH:mm"), $"{city} · {t:dddd d MMM} · {rel}", t.ToString("HH:mm")));
            }
            catch { }
        }
        var dm = Regex.Match(q, @"^(?:days?\s+(?:until|till|to)|how long until)\s+(.+)$");
        if (dm.Success && TryDate(dm.Groups[1].Value.Trim(), out var date))
        {
            int days = (int)Math.Ceiling((date - DateTime.Today).TotalDays);
            r.Add(Answer("time", Icons.Calendar, days == 0 ? "Today" : $"{days} day{(days == 1 ? "" : "s")}", $"until {date:dddd d MMMM yyyy}", days.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static bool TryDate(string text, out DateTime date)
    {
        var special = new Dictionary<string, (int m, int d)> { ["christmas"] = (12, 25), ["new year"] = (1, 1), ["new years"] = (1, 1), ["halloween"] = (10, 31), ["valentines"] = (2, 14) };
        if (special.TryGetValue(text.ToLowerInvariant(), out var md)) { date = new DateTime(DateTime.Today.Year, md.m, md.d); if (date < DateTime.Today) date = date.AddYears(1); return true; }
        foreach (var culture in new[] { CultureInfo.CurrentCulture, new CultureInfo("en-GB"), CultureInfo.InvariantCulture })
            if (DateTime.TryParse(text, culture, DateTimeStyles.AllowWhiteSpaces, out date))
            {
                if (!Regex.IsMatch(text, @"\d{4}") && date < DateTime.Today) date = date.AddYears(1);
                return true;
            }
        date = default;
        return false;
    }

    private static void AddExtras(List<PaletteResult> r, string q)
    {
        if (q is "uuid" or "guid")
        {
            string g = Guid.NewGuid().ToString();
            r.Add(Answer("extras", Icons.Hash, g, "A new random UUID", g));
        }
        var pw = Regex.Match(q, @"^(?:password|pass|pw)(?:\s+(\d{1,3}))?$");
        if (pw.Success)
        {
            int len = Math.Clamp(pw.Groups[1].Success ? int.Parse(pw.Groups[1].Value) : 16, 6, 128);
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%^&*-_=+";
            string p = new(Enumerable.Range(0, len).Select(_ => chars[System.Security.Cryptography.RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
            r.Add(Answer("extras", Icons.Lock, p, $"A random {len}-character password (made on this PC)", p));
        }
        if (q is "ip" or "my ip" or "ip address")
        {
            try
            {
                var ips = System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()).Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(a => a.ToString()).ToList();
                if (ips.Count > 0) r.Add(Answer("extras", Icons.Wifi, ips[0], ips.Count > 1 ? "Local IP · also " + string.Join(", ", ips.Skip(1)) : "Your local IP address", ips[0]));
            }
            catch { }
        }
        if (q is "flip" or "coin" or "flip a coin" or "heads or tails")
        {
            string side = System.Security.Cryptography.RandomNumberGenerator.GetInt32(2) == 0 ? "Heads" : "Tails";
            r.Add(Answer("extras", Icons.Dice, side, "Flipped a coin", side));
        }
        var roll = Regex.Match(q, @"^(?:roll|dice)(?:\s+d?(\d{1,4}))?$");
        if (roll.Success)
        {
            int sides = Math.Max(2, roll.Groups[1].Success ? int.Parse(roll.Groups[1].Value) : 6);
            int n = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, sides + 1);
            r.Add(Answer("extras", Icons.Dice, n.ToString(CultureInfo.InvariantCulture), $"Rolled a d{sides}", n.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static async Task<List<PaletteResult>> DefineAsync(string word, CancellationToken ct)
    {
        var list = new List<PaletteResult>();
        word = word.Trim();
        if (word.Length < 2 || !word.All(c => char.IsLetter(c) || c is '-' or '\'' or ' ')) return list;
        try
        {
            string json = await Http.GetStringAsync("https://api.dictionaryapi.dev/api/v2/entries/en/" + Uri.EscapeDataString(word), ct);
            using var doc = JsonDocument.Parse(json);
            int n = 0;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                foreach (var meaning in entry.GetProperty("meanings").EnumerateArray())
                {
                    string pos = meaning.TryGetProperty("partOfSpeech", out var p) ? p.GetString() ?? "" : "";
                    foreach (var def in meaning.GetProperty("definitions").EnumerateArray())
                    {
                        string text = def.GetProperty("definition").GetString() ?? "";
                        if (text.Length == 0) continue;
                        list.Add(new PaletteResult
                        {
                            Title = text, Subtitle = $"{word} · {pos}", Function = "define", Icon = Icons.Book, Score = 700 - n, IsAnswer = n == 0, Hint = "Enter to copy",
                            Run = _ => Task.FromResult(Copy(text)),
                        });
                        if (++n >= 3) return list;
                        break; // one definition per part of speech first
                    }
                }
            }
        }
        catch (HttpRequestException) { list.Add(new PaletteResult { Title = $"No definition found for “{word}”", Subtitle = "Check the spelling, or you may be offline", Function = "define", Icon = Icons.Book, Score = 700, Hint = "" }); }
        return list;
    }

    // ---- Running -----------------------------------------------------------------------------------------
    /// <summary>Runs a result (Enter / click; <paramref name="alt"/> = Ctrl+Enter) and remembers it.</summary>
    public async Task<PaletteOutcome> RunAsync(PaletteResult r, bool alt)
    {
        if (r.Run == null) return PaletteOutcome.Stay;
        var outcome = await r.Run(alt);
        if (outcome is PaletteOutcome.Close && r.Recent is { } rec) Remember(rec);
        return outcome;
    }

    private void Remember(PaletteRecent rec)
    {
        var existing = _recent.FirstOrDefault(e => e.Kind == rec.Kind && e.Target == rec.Target);
        if (existing == null) { existing = new PaletteRecent { Kind = rec.Kind, Title = rec.Title, Subtitle = rec.Subtitle, Target = rec.Target }; _recent.Add(existing); }
        existing.Count++;
        existing.Last = DateTime.Now;
        _recent = _recent.OrderByDescending(e => e.Last).Take(30).ToList();
        _recentSnapshot = _recent.ToList();
        SettingsService.WriteJson(RecentFile, _recent);
    }

    public static PaletteOutcome Copy(string text)
    {
        try
        {
            var p = new DataPackage();
            p.SetText(text);
            Clipboard.SetContent(p);
            Clipboard.Flush();
            return PaletteOutcome.Copied;
        }
        catch { return PaletteOutcome.Stay; }
    }

    private static PaletteOutcome OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); return PaletteOutcome.Close; }
        catch { return PaletteOutcome.Stay; }
    }

    private static PaletteOutcome OpenPath(string path, bool folder)
    {
        try
        {
            if (folder) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return PaletteOutcome.Close;
        }
        catch { return PaletteOutcome.Stay; }
    }

    /// <summary>
    /// How well <paramref name="text"/> matches <paramref name="q"/> (0 = not at all): whole-text start &gt; a word
    /// start &gt; anywhere &gt; initials ("vsc" → Visual Studio Code) &gt; letters in order.
    /// </summary>
    public static double Fuzzy(string text, string q)
    {
        if (q.Length == 0 || text.Length == 0) return 0;
        if (text.Equals(q, StringComparison.OrdinalIgnoreCase)) return 120;
        if (text.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 100 - Math.Min(20, text.Length - q.Length) * 0.5;
        int idx = text.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (idx > 0 && !char.IsLetterOrDigit(text[idx - 1])) return 80;
        if (idx > 0) return q.Length >= 3 ? 55 : 0;
        string initials = new(text.Split(new[] { ' ', '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToLowerInvariant(w[0])).ToArray());
        if (q.Length >= 2 && initials.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 70;
        if (q.Length < 3) return 0;
        int ti = 0;
        foreach (char c in q)
        {
            ti = text.IndexOf(c.ToString(), ti, StringComparison.OrdinalIgnoreCase);
            if (ti < 0) return 0;
            ti++;
        }
        return 20;
    }
}
