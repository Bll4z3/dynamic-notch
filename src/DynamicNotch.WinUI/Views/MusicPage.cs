using System.ComponentModel;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using LyricsPanelView = DynamicNotch.Views.Lyrics.LyricsPanel;

namespace DynamicNotch.Views;

/// <summary>
/// Page 1, Music (same layout as the WPF MusicPage): album art on the left (click to cycle players),
/// and a details column centred on the same line as the cover: title, artist, source, the seek bar
/// with elapsed / remaining, and the transport. The art pops on track changes and squashes slightly
/// when paused; the seek bar fill follows the album-art accent.
/// <para>
/// The lyrics button (after Next) morphs the page into the lyrics layout: the art shrinks into a compact left column
/// with the title, progress and controls under it, and the lyrics panel takes the right side (the notch grows a little
/// taller). The morph measures where every block was and where it lands, then springs each from the old spot to the
/// new one (the art also from its old size), so nothing jumps; the panel slides and fades in beside them.
/// </para>
/// </summary>
public sealed class MusicPage : Grid
{
    private readonly MusicViewModel _vm;
    private readonly LyricsViewModel _lyricsVm;
    private readonly Grid _artHost = new(), _artInner = new(), _details = new(), _artFlip = new();
    private readonly Border _titleFlip = new(), _transportFlip = new();
    private readonly Border _artStagger = new(), _artCard = new(), _artImage = new(), _artShadowHost = new() { IsHitTestVisible = false };
    // Each line of text sits in a wrapper sized to its text, so the lyrics morph moves exactly the glyphs (no jump when
    // the alignment changes from left to centred); the text blocks themselves keep their track-change slide.
    private readonly Border _titleWrap = new(), _artistWrap = new(), _sourceWrap = new();
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

