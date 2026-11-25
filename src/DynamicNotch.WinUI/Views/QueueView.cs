using System.ComponentModel;
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
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// Your Spotify queue, beside the album art (built like the playlist view: same header, back button and motion): a
/// "Now playing" row (cover, title, artist and a small equaliser in the accent colour), then "Next up" and the songs
/// after it. When the queue changes while it's open, the rows animate instead of reloading: the old song slides up out
/// of "Now playing" as the next one slides in, rows that went away fold out, the rest glide into place, new ones fade
/// in. Loading shows shimmering placeholder rows; nothing queued and errors show a short line in place of the list.
/// The background takes a faint tint of the cover's colour, fading as songs change. View-only: Spotify's Web API can't
/// reorder, remove or jump to queued songs.
/// </summary>
public sealed class QueueView : Grid
{
    private readonly SpotifyLibraryViewModel _vm;
    private readonly MusicViewModel _music;
    private readonly IconButton _back = new(26, Theme.IconSmall);
    private readonly TextBlock _title = new(), _count = new(), _nextUp = new(), _message = new();
    private readonly Grid _nowHost = new(), _listArea = new(), _messageBox = new();
    private readonly StackPanel _rows = new() { Spacing = 2 }, _skeleton = new() { Spacing = 2 };
    private readonly ScrollViewer _scroll = new();
    private readonly VectorIcon _messageIcon = new();
    private readonly Dictionary<string, QueueRow> _byKey = new();
    private readonly HashSet<QueueRow> _leaving = new();
    private readonly Equalizer _eq = new();
    private readonly SpriteVisual _tint;
    private readonly CompositionColorGradientStop _tintStop;
    private readonly Border _fadeTop = Fade(top: true), _fadeBottom = Fade(top: false);
    private Grid? _nowContent;
    private string? _nowId;
    private bool _shown;

    /// <param name="tintHost">An element behind the whole player (the tint spreads under the art too, so its only edge is the notch's).</param>
    public QueueView(SpotifyLibraryViewModel vm, MusicViewModel music, FrameworkElement tintHost)
    {
        _vm = vm;
        _music = music;
        Background = Theme.TransparentBrush;
        foreach (var h in new[] { GridLength.Auto, Theme.GapS2, GridLength.Auto, new GridLength(10), GridLength.Auto, new GridLength(4), new GridLength(1, GridUnitType.Star) })
            RowDefinitions.Add(new RowDefinition { Height = h });

        // ---- Tint: the cover's colour, very faint, a soft round glow from the upper middle ----
        var c = Anim.VisualOf(this).Compositor;
        var gradient = c.CreateRadialGradientBrush();
        gradient.EllipseCenter = new Vector2(0.55f, 0.15f);
        gradient.EllipseRadius = new Vector2(0.75f, 0.95f);
        gradient.MappingMode = CompositionMappingMode.Relative;
        _tintStop = c.CreateColorGradientStop(0, Color.FromArgb(0, 0, 0, 0));
        gradient.ColorStops.Add(_tintStop);
        gradient.ColorStops.Add(c.CreateColorGradientStop(1, Color.FromArgb(0, 0, 0, 0)));
        _tint = c.CreateSpriteVisual();
        _tint.Brush = gradient;
        ElementCompositionPreview.SetElementChildVisual(tintHost, _tint);
        tintHost.SizeChanged += (_, e) => _tint.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);

