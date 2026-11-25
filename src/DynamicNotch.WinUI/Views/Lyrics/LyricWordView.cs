using System.Globalization;
using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services.Lyrics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DynamicNotch.Views.Lyrics;

/// <summary>The colours a lyric is lit with: tinted by the album art palette.</summary>
public readonly record struct LyricColours(Color Lit, Color Edge, Color Glow)
{
    public static LyricColours Default => new(Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 255, 255, 255));

    /// <summary>A pastel of the palette's main colour for the fill (still reads as white-ish), the colour itself for the glow.</summary>
    public static LyricColours From(Services.GlowPalette? p)
    {
        if (p == null) return Default;
        static Color Lift(Color c, double t) => Color.FromArgb(255, (byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));
        var main = p.Main;
        if (p.Monochrome) return new(Color.FromArgb(255, 250, 250, 252), Color.FromArgb(255, 255, 255, 255), Color.FromArgb(255, 235, 235, 245));
        return new(Lift(main, 0.62), Color.FromArgb(255, 255, 255, 255), Lift(main, 0.18));
    }
}

/// <summary>
/// One sung word: the text (dim until sung) and, while its line is current, a lit copy on top that fills with light as
/// the word is sung, a smooth gradient wipe whose soft edge travels across it, with a soft glow in the album colour
/// around it while it's being sung. A held note sweeps slowly, its glow builds and pulses with the singer, and its
/// letters swell as the fill passes and wave gently. Lines far from the current one show a blurred copy for depth.
/// <para>
/// Everything moves on the compositor: the fill, glow and letters follow expressions over the lyrics clock ("C.T"), the
/// line's word times ("L.s{i}" / "L.e{i}", which the timing refinement can move) and the line's own values ("N.A"
/// active 0..1, "N.Bm" blur 0..1). The lit copy is the text's alpha mask seen through an animated gradient brush.
/// </para>
/// </summary>
internal sealed class LyricWordView : Grid
{
    // ---- Tuning ----
    private const float Feather = 0.45f;        // the wipe's soft edge, as a share of the font size
    private const float GlowBlur = 9;           // the glow around a word being sung (held words grow from here)
    private const float GlowStrength = 0.75f;
    private const float WordRise = 1.6f;        // a sung word rises this much (DIPs)
    private const float HeldSwell = 0.09f;      // a held letter swells this much as the fill passes it
    private const float HeldWave = 2.0f;        // and waves this far (DIPs)
    private const int MaxLetterSplit = 14;      // longer held words swell as a whole instead of letter by letter

    private readonly LyricWord _word;
    private readonly int _index;
    private readonly double _fontSize;
    private readonly bool _letters;
    private readonly List<TextBlock> _texts = new();
    private readonly Border _softHost = new() { IsHitTestVisible = false }, _litHost = new() { IsHitTestVisible = false };
    private readonly List<IDisposable> _owned = new();
    private readonly List<CompositionColorGradientStop[]> _stops = new();
    private ContainerVisual? _softRoot;
    private bool _active, _softBuilt;
    private CompositionPropertySet? _clock, _times, _line;

