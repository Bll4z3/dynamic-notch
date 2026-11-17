using DynamicNotch.Design;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace DynamicNotch.Services;

public enum FontGroup { Bundled, System, Installed }

/// <summary>
/// One choosable UI font. Bundled fonts are app resources (Inter is four static files; the others are one
/// variable file each, so every weight comes from the same file); system / installed fonts are referred to
/// by family name. <see cref="Id"/> is what settings.json stores.
/// </summary>
public sealed record FontChoice(string Id, string Name, FontGroup Group, string Regular, string Medium, string SemiBold, string Bold)
{
    public FontFamily Family(string weight) => new(weight switch { "Medium" => Medium, "SemiBold" => SemiBold, "Bold" => Bold, _ => Regular });
    /// <summary>The family used to draw this font's own name in the picker.</summary>
    public FontFamily Preview => new(Regular);
}

/// <summary>
/// The app-wide UI font (Settings ▸ General). Applying one swaps <see cref="Theme"/>'s font set, re-publishes
/// the text styles and tells every open window to refresh its text (<see cref="FontChanged"/>), so the change
/// is live with no restart. Bold / SemiBold roles keep their weights (DirectWrite synthesises or falls back
/// when a family lacks one). Icons are vector paths, so they never change. If the chosen font's digits
/// aren't all the same width, numbers keep Inter's tabular digits (see <see cref="Theme.UiDigitsTabular"/>).
/// </summary>
public sealed class FontService
{
    public const string DefaultId = "bundled:Inter";
    private readonly SettingsService _settings;
    private List<string>? _installed;

    public FontService(SettingsService settings) => _settings = settings;

    /// <summary>Raised on the UI thread after a new font was applied, with the family sources it replaced (windows refresh their text).</summary>
    public event Action<ISet<string>>? FontChanged;

    public FontChoice Current { get; private set; } = null!;

    // ---- The curated list ------------------------------------------------------------------------------
    private static FontChoice Bundled(string file, string family, string? altFamily = null)
    {
        string src = $"ms-appx:///Assets/Fonts/{file}#{family}" + (altFamily != null ? $", ms-appx:///Assets/Fonts/{file}#{altFamily}" : "") + ", Segoe UI Variable Text, Segoe UI";
        return new FontChoice("bundled:" + family, family, FontGroup.Bundled, src, src, src, src);
    }

    private static FontChoice Inter()
    {
        string F(string w) => Theme.DefaultFontFor(w).Source;
        return new FontChoice(DefaultId, "Inter", FontGroup.Bundled, F("Regular"), F("Medium"), F("SemiBold"), F("Bold"));
    }

    private static FontChoice System(string family, string display) =>
        new("system:" + display, display, FontGroup.System, family, family, family, family);

    public IReadOnlyList<FontChoice> BundledFonts { get; } = new[]
    {
        Inter(),
        Bundled("Geist.ttf", "Geist"),
        Bundled("DMSans.ttf", "DM Sans", "DM Sans 14pt"),
        Bundled("Manrope.ttf", "Manrope"),
        Bundled("PlusJakartaSans.ttf", "Plus Jakarta Sans"),
        Bundled("Outfit.ttf", "Outfit"),
        Bundled("SpaceGrotesk.ttf", "Space Grotesk"),
    };

    private static readonly (string family, string display, string probe)[] SystemCandidates =
    {
        ("Segoe UI Variable Text", "Segoe UI Variable", "Segoe UI Variable"),
        ("Segoe UI", "Segoe UI", "Segoe UI"),
        ("Bahnschrift", "Bahnschrift", "Bahnschrift"),
        ("Arial", "Arial", "Arial"),
        ("Calibri", "Calibri", "Calibri"),
        ("Verdana", "Verdana", "Verdana"),
        ("Trebuchet MS", "Trebuchet MS", "Trebuchet MS"),
    };

    /// <summary>The curated system fonts that are actually installed on this PC.</summary>
    public IReadOnlyList<FontChoice> SystemFonts => SystemCandidates
        .Where(c => Installed.Any(i => i.StartsWith(c.probe, StringComparison.OrdinalIgnoreCase)))
        .Select(c => System(c.family, c.display)).ToList();

    /// <summary>Every font family installed on the PC (sorted, from DirectWrite through Win2D).</summary>
    public IReadOnlyList<string> Installed => _installed ??= LoadInstalled();

