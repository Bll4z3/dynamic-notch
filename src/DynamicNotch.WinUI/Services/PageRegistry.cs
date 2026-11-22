using DynamicNotch.Design;

namespace DynamicNotch.Services;

/// <summary>Every feature of the open notch. Each can be a page, a header icon (a pop-up) or hidden (Settings ▸ Layout).</summary>
public enum PageId { Music, Clipboard, Usage, Focus, Screenshots, Ask, Notifications, QuickActions, Palette }

/// <summary>Where a feature lives: a swipeable page, an icon at the top right that opens it as a pop-up, or nowhere.</summary>
public enum Placement { Page, Icon, Hidden }

/// <summary>One entry of the saved layout (settings.json): a feature id (or "Settings") and its placement, in display order.</summary>
public sealed class LayoutItem
{
    public string Id { get; set; } = "";
    public Placement Placement { get; set; }
}

/// <summary>One feature. <see cref="UseClaudeLogo"/> shows the Claude logo asset instead of <see cref="Icon"/> where it fits.</summary>
public sealed record PageDefinition(PageId Id, string Name, string Description, string Icon, bool UseClaudeLogo = false);

/// <summary>
/// The notch's layout: which features are pages (and in what order), which are header icons (and in what
/// order) and which are hidden, saved as one ordered list (<see cref="AppSettings.Layout"/>). The pager, the
/// page dots, the header icons and Settings ▸ Layout all read from here. At least one feature always stays a
/// page, and Settings is always a header icon (it can move, never hide). Older settings (separate page and
/// header-icon lists) are carried over once.
/// </summary>
public sealed class PageRegistry
{
    public const string SettingsId = "Settings";

    public static readonly IReadOnlyList<PageDefinition> All = new PageDefinition[]
    {
        new(PageId.Music, "Media Player", "Now playing, controls and the album glow", Icons.Music),
        new(PageId.Clipboard, "Clipboard + Shelf", "Recent copies and files you drop on the notch", Icons.Clipboard),
        new(PageId.Usage, "Claude Usage", "Your 5-hour and weekly plan limits", Icons.Gauge),
        new(PageId.Focus, "Focus Timer", "Pomodoro rounds with breaks", Icons.Timer),
        new(PageId.Screenshots, "Screenshot Shelf", "Your latest screenshots: mark up, ask Claude, drag out", Icons.Screenshot),
        new(PageId.Ask, "Ask Claude", "Ask anything, or about your screen (Ctrl+Shift+Space)", Icons.Sparkle, UseClaudeLogo: true),
        new(PageId.Notifications, "Notifications", "Your notification history", Icons.Bell),
        new(PageId.QuickActions, "Quick Actions", "Wi-Fi, Bluetooth, toggles, volume and brightness", Icons.Sliders),
        new(PageId.Palette, "Command Palette", "Search apps, files, maths, conversions and more (Alt+Space)", Icons.Search),
    };

    private static readonly Dictionary<PageId, Placement> Defaults = new()
    {
        [PageId.Music] = Placement.Page, [PageId.Clipboard] = Placement.Page, [PageId.Usage] = Placement.Page,
        [PageId.Focus] = Placement.Page, [PageId.Screenshots] = Placement.Page,
        [PageId.Palette] = Placement.Icon, [PageId.Ask] = Placement.Icon, [PageId.Notifications] = Placement.Icon, [PageId.QuickActions] = Placement.Icon,
    };

    private readonly SettingsService _settings;

    public PageRegistry(SettingsService settings)
    {
        _settings = settings;
        if (Normalise(settings.Current)) settings.Update(_ => { });
    }

    /// <summary>Raised on the UI thread after the layout changed (a placement or the order).</summary>
    public event Action? Changed;

    private List<LayoutItem> Items => _settings.Current.Layout;