    public MusicPage(MusicViewModel vm, SpotifyLikeViewModel like, LyricsViewModel lyrics, Microsoft.UI.Composition.Compositor compositor)
    {
        _vm = vm;
        _like = like;
        _lyricsVm = lyrics;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = Theme.GapS5 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // Rows for the lyrics layout's column (art, gap, details, centred vertically); the normal layout spans them all.
        foreach (var h in new[] { new GridLength(1, GridUnitType.Star), GridLength.Auto, Theme.GapS3, GridLength.Auto, new GridLength(1, GridUnitType.Star) })
            RowDefinitions.Add(new RowDefinition { Height = h });

        // Album art. ArtHost carries the shared-element flight (mini art ⇄ album art); ArtFlip carries the layout morph.
        _artHost.Width = _artHost.Height = Theme.AlbumArtSize;
        _artHost.VerticalAlignment = VerticalAlignment.Center;
        // The rounding comes from a compositor clip (BuildArtSurface), so the corner radius can morph with the flight.
        _artCard.Background = Theme.TileBrush;
        _artImage.Background = _artBrush;
        _artInner.Children.Add(new VectorIcon { Data = Icons.Music, Size = 40, Foreground = Theme.TertiaryText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        _artInner.Children.Add(_artImage);
        _artCard.Child = _artInner;
        _artStagger.Child = _artCard;
        _artFlip.Children.Add(_artShadowHost);
        _artFlip.Children.Add(_artStagger);
        _artHost.Children.Add(_artFlip);
        Children.Add(_artHost);
        Canvas.SetZIndex(_artHost, 1); // the art always draws above the blocks that move around it
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
        foreach (var (wrap, text) in new[] { (_titleWrap, _title), (_artistWrap, _artist), (_sourceWrap, _source) })
        {
            wrap.Child = text;
            wrap.HorizontalAlignment = HorizontalAlignment.Left;
            _titleBlock.Children.Add(wrap);
        }
        _titleFlip.Child = _titleBlock;
        _details.Children.Add(_titleFlip);

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

        // Transport: outline prev/next, large filled play/pause, then the lyrics button.
        SetRow(_transportFlip, 4);
        _transportFlip.HorizontalAlignment = HorizontalAlignment.Center;
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
        _lyricsButton.Icon = Icons.Lyrics;
        _lyricsButton.Margin = new Thickness(Theme.S1, 0, 0, 0);
        _lyricsButton.Click += () => _lyricsVm.TogglePanel();
        _transport.Children.Add(_lyricsButton);
        _prev.Click += () => { _trackDir = -1; Nudge(_prevNudge, -1); };
        _next.Click += () => { _trackDir = +1; Nudge(_nextNudge, +1); };
        _transportFlip.Child = _transport;
        _details.Children.Add(_transportFlip);
        Children.Add(_details);

        // The lyrics panel (right side of the lyrics layout). Its host joins the open-notch stagger; the panel itself
        // slides and fades with the layout morph. It stays in the tree at its lyrics-layout size (transparent and
        // click-through in the normal layout), so its lines are built and laid out ahead of time: opening lyrics only
        // starts the morph, with no layout pass for the lines in the way.
        LyricsView = new LyricsPanelView(lyrics, compositor);
        _lyricsHost.Children.Add(LyricsView);
        _lyricsHost.IsHitTestVisible = false;
        _lyricsHost.HorizontalAlignment = HorizontalAlignment.Right;
        _lyricsHost.VerticalAlignment = VerticalAlignment.Top;
        SetColumnSpan(_lyricsHost, 3);
        SetRowSpan(_lyricsHost, 5);
        Children.Add(_lyricsHost);
        SizeChanged += (_, _) => SizeLyricsHost();
        BuildLyricsMorph(compositor);
        BuildArtSurface(compositor);

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
        // A click pops / un-fills the heart; a sync from your account (new song, page shown) just sets it.
        _like.PropertyChanged += (_, e) => RefreshHeart(animate: e.PropertyName == nameof(SpotifyLikeViewModel.IsLiked) && _like.ChangedByUser);
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MusicViewModel.IsVisible) && _vm.IsVisible) _like.Refresh(); };
        BuildControlMotion();
        RefreshHeart(animate: false);
        Refresh("");
    }

    /// <summary>The element that carries the shared-element (mini art ⇄ album art) flight.</summary>
    public FrameworkElement ArtElement => _artHost;

    /// <summary>The text, progress and controls column (the music glow keeps a dark scrim behind it).</summary>
    public FrameworkElement DetailsElement => _details;

    /// <summary>
    /// The music-reactive glow's kick bumps the album art slightly (~2%). It drives the art wrapper's scale, which nothing
    /// else animates (its stagger uses opacity and translation; the press / pop springs scale the inner elements).
    /// </summary>
    public void BindArtBump(Microsoft.UI.Composition.CompositionPropertySet glow)
    {
        Motions.CentreOrigin(_artStagger);
        var v = Anim.VisualOf(_artStagger);
        string s = AlbumGlow.ArtBumpExpression;
        var e = v.Compositor.CreateExpressionAnimation($"Vector3({s}, {s}, 1)");
        e.SetReferenceParameter("G", glow);
        v.StartAnimation("Scale", e);
    }

    /// <summary>
    /// Blocks for the staggered entrance, with their order: the art first (it flies in from the closed notch), then the
    /// title, the progress bar and the controls, each a beat apart.
    /// </summary>
    public IEnumerable<(FrameworkElement element, double order)> StaggerBlocks =>
        new (FrameworkElement, double)[] { (_artStagger, 1), (_titleBlock, 2), (_progress, 3.5), (_transport, 5), (_lyricsHost, 3.5) };

    /// <summary>The lyrics panel's host (its stagger block fades without scaling).</summary>
    public FrameworkElement LyricsHostElement => _lyricsHost;

    /// <summary>The art's stagger block (it's held while the art flies in from the closed notch instead of fading).</summary>
    public FrameworkElement ArtStaggerElement => _artStagger;

    /// <summary>True while the art takes the shared-element flight (the window decides: music playing, page shown…).</summary>
    public Func<bool>? ArtFlightActive { get; set; }

    // ---- Album art surface: morphing corner radius and the soft shadow ---------------------------------------------
    private Microsoft.UI.Composition.CompositionPropertySet _flightP = null!, _artK = null!;
    private Microsoft.UI.Composition.SpriteVisual? _artShadow;

    /// <summary>
    /// The art card is rounded by a compositor clip rather than its own corner radius, so the on-screen radius can be
    /// controlled through every scale the art goes through: during the flight it morphs from the closed notch's mini art
    /// (a circle) to the media player's radius, and during the lyrics morph it stays constant while the art shrinks.
    /// The soft shadow under it fades in with the flight. Everything is a compositor expression: nothing per frame.
    /// </summary>
    private void BuildArtSurface(Microsoft.UI.Composition.Compositor c)
    {
        _artK = c.CreatePropertySet();
        _artK.InsertScalar("Rm", (float)Math.Min(Theme.CornerRadius, Theme.MiniArtSize / 2));
        _artK.InsertScalar("Rb", (float)Theme.CornerRadius);
        _artK.InsertScalar("h", (float)Theme.AlbumArtSize / 2);
        _artK.InsertScalar("s", 1);
        _artK.InsertScalar("rs", (float)Theme.CornerRadius);
        _flightP = c.CreatePropertySet(); // until the window binds the real flight: no flight, at rest
        _flightP.InsertScalar("act", 0); _flightP.InsertScalar("ch", 1); _flightP.InsertScalar("bh", 1); _flightP.InsertScalar("t", 1);

        var geo = c.CreateRoundedRectangleGeometry();
        Anim.VisualOf(_artCard).Clip = c.CreateGeometricClip(geo);
        void Size()
        {
            float w = (float)_artCard.ActualWidth, h = (float)_artCard.ActualHeight;
            geo.Size = new System.Numerics.Vector2(w, h);
            _artK.InsertScalar("h", Math.Max(0, Math.Min(w, h) / 2));
        }
        _artCard.SizeChanged += (_, _) => Size();
        _artRadius = geo;

        try
        {
            _artShadow = SoftShadow.Create(c, Theme.CornerRadius, 18, 0.55, 6);
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetElementChildVisual(_artShadowHost, _artShadow);
            _artShadowHost.SizeChanged += (_, e) => SoftShadow.Layout(_artShadow, new System.Numerics.Vector2((float)e.NewSize.Width, (float)e.NewSize.Height));
        }
        catch (Exception ex) { App.LogError(ex); }
        StartArtSurface();
        ThemeService.RadiusChanged += () =>
        {
            _artK.InsertScalar("Rm", (float)Math.Min(Theme.CornerRadius, Theme.MiniArtSize / 2));
            _artK.InsertScalar("Rb", (float)Theme.CornerRadius);
        };
    }

    private Microsoft.UI.Composition.CompositionRoundedRectangleGeometry? _artRadius;

    private void StartArtSurface()
    {
        if (_artRadius == null) return;
        var c = _artK.Compositor;
        var f = _flip[_artFlip];
        Microsoft.UI.Composition.ExpressionAnimation E(string text)
        {
            var e = c.CreateExpressionAnimation(text);
            e.SetReferenceParameter("P", _flightP);
            e.SetReferenceParameter("F", f);
            e.SetReferenceParameter("Q", _morph!);
            e.SetReferenceParameter("K", _artK);
            return e;
        }
        // s: every scale the card is drawn at (flight × lyrics morph); rs: the radius it should show on screen.
        _artK.StartAnimation("s", E("(P.act * P.ch / Max(P.bh, 1) + (1 - P.act)) * (1 + (F.sx - 1) * Q.ps)"));
        _artK.StartAnimation("rs", E("P.act * (K.Rm + (K.Rb - K.Rm) * Clamp(P.t, 0, 1)) + (1 - P.act) * K.Rb"));
        _artRadius.StartAnimation("CornerRadius", E("Vector2(Min(K.rs / Max(K.s, 0.01), K.h), Min(K.rs / Max(K.s, 0.01), K.h))"));
        // The shadow grows in with the flight (it would look like a smudge on the tiny closed-notch art).
        Anim.VisualOf(_artShadowHost).StartAnimation("Opacity", E($"P.act * {Anim.SmoothStep(0.3, 1, "P.t")} + (1 - P.act)"));
    }

    /// <summary>The window's shared-element flight for the art (its property set: act, ch, bh, t): the radius and shadow follow it.</summary>
    public void BindArtFlight(Microsoft.UI.Composition.CompositionPropertySet flight)
    {
        _flightP = flight;
        StartArtSurface();
    }

    // =================================================================================================================
    // Lyrics layout
    // =================================================================================================================
    private readonly Grid _lyricsHost = new();
    private readonly IconButton _lyricsButton = new();
    private bool _lyricsLayout;
    private double _baseHeight = double.NaN;
    // The morph's progress per axis (1 = blocks at their old spots, 0 = settled): "px" / "py" move the blocks, "ps"
    // scales the art and the seek bar. One axis leads and the other follows, so every block takes a curved path.
    private Microsoft.UI.Composition.CompositionPropertySet? _morph;
    private CompSpring? _mx, _my, _ms, _panelShow;
    private readonly Dictionary<FrameworkElement, Microsoft.UI.Composition.CompositionPropertySet> _flip = new();
    private FrameworkElement[] _scaled = Array.Empty<FrameworkElement>();

    /// <summary>The lyrics panel (right side of the lyrics layout).</summary>
    public LyricsPanelView LyricsView { get; }

    public bool IsLyricsLayout => _lyricsLayout;

    /// <summary>The layout switched (true: it's morphing, so what follows it, like the glow, should glide too).</summary>
    public event Action<bool>? LayoutChanged;

    /// <summary>The page's height in the normal layout (the lyrics layout adds <see cref="Theme.LyricsExtraHeight"/>).</summary>
    public double BaseHeight
    {
        set { _baseHeight = value; Height = value + (_lyricsLayout ? Theme.LyricsExtraHeight : 0); SizeLyricsHost(); }
    }

    /// <summary>The band behind the current lyric line, in this page's coordinates (the vocal glow flows there).</summary>
    public Windows.Foundation.Rect? LyricsFocus
    {
        get
        {
            if (!_lyricsLayout || LyricsView.ActualWidth <= 0) return null;
            var band = LyricsView.FocusBand;
            var o = LyricsView.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
            return new Windows.Foundation.Rect(o.X + band.X - 20, o.Y + band.Y, band.Width + 10, band.Height);
        }
    }

    /// <summary>The panel always has its lyrics-layout size (right of the lyrics column, the taller page height).</summary>
    private void SizeLyricsHost()
    {
        if (ActualWidth > 0) _lyricsHost.Width = Math.Max(0, ActualWidth - Theme.LyricsColumnWidth - Theme.S5);
        if (!double.IsNaN(_baseHeight)) _lyricsHost.Height = _baseHeight + Theme.LyricsExtraHeight;
    }

    private void BuildLyricsMorph(Microsoft.UI.Composition.Compositor c)
    {
        _morph = c.CreatePropertySet();
        _mx = new CompSpring(_morph, "px", Motion.LyricsFollow, 0);
        _my = new CompSpring(_morph, "py", Motion.LyricsLead, 0);
        _ms = new CompSpring(_morph, "ps", Motion.LyricsLead, 0);
        _scaled = new FrameworkElement[] { _artFlip, _seek };
        // Every block that changes place moves on its own: the art, each line of text, the time labels and seek bar, and
        // each transport button (their spacing changes between the layouts).
        foreach (var el in new FrameworkElement[] { _artFlip, _titleWrap, _artistWrap, _sourceWrap, _elapsed, _seek, _remaining, _heart, _prev, _play, _next, _lyricsButton })
        {
            var f = c.CreatePropertySet();
            f.InsertScalar("dx", 0); f.InsertScalar("dy", 0); f.InsertScalar("sx", 1); f.InsertScalar("sy", 1);
            _flip[el] = f;
            var v = Anim.VisualOf(el, translation: true);
            v.StartAnimation("Translation", Anim.Expr(c, "Vector3(F.dx * M.px, F.dy * M.py, 0)", ("F", f), ("M", _morph)));
            if (_scaled.Contains(el))
            {
                Motions.CentreOrigin(el);
                v.StartAnimation("Scale", Anim.Expr(c, "Vector3(1 + (F.sx - 1) * M.ps, 1 + (F.sy - 1) * M.ps, 1)", ("F", f), ("M", _morph)));
            }
        }
        // The panel slides in from the right and fades as the blocks clear the right side (it starts with the sideways
        // move, not after it), and leaves quickly when lyrics close.
        var pv = Anim.VisualOf(LyricsView, translation: true);
        var (show, set) = Motions.Value(c, Motion.LyricsFollow, 0);
        _panelShow = show;
        pv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v * 1.25 - 0.1, 0, 1)", ("p", set)));
        pv.StartAnimation("Translation", Anim.Expr(c, "Vector3((1 - p.v) * 34, 0, 0)", ("p", set)));

        _lyricsVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "" or null or nameof(LyricsViewModel.IsOpen)) SetLyricsLayout(_lyricsVm.IsOpen, animate: IsLoaded && Anim.Enabled);
            RefreshLyricsButton();
        };
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName is "" or null or nameof(MusicViewModel.IsVisible) or nameof(MusicViewModel.HasMedia)) { UpdatePanelShown(); RefreshLyricsButton(); } };
        _lyricsLayout = _lyricsVm.IsOpen;
        ApplyLayout(_lyricsLayout);
        _panelShow.Snap(_lyricsLayout ? 1 : 0);
        _lyricsHost.IsHitTestVisible = _lyricsLayout;
        RefreshLyricsButton();
    }

    /// <summary>
    /// Normal ⇄ lyrics layout. Animated: every block springs from where it is right now (even mid-morph) to where it
    /// lands, on a curved path that keeps clear of the album art. Opening, the vertical move and the art's shrink lead
    /// and the sideways move follows; closing is the same paths reversed (sideways first, then the vertical move and
    /// the art growing back).
    /// </summary>
    private void SetLyricsLayout(bool on, bool animate)
    {
        if (on == _lyricsLayout) return;
        var before = animate ? Capture(current: true) : null;
        _lyricsLayout = on;
        ApplyLayout(on);
        if (before != null)
        {
            UpdateLayout();
            var after = Capture(current: false);
            foreach (var (el, f) in _flip)
            {
                if (!before.TryGetValue(el, out var b) || !after.TryGetValue(el, out var a))
                {
                    f.InsertScalar("dx", 0); f.InsertScalar("dy", 0); f.InsertScalar("sx", 1); f.InsertScalar("sy", 1);
                    continue;
                }
                f.InsertScalar("dx", (float)((b.X + b.Width / 2) - (a.X + a.Width / 2)));
                f.InsertScalar("dy", (float)((b.Y + b.Height / 2) - (a.Y + a.Height / 2)));
                bool scales = _scaled.Contains(el);
                f.InsertScalar("sx", scales && a.Width > 0 ? (float)(b.Width / a.Width) : 1);
                f.InsertScalar("sy", ReferenceEquals(el, _artFlip) && a.Height > 0 ? (float)(b.Height / a.Height) : 1);
            }
            _mx!.Snap(1); _my!.Snap(1); _ms!.Snap(1);
            double lag = Motion.LyricsAxisDelay / Math.Max(0.5, Motion.Speed);
            if (on)
            {
                _my.SetTarget(0, Motion.LyricsLead);
                _ms.SetTarget(0, Motion.LyricsLead);
                _mx.SetTarget(0, Motion.LyricsFollow, delay: lag);
            }
            else
            {
                _mx.SetTarget(0, Motion.LyricsLead);
                _my.SetTarget(0, Motion.LyricsFollow, delay: lag);
                _ms.SetTarget(0, Motion.LyricsFollow, delay: lag);
            }
        }
        else { _mx?.Snap(0); _my?.Snap(0); _ms?.Snap(0); }

        _lyricsHost.IsHitTestVisible = on;
        if (on)
        {
            if (animate) _panelShow!.SetTarget(1, Motion.LyricsFollow, delay: _panelShow.Value < 0.05 ? Motion.LyricsAxisDelay * 0.6 / Math.Max(0.5, Motion.Speed) : 0);
            else _panelShow!.Snap(1);
        }
        else
        {
            if (animate) _panelShow!.SetTarget(0, Motion.FadeOut);
            else _panelShow!.Snap(0);
        }
        UpdatePanelShown();
        LayoutChanged?.Invoke(animate);
    }

    /// <summary>Each block's rectangle in this page: its layout spot, or (current) where the running morph shows it now.</summary>
    private Dictionary<FrameworkElement, Windows.Foundation.Rect> Capture(bool current)
    {
        var map = new Dictionary<FrameworkElement, Windows.Foundation.Rect>();
        double px = current ? _mx?.Value ?? 0 : 0, py = current ? _my?.Value ?? 0 : 0, ps = current ? _ms?.Value ?? 0 : 0;
        foreach (var (el, f) in _flip)
        {
            if (el.ActualWidth <= 0 || el.XamlRoot == null || el.Visibility != Visibility.Visible) continue;
            Windows.Foundation.Point o;
            try { o = el.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0)); } catch { continue; }
            double w = el.ActualWidth, h = el.ActualHeight, cx = o.X + w / 2, cy = o.Y + h / 2;
            if (current)
            {
                f.TryGetScalar("dx", out float dx); f.TryGetScalar("dy", out float dy);
                f.TryGetScalar("sx", out float sx); f.TryGetScalar("sy", out float sy);
                cx += dx * px; cy += dy * py;
                w *= 1 + (sx - 1) * ps; h *= 1 + (sy - 1) * ps;
            }
            map[el] = new Windows.Foundation.Rect(cx - w / 2, cy - h / 2, w, h);
        }
        return map;
    }

    private void ApplyLayout(bool on)
    {
        ColumnDefinitions[0].Width = on ? new GridLength(Theme.LyricsColumnWidth) : GridLength.Auto;
        _artHost.Width = _artHost.Height = on ? Theme.LyricsArtSize : Theme.AlbumArtSize;
        SetRow(_artHost, on ? 1 : 0);
        SetRowSpan(_artHost, on ? 1 : 5);
        _artHost.HorizontalAlignment = on ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        SetColumn(_details, on ? 0 : 2);
        SetRow(_details, on ? 3 : 0);
        SetRowSpan(_details, on ? 1 : 5);
        _details.VerticalAlignment = on ? VerticalAlignment.Top : VerticalAlignment.Center;
        foreach (var (wrap, t) in new[] { (_titleWrap, _title), (_artistWrap, _artist), (_sourceWrap, _source) })
        {
            wrap.HorizontalAlignment = on ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            t.TextAlignment = on ? TextAlignment.Center : TextAlignment.Left;
        }
        _heart.Margin = new Thickness(0, 0, on ? Theme.S1 : Theme.S3, 0);
        _play.Margin = on ? new Thickness(Theme.S1, 0, Theme.S1, 0) : Theme.TransportGap;
        _lyricsButton.Margin = new Thickness(on ? 0 : Theme.S1, 0, 0, 0);
        if (!double.IsNaN(_baseHeight)) Height = _baseHeight + (on ? Theme.LyricsExtraHeight : 0);
    }

    private void UpdatePanelShown()
    {
        bool shown = _lyricsLayout && _vm.IsVisible;
        LyricsView.SetShown(shown);
        _lyricsVm.PanelVisible = shown;
    }

    /// <summary>Lit while lyrics show; dimmed with "No lyrics found" when the song has none.</summary>
    private void RefreshLyricsButton()
    {
        bool open = _lyricsVm.IsOpen, has = _lyricsVm.HasLyrics, loading = _lyricsVm.Status == Services.Lyrics.LyricsStatus.Loading;
        _lyricsButton.Enabled = open || (_vm.HasMedia && (has || loading));
        _lyricsButton.Foreground = open ? Theme.AccentReadableBrush : Theme.PrimaryText;
        _lyricsButton.Backing = open ? Theme.CardHoverBrush : null;
        _lyricsButton.Tooltip = open ? "Hide lyrics" : has ? "Show lyrics" : loading ? "Looking for lyrics…" : _vm.HasMedia ? "No lyrics found" : "Lyrics";
    }

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
        string tip = !_like.IsConnected ? "Like on Spotify (connect Spotify in Settings ▸ General)"
            : _like.LimitedUntil is { } until ? $"Spotify is limiting requests from your app. Likes work again at {until.LocalDateTime:t}."
            : _like.IsLiked ? "Remove from Liked Songs" : "Save to Liked Songs";
        ToolTipService.SetToolTip(_heart, tip);
        _heart.Set(_like.IsLiked, animate && IsLoaded);
    }
}