        // ---- Header: back · Queue · count ----
        var head = new Grid { ColumnSpacing = Theme.S2 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _back.Icon = Icons.ChevronRight;
        _back.Tooltip = "Back";
        _back.Margin = new Thickness(-Theme.S1, 0, 0, 0);
        var bi = Anim.VisualOf(_back.IconElement);
        bi.CenterPoint = new Vector3((float)Theme.IconSmall / 2, (float)Theme.IconSmall / 2, 0);
        bi.RotationAngleInDegrees = 180;
        _back.Click += () => _vm.CloseQueue();
        head.Children.Add(_back);
        _title.Text = "Queue";
        _title.FontFamily = Theme.FontBold;
        _title.FontWeight = FontWeights.Bold;
        _title.FontSize = Theme.FontTitle - 1;
        _title.Foreground = Theme.PrimaryText;
        _title.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(_title, 1);
        head.Children.Add(_title);
        _count.Style = StyleOf("CaptionText");
        _count.Foreground = Theme.TertiaryText;
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.Margin = new Thickness(0, 0, Theme.S2, 0);
        SetColumn(_count, 2);
        head.Children.Add(_count);
        Children.Add(head);

        // ---- Now playing ----
        SetRow(_nowHost, 2);
        _nowHost.Height = 50;
        _nowHost.Margin = new Thickness(0, 0, Theme.S2, 0);
        var nowBack = new Border { Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(10), IsHitTestVisible = false };
        _nowHost.Children.Add(nowBack);
        _eq.HorizontalAlignment = HorizontalAlignment.Right;
        _eq.VerticalAlignment = VerticalAlignment.Center;
        _eq.Margin = new Thickness(0, 0, Theme.S3, 0);
        Canvas.SetZIndex(_eq, 2);
        _nowHost.Children.Add(_eq);
        Children.Add(_nowHost);

        _nextUp.Text = "NEXT UP";
        _nextUp.FontFamily = Theme.FontBold;
        _nextUp.FontWeight = FontWeights.Bold;
        _nextUp.FontSize = 10;
        _nextUp.CharacterSpacing = 80;
        _nextUp.Foreground = Theme.TertiaryText;
        _nextUp.Margin = new Thickness(2, 0, 0, 0);
        SetRow(_nextUp, 4);
        Children.Add(_nextUp);

        // ---- List: rows · skeleton · message, with soft fades at the top and bottom ----
        SetRow(_listArea, 6);
        _scroll.Content = _rows;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; // the thin, auto-hiding indicator
        _scroll.HorizontalScrollMode = ScrollMode.Disabled;
        _scroll.Padding = new Thickness(0, 6, Theme.S2, Theme.S3);
        _listArea.Children.Add(_scroll);
        _skeleton.Margin = new Thickness(0, 6, Theme.S2, 0);
        for (int i = 0; i < 5; i++) _skeleton.Children.Add(SkeletonRow(i));
        _skeleton.Visibility = Visibility.Collapsed;
        _listArea.Children.Add(_skeleton);
        var ms = new StackPanel { Spacing = Theme.S2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, Theme.S4) };
        _messageIcon.Data = Icons.Queue;
        _messageIcon.Size = 22;
        _messageIcon.Foreground = Theme.TertiaryText;
        _messageIcon.HorizontalAlignment = HorizontalAlignment.Center;
        ms.Children.Add(_messageIcon);
        _message.Style = StyleOf("CaptionText");
        _message.FontSize = Theme.FontBody;
        _message.Foreground = Theme.SecondaryText;
        _message.TextAlignment = TextAlignment.Center;
        _message.TextWrapping = TextWrapping.Wrap;
        _message.TextTrimming = TextTrimming.None;
        _message.MaxWidth = 340;
        ms.Children.Add(_message);
        _messageBox.Children.Add(ms);
        _messageBox.Visibility = Visibility.Collapsed;
        _listArea.Children.Add(_messageBox);
        _listArea.Children.Add(_fadeTop);
        _listArea.Children.Add(_fadeBottom);
        _scroll.ViewChanged += (_, _) => UpdateFades();
        _scroll.SizeChanged += (_, _) => UpdateFades();
        _rows.SizeChanged += (_, _) => UpdateFades();
        Children.Add(_listArea);

