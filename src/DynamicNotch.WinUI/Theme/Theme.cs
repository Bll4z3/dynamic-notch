using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;

namespace DynamicNotch.Design;

/// <summary>
/// The single source of truth for the design system (same values as the WPF app). Every shape uses
/// <see cref="CornerRadius"/>. Colours are plain values usable from any thread; brushes and fonts are
/// XAML objects, so they're created once on the UI thread by <see cref="Initialize"/> and are also
/// published as app resources (see App.xaml.cs) for XAML to use.
/// </summary>
public static class Theme
{
    // ---- Shape -------------------------------------------------------------------------------
    /// <summary>The ONE corner radius used by every shape in the app (clamped to half the height of small shapes).</summary>
    public const double CornerRadius = 22;
    public static readonly Microsoft.UI.Xaml.CornerRadius Radius = new(CornerRadius);
    /// <summary>
    /// The ONE small radius, used only for the Ask screenshot thumbnail, clipboard tiles (and their
    /// overlay buttons) and the clipboard preview card. Pills don't use a fixed radius at all:
    /// their corners are always half their height.
    /// </summary>
    public const double CornerRadiusSmall = 11;
    public static readonly Microsoft.UI.Xaml.CornerRadius RadiusSmall = new(CornerRadiusSmall);
    /// <summary>How far the notch's continuous (squircle-style) corners extend beyond a circular arc (WPF only; see README).</summary>
    public const double CornerSmoothing = 0.6;

    // ---- Spacing scale (4 / 8 / 12 / 16 / 20) ------------------------------------------------
    public const double S1 = 4, S2 = 8, S3 = 12, S4 = 16, S5 = 20;
    public static readonly Thickness PadS1 = new(S1), PadS2 = new(S2), PadS3 = new(S3), PadS4 = new(S4);
    public static readonly GridLength GapS1 = new(S1), GapS2 = new(S2), GapS3 = new(S3), GapS4 = new(S4), GapS5 = new(S5);
    public static readonly Thickness GapLeftS1 = new(S1, 0, 0, 0), GapLeftS2 = new(S2, 0, 0, 0), GapLeftS3 = new(S3, 0, 0, 0);
    public static readonly Thickness GapRightS2 = new(0, 0, S2, 0);
    public static readonly Thickness GapTopS1 = new(0, S1 / 2, 0, 0), GapTopS2 = new(0, S2, 0, 0), GapTopS3 = new(0, S3, 0, 0), GapTopS4 = new(0, S4, 0, 0);
    /// <summary>Expanded notch content inset: generous sides, a little less at the top (under the bezel).</summary>
    public static readonly Thickness ExpandedPadding = new(S5, S3, S5, S3);
    public const double ExpandedContentHeight = ExpandedHeight - 2 * S3;
    /// <summary>Collapsed notch side inset (battery ring on the left, art on the right).</summary>
    public static readonly Thickness CollapsedPadding = new(S3, 0, S3, 0);
    public static readonly Thickness PillPadding = new(S3, S3, S4, S3);
    public static readonly Thickness CardPadding = new(S3 - 2, S2 + 2, S3 - 2, S3 - 2);
    public static readonly Thickness CardHeaderMargin = new(S1, 0, 0, S2);
    public const double CardHeaderHeight = 22;

    // ---- Icons (vector, Lucide set; see Icons.cs) --------------------------------------------
    public const double IconSmall = 14, IconMedium = 18, IconLarge = 22, IconHero = 28;
    /// <summary>Stroke width of outline icons in DIPs, constant at every icon size.</summary>
    public const double IconStroke = 1.7;

    // ---- Type: Inter (bundled) -----------------------------------------------------------------
    public const double FontCaption = 11, FontBody = 13, FontTitle = 16;
    public const double FontPillTitle = 14;
    /// <summary>Collapsed-notch clock and the larger expanded-header clock.</summary>
    public const double FontClock = 13, FontHeaderClock = 15, FontHeaderDate = 13;
    /// <summary>Music elapsed/remaining labels: bold, 2pt above caption size.</summary>
    public const double FontMusicTime = FontCaption + 2;