    public LyricWordView(LyricWord word, int index, FontFamily font, double size, bool backing)
    {
        _word = word;
        _index = index;
        _fontSize = size;
        IsHitTestVisible = false;
        Backing = backing;
        var letters = TextElements(word.Text);
        _letters = word.Held && letters.Count is >= 2 and <= MaxLetterSplit;
        Children.Add(_softHost);
        if (_letters)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var l in letters) { var tb = Text(l, font, size); _texts.Add(tb); row.Children.Add(tb); }
            Children.Add(row);
        }
        else
        {
            var tb = Text(word.Text, font, size);
            _texts.Add(tb);
            Children.Add(tb);
        }
        Children.Add(_litHost);
    }

    public LyricWord Word => _word;
    public bool Backing { get; }
    public bool IsActive => _active;

    private static TextBlock Text(string s, FontFamily font, double size) => new()
    {
        Text = s,
        FontFamily = font,
        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        FontSize = size,
        Foreground = Theme.PrimaryText,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.None,
        IsHitTestVisible = false,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        LineHeight = Math.Round(size * 1.28),
        TextLineBounds = TextLineBounds.Full,
        OpticalMarginAlignment = OpticalMarginAlignment.None,
    };

    private static List<string> TextElements(string s)
    {
        var list = new List<string>();
        var e = StringInfo.GetTextElementEnumerator(s);
        while (e.MoveNext()) list.Add(e.GetTextElement());
        return list;
    }

    // =================================================================================================================
    // Base text (always): dims while its line is current and not yet sung, rises a touch when sung
    // =================================================================================================================
    /// <summary>Binds the base text to the line's values (call once the word is in the tree; again after a re-layout).</summary>
    public void BindBase(CompositionPropertySet clock, CompositionPropertySet times, CompositionPropertySet line)
    {
        _clock = clock; _times = times; _line = line;
        float k = Backing ? 0.72f : 1f;
        string p = Progress;
        for (int j = 0; j < _texts.Count; j++)
        {
            var v = Anim.VisualOf(_texts[j], translation: true);
            v.StartAnimation("Opacity", Expr($"{Anim.F(k)} * (1 - 0.45 * N.A) * (1 - 0.85 * N.Bm)"));
            v.StartAnimation("Translation", Expr(_letters ? LetterTranslation(j) : $"Vector3(0, -{Anim.F(WordRise)} * N.A * Clamp({p} * 1.5, 0, 1), 0)"));
            if (_letters) v.StartAnimation("Scale", Expr(LetterScale(j)));
            else if (_word.Held) v.StartAnimation("Scale", Expr(HeldWordScale));
        }
    }

    private string Progress => $"Clamp((C.T - L.s{_index}) / Max(0.001, L.e{_index} - L.s{_index}), 0, 1)";

    /// <summary>1 while the word is being sung, easing off over ~0.45 s after it ends.</summary>
    private string Singing => $"(Clamp((C.T - L.s{_index}) * 8, 0, 1) * (1 - Clamp((C.T - L.e{_index}) / 0.45, 0, 1)))";

    private string LetterTranslation(int j)
    {
        int n = _texts.Count;
        return $"Vector3(0, -{Anim.F(WordRise)} * N.A * Clamp({Progress} * 1.5, 0, 1) - N.A * {Singing} * Clamp({Progress} * 3, 0, 1) * {Anim.F(HeldWave)} * Sin(C.T * 7.5 + {Anim.F(j * 0.8)}) * (0.6 + 0.6 * C.H), 0)";
    }

    private string LetterScale(int j)
    {
        int n = _texts.Count;
        string bump = $"Clamp(1 - Abs({Progress} * {n} - {Anim.F(j + 0.5)}) / 1.4, 0, 1)";
        string s = $"(1 + N.A * ({Anim.F(HeldSwell)} * {bump} + 0.03 * {Singing}))";
        return $"Vector3({s}, {s}, 1)";
    }

    private string HeldWordScale => $"Vector3(1 + 0.06 * N.A * {Singing} * {Progress}, 1 + 0.06 * N.A * {Singing} * {Progress}, 1)";

    private ExpressionAnimation Expr(string e)
    {
        var x = _clock!.Compositor.CreateExpressionAnimation(e);
        x.SetReferenceParameter("C", _clock);
        x.SetReferenceParameter("L", _times!);
        x.SetReferenceParameter("N", _line!);
        return x;
    }

    // =================================================================================================================
    // The lit copy and glow (only while the line is current or near it)
    // =================================================================================================================
    /// <summary>Builds the lit overlay and glow (needs the text laid out). A failure leaves the plain text, never an exception.</summary>
    public void Activate(LyricColours colours)
    {
        try { ActivateCore(colours); }
        catch (Exception ex) { App.LogError(ex); Deactivate(); }
    }

    private void ActivateCore(LyricColours colours)
    {
        if (_active || _clock == null || _texts[0].ActualWidth <= 0) return;
        _active = true;
        var c = _clock.Compositor;
        var root = c.CreateContainerVisual();
        _owned.Add(root);
        float k = Backing ? 0.85f : 1f;
        for (int j = 0; j < _texts.Count; j++)
        {
            var tb = _texts[j];
            var pos = _letters ? tb.ActualOffset : Vector3.Zero;
            var size = new Vector2((float)tb.ActualWidth, (float)tb.ActualHeight);
            CompositionBrush mask;
            // XAML owns the alpha mask (GetAlphaMask returns the same live brush every time): never dispose it.
            try { mask = tb.GetAlphaMask(); } catch { continue; }

            float feather = (float)(_fontSize * Feather) * (_letters ? 0.6f : 1f);
            var grad = c.CreateLinearGradientBrush();
            grad.MappingMode = CompositionMappingMode.Absolute;
            var stops = new[]
            {
                c.CreateColorGradientStop(0, colours.Lit),
                c.CreateColorGradientStop(0.55f, colours.Edge),
                c.CreateColorGradientStop(1, Color.FromArgb(0, colours.Edge.R, colours.Edge.G, colours.Edge.B)),
            };
            foreach (var s in stops) grad.ColorStops.Add(s);
            _stops.Add(stops);
            // The fill's edge: travels from just before the start to past the end over the word's (or letter's) part.
            string p = _letters ? $"Clamp({Progress} * {_texts.Count} - {j}, 0, 1)" : Progress;
            var edge = Expr($"Vector2({p} * (W + F) - F, 0)");
            edge.SetScalarParameter("W", size.X);
            edge.SetScalarParameter("F", feather);
            grad.StartAnimation("StartPoint", edge);
            var edgeEnd = Expr($"Vector2({p} * (W + F), 0)");
            edgeEnd.SetScalarParameter("W", size.X);
            edgeEnd.SetScalarParameter("F", feather);
            grad.StartAnimation("EndPoint", edgeEnd);

            var lit = c.CreateMaskBrush();
            lit.Source = grad;
            lit.Mask = mask;
            var sprite = c.CreateSpriteVisual();
            sprite.Size = size;
            sprite.Brush = lit;
            sprite.StartAnimation("Opacity", Expr($"{Anim.F(k)} * Clamp(N.A, 0, 1)"));
            _owned.Add(grad); _owned.Add(lit); _owned.Add(sprite);

            // The glow: the lit shape blurred in the album colour, while the word is sung (held notes build and pulse).
            try
            {
                var glow = c.CreateDropShadow();
                glow.Mask = lit;
                glow.Color = colours.Glow;
                glow.Offset = Vector3.Zero;
                if (_word.Held)
                {
                    glow.StartAnimation("BlurRadius", Expr($"{Anim.F(GlowBlur - 2)} + 10 * {Progress} + 4 * C.H"));
                    glow.StartAnimation("Opacity", Expr($"Clamp(N.A * {Singing} * (0.55 + 0.45 * {Progress}) * (0.75 + 0.4 * C.V), 0, 1)"));
                }
                else
                {
                    glow.BlurRadius = GlowBlur * (float)(_fontSize / 20);
                    glow.StartAnimation("Opacity", Expr($"Clamp(N.A * {Singing} * {Anim.F(GlowStrength * k)} * (0.7 + 0.3 * C.V), 0, 1)"));
                }
                sprite.Shadow = glow;
                _owned.Add(glow);
                _glows.Add(glow);
            }
            catch { /* no glow on this system: the fill still works */ }

            // Follow the base text exactly (rise, letter wave and swell).
            var group = c.CreateContainerVisual();
            group.Size = size;
            group.CenterPoint = new Vector3(size.X / 2, size.Y * (_letters ? 0.6f : 0.85f), 0);
            var offset = Expr(_letters ? $"Vector3(X, Y, 0) + {LetterTranslation(j)}" : $"Vector3(0, -{Anim.F(WordRise)} * N.A * Clamp({Progress} * 1.5, 0, 1), 0)");
            offset.SetScalarParameter("X", pos.X);
            offset.SetScalarParameter("Y", pos.Y);
            group.StartAnimation("Offset", offset);
            if (_letters) group.StartAnimation("Scale", Expr(LetterScale(j)));
            else if (_word.Held) group.StartAnimation("Scale", Expr(HeldWordScale));
            group.Children.InsertAtTop(sprite);
            root.Children.InsertAtTop(group);
            _owned.Add(group);
            // The base text scales about the same point.
            Anim.VisualOf(tb).CenterPoint = group.CenterPoint;
        }
        ElementCompositionPreview.SetElementChildVisual(_litHost, root);
    }

    private readonly List<DropShadow> _glows = new();

    /// <summary>New album colours for an active word.</summary>
    public void Recolour(LyricColours colours)
    {
        foreach (var s in _stops)
        {
            if (s.Length < 3) continue;
            s[0].Color = colours.Lit;
            s[1].Color = colours.Edge;
            s[2].Color = Color.FromArgb(0, colours.Edge.R, colours.Edge.G, colours.Edge.B);
        }
        foreach (var g in _glows) g.Color = colours.Glow;
    }

    /// <summary>The blurred copy for lines far from the current one (built the first time it's needed).</summary>
    public void EnsureSoft()
    {
        try { EnsureSoftCore(); }
        catch (Exception ex) { App.LogError(ex); }
    }

    private void EnsureSoftCore()
    {
        if (_softBuilt || _clock == null || _texts[0].ActualWidth <= 0) return;
        _softBuilt = true;
        var c = _clock.Compositor;
        _softRoot = c.CreateContainerVisual();
        _owned.Add(_softRoot);
        float k = Backing ? 0.72f : 1f;
        foreach (var tb in _texts)
        {
            CompositionBrush mask;
            try { mask = tb.GetAlphaMask(); } catch { continue; }
            var size = new Vector2((float)tb.ActualWidth, (float)tb.ActualHeight);
            var sprite = c.CreateSpriteVisual();
            sprite.Size = size;
            sprite.Offset = _letters ? tb.ActualOffset : Vector3.Zero;
            try
            {
                var blur = c.CreateDropShadow();
                blur.Mask = mask;
                blur.Color = Color.FromArgb(255, 245, 245, 247);
                blur.Offset = Vector3.Zero;
                blur.StartAnimation("BlurRadius", Expr($"1 + {Anim.F(_fontSize * 0.16)} * N.Bm"));
                blur.StartAnimation("Opacity", Expr($"{Anim.F(k * 0.95)} * Clamp(N.Bm * 1.4, 0, 1)"));
                sprite.Shadow = blur;
                _owned.Add(blur);
            }
            catch { }
            _owned.Add(sprite); // (not the mask: XAML owns it)
            _softRoot.Children.InsertAtTop(sprite);
        }
        ElementCompositionPreview.SetElementChildVisual(_softHost, _softRoot);
    }

    /// <summary>Releases the lit copy, glow and blurred copy (the line scrolled far away, or the lyrics changed).</summary>
    public void Deactivate()
    {
        if (!_active && !_softBuilt) return;
        _active = _softBuilt = false;
        ElementCompositionPreview.SetElementChildVisual(_litHost, null);
        ElementCompositionPreview.SetElementChildVisual(_softHost, null);
        foreach (var o in _owned) try { o.Dispose(); } catch { }
        _owned.Clear();
        _stops.Clear();
        _glows.Clear();
        _softRoot = null;
    }
}

