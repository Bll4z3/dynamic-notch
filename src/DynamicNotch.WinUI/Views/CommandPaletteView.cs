using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.Services.Palette;
using DynamicNotch.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;

namespace DynamicNotch.Views;

/// <summary>
/// The command palette inside the notch: a search bar (focused as soon as it opens), results that update as
/// you type, a footer of key hints and a soft white glow along the notch's bottom edge that reacts to what
/// you do. ↑ / ↓ move a highlight that glides between rows, Enter runs, Ctrl+Enter runs the alternate
/// action, Tab asks Claude, Esc closes (the window handles Esc).
/// <para>
/// Typing stays smooth: searching happens on a worker (the view model), result rows are reused and updated
/// in place rather than rebuilt per key (only genuinely new rows fade in), and the palette's height grows
/// straight away but waits for a short pause before it shrinks, so the notch doesn't bounce while you type.
/// </para>
/// </summary>
public sealed class CommandPaletteView : UserControl
{
    private readonly CommandPaletteViewModel _vm;
    private readonly Grid _container = new(), _root = new(), _listHost = new(), _glowHost = new();
    private readonly TextBox _box = new();
    private readonly StackPanel _rows = new();
    private readonly Border _highlight = new();
    private readonly StackPanel _askPanel = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _askQuestion = new(), _empty = new(), _footer = new();
    private readonly MarkdownView _askAnswer = new();
    private readonly ScrollViewer _askScroll = new();
    private readonly CompSpring _hy, _hShow;
    private readonly List<Row> _rowViews = new();
    private readonly PaletteGlow _glow;
    private readonly DispatcherQueueTimer _shrink;
    private double _heldHeight;
    private int _selected;
    private bool _running, _hadResults = true;
    private bool _pageMode;

