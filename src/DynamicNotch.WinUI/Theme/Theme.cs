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
    // ---- Shape (live tokens: Settings ▸ Customisation ▸ Shape) ---------------------------------
    /// <summary>Default notch corner radius, and the slider's range.</summary>
    public const double DefaultCornerRadius = 22, MinCornerRadius = 8, MaxCornerRadius = 40;
    /// <summary>Default tile / card radius (≈ 0.5 × the notch radius), and the slider's range.</summary>
    public const double DefaultCornerRadiusSmall = 11, MinCornerRadiusSmall = 4, MaxCornerRadiusSmall = 24;

    /// <summary>The ONE corner radius used by the notch and its large shapes (clamped to half the height of small shapes).</summary>
    public static double CornerRadius { get; private set; } = DefaultCornerRadius;
    /// <summary>
    /// The ONE small radius: tiles, rows, cards, popups, thumbnails. Pills and switches don't use a fixed
    /// radius at all: their corners are always half their height, and the sliders never touch them.
    /// </summary>
    public static double CornerRadiusSmall { get; private set; } = DefaultCornerRadiusSmall;

    // Every CornerRadius handed out by Radius / RadiusSmall carries an exact, invisible tag in its fraction
    // (1/512 or 2/512 of a pixel) so a live radius change can find exactly the shapes that use a token and
    // leave everything else (pills: height / 2, fixed-size chips) alone. See ThemeService.RefreshRadii.
    internal const double RadiusTagUnit = 1.0 / 512;
    internal const int LargeTag = 1, SmallTag = 2;
    public static Microsoft.UI.Xaml.CornerRadius Radius => new(CornerRadius + LargeTag * RadiusTagUnit);
    public static Microsoft.UI.Xaml.CornerRadius RadiusSmall => new(CornerRadiusSmall + SmallTag * RadiusTagUnit);

    /// <summary>Which token a corner value came from (0 = none).</summary>
    internal static int RadiusTagOf(double v)
    {
        double units = v / RadiusTagUnit;
        long whole = (long)Math.Round(units);
        if (Math.Abs(units - whole) > 1e-6) return 0;
        int t = (int)(whole % 512);
        return t is LargeTag or SmallTag ? t : 0;
    }

    internal static void SetRadii(double large, double small)
    {
        CornerRadius = Math.Round(Math.Clamp(large, MinCornerRadius, MaxCornerRadius));
        CornerRadiusSmall = Math.Round(Math.Clamp(small, MinCornerRadiusSmall, MaxCornerRadiusSmall));
    }

    /// <summary>How far the notch's continuous (squircle-style) corners extend beyond a circular arc.</summary>
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
    /// <summary>The default accent (pink: the navigator's original active colour).</summary>
    public static readonly Color DefaultAccent = Rgb(0xFF, 0x37, 0x5F);
    /// <summary>The live accent (Settings ▸ Customisation ▸ Accent). Interactive and selected states only;
    /// semantic colours (battery, charging, Caps Lock, error, success) never follow it.</summary>
    public static Color AccentColor { get; private set; } = DefaultAccent;
    /// <summary>Text / icons drawn on the accent (black or white, whichever has the better WCAG contrast).</summary>
    public static Color OnAccentColor { get; private set; } = Rgb(0xFF, 0xFF, 0xFF);
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
    /// <summary>1px inner highlight along the notch's sides and bottom (default ~8% white; live: Settings ▸ Notch look). The top edge stays pure black.</summary>
    public static Color HighlightColor => Argb((byte)Math.Round(255 * HighlightStrength), 0xFF, 0xFF, 0xFF);
    /// <summary>Border highlight strength, 0..0.2 (default 0.08).</summary>
    public static double HighlightStrength { get; internal set; } = DefaultHighlight;
    public const double DefaultHighlight = 0x14 / 255.0;
    /// <summary>Multiplies every shadow's opacity (0..1.5, default 1).</summary>
    public static double ShadowIntensity { get; internal set; } = 1;
    /// <summary>Soft shadow under the notch (compositor drop shadow; fits inside <see cref="ShadowMargin"/>).</summary>
    public const double ShadowBlur = 16, ShadowOffsetY = 4, BaseShadowOpacity = 0.5;
    public static double ShadowOpacity => BaseShadowOpacity * ShadowIntensity;
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
    /// <summary>Window host size: big enough for the expanded notch plus its shoulders (at the largest radius) and shadow.</summary>
    public const double HostWidth = ExpandedWidth + 2 * MaxCornerRadius + 2 * ShadowRoom, HostHeight = ExpandedMaxHeight + ShadowRoom;
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
    public const double SettingsWidth = 780, SettingsHeight = 540, SettingsMinWidth = 640, SettingsMinHeight = 420, SettingsTabWidth = 180, SettingsTabHeight = 36;
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
    /// <summary>Font picker (Settings ▸ General): option row height and the dropdown's tallest size.</summary>
    public const double FontItemHeight = 34, FontListMaxHeight = 280;
    /// <summary>Pages tab reordering: the grip's hit width, switched-off rows' dimming, the lifted row's shadow.</summary>
    public const double GripHitWidth = 26, DisabledRowOpacity = 0.5;
    public const double RowLiftShadowBlur = 18, RowLiftShadowOffsetY = 6, RowLiftShadowOpacity = 0.6;
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

    // ---- Activity pills (volume, brightness, Caps Lock, charging, downloads, updates) -----------------
    /// <summary>Height of the activity pill the collapsed notch grows into, and of its slim twin in the open notch.</summary>
    public const double ActivityHeight = 44, ActivityCompactHeight = 30;
    /// <summary>Pill width per kind (the notch springs between them when one activity replaces another).</summary>
    public const double ActivityWidthSlider = 320, ActivityWidthCaps = 300, ActivityWidthBattery = 330,
                        ActivityWidthDownload = 360, ActivityWidthUpdate = 340, ActivityCompactWidth = 216;
    public static readonly Thickness ActivityPadding = new(S4, 0, S4, 0), ActivityCompactPadding = new(S3, 0, S3, 0);
    public const double ActivityIconBox = 26, ActivityIconSize = 20, ActivityCompactIconSize = 16;
    public const double ActivityTrackHeight = 6, ActivityThumbSize = 14;
    public const double ActivityValueWidth = 44;
    public const double FontActivityTitle = 13, FontActivityValue = 14, FontActivityGlyph = 20;
    public static readonly Color VolumeColor = Rgb(0xF5, 0xF5, 0xF7);
    public static readonly Color VolumeMutedColor = Argb(0x59, 0xF5, 0xF5, 0xF7);
    public static readonly Color BrightnessColor = Rgb(0xFF, 0xC2, 0x4D);
    public static readonly Color CapsOnColor = Rgb(0xFF, 0xB0, 0x20);
    public static readonly Color ChargingColor = Rgb(0x32, 0xD7, 0x4B);
    public static readonly Color DownloadColor = Rgb(0x0A, 0x84, 0xFF);
    public static readonly Color UpdateColor = Rgb(0xA8, 0x6B, 0xFF);
    /// <summary>Soft coloured glow behind an activity icon (Caps Lock on, charging).</summary>
    public const double ActivityGlowSize = 44, ActivityGlowOpacity = 0.5, UnpluggedGlowOpacity = 0.18;
    /// <summary>The charging halo around the collapsed notch's battery ring (multiple of the ring size).</summary>
    public const double ChargeHaloScale = 1.9, ChargeHaloOpacity = 0.55;

    // ---- Page: Notifications + Quick actions -------------------------------------------------------
    public const double HubRowMinHeight = 46, HubRowIcon = 28, HubTileHeight = 52, HubSliderHeight = 28, HubHeaderHeight = 20;
    public static readonly Thickness HubRowPadding = new(S2, 6, S2, 6);
    public const int HubHistoryMax = 60;
    public const double HubRowGap = 6, HubSmallPillHeight = 24, ClearPillMinWidth = 78;
    /// <summary>The soft highlight that sweeps down behind rows as Clear all removes them.</summary>
    public const double SweepHeight = 90;
    public static readonly Color SweepColor = Argb(0x16, 0xFF, 0xFF, 0xFF);

    // ---- Quick-action popovers (Wi-Fi, Bluetooth) --------------------------------------------------------
    public static readonly Thickness PopoverPadding = new(S3, S3, S3, S2);
    public const double PopoverIconTile = 28, PopRowHeight = 44, PopoverDimOpacity = 0.55;
    public const double PopoverShadowBlur = 24, PopoverShadowOffsetY = 8, PopoverShadowOpacity = 0.7;
    /// <summary>The connected network / device row's faint fill.</summary>
    public static readonly Color PopRowActiveColor = Argb(0x14, 0xFF, 0xFF, 0xFF);
    public const double PasswordBoxHeight = 32;
    public static readonly Color TileActiveColor = Rgb(0xF5, 0xF5, 0xF7);
    public static readonly Color TileActiveIconColor = Rgb(0x0A, 0x0A, 0x0C);
    public const double FontTileLabel = 10;

    // ---- XAML objects (UI thread) ----------------------------------------------------------------
    public static SolidColorBrush NotchBrush { get; private set; } = null!;
    public static SolidColorBrush PrimaryText { get; private set; } = null!;
    public static SolidColorBrush SecondaryText { get; private set; } = null!;
    public static SolidColorBrush TertiaryText { get; private set; } = null!;
    public static SolidColorBrush TileBrush { get; private set; } = null!;
    public static SolidColorBrush TileBorderBrush { get; private set; } = null!;
    public static SolidColorBrush CardHoverBrush { get; private set; } = null!;
    public static SolidColorBrush TrackBrush { get; private set; } = null!;
    /// <summary>Shared accent brushes: mutated in place, so everything drawn with them follows a change live.</summary>
    public static SolidColorBrush AccentBrush { get; private set; } = null!;
    /// <summary>The accent, a little lighter (hover / pressed on accent surfaces).</summary>
    public static SolidColorBrush AccentHoverBrush { get; private set; } = null!;
    /// <summary>A low-alpha accent tint (selected rows, the Settings tab highlight, active-row fills).</summary>
    public static SolidColorBrush AccentSubtleBrush { get; private set; } = null!;
    /// <summary>Text / icons on the accent (auto black or white for WCAG AA contrast).</summary>
    public static SolidColorBrush OnAccentBrush { get; private set; } = null!;
    /// <summary>Raised (UI thread) whenever the accent colour changes, including every frame of a cross-fade.
    /// Composition brushes and cached colours update here; keep handlers trivial.</summary>
    public static event Action? AccentChanged;

    /// <summary>Applies an accent colour right now (ThemeService calls this per frame while cross-fading).</summary>
    public static void ApplyAccent(Color c)
    {
        AccentColor = c;
        OnAccentColor = OnColorFor(c);
        if (AccentBrush == null) return;
        AccentBrush.Color = c;
        AccentHoverBrush.Color = Lerp(c, Rgb(0xFF, 0xFF, 0xFF), 0.18);
        AccentSubtleBrush.Color = Color.FromArgb(0x2E, c.R, c.G, c.B);
        OnAccentBrush.Color = OnAccentColor;
        AccentChanged?.Invoke();
    }

    /// <summary>WCAG relative luminance (0..1).</summary>
    public static double Luminance(Color c)
    {
        static double Ch(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    public static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Black or white, whichever reads better on <paramref name="c"/>.</summary>
    public static Color OnColorFor(Color c) =>
        Contrast(c, Rgb(0x0A, 0x0A, 0x0C)) >= Contrast(c, Rgb(0xFF, 0xFF, 0xFF)) ? Rgb(0x0A, 0x0A, 0x0C) : Rgb(0xFF, 0xFF, 0xFF);
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
        AccentHoverBrush = new SolidColorBrush(AccentColor);
        AccentSubtleBrush = new SolidColorBrush(AccentColor);
        OnAccentBrush = new SolidColorBrush(OnAccentColor);
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
        ApplyAccent(AccentColor);

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

    /// <summary>The UI font's family for a weight (Bold, SemiBold, Medium, else Regular).</summary>
    public static FontFamily FontFor(FontWeight w) =>
        w.Weight >= 700 ? FontBold : w.Weight >= 600 ? FontSemiBold : w.Weight >= 500 ? FontMedium : Font;

    /// <summary>The default UI font (Inter, falling back to Segoe UI Variable) at a weight.</summary>
    public static FontFamily DefaultFontFor(string weight) => InterFile(weight);

    /// <summary>
    /// Whether the chosen UI font draws digits at a fixed width (its tabular figures, or digits that are
    /// equal-width by design). When it doesn't, numbers (clock, times, percentages) are drawn with the
    /// default font's tabular digits instead so they never jitter.
    /// </summary>
    public static bool UiDigitsTabular { get; private set; } = true;

    private static FontFamily? _numRegular, _numMedium, _numSemiBold, _numBold;

    /// <summary>The family used for numbers at a weight (the UI font, or Inter when it lacks tabular digits).</summary>
    public static FontFamily NumberFontFor(FontWeight w)
    {
        if (UiDigitsTabular) return FontFor(w);
        return w.Weight >= 700 ? _numBold ??= InterFile("Bold") : w.Weight >= 600 ? _numSemiBold ??= InterFile("SemiBold")
             : w.Weight >= 500 ? _numMedium ??= InterFile("Medium") : _numRegular ??= InterFile("Regular");
    }

    /// <summary>Replaces the UI font set (FontService). Views pick it up through Styles and a tree refresh.</summary>
    public static void SetFonts(FontFamily regular, FontFamily medium, FontFamily semiBold, FontFamily bold, bool digitsTabular)
    {
        Font = regular;
        FontMedium = medium;
        FontSemiBold = semiBold;
        FontBold = bold;
        UiDigitsTabular = digitsTabular;
    }

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
