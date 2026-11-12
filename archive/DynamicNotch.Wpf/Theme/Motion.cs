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
    // ---- Core presets (softer than v1: higher damping, small overshoot, longer settle) ---------
    /// <summary>The notch container morph (collapsed ⇄ pill ⇄ expanded).</summary>
    public static readonly SpringPreset Notch = SpringPreset.FromResponse(0.52, 0.80);

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
    public static readonly SpringPreset WindowOpen = SpringPreset.FromResponse(0.36, 0.8);
    public static readonly SpringPreset TabHighlight = SpringPreset.FromResponse(0.38, 0.82);
    public const double WindowStartScale = 0.94;

    // ---- Blur with motion (radii in DIPs; 0 at rest, so settled content is always sharp) ------------
    /// <summary>Content blurring in/out during staggered entrances and Ask state changes.</summary>
    public const double EnterBlur = 10;
    /// <summary>Collapsed notch content and pills blurring as they fade.</summary>
    public const double CollapsedBlur = 6, PillBlur = 8;
    /// <summary>Clipboard preview card, list items and the Settings window springing in.</summary>
    public const double PreviewBlur = 8, ItemBlur = 6, WindowBlur = 12;
    /// <summary>Motion blur while pages slide: radius per page/second of pager speed, capped.</summary>
    public const double PageBlurPerVelocity = 1.4, PageBlurMax = 6;

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
}