    public CommandPaletteView(CommandPaletteViewModel vm, CompositionPropertySet notch)
    {
        _vm = vm;
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        Background = Theme.TransparentBrush;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        Width = Theme.PaletteWidth;

        _root.Margin = Theme.PalettePadding;
        _root.VerticalAlignment = VerticalAlignment.Top;
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            _root.RowDefinitions.Add(new RowDefinition { Height = h });
        _container.Children.Add(_root);
        // The glow sits on the palette's (= the notch's) top edge; the notch shape clips its upper half, so it
        // shines down behind the search bar.
        _glowHost.Height = 1;
        _glowHost.VerticalAlignment = VerticalAlignment.Top;
        _glowHost.IsHitTestVisible = false;
        _container.Children.Add(_glowHost);
        Content = _container;

        // ---- Search bar ----
        var bar = new Grid { Height = Theme.PaletteBarHeight };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(new VectorIcon { Data = Icons.Search, Size = Theme.IconMedium, Foreground = Theme.SecondaryText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Theme.S1, 0, Theme.S3, 0) });
        _box.PlaceholderText = "Search apps, files, maths, conversions… ( / for everything)";
        _box.FontFamily = Theme.Font;
        _box.FontSize = Theme.FontPaletteInput;
        _box.Foreground = Theme.PrimaryText;
        _box.Background = Theme.TransparentBrush;
        _box.BorderThickness = new Thickness(0);
        _box.Padding = new Thickness(0, 6, 0, 6);
        _box.VerticalAlignment = VerticalAlignment.Center;
        _box.IsSpellCheckEnabled = false;
        _box.IsTextPredictionEnabled = false; // no suggestion overlay redrawing as you type
        _box.SelectionHighlightColor = Theme.AccentBrush;
        // A borderless, see-through box in every state (the notch is its background).
        foreach (var state in new[] { "", "PointerOver", "Focused", "Disabled" })
        {
            _box.Resources["TextControlBackground" + state] = Theme.TransparentBrush;
            _box.Resources["TextControlBorderBrush" + state] = Theme.TransparentBrush;
            _box.Resources["TextControlForeground" + state] = Theme.PrimaryText;
            _box.Resources["TextControlPlaceholderForeground" + state] = Theme.TertiaryText;
        }
        _box.Resources["TextControlSelectionHighlightColor"] = Theme.AccentBrush;
        _box.MinWidth = 0;
        _box.TextChanged += (_, _) => _vm.Query = _box.Text; // cheap: the search itself runs on a worker
        _box.AddHandler(KeyDownEvent, new KeyEventHandler(OnKey), handledEventsToo: true);
        _box.GotFocus += (_, _) => FocusRequested?.Invoke();
        Grid.SetColumn(_box, 1);
        bar.Children.Add(_box);
        var esc = new Border
        {
            Background = Theme.InputBrush, BorderBrush = Theme.CardBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 1, 6, 2), VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS2,
            Child = new TextBlock { Text = "Esc", FontFamily = Theme.MonoFont, FontSize = Theme.FontCaption, Foreground = Theme.TertiaryText },
        };
        Grid.SetColumn(esc, 2);
        bar.Children.Add(esc);
        _root.Children.Add(bar);

        // ---- Results (with the gliding highlight behind them) ----
        _listHost.Margin = Theme.GapTopS1;
        _highlight.Height = Theme.PaletteRowHeight;
        _highlight.VerticalAlignment = VerticalAlignment.Top;
        _highlight.CornerRadius = Theme.RadiusSmall;
        _highlight.Background = Theme.SettingsTabFillBrush;
        _highlight.BorderBrush = Theme.CardBorderBrush;
        _highlight.BorderThickness = new Thickness(1);
        _highlight.IsHitTestVisible = false;
        _listHost.Children.Add(_highlight);
        _listHost.Children.Add(_rows);
        _empty.Style = (Style)Application.Current.Resources["CaptionText"];
        _empty.Margin = new Thickness(Theme.S2, Theme.S2, 0, Theme.S1);
        _empty.Visibility = Visibility.Collapsed;
        _listHost.Children.Add(_empty);
        Grid.SetRow(_listHost, 1);
        _root.Children.Add(_listHost);

        // ---- Ask Claude answer ----
        _askQuestion.Style = (Style)Application.Current.Resources["BaseText"];
        _askQuestion.FontFamily = Theme.FontSemiBold;
        _askQuestion.FontWeight = FontWeights.SemiBold;
        _askQuestion.TextWrapping = TextWrapping.Wrap;
        _askQuestion.TextTrimming = TextTrimming.None;
        _askQuestion.Margin = new Thickness(Theme.S1, Theme.S2, 0, Theme.S2);
        _askPanel.Children.Add(_askQuestion);
        _askAnswer.ColumnWidth = Theme.PaletteWidth - Theme.PalettePadding.Left - Theme.PalettePadding.Right - 24;
        _askScroll.Content = _askAnswer;
        _askScroll.MaxHeight = Theme.PaletteAskMaxHeight;
        _askScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _askScroll.Padding = new Thickness(Theme.S1, 0, Theme.S3, 0);
        _askPanel.Children.Add(_askScroll);
        Grid.SetRow(_askPanel, 2);
        _root.Children.Add(_askPanel);

        // ---- Footer: key hints (and the page dots when the palette is a page) ----
        _footer.Text = DefaultFooter;
        _footer.Style = (Style)Application.Current.Resources["CaptionText"];
        _footer.Foreground = Theme.TertiaryText;
        _footer.HorizontalAlignment = HorizontalAlignment.Center;
        _footer.Margin = Theme.GapTopS2;
        Grid.SetRow(_footer, 3);
        _root.Children.Add(_footer);
        Dots = new PageNavigator(notch) { HorizontalAlignment = HorizontalAlignment.Center, Margin = Theme.GapTopS1, Visibility = Visibility.Collapsed };
        Grid.SetRow(Dots, 4);
        _root.Children.Add(Dots);

        // ---- Motion ----
        var c = Anim.VisualOf(_highlight).Compositor;
        var p = c.CreatePropertySet();
        _hy = new CompSpring(p, "y", Motion.PaletteHighlight, 0, precision: 50);
        _hShow = new CompSpring(p, "s", Motion.FadeIn, 0);
        var hv = Anim.VisualOf(_highlight, translation: true);
        hv.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, p.y, 0)", ("p", p)));
        hv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.s, 0, 1)", ("p", p)));
        _glow = new PaletteGlow(_glowHost, c);
        _container.SizeChanged += (_, e) => _glow.Resize(e.NewSize.Width);
        _glow.SetIntensity(vm.GlowIntensity);
        vm.SettingsChanged += () => _glow.SetIntensity(vm.GlowIntensity); // live from Settings ▸ Command Palette

        _shrink = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _shrink.IsRepeating = false;
        _shrink.Interval = TimeSpan.FromMilliseconds(Motion.PaletteShrinkDelayMs);
        _shrink.Tick += (_, _) => { _shrink.Stop(); ApplyHeight(force: true); };
        _root.SizeChanged += (_, _) => ApplyHeight();

        vm.ResultsChanged += Rebuild;
        vm.AskChanged += OnAskChanged;
        Rebuild();
    }

    public event Action? CloseRequested, NotchSizeChanged, FocusRequested;

    /// <summary>Page dots, shown when the palette is one of the notch's pages (Settings ▸ Layout).</summary>
    public PageNavigator Dots { get; }

    /// <summary>True when the palette is a page (not a header pop-up): page dots under the key hints.</summary>
    public bool PageMode
    {
        get => _pageMode;
        set { _pageMode = value; Dots.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    }

    private const string DefaultFooter = "↑↓ select   ·   Enter open   ·   Ctrl+Enter alternate   ·   Tab ask Claude   ·   / commands";

    private double MeasuredHeight => _root.ActualHeight + Theme.PalettePadding.Top + Theme.PalettePadding.Bottom;

    /// <summary>
    /// The notch follows the measured content (called after layout, so the size is the new one). It grows at once and
    /// shrinks at once too, except while the same query's slow results (files) may still arrive: then it waits for
    /// them, at most <see cref="Motion.PaletteShrinkDelayMs"/>, so it doesn't drop and come straight back. The wait is
    /// never restarted by later keys (that used to keep the notch tall with empty space while you typed).
    /// </summary>
    private void ApplyHeight(bool force = false)
    {
        double h = MeasuredHeight;
        if (Math.Abs(h - _heldHeight) < 0.5) { _shrink.Stop(); return; }
        if (h > _heldHeight || force || !_vm.SlowPending)
        {
            _shrink.Stop();
            _heldHeight = h;
            NotchSizeChanged?.Invoke();
        }
        else if (!_shrink.IsRunning) _shrink.Start();
    }

    /// <summary>The notch size the palette wants: its width, and as tall as the bar + results (+ answer).</summary>
    public (double Width, double Height) DesiredNotchSize =>
        (Theme.PaletteWidth + 2 * Theme.S2, Math.Clamp(Math.Max(_heldHeight, Theme.PaletteMinHeight), Theme.PaletteMinHeight, Theme.AskAnswerMaxHeight));

    /// <summary>Called when the palette opens: an empty box, focused, the glow easing in.</summary>
    public void Reset(bool focus = true)
    {
        _box.Text = "";
        _askPanel.Visibility = Visibility.Collapsed;
        _listHost.Visibility = Visibility.Visible;
        _vm.Opened();
        _heldHeight = 0;
        _glow.Show(true);
        if (focus) FocusInput();
    }

    /// <summary>The palette is going away: the glow fades.</summary>
    public void Hidden() => _glow.Show(false);

    public void FocusInput()
    {
        void Go() { if (_box.Focus(FocusState.Programmatic)) _box.SelectionStart = _box.Text.Length; }
        if (_box.IsLoaded && _box.ActualWidth > 0) Go();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Go);
    }

    /// <summary>Dev: types <paramref name="text"/> into the box.</summary>
    public void DevType(string text) { _box.Text = text; _box.SelectionStart = text.Length; }

    public void ClearFocus() { if (XamlRoot != null) Focus(FocusState.Programmatic); }

    // ---- Results: rows are reused and rebound in place -------------------------------------------------------
    private void Rebuild()
    {
        var results = _vm.Results;
        var oldTitles = _rowViews.Select(r => r.Result?.Title).ToHashSet();
        string? selectedTitle = _selected < _rowViews.Count ? _rowViews[_selected].Result?.Title : null;
        while (_rowViews.Count < results.Count)
        {
            var row = new Row();
            row.PointerEntered += (_, _) => { int i = _rowViews.IndexOf(row); if (i >= 0) Select(i, animate: true); };
            row.Tapped += async (_, e) => { e.Handled = true; int i = _rowViews.IndexOf(row); if (i < 0) return; Select(i, animate: true); await RunSelected(alt: false); };
            _rowViews.Add(row);
            _rows.Children.Add(row);
        }
        while (_rowViews.Count > results.Count)
        {
            _rows.Children.RemoveAt(_rowViews.Count - 1);
            _rowViews.RemoveAt(_rowViews.Count - 1);
        }
        int entering = 0;
        for (int i = 0; i < results.Count; i++)
        {
            bool isNew = !oldTitles.Contains(results[i].Title);
            _rowViews[i].Bind(results[i], isNew ? entering++ * Motion.PaletteRowStagger : -1);
        }
        bool none = results.Count == 0;
        bool typed = _vm.Query.Trim().Length > 0;
        _empty.Visibility = none && typed && !_vm.IsAskMode ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = "No matches. Press Tab to ask Claude.";
        if (none && typed && _hadResults) _glow.Empty();
        _hadResults = !none || !typed;
        // Keep the selection on the same item when it's still near the top; otherwise the first row.
        int keep = selectedTitle == null ? -1 : results.ToList().FindIndex(r => r.Title == selectedTitle);
        _selected = keep >= 0 && keep < 3 ? keep : 0;
        _hShow.SetTarget(none || _vm.IsAskMode ? 0 : 1, Motion.FadeIn);
        Select(_selected, animate: keep >= 0);
        // Measure after this pass's layout (not the previous one's), then resize. When the size is unchanged no
        // SizeChanged fires, so this is also what releases a shrink held for slow results that came back empty.
        _root.UpdateLayout();
        ApplyHeight();
    }

    private void Select(int index, bool animate)
    {
        if (_rowViews.Count == 0) return;
        _selected = Math.Clamp(index, 0, _rowViews.Count - 1);
        double y = 0;
        for (int k = 0; k < _selected; k++) y += _rowViews[k].Height;
        _highlight.Height = _rowViews[_selected].Height;
        if (animate) _hy.SetTarget(y, Motion.PaletteHighlight); else _hy.Snap(y);
        for (int k = 0; k < _rowViews.Count; k++) _rowViews[k].Selected = k == _selected;
    }

    private async void OnKey(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Down:
                e.Handled = true;
                Select(_selected + 1 >= _rowViews.Count ? 0 : _selected + 1, animate: true);
                _glow.Move(+1);
                break;
            case VirtualKey.Up:
                e.Handled = true;
                Select(_selected - 1 < 0 ? _rowViews.Count - 1 : _selected - 1, animate: true);
                _glow.Move(-1);
                break;
            case VirtualKey.Back:
            case VirtualKey.Delete:
                _glow.Backspace();
                break;
            case VirtualKey.Tab:
                e.Handled = true;
                if (_box.Text.Trim().Length > 0) { _glow.Run(); await _vm.AskAsync(_box.Text); }
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                _glow.Run();
                if (_vm.IsAskMode && !_vm.IsAsking && _vm.AskText.Length > 0 && !_box.Text.StartsWith('?')) { CommandPaletteViewModel.Copy(_vm.AskText); break; }
                if (_box.Text.TrimStart().StartsWith('?')) { await _vm.AskAsync(_box.Text); break; }
                await RunSelected(ctrl);
                break;
            case VirtualKey.Escape or VirtualKey.Left or VirtualKey.Right or VirtualKey.Home or VirtualKey.End
                or VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu or VirtualKey.LeftWindows:
                break;
            default:
                _glow.Type(CaretX());
                break;
        }
    }

    /// <summary>About where the caret is (for the typing ripple): the box's left plus the text's width so far.</summary>
    private double CaretX()
    {
        double left = Theme.PalettePadding.Left + Theme.S1 + Theme.IconMedium + Theme.S3;
        return Math.Min(left + _box.Text.Length * Theme.FontPaletteInput * 0.55, Math.Max(left, ActualWidth - 40));
    }

    private async Task RunSelected(bool alt)
    {
        if (_running || _selected < 0 || _selected >= _rowViews.Count || _rowViews[_selected].Result is not { } result) return;
        var row = _rowViews[_selected];
        _running = true;
        try
        {
            var outcome = await _vm.RunAsync(result, alt);
            switch (outcome)
            {
                case PaletteOutcome.Copied:
                    row.ShowCopied();
                    await Task.Delay(Motion.PaletteCopiedMs);
                    CloseRequested?.Invoke();
                    break;
                case PaletteOutcome.Close:
                    row.Pop();
                    await Task.Delay(Motion.PaletteLaunchMs);
                    CloseRequested?.Invoke();
                    break;
                case PaletteOutcome.Fill when result.Fill != null:
                    _box.Text = result.Fill;
                    FocusInput();
                    break;
            }
        }
        finally { _running = false; }
    }

    private void OnAskChanged()
    {
        bool ask = _vm.IsAskMode;
        _askPanel.Visibility = ask ? Visibility.Visible : Visibility.Collapsed;
        _listHost.Visibility = ask ? Visibility.Collapsed : Visibility.Visible;
        if (!ask) { _footer.Text = DefaultFooter; return; } // typing again after an answer: the search hints come back
        _askQuestion.Text = _vm.AskQuestion;
        string text = _vm.AskText.Length > 0 ? _vm.AskText : _vm.IsAsking ? "Thinking…" : "";
        _askAnswer.Render(text, animate: false);
        _footer.Text = _vm.IsAsking ? "Esc closes" : "Enter copies the answer   ·   type to search again   ·   Esc closes";
        if (_vm.IsAsking) _askScroll.ChangeView(null, _askScroll.ScrollableHeight, null, disableAnimation: true);
    }

    /// <summary>One result row, reused across keystrokes: icon / image / swatch · title + subtitle · key hint.</summary>
    private sealed class Row : Grid
    {
        private readonly TextBlock _title = new(), _subtitle = new(), _hint = new();
        private readonly Grid _iconHost = new();
        private readonly Border _back = new();
        private readonly VectorIcon _icon = new();
        private readonly Image _img = new() { Stretch = Stretch.Uniform };
        private readonly VectorIcon _check = new() { Data = Icons.Check, Size = Theme.IconMedium, Foreground = Theme.SuccessBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly CompSpring _show, _pop, _copied;
        private int _token;

        public Row()
        {
            Height = Theme.PaletteRowHeight;
            Background = Theme.TransparentBrush;
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Hand);

            double tile = Theme.PaletteIconTile;
            _iconHost.Width = _iconHost.Height = tile;
            _iconHost.Margin = new Thickness(Theme.S2, 0, Theme.S3, 0);
            _iconHost.VerticalAlignment = VerticalAlignment.Center;
            _back.CornerRadius = new CornerRadius(8);
            _back.BorderBrush = Theme.TileBorderBrush;
            _iconHost.Children.Add(_back);
            _icon.Size = Theme.IconSmall + 1;
            _icon.Foreground = Theme.PrimaryText;
            _icon.HorizontalAlignment = HorizontalAlignment.Center;
            _icon.VerticalAlignment = VerticalAlignment.Center;
            _iconHost.Children.Add(_icon);
            _img.Width = _img.Height = tile - 6;
            _img.HorizontalAlignment = HorizontalAlignment.Center;
            _img.VerticalAlignment = VerticalAlignment.Center;
            _iconHost.Children.Add(_img);
            _iconHost.Children.Add(_check);
            Children.Add(_iconHost);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _title.Style = (Style)Application.Current.Resources["BaseText"];
            _title.TextTrimming = TextTrimming.CharacterEllipsis;
            _subtitle.Style = (Style)Application.Current.Resources["CaptionText"];
            _subtitle.Foreground = Theme.SecondaryText;
            _subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
            _subtitle.Margin = new Thickness(0, 1, 0, 0);
            text.Children.Add(_title);
            text.Children.Add(_subtitle);
            SetColumn(text, 1);
            Children.Add(text);

            _hint.Style = (Style)Application.Current.Resources["CaptionText"];
            _hint.Foreground = Theme.TertiaryText;
            _hint.VerticalAlignment = VerticalAlignment.Center;
            _hint.Margin = new Thickness(Theme.S3, 0, Theme.S3, 0);
            _hint.Opacity = 0;
            SetColumn(_hint, 2);
            Children.Add(_hint);

            var v = Anim.VisualOf(this, translation: true);
            var c = v.Compositor;
            var p = c.CreatePropertySet();
            _show = new CompSpring(p, "s", Motion.PaletteRowIn, 1);
            _pop = new CompSpring(p, "k", Motion.Pop, 0);
            _copied = new CompSpring(p, "c", Motion.Pop, 0);
            v.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.s, 0, 1)", ("p", p)));
            v.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, (1 - Clamp(p.s, 0, 1)) * 6, 0)", ("p", p)));
            var iv = Anim.VisualOf(_iconHost);
            iv.CenterPoint = new Vector3((float)tile / 2, (float)tile / 2, 0);
            iv.StartAnimation("Scale", Anim.Expr(c, "Vector3(1 + 0.28 * p.k, 1 + 0.28 * p.k, 1)", ("p", p)));
            var cv = Anim.VisualOf(_check);
            cv.CenterPoint = new Vector3((float)Theme.IconMedium / 2, (float)Theme.IconMedium / 2, 0);
            cv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.c, 0, 1)", ("p", p)));
            cv.StartAnimation("Scale", Anim.Expr(c, "Vector3(0.4 + 0.6 * p.c, 0.4 + 0.6 * p.c, 1)", ("p", p)));
        }

        public PaletteResult? Result { get; private set; }

        /// <summary>Shows <paramref name="r"/> in this row; <paramref name="enterDelay"/> ≥ 0 fades it in (a new result).</summary>
        public void Bind(PaletteResult r, double enterDelay)
        {
            bool sameItem = Result != null && Result.Title == r.Title && Result.Icon == r.Icon && Result.Swatch == r.Swatch;
            Result = r;
            Height = r.IsAnswer ? Theme.PaletteAnswerHeight : Theme.PaletteRowHeight;
            _title.Text = r.Title;
            _title.FontFamily = r.IsAnswer ? Theme.FontBold : Theme.FontSemiBold;
            _title.FontWeight = r.IsAnswer ? FontWeights.Bold : FontWeights.SemiBold;
            _title.FontSize = r.IsAnswer ? Theme.FontPaletteAnswer : Theme.FontBody;
            _subtitle.Text = r.Subtitle ?? "";
            _subtitle.Visibility = string.IsNullOrEmpty(r.Subtitle) ? Visibility.Collapsed : Visibility.Visible;
            _hint.Text = r.Hint;
            _hint.Foreground = Theme.TertiaryText;
            _copied.Snap(0);
            if (sameItem && _img.Source == null && r.Swatch == null && (r.Image != null || r.ImageLoader != null))
            {
                // Same row, but its icon hadn't arrived yet when the results refreshed: fetch it now (cached).
                int t = ++_token;
                if (r.Image != null) _ = ShowImage(r.Image, t); else _ = LoadImage(r, t);
            }
            if (!sameItem)
            {
                int token = ++_token;
                _back.Background = r.Swatch is { } sw ? new SolidColorBrush(sw) : Theme.TileBrush;
                _back.BorderThickness = new Thickness(1);
                _icon.Data = r.Icon;
                _icon.Visibility = r.Swatch == null ? Visibility.Visible : Visibility.Collapsed;
                _img.Source = null;
                if (r.Swatch == null)
                {
                    if (r.Image != null) _ = ShowImage(r.Image, token);
                    else if (r.ImageLoader != null) _ = LoadImage(r, token);
                }
            }
            if (enterDelay >= 0 && Anim.Enabled) { _show.Snap(0); _show.SetTarget(1, Motion.PaletteRowIn, enterDelay); }
        }

        private async Task LoadImage(PaletteResult r, int token)
        {
            try
            {
                var bmp = await r.ImageLoader!();
                if (token != _token || bmp == null) return;
                r.Image = bmp;
                await ShowImage(bmp, token);
            }
            catch { }
        }

        private async Task ShowImage(Windows.Graphics.Imaging.SoftwareBitmap bmp, int token)
        {
            var src = await ImageLoader.ToSourceAsync(bmp);
            if (token != _token || src == null) return;
            _img.Source = src;
            _icon.Visibility = Visibility.Collapsed;
            _back.Background = Theme.TransparentBrush;
            _back.BorderThickness = new Thickness(0);
        }

        public bool Selected { set => _hint.Opacity = value ? 1 : 0; }

        /// <summary>A launched app / opened item: its icon swells for a moment.</summary>
        public void Pop()
        {
            if (!Anim.Enabled) return;
            _pop.Snap(0);
            _pop.SetTarget(1, Motion.Pop);
        }

        /// <summary>Copied: a check pops in over the icon and the hint says so.</summary>
        public void ShowCopied()
        {
            _hint.Text = "Copied";
            _hint.Foreground = Theme.SuccessBrush;
            _hint.Opacity = 1;
            _copied.Snap(0);
            _copied.SetTarget(1, Motion.Pop);
        }
    }
}

