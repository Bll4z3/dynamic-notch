using System.ComponentModel;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DynamicNotch.Views;

/// <summary>
/// Page 1, Music (same layout as the WPF MusicPage): album art on the left (click to cycle players),
/// and a details column centred on the same line as the cover: title, artist, source, the seek bar
/// with elapsed / remaining, and the transport. The art pops on track changes and squashes slightly
/// when paused; the seek bar fill follows the album-art accent.
/// </summary>
public sealed class MusicPage : Grid
{
    private readonly MusicViewModel _vm;
    private readonly Grid _artHost = new(), _artInner = new(), _details = new();
    private readonly Border _artStagger = new(), _artCard = new(), _artImage = new();
    private readonly ImageBrush _artBrush = new() { Stretch = Stretch.UniformToFill };
    private readonly StackPanel _titleBlock = new(), _transport = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Grid _progress = new();
    private readonly TextBlock _title = new(), _artist = new(), _source = new(), _elapsed = new(), _remaining = new();
    private readonly SeekBar _seek = new();
    private readonly IconButton _prev = new(), _play = new(Theme.PlayButtonSize, Theme.IconHero), _next = new();
    private CompSpring? _artPop;
    private bool _lastPlaying;
    private Windows.UI.Color _accent = Theme.AccentColor;
    private bool _hasArtAccent;

