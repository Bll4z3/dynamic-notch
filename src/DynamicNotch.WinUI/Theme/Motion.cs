namespace DynamicNotch.Design;

/// <summary>Spring physics parameters (mass-spring-damper). Damping ratio &lt; 1 overshoots; = 1 is critically damped.</summary>
public readonly record struct SpringPreset(double Stiffness, double Damping, double Mass = 1.0)
{
    /// <summary>
    /// Designer-friendly spring: <paramref name="response"/> is roughly the period in seconds,
    /// <paramref name="dampingRatio"/> 1 = no overshoot, 0.8 ≈ 1.5% overshoot, 0.7 ≈ 4.6%.
    /// </summary>
    public static SpringPreset FromResponse(double response, double dampingRatio)
    {
        double w = 2 * Math.PI / response;
        return new SpringPreset(w * w, 2 * dampingRatio * w);
    }
}

/// <summary>All motion tuning lives here. Every animation in the app is one of these springs.</summary>
public static class Motion
{
    // ---- Live motion tokens (Settings ▸ Customisation ▸ Motion) -------------------------------
    /// <summary>smooth (as designed), snappy, bouncy, calm, reduced.</summary>
    public static string Feel { get; private set; } = "smooth";
    /// <summary>0.5x .. 1.5x: higher is faster.</summary>
    public static double Speed { get; private set; } = 1;
    /// <summary>Low power mode: shorter staggers, slower visualiser, no glow breathing.</summary>
    public static bool LowPower { get; set; }

    public static void SetFeel(string feel, double speed)
    {
        Feel = feel;
        Speed = Math.Clamp(speed, 0.5, 1.5);
    }

    /// <summary>
    /// Every spring passes through here when it is created or retargeted (SpringModel), so the feel and
    /// speed apply to the whole app at once. Smooth at 1x returns the preset unchanged.
    /// </summary>
    public static SpringPreset Tune(SpringPreset p)
    {
        if (Feel == "smooth" && Speed == 1) return p;
        double w = Math.Sqrt(p.Stiffness / p.Mass);
        double response = 2 * Math.PI / w, zeta = p.Damping / (2 * Math.Sqrt(p.Stiffness * p.Mass));
        switch (Feel)
        {
            case "snappy": response *= 0.72; zeta = Math.Min(1, zeta + 0.06); break;
            case "bouncy": response *= 0.95; zeta = Math.Max(0.42, zeta * 0.72); break;
            case "calm": response *= 1.35; zeta = Math.Max(zeta, 0.92); break;
            case "reduced": response *= 0.6; zeta = Math.Max(zeta, 1); break;
        }
        response /= Speed;
        return SpringPreset.FromResponse(response, zeta);
    }

    /// <summary>A stagger delay under the current speed / power mode.</summary>
    public static double StaggerScale => (LowPower ? 0.5 : 1) / Speed * (Feel == "reduced" ? 0.3 : 1);

    // ---- Core presets (softer than v1: higher damping, small overshoot, longer settle) ---------
    /// <summary>The notch container morph (collapsed ⇄ pill ⇄ expanded).</summary>
    public static readonly SpringPreset Notch = SpringPreset.FromResponse(0.52, 0.80);
    /// <summary>The leading axis of a notch morph (width when opening, height when closing): a little quicker, same damping.</summary>
    public static readonly SpringPreset NotchLead = SpringPreset.FromResponse(0.44, 0.80);

    /// <summary>Horizontal page slides and the page-dot indicator.</summary>
    public static readonly SpringPreset Page = SpringPreset.FromResponse(0.48, 0.86);

    /// <summary>Button press squash and release.</summary>
    public static readonly SpringPreset Press = SpringPreset.FromResponse(0.26, 0.72);

    /// <summary>Pills, list items appearing, album-art pop on track change.</summary>
    public static readonly SpringPreset Pop = SpringPreset.FromResponse(0.44, 0.70);

    /// <summary>Hover fills, tile lift, progress bar thickening.</summary>
    public static readonly SpringPreset Hover = SpringPreset.FromResponse(0.34, 0.78);

