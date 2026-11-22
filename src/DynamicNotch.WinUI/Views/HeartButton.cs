using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace DynamicNotch.Views;

/// <summary>
/// The Like button: an outline heart that, when liked, pops (springs up past full size and settles), fills
/// with Spotify green and gives off a small burst of sparkles; unliking shrinks the fill back smoothly to the
/// outline. Hover lifts it a touch, a press squashes it. All compositor motion.
/// </summary>
public sealed class HeartButton : Grid
{
    private const string HeartPath = "M19 14 c1.49 -1.46 3 -3.21 3 -5.5 A5.5 5.5 0 0 0 16.5 3 c-1.76 0 -3 .5 -4.5 2 c-1.5 -1.5 -2.74 -2 -4.5 -2 A5.5 5.5 0 0 0 2 8.5 c0 2.3 1.5 4.05 3 5.5 l7 7 Z";
    private static readonly Color Green = Color.FromArgb(255, 0x1E, 0xD7, 0x60);
    private readonly VectorIcon _outline = new(), _fill = new();
    private readonly Border _hover = new();
    private readonly CompSpring _filled, _pop;
    private readonly ContainerVisual _burst;
    private readonly Compositor _c;
    private bool _liked;

    public HeartButton(double size = 32)
    {
        Width = Height = size;
        Background = Theme.TransparentBrush;
        ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);
        _hover.CornerRadius = new CornerRadius(size / 2);
        _hover.Background = Theme.CardHoverBrush;
        Children.Add(_hover);
        Motions.HoverFade(this, _hover);
        var burstHost = new Grid { IsHitTestVisible = false };
        Children.Add(burstHost);
        foreach (var (icon, data, brush) in new[] { (_outline, HeartPath, (Brush)Theme.PrimaryText), (_fill, "F1 " + HeartPath, new SolidColorBrush(Green)) })
        {
            icon.Data = data;
            icon.Size = Theme.IconMedium;
            icon.Foreground = brush;
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.IsHitTestVisible = false;
            Children.Add(icon);
        }

        var v = Anim.VisualOf(this);
        _c = v.Compositor;
        var p = _c.CreatePropertySet();
        _filled = new CompSpring(p, "f", Motion.HeartFill, 0);
        _pop = new CompSpring(p, "k", Motion.HeartPop, 0);
        float half = (float)Theme.IconMedium / 2;
        foreach (var icon in new[] { _outline, _fill })
        {
            var iv = Anim.VisualOf(icon);
            iv.CenterPoint = new Vector3(half, half, 0);
        }
        var fv = Anim.VisualOf(_fill);
        fv.StartAnimation("Opacity", Anim.Expr(_c, "Clamp(p.f * 1.5, 0, 1)", ("p", p)));
        fv.StartAnimation("Scale", Anim.Expr(_c, "Vector3((0.35 + 0.65 * Clamp(p.f, 0, 1.2)) * (1 + 0.32 * p.k), (0.35 + 0.65 * Clamp(p.f, 0, 1.2)) * (1 + 0.32 * p.k), 1)", ("p", p)));
        var ov = Anim.VisualOf(_outline);
        ov.StartAnimation("Opacity", Anim.Expr(_c, "1 - Clamp(p.f, 0, 1)", ("p", p)));
        ov.StartAnimation("Scale", Anim.Expr(_c, "Vector3(1 + 0.12 * p.k, 1 + 0.12 * p.k, 1)", ("p", p)));
        Motions.PressScale(this);

        _burst = _c.CreateContainerVisual();
        _burst.Offset = new Vector3((float)size / 2, (float)size / 2, 0);
        ElementCompositionPreview.SetElementChildVisual(burstHost, _burst);
    }

    public bool IsLiked
    {
        get => _liked;
        set => Set(value, animate: IsLoaded);
    }

    public void Set(bool liked, bool animate)
    {
        if (liked == _liked && animate) return;
        _liked = liked;
        if (!animate || !Anim.Enabled) { _filled.Snap(liked ? 1 : 0); _pop.Snap(0); return; }
        if (liked)
        {
            _filled.Snap(0);
            _filled.SetTarget(1, Motion.HeartFill);
            _pop.Snap(1);
            _pop.SetTarget(0, Motion.HeartPop);
            Burst();
        }
        else _filled.SetTarget(0, Motion.FadeOut);
    }

    /// <summary>Eight tiny sparkles fly out from the heart and fade.</summary>
    private void Burst()
    {
        _burst.Children.RemoveAll();
        var ease = _c.CreateCubicBezierEasingFunction(new Vector2(0.15f, 0.8f), new Vector2(0.3f, 1f));
        for (int i = 0; i < 8; i++)
        {
            double a = i * Math.PI / 4 + Math.PI / 8;
            float r = i % 2 == 0 ? 17 : 13, dot = i % 2 == 0 ? 4 : 3;
            var geo = _c.CreateEllipseGeometry();
            geo.Radius = new Vector2(dot / 2, dot / 2);
            geo.Center = new Vector2(dot / 2, dot / 2);
            var shape = _c.CreateShapeVisual();
            shape.Size = new Vector2(dot, dot);
            var sprite = _c.CreateSpriteShape(geo);
            sprite.FillBrush = _c.CreateColorBrush(i % 3 == 0 ? Colors.White : Green);
            shape.Shapes.Add(sprite);
            shape.Offset = new Vector3(-dot / 2, -dot / 2, 0);
            shape.CenterPoint = new Vector3(dot / 2, dot / 2, 0);
            _burst.Children.InsertAtTop(shape);
            var move = _c.CreateVector3KeyFrameAnimation();
            move.InsertKeyFrame(0, new Vector3(-dot / 2, -dot / 2, 0));
            move.InsertKeyFrame(1, new Vector3((float)(Math.Cos(a) * r) - dot / 2, (float)(Math.Sin(a) * r) - dot / 2, 0), ease);
            move.Duration = TimeSpan.FromMilliseconds(460);
            var fade = _c.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, 1);
            fade.InsertKeyFrame(0.6f, 1);
            fade.InsertKeyFrame(1, 0);
            fade.Duration = TimeSpan.FromMilliseconds(460);
            var shrink = _c.CreateVector3KeyFrameAnimation();
            shrink.InsertKeyFrame(0, new Vector3(1.2f, 1.2f, 1));
            shrink.InsertKeyFrame(1, new Vector3(0.2f, 0.2f, 1), ease);
            shrink.Duration = TimeSpan.FromMilliseconds(460);
            shape.StartAnimation("Offset", move);
            shape.StartAnimation("Opacity", fade);
            shape.StartAnimation("Scale", shrink);
        }
    }
}