/// <summary>
/// The soft white glow along the top edge of the palette while it is open, reacting to input:
/// a small ripple where you type, a dip and inward squeeze on Backspace, a bright sweep across on Enter, a
/// faint shimmer when moving between results and a short flicker when nothing matches. Everything runs on
/// the compositor (keyframes and springs on a property set), so typing never waits on it.
/// </summary>
internal sealed class PaletteGlow
{
    private const float GlowHeight = 64, BaseLevel = 0.55f;
    private readonly Compositor _c;
    private readonly CompositionPropertySet _p;
    private readonly CompSpring _level, _width;
    private readonly SpriteVisual _base, _sweep, _ripple;
    private float _w = 600;

    public PaletteGlow(UIElement host, Compositor c)
    {
        _c = c;
        _p = c.CreatePropertySet();
        _p.InsertScalar("S", 0);   // sweep position 0..1
        _p.InsertScalar("SA", 0);  // sweep strength
        _p.InsertScalar("F", 1);   // flicker multiplier
        _p.InsertScalar("W", _w);
        _p.InsertScalar("I", 1);   // intensity boost above 100% (below 100% the whole layer's opacity scales)
        _level = new CompSpring(_p, "L", Motion.PaletteGlow, 0);
        _width = new CompSpring(_p, "X", Motion.PaletteGlow, 1);

        var root = _root = c.CreateContainerVisual();
        _base = Glow(0x5C);
        _base.StartAnimation("Size", Expr($"Vector2(P.W * 1.1, {Anim.F(GlowHeight)})"));
        _base.StartAnimation("Offset", Expr($"Vector3(-P.W * 0.05, {Anim.F(-GlowHeight / 2)}, 0)"));
        _base.StartAnimation("CenterPoint", Expr($"Vector3(P.W * 0.55, {Anim.F(GlowHeight / 2)}, 0)"));
        _base.StartAnimation("Scale", Expr($"Vector3(P.X, 0.85 + 0.15 * Clamp(P.L / {Anim.F(BaseLevel)}, 0, 1.6), 1)"));
        _base.StartAnimation("Opacity", Expr("Clamp(P.L * P.I, 0, 1) * P.F"));
        root.Children.InsertAtTop(_base);

        _sweep = Glow(0xC0);
        _sweep.Size = new Vector2(220, GlowHeight);
        _sweep.StartAnimation("Offset", Expr($"Vector3(-220 + P.S * (P.W + 220), {Anim.F(-GlowHeight / 2)}, 0)"));
        _sweep.StartAnimation("Opacity", Expr("Clamp(Sin(Clamp(P.S, 0, 1) * 3.14159) * P.SA * P.I, 0, 1) * P.F * Clamp(P.L * 2, 0, 1)"));
        root.Children.InsertAtTop(_sweep);

        _ripple = Glow(0xA0);
        _ripple.Size = new Vector2(120, 48);
        _ripple.CenterPoint = new Vector3(60, 24, 0);
        _ripple.Opacity = 0;
        root.Children.InsertAtTop(_ripple);

        ElementCompositionPreview.SetElementChildVisual(host, root);

        ExpressionAnimation Expr(string e)
        {
            var x = _c.CreateExpressionAnimation(e);
            x.SetReferenceParameter("P", _p);
            return x;
        }
    }