    // ---- Supporting presets ----------------------------------------------------------------
    /// <summary>Content fading/rising in (staggered entrances). Critically damped.</summary>
    public static readonly SpringPreset FadeIn = SpringPreset.FromResponse(0.38, 1.0);

    /// <summary>Content leaving: noticeably quicker than it arrived.</summary>
    public static readonly SpringPreset FadeOut = SpringPreset.FromResponse(0.18, 1.0);

    /// <summary>Visualiser bars: fast and lively.</summary>
    public static readonly SpringPreset Wobble = SpringPreset.FromResponse(0.24, 0.45);

    /// <summary>Battery ring fill and colour, music accent colour.</summary>
    public static readonly SpringPreset RingFill = SpringPreset.FromResponse(0.70, 0.95);

    /// <summary>The % number popping into a ring, and clock crossfades.</summary>
    public static readonly SpringPreset Reveal = SpringPreset.FromResponse(0.32, 0.86);

    /// <summary>Clipboard tile zoom on hover and the neighbour nudge.</summary>
    public static readonly SpringPreset TileHover = SpringPreset.FromResponse(0.36, 0.68);
    public static readonly SpringPreset TileNudge = SpringPreset.FromResponse(0.42, 0.85);

    /// <summary>Device icons sliding into new slots as the row reflows.</summary>
    public static readonly SpringPreset Reflow = SpringPreset.FromResponse(0.44, 0.86);

    // ---- Staggered entrances ---------------------------------------------------------------
    /// <summary>Delay between consecutive content blocks fading/rising in (ms).</summary>
    public const int StaggerStepMs = 30;
    /// <summary>How far content rises while it fades in (DIPs).</summary>
    public const double StaggerRise = 8;
    /// <summary>Content starts revealing once the container is this far through its morph.</summary>
    public const double ContentRevealThreshold = 0.35;
    /// <summary>Shared (matched) elements fade in across this slice of the morph.</summary>
    public const double MatchedFadeFrom = 0.25, MatchedFadeTo = 0.85;

    // ---- Behaviour timings / amounts -------------------------------------------------------
    public const int HoverExpandDelayMs = 60;
    public const int HoverCollapseDelayMs = 450;
    public const int PillDurationMs = 3000;
    public const double ButtonPressScale = 0.92;
    /// <summary>Pills start slightly small and grow to full size as they pop in.</summary>
    public const double PillStartScale = 0.9;
    /// <summary>Clipboard tile lift on hover (DIPs, upward).</summary>
    public const double TileLift = 3;
    /// <summary>Marching-ants speed of the shelf drop zone outline while a file hovers (DIPs/s).</summary>
    public const double DropZoneDashSpeed = 18;
    /// <summary>The collapsed visualiser is decorative: cap its redraws to save CPU on the see-through window.</summary>
    public const double VisualizerFps = 30;

    // ---- Ask page ----------------------------------------------------------------------------
    /// <summary>The notch resizing between Ask states (idle, thinking, answer) and as the answer grows.</summary>
    public static readonly SpringPreset AskResize = SpringPreset.FromResponse(0.5, 0.84);
    /// <summary>Each streamed chunk of the answer fading in.</summary>
    public static readonly SpringPreset StreamFade = SpringPreset.FromResponse(0.3, 1.0);
    /// <summary>"Claude is thinking…" pulse period (s).</summary>
    public const double ThinkingPulsePeriod = 1.4;
    /// <summary>Answer re-render coalescing while streaming (ms).</summary>
    public const int StreamRenderMs = 40;

    // ---- Album glow --------------------------------------------------------------------------
    /// <summary>Colour cross-fade between tracks (~600 ms, no overshoot).</summary>
    public static readonly SpringPreset GlowCrossfade = SpringPreset.FromResponse(0.6, 1.0);
    /// <summary>Dimming when paused / brightening when playing.</summary>
    public static readonly SpringPreset GlowDim = SpringPreset.FromResponse(0.55, 1.0);

