using System.Windows;
using System.Windows.Media;

namespace DynamicNotch.Design;

/// <summary>
/// The single source of truth for the design system. Every shape in the app uses
/// <see cref="CornerRadius"/>; XAML reaches these values through {x:Static}.
/// </summary>
public static class Theme
{
    // ---- Shape -------------------------------------------------------------------------------
    /// <summary>The ONE corner radius used by every shape in the app (clamped to half the height of small shapes).</summary>
    public const double CornerRadius = 22;
    public static readonly System.Windows.CornerRadius Radius = new(CornerRadius);
    /// <summary>
    /// The ONE small radius, used only for the Ask screenshot thumbnail, clipboard tiles (and their
    /// overlay buttons) and the clipboard preview card. Pills don't use a fixed radius at all:
    /// their corners are always half their height (see PillRadiusConverter).
    /// </summary>
    public const double CornerRadiusSmall = 11;
    public static readonly System.Windows.CornerRadius RadiusSmall = new(CornerRadiusSmall);
    /// <summary>How far the notch's continuous (squircle-style) corners extend beyond a circular arc.</summary>
    public const double CornerSmoothing = 0.6;

    // ---- Spacing scale (4 / 8 / 12 / 16 / 20) ------------------------------------------------
    public const double S1 = 4, S2 = 8, S3 = 12, S4 = 16, S5 = 20;
    public static readonly Thickness PadS1 = new(S1);
    public static readonly Thickness PadS2 = new(S2);
    public static readonly Thickness PadS3 = new(S3);
    public static readonly Thickness PadS4 = new(S4);
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

