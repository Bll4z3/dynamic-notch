using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using VirtualKey = Windows.System.VirtualKey;

namespace DynamicNotch.Views;

/// <summary>
/// "General" tab (the first tab): the app-wide, non-visual switches (Minimal notch, Start with Windows,
/// Hide when fullscreen) and the hotkey. Everything visual (font, accent, shape…) lives in Customisation.
/// The font picker and its sample card are defined here and reused by the Customisation tab.
/// </summary>
internal sealed class GeneralTabView : Grid
{
    private readonly ApiKeyTabView _apiKey;

    /// <summary>Scrolls to the Claude API key section and focuses its box (Ask's "Open Settings").</summary>
    public void FocusApiKey()
    {
        _apiKey.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0.1 });
        _apiKey.FocusBox();
    }

    public GeneralTabView(GeneralTabViewModel vm, ApiKeyTabViewModel apiKey)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, Theme.S3, Theme.S2) };
        stack.Children.Add(SettingsUi.Title("General"));
        stack.Children.Add(SettingsUi.Subtitle("How the notch behaves. Colours, shape and motion are in Customisation."));

        // ---- Behaviour ----
        stack.Children.Add(new TextBlock { Text = "Behaviour", Style = SettingsUi.StyleOf("SectionText"), Margin = new Thickness(Theme.S1, Theme.S4, 0, Theme.S2) });
        var toggles = new StackPanel();
        toggles.Children.Add(SettingsUi.ToggleRow("Minimal notch", "A thin bar until you hover it",
            () => vm.MinimalNotch, v => vm.MinimalNotch = v, vm, nameof(GeneralTabViewModel.MinimalNotch)));
        toggles.Children.Add(Divider());
        toggles.Children.Add(SettingsUi.ToggleRow("Start with Windows", "Open the notch when you sign in",
            () => vm.StartWithWindows, v => vm.StartWithWindows = v, vm, nameof(GeneralTabViewModel.StartWithWindows)));
        toggles.Children.Add(Divider());
        toggles.Children.Add(SettingsUi.ToggleRow("Hide notch when an app is fullscreen", "Videos, games and presentations. It comes back when they exit.",
            () => vm.HideWhenFullscreen, v => vm.HideWhenFullscreen = v, vm, nameof(GeneralTabViewModel.HideWhenFullscreen)));
        stack.Children.Add(SettingsUi.Card(toggles));

        // ---- Screenshot shelf ----
        stack.Children.Add(new TextBlock { Text = "Screenshot shelf", Style = SettingsUi.StyleOf("SectionText"), Margin = new Thickness(Theme.S1, Theme.S4, 0, Theme.S2) });
        var shots = new StackPanel();
        shots.Children.Add(SettingsUi.ToggleRow("Screenshot shelf", "New screenshots (Win+Shift+S, PrtScn) land in the notch",
            () => vm.ScreenshotShelf, v => vm.ScreenshotShelf = v, vm, nameof(GeneralTabViewModel.ScreenshotShelf)));
        shots.Children.Add(Divider());
        shots.Children.Add(SettingsUi.ToggleRow("Open the notch on a new screenshot", "It closes again after a few seconds",
            () => vm.ScreenshotAutoOpen, v => vm.ScreenshotAutoOpen = v, vm, nameof(GeneralTabViewModel.ScreenshotAutoOpen)));
        shots.Children.Add(Divider());
        var keep = new Segmented(("5", "5"), ("10", "10"), ("20", "20")) { Width = 180, VerticalAlignment = VerticalAlignment.Center };
        keep.Value = vm.ScreenshotKeep <= 5 ? "5" : vm.ScreenshotKeep <= 10 ? "10" : "20";
        keep.Selected += id => vm.ScreenshotKeep = int.Parse(id);
        shots.Children.Add(LabelRow("Keep", "How many recent screenshots the shelf holds", keep));
        shots.Children.Add(Divider());
        var folderText = new TextBlock { Style = SettingsUi.StyleOf("CaptionText"), Foreground = Theme.SecondaryText, TextTrimming = TextTrimming.CharacterEllipsis, Margin = Theme.GapTopS1 };
        void ShowFolder() { folderText.Text = vm.ScreenshotSaveFolder; ToolTipService.SetToolTip(folderText, vm.ScreenshotSaveFolder); }
        ShowFolder();
        var change = new PillButton(Icons.Folder, "Change", quiet: true) { VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS3 };
        change.Click += async () =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, SettingsWindow.CurrentHandle);
                var folder = await picker.PickSingleFolderAsync();
                if (folder != null) { vm.ScreenshotSaveFolder = folder.Path; ShowFolder(); }
            }
            catch { }
        };
        var folderRow = new Grid { MinHeight = 40 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var folderLabel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        folderLabel.Children.Add(new TextBlock { Text = "Save to", Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold });
        folderLabel.Children.Add(folderText);
        folderRow.Children.Add(folderLabel);
        Grid.SetColumn(change, 1);
        folderRow.Children.Add(change);
        shots.Children.Add(folderRow);
        stack.Children.Add(SettingsUi.Card(shots));

        // ---- Spotify: connect your account (Like button, playlist panel, search, playback) ----
        stack.Children.Add(new TextBlock { Text = "Spotify", Style = SettingsUi.StyleOf("SectionText"), Margin = new Thickness(Theme.S1, Theme.S4, 0, Theme.S2) });
        var playlists = new NotchPlaylistsCard(vm);
        stack.Children.Add(SettingsUi.Card(SpotifyCard(vm, playlists.Refresh)));
        stack.Children.Add(new TextBlock { Text = "Notch playlists", Style = SettingsUi.StyleOf("SectionText"), Margin = new Thickness(Theme.S1, Theme.S4, 0, Theme.S2) });
        stack.Children.Add(SettingsUi.Card(playlists));

        // ---- Claude API key (was its own tab): the same box, Paste, Save / Remove / Test and status ----
        stack.Children.Add(new TextBlock { Text = "Claude API key", Style = SettingsUi.StyleOf("SectionText"), Margin = new Thickness(Theme.S1, Theme.S4, 0, Theme.S2) });
        _apiKey = new ApiKeyTabView(apiKey, embedded: true);
        stack.Children.Add(SettingsUi.Card(_apiKey));

        stack.Children.Add(new TextBlock { Text = "Ask hotkey", Style = SettingsUi.StyleOf("SectionText"), Margin = new Thickness(Theme.S1, Theme.S4, 0, Theme.S2) });
        var hotkey = new Grid { MinHeight = 40 };
        hotkey.Children.Add(new TextBlock { Text = "Open Ask Claude from anywhere", Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, VerticalAlignment = VerticalAlignment.Center });
        hotkey.Children.Add(new Border
        {
            Background = Theme.InputBrush, BorderBrush = Theme.CardBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 3, 10, 4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = AppConfig.AskHotkeyLabel, FontFamily = Theme.MonoFont, FontSize = Theme.FontCaption + 1, Foreground = Theme.PrimaryText },
        });
        stack.Children.Add(SettingsUi.Card(hotkey));

        stack.Children.Add(new TextBlock
        {
            Text = "Claude Usage reads an unofficial, undocumented endpoint (api.anthropic.com/api/oauth/usage) with Claude Code's sign-in. " +
                   "It may change or stop working at any time. The sign-in token is only read into memory and sent to api.anthropic.com.",
            Style = SettingsUi.StyleOf("CaptionText"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = Theme.GapTopS3,
        });

        Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    /// <summary>
    /// Spotify: your Spotify app's Client ID, then Connect (signs in through your browser, OAuth with PKCE) and
    /// Disconnect, with the status (a sign-in from before the playlist panel shows "Reconnect"), a short how-to (the
    /// redirect URI to add to the Spotify app) and Spotify's Development Mode rules.
    /// </summary>
    private static FrameworkElement SpotifyCard(GeneralTabViewModel vm, Action connectedChanged)
    {
        var panel = new StackPanel { Spacing = Theme.S2 };
        var title = new TextBlock { Text = "Connect Spotify", Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold };
        panel.Children.Add(title);
        panel.Children.Add(new TextBlock
        {
            Text = $"For the Like button, the playlist panel, search and playing songs from the notch. Create a free app at developer.spotify.com/dashboard, add {SpotifyService.RedirectUri} as a Redirect URI, tick \"Web API\", then paste its Client ID here and press Connect.",
            Style = SettingsUi.StyleOf("CaptionText"), Foreground = Theme.SecondaryText, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
        });
        var row = new Grid { ColumnSpacing = Theme.S2 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var id = new TextBox { PlaceholderText = "Client ID", Text = vm.SpotifyClientId, FontFamily = Theme.MonoFont, FontSize = Theme.FontBody, Height = Theme.SettingsFieldHeight, Padding = Theme.SettingsFieldPadding, VerticalContentAlignment = VerticalAlignment.Center };
        InputStyle.Apply(id);
        id.LostFocus += (_, _) => vm.SpotifyClientId = id.Text;
        row.Children.Add(id);
        var connect = new PillButton(null, "Connect") { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(connect, 1);
        row.Children.Add(connect);
        var disconnect = new PillButton(null, "Disconnect", quiet: true) { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(disconnect, 2);
        row.Children.Add(disconnect);
        panel.Children.Add(row);

        // Status: a dot and a line (connected / needs reconnect / not connected / an error).
        var statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Theme.S2 };
        var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        var status = new TextBlock { Style = SettingsUi.StyleOf("CaptionText"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, VerticalAlignment = VerticalAlignment.Center };
        statusRow.Children.Add(dot);
        statusRow.Children.Add(status);
        panel.Children.Add(statusRow);
        panel.Children.Add(new TextBlock
        {
            Text = "Spotify's Development Mode: the app's owner needs Spotify Premium, and up to 5 accounts can use it (add each under User Management in the dashboard). " +
                   "Songs are listed only for playlists you own or collaborate on (others get a Play button), and search shows up to 10 results.",
            Style = SettingsUi.StyleOf("CaptionText"), Foreground = Theme.TertiaryText, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
        });

        bool busy = false;
        void Show(string? message = null, bool error = false)
        {
            var sp = vm.Spotify;
            bool on = sp?.IsConnected == true, full = sp?.HasLibraryScopes == true;
            connect.SetContent(null!, busy ? "Waiting…" : on && !full ? "Reconnect" : "Connect");
            connect.Visibility = !on || !full || busy ? Visibility.Visible : Visibility.Collapsed;
            disconnect.Visibility = on && !busy ? Visibility.Visible : Visibility.Collapsed;
            status.Text = message ?? (!on ? "Not connected."
                : !full ? "Connected for the Like button. Reconnect to allow playlists, search and playback."
                : "Connected. Open the arrow under the media controls while Spotify plays.");
            var color = error ? Theme.DangerColor : on && full ? Theme.SuccessColor : on ? Theme.BatteryAmber : Theme.TertiaryTextColor;
            dot.Fill = new SolidColorBrush(color);
            status.Foreground = error ? Theme.DangerBrush : Theme.SecondaryText;
        }
        connect.Click += async () =>
        {
            if (vm.Spotify is not { } sp || busy) return;
            vm.SpotifyClientId = id.Text;
            busy = true;
            Show("Finish signing in in your browser…");
            var error = await sp.ConnectAsync();
            busy = false;
            Show(error, error != null);
            connectedChanged();
        };
        disconnect.Click += () =>
        {
            if (vm.Spotify is not { } sp || busy) return;
            sp.Disconnect();
            Show();
            connectedChanged();
        };
        Show();
        return panel;
    }

    // =======================================================================================================
    /// <summary>
    /// "Notch playlists": what the media player's playlist panel lists (recently played, your picks, or both with the
    /// picks pinned on top), and the picks themselves: every playlist of yours (GET /me/playlists) with a checkbox, its
    /// cover and name. Saved to settings.json at once.
    /// </summary>
    internal sealed class NotchPlaylistsCard : StackPanel
    {
        private readonly GeneralTabViewModel _vm;
        private readonly StackPanel _list = new() { Spacing = 2 };
        private readonly TextBlock _listStatus = new();
        private readonly PillButton _reload = new(null, "Refresh", quiet: true) { VerticalAlignment = VerticalAlignment.Center };
        private bool _loading;

        public NotchPlaylistsCard(GeneralTabViewModel vm)
        {
            _vm = vm;
            Spacing = Theme.S2;
            var source = new Segmented(("recent", "Recently played"), ("selected", "My selected"), ("both", "Both")) { HorizontalAlignment = HorizontalAlignment.Stretch };
            source.Value = vm.SpotifyPlaylistSource;
            source.Selected += v => vm.SpotifyPlaylistSource = v;
            Children.Add(new TextBlock { Text = "Show in the playlist panel", Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold });
            Children.Add(new TextBlock
            {
                Text = "Recently played comes from your listening history; \"Both\" pins your selected playlists above it.",
                Style = SettingsUi.StyleOf("CaptionText"), Foreground = Theme.SecondaryText, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None,
            });
            Children.Add(source);
            Children.Add(new Rectangle { Height = 1, Fill = Theme.CardBorderBrush, Margin = new Thickness(0, Theme.S1, 0, Theme.S1) });

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var headText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            headText.Children.Add(new TextBlock { Text = "My selected playlists", Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold });
            _listStatus.Style = SettingsUi.StyleOf("CaptionText");
            _listStatus.Foreground = Theme.SecondaryText;
            _listStatus.TextWrapping = TextWrapping.Wrap;
            _listStatus.TextTrimming = TextTrimming.None;
            _listStatus.Margin = Theme.GapTopS1;
            headText.Children.Add(_listStatus);
            head.Children.Add(headText);
            Grid.SetColumn(_reload, 1);
            _reload.Margin = Theme.GapLeftS3;
            _reload.Click += () => _ = LoadAsync(force: true);
            head.Children.Add(_reload);
            Children.Add(head);
            Children.Add(new ScrollViewer { Content = _list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, Theme.S2, 0) });
            Loaded += (_, _) => _ = LoadAsync(force: false);
        }

        /// <summary>The account changed (connected / disconnected): reload the list.</summary>
        public void Refresh() => _ = LoadAsync(force: true);

        private async Task LoadAsync(bool force)
        {
            if (_loading) return;
            var sp = _vm.Spotify;
            if (sp == null || !sp.IsConnected) { ShowStatus("Connect Spotify above to choose playlists."); _list.Children.Clear(); return; }
            if (!sp.HasLibraryScopes) { ShowStatus("Reconnect Spotify above to allow playlists."); _list.Children.Clear(); return; }
            _loading = true;
            ShowStatus("Loading your playlists…");
            try
            {
                var r = await sp.MyPlaylistsAsync(force);
                if (!r.Ok) { ShowStatus(sp.Describe(r.Error)); return; }
                Build(r.Value!);
            }
            finally { _loading = false; }
        }

        private void ShowStatus(string text) => _listStatus.Text = text;

        private void Build(List<SpotifyPlaylist> all)
        {
            _list.Children.Clear();
            var picked = _vm.PinnedPlaylists.ToDictionary(p => p.Id);
            // Picks that are no longer among your playlists (unfollowed) stay listed so they can be removed.
            var rows = new List<SpotifyPlaylist> { SpotifyService.LikedSongs }; // not a playlist in the API, but pickable
            rows.AddRange(all);
            foreach (var p in picked.Values.Where(p => p.Id != SpotifyService.LikedSongsId && all.All(a => a.Id != p.Id)))
                rows.Add(new SpotifyPlaylist(p.Id, p.Uri, p.Name, p.ImageUrl, null));
            foreach (var p in rows) _list.Children.Add(Row(p, picked.ContainsKey(p.Id)));
            int n = picked.Count;
            ShowStatus(rows.Count == 0 ? "You don't have any playlists yet." : n == 0 ? $"{rows.Count} playlists. Tick the ones to show in the notch." : $"{n} selected of {rows.Count}.");
            // Keep stored names / covers current (renamed playlists, new covers).
            _vm.UpdatePinnedDetails(all);
        }

        private FrameworkElement Row(SpotifyPlaylist p, bool isPicked)
        {
            var row = new Grid { MinHeight = 44, Background = Theme.TransparentBrush, Tag = SettingsWindow.NoDragTag };
            var fill = new Border { Background = Theme.CardHoverBrush, CornerRadius = Theme.RadiusSmall };
            row.Children.Add(fill);
            Motions.HoverFade(row, fill);
            var line = new Grid { Margin = new Thickness(Theme.S1, Theme.S1, Theme.S2, Theme.S1), ColumnSpacing = Theme.S3 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var box = new CheckBox { IsChecked = isPicked, MinWidth = 0, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(box);
            var cover = SpotifyPanel.Cover(p.ImageUrl, 36, 6);
            cover.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(cover, 1);
            line.Children.Add(cover);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = p.Name, Style = SettingsUi.StyleOf("BaseText"), TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(p.Owner))
                text.Children.Add(new TextBlock { Text = "by " + p.Owner, Style = SettingsUi.StyleOf("CaptionText"), Foreground = Theme.TertiaryText, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(text, 2);
            line.Children.Add(text);
            row.Children.Add(line);
            box.Checked += (_, _) => { _vm.SetPinned(p, true); Count(); };
            box.Unchecked += (_, _) => { _vm.SetPinned(p, false); Count(); };
            row.Tapped += (_, e) => { if (!IsInside(e.OriginalSource as DependencyObject, box)) box.IsChecked = box.IsChecked != true; };
            return row;
        }

        private static bool IsInside(DependencyObject? el, DependencyObject container)
        {
            for (var d = el; d != null; d = VisualTreeHelper.GetParent(d))
                if (ReferenceEquals(d, container)) return true;
            return false;
        }

        private void Count()
        {
            int n = _vm.PinnedPlaylists.Count, total = _list.Children.Count;
            ShowStatus(n == 0 ? $"{total} playlists. Tick the ones to show in the notch." : $"{n} selected of {total}.");
        }

    }

    /// <summary>A row with a bold label + caption on the left and <paramref name="control"/> on the right.</summary>
    private static FrameworkElement LabelRow(string title, string caption, FrameworkElement control)
    {
        var g = new Grid { MinHeight = 44 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = caption, Style = SettingsUi.StyleOf("CaptionText"), Foreground = Theme.SecondaryText, Margin = Theme.GapTopS1 });
        g.Children.Add(text);
        control.Margin = Theme.GapLeftS3;
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    private static Rectangle Divider() => new() { Height = 1, Fill = Theme.CardBorderBrush, Margin = new Thickness(0, Theme.S1, 0, Theme.S1) };

    // =======================================================================================================
    /// <summary>"Now Playing" sample, drawn in the hovered / chosen font (numbers as the app would draw them).</summary>
    internal sealed class SampleCard : Grid
    {
        private readonly TextBlock _caption = new(), _title = new(), _artist = new(), _time = new(), _sentence = new(), _name = new();

        public SampleCard()
        {
            Background = Theme.CardBrush;
            BorderBrush = Theme.CardEdgeBrush;
            BorderThickness = new Thickness(1);
            CornerRadius = Theme.RadiusSmall;
            Padding = Theme.PadS3;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new StackPanel();
            _caption.Text = "NOW PLAYING";
            _title.Text = "Midnight Drive";
            _artist.Text = "The Night Shift";
            _sentence.Text = "The quick brown fox jumps over the lazy dog.";
            Set(_caption, Theme.FontCaption, FontWeights.SemiBold, Theme.TertiaryText);
            Set(_title, Theme.FontTitle, FontWeights.Bold, Theme.PrimaryText);
            Set(_artist, Theme.FontBody, FontWeights.Normal, Theme.SecondaryText);
            Set(_sentence, Theme.FontBody, FontWeights.Normal, Theme.SecondaryText);
            _title.Margin = Theme.GapTopS1;
            _sentence.Margin = Theme.GapTopS2;
            _sentence.TextWrapping = TextWrapping.Wrap;
            _sentence.TextTrimming = TextTrimming.None;
            left.Children.Add(_caption);
            left.Children.Add(_title);
            left.Children.Add(_artist);
            left.Children.Add(_sentence);
            g.Children.Add(left);
            var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = Theme.GapLeftS3 };
            _time.Text = "12:34";
            Set(_time, Theme.FontTimerDigits * 0.6, FontWeights.Bold, Theme.PrimaryText);
            Typography.SetNumeralAlignment(_time, FontNumeralAlignment.Tabular);
            _time.HorizontalAlignment = HorizontalAlignment.Right;
            Set(_name, Theme.FontCaption, FontWeights.Normal, Theme.TertiaryText);
            _name.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(_time);
            right.Children.Add(_name);
            Grid.SetColumn(right, 1);
            g.Children.Add(right);
            Children.Add(g);
        }

        private static void Set(TextBlock t, double size, Windows.UI.Text.FontWeight weight, Brush fg)
        {
            t.FontSize = size;
            t.FontWeight = weight;
            t.Foreground = fg;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.IsHitTestVisible = false;
        }

        public void Show(FontChoice f)
        {
            _caption.FontFamily = f.Family("SemiBold");
            _title.FontFamily = f.Family("Bold");
            _artist.FontFamily = _sentence.FontFamily = f.Family("Regular");
            // Numbers as the app would draw them: the font's own digits when they're even, else the default font's.
            var bold = f.Family("Bold");
            _time.FontFamily = FontService.DigitsAreEven(bold) ? bold : Theme.DefaultFontFor("Bold");
            _name.FontFamily = Theme.Font;
            _name.Text = f.Name + (f.Id == FontService.DefaultId ? " (default)" : "");
        }
    }

    // =======================================================================================================
    /// <summary>
    /// Pill dropdown of the ten fonts (<see cref="FontService.Fonts"/>), in a light-dismiss popup under the
    /// pill. Every option is drawn in its own face.
    /// </summary>
    internal sealed class FontPicker : Grid
    {
        private readonly FontService _fonts;
        private readonly TextBlock _label = new();
        private readonly Popup _popup = new();
        private readonly StackPanel _items = new();
        private readonly CompSpring _open;
        private readonly Border _card = new();

        public FontPicker(FontService fonts)
        {
            _fonts = fonts;
            Height = Theme.SettingsFieldHeight;
            Background = Theme.TransparentBrush;
            Tag = SettingsWindow.NoDragTag;
            var radius = new CornerRadius(Theme.SettingsFieldHeight / 2);
            var back = new Border { Background = Theme.InputBrush, BorderBrush = Theme.HighlightBorderBrush, BorderThickness = new Thickness(1), CornerRadius = radius };
            var lighten = new Border { Background = Theme.PillLightenBrush, CornerRadius = radius };
            Children.Add(back);
            Children.Add(lighten);
            var row = new Grid { Margin = Theme.SettingsFieldPadding };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _label.FontSize = Theme.FontBody + 1;
            _label.Foreground = Theme.PrimaryText;
            _label.VerticalAlignment = VerticalAlignment.Center;
            _label.TextTrimming = TextTrimming.CharacterEllipsis;
            _label.IsHitTestVisible = false;
            row.Children.Add(_label);
            var chevron = new VectorIcon { Data = Icons.ChevronDown, Size = Theme.IconSmall, Foreground = Theme.SecondaryText, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(chevron, 1);
            row.Children.Add(chevron);
            Children.Add(row);
            Motions.HoverFade(this, lighten);
            Motions.PressScale(this);
            ToolTipService.SetToolTip(this, "Choose the app's font");
            Tapped += (_, e) => { e.Handled = true; Open(); };

            // ---- The popup card ----
            _card.Background = Theme.NotchBrush;
            _card.BorderBrush = Theme.HighlightBorderBrush;
            _card.BorderThickness = new Thickness(1);
            _card.CornerRadius = Theme.RadiusSmall;
            _card.Padding = new Thickness(Theme.S1);
            _card.MaxHeight = Theme.FontListMaxHeight;
            var scroll = new ScrollViewer { Content = _items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, Theme.S2, 0) };
            _card.Child = scroll;
            _card.KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { Close(); e.Handled = true; } };
            _card.PointerExited += (_, _) => Hovered?.Invoke(null);
            _popup.Child = _card;
            _popup.IsLightDismissEnabled = true;
            _popup.Closed += (_, _) => Hovered?.Invoke(null); // the sample returns to the chosen font

            var c = Anim.VisualOf(_card).Compositor;
            var (open, set) = Motions.Value(c, Motion.Pop, 0);
            _open = open;
            var cv = Anim.VisualOf(_card, translation: true);
            cv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", set)));
            cv.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, (1 - p.v) * -6, 0)", ("p", set)));
            cv.StartAnimation("Scale", Anim.Expr(c, "Vector3(1, 0.96 + 0.04 * p.v, 1)", ("p", set)));
            Refresh();
        }

        /// <summary>Raised with the option under the pointer (null when none) — only the sample follows it.</summary>
        public event Action<FontChoice?>? Hovered;
        public event Action<FontChoice>? Selected;

        public void Refresh()
        {
            _label.Text = _fonts.Current.Name;
            _label.FontFamily = _fonts.Current.Preview;
        }

        private void Open()
        {
            Build();
            _card.Width = ActualWidth;
            _popup.XamlRoot = XamlRoot;
            _popup.PlacementTarget = this;
            _popup.DesiredPlacement = PopupPlacementMode.Bottom;
            _popup.VerticalOffset = Theme.S1;
            _popup.IsOpen = true;
            _open.Snap(0);
            _open.SetTarget(1, Motion.Pop);
            _card.Focus(FocusState.Programmatic);
        }

        private void Close() => _popup.IsOpen = false;

        private void Build()
        {
            _items.Children.Clear();
            foreach (var f in _fonts.Fonts) _items.Children.Add(Item(f));
        }

        private FrameworkElement Item(FontChoice f)
        {
            var row = new Grid { Height = Theme.FontItemHeight, Background = Theme.TransparentBrush };
            var fill = new Border { Background = Theme.CardHoverBrush, CornerRadius = Theme.RadiusSmall };
            row.Children.Add(fill);
            Motions.HoverFade(row, fill);
            var line = new Grid { Margin = new Thickness(Theme.S3, 0, Theme.S2, 0) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new TextBlock
            {
                Text = f.Name, FontFamily = f.Preview, FontSize = Theme.FontBody + 2, Foreground = Theme.PrimaryText,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false,
            });
            if (f.Id == _fonts.Current.Id)
            {
                var check = new VectorIcon { Data = Icons.Check, Size = Theme.IconSmall, Foreground = Theme.SuccessBrush, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(check, 1);
                line.Children.Add(check);
            }
            row.Children.Add(line);
            row.PointerEntered += (_, _) => Hovered?.Invoke(f);
            row.Tapped += (_, e) =>
            {
                e.Handled = true;
                Close();
                Selected?.Invoke(f);
            };
            return row;
        }
    }
}
