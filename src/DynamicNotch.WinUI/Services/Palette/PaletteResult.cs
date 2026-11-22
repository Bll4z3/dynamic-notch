using DynamicNotch.Design;
using Windows.Graphics.Imaging;
using Windows.UI;

namespace DynamicNotch.Services.Palette;

/// <summary>What the palette does after a result ran.</summary>
public enum PaletteOutcome
{
    /// <summary>Done: the palette closes (an app launched, a page opened…).</summary>
    Close,
    /// <summary>Something was copied: a quick check mark, then the palette closes.</summary>
    Copied,
    /// <summary>Stay open (e.g. "press Enter again to confirm").</summary>
    Stay,
    /// <summary>Put <see cref="PaletteResult.Fill"/> in the box and keep typing (a command from the list).</summary>
    Fill,
}

/// <summary>One row in the palette.</summary>
public sealed class PaletteResult
{
    public required string Title { get; init; }
    public string? Subtitle { get; set; }
    /// <summary>Vector icon (Icons.*), used when there's no <see cref="Image"/>.</summary>
    public string Icon { get; init; } = Icons.Search;
    /// <summary>A real icon or thumbnail (apps, files, screenshots); may arrive later (<see cref="ImageLoader"/>).</summary>
    public SoftwareBitmap? Image { get; set; }
    public Func<Task<SoftwareBitmap?>>? ImageLoader { get; init; }
    /// <summary>A colour swatch instead of an icon (hex preview).</summary>
    public Color? Swatch { get; init; }
    /// <summary>The key hint on the right ("Enter to copy", "Open"…).</summary>
    public string Hint { get; set; } = "Open";
    /// <summary>The function this came from (for the off switches and ranking).</summary>
    public required string Function { get; init; }
    public double Score { get; set; }
    /// <summary>Runs it. The argument is true for the alternate action (Ctrl+Enter: open the folder, copy only…).</summary>
    public Func<bool, Task<PaletteOutcome>>? Run { get; init; }
    /// <summary>Text for <see cref="PaletteOutcome.Fill"/>.</summary>
    public string? Fill { get; init; }
    /// <summary>When set, running it is remembered for the "recent" list shown on an empty box.</summary>
    public PaletteRecent? Recent { get; init; }
    /// <summary>A big answer shown under the row (maths, conversions, definitions).</summary>
    public bool IsAnswer { get; init; }
}

/// <summary>A remembered result: enough to show it again and run it again.</summary>
public sealed class PaletteRecent
{
    public string Kind { get; set; } = "";   // app, file, folder, url, shortcut, system, feature
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public string Target { get; set; } = "";
    public int Count { get; set; }
    public DateTime Last { get; set; }
}

/// <summary>One palette function, for the "/" command list and the switches in Settings.</summary>
public sealed record PaletteFunction(string Id, string Name, string Description, string Example, string Icon, string? Fill = null);

public static class PaletteFunctions
{
    public static readonly IReadOnlyList<PaletteFunction> All = new PaletteFunction[]
    {
        new("apps", "Launch apps", "Find any installed app or Start menu shortcut", "spotify", Icons.AppWindow),
        new("files", "Search files", "Files and folders by name (Windows Search). Ctrl+Enter opens the folder", "budget.xlsx", Icons.Folder),
        new("calc", "Quick maths", "Live answers; Enter copies", "25*4+10   15% of 80   sqrt(144)", Icons.Calculator),
        new("units", "Convert units", "Length, weight, temperature, speed, data, time, volume, area", "10 km to miles", Icons.Ruler, "10 km to miles"),
        new("currency", "Convert currency", "Live exchange rates (saved for offline use)", "50 gbp to usd", Icons.Coins, "50 gbp to usd"),
        new("web", "Search the web", "g Google · yt YouTube · w Wikipedia · gh GitHub · r Reddit · maps · ddg · img", "g best laptops", Icons.Globe, "g "),
        new("url", "Open websites", "Type an address to open it", "github.com", Icons.Link),
        new("system", "System commands", "Lock, sleep, restart, shut down, empty recycle bin, dark mode, Settings pages", "lock", Icons.Power),
        new("clipboard", "Clipboard history", "Search recent copies; Enter pastes, Ctrl+Enter copies", "clip invoice", Icons.Clipboard, "clip "),
        new("ask", "Ask Claude", "Start with ? or press Tab to ask about what you typed", "? what is a ROI", Icons.Sparkle, "? "),
        new("timer", "Timer and stopwatch", "A countdown on the notch; 'stopwatch' counts up", "timer 5m", Icons.Timer, "timer "),
        new("color", "Colour preview", "Hex or rgb() with a swatch; Enter copies", "#1E90FF", Icons.Palette, "#"),
        new("time", "Dates and times", "Time in other cities, days until a date", "time in tokyo   days until 25 dec", Icons.Clock, "time in "),
        new("define", "Define a word", "Dictionary definitions", "define serendipity", Icons.Book, "define "),
        new("screenshots", "Screenshot shelf", "Search or open your recent screenshots", "ss", Icons.Screenshot, "ss "),
        new("notch", "Notch features", "Jump to Quick actions, Notifications, Settings or a page", "notifications", Icons.Command),
        new("shortcuts", "Your shortcuts", "Commands you made in Settings ▸ Command Palette", "(your keywords)", Icons.Star),
        new("extras", "Handy extras", "uuid, password, ip, flip a coin, roll a dice", "password 20", Icons.Dice),
    };
}