    // ---- Clipboard preview card --------------------------------------------------------------
    /// <summary>The card springing out of the tile.</summary>
    public static readonly SpringPreset PreviewPop = SpringPreset.FromResponse(0.36, 0.76);
    /// <summary>The card gliding between tiles.</summary>
    public static readonly SpringPreset PreviewMove = SpringPreset.FromResponse(0.34, 0.9);
    public const int PreviewHoverDelayMs = 200;
    /// <summary>Grace period after leaving a tile, so moving to the next tile re-targets instead of closing.</summary>
    public const int PreviewCloseGraceMs = 140;
    public const double PreviewStartScale = 0.6;

    // ---- Settings window ---------------------------------------------------------------------
    // Short and ease-out (critically damped or close to it): settles in ~250 ms with no visible bounce.
    public static readonly SpringPreset WindowOpen = SpringPreset.FromResponse(0.3, 1.0);
    public static readonly SpringPreset TabHighlight = SpringPreset.FromResponse(0.28, 0.9);
    public const double WindowStartScale = 0.97;
    /// <summary>Settings: the tab line's trailing end (a little slower than TabHighlight, so the line stretches slightly between tabs) and how far the window rises as it opens.</summary>
    public static readonly SpringPreset TabTrail = SpringPreset.FromResponse(0.36, 1.0);
    public const double WindowRise = 8;

    // ---- Page navigator ------------------------------------------------------------------------------
    /// <summary>Dots ⇄ icons morph: soft with a small overshoot.</summary>
    public static readonly SpringPreset NavMorph = SpringPreset.FromResponse(0.42, 0.78);
    public const int NavStaggerMs = 25, NavLeaveDelayMs = 250;
    public const double NavIconHoverScale = 1.15, NavIconStartScale = 0.5;
    /// <summary>Second block of a new Ask turn (the answer) starts this long after the question block.</summary>
    public const int TurnAnswerDelayMs = 60;

    // ---- Preview card follows the cursor; settings switch ----------------------------------------
    /// <summary>Light and fast: the card keeps up with the cursor without visible lag or jitter.</summary>
    public static readonly SpringPreset PreviewFollow = SpringPreset.FromResponse(0.12, 1.0);
    public static readonly SpringPreset SwitchThumb = SpringPreset.FromResponse(0.28, 0.74);
    /// <summary>"Keep at least one page on": a quick sideways shake (low damping + impulse).</summary>
    public static readonly SpringPreset Shake = SpringPreset.FromResponse(0.16, 0.22);
    public const double ShakeImpulse = 420;
    /// <summary>Page dots re-flowing when a page is turned on/off.</summary>
    public static readonly SpringPreset DotsReflow = SpringPreset.FromResponse(0.4, 0.86);

    // ---- Usage page / focus timer ---------------------------------------------------------------------
    /// <summary>Content cross-fading between the usage page's states (loading, numbers, unavailable).</summary>
    public static readonly SpringPreset StateFade = SpringPreset.FromResponse(0.36, 1.0);
    /// <summary>Breathing period of the timer's leading dot (s).</summary>
    public const double TimerPulsePeriod = 1.8;
    /// <summary>Phase change: the digits block dips and springs back; the cue ring flashes for this long (ms).</summary>
    public static readonly SpringPreset PhaseChange = SpringPreset.FromResponse(0.5, 0.72);
    public const int PhaseCueMs = 1600;

