using DynamicNotch.Design;

namespace DynamicNotch.Services;

public enum PageId { Music, Clipboard, Ask, Usage, Focus }

/// <summary>One notch page. <see cref="UseClaudeLogo"/> shows the Claude logo asset instead of <see cref="Icon"/> where it fits.</summary>
public sealed record PageDefinition(PageId Id, string Name, string Description, string Icon, bool UseClaudeLogo = false);

/// <summary>
/// The single list of notch pages and which are switched on. The notch navigation, the page dots and
/// the Settings "Pages" tab all read from here, so adding a page is one line in <see cref="All"/>
/// (plus its view). Enabled state is saved in settings.json. At least one page always stays on.
/// </summary>
public sealed class PageRegistry
{
    public static readonly IReadOnlyList<PageDefinition> All = new PageDefinition[]
    {
        new(PageId.Music, "Music Player", "Now playing, controls and the album glow", Icons.Music),
        new(PageId.Clipboard, "Clipboard + Shelf", "Recent copies and files you drop on the notch", Icons.Clipboard),
        new(PageId.Ask, "Ask Claude", "Ask anything or about your screen (Ctrl+Shift+Space)", Icons.Sparkle, UseClaudeLogo: true),
        new(PageId.Usage, "Claude Usage", "Your 5-hour and weekly plan limits", Icons.Gauge),
        new(PageId.Focus, "Focus Timer", "Pomodoro rounds with breaks", Icons.Timer),
    };

    private readonly SettingsService _settings;

    public PageRegistry(SettingsService settings)
    {
        _settings = settings;
        // A settings file that somehow turned everything off still shows the first page.
        if (Enabled.Count == 0) _settings.Update(s => s.DisabledPages.Clear());
    }

    /// <summary>Raised on the UI thread after a page is switched on or off.</summary>
    public event Action? Changed;

    public bool IsEnabled(PageId id) => !_settings.Current.DisabledPages.Contains(id.ToString());

    /// <summary>Enabled pages in display order.</summary>
    public IReadOnlyList<PageId> Enabled => All.Select(p => p.Id).Where(IsEnabled).ToList();

    /// <summary>Position of a page among the enabled pages, or -1 when it's off.</summary>
    public int IndexOf(PageId id)
    {
        var list = Enabled;
        for (int i = 0; i < list.Count; i++) if (list[i] == id) return i;
        return -1;
    }

    /// <summary>Switch a page on/off. Returns false (and changes nothing) when it would turn off the last page.</summary>
    public bool TrySetEnabled(PageId id, bool on)
    {
        if (IsEnabled(id) == on) return true;
        if (!on && Enabled.Count <= 1) return false;
        _settings.Update(s =>
        {
            if (on) s.DisabledPages.Remove(id.ToString());
            else s.DisabledPages.Add(id.ToString());
        });
        Changed?.Invoke();
        return true;
    }
}
