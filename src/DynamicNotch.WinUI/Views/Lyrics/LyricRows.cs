using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services.Lyrics;
using DynamicNotch.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DynamicNotch.Views.Lyrics;

/// <summary>
/// A row of the lyrics list (a line, an instrumental break or the credit). Its vertical position, opacity, scale,
/// "current" amount and blur are springs on its own property set ("Y", "O", "S", "A", "Bm"), so the panel only
/// retargets them; the compositor moves everything.
/// </summary>
internal abstract class LyricRow : Grid
{
    public CompositionPropertySet P { get; }
    public CompSpring Y { get; }
    public CompSpring O { get; }
    public CompSpring S { get; }
    public CompSpring A { get; }
    public CompSpring Bm { get; }
    /// <summary>The document item this row shows (-1 for the credit).</summary>
    public int Index { get; init; } = -1;
    protected HorizontalAlignment Origin = HorizontalAlignment.Left;

    protected LyricRow(Compositor c)
    {
        P = c.CreatePropertySet();
        Y = new CompSpring(P, "Y", Motion.LyricScroll, 0, precision: 60);
        O = new CompSpring(P, "O", Motion.LyricLine, 0.5);
        S = new CompSpring(P, "S", Motion.LyricLine, 0.95);
        A = new CompSpring(P, "A", Motion.LyricLine, 0);
        Bm = new CompSpring(P, "Bm", Motion.LyricLine, 0);
        var v = Anim.VisualOf(this, translation: true);
        v.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, P.Y, 0)", ("P", P)));
        v.StartAnimation("Opacity", Anim.Expr(c, "Clamp(P.O, 0, 1)", ("P", P)));
        v.StartAnimation("Scale", Anim.Expr(c, "Vector3(P.S, P.S, 1)", ("P", P)));
        SizeChanged += (_, e) =>
        {
            float x = Origin switch { HorizontalAlignment.Center => (float)e.NewSize.Width / 2, HorizontalAlignment.Right => (float)e.NewSize.Width, _ => 0 };
            v.CenterPoint = new Vector3(x, (float)e.NewSize.Height / 2, 0);
        };
    }

    /// <summary>Applies a style instantly (rows far off screen) or with the line spring.</summary>
    public void ApplyStyle(double opacity, double scale, double active, double blur, bool animate, double delay = 0)
    {
        if (!animate || !Anim.Enabled)
        {
            O.Snap(opacity); S.Snap(scale); A.Snap(active); Bm.Snap(blur);
            return;
        }
        O.SetTarget(opacity, Motion.LyricLine, delay);
        S.SetTarget(scale, Motion.LyricLine, delay);
        A.SetTarget(active, Motion.LyricLine, active > A.Target ? 0 : delay);
        Bm.SetTarget(blur, Motion.LyricLine, delay);
    }

    public virtual void Activate(LyricColours colours) { }
    public virtual void Deactivate() { }
    public virtual void Recolour(LyricColours colours) { }
    public virtual void NeedBlur() { }
}

/// <summary>
/// One lyric line: its words in rows (aligned left, or right / centred for other singers of a duet), a subtle
/// highlight on hover, a ripple and a seek on click, a copy on right-click.
/// </summary>
internal sealed class LyricLineView : LyricRow
{
    private readonly LyricsViewModel _vm;
    private readonly WordFlow _flow = new();
    private readonly Border _hover = new(), _rippleHost = new();
    private readonly CompSpring _hoverSpring;
    private readonly List<LyricWordView> _words = new();
    private LyricColours? _pending;
    private bool _bound;

    public LyricLine Line { get; }
    public bool Seekable { get; init; } = true;