        vm.QueueChanged += () => Apply(animate: _shown && Anim.Enabled);
        vm.PropertyChanged += OnVmChanged;
        music.PropertyChanged += (_, e) => { if (e.PropertyName is "" or null or nameof(MusicViewModel.IsPlaying)) _eq.Playing = _music.IsPlaying && _shown; };
        Apply(animate: false);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SpotifyLibraryViewModel.QueueLoading) or nameof(SpotifyLibraryViewModel.QueueMessage)) RefreshState();
    }

    /// <summary>The view came on screen: the rows cascade in from the right, the equaliser starts.</summary>
    public void Shown()
    {
        _shown = true;
        _eq.Playing = _music.IsPlaying;
        RefreshState();
        int i = 0;
        foreach (var row in _rows.Children.OfType<QueueRow>())
        {
            if (_leaving.Contains(row)) continue;
            row.Cascade(Anim.Enabled ? Math.Min(i, 10) * 0.03 + 0.06 : 0);
            i++;
        }
        _scroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    /// <summary>The player's song changed: refresh the "Now playing" stand-in (only used while Spotify sends none).</summary>
    public void PlayerChanged()
    {
        if (_vm.Queue?.Current == null) { ApplyNowPlaying(FromPlayer(), animate: _shown && Anim.Enabled); RefreshState(); }
    }

    public void Hidden()
    {
        _shown = false;
        _eq.Playing = false;
        SetSkeleton(false);
    }

    /// <summary>The cover's colour (null: none), faded in over ~0.6 s.</summary>
    public void SetTint(Color? color, bool animate)
    {
        var target = color is { } k ? Color.FromArgb(0x2E, k.R, k.G, k.B) : Color.FromArgb(0, 0, 0, 0);
        if (!animate || !Anim.Enabled) { _tintStop.StopAnimation("Color"); _tintStop.Color = target; return; }
        var a = _tintStop.Compositor.CreateColorKeyFrameAnimation();
        a.InsertKeyFrame(1, target);
        a.Duration = TimeSpan.FromMilliseconds(650);
        _tintStop.StartAnimation("Color", a);
    }

    // ---- Applying a queue -----------------------------------------------------------------------------------
    private void Apply(bool animate)
    {
        var q = _vm.Queue;
        ApplyNowPlaying(q?.Current ?? FromPlayer(), animate);
        // Keys: the URI plus its occurrence (a queue can hold the same song twice).
        var seen = new Dictionary<string, int>();
        var desired = new List<(string Key, SpotifyTrack Track)>();
        foreach (var t in q?.Items ?? Array.Empty<SpotifyTrack>())
        {
            int n = seen.GetValueOrDefault(t.Uri);
            seen[t.Uri] = n + 1;
            desired.Add(($"{t.Uri}#{n}", t));
        }
        var keep = desired.Select(d => d.Key).ToHashSet();
        foreach (var (key, row) in _byKey.ToList())
        {
            if (keep.Contains(key)) continue;
            _byKey.Remove(key);
            Remove(row, animate);
        }
        int pos = 0, appended = 0;
        foreach (var (key, track) in desired)
        {
            while (pos < _rows.Children.Count && _rows.Children[pos] is QueueRow lr && _leaving.Contains(lr)) pos++;
            if (_byKey.TryGetValue(key, out var row))
            {
                int cur = _rows.Children.IndexOf(row);
                if (cur != pos && cur >= 0) _rows.Children.Move((uint)cur, (uint)Math.Min(pos, _rows.Children.Count - 1));
            }
            else
            {
                row = new QueueRow(track);
                _byKey[key] = row;
                _rows.Children.Insert(Math.Min(pos, _rows.Children.Count), row);
                if (animate) row.FadeIn(0.12 + Math.Min(appended++, 8) * 0.03);
                else row.Cascade(_shown ? 0 : -1);
            }
            pos = _rows.Children.IndexOf(row) + 1;
        }
        int count = desired.Count;
        _count.Text = count == 0 ? "" : count == 1 ? "1 song" : $"{count} songs";
        RefreshState();
    }

    private void Remove(QueueRow row, bool animate)
    {
        if (!animate) { _rows.Children.Remove(row); return; }
        _leaving.Add(row);
        row.Leave();
        var t = DispatcherQueue.GetForCurrentThread().CreateTimer();
        t.IsRepeating = false;
        t.Interval = TimeSpan.FromMilliseconds(200);
        t.Tick += (_, _) => { t.Stop(); _leaving.Remove(row); _rows.Children.Remove(row); };
        t.Start();
    }

    /// <summary>"Now playing": a new song slides up into the row as the old one slides up and out.</summary>
    private void ApplyNowPlaying(SpotifyTrack? t, bool animate)
    {
        string? id = t?.Uri;
        if (id == _nowId && _nowContent != null) return;
        _nowId = id;
        var old = _nowContent;
        var fresh = t == null ? null : NowContent(t);
        _nowContent = fresh;
        if (fresh != null) _nowHost.Children.Insert(1, fresh);
        _nowHost.Visibility = t != null || _vm.QueueLoading ? Visibility.Visible : Visibility.Collapsed;
        if (!animate)
        {
            if (old != null) _nowHost.Children.Remove(old);
            return;
        }
        var c = Anim.VisualOf(this).Compositor;
        if (old != null)
        {
            var (o, os) = Motions.Value(c, Motion.FadeOut, 1);
            var ov = Anim.VisualOf(old, translation: true);
            ov.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", os)));
            ov.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, (1 - p.v) * -14, 0)", ("p", os)));
            o.SetTarget(0, Motion.FadeOut);
            var t2 = DispatcherQueue.GetForCurrentThread().CreateTimer();
            t2.IsRepeating = false;
            t2.Interval = TimeSpan.FromMilliseconds(260);
            t2.Tick += (_, _) => { t2.Stop(); _nowHost.Children.Remove(old); };
            t2.Start();
        }
        if (fresh != null)
        {
            var (n, ns) = Motions.Value(c, Motion.SpotifyViewSwap, 0);
            var nv = Anim.VisualOf(fresh, translation: true);
            nv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", ns)));
            nv.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, (1 - p.v) * 16, 0)", ("p", ns)));
            n.SetTarget(1, Motion.SpotifyViewSwap, 0.06);
        }
    }

    /// <summary>Spotify sends no "currently playing" while it's paused / idle: the song in the player stands in.</summary>
    private SpotifyTrack? FromPlayer()
    {
        var s = _music.Snapshot;
        if (!s.HasMedia || string.IsNullOrWhiteSpace(s.Title) || !s.SourceApp.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return null;
        return new SpotifyTrack("", "player:" + s.Title, s.Title, s.Artist, PlayerCover, s.Duration);
    }

    private static async void LoadPlayerCover(ImageBrush brush, Windows.Graphics.Imaging.SoftwareBitmap? art) =>
        brush.ImageSource = await ImageLoader.ToSourceAsync(art);

    /// <summary>Marks a cover that comes from the player's own artwork (a bitmap, not a URL).</summary>
    private const string PlayerCover = "dn:player-cover";

    private Grid NowContent(SpotifyTrack t)
    {
        var g = new Grid { Margin = new Thickness(Theme.S2 - 2, 0, 44, 0), ColumnSpacing = Theme.S3, IsHitTestVisible = false };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid cover;
        if (t.ImageUrl == PlayerCover)
        {
            cover = SpotifyPanel.Cover(null, 36, 6);
            var brush = new ImageBrush { Stretch = Stretch.UniformToFill };
            cover.Children.Add(new Border { Background = brush });
            LoadPlayerCover(brush, _music.Artwork);
        }
        else cover = SpotifyPanel.Cover(t.ImageUrl, 36, 6);
        cover.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(cover);
        var text = SpotifyPanel.TwoLines(t.Name, t.Artists);
        SetColumn(text, 1);
        g.Children.Add(text);
        ToolTipService.SetToolTip(g, $"Now playing: {t.Name}");
        return g;
    }

    /// <summary>Which of list / skeleton / message shows.</summary>
    private void RefreshState()
    {
        var q = _vm.Queue;
        bool loading = _vm.QueueLoading && q == null;
        SetSkeleton(loading && _shown);
        string? message = loading ? null
            : q == null ? _vm.QueueMessage ?? null
            : q.Items.Count == 0 ? (_vm.QueueMessage ?? "Nothing queued – add songs from search")
            : null;
        _messageBox.Visibility = message != null ? Visibility.Visible : Visibility.Collapsed;
        _message.Text = message ?? "";
        _messageIcon.Data = q is { Items.Count: 0 } && _vm.QueueMessage == null ? Icons.Queue : Icons.Alert;
        _scroll.Visibility = loading || message != null ? Visibility.Collapsed : Visibility.Visible;
        UpdateFades();
        _nextUp.Visibility = loading || q is { Items.Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        _nowHost.Visibility = _nowContent != null || loading ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Each edge fades only while rows are hidden past it (at rest a fade would just read as a dark band).</summary>
    private void UpdateFades()
    {
        bool list = _scroll.Visibility == Visibility.Visible;
        _fadeTop.Opacity = list && _scroll.VerticalOffset > 1 ? 1 : 0;
        _fadeBottom.Opacity = list && _scroll.ScrollableHeight - _scroll.VerticalOffset > 1 ? 1 : 0;
    }

    // ---- Skeleton rows: rounded placeholders that shimmer in a wave ------------------------------------------
    private bool _skeletonOn;

    private void SetSkeleton(bool on)
    {
        if (on == _skeletonOn) return;
        _skeletonOn = on;
        _skeleton.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < _skeleton.Children.Count; i++)
        {
            var v = Anim.VisualOf((UIElement)_skeleton.Children[i]);
            if (!on) { v.StopAnimation("Opacity"); continue; }
            if (!Anim.Enabled) { v.Opacity = 0.6f; continue; }
            var a = v.Compositor.CreateScalarKeyFrameAnimation();
            a.InsertKeyFrame(0, 0.35f);
            a.InsertKeyFrame(0.5f, 0.85f);
            a.InsertKeyFrame(1, 0.35f);
            a.Duration = TimeSpan.FromMilliseconds(1300);
            a.DelayTime = TimeSpan.FromMilliseconds(i * 110);
            a.IterationBehavior = AnimationIterationBehavior.Forever;
            v.StartAnimation("Opacity", a);
        }
    }

    private static Grid SkeletonRow(int i)
    {
        var fill = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        var g = new Grid { Height = QueueRow.RowHeight, ColumnSpacing = Theme.S3, Margin = new Thickness(Theme.S1, 0, Theme.S2, 0), IsHitTestVisible = false };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new Border { Width = QueueRow.CoverSize, Height = QueueRow.CoverSize, CornerRadius = new CornerRadius(6), Background = fill, VerticalAlignment = VerticalAlignment.Center });
        var lines = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(new Border { Height = 9, Width = 150 + (i * 37 % 70), CornerRadius = new CornerRadius(4.5), Background = fill, HorizontalAlignment = HorizontalAlignment.Left });
        lines.Children.Add(new Border { Height = 8, Width = 80 + (i * 23 % 50), CornerRadius = new CornerRadius(4), Background = fill, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0.7 });
        SetColumn(lines, 1);
        g.Children.Add(lines);
        return g;
    }

    /// <summary>A soft fade into the notch's black at the top / bottom edge of the list, so rows don't cut off sharply.</summary>
    private static Border Fade(bool top)
    {
        var b = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, top ? 0 : 1), EndPoint = new Windows.Foundation.Point(0, top ? 1 : 0) };
        b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0xC8, 0, 0, 0), Offset = 0 });
        b.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 1 });
        return new Border
        {
            Height = top ? 16 : 24, Background = b, IsHitTestVisible = false, Margin = new Thickness(0, 0, Theme.S2, 0), Opacity = 0,
            VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom,
        };
    }

    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    // =========================================================================================================
    /// <summary>One queued song: cover · title / artist · length; hover lights it and nudges it right. View-only.</summary>
    private sealed class QueueRow : Grid
    {
        public const double RowHeight = 44, CoverSize = 34;
        private readonly Grid _content = new();
        private readonly CompSpring _in, _hover;

        public QueueRow(SpotifyTrack t)
        {
            Height = RowHeight;
            Background = Theme.TransparentBrush;
            var fill = new Border { Background = Theme.CardHoverBrush, CornerRadius = new CornerRadius(8) };
            Children.Add(fill);
            Motions.HoverFade(this, fill);
            _content.Margin = new Thickness(Theme.S1, 0, Theme.S2, 0);
            _content.ColumnSpacing = Theme.S3;
            _content.IsHitTestVisible = false;
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cover = SpotifyPanel.Cover(t.ImageUrl, CoverSize, 6);
            cover.VerticalAlignment = VerticalAlignment.Center;
            _content.Children.Add(cover);
            var text = SpotifyPanel.TwoLines(t.Name, t.Artists);
            SetColumn(text, 1);
            _content.Children.Add(text);
            var time = new TextBlock { Text = SpotifyPanel.Duration(t.Duration), Style = StyleOf("CaptionText"), Foreground = Theme.TertiaryText, VerticalAlignment = VerticalAlignment.Center };
            Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
            SetColumn(time, 2);
            _content.Children.Add(time);
            Children.Add(_content);
            ToolTipService.SetToolTip(this, string.IsNullOrEmpty(t.Artists) ? t.Name : $"{t.Name} · {t.Artists}");
            Motions.SlideIntoPlace(this, 280); // others glide when a row leaves or arrives

            var c = Anim.VisualOf(this).Compositor;
            var p = c.CreatePropertySet();
            p.InsertScalar("m", Anim.Enabled ? 1 : 0);
            _in = new CompSpring(p, "i", Motion.SpotifyViewSwap, 1);
            _hover = new CompSpring(p, "h", Motion.Hover, 0);
            var cv = Anim.VisualOf(_content, translation: true);
            cv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.i, 0, 1)", ("p", p)));
            cv.StartAnimation("Translation", Anim.Expr(c, "Vector3(((1 - p.i) * 18 + p.h * 3) * p.m, 0, 0)", ("p", p)));
            PointerEntered += (_, _) => _hover.SetTarget(1, Motion.Hover);
            PointerExited += (_, _) => _hover.SetTarget(0, Motion.Hover);
        }

        /// <summary>Slides in from the right after <paramref name="delay"/> s (negative: just shown, no motion).</summary>
        public void Cascade(double delay)
        {
            if (delay < 0) { _in.Snap(1); return; }
            _in.Snap(0);
            _in.SetTarget(1, Motion.SpotifyViewSwap, delay);
        }

        /// <summary>A song that joined the queue: fades in where it lands.</summary>
        public void FadeIn(double delay)
        {
            _in.Snap(0);
            _in.SetTarget(1, Motion.FadeIn, delay);
        }

        public void Leave() => Motions.Leave(this);
    }

    // =========================================================================================================
    /// <summary>Three small bars bouncing at different speeds in the accent colour (still and low while paused).</summary>
    private sealed class Equalizer : StackPanel
    {
        private readonly Rectangle[] _bars = new Rectangle[3];
        private bool _playing;

        public Equalizer()
        {
            Orientation = Orientation.Horizontal;
            Spacing = 2.5;
            Height = 14;
            IsHitTestVisible = false;
            for (int i = 0; i < 3; i++)
            {
                var r = new Rectangle { Width = 3, Height = 14, RadiusX = 1.5, RadiusY = 1.5, Fill = Theme.AccentReadableBrush, VerticalAlignment = VerticalAlignment.Bottom };
                _bars[i] = r;
                Children.Add(r);
                var v = Anim.VisualOf(r);
                v.CenterPoint = new Vector3(1.5f, 14, 0);
                v.Scale = new Vector3(1, 0.35f, 1);
            }
        }

        public bool Playing
        {
            set
            {
                if (value == _playing) return;
                _playing = value;
                double[][] frames = { new[] { 0.35, 1.0, 0.5, 0.85, 0.35 }, new[] { 0.8, 0.3, 1.0, 0.45, 0.8 }, new[] { 0.5, 0.75, 0.3, 1.0, 0.5 } };
                int[] ms = { 900, 760, 1040 };
                for (int i = 0; i < 3; i++)
                {
                    var v = Anim.VisualOf(_bars[i]);
                    if (!_playing || !Anim.Enabled) { v.StopAnimation("Scale"); v.Scale = new Vector3(1, _playing ? 0.7f : 0.35f, 1); continue; }
                    var a = v.Compositor.CreateVector3KeyFrameAnimation();
                    var ease = v.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0), new Vector2(0.55f, 1));
                    for (int k = 0; k < frames[i].Length; k++)
                        a.InsertKeyFrame(k / (float)(frames[i].Length - 1), new Vector3(1, (float)frames[i][k], 1), ease);
                    a.Duration = TimeSpan.FromMilliseconds(ms[i]);
                    a.IterationBehavior = AnimationIterationBehavior.Forever;
                    v.StartAnimation("Scale", a);
                }
            }
        }
    }
}