    public MusicPage(MusicViewModel vm, SpotifyLikeViewModel like)
    {
        _vm = vm;
        _like = like;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = Theme.GapS5 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Album art. ArtHost carries the shared-element flight (mini art ⇄ album art).
        _artHost.Width = _artHost.Height = Theme.AlbumArtSize;
        _artHost.VerticalAlignment = VerticalAlignment.Center;
        _artCard.CornerRadius = Theme.Radius;
        _artCard.Background = Theme.TileBrush;
        _artCard.BorderBrush = Theme.TileBorderBrush;
        _artCard.BorderThickness = new Thickness(1);
        _artImage.CornerRadius = Theme.Radius;
        _artImage.Margin = new Thickness(-1);
        _artImage.Background = _artBrush;
        _artInner.Children.Add(new VectorIcon { Data = Icons.Music, Size = 40, Foreground = Theme.TertiaryText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        _artInner.Children.Add(_artImage);
        _artCard.Child = _artInner;
        _artStagger.Child = _artCard;
        _artHost.Children.Add(_artStagger);
        Children.Add(_artHost);
        Motions.PressScale(_artCard);
        _artCard.Tapped += (_, e) => { if (_vm.HasMultipleSessions) _vm.CycleSessionCommand.Execute(null); e.Handled = true; };

        // Details column: centred on the same horizontal line as the cover.
        SetColumn(_details, 2);
        _details.VerticalAlignment = VerticalAlignment.Center;
        foreach (var h in new[] { GridLength.Auto, Theme.GapS3, GridLength.Auto, Theme.GapS1, GridLength.Auto })
            _details.RowDefinitions.Add(new RowDefinition { Height = h });

        _title.Style = StyleOf("TitleText");
        _artist.Style = StyleOf("SecondaryText");
        _artist.Margin = Theme.GapTopS1;
        _source.Style = StyleOf("CaptionText");
        _source.Margin = Theme.GapTopS1;
        _titleBlock.Children.Add(_title);
        _titleBlock.Children.Add(_artist);
        _titleBlock.Children.Add(_source);
        _details.Children.Add(_titleBlock);

        // Progress: elapsed | bar | remaining (bold, tabular, +2pt)
        SetRow(_progress, 2);
        _progress.ColumnDefinitions.Add(new ColumnDefinition { Width = Theme.TimeColumn });
        _progress.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _progress.ColumnDefinitions.Add(new ColumnDefinition { Width = Theme.TimeColumn });
        _elapsed.Style = _remaining.Style = StyleOf("MusicTimeText");
        _elapsed.VerticalAlignment = _remaining.VerticalAlignment = VerticalAlignment.Center;
        _remaining.HorizontalAlignment = HorizontalAlignment.Right;
        SetColumn(_seek, 1);
        SetColumn(_remaining, 2);
        _progress.Children.Add(_elapsed);
        _progress.Children.Add(_seek);
        _progress.Children.Add(_remaining);
        _details.Children.Add(_progress);

        // Transport: outline prev/next, large filled play/pause.
        SetRow(_transport, 4);
        _prev.Icon = Icons.Prev;
        _next.Icon = Icons.Next;
        _play.Icon = Icons.Play;
        _play.Margin = Theme.TransportGap;
        _prev.Command = _vm.PreviousCommand;
        _play.Command = _vm.PlayPauseCommand;
        _next.Command = _vm.NextCommand;
        // Spotify's Like heart, left of the controls (only while Spotify is the player).
        _heart.Margin = new Thickness(0, 0, Theme.S3, 0);
        _heart.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(_heart, "Like on Spotify");
        _heart.Tapped += async (_, e) => { e.Handled = true; await _like.ToggleAsync(); };
        _transport.Children.Add(_heart);
        _transport.Children.Add(_prev);
        _transport.Children.Add(_play);
        _transport.Children.Add(_next);
        _prev.Click += () => { _trackDir = -1; Nudge(_prevNudge, -1); };
        _next.Click += () => { _trackDir = +1; Nudge(_nextNudge, +1); };
        _details.Children.Add(_transport);
        Children.Add(_details);

        _seek.ScrubStarted += _vm.BeginScrub;
        _seek.Scrubbing += _vm.PreviewScrub;
        _seek.ScrubCommitted += _vm.CommitScrub;

        Loaded += (_, _) =>
        {
            if (_artPop != null) return;
            var c = Anim.VisualOf(_artInner).Compositor;
            (_artPop, var set) = Motions.Value(c, Motion.Pop, 1);
            Motions.CentreOrigin(_artInner);
            Anim.VisualOf(_artInner).StartAnimation("Scale", Anim.Expr(c, "Vector3(p.v, p.v, 1)", ("p", set)));
            _lastPlaying = _vm.IsPlaying;
            if (!_lastPlaying) _artPop.Snap(0.94);
        };
        _vm.PropertyChanged += OnVmChanged;
        _vm.TrackChanged += OnTrackChanged;
        _like.PropertyChanged += (_, e) => RefreshHeart(animate: e.PropertyName == nameof(SpotifyLikeViewModel.IsLiked));
        BuildControlMotion();
        RefreshHeart(animate: false);
        Refresh("");
    }

    /// <summary>The element that carries the shared-element (mini art ⇄ album art) flight.</summary>
    public FrameworkElement ArtElement => _artHost;

    /// <summary>Blocks for the staggered entrance, with their order (same orders as the WPF page).</summary>
    public IEnumerable<(FrameworkElement element, int order)> StaggerBlocks =>
        new (FrameworkElement, int)[] { (_artStagger, 1), (_titleBlock, 2), (_progress, 3), (_transport, 4) };

    /// <summary>Vertical midpoints (in this page's coordinates) of the cover and of the details column.</summary>
    public (double cover, double details) MeasureCentres()
    {
        var art = _artHost.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, _artHost.ActualHeight / 2));
        var info = _details.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, _details.ActualHeight / 2));
        return (art.Y, info.Y);
    }

    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e) => Refresh(e.PropertyName ?? "");

    private async void Refresh(string name)
    {
        bool all = name == "";
        if (all || name is nameof(MusicViewModel.Title) or nameof(MusicViewModel.Artist) or nameof(MusicViewModel.SessionHint))
        {
            _title.Text = _vm.Title;
            _artist.Text = _vm.Artist;
            _source.Text = _vm.SessionHint;
            ToolTipService.SetToolTip(_artCard, string.IsNullOrEmpty(_vm.SessionHint) ? null : _vm.SessionHint);
        }
        if (all || name == nameof(MusicViewModel.ElapsedText)) _elapsed.Text = _vm.ElapsedText;
        if (all || name == nameof(MusicViewModel.RemainingText)) _remaining.Text = _vm.RemainingText;
        if (all || name == nameof(MusicViewModel.Progress)) _seek.Value = _vm.Progress;
        if (all)
        {
            _seek.IsSeekable = _vm.CanSeek;
            _prev.Enabled = _vm.CanPrevious;
            _next.Enabled = _vm.CanNext;
            _play.Enabled = _vm.HasMedia;
            UpdatePlayGlyph();

            var art = _vm.Artwork;
            _artImage.Visibility = art != null ? Visibility.Visible : Visibility.Collapsed;
            if (art == null) { _hasArtAccent = false; _seek.FollowAccent(); }
            else
            {
                var target = AlbumColorService.Extract(art);
                if (!_hasArtAccent || target != _accent) { _accent = target; _hasArtAccent = true; _seek.SetAccent(target, animate: IsLoaded); }
            }
            var source = await ImageLoader.ToSourceAsync(art);
            if (ReferenceEquals(art, _vm.Artwork)) _artBrush.ImageSource = source;
        }
    }

    private void UpdatePlayGlyph()
    {
        bool playing = _vm.IsPlaying;
        _playMorph?.SetTarget(playing ? 1 : 0, Motion.PlayMorph);
        if (playing == _lastPlaying) return;
        _lastPlaying = playing;
        _artPop?.SetTarget(playing ? 1 : 0.94, Motion.Pop); // subtle squash on the art when play state flips
    }

    private void OnTrackChanged()
    {
        if (_artPop == null) return;
        _artPop.Snap(0.88);
        _artPop.SetTarget(_vm.IsPlaying ? 1 : 0.94, Motion.Pop);
        // The new song's details and cover slide in from the side you skipped to, cross-fading.
        if (Anim.Enabled && _trackIn != null)
        {
            _controlSet!.InsertScalar("dir", _trackDir);
            _trackIn.Snap(0);
            _trackIn.SetTarget(1, Motion.TrackSlide);
        }
        _trackDir = +1; // songs that change by themselves arrive from the right
    }

    // ---- Media control motion -----------------------------------------------------------------------
    private readonly SpotifyLikeViewModel _like;
    private readonly HeartButton _heart = new();
    private readonly VectorIcon _playGlyph = new(), _pauseGlyph = new();
    private CompSpring? _playMorph, _prevNudge, _nextNudge, _trackIn;
    private Microsoft.UI.Composition.CompositionPropertySet? _controlSet;
    private int _trackDir = +1;

    /// <summary>
    /// Play / pause morphs (the two glyphs cross-fade, turning and scaling through each other); next / previous
    /// nudge their icon in their direction; every control lifts a little on hover (the press squash and hover
    /// fill come from IconButton); a new track's text and cover slide and fade in.
    /// </summary>
    private void BuildControlMotion()
    {
        var c = Anim.VisualOf(this).Compositor;
        var p = _controlSet = c.CreatePropertySet();
        p.InsertScalar("dir", 1);
        _playMorph = new CompSpring(p, "m", Motion.PlayMorph, _vm.IsPlaying ? 1 : 0);
        _prevNudge = new CompSpring(p, "pn", Motion.Nudge, 0);
        _nextNudge = new CompSpring(p, "nn", Motion.Nudge, 0);
        _trackIn = new CompSpring(p, "t", Motion.TrackSlide, 1);

        // Play / pause: the button's own glyph is replaced by two that morph.
        _play.Icon = null;
        foreach (var (g, data) in new[] { (_playGlyph, Icons.Play), (_pauseGlyph, Icons.Pause) })
        {
            g.Data = data;
            g.Size = Theme.IconHero;
            g.Foreground = Theme.PrimaryText;
            g.HorizontalAlignment = HorizontalAlignment.Center;
            g.VerticalAlignment = VerticalAlignment.Center;
            g.IsHitTestVisible = false;
            _play.Children.Add(g);
            var gv = Anim.VisualOf(g);
            gv.CenterPoint = new System.Numerics.Vector3((float)Theme.IconHero / 2, (float)Theme.IconHero / 2, 0);
        }
        string m = "Clamp(p.m, 0, 1)";
        var pv = Anim.VisualOf(_playGlyph);
        pv.StartAnimation("Opacity", Anim.Expr(c, $"1 - {m}", ("p", p)));
        pv.StartAnimation("Scale", Anim.Expr(c, $"Vector3(1 - 0.45 * {m}, 1 - 0.45 * {m}, 1)", ("p", p)));
        pv.StartAnimation("RotationAngleInDegrees", Anim.Expr(c, $"-90 * {m}", ("p", p)));
        var sv = Anim.VisualOf(_pauseGlyph);
        sv.StartAnimation("Opacity", Anim.Expr(c, m, ("p", p)));
        sv.StartAnimation("Scale", Anim.Expr(c, $"Vector3(0.55 + 0.45 * {m}, 0.55 + 0.45 * {m}, 1)", ("p", p)));
        sv.StartAnimation("RotationAngleInDegrees", Anim.Expr(c, $"90 * (1 - {m})", ("p", p)));

        // Next / previous: the icon nudges sideways and springs back; hover lifts it slightly.
        foreach (var (button, key) in new[] { (_prev, "pn"), (_next, "nn") })
        {
            if (button.Children.Count < 3 || button.Children[2] is not FrameworkElement icon) continue;
            var (hover, hset) = Motions.Value(c, Motion.Hover, 0);
            button.PointerEntered += (_, _) => hover.SetTarget(1, Motion.Hover);
            button.PointerExited += (_, _) => hover.SetTarget(0, Motion.Hover);
            var iv = Anim.VisualOf(icon, translation: true);
            Motions.CentreOrigin(icon);
            var t = Anim.Expr(c, $"Vector3(p.{key} * 7, 0, 0)", ("p", p));
            iv.StartAnimation("Translation", t);
            iv.StartAnimation("Scale", Anim.Expr(c, "Vector3(1 + 0.1 * h.v, 1 + 0.1 * h.v, 1)", ("h", hset)));
        }
        var (playHover, phset) = Motions.Value(c, Motion.Hover, 0);
        _play.PointerEntered += (_, _) => playHover.SetTarget(1, Motion.Hover);
        _play.PointerExited += (_, _) => playHover.SetTarget(0, Motion.Hover);
        foreach (var g in new[] { _playGlyph, _pauseGlyph })
            Anim.VisualOf(g, translation: true).StartAnimation("Translation", Anim.Expr(c, "Vector3(0, -1.5 * h.v, 0)", ("h", phset)));

        // A new track: the title, artist and source, and the cover image, slide in and fade.
        foreach (var el in new FrameworkElement[] { _title, _artist, _source })
        {
            var v = Anim.VisualOf(el, translation: true);
            v.StartAnimation("Translation", Anim.Expr(c, "Vector3((1 - Clamp(p.t, 0, 1)) * p.dir * 18, 0, 0)", ("p", p)));
            v.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.t * 1.4, 0, 1)", ("p", p)));
        }
        var art = Anim.VisualOf(_artImage, translation: true);
        art.StartAnimation("Translation", Anim.Expr(c, "Vector3((1 - Clamp(p.t, 0, 1)) * p.dir * 10, 0, 0)", ("p", p)));
        art.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.t * 1.2, 0, 1)", ("p", p)));
    }

    private static void Nudge(CompSpring? spring, int dir)
    {
        if (spring == null || !Anim.Enabled) return;
        spring.Snap(dir);
        spring.SetTarget(0, Motion.Nudge);
    }

    private void RefreshHeart(bool animate)
    {
        _heart.Visibility = _like.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(_heart, !_like.IsConnected ? "Like on Spotify (connect Spotify in Settings ▸ General)" : _like.IsLiked ? "Remove from Liked Songs" : "Save to Liked Songs");
        _heart.Set(_like.IsLiked, animate && IsLoaded);
    }
}