    public LyricLineView(Compositor c, LyricsViewModel vm, LyricLine line, double fontSize, HorizontalAlignment align) : base(c)
    {
        _vm = vm;
        Line = line;
        Index = line.Index;
        Origin = align;
        Background = Theme.TransparentBrush; // the whole row takes clicks / hover
        Padding = new Thickness(10, 5, 10, 5);
        Margin = new Thickness(0, line.IsBacking ? 0 : 1, 0, line.IsBacking ? 0 : 1);

        _hover.Background = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF));
        _hover.CornerRadius = Theme.RadiusSmall;
        _hover.Margin = new Thickness(-2, 0, -2, 0);
        _hover.IsHitTestVisible = false;
        Children.Add(_hover);
        _rippleHost.IsHitTestVisible = false;
        Children.Add(_rippleHost);
        var hv = Anim.VisualOf(_hover);
        var (spring, set) = Motions.Value(c, Motion.Hover, 0);
        _hoverSpring = spring;
        hv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(h.v, 0, 1)", ("h", set)));

        _flow.Align = align;
        _flow.SpaceWidth = Math.Round(fontSize * 0.27);
        _flow.RowGap = 1;
        double size = line.IsBacking ? fontSize * 0.8 : fontSize;
        for (int i = 0; i < line.Words.Count; i++)
        {
            var w = line.Words[i];
            bool backing = w.Backing || line.IsBacking;
            var font = backing ? Theme.FontSemiBold : Theme.FontBold;
            var view = new LyricWordView(w, i, font, backing && !line.IsBacking ? size * 0.8 : size, backing);
            _words.Add(view);
            _flow.Children.Add(view);
        }
        if (line.Words.Count == 0) Height = fontSize * 0.7; // a stanza break in plain lyrics
        Children.Add(_flow);

        PointerEntered += (_, _) => _hoverSpring.SetTarget(1, Motion.Hover);
        PointerExited += (_, _) => _hoverSpring.SetTarget(0, Motion.Hover);
        PointerCanceled += (_, _) => _hoverSpring.SetTarget(0, Motion.Hover);
        Tapped += (_, e) =>
        {
            e.Handled = true;
            if (!Seekable || line.Words.Count == 0) return;
            Ripple(e.GetPosition(this));
            _vm.SeekTo(line);
        };
        RightTapped += (_, e) =>
        {
            e.Handled = true;
            if (line.Words.Count == 0) return;
            Ripple(e.GetPosition(this));
            _vm.Copy(line);
        };
        SizeChanged += (_, _) => { if (_pending is { } p && _bound) { _pending = null; Activate(p); } };
    }

    /// <summary>Binds the words to the clock and this line's times (synced lyrics only).</summary>
    public void Bind()
    {
        if (_bound || _vm.Clock is not { } clock || _vm.TimesFor(Line) is not { } times) return;
        _bound = true;
        foreach (var w in _words) w.BindBase(clock, times, P);
    }

    public override void Activate(LyricColours colours)
    {
        Bind();
        if (!_bound) return;
        if (_words.Count > 0 && _words[0].ActualWidth <= 0) { _pending = colours; return; } // after layout
        foreach (var w in _words) w.Activate(colours);
    }

    public override void Deactivate()
    {
        _pending = null;
        foreach (var w in _words) w.Deactivate();
    }

    public override void Recolour(LyricColours colours)
    {
        foreach (var w in _words) if (w.IsActive) w.Recolour(colours);
    }

    public override void NeedBlur()
    {
        Bind();
        foreach (var w in _words) w.EnsureSoft();
    }

    /// <summary>A soft circle of light spreading from the click, clipped to the row.</summary>
    private void Ripple(Windows.Foundation.Point at)
    {
        if (!Anim.Enabled) return;
        var c = P.Compositor;
        var size = new Vector2((float)ActualWidth, (float)ActualHeight);
        var shapes = c.CreateShapeVisual();
        shapes.Size = size;
        var clipGeo = c.CreateRoundedRectangleGeometry();
        clipGeo.Size = size;
        clipGeo.CornerRadius = new Vector2((float)Theme.CornerRadiusSmall);
        shapes.Clip = c.CreateGeometricClip(clipGeo);
        var circle = c.CreateEllipseGeometry();
        circle.Center = new Vector2((float)at.X, (float)at.Y);
        var accent = Theme.Palette.Readable;
        var shape = c.CreateSpriteShape(circle);
        shape.FillBrush = c.CreateColorBrush(Color.FromArgb(0x38, accent.R, accent.G, accent.B));
        shapes.Shapes.Add(shape);
        ElementCompositionPreview.SetElementChildVisual(_rippleHost, shapes);
        float reach = Math.Max(size.X, size.Y) * 1.1f;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.6f), new Vector2(0.25f, 1f));
        var grow = c.CreateVector2KeyFrameAnimation();
        grow.InsertKeyFrame(0, new Vector2(6, 6));
        grow.InsertKeyFrame(1, new Vector2(reach, reach), ease);
        grow.Duration = TimeSpan.FromMilliseconds(520);
        circle.StartAnimation("Radius", grow);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 1);
        fade.InsertKeyFrame(0.35f, 0.9f);
        fade.InsertKeyFrame(1, 0, ease);
        fade.Duration = TimeSpan.FromMilliseconds(620);
        shapes.StartAnimation("Opacity", fade);
        _hoverSpring.Impulse(4); // a tiny flash of the highlight
    }
}

/// <summary>
/// An instrumental break: three dots that fill one after another in time with the gap, breathing softly, then swell
/// and fade into the next line.
/// </summary>
internal sealed class InterludeRow : LyricRow
{
    public InterludeRow(Compositor c, CompositionPropertySet clock, LyricLine line, double dotSize, HorizontalAlignment align) : base(c)
    {
        Index = line.Index;
        Origin = align;
        Padding = new Thickness(12, 8, 12, 8);
        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = dotSize * 0.75, HorizontalAlignment = align };
        double s = line.Start, d = Math.Max(0.6, line.End - line.Start - 0.35);
        for (int i = 0; i < 3; i++)
        {
            var dot = new Ellipse { Width = dotSize, Height = dotSize, Fill = Theme.PrimaryText, IsHitTestVisible = false };
            dots.Children.Add(dot);
            var v = Anim.VisualOf(dot);
            v.CenterPoint = new Vector3((float)dotSize / 2, (float)dotSize / 2, 0);
            string f = $"Clamp((C.T - {Anim.F(s + i * d / 3)}) / {Anim.F(d / 3)}, 0, 1)";
            string end = $"Clamp(({Anim.F(line.End)} - C.T) / 0.35, 0, 1)";
            var op = Anim.Expr(c, $"(0.28 + 0.72 * {f}) * {end}", ("C", clock));
            v.StartAnimation("Opacity", op);
            string sc = $"(0.78 + 0.3 * {f} + 0.07 * Sin(C.T * 3.4 + {Anim.F(i * 0.9)}) * P.A) * (1 + 0.4 * (1 - {end}))";
            v.StartAnimation("Scale", Anim.Expr(c, $"Vector3({sc}, {sc}, 1)", ("C", clock), ("P", P)));
        }
        Children.Add(dots);
        IsHitTestVisible = false;
    }
}

/// <summary>"Lyrics from LRCLIB" at the end of the lyrics.</summary>
internal sealed class CreditRow : LyricRow
{
    public CreditRow(Compositor c, string credit) : base(c)
    {
        Padding = new Thickness(10, 14, 10, 18);
        Children.Add(new TextBlock
        {
            Text = credit,
            Style = (Style)Application.Current.Resources["CaptionText"],
            Foreground = Theme.TertiaryText,
        });
        IsHitTestVisible = false;
        O.Snap(0.9);
        S.Snap(1);
    }
}