    // ---- Colours: neutral greys on black + one accent ----------------------------------------
    public static readonly Color NotchColor = Rgb(0, 0, 0);
    public static readonly Color AccentColor = Rgb(0x0A, 0x84, 0xFF);
    public static readonly Color PrimaryTextColor = Rgb(0xF5, 0xF5, 0xF7);
    public static readonly Color SecondaryTextColor = Rgb(0xA1, 0xA1, 0xA6);
    public static readonly Color TertiaryTextColor = Rgb(0x6E, 0x6E, 0x73);
    public static readonly Color CardColor = Rgb(0x10, 0x10, 0x12);
    public static readonly Color CardBorderColor = Argb(0x12, 0xFF, 0xFF, 0xFF);
    public static readonly Color TileColor = Rgb(0x1A, 0x1A, 0x1D);
    public static readonly Color TileBorderColor = Argb(0x16, 0xFF, 0xFF, 0xFF);
    public static readonly Color CardHoverColor = Argb(0x1C, 0xFF, 0xFF, 0xFF);
    public static readonly Color TrackColor = Argb(0x30, 0xFF, 0xFF, 0xFF);
    public static readonly Color DangerColor = Rgb(0xFF, 0x45, 0x3A);
    public static readonly Color SuccessColor = Rgb(0x30, 0xD1, 0x58);
    public static readonly Color DropHighlightColor = Argb(0x55, 0xFF, 0xFF, 0xFF);
    public static readonly Color DropZoneColor = Argb(0x40, 0xFF, 0xFF, 0xFF);
    public static readonly Color OverlayButtonColor = Argb(0xE6, 0x0C, 0x0C, 0x0E);
    /// <summary>1px inner highlight along the notch's sides and bottom (~8% white). The top edge stays pure black.</summary>
    public static readonly Color HighlightColor = Argb(0x14, 0xFF, 0xFF, 0xFF);
    /// <summary>Soft shadow under the notch (compositor drop shadow; fits inside <see cref="ShadowMargin"/>).</summary>
    public const double ShadowBlur = 16, ShadowOffsetY = 4, ShadowOpacity = 0.5;
    /// <summary>Extra clickable/drawn band under the expanded notch so its shadow isn't cut off.</summary>
    public const double ShadowMargin = 16;
    public const double DropZoneDash = 4, DropZoneThickness = 1.5;

    // ---- Battery ring ------------------------------------------------------------------------
    /// <summary>Above this % the ring is green; at/below <see cref="BatteryLowPercent"/> it is red; amber between.</summary>
    public const double BatteryHighPercent = 50, BatteryLowPercent = 20;
    public static readonly Color BatteryGreen = Rgb(0x32, 0xD7, 0x4B);
    public static readonly Color BatteryAmber = Rgb(0xFF, 0xB0, 0x20);
    public static readonly Color BatteryRed = Rgb(0xFF, 0x45, 0x3A);
    public const double NotchRingSize = 16, NotchRingThickness = 2;
    public const double HeaderRingSize = 20, HeaderRingThickness = 2;
    public const double DeviceRingSize = 26, DeviceRingThickness = 1.75;
    public const double RingNumberFontSize = 7.5;
    /// <summary>Width of one device slot in the connected-devices row (ring + spacing).</summary>
    public const double DeviceSlotWidth = DeviceRingSize + S2;

    // ---- Music page --------------------------------------------------------------------------
    public const double AlbumArtSize = 156, MiniArtSize = 20;
    public const double SeekThickness = 4, SeekThicknessHover = 7, SeekHandleSize = 12, SeekHeight = 18;
    public const double TimeColumnWidth = 48;
    public static readonly GridLength TimeColumn = new(TimeColumnWidth);
    public const double TransportButtonSize = 40, PlayButtonSize = 52;
    public static readonly Thickness TransportGap = new(S2, 0, S2, 0);
    public const double AccentMinLightness = 0.58, AccentMinSaturation = 0.35, AccentGlowOpacity = 0.32;

    // ---- Clipboard tiles ---------------------------------------------------------------------
    public const double ClipTileSize = 53;
    public static readonly Thickness ClipTileMargin = new(2);
    public const double ClipTileHoverScale = 1.08;
    public const double ClipNeighbourPush = 0;
    public static readonly Thickness ClipTileInsetMargin = new(5);
    public static readonly Thickness ClipSnippetMargin = new(7, 6, 7, 6);
    public const double ClipSnippetFontSize = 8, ClipSnippetLineHeight = 9.5;
    public const double OverlayButtonSize = 17, OverlayIconSize = 10;
    public const double ShelfTileWidth = 70, ShelfTileHeight = 74, ShelfIconSize = 32, ShelfNameFontSize = 10;

