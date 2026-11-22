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
    private DispatcherQueueTimer? _devPreview, _devPerf;
    private DispatcherQueueTimer? _devAsk;
    private DispatcherQueueTimer? _devToggle;
    private DispatcherQueueTimer? _devActivity;
    private DispatcherQueueTimer? _devCharge;

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
        var settings = new SettingsService();
        settings.Load();
        // The chosen UI font goes into Theme before any style or view is built.
        var fonts = new FontService(settings);
        fonts.ApplySaved();
        // Customisation tokens (accent, radii, motion, surface) go into Theme before any view is built too.
        var theme = new ThemeService(settings);
        theme.ApplySaved();
        Styles.Register(Resources);
        Anim.RefreshSystemSetting();

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
        var audio = Track(new AudioService());
        var brightness = Track(new BrightnessService());
        var downloads = Track(new DownloadWatcherService());
        var quick = Track(new QuickActionsService());
        var updates = Track(new UpdateService(settings));
        var activity = new ActivityViewModel(settings);
        var devices = new DevicesViewModel(bluetooth);
        var wifi = new WifiService();
        var btDevices = Track(new BluetoothDeviceService());
        var hub = new HubViewModel(settings, quick, audio, brightness, notifications, wifi, btDevices, devices);
        var screenshots = Track(new ScreenshotService());
        var shots = new ScreenshotShelfViewModel(settings, screenshots);
        var quickTimer = new QuickTimerViewModel();
        var clipVm = new ClipboardViewModel(clipboard);
        var spotify = new SpotifyService(settings);
        var musicVm = new MusicViewModel(media);
        var spotifyLike = new SpotifyLikeViewModel(musicVm, spotify);
        var settingsVm = new SettingsViewModel(secrets, claude, pages, settings, focus, updates, fonts, theme);
        settingsVm.General.Spotify = spotify;
        spotifyLike.ConnectRequested += () => Views.SettingsWindow.ShowSingle(settingsVm);

        var vm = new MainViewModel(settings,
            musicVm,
            clipVm,
            new ShelfViewModel(shelf),
            new AskViewModel(claude, secrets),
            new StatusViewModel(clock, battery),
            devices,
            pages,
            notifications,
            new UsageViewModel(new ClaudeUsageService()),
            focus,
            activity,
            hub) { Screenshots = shots, QuickTimer = quickTimer, SpotifyLike = spotifyLike, Palette = new CommandPaletteViewModel(settings, clipVm, shots, claude, quickTimer) };

        foreach (var src in new Action<Action<PillRequest>>[]
                 {
                     h => network.Pill += h, h => vpn.Pill += h, h => bluetooth.Pill += h, h => notifications.Pill += h,
                 })
            src(vm.EnqueuePill);

        // ---- Activity pills: every source feeds the ONE activity host ----
        audio.VolumeChanged += v => activity.Show(new Activity(ActivityKind.Volume, "Volume", v.Level, On: v.Muted, OnSet: audio.SetVolume));
        // Brightness: only when the user pressed a brightness key. Changes Windows makes by itself (adaptive /
        // auto brightness, dimming) also arrive through brightness.Changed, so they are ignored here; the
        // notch's own brightness slider still follows every change (HubViewModel listens separately).
        var brightnessKeys = Track(new BrightnessKeyService());
        void ShowBrightness(int level) => activity.Show(new Activity(ActivityKind.Brightness, "Brightness", level / 100.0,
            OnSet: x => brightness.Set((int)Math.Round(x * 100))));
        bool brightnessShownForKey = false;
        var brightnessAtLimit = DispatcherQueue.GetForCurrentThread().CreateTimer();
        brightnessAtLimit.IsRepeating = false;
        brightnessAtLimit.Interval = TimeSpan.FromMilliseconds(350);
        // A key press that doesn't change the level (already at 0 or 100 %) still shows the pill, like Windows does.
        brightnessAtLimit.Tick += (_, _) => { brightnessAtLimit.Stop(); if (!brightnessShownForKey && brightness.IsSupported) ShowBrightness(brightness.Level); };
        brightnessKeys.Pressed += _ => { brightnessShownForKey = false; brightnessAtLimit.Stop(); brightnessAtLimit.Start(); };
        brightness.Changed += level =>
        {
            if (!brightnessKeys.RecentlyPressed) return;
            brightnessShownForKey = true;
            ShowBrightness(level);
        };
        power.PlugChanged += (plugged, pct, estimate) =>
        {
            string title = plugged ? "Charging" : "On battery";
            activity.Show(new Activity(ActivityKind.Charging, title, pct / 100.0, On: plugged, Subtitle: estimate));
            hub.Record(new PillRequest(PillSource.Power, title, $"{pct}%", plugged ? Icons.Bolt : Icons.Battery));
        };
        power.LowBattery += pct =>
        {
            activity.Show(new Activity(ActivityKind.LowBattery, "Low battery", pct / 100.0, Subtitle: PowerService.Estimate(false) ?? "Plug in soon"));
            hub.Record(new PillRequest(PillSource.Power, "Low battery", $"{pct}% remaining", Icons.BatteryLow));
        };
        downloads.Progress += d => ShowDownload(activity, hub, d);
        downloads.Enabled = settings.Current.ShowDownloadPill;
        settings.Changed += () => downloads.Enabled = settings.Current.ShowDownloadPill;

        // ---- Updates: pill + history entry when found, progress while downloading, "Restart" when ready ----
        updates.UpdateFound += info =>
        {
            string ver = info.Version.ToString(3);
            Action get = () => _ = updates.GetUpdateAsync();
            activity.Show(new Activity(ActivityKind.Update, "Update available", Subtitle: $"Version {ver}",
                Value: UpdateService.CanInstallInPlace ? "Update" : "Get", OnClick: get));
            hub.Record(new PillRequest(PillSource.System, "Update available", $"Dynamic Notch {ver} · click to update", Icons.UpdateArrow, OnClick: get));
        };
        updates.DownloadProgress += d => { if (!d.Done || d.Failed) ShowDownload(activity, hub, d); };
        var lastStatus = updates.Status;
        updates.Changed += () =>
        {
            if (_tray != null) _tray.UpdateReady = updates.Status == UpdateStatus.Ready;
            if (updates.Status == UpdateStatus.Ready && lastStatus != UpdateStatus.Ready)
                activity.Show(new Activity(ActivityKind.Update, "Update ready", Subtitle: "Click to restart and install", Value: "Restart",
                    OnClick: updates.InstallAndRestart));
            lastStatus = updates.Status;
        };
        updates.QuitRequested += Quit;

        _window = new NotchWindow(vm);
        _window.AttachTheme(theme);
        // Performance mode: Low power when chosen, or (auto) on battery saver and in remote desktop sessions.
        void ApplyPower()
        {
            Motion.LowPower = theme.ResolveLowPower(quick.EnergySaverOn);
            Views.Visualizer.FrameRate = Motion.LowPower ? 15 : Motion.VisualizerFps;
        }
        ApplyPower();
        quick.Changed += ApplyPower;
        theme.Changed += p => { if (p.HasFlag(ThemeService.Part.Performance)) ApplyPower(); };
        _window.ShowNotch();
        // Hide the notch while another app is fullscreen (Settings ▸ General).
        var fullscreen = Track(new FullscreenService(() => _window?.Handle ?? IntPtr.Zero));
        fullscreen.Changed += full => _window?.SetFullscreenHidden(full);
        // Dev: `--keep-visible` never hides for fullscreen apps (for testing while another app is fullscreen).
        bool keepVisible = _args.Contains("--keep-visible", StringComparer.OrdinalIgnoreCase);
        fullscreen.Enabled = settings.Current.HideWhenFullscreen && !keepVisible;
        settings.Changed += () => fullscreen.Enabled = settings.Current.HideWhenFullscreen && !keepVisible;
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
        keys.PrimeCapsLock(); // only real changes from here on raise the pill
        // Screenshot shelf: new screenshots land on the Screenshots page (see ScreenshotService for how they're told apart).
        screenshots.PrintScreenTick = () => keys.LastPrintScreenTick;
        screenshots.Captured += async (path, owned) =>
        {
            var item = await shots.AddAsync(path, owned);
            _window?.OnScreenshotAdded(item, settings.Current.ScreenshotAutoOpen);
        };
        void SyncScreenshots()
        {
            screenshots.SetRunning(settings.Current.ScreenshotShelf && pages.IsShown(PageId.Screenshots));
            shots.ApplyLimit();
        }
        pages.Changed += SyncScreenshots;
        settings.Changed += SyncScreenshots;
        SyncScreenshots();
        keys.CapsLockChanged += on => activity.Show(new Activity(ActivityKind.CapsLock, on ? "Caps Lock on" : "Caps Lock off", On: on));
        _window.AttachKeyboard(keys);
        // Settings (single window); the hook never intercepts keys while it is in front.
        vm.OpenSettingsRequested += () => Views.SettingsWindow.ShowSingle(settingsVm);
        vm.OpenApiKeyRequested += () => { settingsVm.SelectApiKey(); Views.SettingsWindow.ShowSingle(settingsVm); };
        Views.SettingsWindow.HandleChanged += h => { keys.PassThroughWindow = h; theme.SetSettingsOpen(h != IntPtr.Zero); };
        // A new UI font: every open window refreshes its text with a short cross-fade (no restart).
        fonts.FontChanged += previous =>
        {
            _window?.ApplyFont(previous);
            Views.SettingsWindow.ApplyFont(previous);
        };

        // Tray (Minimal notch and Start with Windows also live in Settings ▸ General; both stay in sync)
        _tray = new TrayService();
        _tray.SetMinimal(settings.Current.MinimalNotch);
        void SetMinimal(bool on) { vm.MinimalNotch = on; _tray?.SetMinimal(on); settingsVm.General.RefreshToggles(); }
        async void SetStartup(bool on)
        {
            bool result = await StartupService.SetEnabledAsync(on);
            settings.Update(s => s.StartWithWindows = result);
            _tray?.SetStartup(result);
            settingsVm.General.RefreshToggles();
        }
        _tray.MinimalToggled += SetMinimal;
        _tray.StartupToggled += SetStartup;
        settingsVm.General.MinimalChanged += SetMinimal;
        settingsVm.General.StartupChanged += SetStartup;
        _tray.NotificationAccessRequested += NotificationService.OpenPermissionSettings;
        _tray.QuitRequested += Quit;
        _tray.SettingsAvailable = true;
        _tray.SettingsRequested += () => Views.SettingsWindow.ShowSingle(settingsVm);
        _tray.UpdatesRequested += () =>
        {
            settingsVm.SelectUpdates();
            Views.SettingsWindow.ShowSingle(settingsVm);
            if (updates.Status is not (UpdateStatus.Ready or UpdateStatus.Downloading)) _ = updates.CheckAsync(manual: true);
        };
        notifications.AccessChanged += a => Ui.Post(() => _tray?.SetNotificationAccess(a));

        // Start everything (all event-driven).
        clock.Start();
        battery.Start();
        network.Start();
        vpn.Start();
        bluetooth.Start();
        power.Start();
        audio.Start();
        brightness.Start();
        brightnessKeys.Start();
        brightnessKeys.FallbackKeyTick = () => keys.LastKeyTick; // PCs whose brightness keys aren't HID buttons
        updates.Start();
        _ = quick.StartAsync();
        if (pages.IsEnabled(PageId.Music)) await media.StartAsync();
        // The real state wins (Task Manager / Windows Settings can change it while the app is closed), so the
        // switch in Settings ▸ General and the saved value match what Windows will actually do.
        bool startup = await StartupService.IsEnabledAsync();
        _tray.SetStartup(startup);
        if (settings.Current.StartWithWindows != startup) settings.Update(s => s.StartWithWindows = startup);
        settingsVm.General.RefreshToggles();
        await notifications.StartAsync();

        // Dev: `--palette [text]` opens the command palette (with that text typed) a moment after start.
        int palArg = Array.FindIndex(_args, a => a.Equals("--palette", StringComparison.OrdinalIgnoreCase));
        if (palArg >= 0)
        {
            string text = palArg + 1 < _args.Length && !_args[palArg + 1].StartsWith("--") ? _args[palArg + 1] : "";
            var pt = DispatcherQueue.GetForCurrentThread().CreateTimer();
            pt.IsRepeating = false;
            pt.Interval = TimeSpan.FromMilliseconds(3000);
            pt.Tick += (_, _) => { pt.Stop(); _window?.DevPalette(text); };
            pt.Start();
            _devTimers.Add(pt); // kept referenced, or it can be collected before it fires
        }
        if (_args.Contains("--markup", StringComparer.OrdinalIgnoreCase))
        {
            var mt = DispatcherQueue.GetForCurrentThread().CreateTimer();
            mt.IsRepeating = false;
            mt.Interval = TimeSpan.FromMilliseconds(1500);
            mt.Tick += (_, _) => { mt.Stop(); _window?.DevMarkup(); };
            mt.Start();
            _devTimers.Add(mt); // kept referenced, or it can be collected before it fires
        }
        // Dev: `--header notifications|quick` opens the header pop-up a moment after start (use with --open).
        int headerArg = Array.FindIndex(_args, a => a.Equals("--header", StringComparison.OrdinalIgnoreCase));
        if (headerArg >= 0)
        {
            var panel = headerArg + 1 < _args.Length && _args[headerArg + 1].StartsWith("q", StringComparison.OrdinalIgnoreCase)
                ? PageId.QuickActions : PageId.Notifications;
            var ht = DispatcherQueue.GetForCurrentThread().CreateTimer();
            ht.IsRepeating = false;
            ht.Interval = TimeSpan.FromMilliseconds(1500);
            ht.Tick += (_, _) => { ht.Stop(); _window?.DevHeaderPopup(panel); };
            ht.Start();
            _devTimers.Add(ht); // kept referenced, or it can be collected before it fires
        }

        int open = Array.FindIndex(_args, a => a.Equals("--open", StringComparison.OrdinalIgnoreCase));
        if (open >= 0)
        {
            // `--open <index|music|clipboard|ask|usage|focus|hub> [delay ms]`: pins the notch open on that page.
            string which = open + 1 < _args.Length ? _args[open + 1] : "0";
            int page = int.TryParse(which, out var pg) ? pg
                : Enum.TryParse<PageId>(which, ignoreCase: true, out var id) ? Math.Max(0, vm.Pages.Enabled.ToList().IndexOf(id)) : 0;
            _window.DevHoldOpen = true;
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
        // Dev: `--perf-all` measures startup, then (after a 25 s idle window for external CPU sampling) the
        // expand, every page switch, both popups, the clipboard preview and the collapse, into perf.log.
        if (_args.Contains("--perf-all", StringComparer.OrdinalIgnoreCase)) StartPerfRun(vm);
        if (_args.Contains("--perf-overlay", StringComparer.OrdinalIgnoreCase)) Views.PerfOverlay.Show();
        // Dev: `--perf-popovers` opens page 4 and opens/closes the Bluetooth and Wi-Fi popups a few times,
        // writing click → frame → list timings to %LocalAppData%\DynamicNotch\perf.log.
        if (_args.Contains("--perf-popovers", StringComparer.OrdinalIgnoreCase))
        {
            PerfLog.Enabled = true;
            _window.DevHoldOpen = true;
            var t = _devPerf = DispatcherQueue.GetForCurrentThread().CreateTimer();
            int step = 0;
            t.Interval = TimeSpan.FromMilliseconds(1800);
            t.Tick += (_, _) =>
            {
                var quick = _window?.DevQuickActions;
                if (quick == null) return;
                int s = step++;
                if (s == 0) { _window!.PinOpen(0); _window.DevHeaderPopup(PageId.QuickActions); return; }
                if (s > 16) { quick.ClosePopover(); t.Stop(); return; }
                if (s % 2 == 1) quick.DevOpenPopover(bluetooth: s <= 8); else quick.ClosePopover();
            };
            t.Start();
        }
        // Dev: `--settings [pages]` opens the Settings window (on the Pages tab with `pages`).
        int settingsArg = Array.FindIndex(_args, a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase));
        if (settingsArg >= 0)
        {
            string tab = settingsArg + 1 < _args.Length ? _args[settingsArg + 1].ToLowerInvariant() : "";
            int tabIndex = tab switch { "custom" => 1, "key" => 2, "layout" => 3, "pages" => 3, "palette" => 4, "pills" => 5, "focus" => 6, "updates" => 7, _ => 0 };
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
                        else { _window?.PinOpen(0); _window?.InvokeAskHotkey(); }
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

        // Dev: `--charge-demo` flips the notch's own battery between charging and not every 6 s, with the matching pills.
        if (_args.Contains("--charge-demo", StringComparer.OrdinalIgnoreCase))
        {
            bool plugged = false;
            var ct = _devCharge = DispatcherQueue.GetForCurrentThread().CreateTimer();
            ct.Interval = TimeSpan.FromSeconds(6);
            ct.Tick += (_, _) =>
            {
                plugged = !plugged;
                vm.Status.DevSetCharging(plugged);
                double pct = Math.Max(0, vm.Status.BatteryPercent) / 100.0;
                activity.Show(new Activity(ActivityKind.Charging, plugged ? "Charging" : "On battery", pct, On: plugged, Subtitle: plugged ? "1 h 40 min until full" : "2 h 10 min left"));
            };
            ct.Start();
        }

        // Dev: `--activity-demo [kind]` plays each activity pill in turn (or one kind repeatedly), all local and fake:
        // nothing is read from or changed on the PC.
        int actDemo = Array.FindIndex(_args, a => a.Equals("--activity-demo", StringComparison.OrdinalIgnoreCase));
        if (actDemo >= 0)
        {
            string only = actDemo + 1 < _args.Length ? _args[actDemo + 1].ToLowerInvariant() : "";
            var steps = new List<(string kind, Action show)>
            {
                ("volume", () => activity.Show(new Activity(ActivityKind.Volume, "Volume", 0.35, OnSet: _ => { }))),
                ("volume", () => activity.Show(new Activity(ActivityKind.Volume, "Volume", 0.72, OnSet: _ => { }))),
                ("brightness", () => activity.Show(new Activity(ActivityKind.Brightness, "Brightness", 0.6, OnSet: _ => { }))),
                ("caps", () => activity.Show(new Activity(ActivityKind.CapsLock, "Caps Lock on", On: true))),
                ("caps", () => activity.Show(new Activity(ActivityKind.CapsLock, "Caps Lock off", On: false))),
                ("charging", () => activity.Show(new Activity(ActivityKind.Charging, "Charging", 0.64, On: true, Subtitle: "1 h 10 min until full"))),
                ("download", () => activity.Show(new Activity(ActivityKind.Download, "Quarterly report.pdf", null, "12.4 MB", Subtitle: "12.4 MB", Key: "demo"))),
                ("download", () => activity.Show(new Activity(ActivityKind.Download, "Downloaded", null, "", On: true, Subtitle: "Quarterly report.pdf", Key: "demo"))),
                ("update", () => activity.Show(new Activity(ActivityKind.Update, "Update available", Subtitle: "Version 2.2.0", Value: "Update"))),
                ("lowbattery", () => activity.Show(new Activity(ActivityKind.LowBattery, "Low battery", 0.12, Subtitle: "25 min left"))),
            };
            if (only.Length > 0 && !only.StartsWith("-")) steps = steps.Where(s => s.kind == only).ToList();
            vm.Hub.Record(new PillRequest(PillSource.Device, "WH-1000XM5", "Connected · 80%", Icons.Headphone));
            vm.Hub.Record(new PillRequest(PillSource.Network, "Wi-Fi connected", "Home-5G", Icons.Wifi));
            vm.Hub.Record(new PillRequest(PillSource.App, "Alex", "Are we still on for 7?", Icons.Bell, AppName: "Messages"));
            int i = 0;
            var t = _devActivity = DispatcherQueue.GetForCurrentThread().CreateTimer();
            t.Interval = TimeSpan.FromMilliseconds(2600);
            t.Tick += (_, _) => { if (steps.Count > 0) steps[i++ % steps.Count].show(); };
            t.Start();
        }
    }

    private DispatcherQueueTimer? _devPerfAll;

    /// <summary>Dev (`--perf-all`): startup time, a quiet idle window, then each animation traced in perf.log.</summary>
    private void StartPerfRun(MainViewModel vm)
    {
        PerfLog.Enabled = true;
        _window!.DevHoldOpen = true;
        EventHandler<object>? first = null;
        first = (_, _) =>
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= first;
            double ms = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            PerfLog.Note($"startup: first frame {ms:0} ms after process start");
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += first;

        var steps = new List<(string name, Action act)>();
        steps.Add(("#IDLE-START (collapsed, 25 s)", () => { }));
        for (int k = 0; k < 16; k++) steps.Add(("", () => { })); // 16 × 1.6 s ≈ 25 s of idle
        steps.Add(("#IDLE-END", () => { }));
        steps.Add(("Expand (collapsed → open)", () => _window?.PinOpen(0)));
        steps.Add(("#MUSIC-START (expanded music page, 10 s)", () => { }));
        for (int k = 0; k < 6; k++) steps.Add(("", () => { }));
        steps.Add(("#MUSIC-END", () => { }));
        int n = vm.Pages.Enabled.Count;
        for (int p = 1; p < n; p++)
        {
            int to = p;
            steps.Add(($"Page switch {vm.Pages.Enabled[to - 1]} → {vm.Pages.Enabled[to]}", () => vm.PageIndex = to));
        }
        steps.Add(("Quick actions pop-up", () => _window?.DevHeaderPopup(PageId.QuickActions)));
        steps.Add(("!Bluetooth", () => _window?.DevQuickActions.DevOpenPopover(true)));
        steps.Add(("-close", () => _window?.DevQuickActions.ClosePopover()));
        steps.Add(("!Wi-Fi", () => _window?.DevQuickActions.DevOpenPopover(false)));
        steps.Add(("-close", () => _window?.DevQuickActions.ClosePopover()));
        steps.Add(("Notifications pop-up", () => _window?.DevHeaderPopup(PageId.Notifications)));
        steps.Add(("-close", () => _window?.DevHeaderPopup(null)));
        int clip = vm.Pages.Enabled.ToList().IndexOf(PageId.Clipboard);
        if (clip >= 0)
        {
            steps.Add(($"Page switch → Clipboard", () => vm.PageIndex = clip));
            steps.Add(("Clipboard hover preview", () => _window?.ShowFirstPreview()));
        }
        steps.Add(("Collapse (open → collapsed)", () => _window?.DevToggle()));
        steps.Add(("#DONE", () => { }));

        int i = 0;
        var t = _devPerfAll = DispatcherQueue.GetForCurrentThread().CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(1600);
        t.Tick += (_, _) =>
        {
            if (i >= steps.Count) { t.Stop(); return; }
            var (name, act) = steps[i++];
            if (name.StartsWith("#")) { PerfLog.End(); PerfLog.Note(name[1..]); act(); return; }
            if (name == "") return;
            if (name.StartsWith("-")) { act(); return; }        // popup traces end on their own Closed
            if (!name.StartsWith("!")) PerfLog.Begin(name);    // popup opens begin their own trace
            act();
        };
        t.Start();
    }

    /// <summary>A download's progress / result as an activity pill (and a history entry when it finishes).</summary>
    private static void ShowDownload(ActivityViewModel activity, HubViewModel hub, DownloadInfo d)
    {
        string value = d.Done ? "" : d.Fraction is { } f ? $"{f * 100:0}%" : FormatBytes(d.Received);
        Action? open = d.FinalPath is { } path ? () => RevealInExplorer(path) : null;
        string title = d.Done ? d.Failed ? "Download failed" : "Downloaded" : d.Name;
        activity.Show(new Activity(ActivityKind.Download, title, d.Fraction, value, On: d.Done, Subtitle: d.Done ? d.Name : FormatBytes(d.Received),
            Failed: d.Failed, OnClick: open, Key: d.Key));
        if (d.Done && !d.Failed && d.FinalPath != null) hub.Record(new PillRequest(PillSource.System, "Downloaded", d.Name, Icons.Download, OnClick: open));
    }

    private static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{b / 1024.0:0} KB",
        _ => $"{b} B",
    };

    private static void RevealInExplorer(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
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

    private readonly List<object> _devTimers = new();

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
