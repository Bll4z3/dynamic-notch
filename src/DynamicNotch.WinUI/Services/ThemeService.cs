using System.Diagnostics;
using DynamicNotch.Design;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace DynamicNotch.Services;

/// <summary>
/// The single owner of the live visual tokens (Settings ▸ Customisation): accent (+ hover, subtle tint and
/// on-accent text), corner radii, motion feel / speed, surface strength (border highlight, shadow, glow),
/// the visualiser, notch scale / position, the collapsed notch's items and the hover timings. Views read
/// the tokens from <see cref="Theme"/> / <see cref="Motion"/>; this service changes them and tells
/// everything that caches a value:
/// - accent: shared brushes are cross-faded in place (~250 ms; a frame callback is attached only while
///   fading), composition brushes follow <see cref="Theme.AccentChanged"/>;
/// - radii: every registered window's tree is walked once (coalesced to one walk per frame) and only
///   corners that carry a radius token tag are rewritten (pills keep height / 2); the notch shape springs;
/// - everything else: <see cref="Changed"/> with the parts that changed.
/// All of it is persisted in settings.json ("Custom"), with debounced writes while sliders move.
/// </summary>
public sealed class ThemeService
{
    [Flags]
    public enum Part { None = 0, Accent = 1, Radius = 2, Motion = 4, Surface = 8, Glow = 16, Visualizer = 32, Layout = 64, Collapsed = 128, Behaviour = 256, Sounds = 512, Performance = 1024, All = 0x7FF }

    private readonly SettingsService _settings;
    private readonly List<WeakReference<FrameworkElement>> _roots = new();
    private readonly UISettings? _ui;
    private Color _fadeFrom, _fadeTo;
    private readonly Stopwatch _fadeClock = new();
    private bool _fading, _radiusWalkPending;
    private Color? _albumColor;
    private bool _musicShown;

    public ThemeService(SettingsService settings)
    {
        _settings = settings;
        try
        {
            _ui = new UISettings();
            _ui.ColorValuesChanged += (_, _) => Ui.Post(() => { if (Current.MatchWindowsAccent) ApplyAccent(animate: true); });
        }
        catch { _ui = null; }
    }

    public CustomSettings Current => _settings.Current.Custom;

    /// <summary>Raised (UI thread) after tokens changed; the argument says which parts.</summary>
    public event Action<Part>? Changed;

    /// <summary>Raised when a token-driven element should replay something (accent ripple, motion demo, sound bars).</summary>
    public event Action<Part>? Demo;

    // ---- Start-up ------------------------------------------------------------------------------------------
    /// <summary>Applies the saved tokens before any view is built (no animation).</summary>
    public void ApplySaved()
    {
        var s = Current;
        Theme.SetRadii(s.CornerRadius, EffectiveTileRadius(s));
        Motion.SetFeel(s.MotionFeel, s.MotionSpeed);
        Theme.HighlightStrength = s.Highlight;
        Theme.ShadowIntensity = s.ShadowIntensity;
        Theme.ApplyAccent(ResolveAccent());
    }

    /// <summary>A window (or preview) whose tree follows radius changes.</summary>
    public void RegisterRoot(FrameworkElement root)
    {
        _roots.RemoveAll(w => !w.TryGetTarget(out _));
        _roots.Add(new WeakReference<FrameworkElement>(root));
    }

    // ---- Changing tokens -----------------------------------------------------------------------------------
    /// <summary>
    /// Changes customisation values. <paramref name="continuous"/> = a slider being dragged (the file is
    /// written once it settles). Works out which parts changed and applies them.
    /// </summary>
    public void Update(Action<CustomSettings> change, bool continuous = false, bool animate = true)
    {
        var before = Current.Clone();
        void Apply(AppSettings a) { change(a.Custom); a.Custom.Sanitize(); }
        if (continuous) _settings.UpdateDeferred(Apply); else _settings.Update(Apply);
        ApplyDiff(before, Current, animate);
    }

