using System.ComponentModel;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace DynamicNotch.Views;

/// <summary>
/// One playlist's songs, beside the album art of the song playing now (the media player's details make way for it):
/// a small back button at the top left with the playlist name to its right, then a smooth, virtualised list of the
/// songs (small cover, title, artist, length) that loads the next page as you near the end. A song plays inside the
/// playlist, so the playlist carries on after it. Someone else's playlist (Spotify answers 403 in Development Mode)
/// shows its cover and a "Play playlist" button instead of the list.
/// </summary>
public sealed class PlaylistView : Grid
{
    private readonly SpotifyLibraryViewModel _vm;
    private readonly IconButton _back = new(26, Theme.IconSmall);
    private readonly TextBlock _name = new(), _count = new(), _status = new();
    private readonly ScrollViewer _scroll = new();
    private readonly ItemsRepeater _list = new();
    private readonly Grid _forbidden = new();
    private readonly Grid _forbiddenCover = new();
    private readonly TextBlock _forbiddenName = new();
    private readonly PillButton _playAll = new(Icons.Play, "Play playlist");

    public PlaylistView(SpotifyLibraryViewModel vm)
    {
        _vm = vm;
        Background = Theme.TransparentBrush;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = Theme.GapS2 });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- Header: back · name (· song count) ----
        var head = new Grid { ColumnSpacing = Theme.S2 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _back.Icon = Icons.ChevronRight;
        _back.Tooltip = "Back to playlists";
        _back.Margin = new Thickness(-Theme.S1, 0, 0, 0);
        var bi = Anim.VisualOf(_back.IconElement);
        bi.CenterPoint = new System.Numerics.Vector3((float)Theme.IconSmall / 2, (float)Theme.IconSmall / 2, 0);
        bi.RotationAngleInDegrees = 180; // a left chevron
        _back.Click += () => _vm.BackToPanel();
        head.Children.Add(_back);
        _name.FontFamily = Theme.FontBold;
        _name.FontWeight = FontWeights.Bold;
        _name.FontSize = Theme.FontTitle - 1;
        _name.Foreground = Theme.PrimaryText;
        _name.TextTrimming = TextTrimming.CharacterEllipsis;
        _name.TextWrapping = TextWrapping.NoWrap;
        _name.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(_name, 1);
        head.Children.Add(_name);
        _count.Style = StyleOf("CaptionText");
        _count.Foreground = Theme.TertiaryText;
        _count.VerticalAlignment = VerticalAlignment.Center;
        _count.Margin = new Thickness(0, 0, Theme.S2, 0);
        SetColumn(_count, 2);
        head.Children.Add(_count);
        Children.Add(head);

        // ---- Songs: virtualised, recycled rows ----
        SetRow(_scroll, 2);
        _list.Layout = new StackLayout { Spacing = 2 };
        _list.ItemTemplate = new TrackRowFactory(t => _ = _vm.PlayInPlaylistAsync(t));
        _list.ItemsSource = vm.Tracks;
        _scroll.Content = _list;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollMode = ScrollMode.Disabled;
        _scroll.Padding = new Thickness(0, 0, Theme.S2, Theme.S2);
        _scroll.ViewChanged += (_, _) => MaybeLoadMore();
        _scroll.SizeChanged += (_, _) => MaybeLoadMore();
        Children.Add(_scroll);

        _status.Style = StyleOf("CaptionText");
        _status.FontSize = Theme.FontBody;
        _status.Foreground = Theme.SecondaryText;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.TextTrimming = TextTrimming.None;
        _status.TextAlignment = TextAlignment.Center;
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.MaxWidth = 360;
        SetRow(_status, 2);
        Children.Add(_status);

        // ---- Not your playlist: cover, a line, Play playlist ----
        SetRow(_forbidden, 2);
        var fs = new StackPanel { Spacing = Theme.S3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _forbiddenCover.HorizontalAlignment = HorizontalAlignment.Center;
        fs.Children.Add(_forbiddenCover);
        _forbiddenName.Style = StyleOf("CaptionText");
        _forbiddenName.FontSize = Theme.FontBody;
        _forbiddenName.Foreground = Theme.SecondaryText;
        _forbiddenName.TextWrapping = TextWrapping.Wrap;
        _forbiddenName.TextTrimming = TextTrimming.None;
        _forbiddenName.TextAlignment = TextAlignment.Center;
        _forbiddenName.MaxWidth = 340;
        _forbiddenName.Text = "Spotify only lists the songs of playlists you own or collaborate on.";
        fs.Children.Add(_forbiddenName);
        _playAll.HorizontalAlignment = HorizontalAlignment.Center;
        _playAll.Click += () => _ = _vm.PlayPlaylistAsync();
        fs.Children.Add(_playAll);
        _forbidden.Children.Add(fs);
        _forbidden.Visibility = Visibility.Collapsed;
        Children.Add(_forbidden);

        vm.PropertyChanged += OnVmChanged;
        vm.Tracks.CollectionChanged += (_, _) => { UpdateCount(); Refresh(); };
        Refresh();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SpotifyLibraryViewModel.Playlist))
        {
            var p = _vm.Playlist;
            _name.Text = p?.Name ?? "";
            ToolTipService.SetToolTip(_name, p?.Name);
            _forbiddenCover.Children.Clear();
            if (p != null)
            {
                var cover = SpotifyPanel.Cover(p.ImageUrl, 84, 10);
                _forbiddenCover.Children.Add(cover);
            }
            _scroll.ChangeView(null, 0, null, disableAnimation: true);
            UpdateCount();
        }
        if (e.PropertyName is nameof(SpotifyLibraryViewModel.TrackTotal) or nameof(SpotifyLibraryViewModel.PlaylistForbidden)) UpdateCount();
        Refresh();
    }

    private void Refresh()
    {
        bool forbidden = _vm.PlaylistForbidden;
        _forbidden.Visibility = forbidden ? Visibility.Visible : Visibility.Collapsed;
        _scroll.Visibility = forbidden ? Visibility.Collapsed : Visibility.Visible;
        string? status = forbidden ? null
            : _vm.TracksLoading && _vm.Tracks.Count == 0 ? "Loading songs…"
            : _vm.Tracks.Count == 0 ? _vm.PlaylistMessage : null;
        _status.Text = status ?? "";
        _status.Visibility = status != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCount()
    {
        int n = Math.Max(_vm.TrackTotal, _vm.Tracks.Count);
        _count.Text = n > 0 && !_vm.PlaylistForbidden ? (n == 1 ? "1 song" : $"{n} songs") : "";
    }

    /// <summary>Near the end of what's loaded: ask for the next page.</summary>
    private void MaybeLoadMore()
    {
        if (_vm.Tracks.Count == 0) return;
        if (_scroll.ScrollableHeight - _scroll.VerticalOffset < 240) _ = _vm.LoadMoreAsync();
    }

    /// <summary>The back button (Esc and the window use it).</summary>
    public void Back() => _vm.BackToPanel();

    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    // =========================================================================================================
    /// <summary>Makes and recycles song rows for the ItemsRepeater (only the rows on screen exist).</summary>
    private sealed class TrackRowFactory : IElementFactory
    {
        private readonly Action<SpotifyTrack> _play;
        private readonly Stack<TrackRow> _pool = new();
        public TrackRowFactory(Action<SpotifyTrack> play) => _play = play;

        public UIElement GetElement(ElementFactoryGetArgs args)
        {
            var row = _pool.Count > 0 ? _pool.Pop() : new TrackRow(_play);
            row.Bind(args.Data as SpotifyTrack);
            return row;
        }

        public void RecycleElement(ElementFactoryRecycleArgs args)
        {
            if (args.Element is TrackRow row) { row.Bind(null); _pool.Push(row); }
        }
    }

    /// <summary>One song: cover · title / artist · length, a hover highlight and press squash; a click plays it.</summary>
    private sealed class TrackRow : Grid
    {
        public const double RowHeight = 44, CoverSize = 34;
        private readonly ImageBrush _brush = new() { Stretch = Stretch.UniformToFill };
        private readonly TextBlock _title = new(), _artist = new(), _time = new();
        private SpotifyTrack? _track;

        public TrackRow(Action<SpotifyTrack> play)
        {
            Height = RowHeight;
            Background = Theme.TransparentBrush;
            var fill = new Border { Background = Theme.CardHoverBrush, CornerRadius = new CornerRadius(8) };
            Children.Add(fill);
            Motions.HoverFade(this, fill);
            Motions.PressScale(this);
            var line = new Grid { Margin = new Thickness(Theme.S1, 0, Theme.S2, 0), ColumnSpacing = Theme.S3 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cover = new Grid { Width = CoverSize, Height = CoverSize, Background = Theme.TileBrush, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            cover.Children.Add(new VectorIcon { Data = Icons.Music, Size = 11, Foreground = Theme.TertiaryText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            cover.Children.Add(new Border { Background = _brush });
            line.Children.Add(cover);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            _title.FontFamily = Theme.FontSemiBold;
            _title.FontWeight = FontWeights.SemiBold;
            _title.FontSize = Theme.FontBody;
            _title.Foreground = Theme.PrimaryText;
            _artist.FontSize = Theme.FontCaption + 0.5;
            _artist.Foreground = Theme.SecondaryText;
            _artist.Margin = new Thickness(0, 1, 0, 0);
            foreach (var t in new[] { _title, _artist }) { t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; text.Children.Add(t); }
            SetColumn(text, 1);
            line.Children.Add(text);
            _time.Style = StyleOf("CaptionText");
            _time.Foreground = Theme.TertiaryText;
            _time.VerticalAlignment = VerticalAlignment.Center;
            _time.IsHitTestVisible = false;
            Typography.SetNumeralAlignment(_time, FontNumeralAlignment.Tabular);
            SetColumn(_time, 2);
            line.Children.Add(_time);
            Children.Add(line);
            Tapped += (_, e) => { e.Handled = true; if (_track != null) play(_track); };
        }

        public async void Bind(SpotifyTrack? t)
        {
            _track = t;
            _title.Text = t?.Name ?? "";
            _artist.Text = t?.Artists ?? "";
            _time.Text = t != null ? SpotifyPanel.Duration(t.Duration) : "";
            ToolTipService.SetToolTip(this, t != null ? $"Play {t.Name}" : null);
            _brush.ImageSource = SpotifyArtCache.Peek(t?.ImageUrl, (int)CoverSize);
            if (t?.ImageUrl is not { } url || _brush.ImageSource != null) return;
            var img = await SpotifyArtCache.GetAsync(url, (int)CoverSize);
            if (ReferenceEquals(_track, t)) _brush.ImageSource = img; // still showing this song (rows are recycled)
        }
    }
}

/// <summary>A small capsule that rises from the bottom with a short message, then fades away (~2.6 s).</summary>
public sealed class NoticePill : Grid
{
    private readonly Border _capsule = new();
    private readonly TextBlock _text = new();
    private readonly CompSpring _show;
    private readonly DispatcherQueueTimer _timer;

    public NoticePill()
    {
        _capsule.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xF2, 0x26, 0x26, 0x2A));
        _capsule.BorderBrush = Theme.HighlightBorderBrush;
        _capsule.BorderThickness = new Thickness(1);
        _capsule.Padding = new Thickness(Theme.S3 + 2, 5, Theme.S3 + 2, 6);
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        IsHitTestVisible = false;
        _capsule.SizeChanged += (_, e) => _capsule.CornerRadius = new CornerRadius(e.NewSize.Height / 2);
        _text.FontFamily = Theme.FontSemiBold;
        _text.FontWeight = FontWeights.SemiBold;
        _text.FontSize = Theme.FontCaption + 1;
        _text.TextTrimming = TextTrimming.CharacterEllipsis;
        _text.MaxWidth = 480;
        _capsule.Child = _text;
        Children.Add(_capsule);
        var c = Anim.VisualOf(this).Compositor;
        (_show, var set) = Motions.Value(c, Motion.Pop, 0);
        var v = Anim.VisualOf(this, translation: true);
        v.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", set)));
        v.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, (1 - p.v) * 10, 0)", ("p", set)));
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.IsRepeating = false;
        _timer.Interval = TimeSpan.FromMilliseconds(2600);
        _timer.Tick += (_, _) => { _timer.Stop(); _show.SetTarget(0, Motion.FadeOut); };
    }

    public void Show(string text, bool problem)
    {
        _text.Text = text;
        _text.Foreground = problem ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xA0, 0x96)) : Theme.PrimaryText;
        _show.SetTarget(1, Motion.Pop);
        _timer.Stop();
        _timer.Start();
    }

    public void Hide() { _timer.Stop(); _show.SetTarget(0, Motion.FadeOut); }
}
