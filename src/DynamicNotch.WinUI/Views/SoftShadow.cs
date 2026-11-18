using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI.Composition;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// A pre-rendered soft shadow for rounded cards: a blurred rounded rectangle drawn ONCE (per radius / blur)
/// with Win2D into a composition surface, then stretched to any size by a nine-grid brush. Unlike a drop
/// shadow masked by a live CompositionVisualSurface of the card, nothing is re-rendered when the card's
/// content changes (hovers, spinners, rows arriving) or while it animates: the shadow is just a sprite.
/// </summary>
internal static class SoftShadow
{
    private static CompositionGraphicsDevice? _device;
    private static readonly Dictionary<(Compositor, int, int), (CompositionSurfaceBrush brush, float inset, float margin)> Cache = new();

    /// <summary>
    /// A sprite that draws the shadow of a <paramref name="radius"/>-cornered card. Size it with
    /// <see cref="Layout"/> whenever the card's size changes.
    /// </summary>
    public static SpriteVisual Create(Compositor c, double radius, double blur, double opacity, double offsetY)
    {
        var (surface, inset, margin) = Get(c, radius, blur);
        var nine = c.CreateNineGridBrush();
        nine.Source = surface;
        nine.SetInsets(inset);
        var sprite = c.CreateSpriteVisual();
        sprite.Brush = nine;
        sprite.Opacity = (float)opacity;
        sprite.Properties.InsertScalar("Margin", margin);
        sprite.Properties.InsertScalar("OffsetY", (float)offsetY);
        return sprite;
    }

    /// <summary>Fits the shadow sprite around a card of <paramref name="size"/> placed at the origin.</summary>
    public static void Layout(SpriteVisual sprite, Vector2 size)
    {
        sprite.Properties.TryGetScalar("Margin", out float m);
        sprite.Properties.TryGetScalar("OffsetY", out float dy);
        sprite.Size = size + new Vector2(2 * m);
        sprite.Offset = new Vector3(-m, -m + dy, 0);
    }

    private static (CompositionSurfaceBrush, float inset, float margin) Get(Compositor c, double radius, double blur)
    {
        int r = (int)Math.Round(radius), b = (int)Math.Round(blur);
        if (Cache.TryGetValue((c, r, b), out var hit)) return hit;
        _device ??= CanvasComposition.CreateCompositionGraphicsDevice(c, CanvasDevice.GetSharedDevice());
        float margin = 2 * b;                         // room for the blur outside the card
        float inset = margin + r + 2;                 // corners stay intact inside the nine-grid
        int side = (int)Math.Ceiling(2 * inset + 4);  // a tiny stretchable middle
        var surface = _device.CreateDrawingSurface(new Windows.Foundation.Size(side, side),
            Microsoft.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
        try
        {
            // The blur source must come from the same device as the surface's drawing session.
            var device = CanvasComposition.GetCanvasDevice(_device);
            using var shape = new CanvasCommandList(device);
            using (var ds = shape.CreateDrawingSession())
                ds.FillRoundedRectangle(margin, margin, side - 2 * margin, side - 2 * margin, r, r, Color.FromArgb(255, 0, 0, 0));
            using var blurFx = new GaussianBlurEffect { Source = shape, BlurAmount = Math.Max(0.5f, b / 2f), BorderMode = EffectBorderMode.Soft };
            using var draw = CanvasComposition.CreateDrawingSession(surface);
            draw.Clear(Color.FromArgb(0, 0, 0, 0));
            draw.DrawImage(blurFx);
        }
        catch (Exception ex)
        {
            // A shadow must never stop a window from opening: fall back to none.
            System.Diagnostics.Debug.WriteLine($"Soft shadow unavailable: {ex.Message}");
        }
        var brush = c.CreateSurfaceBrush(surface);
        brush.Stretch = CompositionStretch.Fill;
        var result = (brush, inset, margin);
        Cache[(c, r, b)] = result;
        return result;
    }
}
