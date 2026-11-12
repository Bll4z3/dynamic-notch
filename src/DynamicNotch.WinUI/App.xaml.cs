using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DynamicNotch;

/// <summary>Composition root: creates services, view models, the notch window and the tray icon.</summary>
public partial class App : Application
{
    private readonly string[] _args;
    private readonly List<IDisposable> _disposables = new();
    private TrayService? _tray;
    private NotchWindow? _window;
    private DispatcherQueueTimer? _devCycle; // held: an unreferenced timer is collected and stops
    private DispatcherQueueTimer? _devOpen;
    private DispatcherQueueTimer? _devPreview;
    private DispatcherQueueTimer? _devAsk;
    private DispatcherQueueTimer? _devToggle;

    public App(string[] args)
    {
        _args = args;
        InitializeComponent();

        // A UI glitch should never take the notch down: log it and keep running.
        UnhandledException += (_, e) =>
        {
            LogError(e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { LogError(e.Exception); e.SetObserved(); };
    }

    public static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(SettingsService.DataFolder);
            File.AppendAllText(Path.Combine(SettingsService.DataFolder, "error.log"), $"[{DateTime.Now:u}] [WinUI] {ex}\n\n");
        }
        catch { }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs launchArgs)
    {
        Ui.Initialize();
        Theme.Initialize();
        Styles.Register(Resources);
        Anim.RefreshSystemSetting();

        var settings = new SettingsService();
        settings.Load();

        var media = Track(new MediaService());
        var clipboard = Track(new ClipboardService());
        var shelf = new ShelfService();
        shelf.Load();
        var network = Track(new NetworkService());
        var vpn = Track(new VpnService());
        var bluetooth = Track(new BluetoothService());
        var power = Track(new PowerService(() => settings.Current.LowBatteryPercent));
        var notifications = Track(new NotificationService());
        var battery = Track(new BatteryService());
        var clock = Track(new ClockService());
        var pages = new PageRegistry(settings);
        var secrets = new SecretStore();
        var claude = new ClaudeService(secrets);
        var focus = new FocusTimerViewModel(settings);
        var settingsVm = new SettingsViewModel(secrets, claude, pages, settings, focus);

        var vm = new MainViewModel(settings,
            new MusicViewModel(media),
            new ClipboardViewModel(clipboard),
            new ShelfViewModel(shelf),
            new AskViewModel(claude, secrets),
            new StatusViewModel(clock, battery),
            new DevicesViewModel(bluetooth),
            pages,
            notifications,
            new UsageViewModel(new ClaudeUsageService()),
            focus);

        foreach (var src in new Action<Action<PillRequest>>[]
                 {
                     h => network.Pill += h, h => vpn.Pill += h, h => bluetooth.Pill += h,
                     h => power.Pill += h, h => notifications.Pill += h,
                 })
            src(vm.EnqueuePill);

        _window = new NotchWindow(vm);
        _window.ShowNotch();
        // Hide the notch while another app is fullscreen (Settings ▸ General).
        var fullscreen = Track(new FullscreenService(() => _window?.Handle ?? IntPtr.Zero));
        fullscreen.Changed += full => _window?.SetFullscreenHidden(full);
        fullscreen.Enabled = settings.Current.HideWhenFullscreen;
        settings.Changed += () => fullscreen.Enabled = settings.Current.HideWhenFullscreen;
        // Pages switched off in Settings stop their listeners; switching them back on restarts them.
        clipboard.SetListening(pages.IsEnabled(PageId.Clipboard));
        pages.Changed += () =>
        {
            clipboard.SetListening(pages.IsEnabled(PageId.Clipboard));
            if (pages.IsEnabled(PageId.Music)) _ = media.StartAsync(); else media.Stop();
            vm.Usage.SetEnabled(pages.IsEnabled(PageId.Usage));
            if (!pages.IsEnabled(PageId.Focus) && vm.Focus.Status != TimerStatus.Idle) vm.Focus.Reset(); // page off = timer off
        };
        // Dev: `--usage-demo [error]` feeds the usage page canned JSON through the real parser (no request, no token read).
        int usageDemo = Array.FindIndex(_args, a => a.Equals("--usage-demo", StringComparison.OrdinalIgnoreCase));
        if (usageDemo >= 0)
        {
            bool fail = usageDemo + 1 < _args.Length && _args[usageDemo + 1].Equals("error", StringComparison.OrdinalIgnoreCase);
            vm.Usage.UseFetcher(_ => Task.FromResult(fail
                ? UsageResult.Fail(UsageFailure.NotSignedIn, "Claude Code isn't signed in on this PC")
                : ClaudeUsageService.Parse(DemoUsageJson(), DateTimeOffset.Now)));
        }
        vm.Usage.SetEnabled(pages.IsEnabled(PageId.Usage)); // the one start-up fetch (then only while visible)
        // Dev: `--focus-demo [end]` starts the timer in the second focus session (or 6 s before the first one ends).
        int focusDemo = Array.FindIndex(_args, a => a.Equals("--focus-demo", StringComparison.OrdinalIgnoreCase));
        if (focusDemo >= 0)
        {
            bool end = focusDemo + 1 < _args.Length && _args[focusDemo + 1].Equals("end", StringComparison.OrdinalIgnoreCase);
            vm.Focus.DevJump(end ? 0 : 2, end ? TimeSpan.FromSeconds(6) : TimeSpan.FromSeconds(18 * 60 + 42));
        }

        // Global ← / → / Space / Esc while the notch is open or hovered, without ever taking focus.
        var keys = Track(new KeyboardHookService(DispatcherQueue.GetForCurrentThread()));
        keys.Start();
        _window.AttachKeyboard(keys);
        // Settings (single window); the hook never intercepts keys while it is in front.
        vm.OpenSettingsRequested += () => Views.SettingsWindow.ShowSingle(settingsVm);
        Views.SettingsWindow.HandleChanged += h => keys.PassThroughWindow = h;

        // Tray
        _tray = new TrayService();
        _tray.SetMinimal(settings.Current.MinimalNotch);
        _tray.MinimalToggled += on => vm.MinimalNotch = on;
        _tray.StartupToggled += async on =>
        {
            bool result = await StartupService.SetEnabledAsync(on);
            settings.Update(s => s.StartWithWindows = result);
            _tray.SetStartup(result);
        };
        _tray.NotificationAccessRequested += NotificationService.OpenPermissionSettings;
        _tray.QuitRequested += Quit;
        _tray.SettingsAvailable = true;
        _tray.SettingsRequested += () => Views.SettingsWindow.ShowSingle(settingsVm);
        notifications.AccessChanged += a => Ui.Post(() => _tray?.SetNotificationAccess(a));

        // Start everything (all event-driven).
        clock.Start();
        battery.Start();
        network.Start();
        vpn.Start();
        bluetooth.Start();
        power.Start();
        if (pages.IsEnabled(PageId.Music)) await media.StartAsync();
        _tray.SetStartup(await StartupService.IsEnabledAsync());
        await notifications.StartAsync();

        int open = Array.FindIndex(_args, a => a.Equals("--open", StringComparison.OrdinalIgnoreCase));
        if (open >= 0)
        {
            int page = open + 1 < _args.Length && int.TryParse(_args[open + 1], out var pg) ? pg : 0;
            int delay = open + 2 < _args.Length && int.TryParse(_args[open + 2], out var dl) ? dl : 0;
            if (delay <= 0) _window.PinOpen(page);
            else
            {
                var t = _devOpen = DispatcherQueue.GetForCurrentThread().CreateTimer();
                t.IsRepeating = false;
                t.Interval = TimeSpan.FromMilliseconds(delay);
                t.Tick += (_, _) => _window?.PinOpen(page);
                t.Start();
            }
        }
        if (_args.Contains("--nav-icons", StringComparer.OrdinalIgnoreCase)) _window.ShowNavigatorIcons();
        // Dev: `--preview` opens the clipboard preview card for the first tile (after the demo items arrive).
        if (_args.Contains("--preview", StringComparer.OrdinalIgnoreCase))
        {
            var t = _devPreview = DispatcherQueue.GetForCurrentThread().CreateTimer();
            t.IsRepeating = false;
            t.Interval = TimeSpan.FromMilliseconds(2500);
            t.Tick += (_, _) => _window?.ShowFirstPreview();
            t.Start();
        }
        // Dev: `--settings [pages]` opens the Settings window (on the Pages tab with `pages`).
        int settingsArg = Array.FindIndex(_args, a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase));
        if (settingsArg >= 0)
        {
            string tab = settingsArg + 1 < _args.Length ? _args[settingsArg + 1].ToLowerInvariant() : "";
            int tabIndex = tab switch { "pages" => 1, "focus" => 2, "general" => 3, _ => 0 };
            settingsVm.SelectedTab = settingsVm.Tabs[tabIndex];
            Views.SettingsWindow.ShowSingle(settingsVm);
        }
        // Dev: `--ask-demo [error|thinking] [focus|select]` opens Ask (pinned; with `focus`, as the hotkey does) and plays a local, canned
        // conversation: no request is made and the saved key is never read.
        int askDemo = Array.FindIndex(_args, a => a.Equals("--ask-demo", StringComparison.OrdinalIgnoreCase));
        if (askDemo >= 0)
        {
            string mode = askDemo + 1 < _args.Length ? _args[askDemo + 1].ToLowerInvariant() : "";
            vm.Ask.UseStream((history, onText, ct) => DemoStreamAsync(mode, onText, ct));
            _window.DevHoldOpen = true;
            var t = _devAsk = DispatcherQueue.GetForCurrentThread().CreateTimer();
            int step = 0;
            t.Interval = TimeSpan.FromMilliseconds(1200);
            t.Tick += (_, _) =>
            {
                switch (step++)
                {
                    case 0:
                        if (_args.Contains("focus", StringComparer.OrdinalIgnoreCase)) _window?.InvokeAskHotkey(); // takes keyboard focus, like the hotkey
                        else _window?.PinOpen(Math.Max(0, vm.Pages.IndexOf(PageId.Ask)));
                        break;
                    case 1:
                        if (_args.Contains("focus", StringComparer.OrdinalIgnoreCase)) { t.Stop(); break; } // focus check: just the open box
                        if (_args.Contains("select", StringComparer.OrdinalIgnoreCase)) { t.Stop(); vm.Ask.SelectAreaCommand.Execute(null); break; } // region selector check
                        vm.Ask.Attach(new Screenshot("", DemoImage(), 320, 200));
                        vm.Ask.InputText = "What does this chart show?";
                        break;
                    case 2: vm.Ask.SendCommand.Execute(null); t.Stop(); break;
                }
            };
            t.Start();
        }
        // Dev: `--toggle` opens and closes the notch every 1.8 s (to measure the morph without moving the mouse).
        if (_args.Contains("--toggle", StringComparer.OrdinalIgnoreCase))
        {
            var tg = _devToggle = DispatcherQueue.GetForCurrentThread().CreateTimer();
            tg.Interval = TimeSpan.FromMilliseconds(1800);
            tg.Tick += (_, _) => _window?.DevToggle();
            tg.Start();
        }
        // Dev: `--cycle` steps through the pages every 1.6 s (to measure page slides without key presses).
        if (_args.Contains("--cycle", StringComparer.OrdinalIgnoreCase))
        {
            var cycle = _devCycle = DispatcherQueue.GetForCurrentThread().CreateTimer();
            cycle.Interval = TimeSpan.FromMilliseconds(1600);
            cycle.Tick += (_, _) => vm.PageIndex = (vm.PageIndex + 1) % Math.Max(1, vm.PageCount);
            cycle.Start();
        }

