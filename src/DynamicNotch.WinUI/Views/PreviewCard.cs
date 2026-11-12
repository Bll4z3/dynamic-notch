using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// Large hover preview of a clipboard item (same look as the WPF card): deep black, 1px ~8% white
/// inner border, the small radius, a soft shadow. Shows the whole image, the text at a readable size
/// (up to 400 characters), or the full link. Never takes the mouse or focus. The window moves it with
/// <see cref="Place"/>; position, pop and fade are compositor springs.
/// </summary>
public sealed class PreviewCard : Grid
{
    private readonly Border _card = new(), _shadowHost = new();
    private readonly Border _imageFrame = new(), _imageBox = new();
    private readonly ImageBrush _imageFill = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _text = new(), _linkText = new();
    private readonly Grid _linkPanel = new();
    private CompSpring? _show, _x, _y;
    private SpriteVisual? _shadow;
    private bool _flipped;

    public PreviewCard()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;

        _card.Background = Theme.NotchBrush;
        _card.BorderBrush = Theme.HighlightBorderBrush;
        _card.BorderThickness = new Thickness(1);
        _card.CornerRadius = Theme.RadiusSmall;
        _card.Padding = Theme.PreviewPadding;

        var content = new Grid();
        _imageFrame.CornerRadius = _imageBox.CornerRadius = Theme.RadiusSmall;
        _imageFrame.Background = Theme.TileBrush;
        _imageBox.Background = _imageFill;
        _imageFrame.Child = _imageBox;
        content.Children.Add(_imageFrame);

        Text(_text);
        _text.Width = Theme.PreviewTextWidth;
        content.Children.Add(_text);

        _linkPanel.Width = Theme.PreviewTextWidth;
        _linkPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _linkPanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _linkPanel.Children.Add(new VectorIcon { Data = Icons.Link, Size = Theme.IconMedium, Foreground = Theme.AccentBrush, VerticalAlignment = VerticalAlignment.Top, Margin = Theme.GapRightS2 });
        Text(_linkText);
        SetColumn(_linkText, 1);
        _linkPanel.Children.Add(_linkText);
        content.Children.Add(_linkPanel);

        _card.Child = content;
        Children.Add(_shadowHost);
        Children.Add(_card);
        Collapse();

