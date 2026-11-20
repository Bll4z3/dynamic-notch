using System.Text.Json;
using DynamicNotch.Services;

namespace DynamicNotch.ViewModels;

/// <summary>The settings an export file carries: appearance and behaviour only (never the API key or anything personal).</summary>
public sealed class SettingsExport
{
    public string App { get; set; } = "DynamicNotch";
    public int Format { get; set; } = 1;
    public string? Version { get; set; }
    public CustomSettings Custom { get; set; } = new();
    public string? FontId { get; set; }
    public bool MinimalNotch { get; set; }
    public bool HideWhenFullscreen { get; set; } = true;
    public bool ShowVolumePill { get; set; } = true;
    public bool ShowBrightnessPill { get; set; } = true;
    public bool ShowCapsLockPill { get; set; } = true;
    public bool ShowDownloadPill { get; set; } = true;
    public bool ShowBatteryPills { get; set; } = true;
    public bool ShowDevicePills { get; set; } = true;
    public bool ShowNetworkPills { get; set; } = true;
    public bool ShowAppNotifications { get; set; } = true;
}

/// <summary>
/// "Customisation" tab: everything goes through <see cref="ThemeService"/> (live tokens, persisted);
/// this adds the font, reset with undo, and export / import.
/// </summary>
public sealed class CustomisationTabViewModel
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly SettingsService _settings;
    private (CustomSettings custom, string? font)? _undo;

    public CustomisationTabViewModel(SettingsService settings, ThemeService theme, FontService fonts)
    {
        _settings = settings;
        Theme = theme;
        Fonts = fonts;
    }

    public ThemeService Theme { get; }
    public FontService Fonts { get; }
    public CustomSettings Current => Theme.Current;
    public bool CanUndo => _undo != null;

    // ---- Reset / undo ----
    /// <summary>Resets every customisation and the font; <see cref="Undo"/> brings it back.</summary>
    public void Reset()
    {
        _undo = (Current.Clone(), _settings.Current.FontId);
        Theme.ReplaceAll(new CustomSettings());
        Fonts.ResetToDefault();
    }

    public void Undo()
    {
        if (_undo is not { } u) return;
        _undo = null;
        Theme.ReplaceAll(u.custom);
        var font = Fonts.Find(u.font) ?? Fonts.BundledFonts[0];
        Fonts.Apply(font);
    }

    public void ForgetUndo() => _undo = null;

    // ---- Export / import ----
    public string ExportJson()
    {
        var s = _settings.Current;
        var e = new SettingsExport
        {
            Version = UpdateService.CurrentVersionText,
            Custom = s.Custom.Clone(),
            FontId = s.FontId,
            MinimalNotch = s.MinimalNotch, HideWhenFullscreen = s.HideWhenFullscreen,
            ShowVolumePill = s.ShowVolumePill, ShowBrightnessPill = s.ShowBrightnessPill, ShowCapsLockPill = s.ShowCapsLockPill,
            ShowDownloadPill = s.ShowDownloadPill, ShowBatteryPills = s.ShowBatteryPills, ShowDevicePills = s.ShowDevicePills,
            ShowNetworkPills = s.ShowNetworkPills, ShowAppNotifications = s.ShowAppNotifications,
        };
        return JsonSerializer.Serialize(e, Json);
    }

    /// <summary>Validates and applies an export file. Returns null on success, or what was wrong.</summary>
    public string? ImportJson(string json)
    {
        SettingsExport? e;
        try { e = JsonSerializer.Deserialize<SettingsExport>(json); }
        catch (JsonException) { return "That file isn't valid JSON."; }
        if (e == null || e.App != "DynamicNotch") return "That isn't a Dynamic Notch settings file.";
        if (e.Format != 1) return $"Unsupported settings format ({e.Format}).";
        if (e.Custom == null) return "The file has no customisation settings.";
        _undo = (Current.Clone(), _settings.Current.FontId);
        Theme.ReplaceAll(e.Custom);
        if (Fonts.Find(e.FontId) is { } font) Fonts.Apply(font); else if (e.FontId == null) Fonts.ResetToDefault();
        _settings.Update(s =>
        {
            s.HideWhenFullscreen = e.HideWhenFullscreen;
            s.ShowVolumePill = e.ShowVolumePill; s.ShowBrightnessPill = e.ShowBrightnessPill; s.ShowCapsLockPill = e.ShowCapsLockPill;
            s.ShowDownloadPill = e.ShowDownloadPill; s.ShowBatteryPills = e.ShowBatteryPills; s.ShowDevicePills = e.ShowDevicePills;
            s.ShowNetworkPills = e.ShowNetworkPills; s.ShowAppNotifications = e.ShowAppNotifications;
        });
        return null;
    }
}