    private SpriteVisual Glow(byte alpha)
    {
        var brush = _c.CreateRadialGradientBrush();
        brush.ColorStops.Add(_c.CreateColorGradientStop(0, Windows.UI.Color.FromArgb(alpha, 255, 255, 255)));
        brush.ColorStops.Add(_c.CreateColorGradientStop(0.5f, Windows.UI.Color.FromArgb((byte)(alpha / 3), 255, 255, 255)));
        brush.ColorStops.Add(_c.CreateColorGradientStop(1, Windows.UI.Color.FromArgb(0, 255, 255, 255)));
        var v = _c.CreateSpriteVisual();
        v.Brush = brush;
        return v;
    }

    public void Resize(double width) { _w = (float)Math.Max(100, width); _p.InsertScalar("W", _w); }

    private readonly ContainerVisual _root;

    /// <summary>0 = no glow, 1 = the original strength, up to 2 = brighter.</summary>
    public void SetIntensity(double intensity)
    {
        intensity = Math.Clamp(intensity, 0, 2);
        _root.Opacity = (float)Math.Min(1, intensity);
        _p.InsertScalar("I", (float)Math.Max(1, intensity));
    }

    public void Show(bool on) => _level.SetTarget(on ? BaseLevel : 0, Motion.PaletteGlow);