/// <summary>
/// The small Spotify-style queue icon at the end of the playing song's title: cap-height sized, dimmed until hovered
/// (then full strength, a soft round highlight and a slight grow), squashing when pressed.
/// </summary>
public sealed class QueueButton : Grid
{
    public const double Box = 22, Glyph = 14;
    private readonly CompSpring _hover, _press;

    public QueueButton()
    {
        Width = Height = Box;
        Background = Theme.TransparentBrush;
        var halo = new Ellipse { Fill = Theme.CardHoverBrush, IsHitTestVisible = false };
        Children.Add(halo);
        var icon = new VectorIcon { Data = Icons.Queue, Size = Glyph, Foreground = Theme.PrimaryText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Children.Add(icon);
        ToolTipService.SetToolTip(this, "Queue");

        var c = Anim.VisualOf(this).Compositor;
        var p = c.CreatePropertySet();
        _hover = new CompSpring(p, "h", Motion.Hover, 0);
        _press = new CompSpring(p, "s", Motion.Press, 1);
        Anim.VisualOf(halo).StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.h, 0, 1)", ("p", p)));
        Anim.VisualOf(icon).StartAnimation("Opacity", Anim.Expr(c, "0.6 + 0.4 * Clamp(p.h, 0, 1)", ("p", p)));
        var v = Anim.VisualOf(this);
        v.CenterPoint = new Vector3((float)Box / 2, (float)Box / 2, 0);
        v.StartAnimation("Scale", Anim.Expr(c, "Vector3((1 + 0.1 * p.h) * p.s, (1 + 0.1 * p.h) * p.s, 1)", ("p", p)));
        PointerEntered += (_, _) => _hover.SetTarget(1, Motion.Hover);
        PointerExited += (_, _) => { _hover.SetTarget(0, Motion.Hover); _press.SetTarget(1, Motion.Press); };
        PointerPressed += (_, e) => { _press.SetTarget(0.92, Motion.Press); e.Handled = true; };
        PointerReleased += (_, e) => { _press.SetTarget(1, Motion.Press); e.Handled = true; };
        PointerCaptureLost += (_, _) => _press.SetTarget(1, Motion.Press);
        Tapped += (_, e) => { e.Handled = true; Click?.Invoke(); };
    }