    // ---- Activity pills --------------------------------------------------------------------------------
    /// <summary>The notch growing into an activity pill and springing between pill widths.</summary>
    public static readonly SpringPreset ActivityMorph = SpringPreset.FromResponse(0.46, 0.76);
    /// <summary>Progress / level fills (a transform, never a width change).</summary>
    public static readonly SpringPreset ActivityFill = SpringPreset.FromResponse(0.3, 0.92);
    /// <summary>The padlock shackle opening / closing with a small bounce, and the lock "click" pulse.</summary>
    public static readonly SpringPreset LockShackle = SpringPreset.FromResponse(0.38, 0.55);
    public static readonly SpringPreset LockClick = SpringPreset.FromResponse(0.2, 0.45);
    public const double LockClickImpulse = -2.2;
    /// <summary>How long each kind stays up before it dismisses itself (ms).</summary>
    public const int ActivitySliderMs = 1800, ActivityCapsMs = 1800, ActivityBatteryMs = 3000,
                     ActivityDownloadDoneMs = 3000, ActivityUpdateMs = 3000, ActivityDownloadIdleMs = 6000;
    /// <summary>A waiting lower-priority activity is dropped once it's this old.</summary>
    public const int ActivityStaleMs = 2000;
    /// <summary>Activity content blocks fade / rise in this far apart, and rise by this much.</summary>
    public const int ActivityStaggerMs = 25;
    public const double ActivityRise = 6;
    /// <summary>Percentages count to their new value over this long.</summary>
    public const int CountUpMs = 240;
    /// <summary>Charging halo breathing period (s), and the indeterminate download shimmer period (s).</summary>
    public const double ChargePulsePeriod = 2.4, ShimmerPeriod = 1.3;
    /// <summary>Caps Lock state is read this long after the key (the toggle has applied by then).</summary>
    public const int CapsReadDelayMs = 30;
    /// <summary>Download-folder changes are coalesced to at most one pill update per this many ms.</summary>
    public const int DownloadThrottleMs = 250;
    /// <summary>Battery ring colour change by level, and the quicker charging ⇄ level cross-fade (ms).</summary>
    public const int RingColorMs = 700, ChargeColorMs = 300;
    /// <summary>The charging / unplugged bolt pops in from this scale (with the Pop overshoot).</summary>
    public const double BoltPopFrom = 0.45;

    // ---- Notification history: clear all, undo, dismiss ------------------------------------------------
    /// <summary>A row leaving to the right (Clear all cascade, × dismiss) and coming back (Undo).</summary>
    public static readonly SpringPreset RowLeave = SpringPreset.FromResponse(0.26, 1.0);
    public static readonly SpringPreset RowEnter = SpringPreset.FromResponse(0.4, 0.82);
    /// <summary>Rows below a dismissed row springing up to close the gap; a dragged row springing back.</summary>
    public static readonly SpringPreset RowCollapse = SpringPreset.FromResponse(0.4, 0.84);
    public static readonly SpringPreset RowSnapBack = SpringPreset.FromResponse(0.34, 0.7);
    /// <summary>The bell's damped ring-wobble when the list empties (low damping + impulse ≈ ±12°).</summary>
    public static readonly SpringPreset BellWobble = SpringPreset.FromResponse(0.32, 0.22);
    public const double BellWobbleImpulse = 260;
    public const double RowLeaveDistance = 40, RowLeaveScale = 0.96;
    /// <summary>Clear-all cascade: delay per visible row, capped in total (ms).</summary>
    public const int RowStaggerMs = 25, RowStaggerCapMs = 300;
    /// <summary>The soft highlight sweeping down behind the leaving rows (ms).</summary>
    public const int SweepMs = 520;
    // ---- Quick-action popovers ------------------------------------------------------------------------------
    /// <summary>The popover springing out of its tile (and back).</summary>
    public static readonly SpringPreset PopoverOpen = SpringPreset.FromResponse(0.38, 0.84);
    /// <summary>Header pop-up (Notifications / Quick actions): how small it starts as it grows out of its icon, the panel swap spring and slide.</summary>
    public const double HeaderPopupStartScale = 0.55, HeaderPanelSlide = 14;
    /// <summary>Header pop-ups (Notifications, Quick actions, Ask, the command palette) slide in from the right this far, ease-out spring.</summary>
    public const double HeaderPopupSlide = 56;
    public static readonly SpringPreset HeaderPopupOpen = SpringPreset.FromResponse(0.34, 0.9);
    public static readonly SpringPreset HeaderPopupClose = SpringPreset.FromResponse(0.24, 1.0);
    public static readonly SpringPreset HeaderPanelSwap = SpringPreset.FromResponse(0.3, 0.92);