    // ---- Notch geometry (DIPs) ---------------------------------------------------------------
    public const double CollapsedWidth = 196, CollapsedHeight = 32;
    public const double CollapsedPlayingWidth = 252;
    public const double MinimalWidth = 150, MinimalHeight = 6;
    public const double ExpandedWidth = 680, ExpandedHeight = 312;
    /// <summary>The music page is a little shorter than the clipboard page (snug under the controls).</summary>
    public const double MusicExpandedHeight = ExpandedHeight - 14;
    public const double PillWidth = 400, PillHeight = 72, PillIconSize = 42;
    public const double HeaderHeight = 26, FooterHeight = 30;
    public static readonly GridLength HeaderRow = new(HeaderHeight), FooterRow = new(FooterHeight);
    /// <summary>Extra room around the notch inside the host window for the soft shadow.</summary>
    public const double ShadowRoom = 40;
    /// <summary>Window host size: big enough for the expanded notch plus its shoulders and shadow.</summary>
    public const double HostWidth = ExpandedWidth + 2 * CornerRadius + 2 * ShadowRoom, HostHeight = ExpandedMaxHeight + ShadowRoom;
    /// <summary>Near-invisible hover strip so the minimal (6px) notch is easy to reach.</summary>
    public const double HoverStripHeight = 14, HoverStripExtra = 60;

    // ---- Ask page (Claude) ---------------------------------------------------------------------
    public const double AskIdleWidth = 560, AskIdleHeight = 150;
    public const double AskThinkingWidth = 560, AskThinkingHeight = 130;
    public const double AskAnswerWidth = 600, AskAnswerMinHeight = 230, AskAnswerMaxHeight = 420;
    /// <summary>The tallest the expanded notch ever gets (the Ask answer); the host window is sized for it.</summary>
    public const double ExpandedMaxHeight = AskAnswerMaxHeight > ExpandedHeight ? AskAnswerMaxHeight : ExpandedHeight;
    public static readonly Thickness AskPadding = new(S4, S3, S4, S3);
    public const double AskInputMinHeight = 36, AskInputLineHeight = 18, AskInputMaxLines = 3;
    public const double AskInputMaxHeight = AskInputLineHeight * AskInputMaxLines + 18;
    public static readonly Thickness AskInputPadding = new(S3 + 2, S2, S3, S2);
    public static readonly Thickness AskChipMargin = new(0, 0, 0, S2), AskChipClose = new(0, -S1, -S1, 0);
    public const double AskAnswerFontSize = 14, AskAnswerLineHeight = 21; // ~1.5 line height: the answer is the main content
    /// <summary>Most the answer text area can take before it scrolls.</summary>
    public const double AskAnswerScrollMaxHeight = 280;
    public const double PillButtonHeight = 32;
    public static readonly Thickness PillButtonPadding = new(15, 0, 15, 0);
    public const double PillIconGap = 6;
    public const double MonoFontSize = 12;
    public static readonly Color PillButtonColor = Rgb(0xF5, 0xF5, 0xF7);
    public static readonly Color PillButtonTextColor = Rgb(0x0A, 0x0A, 0x0C);
    public static readonly Color QuietPillColor = Rgb(0x1C, 0x1C, 0x20);
    public static readonly Color PillLightenColor = Argb(0x1F, 0xFF, 0xFF, 0xFF);
    public static readonly Color InputColor = Rgb(0x16, 0x16, 0x19);
    public static readonly Color InlineCodeColor = Argb(0x24, 0xFF, 0xFF, 0xFF);

    // ---- Ask thumbnail (fits the captured image's shape) ------------------------------------------
    public const double ThumbMaxWidth = 150, ThumbMaxHeight = 88;          // attached-screenshot chip
    public const double ThumbHeaderMaxWidth = 180, ThumbHeaderMaxHeight = 96; // in the question block
    public const double ThumbPreviewMaxWidth = 420, ThumbPreviewMaxHeight = 280; // hover preview card
    /// <summary>Pixel size kept for screenshot previews (2x the preview card for high-DPI screens).</summary>
    public const int ScreenshotPreviewPixelsW = 840, ScreenshotPreviewPixelsH = 560;

