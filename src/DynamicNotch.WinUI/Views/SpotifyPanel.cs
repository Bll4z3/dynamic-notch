using System.Collections.Specialized;
using System.ComponentModel;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;

namespace DynamicNotch.Views;

/// <summary>
/// The media player's playlist panel (under the player; the notch grows down to show it): a search bar, then the
/// playlists as Spotify-style tiles in two columns (cover flush left, name beside it), grouped "Pinned" / "Recently
/// played" when both show. While there's text in the box, song results replace the tiles: cover, title, artist, a
/// hover highlight, and a "+" that queues the song. A short notice pill rises from the bottom for confirmations and
/// problems ("Added to queue", "Open Spotify to play"). Clicking the box asks the notch for keyboard focus; Enter plays
/// the top result, Esc clears the box (a second Esc hands focus back).
/// </summary>
public sealed class SpotifyPanel : Grid
{
    private readonly SpotifyLibraryViewModel _vm;
    private readonly TextBox _box = new();
    private readonly IconButton _clear = new(22, 12);
    private readonly Grid _body = new(), _messageBox = new();
    private readonly ScrollViewer _playlistScroll = new(), _resultScroll = new();
    private readonly StackPanel _playlistStack = new(), _resultStack = new();
    private readonly TextBlock _message = new(), _status = new();
    private readonly PillButton _openSettings = new(Icons.Settings, "Open Settings", quiet: true) { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly CompSpring _resultsShow;
    private bool _syncingBox;

    public const double SearchHeight = 32, TileHeight = 46, ResultHeight = 46;

    public SpotifyPanel(SpotifyLibraryViewModel vm)
    {
        _vm = vm;
        Background = Theme.TransparentBrush;
        Padding = new Thickness(0, Theme.S2, 0, Theme.S2);
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(SearchHeight) });
        RowDefinitions.Add(new RowDefinition { Height = Theme.GapS2 });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- Search bar: icon · box · clear ----
        var bar = new Grid();
        _box.PlaceholderText = "Search songs";
        _box.FontSize = Theme.FontBody;
        _box.Height = SearchHeight;
        _box.Padding = new Thickness(34, 6, 30, 6);
        _box.VerticalContentAlignment = VerticalAlignment.Center;
        _box.IsSpellCheckEnabled = false;
        _box.IsTextPredictionEnabled = false;
        InputStyle.Apply(_box);
        bar.Children.Add(_box);
        bar.Children.Add(new VectorIcon
        {
            Data = Icons.Search, Size = Theme.IconSmall, Foreground = Theme.TertiaryText, IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Theme.S3, 0, 0, 0),
        });
        _clear.Icon = Icons.Close;
        _clear.Tooltip = "Clear";
        _clear.HorizontalAlignment = HorizontalAlignment.Right;
        _clear.VerticalAlignment = VerticalAlignment.Center;
        _clear.Margin = new Thickness(0, 0, 5, 0);
        _clear.Visibility = Visibility.Collapsed;
        _clear.Click += () => { _vm.ClearSearch(); _box.Focus(FocusState.Programmatic); };
        bar.Children.Add(_clear);
        Children.Add(bar);
        _box.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => FocusRequested?.Invoke()), handledEventsToo: true);
        _box.GotFocus += (_, _) => { if (_box.FocusState is FocusState.Pointer or FocusState.Keyboard) FocusRequested?.Invoke(); };
        _box.TextChanged += (_, _) => { if (!_syncingBox) _vm.Query = _box.Text; _clear.Visibility = _box.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed; };
        _box.KeyDown += OnBoxKey;
        // WinUI's own clear button would sit on top of ours (two x's): hide it whenever the template shows it.
        _box.Loaded += (_, _) => HideBuiltInClear();
        _box.GotFocus += (_, _) => HideBuiltInClear();
        _box.TextChanged += (_, _) => HideBuiltInClear();

        // ---- Body: playlist tiles · search results · message ----
        SetRow(_body, 2);
        _playlistScroll.Content = _playlistStack;
        _resultScroll.Content = _resultStack;
        foreach (var s in new[] { _playlistScroll, _resultScroll })
        {
            s.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            s.HorizontalScrollMode = ScrollMode.Disabled;
            s.Padding = new Thickness(0, 0, Theme.S2, 0);
            _body.Children.Add(s);
        }
        _playlistStack.Spacing = Theme.S1;
        _resultStack.Spacing = 2;
        _resultScroll.Visibility = Visibility.Collapsed;

        _message.Style = StyleOf("CaptionText");
        _message.FontSize = Theme.FontBody;
        _message.Foreground = Theme.SecondaryText;
        _message.TextWrapping = TextWrapping.Wrap;
        _message.TextTrimming = TextTrimming.None;
        _message.TextAlignment = TextAlignment.Center;
        _message.HorizontalAlignment = HorizontalAlignment.Center;
        _message.MaxWidth = 420;
        var messageStack = new StackPanel { Spacing = Theme.S3, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        messageStack.Children.Add(_message);
        messageStack.Children.Add(_openSettings);
        _openSettings.Click += () => _vm.RequestSettings();
        _messageBox.Children.Add(messageStack);
        _messageBox.Visibility = Visibility.Collapsed;
        _body.Children.Add(_messageBox);

        _status.Style = StyleOf("CaptionText");
        _status.Foreground = Theme.TertiaryText;
        _status.HorizontalAlignment = HorizontalAlignment.Center;
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.Visibility = Visibility.Collapsed;
        _body.Children.Add(_status);
        Children.Add(_body);

        var c = Anim.VisualOf(this).Compositor;
        // Results ⇄ playlists cross-fade (a small rise on the incoming side).
        (_resultsShow, var rset) = Motions.Value(c, Motion.FadeIn, 0);
        var rv = Anim.VisualOf(_resultScroll, translation: true);
        rv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", rset)));
        rv.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, (1 - p.v) * 6, 0)", ("p", rset)));
        var pv = Anim.VisualOf(_playlistScroll, translation: true);
        pv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(1 - p.v, 0, 1)", ("p", rset)));
        pv.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, p.v * 6, 0)", ("p", rset)));

        vm.PropertyChanged += OnVmChanged;
        vm.Results.CollectionChanged += (_, _) => BuildResults();
        BuildSections();
        Refresh();
    }

    /// <summary>The search box was clicked: the notch should take keyboard focus.</summary>
    public event Action? FocusRequested;
    /// <summary>Esc in an empty box: hand keyboard focus back.</summary>
    public event Action? FocusReleaseRequested;

    /// <summary>Puts the caret in the search box (after the notch took focus).</summary>
    public void FocusBox()
    {
        void Go() { if (_box.Focus(FocusState.Programmatic)) _box.SelectionStart = _box.Text.Length; }
        Go();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Go);
    }

    /// <summary>Drops the caret (the notch is handing focus back).</summary>
    public void ClearFocus()
    {
        if (XamlRoot == null) return;
        if (FocusManager.GetFocusedElement(XamlRoot) is DependencyObject f && IsInside(f, this)) Focus(FocusState.Programmatic);
    }

    public bool HasFocus => XamlRoot != null && FocusManager.GetFocusedElement(XamlRoot) is DependencyObject f && ReferenceEquals(f, _box);

    private void OnBoxKey(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                _ = _vm.PlayTopResultAsync();
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                if (_box.Text.Length > 0) _vm.ClearSearch();
                else FocusReleaseRequested?.Invoke();
                break;
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SpotifyLibraryViewModel.Sections):
                BuildSections();
                Refresh();
                break;
            case nameof(SpotifyLibraryViewModel.Query):
                if (_box.Text != _vm.Query) { _syncingBox = true; _box.Text = _vm.Query; _syncingBox = false; }
                break;
            default:
                Refresh();
                break;
        }
    }

    /// <summary>Which body shows: results while searching, else the playlists, a message or "Loading…".</summary>
    private void Refresh()
    {
        bool searching = _vm.IsSearching;
        _resultScroll.Visibility = Visibility.Visible; // both stay in the tree so they can cross-fade; hit testing picks one
        _resultScroll.IsHitTestVisible = searching;
        _playlistScroll.IsHitTestVisible = !searching;
        _resultsShow.SetTarget(searching ? 1 : 0, searching ? Motion.FadeIn : Motion.FadeOut);
        _box.IsEnabled = _vm.PanelMessage == null || !_vm.PanelNeedsSettings;

        string? status = null;
        if (searching)
        {
            if (_vm.SearchLoading && _vm.Results.Count == 0) status = "Searching…";
            else if (_vm.SearchMessage is { } m && _vm.Results.Count == 0) status = m;
        }
        else if (_vm.IsLoading) status = "Loading your playlists…";
        _status.Text = status ?? "";
        _status.Visibility = status != null ? Visibility.Visible : Visibility.Collapsed;

        bool message = !searching && _vm.PanelMessage != null;
        _messageBox.Visibility = message ? Visibility.Visible : Visibility.Collapsed;
        _message.Text = _vm.PanelMessage ?? "";
        _openSettings.Visibility = _vm.PanelNeedsSettings ? Visibility.Visible : Visibility.Collapsed;
        _playlistScroll.Visibility = message ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Playlist tiles ---------------------------------------------------------------------------------------
    private void BuildSections()
    {
        _playlistStack.Children.Clear();
        int index = 0;
        foreach (var section in _vm.Sections)
        {
            if (section.Header is { } h)
                _playlistStack.Children.Add(new TextBlock
                {
                    Text = h.ToUpperInvariant(), FontFamily = Theme.FontBold, FontWeight = FontWeights.Bold, FontSize = 10, CharacterSpacing = 80,
                    Foreground = Theme.TertiaryText, Margin = new Thickness(2, index == 0 ? 0 : Theme.S2, 0, 2),
                });
            var grid = new Grid { ColumnSpacing = Theme.S2, RowSpacing = Theme.S2 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < section.Items.Count; i++)
            {
                if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TileHeight) });
                var tile = Tile(section.Items[i]);
                SetRow(tile, i / 2);
                SetColumn(tile, i % 2);
                grid.Children.Add(tile);
                if (Anim.Enabled && index < 12) Motions.PopIn(tile, index * 0.018);
                index++;
            }
            _playlistStack.Children.Add(grid);
        }
    }

    private FrameworkElement Tile(SpotifyPlaylist p)
    {
        var tile = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)), CornerRadius = new CornerRadius(8) };
        var fill = new Border { Background = Theme.CardHoverBrush };
        tile.Children.Add(fill);
        Motions.HoverFade(tile, fill);
        Motions.PressScale(tile);
        tile.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TileHeight) });
        tile.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        SetColumnSpan(fill, 2);
        var cover = Cover(p.ImageUrl, TileHeight, 0);
        tile.Children.Add(cover);
        var name = new TextBlock
        {
            Text = p.Name, FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold, FontSize = Theme.FontBody - 0.5,
            Foreground = Theme.PrimaryText, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Theme.S3, 0, Theme.S2, 0), IsHitTestVisible = false,
        };
        SetColumn(name, 1);
        tile.Children.Add(name);
        ToolTipService.SetToolTip(tile, p.Owner is { Length: > 0 } o ? $"{p.Name} · {o}" : p.Name);
        tile.Tapped += (_, e) => { e.Handled = true; _vm.OpenPlaylist(p); };
        return tile;
    }

    // ---- Search results ---------------------------------------------------------------------------------------
    private void BuildResults()
    {
        _resultStack.Children.Clear();
        for (int i = 0; i < _vm.Results.Count; i++)
        {
            var row = ResultRow(_vm.Results[i], i == 0);
            _resultStack.Children.Add(row);
            if (Anim.Enabled) Motions.PopIn(row, i * 0.015);
        }
        _resultScroll.ChangeView(null, 0, null, disableAnimation: true);
        Refresh();
    }

    private FrameworkElement ResultRow(SpotifyTrack t, bool top)
    {
        var row = new Grid { Height = ResultHeight, Background = Theme.TransparentBrush };
        var fill = new Border { Background = Theme.CardHoverBrush, CornerRadius = new CornerRadius(8) };
        row.Children.Add(fill);
        Motions.HoverFade(row, fill);
        var line = new Grid { Margin = new Thickness(Theme.S1, 0, Theme.S1, 0), ColumnSpacing = Theme.S3 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cover = Cover(t.ImageUrl, 38, 5);
        cover.VerticalAlignment = VerticalAlignment.Center;
        line.Children.Add(cover);
        var text = TwoLines(t.Name, t.Artists);
        SetColumn(text, 1);
        line.Children.Add(text);
        var time = new TextBlock { Text = Duration(t.Duration), Style = StyleOf("CaptionText"), Foreground = Theme.TertiaryText, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);
        SetColumn(time, 2);
        line.Children.Add(time);
        var queue = new IconButton(28, Theme.IconSmall) { Icon = Icons.Plus, Tooltip = "Add to queue", VerticalAlignment = VerticalAlignment.Center };
        queue.Click += () => _ = _vm.QueueAsync(t);
        SetColumn(queue, 3);
        line.Children.Add(queue);
        row.Children.Add(line);
        ToolTipService.SetToolTip(row, top ? $"Play {t.Name} (Enter)" : $"Play {t.Name}");
        Motions.PressScale(row);
        row.Tapped += (_, e) => { e.Handled = true; _ = _vm.PlayTrackAsync(t); };
        return row;
    }

    // ---- Shared bits (also used by the playlist view) ---------------------------------------------------------
    internal static StackPanel TwoLines(string title, string subtitle)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        text.Children.Add(new TextBlock
        {
            Text = title, FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold, FontSize = Theme.FontBody,
            Foreground = Theme.PrimaryText, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = subtitle, FontSize = Theme.FontCaption + 0.5, Foreground = Theme.SecondaryText, Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
        });
        return text;
    }

    /// <summary>A cover tile: placeholder note, then the cached image (decoded at its drawn size).</summary>
    internal static Grid Cover(string? url, double size, double radius)
    {
        if (url == SpotifyService.LikedSongsCover) return LikedCover(size, radius);
        var g = new Grid { Width = size, Height = size, Background = Theme.TileBrush, CornerRadius = new CornerRadius(radius), IsHitTestVisible = false };
        g.Children.Add(new VectorIcon { Data = Icons.Music, Size = Math.Max(10, size * 0.32), Foreground = Theme.TertiaryText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var brush = new ImageBrush { Stretch = Stretch.UniformToFill, ImageSource = SpotifyArtCache.Peek(url, (int)size) };
        g.Children.Add(new Border { Background = brush });
        if (brush.ImageSource == null && url != null) _ = Fill(brush, url, (int)size);
        return g;
    }

    /// <summary>Liked Songs' cover: the violet-to-mint gradient with a white heart (drawn here: it has no image in the API).</summary>
    private static Grid LikedCover(double size, double radius)
    {
        var gradient = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
        gradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xFF, 0x45, 0x0A, 0xF5), Offset = 0 });
        gradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xFF, 0x8E, 0x8E, 0xE5), Offset = 0.55 });
        gradient.GradientStops.Add(new GradientStop { Color = Windows.UI.Color.FromArgb(0xFF, 0xC4, 0xEF, 0xD9), Offset = 1 });
        var g = new Grid { Width = size, Height = size, Background = gradient, CornerRadius = new CornerRadius(radius), IsHitTestVisible = false };
        g.Children.Add(new VectorIcon
        {
            Data = HeartButton.HeartData, Size = Math.Max(10, size * 0.4), Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        return g;
    }

    private static async Task Fill(ImageBrush brush, string url, int size)
    {
        if (await SpotifyArtCache.GetAsync(url, size) is { } img) brush.ImageSource = img;
    }

    internal static string Duration(TimeSpan d) => d <= TimeSpan.Zero ? "" : d.TotalHours >= 1 ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : $"{(int)d.TotalMinutes}:{d.Seconds:00}";

    private void HideBuiltInClear()
    {
        if (FindNamed(_box, "DeleteButton") is not FrameworkElement b || b.MaxWidth == 0) return;
        b.MaxWidth = 0;
        b.Opacity = 0;
        b.IsHitTestVisible = false;
        if (b is Control c) c.IsTabStop = false;
    }

    private static DependencyObject? FindNamed(DependencyObject root, string name)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (FindNamed(child, name) is { } found) return found;
        }
        return null;
    }

    private static bool IsInside(DependencyObject el, DependencyObject container)
    {
        for (var d = el; d != null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, container)) return true;
        return false;
    }

    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];
}
