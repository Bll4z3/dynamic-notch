using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// Music page album glow: a soft radial ellipse in the album art's most vibrant colour, anchored at the
/// notch's bottom centre behind the content (only its top half rises into view; the notch shape clips
/// the rest). Two ellipses cross-fade between track colours; the glow is brighter while playing,
/// dimmed while paused, and breathes slowly while playing. Its opacity also follows how open the notch
/// is and how close the pager is to the music page. All compositor: springs, a looping pulse and one
/// expression, so the glow costs the app nothing per frame.
/// </summary>
public sealed class AlbumGlow
{
    private readonly Compositor _c;
    private readonly CompositionPropertySet _g;
    private readonly CompSpring _level, _fade;
    private readonly CompositionColorGradientStop[] _stopsA, _stopsB;
    private readonly ContainerVisual _group;
    private bool _frontIsA = true, _hasColor, _pulsing;
    private Color _color;

    /// <param name="host">Element (laid out at the expanded layer's top-left, expanded width) that hosts the glow.</param>
    /// <param name="notch">The notch property set ("H", "Full", "Page").</param>
    public AlbumGlow(UIElement host, CompositionPropertySet notch)
    {
        _c = ElementCompositionPreview.GetElementVisual(host).Compositor;
        // The overlays' springs (Ask, the command palette) may not exist yet: the glow reads them below.
        foreach (var key in new[] { "AskOv", "PalOv" }) if (notch.TryGetScalar(key, out _) != CompositionGetValueStatus.Succeeded) notch.InsertScalar(key, 0);
        _g = _c.CreatePropertySet();
        _g.InsertScalar("Music", 0);
        _g.InsertScalar("FrontA", 1);
        _g.InsertScalar("Phase", 1);
        _g.InsertScalar("Pulsing", 0);
        _level = new CompSpring(_g, "Level", Motion.GlowDim, 0);
        _fade = new CompSpring(_g, "Fade", Motion.GlowCrossfade, 1);

        var root = _c.CreateContainerVisual();
        _group = _c.CreateContainerVisual();
        _group.Size = new Vector2((float)Theme.GlowWidth, (float)Theme.GlowHeight);
        // Ellipse centre on the music page's bottom edge, horizontally centred.
        _group.Offset = new Vector3((float)((Theme.ExpandedWidth - Theme.GlowWidth) / 2), (float)(Theme.MusicExpandedHeight - Theme.GlowHeight / 2), 0);
        _group.CenterPoint = new Vector3(_group.Size / 2, 0);
        root.Children.InsertAtTop(_group);

        var a = Ellipse(out _stopsA);
        var b = Ellipse(out _stopsB);
        _group.Children.InsertAtTop(a);
        _group.Children.InsertAtTop(b);
        a.StartAnimation("Opacity", Expr("G.FrontA * Clamp(G.Fade, 0, 1) + (1 - G.FrontA) * (1 - Clamp(G.Fade, 0, 1))"));
        b.StartAnimation("Opacity", Expr("(1 - G.FrontA) * Clamp(G.Fade, 0, 1) + G.FrontA * (1 - Clamp(G.Fade, 0, 1))"));

        // Visible only on the music page, once the notch is open, at the level's brightness, with the pulse.
        string p = $"Clamp((N.H - {Anim.F(Theme.CollapsedHeight)}) / Max(1, N.Full - {Anim.F(Theme.CollapsedHeight)}), 0, 1)";
        string musicVisible = "(G.Music >= 0 ? 1 - Clamp(Abs(N.Page - G.Music), 0, 1) : 0)";
        string opened = Anim.SmoothStep(0.3, 1, p);
        string pulse = $"(1 - G.Pulsing * {Anim.F(Theme.GlowPulseOpacity)} * (1 - G.Phase))";
        // ...and it fades out while Ask or the command palette is over the page (they have their own clean background).
        string uncovered = "(1 - Clamp(Max(N.AskOv, N.PalOv), 0, 1))";
        root.StartAnimation("Opacity", Expr($"Clamp(G.Level, 0, 1) * {musicVisible} * {opened} * {pulse} * {uncovered}"));
        string scale = $"(1 + G.Pulsing * {Anim.F(Theme.GlowPulseScale)} * G.Phase)";
        _group.StartAnimation("Scale", Expr($"Vector3({scale}, {scale}, 1)"));

        ElementCompositionPreview.SetElementChildVisual(host, root);

        ExpressionAnimation Expr(string e)
        {
            var x = _c.CreateExpressionAnimation(e);
            x.SetReferenceParameter("G", _g);
            x.SetReferenceParameter("N", notch);
            return x;
        }
    }

    private SpriteVisual Ellipse(out CompositionColorGradientStop[] stops)
    {
        var brush = _c.CreateRadialGradientBrush();
        stops = new[] { _c.CreateColorGradientStop(0, default), _c.CreateColorGradientStop(0.45f, default), _c.CreateColorGradientStop(1, default) };
        foreach (var s in stops) brush.ColorStops.Add(s);
        var v = _c.CreateSpriteVisual();
        v.Size = _group.Size;
        v.Brush = brush;
        return v;
    }

    private static void Paint(CompositionColorGradientStop[] stops, Color c)
    {
        stops[0].Color = Color.FromArgb(0xFF, c.R, c.G, c.B);
        stops[1].Color = Color.FromArgb(0x70, c.R, c.G, c.B);
        stops[2].Color = Color.FromArgb(0x00, c.R, c.G, c.B);
    }

    /// <summary>Index of the music page among the enabled pages (-1 = switched off).</summary>
    public void SetMusicIndex(int index) => _g.InsertScalar("Music", index);

    /// <summary>New track colour: paints the hidden ellipse and cross-fades to it (~600 ms).</summary>
    public void SetColor(Color color, bool animate)
    {
        if (_hasColor && color == _color) return;
        _color = color;
        if (!animate || !_hasColor)
        {
            _hasColor = true;
            Paint(_frontIsA ? _stopsA : _stopsB, color);
            _fade.Snap(1);
            return;
        }
        _frontIsA = !_frontIsA;
        Paint(_frontIsA ? _stopsA : _stopsB, color);
        _g.InsertScalar("FrontA", _frontIsA ? 1 : 0);
        _fade.Snap(0);
        _fade.SetTarget(1, Motion.GlowCrossfade);
    }

    /// <summary>Brightness target: playing, paused or off.</summary>
    public void SetLevel(double level) => _level.SetTarget(level, Motion.GlowDim);

    /// <summary>The slow breathing while playing with the notch open (a looping compositor animation).</summary>
    public void SetPulsing(bool on)
    {
        on &= Anim.Enabled;
        if (on == _pulsing) return;
        _pulsing = on;
        _g.InsertScalar("Pulsing", on ? 1 : 0);
        if (!on) { _g.StopAnimation("Phase"); _g.InsertScalar("Phase", 1); return; }
        var a = _c.CreateScalarKeyFrameAnimation();
        var linear = _c.CreateLinearEasingFunction();
        const int keys = 120;
        for (int i = 0; i <= keys; i++)
        {
            double t = (double)i / keys;
            a.InsertKeyFrame((float)t, (float)(0.5 + 0.5 * Math.Sin(2 * Math.PI * t + Math.PI / 2)), linear); // starts at the top
        }
        a.Duration = TimeSpan.FromSeconds(Theme.GlowPulsePeriod);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        _g.StartAnimation("Phase", a);
    }
}