    public event Action? Click;
}

/// <summary>
/// The title line: the title, then (when shown) the queue button right after its last character. The title is measured
/// with room left for the button, so a long title ends in an ellipsis and the button never wraps or slips out of view.
/// The button is centred on the capital letters' middle.
/// </summary>
public sealed class TitleLine : Panel
{
    private const double Gap = 6;
    private readonly TextBlock _text;
    private readonly QueueButton _button;

    public TitleLine(TextBlock text, QueueButton button)
    {
        _text = text;
        _button = button;
        Children.Add(text);
        Children.Add(button);
    }

    private bool ButtonShown => _button.Visibility == Visibility.Visible;
    private double Trailing => ButtonShown ? Gap + QueueButton.Glyph : 0;

    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        _button.Measure(new Windows.Foundation.Size(QueueButton.Box, QueueButton.Box));
        double room = double.IsInfinity(available.Width) ? double.PositiveInfinity : Math.Max(0, available.Width - Trailing);
        _text.Measure(new Windows.Foundation.Size(room, available.Height));
        return new Windows.Foundation.Size(_text.DesiredSize.Width + Trailing, _text.DesiredSize.Height);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        double w = Math.Min(_text.DesiredSize.Width, Math.Max(0, final.Width - Trailing));
        // A centred title (the lyrics layout) keeps the pair centred too.
        double x0 = _text.TextAlignment == TextAlignment.Center ? Math.Max(0, (final.Width - w - Trailing) / 2) : 0;
        _text.Arrange(new Windows.Foundation.Rect(x0, 0, w, final.Height));
        if (ButtonShown)
        {
            double inset = (QueueButton.Box - QueueButton.Glyph) / 2;
            double cy = _text.DesiredSize.Height * 0.52; // the middle of the capitals, a hair below the box's middle
            _button.Arrange(new Windows.Foundation.Rect(x0 + w + Gap - inset, cy - QueueButton.Box / 2, QueueButton.Box, QueueButton.Box));
        }
        else _button.Arrange(new Windows.Foundation.Rect(0, 0, 0, 0));
        return final;
    }
}