    // ---- Ask conversation turns ----------------------------------------------------------------------
    public static readonly Color QuestionCardColor = Argb(0x14, 0xFF, 0xFF, 0xFF);
    public static readonly Color QuestionTextColor = Rgb(0xD4, 0xD4, 0xD9);
    public static readonly Thickness QuestionCardPadding = new(12, 10, 12, 10);
    public const double QuestionFontSize = 13, TurnOlderOpacity = 0.8, AnswerLogoSize = 16;
    public static readonly Thickness TurnGap = new(0, S3, 0, S3);
    public static readonly Thickness TurnBottom = new(0, 0, 0, S4);

    // ---- Region selection overlay ----------------------------------------------------------------
    public static readonly Color OverlayDimColor = Argb(0x73, 0, 0, 0);
    public static readonly Color SelectionFillColor = Argb(0x2E, 0x0A, 0x84, 0xFF);
    public const double SelectionStrokeThickness = 2, SelectionDash = 4;

    // ---- Settings window -------------------------------------------------------------------------
    public const double SettingsWidth = 640, SettingsHeight = 480, SettingsTabWidth = 180, SettingsTabHeight = 36;
    public static readonly Thickness SettingsPadding = new(S5);
    public static readonly Thickness SettingsTabPadding = new(S3, S4, S3, S4);
    public const double SettingsShadowRoom = 24;
    /// <summary>The window drags only from this band at the top of the card (the "Settings" heading and each tab's title).</summary>
    public const double SettingsDragBandHeight = 44;
    public const double SettingsFieldHeight = 36;
    public const double SettingsShadowBlur = 30, SettingsShadowOffsetY = 10, SettingsShadowOpacity = 0.6;

    // ---- Settings: Pages tab + switch -------------------------------------------------------------
    public const double SwitchWidth = 42, SwitchHeight = 24, SwitchThumbSize = 18, SwitchThumbInset = 3;
    public static readonly Color SwitchOnColor = Rgb(0xF5, 0xF5, 0xF7);
    public static readonly Color SwitchOffColor = Rgb(0x3A, 0x3A, 0x3E);
    public static readonly Color SwitchThumbOnColor = Rgb(0x0A, 0x0A, 0x0C);
    public static readonly Color SwitchThumbOffColor = Rgb(0xF5, 0xF5, 0xF7);
    public const double PageRowHeight = 56, PageIconTile = 36;
    public static readonly Thickness PageRowMargin = new(0, 0, 0, S2);
    public static readonly Thickness PageRowPadding = new(S3, S2, S3, S2);

    // ---- Music page album glow -------------------------------------------------------------------
    public const double GlowWidth = 600, GlowHeight = 380;
    public const double GlowMaxOpacity = 0.50, GlowPausedOpacity = 0.17;
    public const double GlowPulseScale = 0.04, GlowPulseOpacity = 0.12, GlowPulsePeriod = 4.5;
    public static readonly Color GlowNeutral = Rgb(0xE8, 0xE8, 0xEE);

    // ---- Clipboard preview card (follows the cursor) ------------------------------------------
    public const double PreviewImageMaxWidth = 320, PreviewImageMaxHeight = 220;
    public const double PreviewTextWidth = 340, PreviewTextFontSize = 14, PreviewTextLineHeight = 20;
    public const int PreviewMaxChars = 400;
    public static readonly Thickness PreviewPadding = new(S3);
    /// <summary>Space between the clipboard column and the card, and the card's minimum inset from the notch edge.</summary>
    public const double PreviewGap = 12, PreviewInset = 10;
    /// <summary>Card offset from the cursor: to the right and slightly below (flips left near the edge).</summary>
    public const double PreviewCursorOffsetX = 18, PreviewCursorOffsetY = 10;
    public const double PreviewShadowBlur = 28, PreviewShadowOffsetY = 8, PreviewShadowOpacity = 0.55;

