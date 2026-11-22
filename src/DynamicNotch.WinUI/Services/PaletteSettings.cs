namespace DynamicNotch.Services;

/// <summary>What a custom shortcut does.</summary>
public enum ShortcutKind { App, File, Folder, Url, Command }

/// <summary>A user-made palette command: type its keyword (or name) and Enter runs it.</summary>
public sealed class PaletteShortcut
{
    public string Keyword { get; set; } = "";
    public string Name { get; set; } = "";
    public ShortcutKind Kind { get; set; } = ShortcutKind.Url;
    /// <summary>The app / file / folder path, the URL, or the command line.</summary>
    public string Target { get; set; } = "";
}

/// <summary>Settings ▸ Command Palette.</summary>
public sealed class PaletteSettings
{
    /// <summary>Global hotkey: Win32 modifier flags (MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8) and virtual key.</summary>
    public uint HotkeyModifiers { get; set; } = 0x0001;
    public uint HotkeyKey { get; set; } = 0x20; // Space
    /// <summary>Ids of palette functions switched off (see PaletteFunctions.All).</summary>
    public List<string> Disabled { get; set; } = new();
    public List<PaletteShortcut> Shortcuts { get; set; } = new();
}
