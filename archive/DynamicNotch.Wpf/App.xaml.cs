using System.Windows;
using System.Windows.Media;
using DynamicNotch.Animation;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;

namespace DynamicNotch;

/// <summary>Composition root: creates services, view models, the notch window and the tray icon.</summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private readonly List<IDisposable> _disposables = new();
    private TrayService? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\DynamicNotch.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        // A UI glitch should never take the notch down: log it and keep running.
        DispatcherUnhandledException += (_, args) =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(SettingsService.DataFolder);
                System.IO.File.AppendAllText(System.IO.Path.Combine(SettingsService.DataFolder, "error.log"),
                    $"[{DateTime.Now:u}] {args.Exception}\n\n");
            }
            catch { }
            args.Handled = true;
        };

        // Hardware rendering only; the animator respects the Windows "Animation effects" setting.
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;
        SpringAnimator.RefreshSystemSetting();

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
        var secrets = new SecretStore();
        var claude = new ClaudeService(secrets);
        var pages = new PageRegistry(settings);
        var settingsVm = new SettingsViewModel(secrets, claude, pages);

        var vm = new MainViewModel(settings,
            new MusicViewModel(media),
            new ClipboardViewModel(clipboard),
            new ShelfViewModel(shelf),
            new StatusViewModel(clock, battery),
            new DevicesViewModel(bluetooth),
            new AskViewModel(claude, secrets),
            pages,
            notifications);

        foreach (var src in new Action<Action<PillRequest>>[]
                 {
                     h => network.Pill += h, h => vpn.Pill += h, h => bluetooth.Pill += h,
                     h => power.Pill += h, h => notifications.Pill += h,
                 })
            src(vm.EnqueuePill);

        var window = new NotchWindow(vm);
        MainWindow = window;
        window.Show();
        clipboard.Attach(window);
        clipboard.SetListening(pages.IsEnabled(PageId.Clipboard));
        // Pages switched off in Settings stop their listeners; switching them back on restarts them.
        pages.Changed += () =>
        {
            clipboard.SetListening(pages.IsEnabled(PageId.Clipboard));
            if (pages.IsEnabled(PageId.Music)) _ = media.StartAsync(); else media.Stop();
        };

        // Global ← / → / Space / Esc while the notch is open or hovered, without ever taking focus.
        var keys = Track(new KeyboardHookService(Dispatcher));
        keys.Start();
        window.AttachKeyboard(keys);
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
        _tray.SettingsRequested += () => Views.SettingsWindow.ShowSingle(settingsVm);
        _tray.QuitRequested += () => Shutdown();
        notifications.AccessChanged += a => Dispatcher.InvokeAsync(() => _tray?.SetNotificationAccess(a));

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

        // `DynamicNotch.exe --demo` queues one of each pill so the animations can be checked.
        if (e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase))
        {
            // Sample devices for the connected-devices row (one without battery data).
            vm.Devices.AddPreview(new ConnectedDevice("demo-buds", "Demo Buds", DeviceKind.Headphones, 76));
            vm.Devices.AddPreview(new ConnectedDevice("demo-mouse", "Demo Mouse", DeviceKind.Mouse, 34));
            vm.Devices.AddPreview(new ConnectedDevice("demo-pad", "Demo Controller", DeviceKind.Controller, 12));
            vm.Devices.AddPreview(new ConnectedDevice("demo-spk", "Demo Speaker", DeviceKind.Speaker, null));
            await Task.Delay(1500);
            vm.EnqueuePill(new PillRequest(PillSource.Device, "WH-1000XM5", "Connected", Design.Icons.Headphone, Badge: "80%"));
            vm.EnqueuePill(new PillRequest(PillSource.Network, "Wi-Fi connected", "Home-5G", Design.Icons.Wifi));
            vm.EnqueuePill(new PillRequest(PillSource.Vpn, "VPN connected", "WireGuard", Design.Icons.Vpn));
            vm.EnqueuePill(new PillRequest(PillSource.Power, "Charging", "64%", Design.Icons.Bolt, Badge: "64%"));
            vm.EnqueuePill(new PillRequest(PillSource.App, "Alex", "Are we still on for 7?", Design.Icons.Bell, AppName: "Messages"));
        }

        // Re-check notification permission whenever the app is re-activated from Settings.
        Activated += async (_, _) =>
        {
            if (notifications.Access == NotificationAccess.Denied) await notifications.RetryAsync();
        };
    }

    private T Track<T>(T disposable) where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { }
        }
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