    /// <summary>Replaces the whole customisation (Looks, reset, import, undo) and animates to it.</summary>
    public void ReplaceAll(CustomSettings next, bool animate = true)
    {
        var before = Current.Clone();
        var clean = next.Clone().Sanitize();
        _settings.Update(a => a.Custom = clean);
        ApplyDiff(before, Current, animate);
    }

    private void ApplyDiff(CustomSettings a, CustomSettings b, bool animate)
    {
        var parts = Part.None;
        if (a.AccentPreset != b.AccentPreset || a.CustomAccent != b.CustomAccent || a.MatchWindowsAccent != b.MatchWindowsAccent || a.FollowAlbumColor != b.FollowAlbumColor)
        { parts |= Part.Accent; ApplyAccent(animate); }
        if (a.CornerRadius != b.CornerRadius || a.TileRadius != b.TileRadius || a.LinkTileRadius != b.LinkTileRadius)
        { parts |= Part.Radius; Theme.SetRadii(b.CornerRadius, EffectiveTileRadius(b)); ScheduleRadiusWalk(); }
        if (a.MotionFeel != b.MotionFeel || a.MotionSpeed != b.MotionSpeed)
        { parts |= Part.Motion; Motion.SetFeel(b.MotionFeel, b.MotionSpeed); }
        if (a.Highlight != b.Highlight || a.ShadowIntensity != b.ShadowIntensity)
        {
            parts |= Part.Surface;
            Theme.HighlightStrength = b.Highlight;
            Theme.ShadowIntensity = b.ShadowIntensity;
            Theme.HighlightBorderBrush.Color = Theme.HighlightColor;
        }
        if (a.GlowEnabled != b.GlowEnabled || a.GlowIntensity != b.GlowIntensity || a.GlowSource != b.GlowSource) parts |= Part.Glow;
        if (a.Visualizer != b.Visualizer) parts |= Part.Visualizer;
        if (a.NotchScale != b.NotchScale || a.TopOffset != b.TopOffset || a.Monitor != b.Monitor) parts |= Part.Layout;
        if (a.ShowBatteryRing != b.ShowBatteryRing || a.ShowClock != b.ShowClock || a.ShowDate != b.ShowDate || a.ClockFormat != b.ClockFormat) parts |= Part.Collapsed;
        if (a.HoverDelayMs != b.HoverDelayMs || a.CollapseDelayMs != b.CollapseDelayMs) parts |= Part.Behaviour;
        if (a.SoundsEnabled != b.SoundsEnabled || a.SoundPreset != b.SoundPreset || a.SoundVolume != b.SoundVolume || !a.SoundEvents.SequenceEqual(b.SoundEvents)) parts |= Part.Sounds;
        if (a.PerformanceMode != b.PerformanceMode) parts |= Part.Performance;
        if (parts != Part.None) Changed?.Invoke(parts);
    }

    public void PlayDemo(Part part) => Demo?.Invoke(part);

    public static double EffectiveTileRadius(CustomSettings s) =>
        s.LinkTileRadius ? Math.Clamp(Math.Round(s.CornerRadius * 0.5), Theme.MinCornerRadiusSmall, Theme.MaxCornerRadiusSmall) : s.TileRadius;

    // ---- Accent --------------------------------------------------------------------------------------------
    /// <summary>The chosen accent (preset / custom / Windows), before the album override.</summary>
    public Color ChosenAccent()
    {
        var s = Current;
        if (s.MatchWindowsAccent && _ui != null)
        {
            try { return _ui.GetColorValue(UIColorType.Accent); } catch { }
        }
        if (s.AccentPreset == "custom" && ColorUtil.TryParse(s.CustomAccent, out var c)) return c;
        return AccentPresets.All.FirstOrDefault(p => p.Id == s.AccentPreset)?.Color ?? Theme.DefaultAccent;
    }

    private Color ResolveAccent() =>
        Current.FollowAlbumColor && _musicShown && _albumColor is { } album ? album : ChosenAccent();