    private static List<string> LoadInstalled()
    {
        try
        {
            return Microsoft.Graphics.Canvas.Text.CanvasTextFormat.GetSystemFontFamilies()
                .Where(f => !string.IsNullOrWhiteSpace(f) && !f.StartsWith("@", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>Installed families matching <paramref name="query"/>, as choices (at most <paramref name="max"/>).</summary>
    public IReadOnlyList<FontChoice> SearchInstalled(string query, int max) => Installed
        .Where(f => string.IsNullOrWhiteSpace(query) || f.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
        .Where(f => !IsIconFont(f))
        .Take(max).Select(f => new FontChoice("installed:" + f, f, FontGroup.Installed, f, f, f, f)).ToList();

    private static bool IsIconFont(string f) =>
        f.Contains("Icons", StringComparison.OrdinalIgnoreCase) || f.Contains("Symbol", StringComparison.OrdinalIgnoreCase) ||
        f.Contains("MDL2", StringComparison.OrdinalIgnoreCase) || f.Contains("Webdings", StringComparison.OrdinalIgnoreCase) ||
        f.Contains("Wingdings", StringComparison.OrdinalIgnoreCase) || f.Contains("Marlett", StringComparison.OrdinalIgnoreCase);

    public FontChoice? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var hit = BundledFonts.FirstOrDefault(f => f.Id == id) ?? SystemFonts.FirstOrDefault(f => f.Id == id);
        if (hit != null) return hit;
        if (id.StartsWith("installed:", StringComparison.Ordinal))
        {
            string family = id["installed:".Length..];
            if (Installed.Contains(family, StringComparer.OrdinalIgnoreCase)) return new FontChoice(id, family, FontGroup.Installed, family, family, family, family);
        }
        return null;
    }

    // ---- Applying ---------------------------------------------------------------------------------------
    /// <summary>Applies the saved choice at start-up (before any view is built); unknown / uninstalled → default.</summary>
    public void ApplySaved()
    {
        var choice = Find(_settings.Current.FontId) ?? BundledFonts[0];
        SetTheme(choice);
    }

    /// <summary>Applies a font app-wide now and saves it.</summary>
    public void Apply(FontChoice choice)
    {
        if (Current?.Id == choice.Id) return;
        var previous = CurrentSources();
        SetTheme(choice);
        _settings.Update(s => s.FontId = choice.Id == DefaultId ? null : choice.Id);
        Styles.Register(Application.Current.Resources); // new elements pick up the new styles
        FontChanged?.Invoke(previous);
    }

    public void ResetToDefault() => Apply(BundledFonts[0]);

    private void SetTheme(FontChoice choice)
    {
        Current = choice;
        var regular = choice.Family("Regular");
        Theme.SetFonts(regular, choice.Family("Medium"), choice.Family("SemiBold"), choice.Family("Bold"), DigitsAreEven(regular));
    }

    /// <summary>
    /// True when all ten digits have the same advance width with tabular figures requested (the font has a
    /// working tnum feature, or its default digits are already equal-width). Measured with real text layout.
    /// </summary>
    public static bool DigitsAreEven(FontFamily family)
    {
        try
        {
            double? first = null;
            for (char d = '0'; d <= '9'; d++)
            {
                var tb = new TextBlock { Text = new string(d, 8), FontFamily = family, FontSize = 40 };
                Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
                tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                double w = tb.DesiredSize.Width;
                first ??= w;
                if (Math.Abs(w - first.Value) > 0.75) return false;
            }
            return true;
        }
        catch { return true; }
    }

    /// <summary>
    /// Re-applies the UI font to every text element under <paramref name="root"/> that uses one of the app's
    /// own fonts (monospace code and anything else keeps its family). Numbers that use tabular figures get
    /// the number font. Called by each window after <see cref="FontChanged"/>.
    /// </summary>
    public static void Refresh(DependencyObject root, ISet<string> previousSources)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            switch (child)
            {
                case TextBlock tb:
                    if (previousSources.Contains(tb.FontFamily?.Source ?? ""))
                        tb.FontFamily = Typography.GetNumeralAlignment(tb) == FontNumeralAlignment.Tabular ? Theme.NumberFontFor(tb.FontWeight) : Theme.FontFor(tb.FontWeight);
                    RefreshInlines(tb.Inlines, previousSources);
                    break;
                case RichTextBlock rtb when previousSources.Contains(rtb.FontFamily?.Source ?? ""):
                    rtb.FontFamily = Theme.FontFor(rtb.FontWeight);
                    break;
                case Control ctl when previousSources.Contains(ctl.FontFamily?.Source ?? ""):
                    ctl.FontFamily = Theme.FontFor(ctl.FontWeight);
                    break;
            }
            Refresh(child, previousSources);
        }
    }

    private static void RefreshInlines(InlineCollection inlines, ISet<string> previousSources)
    {
        foreach (var inline in inlines)
        {
            if (previousSources.Contains(inline.FontFamily?.Source ?? "")) inline.FontFamily = Theme.FontFor(inline.FontWeight);
            if (inline is Span span) RefreshInlines(span.Inlines, previousSources);
        }
    }

    /// <summary>The family sources the app's text currently uses (taken before a change, for <see cref="Refresh"/>).</summary>
    public static HashSet<string> CurrentSources()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in new[] { FontWeights.Normal, FontWeights.Medium, FontWeights.SemiBold, FontWeights.Bold })
        {
            set.Add(Theme.FontFor(w).Source);
            set.Add(Theme.NumberFontFor(w).Source);
        }
        foreach (var w in new[] { "Regular", "Medium", "SemiBold", "Bold" }) set.Add(Theme.DefaultFontFor(w).Source);
        set.Add("Segoe UI Variable Text, Segoe UI");
        set.Add("XamlAutoFontFamily");
        return set;
    }
}