/// <summary>
/// Lays words out in rows that wrap at the available width, each row aligned left, centred or right (singers of a duet
/// sit on opposite sides). A word with <see cref="LyricWord.SpaceAfter"/> keeps a space after it.
/// </summary>
internal sealed class WordFlow : Panel
{
    public HorizontalAlignment Align { get; set; } = HorizontalAlignment.Left;
    public double SpaceWidth { get; set; } = 5;
    public double RowGap { get; set; }
    /// <summary>One row only (the closed notch): never wraps.</summary>
    public bool NoWrap { get; set; }

    private readonly List<(int from, int to, double width, double height)> _rows = new();

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        _rows.Clear();
        double maxW = NoWrap || double.IsInfinity(available.Width) ? double.PositiveInfinity : available.Width;
        double rowW = 0, rowH = 0, totalH = 0, widest = 0;
        int start = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var el = Children[i];
            el.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = el.DesiredSize.Width, h = el.DesiredSize.Height;
            double space = i > start && Gap(i - 1) ? SpaceWidth : 0;
            if (i > start && rowW + space + w > maxW)
            {
                _rows.Add((start, i, rowW, rowH));
                widest = Math.Max(widest, rowW);
                totalH += rowH + RowGap;
                start = i;
                rowW = w;
                rowH = h;
                continue;
            }
            rowW += space + w;
            rowH = Math.Max(rowH, h);
        }
        if (Children.Count > start) { _rows.Add((start, Children.Count, rowW, rowH)); widest = Math.Max(widest, rowW); totalH += rowH; }
        // Left-aligned rows only need their own width; centred / right-aligned ones take the whole width to align in.
        double width = NoWrap || double.IsInfinity(available.Width) || Align == HorizontalAlignment.Left ? widest : available.Width;
        return new Windows.Foundation.Size(width, totalH);
    }

    private bool Gap(int i) => Children[i] is not LyricWordView wv || wv.Word.SpaceAfter;

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        double y = 0;
        foreach (var (from, to, width, height) in _rows)
        {
            double x = Align switch
            {
                HorizontalAlignment.Center => (final.Width - width) / 2,
                HorizontalAlignment.Right => final.Width - width,
                _ => 0,
            };
            for (int i = from; i < to; i++)
            {
                var el = Children[i];
                if (i > from && Gap(i - 1)) x += SpaceWidth;
                var d = el.DesiredSize;
                el.Arrange(new Windows.Foundation.Rect(x, y + (height - d.Height), d.Width, d.Height));
                x += d.Width;
            }
            y += height + RowGap;
        }
        return final;
    }

    /// <summary>The text's natural width on one row (the closed notch sizes itself to it).</summary>
    public double NaturalWidth => _rows.Count == 0 ? 0 : _rows.Max(r => r.width);
}
