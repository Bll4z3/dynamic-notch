using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Xaml.Media;

namespace DynamicNotch;

/// <summary>
/// Live customisation on the notch (ThemeService): radius (the shape springs, the window region and hover
/// targets follow), border highlight and shadow, the album glow (on / intensity / colour source), the
/// collapsed visualiser style, notch scale / top offset / monitor, the collapsed notch's items and the
/// hover timings. Accent and tile radii reach the views through Theme's shared brushes and the radius walk.
/// </summary>
public sealed partial class NotchWindow
{
    private ThemeService? _theme;

    public void AttachTheme(ThemeService theme)
    {
        _theme = theme;
        theme.RegisterRoot(Root);
        theme.Changed += OnThemeChanged;
        ApplyTheme(ThemeService.Part.All, animate: false);
    }

    private void OnThemeChanged(ThemeService.Part parts) => ApplyTheme(parts, animate: true);

    private void ApplyTheme(ThemeService.Part parts, bool animate)
    {
        if (_theme == null) return;
        var s = _theme.Current;
        if (parts.HasFlag(ThemeService.Part.Radius))
        {
            _shape.SetRadius(Theme.CornerRadius, animate);
            UpdateHitArea();
            UpdateRegion();
        }
        if (parts.HasFlag(ThemeService.Part.Surface))
        {
            _shape.SetSurface(Theme.HighlightColor, Theme.ShadowOpacity);
        }
        if (parts.HasFlag(ThemeService.Part.Glow) || parts.HasFlag(ThemeService.Part.Accent))
        {
            UpdateGlowColor(animate);
            UpdateGlowLevel();
        }
        if (parts.HasFlag(ThemeService.Part.Visualizer))
        {
            Bars.Style = s.Visualizer;
            Bars.Colorful = s.VisualizerColor == "accent";
            UpdateState();
        }
        if (parts.HasFlag(ThemeService.Part.Layout)) PositionWindow();
        if (parts.HasFlag(ThemeService.Part.Collapsed))
        {
            NotchRing.Opacity = s.ShowBatteryRing ? 1 : 0;
            CollapsedTime.Opacity = s.ShowClock ? 1 : 0;
            ClockService.ClockFormat = s.ClockFormat;
            StatusViewModel.ShowDateCollapsed = s.ShowDate;
            Vm.Status.Refresh();
        }
        if (parts.HasFlag(ThemeService.Part.Behaviour))
        {
            _expandTimer.Interval = TimeSpan.FromMilliseconds(s.HoverDelayMs);
            _collapseTimer.Interval = TimeSpan.FromMilliseconds(s.CollapseDelayMs);
        }
    }

    /// <summary>Glow brightness scaled by the Notch look settings (0 when the glow is off).</summary>
    private double GlowScale => _theme is { } t ? (t.Current.GlowEnabled ? t.Current.GlowIntensity : 0) : 1;

    /// <summary>The glow colour: the album's, or the accent when the glow follows the accent.</summary>
    private Windows.UI.Color GlowColorFor(Windows.UI.Color album) =>
        _theme?.Current.GlowSource == "accent" ? _theme.ChosenAccent() : album;

    /// <summary>The scale the notch is drawn at (Settings ▸ Customisation ▸ Notch look).</summary>
    private double NotchScale => _theme?.Current.NotchScale ?? 1;
    private double TopOffset => _theme?.Current.TopOffset ?? 0;
    private bool UseMouseMonitor => _theme?.Current.Monitor == "mouse";

    private void ApplyNotchScale()
    {
        double s = NotchScale;
        Root.RenderTransform = s == 1 ? null : new ScaleTransform { ScaleX = s, ScaleY = s };
    }
}
