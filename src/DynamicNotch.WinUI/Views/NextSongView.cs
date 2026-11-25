using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// The "Next song" activity pill: the closed notch grows into it when the playing track changes. The cover (soft
/// rounded corners and a soft shadow) sits on the left; beside it a small "NEXT SONG" label in a light tint of the
/// cover's colour, the title and the artist. Behind everything, the cover itself, heavily blurred and faded at its
/// edges, washes the pill in the album's colours, with a faint tint of its main colour over the rest and a dark
/// scrim under the text so it stays readable on any cover.
/// <para>
/// Motion: the notch morphs into the pill (the window's activity springs); here the cover pops in, the text rises
/// and slides in a beat later, and the colour wash fades in. A second track change while the pill is up cross-fades
/// to the new song in place. Leaving, the window fades the layer as the shape draws back in.
/// </para>
/// </summary>
public sealed class NextSongView : Grid
{
    private readonly Border _wash = new() { IsHitTestVisible = false }, _tint = new() { IsHitTestVisible = false };
    private readonly Border _scrim = new() { IsHitTestVisible = false };
    private readonly Grid _artHost = new(), _artCard = new();
    private readonly Border _artImage = new(), _artShadowHost = new() { IsHitTestVisible = false };
    private readonly ImageBrush _artBrush = new() { Stretch = Stretch.UniformToFill };
    private readonly VectorIcon _placeholder = new();
    private readonly StackPanel _text = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = new(), _title = new(), _artist = new();
    private readonly SolidColorBrush _labelBrush = new(Theme.TertiaryTextColor), _tintBrush = new(Color.FromArgb(0, 0, 0, 0));
    private readonly CompSpring _artIn, _textIn, _washIn;
    private readonly DispatcherQueueTimer _swap;
    private readonly SpriteVisual _washSprite;
    private GlowTextures? _textures;
    private Activity? _pending;
    private Windows.Graphics.Imaging.SoftwareBitmap? _shownArt;