    // ---- Media controls ----
    /// <summary>Play ⇄ pause glyph morph, the next / previous nudge, a new track sliding in, the Like heart filling and popping.</summary>
    /// <summary>The seek bar glides over one playback-clock tick.</summary>
    public const int SeekGlideMs = 260;
    public static readonly SpringPreset PlayMorph = SpringPreset.FromResponse(0.32, 0.8);
    public static readonly SpringPreset Nudge = SpringPreset.FromResponse(0.32, 0.55);
    public static readonly SpringPreset TrackSlide = SpringPreset.FromResponse(0.42, 0.9);
    public static readonly SpringPreset HeartFill = SpringPreset.FromResponse(0.34, 0.72);
    public static readonly SpringPreset HeartPop = SpringPreset.FromResponse(0.42, 0.42);

    // ---- Command palette ----
    /// <summary>The selection highlight gliding between rows, rows fading / sliding in one after another (seconds apart).</summary>
    public static readonly SpringPreset PaletteHighlight = SpringPreset.FromResponse(0.22, 0.86);
    public static readonly SpringPreset PaletteRowIn = SpringPreset.FromResponse(0.24, 0.95);
    public const double PaletteRowStagger = 0.018;
    /// <summary>How long the "Copied" check / a launched app's icon pop show before the palette closes (ms).</summary>
    public const int PaletteCopiedMs = 420, PaletteLaunchMs = 170;
    /// <summary>The palette shrinks only after typing pauses this long (it grows at once), so the notch doesn't bounce per key.</summary>
    public const int PaletteShrinkDelayMs = 260;
    /// <summary>The palette's bottom glow settling back after a reaction.</summary>
    public static readonly SpringPreset PaletteGlow = SpringPreset.FromResponse(0.45, 0.85);

    // ---- Screenshot shelf ----
    /// <summary>A new screenshot drops into the preview: an underdamped spring, so it lands with a little bounce.</summary>
    public static readonly SpringPreset ShotDrop = SpringPreset.FromResponse(0.46, 0.62);
    public const double ShotDropDistance = 26;
    /// <summary>The action buttons slide in after it, one after another (seconds).</summary>
    public static readonly SpringPreset ShotAction = SpringPreset.FromResponse(0.32, 0.86);
    public const double ShotActionDelay = 0.14, ShotActionStagger = 0.045;
    /// <summary>How long a confirmation ("Copied") stays, and how long the notch stays open on its own after a new screenshot.</summary>
    public const int ShotToastMs = 1800, ShotPeekMs = 4500;
    /// <summary>Closing is quicker than opening and never overshoots.</summary>
    public static readonly SpringPreset PopoverClose = SpringPreset.FromResponse(0.22, 1.0);
    public const double PopoverStartScale = 0.6;
    /// <summary>A popup row fading / rising into place (open stagger, devices arriving).</summary>
    public static readonly SpringPreset PopRowEnter = SpringPreset.FromResponse(0.34, 0.88);
    public const double PopRowRise = 6;
    /// <summary>Only this many rows are staggered (the rest arrive with the last one).</summary>
    public const int PopRowStaggerMax = 8;
    /// <summary>Skeleton rows' opacity pulse period (s).</summary>
    public const double SkeletonPulsePeriod = 1.2;
    /// <summary>Popup rows arrive this far apart (ms); the spinner's period (s); Wi-Fi rescans while open (ms).</summary>
    public const int PopRowStaggerMs = 22;
    public const double SpinnerPeriod = 0.9;
    public const int WifiRescanMs = 10_000;

    /// <summary>Applying a new UI font: text dips to this opacity and cross-fades back in the new face.</summary>
    public const double FontSwapDip = 0.35;
    /// <summary>A dragged page row lifts to this scale.</summary>
    public const double RowLiftScale = 1.03;
    /// <summary>How long Undo stays available after Clear all (ms).</summary>
    public const int UndoWindowMs = 5000;
    /// <summary>Swipe to dismiss: distance (fraction of the row width) or fling speed (DIPs/s) that commits it.</summary>
    public const double SwipeCommitFraction = 0.35, SwipeFlingVelocity = 700, SwipeStartDistance = 6, SwipeLeftResistance = 0.25;
}
