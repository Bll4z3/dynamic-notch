using System.IO;
using System.Text.Json;

namespace DynamicNotch.Services;

public sealed class AppSettings
{
    public bool MinimalNotch { get; set; }
    public bool StartWithWindows { get; set; }
    public bool ShowAppNotifications { get; set; } = true;
    public bool ShowDevicePills { get; set; } = true;
    public bool ShowNetworkPills { get; set; } = true;
    public bool ShowBatteryPills { get; set; } = true;
    public int LowBatteryPercent { get; set; } = 20;
    /// <summary>Ids of notch pages switched off in Settings ▸ Pages (see PageRegistry).</summary>
    public List<string> DisabledPages { get; set; } = new();
}

/// <summary>Settings and app data live in %LocalAppData%\DynamicNotch (virtualised per-package under MSIX).</summary>
public sealed class SettingsService
{
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicNotch");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(DataFolder, "settings.json");

    public AppSettings Current { get; private set; } = new();

    public event Action? Changed;

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path)) ?? new AppSettings();
        }
        catch { Current = new AppSettings(); }
    }

    public void Update(Action<AppSettings> change)
    {
        change(Current);
        Save();
        Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(_path, JsonSerializer.Serialize(Current, Json));
        }
        catch { /* settings are best-effort */ }
    }

    public static T? ReadJson<T>(string fileName)
    {
        try
        {
            var p = Path.Combine(DataFolder, fileName);
            return File.Exists(p) ? JsonSerializer.Deserialize<T>(File.ReadAllText(p)) : default;
        }
        catch { return default; }
    }

    public static void WriteJson<T>(string fileName, T value)
    {
        try
        {
            Directory.CreateDirectory(DataFolder);
            File.WriteAllText(Path.Combine(DataFolder, fileName), JsonSerializer.Serialize(value, Json));
        }
        catch { }
    }
}
