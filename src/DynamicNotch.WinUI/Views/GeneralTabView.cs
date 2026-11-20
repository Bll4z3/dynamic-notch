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
    public GeneralTabView(GeneralTabViewModel vm)
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
    /// Pill dropdown of fonts. The list opens in a light-dismiss popup under the pill: Bundled, System (only
    /// fonts installed here) and All installed fonts (searchable, the first 50 matches are listed). Every
    /// option is drawn in its own face.
    /// </summary>
    internal sealed class FontPicker : Grid
    {
        private const int MaxInstalled = 50;
        private readonly FontService _fonts;
        private readonly TextBlock _label = new();
        private readonly Popup _popup = new();
        private readonly StackPanel _items = new(), _installed = new();
        private readonly TextBox _search = new();
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
            _search.PlaceholderText = "Search installed fonts";
            _search.FontFamily = Theme.Font;
            _search.FontSize = Theme.FontBody;
            _search.Margin = new Thickness(Theme.S1, 0, Theme.S1, Theme.S1);
            InputStyle.Apply(_search);
            _search.TextChanged += (_, _) => FillInstalled();

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
            Header("Bundled");
            foreach (var f in _fonts.BundledFonts) _items.Children.Add(Item(f));
            var system = _fonts.SystemFonts;
            if (system.Count > 0)
            {
                Header("System");
                foreach (var f in system) _items.Children.Add(Item(f));
            }
            Header("All installed fonts");
            _items.Children.Add(_search);
            _items.Children.Add(_installed);
            FillInstalled();
        }

        private void FillInstalled()
        {
            _installed.Children.Clear();
            var list = _fonts.SearchInstalled(_search.Text, MaxInstalled);
            foreach (var f in list) _installed.Children.Add(Item(f));
            if (list.Count == 0)
                _installed.Children.Add(new TextBlock { Text = "No fonts match", Style = SettingsUi.StyleOf("CaptionText"), Margin = new Thickness(Theme.S3, Theme.S1, 0, Theme.S2) });
            else if (list.Count == MaxInstalled)
                _installed.Children.Add(new TextBlock { Text = "Type to find more", Style = SettingsUi.StyleOf("CaptionText"), Margin = new Thickness(Theme.S3, Theme.S1, 0, Theme.S2) });
        }

        private void Header(string text) =>
            _items.Children.Add(new TextBlock { Text = text, Style = SettingsUi.StyleOf("SectionText"), Margin = new Thickness(Theme.S3, Theme.S2, 0, Theme.S1) });

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