    /// <summary>A key typed: a soft pulse, and a ripple under the caret.</summary>
    public void Type(double x)
    {
        if (!Anim.Enabled) return;
        _level.Snap(Math.Min(1, _level.Value + 0.12));
        _level.SetTarget(BaseLevel, Motion.PaletteGlow);
        _ripple.Offset = new Vector3((float)x - 60, -24, 0);
        var ease = _c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.8f), new Vector2(0.3f, 1f));
        var s = _c.CreateVector3KeyFrameAnimation();
        s.InsertKeyFrame(0, new Vector3(0.35f, 0.5f, 1));
        s.InsertKeyFrame(1, new Vector3(1.5f, 1.1f, 1), ease);
        s.Duration = TimeSpan.FromMilliseconds(420);
        var o = _c.CreateScalarKeyFrameAnimation();
        o.InsertKeyFrame(0, 0.7f);
        o.InsertKeyFrame(1, 0, ease);
        o.Duration = TimeSpan.FromMilliseconds(420);
        _ripple.StartAnimation("Scale", s);
        _ripple.StartAnimation("Opacity", o);
    }

    /// <summary>Backspace: a quick dim and the glow drawing inwards, then back.</summary>
    public void Backspace()
    {
        if (!Anim.Enabled) return;
        _level.Snap(Math.Max(0.2, _level.Value - 0.2));
        _level.SetTarget(BaseLevel, Motion.PaletteGlow);
        _width.Snap(0.86);
        _width.SetTarget(1, Motion.PaletteGlow);
    }

    /// <summary>Enter / running a command: a bright sweep across the whole edge.</summary>
    public void Run() => Sweep(1, 520);

    /// <summary>Moving between results: a faint, quicker shimmer in that direction.</summary>
    public void Move(int dir)
    {
        if (!Anim.Enabled) return;
        _level.Snap(Math.Min(1, _level.Value + 0.06));
        _level.SetTarget(BaseLevel, Motion.PaletteGlow);
        Sweep(0.35, 320, reverse: dir < 0);
    }

    /// <summary>No results: a short dim with a gentle flicker.</summary>
    public void Empty()
    {
        if (!Anim.Enabled) return;
        var f = _c.CreateScalarKeyFrameAnimation();
        f.InsertKeyFrame(0, 1);
        f.InsertKeyFrame(0.25f, 0.45f);
        f.InsertKeyFrame(0.5f, 0.8f);
        f.InsertKeyFrame(0.75f, 0.5f);
        f.InsertKeyFrame(1, 1);
        f.Duration = TimeSpan.FromMilliseconds(460);
        _p.StartAnimation("F", f);
    }

    private void Sweep(double strength, int ms, bool reverse = false)
    {
        if (!Anim.Enabled) return;
        _p.InsertScalar("SA", (float)strength);
        var a = _c.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(0, reverse ? 1 : 0);
        a.InsertKeyFrame(1, reverse ? 0 : 1, _c.CreateCubicBezierEasingFunction(new Vector2(0.3f, 0.1f), new Vector2(0.3f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(ms);
        _p.StartAnimation("S", a);
    }
}