    // ---- Type: Inter (bundled) with Segoe UI Variable as fallback -----------------------------
    public static readonly FontFamily Font =
        new(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Inter, Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = Font;
    public const double FontCaption = 11, FontBody = 13, FontTitle = 16;
    public const double FontPillTitle = 14;
    /// <summary>Collapsed-notch clock and the larger expanded-header clock.</summary>
    public const double FontClock = 13, FontHeaderClock = 15, FontHeaderDate = 13;
    /// <summary>Music elapsed/remaining labels: bold, 2pt above caption size.</summary>
    public const double FontMusicTime = FontCaption + 2;

    // ---- Colours: neutral greys on black + one accent ----------------------------------------
    public static readonly Color NotchColor = Color.FromRgb(0, 0, 0);
    public static readonly Color AccentColor = Color.FromRgb(0x0A, 0x84, 0xFF);
    public static readonly SolidColorBrush NotchBrush = Freeze(new SolidColorBrush(NotchColor));
    public static readonly SolidColorBrush PrimaryText = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)));
    public static readonly SolidColorBrush SecondaryText = Freeze(new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xA6)));
    public static readonly SolidColorBrush TertiaryText = Freeze(new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x73)));
    public static readonly SolidColorBrush CardBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)));
    public static readonly SolidColorBrush CardBorderBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)));
    public static readonly SolidColorBrush TileBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1D)));
    public static readonly SolidColorBrush TileBorderBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)));
    public static readonly SolidColorBrush CardHoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)));
    public static readonly SolidColorBrush TrackBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    public static readonly SolidColorBrush AccentBrush = Freeze(new SolidColorBrush(AccentColor));
    public static readonly SolidColorBrush DangerBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
    public static readonly SolidColorBrush SuccessBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58)));
    public static readonly SolidColorBrush DropHighlightBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)));
    public static readonly SolidColorBrush DropZoneBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
    public static readonly SolidColorBrush OverlayButtonBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x0C, 0x0C, 0x0E)));
    /// <summary>1px inner highlight along the notch's sides and bottom (~8% white). The top edge stays pure black.</summary>
    public static readonly Color HighlightColor = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);
    /// <summary>Soft layered shadow under the notch: layer count, spread per layer and alpha per layer.</summary>
    public const int ShadowLayers = 7;
    public const double ShadowSpread = 2.5;
    public const byte ShadowAlpha = 0x0C;
    public const double DropZoneDash = 4, DropZoneThickness = 1.5;

    // ---- Battery ring ------------------------------------------------------------------------
    /// <summary>Above this % the ring is green; at/below <see cref="BatteryLowPercent"/> it is red; amber between.</summary>
    public const double BatteryHighPercent = 50, BatteryLowPercent = 20;
    public static readonly Color BatteryGreen = Color.FromRgb(0x32, 0xD7, 0x4B);
    public static readonly Color BatteryAmber = Color.FromRgb(0xFF, 0xB0, 0x20);
    public static readonly Color BatteryRed = Color.FromRgb(0xFF, 0x45, 0x3A);
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
    /// <summary>Horizontal-only gap around play/pause, so the details column stays vertically symmetric.</summary>
    public static readonly Thickness TransportGap = new(S2, 0, S2, 0);
    /// <summary>Album-art accent: minimum lightness/saturation so derived colours stay readable on black.</summary>
    public const double AccentMinLightness = 0.58, AccentMinSaturation = 0.35, AccentGlowOpacity = 0.32;

    // ---- Clipboard tiles ---------------------------------------------------------------------
    public const double ClipTileSize = 53;
    public static readonly Thickness ClipTileMargin = new(2); // 5 x (53 + 4) fits the column even after DPI layout rounding
    public const double ClipTileHoverScale = 1.08;
    /// <summary>How far (DIPs) neighbouring tiles are nudged away from a hovered tile.</summary>
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
    public const double AskChipSize = 40, AskChipWidth = 60, AskThumbSize = 44, AskQuestionMaxHeight = 36;
    public static readonly Thickness AskChipMargin = new(0, 0, 0, S2), AskChipClose = new(0, -S1, -S1, 0);
    public const double AskAnswerFontSize = 14, AskAnswerLineHeight = 21; // ~1.5 line height: the answer is the main content
    /// <summary>Most the answer text area can take before it scrolls.</summary>
    public const double AskAnswerScrollMaxHeight = 280;
    public const double ScrollbarWidth = 3;
    public const double PillButtonHeight = 32;
    public static readonly Thickness PillButtonPadding = new(15, 0, 15, 0);
    public const double PillIconGap = 6;
    public static readonly Thickness PillIconMargin = new(0, 0, PillIconGap, 0);
    public static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");
    public const double MonoFontSize = 12;
    public static readonly SolidColorBrush PillButtonBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)));
    public static readonly SolidColorBrush PillButtonTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C)));
    public static readonly SolidColorBrush PillHoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0, 0, 0)));
    public static readonly SolidColorBrush InputBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x19)));
    public static readonly SolidColorBrush InlineCodeBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
    public static readonly SolidColorBrush HighlightBorderBrush = Freeze(new SolidColorBrush(HighlightColor));

    // ---- Region selection overlay ----------------------------------------------------------------
    public static readonly SolidColorBrush OverlayDimBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0)));
    public static readonly SolidColorBrush SelectionStrokeBrush = Freeze(new SolidColorBrush(AccentColor));
    public static readonly SolidColorBrush SelectionFillBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x2E, 0x0A, 0x84, 0xFF)));
    public const double SelectionStrokeThickness = 2, SelectionDash = 4;

    // ---- Music page album glow -------------------------------------------------------------------
    public const double GlowWidth = 600, GlowHeight = 380;
    /// <summary>Peak opacity while playing (keeps text readable), and the dimmed level while paused.</summary>
    public const double GlowMaxOpacity = 0.50, GlowPausedOpacity = 0.17;
    /// <summary>Breathing pulse while playing: scale and opacity swing, period in seconds, redraw rate.</summary>
    public const double GlowPulseScale = 0.04, GlowPulseOpacity = 0.12, GlowPulsePeriod = 4.5, GlowPulseFps = 30;
    public static readonly Color GlowNeutral = Color.FromRgb(0xE8, 0xE8, 0xEE);

    // ---- Clipboard preview card ------------------------------------------------------------------
    public const double PreviewImageMaxWidth = 320, PreviewImageMaxHeight = 220;
    public const double PreviewTextWidth = 340, PreviewTextFontSize = 14, PreviewTextLineHeight = 20;
    public const int PreviewMaxChars = 400;
    public static readonly Thickness PreviewPadding = new(S3);
    /// <summary>Space between the clipboard column and the card, and the card's minimum inset from the notch edge.</summary>
    public const double PreviewGap = 12, PreviewInset = 10;

    // ---- Settings window -------------------------------------------------------------------------
    public const double SettingsWidth = 640, SettingsHeight = 420, SettingsTabWidth = 180, SettingsTabHeight = 36;
    public static readonly Thickness SettingsPadding = new(S5);
    public static readonly Thickness SettingsTabPadding = new(S3, S4, S3, S4);
    public const double SettingsShadowRoom = 24;
    public const double SettingsFieldHeight = 36;
    public static readonly Thickness SettingsShadowMargin = new(SettingsShadowRoom);
    public static readonly GridLength SettingsTabColumn = new(SettingsTabWidth);

    // ---- Ask thumbnail (fits the captured image's shape) ------------------------------------------
    public const double ThumbMaxWidth = 150, ThumbMaxHeight = 88;          // attached-screenshot chip
    public const double ThumbHeaderMaxWidth = 180, ThumbHeaderMaxHeight = 96; // in the question block
    public const double ThumbPreviewMaxWidth = 420, ThumbPreviewMaxHeight = 280; // hover preview card
    /// <summary>Pixel size kept for screenshot previews (2x the preview card for high-DPI screens).</summary>
    public const int ScreenshotPreviewPixelsW = 840, ScreenshotPreviewPixelsH = 560;
    public static readonly SolidColorBrush QuietPillBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x20)));
    public static readonly SolidColorBrush PillLightenBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)));

    // ---- Clipboard preview follows the cursor ---------------------------------------------------
    /// <summary>Card offset from the cursor: to the right and slightly below (flips left near the edge).</summary>
    public const double PreviewCursorOffsetX = 18, PreviewCursorOffsetY = 10;

    // ---- Settings: Pages tab + switch -------------------------------------------------------------
    public const double SwitchWidth = 42, SwitchHeight = 24, SwitchThumbSize = 18, SwitchThumbInset = 3;
    public static readonly Color SwitchOnColor = Color.FromRgb(0xF5, 0xF5, 0xF7);
    public static readonly Color SwitchOffColor = Color.FromRgb(0x3A, 0x3A, 0x3E);
    public static readonly SolidColorBrush SwitchThumbOnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C)));
    public static readonly SolidColorBrush SwitchThumbOffBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)));
    public static readonly SolidColorBrush FocusRingBrush = Freeze(new SolidColorBrush(AccentColor));
    public const double PageRowHeight = 56, PageIconTile = 36;
    public static readonly Thickness PageRowMargin = new(0, 0, 0, S2);
    public static readonly Thickness PageRowPadding = new(S3, S2, S3, S2);

    // ---- Page navigator (dots that morph into page icons on hover) ---------------------------------
    public static readonly Color NavAccentColor = Color.FromRgb(0xFF, 0x37, 0x5F);
    public static readonly Color NavDimColor = Color.FromArgb(0x8C, 0xF5, 0xF5, 0xF7);
    public static readonly SolidColorBrush NavPillBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1A)));
    public static readonly SolidColorBrush NavHighlightBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));
    public const double NavIconSize = 16, NavSlot = 30, NavPillHeight = 26, NavPillPadding = 6, NavHighlightSize = 24;
    public const double NavDot = 6, NavDotGap = 6, NavDotActiveExtra = 12;
    /// <summary>Extra hover area around the navigator so it's easy to hit.</summary>
    public const double NavHitPadding = 12;

    // ---- Ask conversation turns ----------------------------------------------------------------------
    public static readonly SolidColorBrush QuestionCardBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)));
    public static readonly SolidColorBrush QuestionTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD9)));
    public static readonly Thickness QuestionCardPadding = new(12, 10, 12, 10);
    public const double QuestionFontSize = 13, TurnOlderOpacity = 0.8, AnswerLogoSize = 16;
    public static readonly Thickness TurnGap = new(0, S3, 0, S3);
    public static readonly Thickness TurnBottom = new(0, 0, 0, S4);

    public static Color BatteryColor(double percent) =>
        percent > BatteryHighPercent ? BatteryGreen : percent > BatteryLowPercent ? BatteryAmber : BatteryRed;

    public static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromArgb(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }
}