        Loaded += (_, _) => Build();
        _card.SizeChanged += (_, e) =>
        {
            if (_shadow != null) _shadow.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
            UpdateOrigin();
            SizeChanged2?.Invoke();
        };
    }

    /// <summary>Raised when the card's size changes (new content laid out): the window re-clamps it.</summary>
    public event Action? SizeChanged2;

    public double CardWidth => _card.ActualWidth;
    public double CardHeight => _card.ActualHeight;
    public bool IsShown => _show?.Target > 0;

    private static void Text(TextBlock t)
    {
        t.TextWrapping = TextWrapping.Wrap;
        t.FontFamily = Theme.Font;
        t.FontSize = Theme.PreviewTextFontSize;
        t.LineHeight = Theme.PreviewTextLineHeight;
        t.Foreground = Theme.PrimaryText;
    }

    private void Build()
    {
        if (_show != null) return;
        var v = Anim.VisualOf(this, translation: true);
        var c = v.Compositor;
        var p = c.CreatePropertySet();
        _show = new CompSpring(p, "s", Motion.PreviewPop, 0);
        _x = new CompSpring(p, "x", Motion.PreviewFollow, 0, precision: 50);
        _y = new CompSpring(p, "y", Motion.PreviewFollow, 0, precision: 50);
        v.StartAnimation("Translation", Anim.Expr(c, "Vector3(p.x, p.y, 0)", ("p", p)));
        v.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.s, 0, 1)", ("p", p)));
        string s = $"({Anim.F(Motion.PreviewStartScale)} + (1 - {Anim.F(Motion.PreviewStartScale)}) * Clamp(p.s, 0, 1.2))";
        v.StartAnimation("Scale", Anim.Expr(c, $"Vector3({s}, {s}, 1)", ("p", p)));

        // Soft shadow under the card: a drop shadow masked by a live surface of the card.
        var surface = c.CreateVisualSurface();
        surface.SourceVisual = ElementCompositionPreview.GetElementVisual(_card);
        surface.SourceSize = new Vector2(1000, 1000);
        var shadow = c.CreateDropShadow();
        shadow.BlurRadius = (float)Theme.PreviewShadowBlur;
        shadow.Offset = new Vector3(0, (float)Theme.PreviewShadowOffsetY, 0);
        shadow.Opacity = (float)Theme.PreviewShadowOpacity;
        shadow.Color = Color.FromArgb(255, 0, 0, 0);
        shadow.Mask = c.CreateSurfaceBrush(surface);
        ((CompositionSurfaceBrush)shadow.Mask).Stretch = CompositionStretch.None;
        ((CompositionSurfaceBrush)shadow.Mask).HorizontalAlignmentRatio = 0;
        ((CompositionSurfaceBrush)shadow.Mask).VerticalAlignmentRatio = 0;
        _shadow = c.CreateSpriteVisual();
        _shadow.Size = new Vector2((float)_card.ActualWidth, (float)_card.ActualHeight);
        _shadow.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(_shadowHost, _shadow);
    }

    private void Collapse() => _imageFrame.Visibility = _text.Visibility = _linkPanel.Visibility = Visibility.Collapsed;

    /// <summary>Fills the card for a clipboard item.</summary>
    public async void Show(ClipItemViewModel item)
    {
        Collapse();
        switch (item.Content.Kind)
        {
            case ClipKind.Image when item.Image is { PixelWidth: > 0, PixelHeight: > 0 } img:
                // Fit inside the max preview box, keeping the aspect, so the whole picture shows.
                double s = Math.Min(Theme.PreviewImageMaxWidth / img.PixelWidth, Theme.PreviewImageMaxHeight / img.PixelHeight);
                s = Math.Min(s, 2.0); // don't blow tiny images up past 2x
                _imageBox.Width = Math.Max(1, img.PixelWidth * s);
                _imageBox.Height = Math.Max(1, img.PixelHeight * s);
                _imageFrame.Visibility = Visibility.Visible;
                _imageFill.ImageSource = await ImageLoader.ToSourceAsync(img);
                break;
            case ClipKind.Link:
                _linkText.Text = Cap(item.Content.Text ?? "");
                _linkPanel.Visibility = Visibility.Visible;
                break;
            default:
                _text.Text = Cap(item.Content.Text ?? "");
                _text.Visibility = Visibility.Visible;
                break;
        }
    }

    /// <summary>Fills the card with an Ask screenshot, fitted inside <paramref name="maxW"/> × <paramref name="maxH"/>.</summary>
    public void ShowImage(ImageSource source, int pixelWidth, int pixelHeight, double maxW, double maxH)
    {
        Collapse();
        if (pixelWidth <= 0 || pixelHeight <= 0) return;
        double s = Math.Min(maxW / pixelWidth, maxH / pixelHeight);
        _imageBox.Width = Math.Max(1, Math.Round(pixelWidth * s));
        _imageBox.Height = Math.Max(1, Math.Round(pixelHeight * s));
        _imageFill.ImageSource = source;
        _imageFrame.Visibility = Visibility.Visible;
    }

    /// <summary>Pops the card in (from 60% scale, out of the side facing the cursor) or out.</summary>
    public void SetShown(bool shown, bool snapFromZero = false)
    {
        if (_show == null) return;
        if (shown && snapFromZero) _show.Snap(0);
        _show.SetTarget(shown ? 1 : 0, shown ? Motion.PreviewPop : Motion.FadeOut);
    }

    /// <summary>Moves the card to (x, y) in its parent; <paramref name="flipped"/> = it sits left of the cursor.</summary>
    public void Place(double x, double y, bool flipped, bool snap)
    {
        if (_x == null || _y == null) return;
        if (flipped != _flipped) { _flipped = flipped; UpdateOrigin(); }
        if (snap) { _x.Snap(x); _y.Snap(y); }
        else { _x.SetTarget(x, Motion.PreviewFollow); _y.SetTarget(y, Motion.PreviewFollow); }
    }

    private void UpdateOrigin() =>
        Anim.VisualOf(this).CenterPoint = new Vector3(_flipped ? (float)_card.ActualWidth : 0, (float)_card.ActualHeight / 2, 0);

    private static string Cap(string s)
    {
        s = s.Trim();
        return s.Length > Theme.PreviewMaxChars ? s[..Theme.PreviewMaxChars].TrimEnd() + "…" : s;
    }
}
