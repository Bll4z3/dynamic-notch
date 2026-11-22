using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DynamicNotch.Services.Palette;

/// <summary>A system action the palette offers. <see cref="Confirm"/> ones need Enter twice.</summary>
public sealed record SystemCommand(string Id, string Name, string Description, string[] Keywords, Action Run, bool Confirm = false);

/// <summary>Lock, sleep, restart, shut down, empty the recycle bin, dark mode and the common Settings pages.</summary>
public static class SystemCommands
{
    public static readonly IReadOnlyList<SystemCommand> All = new SystemCommand[]
    {
        new("lock", "Lock", "Lock the PC", new[] { "lock", "lock screen", "lock pc" }, () => LockWorkStation()),
        new("sleep", "Sleep", "Put the PC to sleep", new[] { "sleep", "suspend" }, () => SetSuspendState(false, false, false)),
        new("restart", "Restart", "Restart the PC", new[] { "restart", "reboot" }, () => Run("shutdown", "/r /t 0"), Confirm: true),
        new("shutdown", "Shut down", "Turn the PC off", new[] { "shut down", "shutdown", "power off", "turn off" }, () => Run("shutdown", "/s /t 0"), Confirm: true),
        new("signout", "Sign out", "Sign out of Windows", new[] { "sign out", "log out", "logoff", "log off" }, () => Run("shutdown", "/l"), Confirm: true),
        new("recycle", "Empty recycle bin", "Permanently delete what's in the recycle bin", new[] { "empty recycle bin", "empty bin", "recycle bin", "empty trash" },
            () => SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4), Confirm: true),
        new("darkmode", "Toggle dark mode", "Switch Windows between dark and light", new[] { "dark mode", "light mode", "toggle dark mode", "theme" }, ToggleDarkMode),
        new("taskmgr", "Task Manager", "Open Task Manager", new[] { "task manager", "taskmgr" }, () => Run("taskmgr", "")),
        new("snip", "Take a screenshot", "Open the snipping overlay (Win+Shift+S)", new[] { "screenshot", "snip", "snipping tool" }, () => Open("ms-screenclip:")),
        new("wifi", "Wi-Fi settings", "Open Settings ▸ Wi-Fi", new[] { "wifi", "wi-fi", "wifi settings", "network" }, () => Open("ms-settings:network-wifi")),
        new("bluetooth", "Bluetooth settings", "Open Settings ▸ Bluetooth & devices", new[] { "bluetooth", "bluetooth settings", "devices" }, () => Open("ms-settings:bluetooth")),
        new("display", "Display settings", "Open Settings ▸ Display", new[] { "display", "display settings", "screen", "resolution" }, () => Open("ms-settings:display")),
        new("sound", "Sound settings", "Open Settings ▸ Sound", new[] { "sound", "sound settings", "audio" }, () => Open("ms-settings:sound")),
        new("battery", "Battery settings", "Open Settings ▸ Power & battery", new[] { "battery", "power settings", "energy saver" }, () => Open("ms-settings:batterysaver")),
        new("apps", "Installed apps", "Open Settings ▸ Apps", new[] { "installed apps", "uninstall", "apps settings" }, () => Open("ms-settings:appsfeatures")),
        new("update", "Windows Update", "Open Settings ▸ Windows Update", new[] { "windows update", "update windows", "updates" }, () => Open("ms-settings:windowsupdate")),
        new("personalise", "Personalisation", "Open Settings ▸ Personalisation", new[] { "personalise", "personalize", "wallpaper", "background" }, () => Open("ms-settings:personalization")),
        new("notifs", "Notification settings", "Open Settings ▸ Notifications", new[] { "notification settings" }, () => Open("ms-settings:notifications")),
        new("mouse", "Mouse settings", "Open Settings ▸ Mouse", new[] { "mouse", "touchpad", "trackpad" }, () => Open("ms-settings:mousetouchpad")),
        new("settings", "Windows Settings", "Open the Settings app", new[] { "windows settings", "control panel" }, () => Open("ms-settings:")),
    };

    public static void Open(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    private static void Run(string exe, string args)
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, CreateNoWindow = true }); } catch { }
    }

    private static void ToggleDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: true);
            if (key == null) return;
            int light = key.GetValue("AppsUseLightTheme") is int v ? v : 1;
            int next = light == 0 ? 1 : 0;
            key.SetValue("AppsUseLightTheme", next, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", next, RegistryValueKind.DWord);
            SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 1000, out _);
        }
        catch { }
    }

    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
