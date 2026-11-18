using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace DynamicNotch.Design;

/// <summary>
/// The text styles, built from <see cref="Theme"/> (WinUI XAML has no x:Static, so instead of copying
/// the numbers into a .xaml file they're published from here as app resources; XAML uses them with
/// {StaticResource Name}). Same type scale as the WPF app: title (bold) / body / caption, tabular
/// numerals for every time.
/// </summary>
public static class Styles
{
    public static void Register(ResourceDictionary r)
    {
        // Tokens XAML may want directly.
        r["NotchBrush"] = Theme.NotchBrush;
        r["PrimaryTextBrush"] = Theme.PrimaryText;
        r["SecondaryTextBrush"] = Theme.SecondaryText;
        r["TertiaryTextBrush"] = Theme.TertiaryText;
        r["TileBrush"] = Theme.TileBrush;
        r["TileBorderBrush"] = Theme.TileBorderBrush;
        r["CardHoverBrush"] = Theme.CardHoverBrush;
        // The accent palette (shared brushes, mutated in place on every accent change / cross-fade frame).
        r["AccentBrush"] = Theme.AccentBrush;
        r["AccentLightBrush"] = Theme.AccentLightBrush;
        r["AccentDarkBrush"] = Theme.AccentDarkBrush;
        r["AccentGlowBrush"] = Theme.AccentGlowBrush;
        r["AccentTintBrush"] = Theme.AccentTintBrush;
        r["AccentBorderBrush"] = Theme.AccentBorderBrush;
        r["AccentReadableBrush"] = Theme.AccentReadableBrush;
        r["AccentGradientBrush"] = Theme.AccentGradientBrush;
        r["OnAccentBrush"] = Theme.OnAccentBrush;
        r["Radius"] = Theme.Radius;
        r["RadiusSmall"] = Theme.RadiusSmall;

        var baseText = Text(null, Theme.FontBody, FontWeights.Normal, Theme.PrimaryText, trim: true);
        r["BaseText"] = baseText;
        r["TitleText"] = Text(baseText, Theme.FontTitle, FontWeights.Bold, Theme.PrimaryText, trim: true);
        r["SecondaryText"] = Text(baseText, Theme.FontBody, FontWeights.Normal, Theme.SecondaryText, trim: true);
        r["CaptionText"] = Text(baseText, Theme.FontCaption, FontWeights.Normal, Theme.TertiaryText, trim: true);
        var section = Text(baseText, Theme.FontCaption, FontWeights.SemiBold, Theme.SecondaryText, trim: true);
        section.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        r["SectionText"] = section;
        var time = Text(baseText, Theme.FontCaption, FontWeights.Bold, Theme.SecondaryText, trim: false, tabular: true);
        r["TimeText"] = time;
        r["MusicTimeText"] = Text(time, Theme.FontMusicTime, FontWeights.Bold, Theme.SecondaryText, trim: false, tabular: true);
        var clock = Text(time, Theme.FontClock, FontWeights.Bold, Theme.PrimaryText, trim: false, tabular: true);
        clock.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        r["ClockTimeText"] = clock;
        r["HeaderTimeText"] = Text(clock, Theme.FontHeaderClock, FontWeights.Bold, Theme.PrimaryText, trim: false, tabular: true);
        r["HeaderDateText"] = Text(baseText, Theme.FontHeaderDate, FontWeights.Normal, Theme.SecondaryText, trim: false, tabular: true);

        // Pills
        r["PillAppText"] = Text(baseText, Theme.FontCaption, FontWeights.SemiBold, Theme.TertiaryText, trim: true);
        r["PillTitleText"] = Text(baseText, Theme.FontPillTitle, FontWeights.Bold, Theme.PrimaryText, trim: true);
        r["PillBodyText"] = Text(baseText, Theme.FontBody, FontWeights.Normal, Theme.SecondaryText, trim: true);
        r["BadgeText"] = Text(time, Theme.FontCaption, FontWeights.Bold, Theme.PrimaryText, trim: false, tabular: true);
    }

    private static Style Text(Style? basedOn, double size, FontWeight weight, Brush foreground, bool trim, bool tabular = false)
    {
        var s = new Style(typeof(TextBlock)) { BasedOn = basedOn };
        s.Setters.Add(new Setter(TextBlock.FontFamilyProperty, tabular ? Theme.NumberFontFor(weight) : Theme.FontFor(weight)));
        s.Setters.Add(new Setter(TextBlock.FontSizeProperty, size));
        s.Setters.Add(new Setter(TextBlock.FontWeightProperty, weight));
        s.Setters.Add(new Setter(TextBlock.ForegroundProperty, foreground));
        s.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, trim ? TextTrimming.CharacterEllipsis : TextTrimming.None));
        s.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
        s.Setters.Add(new Setter(TextBlock.IsHitTestVisibleProperty, false));
        if (tabular)
        {
            s.Setters.Add(new Setter(Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular));
            s.Setters.Add(new Setter(Typography.NumeralStyleProperty, FontNumeralStyle.Lining));
        }
        return s;
    }
}