    // ---- Page navigator --------------------------------------------------------------------------
    public static readonly Color NavAccentColor = Rgb(0xFF, 0x37, 0x5F);
    public static readonly Color NavDimColor = Argb(0x8C, 0xF5, 0xF5, 0xF7);
    public static readonly Color NavPillColor = Rgb(0x16, 0x16, 0x1A);
    public static readonly Color NavHighlightColor = Argb(0x24, 0xFF, 0xFF, 0xFF);
    public const double NavIconSize = 16, NavSlot = 30, NavPillHeight = 26, NavPillPadding = 6, NavHighlightSize = 24;
    public const double NavDot = 6, NavDotGap = 6, NavDotActiveExtra = 12;
    /// <summary>Extra hover area around the navigator so it's easy to hit.</summary>
    public const double NavHitPadding = 12;

    // ---- Gradient meters (usage rings / bars, focus timer) ----------------------------------------------
    /// <summary>Usage colour bands: calm blue→purple below <see cref="UsageWarnPercent"/>, amber to <see cref="UsageHotPercent"/>, red above.</summary>
    public const double UsageWarnPercent = 60, UsageHotPercent = 85;
    public static readonly Color UsageCalmA = Rgb(0x5B, 0x8D, 0xFF), UsageCalmB = Rgb(0xA8, 0x6B, 0xFF);
    public static readonly Color UsageWarnA = Rgb(0xFF, 0xC2, 0x3D), UsageWarnB = Rgb(0xFF, 0x8A, 0x3D);
    public static readonly Color UsageHotA = Rgb(0xFF, 0x6A, 0x4D), UsageHotB = Rgb(0xFF, 0x2D, 0x55);
    public static (Color A, Color B) UsageColors(double percent) =>
        percent >= UsageHotPercent ? (UsageHotA, UsageHotB) : percent >= UsageWarnPercent ? (UsageWarnA, UsageWarnB) : (UsageCalmA, UsageCalmB);
    public const double UsageRingSize = 78, UsageRingThickness = 7, UsageGlowSize = 150, UsageGlowOpacity = 0.30;
    public const double FontUsagePercent = 20, FontTimerDigits = 46;
    public const double MeterBarThickness = 4;
    public static readonly Thickness UsageCardPadding = new(S3 + 2, S3, S4, S3);

    // ---- Focus timer ---------------------------------------------------------------------------------
    /// <summary>Interval colours (also the type chips in Settings): focus = purple, short break = teal, long break = blue.</summary>
    public static readonly Color FocusA = Rgb(0x8B, 0x6C, 0xFF), FocusB = Rgb(0xE0, 0x6C, 0xFF);
    public static readonly Color ShortBreakA = Rgb(0x2D, 0xD4, 0xBF), ShortBreakB = Rgb(0x22, 0xC5, 0xEE);
    public static readonly Color LongBreakA = Rgb(0x4F, 0x8B, 0xFF), LongBreakB = Rgb(0x7C, 0xB4, 0xFF);
    public const double TimerSegmentHeight = 6, TimerSegmentGap = 6, TimerBreakDot = 5, TimerLeadDot = 10;
    public const double TimerControlHeight = 36, TimerPanelWidth = 330;
    public const double CollapsedTimerDot = 6;

    // ---- XAML objects (UI thread) ----------------------------------------------------------------
    public static SolidColorBrush NotchBrush { get; private set; } = null!;
    public static SolidColorBrush PrimaryText { get; private set; } = null!;
    public static SolidColorBrush SecondaryText { get; private set; } = null!;
    public static SolidColorBrush TertiaryText { get; private set; } = null!;
    public static SolidColorBrush TileBrush { get; private set; } = null!;
    public static SolidColorBrush TileBorderBrush { get; private set; } = null!;
    public static SolidColorBrush CardHoverBrush { get; private set; } = null!;
    public static SolidColorBrush TrackBrush { get; private set; } = null!;
    public static SolidColorBrush AccentBrush { get; private set; } = null!;
    public static SolidColorBrush TransparentBrush { get; private set; } = null!;
    public static SolidColorBrush CardBrush { get; private set; } = null!;
    public static SolidColorBrush CardBorderBrush { get; private set; } = null!;
    public static SolidColorBrush OverlayButtonBrush { get; private set; } = null!;
    public static SolidColorBrush SuccessBrush { get; private set; } = null!;
    public static SolidColorBrush HighlightBorderBrush { get; private set; } = null!;
    public static SolidColorBrush DangerBrush { get; private set; } = null!;
    public static SolidColorBrush DropZoneBrush { get; private set; } = null!;
    public static SolidColorBrush PillButtonBrush { get; private set; } = null!;
    public static SolidColorBrush PillButtonTextBrush { get; private set; } = null!;
    public static SolidColorBrush QuietPillBrush { get; private set; } = null!;
    public static SolidColorBrush PillLightenBrush { get; private set; } = null!;
    public static SolidColorBrush InputBrush { get; private set; } = null!;
    public static SolidColorBrush InlineCodeBrush { get; private set; } = null!;
    public static SolidColorBrush QuestionCardBrush { get; private set; } = null!;
    public static SolidColorBrush QuestionTextBrush { get; private set; } = null!;
    public static FontFamily MonoFont { get; private set; } = null!;