    /// <summary>Fills in / repairs the saved layout. Returns true when it changed (and needs saving).</summary>
    private static bool Normalise(AppSettings s)
    {
        bool changed = false;
        s.Layout ??= new();
        if (s.Layout.Count == 0)
        {
            // Carry over the old separate settings: the page order / switches, then the header icons.
            var oldPages = new[] { "Music", "Clipboard", "Usage", "Focus", "Screenshots" };
            var order = s.PageOrder.Where(oldPages.Contains).Concat(oldPages).Distinct().ToList();
            foreach (var id in order) s.Layout.Add(new LayoutItem { Id = id, Placement = s.DisabledPages.Contains(id) ? Placement.Hidden : Placement.Page });
            var iconMap = new Dictionary<string, string> { ["palette"] = "Palette", ["ask"] = "Ask", ["notifications"] = "Notifications", ["quick"] = "QuickActions", ["settings"] = SettingsId };
            var iconOrder = s.HeaderIconOrder.Where(iconMap.ContainsKey).Concat(iconMap.Keys).Distinct().ToList();
            foreach (var key in iconOrder)
                s.Layout.Add(new LayoutItem { Id = iconMap[key], Placement = key != "settings" && s.HiddenHeaderIcons.Contains(key) ? Placement.Hidden : Placement.Icon });
            s.PageOrder.Clear();
            s.DisabledPages.Clear();
            s.HeaderIconOrder.Clear();
            s.HiddenHeaderIcons.Clear();
            changed = true;
        }
        // Drop unknown / duplicate ids; add features the saved layout lacks (new in an update) with their defaults.
        var seen = new HashSet<string>();
        int before = s.Layout.Count;
        s.Layout.RemoveAll(i => !(i.Id == SettingsId || Enum.TryParse<PageId>(i.Id, out _)) || !seen.Add(i.Id));
        changed |= s.Layout.Count != before;
        foreach (var def in All)
            if (!seen.Contains(def.Id.ToString())) { s.Layout.Add(new LayoutItem { Id = def.Id.ToString(), Placement = Defaults[def.Id] }); changed = true; }
        var settings = s.Layout.FirstOrDefault(i => i.Id == SettingsId);
        if (settings == null) { s.Layout.Add(new LayoutItem { Id = SettingsId, Placement = Placement.Icon }); changed = true; }
        else if (settings.Placement != Placement.Icon) { settings.Placement = Placement.Icon; changed = true; }
        if (!s.Layout.Any(i => i.Placement == Placement.Page && i.Id != SettingsId))
        {
            var music = s.Layout.First(i => i.Id == nameof(PageId.Music));
            music.Placement = Placement.Page;
            changed = true;
        }
        return changed;
    }

    public static PageDefinition Def(PageId id) => All.First(p => p.Id == id);

    public Placement PlacementOf(PageId id) => Items.FirstOrDefault(i => i.Id == id.ToString())?.Placement ?? Placement.Hidden;

    /// <summary>A page (in the pager).</summary>
    public bool IsEnabled(PageId id) => PlacementOf(id) == Placement.Page;

    /// <summary>Shown somewhere (a page or a header icon).</summary>
    public bool IsShown(PageId id) => PlacementOf(id) != Placement.Hidden;

    /// <summary>Every feature in layout order.</summary>
    public IReadOnlyList<PageDefinition> Ordered =>
        Items.Where(i => i.Id != SettingsId).Select(i => Def(Enum.Parse<PageId>(i.Id))).ToList();

    /// <summary>The pages, in order.</summary>
    public IReadOnlyList<PageId> Enabled => Items.Where(i => i.Placement == Placement.Page && i.Id != SettingsId).Select(i => Enum.Parse<PageId>(i.Id)).ToList();

    /// <summary>The header icons, left to right: feature ids and <see cref="SettingsId"/>.</summary>
    public IReadOnlyList<string> IconIds => Items.Where(i => i.Placement == Placement.Icon).Select(i => i.Id).ToList();

    /// <summary>Position of a page among the pages, or -1 when it isn't one.</summary>
    public int IndexOf(PageId id)
    {
        var list = Enabled;
        for (int i = 0; i < list.Count; i++) if (list[i] == id) return i;
        return -1;
    }

    /// <summary>Changes where a feature lives. False (nothing changes) when it would leave no pages. It goes to the end of its new group.</summary>
    public bool SetPlacement(PageId id, Placement placement)
    {
        if (PlacementOf(id) == placement) return true;
        if (placement != Placement.Page && IsEnabled(id) && Enabled.Count <= 1) return false;
        _settings.Update(s =>
        {
            var item = s.Layout.First(i => i.Id == id.ToString());
            s.Layout.Remove(item);
            item.Placement = placement;
            int last = s.Layout.FindLastIndex(i => i.Placement == placement);
            s.Layout.Insert(last + 1, item);
        });
        Changed?.Invoke();
        return true;
    }

    /// <summary>Moves a feature (or Settings) one place left / right among the others with the same placement.</summary>
    public void Shift(string id, int delta)
    {
        var item = Items.FirstOrDefault(i => i.Id == id);
        if (item == null) return;
        var group = Items.Where(i => i.Placement == item.Placement).ToList();
        int from = group.IndexOf(item), to = Math.Clamp(from + delta, 0, group.Count - 1);
        if (from == to) return;
        var other = group[to];
        _settings.Update(s =>
        {
            int a = s.Layout.IndexOf(item), b = s.Layout.IndexOf(other);
            (s.Layout[a], s.Layout[b]) = (s.Layout[b], s.Layout[a]);
        });
        Changed?.Invoke();
    }

    /// <summary>Moves a page to <paramref name="index"/> among the pages.</summary>
    public void Move(PageId id, int index)
    {
        int from = IndexOf(id);
        if (from < 0) return;
        index = Math.Clamp(index, 0, Enabled.Count - 1);
        while (from < index) { Shift(id.ToString(), +1); from++; }
        while (from > index) { Shift(id.ToString(), -1); from--; }
    }

    /// <summary>On = shown (a hidden feature becomes a page; an icon stays an icon); off = hidden. False when it's the last page.</summary>
    public bool TrySetEnabled(PageId id, bool on)
    {
        if (on) return IsShown(id) || SetPlacement(id, Placement.Page);
        return SetPlacement(id, Placement.Hidden);
    }
}