        // `DynamicNotch.exe --demo` queues one of each pill so the animations can be checked.
        if (_args.Contains("--demo", StringComparer.OrdinalIgnoreCase))
        {
            vm.Devices.AddPreview(new ConnectedDevice("demo-buds", "Demo Buds", DeviceKind.Headphones, 76));
            vm.Devices.AddPreview(new ConnectedDevice("demo-mouse", "Demo Mouse", DeviceKind.Mouse, 34));
            vm.Devices.AddPreview(new ConnectedDevice("demo-pad", "Demo Controller", DeviceKind.Controller, 12));
            vm.Devices.AddPreview(new ConnectedDevice("demo-spk", "Demo Speaker", DeviceKind.Speaker, null));
            // Sample clipboard items (memory only: the real clipboard and the shelf file are untouched).
            vm.Clipboard.AddPreview(new ClipContent { Kind = ClipKind.Text, Text = "Meeting moved to 4pm. Bring the quarterly numbers and the draft for the new onboarding flow.", Fingerprint = "demo:t1" });
            vm.Clipboard.AddPreview(new ClipContent { Kind = ClipKind.Link, Text = "https://learn.microsoft.com/windows/apps/windows-app-sdk/", Fingerprint = "demo:l1" });
            vm.Clipboard.AddPreview(new ClipContent { Kind = ClipKind.Image, Image = DemoImage(), Fingerprint = "demo:i1" });
            vm.Clipboard.AddPreview(new ClipContent { Kind = ClipKind.Text, Text = "dotnet build -c Release", Fingerprint = "demo:t2" });
            await Task.Delay(1500);
            vm.EnqueuePill(new PillRequest(PillSource.Device, "WH-1000XM5", "Connected", Icons.Headphone, Badge: "80%"));
            vm.EnqueuePill(new PillRequest(PillSource.Network, "Wi-Fi connected", "Home-5G", Icons.Wifi));
            vm.EnqueuePill(new PillRequest(PillSource.Vpn, "VPN connected", "WireGuard", Icons.Vpn));
            vm.EnqueuePill(new PillRequest(PillSource.Power, "Charging", "64%", Icons.Bolt, Badge: "64%"));
            vm.EnqueuePill(new PillRequest(PillSource.App, "Alex", "Are we still on for 7?", Icons.Bell, AppName: "Messages"));
        }
    }

    /// <summary>Dev: a canned streamed answer (or a canned failure), chunk by chunk, entirely local.</summary>
    private static async Task DemoStreamAsync(string mode, Action<string> onText, CancellationToken ct)
    {
        await Task.Delay(mode == "thinking" ? 600_000 : 1400, ct);
        if (mode == "error") throw new ClaudeException(ClaudeErrorKind.NoKey, "Add your Claude API key in Settings to use Ask");
        const string answer =
            "## Quarterly revenue\n\nThe chart shows **revenue by quarter**, rising steadily from Q1 to Q4.\n\n" +
            "- Q1 starts lowest, around `1.2M`\n- Q3 has the **biggest jump** (+28%)\n- Q4 ends at roughly `2.1M`\n\n" +
            "1. Growth is consistent\n2. No quarter declined\n\n```\nq4 / q1 = 1.75x\n```\n\n" +
            "Overall it's a healthy upward trend; the Q3 jump is worth a closer look.";
        for (int i = 0; i < answer.Length; i += 6)
        {
            onText(answer.Substring(i, Math.Min(6, answer.Length - i)));
            await Task.Delay(30, ct);
        }
    }

    /// <summary>Dev: a usage response in the expected (unofficial) shape, with resets relative to now.</summary>
    private static string DemoUsageJson()
    {
        string At(TimeSpan t) => DateTimeOffset.UtcNow.Add(t).ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        return $$"""
        { "five_hour": { "utilization": 42.0, "resets_at": "{{At(TimeSpan.FromMinutes(134))}}" },
          "seven_day": { "utilization": 71.0, "resets_at": "{{At(TimeSpan.FromHours(77))}}" },
          "seven_day_oauth_apps": null,
          "seven_day_opus": null,
          "seven_day_sonnet": { "utilization": 88.0, "resets_at": "{{At(TimeSpan.FromHours(77))}}" } }
        """;
    }

    /// <summary>A colourful 320×200 gradient for the `--demo` image tile.</summary>
    private static Windows.Graphics.Imaging.SoftwareBitmap DemoImage()
    {
        const int w = 320, h = 200;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            px[i] = (byte)(255 * y / h); px[i + 1] = (byte)(120 + 100 * x / w); px[i + 2] = (byte)(255 - 255 * x / w); px[i + 3] = 255;
        }
        return Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(px),
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, w, h, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
    }

    private T Track<T>(T disposable) where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    private void Quit()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }
        _disposables.Clear();
        _tray?.Dispose();
        _tray = null;
        _window?.CloseNotch();
        Exit();
    }
}