    /// <summary>Inter at each weight. The static Inter files are separate families per weight, so each weight gets its file.</summary>
    public static FontFamily Font { get; private set; } = null!;
    public static FontFamily FontMedium { get; private set; } = null!;
    public static FontFamily FontSemiBold { get; private set; } = null!;
    public static FontFamily FontBold { get; private set; } = null!;

    private static bool _initialized;

    /// <summary>Creates the brushes and fonts. Call once on the UI thread before any view is built.</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        NotchBrush = new SolidColorBrush(NotchColor);
        PrimaryText = new SolidColorBrush(PrimaryTextColor);
        SecondaryText = new SolidColorBrush(SecondaryTextColor);
        TertiaryText = new SolidColorBrush(TertiaryTextColor);
        TileBrush = new SolidColorBrush(TileColor);
        TileBorderBrush = new SolidColorBrush(TileBorderColor);
        CardHoverBrush = new SolidColorBrush(CardHoverColor);
        TrackBrush = new SolidColorBrush(TrackColor);
        AccentBrush = new SolidColorBrush(AccentColor);
        TransparentBrush = new SolidColorBrush(Argb(0, 0, 0, 0));
        CardBrush = new SolidColorBrush(CardColor);
        CardBorderBrush = new SolidColorBrush(CardBorderColor);
        OverlayButtonBrush = new SolidColorBrush(OverlayButtonColor);
        SuccessBrush = new SolidColorBrush(SuccessColor);
        HighlightBorderBrush = new SolidColorBrush(HighlightColor);
        DangerBrush = new SolidColorBrush(DangerColor);
        DropZoneBrush = new SolidColorBrush(DropZoneColor);
        PillButtonBrush = new SolidColorBrush(PillButtonColor);
        PillButtonTextBrush = new SolidColorBrush(PillButtonTextColor);
        QuietPillBrush = new SolidColorBrush(QuietPillColor);
        PillLightenBrush = new SolidColorBrush(PillLightenColor);
        InputBrush = new SolidColorBrush(InputColor);
        InlineCodeBrush = new SolidColorBrush(InlineCodeColor);
        QuestionCardBrush = new SolidColorBrush(QuestionCardColor);
        QuestionTextBrush = new SolidColorBrush(QuestionTextColor);
        MonoFont = new FontFamily("Cascadia Mono, Consolas");

        Font = InterFile("Regular");
        FontMedium = InterFile("Medium");
        FontSemiBold = InterFile("SemiBold");
        FontBold = InterFile("Bold");
    }

    private static FontFamily InterFile(string weight)
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", $"Inter-{weight}.ttf");
        // Fall back to the system UI font if the file is missing (it's copied next to the exe on build).
        return System.IO.File.Exists(path)
            ? new FontFamily($"ms-appx:///Assets/Fonts/Inter-{weight}.ttf#Inter, Segoe UI Variable Text, Segoe UI")
            : new FontFamily("Segoe UI Variable Text, Segoe UI");
    }

    /// <summary>The Inter file for a weight (Bold, SemiBold, Medium, else Regular).</summary>
    public static FontFamily FontFor(FontWeight w) =>
        w.Weight >= 700 ? FontBold : w.Weight >= 600 ? FontSemiBold : w.Weight >= 500 ? FontMedium : Font;

    public static Color BatteryColor(double percent) =>
        percent > BatteryHighPercent ? BatteryGreen : percent > BatteryLowPercent ? BatteryAmber : BatteryRed;

    public static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(0xFF, r, g, b);
    private static Color Argb(byte a, byte r, byte g, byte b) => Color.FromArgb(a, r, g, b);
}