    /// <summary>The music page reports its extracted album colour (null = no art) and whether it is on screen.</summary>
    public void SetAlbumColor(Color? color, bool musicShown)
    {
        if (_albumColor == color && _musicShown == musicShown) return;
        _albumColor = color;
        _musicShown = musicShown;
        if (Current.FollowAlbumColor) ApplyAccent(animate: true);
    }

    /// <summary>True when the accent is hard to see on the black notch (a small note warns about it).</summary>
    public static bool IsLowContrast(Color c) => Theme.Contrast(c, Theme.NotchColor) < 3.0;

    private void ApplyAccent(bool animate)
    {
        var target = ResolveAccent();
        if (!animate || !Animation.Anim.Enabled) { StopFade(); Theme.ApplyAccent(target); return; }
        _fadeFrom = Theme.AccentColor;
        _fadeTo = target;
        _fadeClock.Restart();
        if (!_fading) { _fading = true; CompositionTarget.Rendering += OnFadeFrame; }
    }

    private void OnFadeFrame(object? sender, object e)
    {
        double t = Math.Min(1, _fadeClock.Elapsed.TotalMilliseconds / 250.0);
        double eased = t * t * (3 - 2 * t);
        Theme.ApplyAccent(Theme.Lerp(_fadeFrom, _fadeTo, eased));
        if (t >= 1) StopFade();
    }

    private void StopFade()
    {
        if (!_fading) return;
        _fading = false;
        CompositionTarget.Rendering -= OnFadeFrame;
    }

    // ---- Radius --------------------------------------------------------------------------------------------
    /// <summary>Raised after the radius tokens changed and the trees were refreshed.</summary>
    public static event Action? RadiusChanged;

    private void ScheduleRadiusWalk()
    {
        if (_radiusWalkPending) return;
        _radiusWalkPending = true;
        EventHandler<object>? h = null;
        h = (_, _) =>
        {
            CompositionTarget.Rendering -= h;
            _radiusWalkPending = false;
            foreach (var w in _roots) if (w.TryGetTarget(out var root)) RefreshRadii(root);
            RadiusChanged?.Invoke();
        };
        CompositionTarget.Rendering += h;
    }

    /// <summary>Rewrites every token-tagged corner under <paramref name="root"/> to the current radius tokens.</summary>
    public static void RefreshRadii(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            switch (child)
            {
                case Border b: if (Map(b.CornerRadius, out var r1)) b.CornerRadius = r1; break;
                case Grid g: if (Map(g.CornerRadius, out var r2)) g.CornerRadius = r2; break;
                case StackPanel sp: if (Map(sp.CornerRadius, out var r3)) sp.CornerRadius = r3; break;
                case Control c: if (Map(c.CornerRadius, out var r4)) c.CornerRadius = r4; break;
                case ContentPresenter cp: if (Map(cp.CornerRadius, out var r5)) cp.CornerRadius = r5; break;
            }
            RefreshRadii(child);
        }
    }

    private static bool Map(CornerRadius r, out CornerRadius result)
    {
        bool any = false;
        double M(double v)
        {
            int tag = Theme.RadiusTagOf(v);
            if (tag == 0) return v;
            any = true;
            return tag == Theme.LargeTag ? Theme.Radius.TopLeft : Theme.RadiusSmall.TopLeft;
        }
        result = new CornerRadius(M(r.TopLeft), M(r.TopRight), M(r.BottomRight), M(r.BottomLeft));
        return any;
    }

    // ---- Performance mode ----------------------------------------------------------------------------------
    /// <summary>Low power when chosen, or (auto) on battery saver or in a remote desktop session.</summary>
    public bool ResolveLowPower(bool energySaverOn)
    {
        return Current.PerformanceMode switch
        {
            "low" => true,
            "standard" => false,
            _ => energySaverOn || IsRemoteSession(),
        };
    }

    /// <summary>SM_REMOTESESSION: true inside a Remote Desktop session.</summary>
    public static bool IsRemoteSession() => GetSystemMetrics(0x1000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
}