    public NextSongView()
    {
        Height = Theme.ActivityNextSongHeight;
        VerticalAlignment = VerticalAlignment.Top;
        Background = Theme.TransparentBrush;
        var c = Anim.VisualOf(this).Compositor;

        // ---- Colour wash: blurred cover (compositor sprite) · faint main-colour tint · text scrim ----
        _washSprite = c.CreateSpriteVisual();
        ElementCompositionPreview.SetElementChildVisual(_wash, _washSprite);
        _wash.SizeChanged += (_, _) => LayoutWash();
        Children.Add(_wash);
        _tint.Background = _tintBrush;
        Children.Add(_tint);
        var scrim = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0.5), EndPoint = new Windows.Foundation.Point(1, 0.5) };
        scrim.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0.12 });
        scrim.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x70, 0, 0, 0), Offset = 0.55 });
        scrim.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x90, 0, 0, 0), Offset = 1 });
        _scrim.Background = scrim;
        Children.Add(_scrim);

        // ---- Row: cover · text ----
        var row = new Grid { Margin = new Thickness(Theme.S3 - 2, 0, Theme.S4 + 2, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = Theme.GapS3 });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Children.Add(row);

        double art = Theme.NextSongArtSize;
        _artHost.Width = _artHost.Height = art;
        _artHost.VerticalAlignment = VerticalAlignment.Center;
        _artCard.CornerRadius = new CornerRadius(Theme.NextSongArtRadius);
        _artCard.Background = Theme.TileBrush;
        _placeholder.Data = Icons.Music;
        _placeholder.Size = 18;
        _placeholder.Foreground = Theme.TertiaryText;
        _placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _artCard.Children.Add(_placeholder);
        _artImage.Background = _artBrush;
        _artCard.Children.Add(_artImage);
        _artHost.Children.Add(_artShadowHost);
        _artHost.Children.Add(_artCard);
        row.Children.Add(_artHost);
        try
        {
            var shadow = SoftShadow.Create(c, Theme.NextSongArtRadius, 12, 0.5, 3);
            ElementCompositionPreview.SetElementChildVisual(_artShadowHost, shadow);
            _artShadowHost.SizeChanged += (_, e) => SoftShadow.Layout(shadow, new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height));
        }
        catch (Exception ex) { App.LogError(ex); }

        _label.Text = "NEXT SONG";
        _label.FontFamily = Theme.FontBold;
        _label.FontWeight = FontWeights.Bold;
        _label.FontSize = 9.5;
        _label.CharacterSpacing = 90;
        _label.Foreground = _labelBrush;
        _title.FontFamily = Theme.FontBold;
        _title.FontWeight = FontWeights.Bold;
        _title.FontSize = Theme.FontActivityTitle + 0.5;
        _title.Foreground = Theme.PrimaryText;
        _title.Margin = new Thickness(0, 1, 0, 0);
        _artist.FontSize = Theme.FontCaption + 1;
        _artist.Foreground = Theme.SecondaryText;
        foreach (var t in new[] { _label, _title, _artist })
        {
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.TextWrapping = TextWrapping.NoWrap;
            t.IsHitTestVisible = false;
            _text.Children.Add(t);
        }
        Grid.SetColumn(_text, 2);
        row.Children.Add(_text);

        // ---- Motion (compositor springs) ----
        (_artIn, var artSet) = Motions.Value(c, Motion.Pop, 1);
        var av = Anim.VisualOf(_artHost);
        av.CenterPoint = new Vector3((float)art / 2, (float)art / 2, 0);
        av.StartAnimation("Scale", Anim.Expr(c, "Vector3(0.72 + 0.28 * p.v, 0.72 + 0.28 * p.v, 1)", ("p", artSet)));
        av.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v * 1.6, 0, 1)", ("p", artSet)));
        (_textIn, var textSet) = Motions.Value(c, Motion.FadeIn, 1);
        var tv = Anim.VisualOf(_text, translation: true);
        tv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", textSet)));
        tv.StartAnimation("Translation", Anim.Expr(c, "Vector3((1 - p.v) * -10, (1 - p.v) * 4, 0)", ("p", textSet)));
        (_washIn, var washSet) = Motions.Value(c, Motion.GlowDim, 1);
        Anim.VisualOf(_wash).StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1) * 0.62", ("p", washSet)));
        Anim.VisualOf(_tint).StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", washSet)));

        _swap = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _swap.IsRepeating = false;
        _swap.Interval = TimeSpan.FromMilliseconds(120);
        _swap.Tick += (_, _) => { _swap.Stop(); if (_pending is { } a) { _pending = null; Build(a); Enter(); } };
    }

    /// <summary>
    /// Shows the song in <paramref name="a"/>. <paramref name="fresh"/>: the pill is just appearing (the window calls
    /// <see cref="Enter"/> once the shape is far enough along); otherwise the old song fades out and the new one in.
    /// </summary>
    public void Apply(Activity a, bool fresh)
    {
        if (fresh || !Anim.Enabled)
        {
            _swap.Stop();
            _pending = null;
            Build(a);
            if (!fresh) Enter();
            else { _artIn.Snap(0); _textIn.Snap(0); _washIn.Snap(0); }
            return;
        }
        _artIn.SetTarget(0, Motion.FadeOut);
        _textIn.SetTarget(0, Motion.FadeOut);
        _pending = a;
        _swap.Stop();
        _swap.Start();
    }

    /// <summary>Staggered entrance: the colour wash, the cover popping in, then the text rising beside it.</summary>
    public void Enter()
    {
        if (!Anim.Enabled) { _artIn.Snap(1); _textIn.Snap(1); _washIn.Snap(1); return; }
        _washIn.SetTarget(1, Motion.GlowDim);
        _artIn.Snap(0);
        _artIn.SetTarget(1, Motion.Pop);
        _textIn.Snap(0);
        _textIn.SetTarget(1, Motion.FadeIn, Motion.ActivityStaggerMs * 2 / 1000.0);
    }

    private async void Build(Activity a)
    {
        _title.Text = string.IsNullOrWhiteSpace(a.Title) ? "Unknown title" : a.Title;
        _artist.Text = a.Subtitle ?? "";
        _artist.Visibility = string.IsNullOrEmpty(a.Subtitle) ? Visibility.Collapsed : Visibility.Visible;

        var art = a.Artwork;
        if (ReferenceEquals(art, _shownArt) && art != null) return;
        _shownArt = art;
        _artImage.Visibility = art != null ? Visibility.Visible : Visibility.Collapsed;
        if (art == null)
        {
            _labelBrush.Color = Theme.TertiaryTextColor;
            _tintBrush.Color = Color.FromArgb(0, 0, 0, 0);
            _washSprite.Brush = null;
            _artBrush.ImageSource = null;
            return;
        }
        var main = AlbumColorService.Extract(art);
        _labelBrush.Color = Mix(main, Theme.PrimaryTextColor, 0.55);
        _tintBrush.Color = Color.FromArgb(0x26, main.R, main.G, main.B);
        try
        {
            _textures ??= new GlowTextures(_washSprite.Compositor);
            _washSprite.Brush = _textures.BlurredArt(art)?.Normal;
        }
        catch (Exception ex) { App.LogError(ex); _washSprite.Brush = null; }
        var source = await ImageLoader.ToSourceAsync(art);
        if (ReferenceEquals(art, _shownArt)) _artBrush.ImageSource = source;
    }

    /// <summary>The blurred cover is centred on the cover and spreads about two-thirds of the way across the pill.</summary>
    private void LayoutWash()
    {
        float h = (float)Math.Max(ActualHeight, Theme.ActivityNextSongHeight);
        float size = Math.Max(h * 4.2f, 220);
        float cx = (float)(Theme.S3 - 2 + Theme.NextSongArtSize / 2);
        _washSprite.Size = new Vector2(size, size);
        _washSprite.Offset = new Vector3(cx - size / 2, h / 2 - size / 2, 0);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(255,
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
}
