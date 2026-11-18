using System.Collections.Specialized;
using System.ComponentModel;
using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;

namespace DynamicNotch.Views;

/// <summary>
/// Page 3, "Ask" (same layout as the WPF page): its own header (Claude logo + "Ask Claude" · clock ·
/// settings), then two panels that cross-fade with springs: the empty box (attachment chip, text box,
/// Send, Select area, hotkey caption, page dots) and the conversation (stacked question / answer
/// turns in a scroller, then the follow-up row with Stop, Send, Select area and New chat). Reports the
/// notch size it wants, which the window springs to, and keeps the conversation scrolled to the newest
/// turn unless the user scrolled up. A Control, so it can hold keyboard focus while the notch owns it.
/// </summary>
public sealed class AskPage : UserControl
{
    private readonly AskViewModel _ask;
    private readonly Grid _root = new(), _panels = new(), _header = new();
    private readonly StackPanel _idle = new(), _conversation = new(), _turns = new(), _idleRow2 = new();
    private readonly Grid _idleInputRow = new();
    private readonly ScrollViewer _scroll = new();
    private readonly TextBox _input, _followUp;
    private readonly PillButton _send1, _send2;
    private readonly IconButton _stop;
    private readonly Grid _chip1, _chip2;
    private readonly ImageThumb _thumb1 = new(), _thumb2 = new();
    private readonly Dictionary<FrameworkElement, CompSpring> _fades = new();
    private readonly DispatcherQueueTimer _hideTimer;
    private FrameworkElement? _shown;
    private bool _stickToBottom = true, _syncingText;

    public AskPage(AskViewModel ask, StatusViewModel status, CompositionPropertySet notch)
    {
        _ask = ask;
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        Background = Theme.TransparentBrush;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;

        _root.Margin = Theme.AskPadding;
        _root.VerticalAlignment = VerticalAlignment.Top;
        _root.RowDefinitions.Add(new RowDefinition { Height = Theme.HeaderRow });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = _root;

        // ---- Header: Claude logo + "Ask Claude" · clock · settings ----
        var title = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new ClaudeLogo(Theme.IconSmall, Theme.PrimaryText));
        title.Children.Add(new TextBlock { Text = "Ask Claude", Style = TextStyle("SectionText"), Foreground = Theme.PrimaryText, Margin = Theme.GapLeftS2 });
        _header.Children.Add(title);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var clock = new CrossfadeText { VerticalAlignment = VerticalAlignment.Center };
        clock.SetStyle(t => t.Style = TextStyle("ClockTimeText"));
        clock.Text = status.TimeText;
        status.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(StatusViewModel.TimeText) or "" or null) clock.Text = status.TimeText; };
        right.Children.Add(clock);
        right.Children.Add(new IconButton(26, Theme.IconSmall) { Icon = Icons.Settings, Foreground = Theme.SecondaryText, Command = ask.OpenSettingsCommand, Tooltip = "Settings", Margin = Theme.GapLeftS1 });
        _header.Children.Add(right);
        _root.Children.Add(_header);

        // ---- Two panels that cross-fade ----
        Grid.SetRow(_panels, 1);
        _root.Children.Add(_panels);

        // Idle: chip · text box / Send / Select area · caption + dots
        _idle.Margin = Theme.GapTopS2;
        _chip1 = Chip(_thumb1);
        _chip1.Margin = Theme.AskChipMargin;
        _idle.Children.Add(_chip1);
        _input = Input("Ask anything, or about your screen…");
        _send1 = new PillButton(Icons.ArrowUp, "Send") { UseAccent = true, Command = ask.SendCommand, Margin = Theme.GapLeftS2, VerticalAlignment = VerticalAlignment.Center };
        Row(_idleInputRow, _input, _send1, new PillButton(Icons.Crop, "Select area", quiet: true) { Command = ask.SelectAreaCommand, Margin = Theme.GapLeftS2, VerticalAlignment = VerticalAlignment.Center });
        _idle.Children.Add(_idleInputRow);
        _idleRow2.Margin = Theme.GapTopS2;
        _idleRow2.Children.Add(new TextBlock { Text = $"{AppConfig.AskHotkeyLabel}  ·  Esc to cancel", Style = TextStyle("CaptionText"), HorizontalAlignment = HorizontalAlignment.Center });
        Dots = new PageNavigator(notch) { HorizontalAlignment = HorizontalAlignment.Center, Margin = Theme.GapTopS1 };
        _idleRow2.Children.Add(Dots);
        _idle.Children.Add(_idleRow2);
        _panels.Children.Add(_idle);

        // Conversation: turns · chip · follow-up row
        _conversation.Margin = Theme.GapTopS2;
        _conversation.Visibility = Visibility.Collapsed;
        _scroll.MaxHeight = Theme.AskAnswerScrollMaxHeight;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollMode = ScrollMode.Enabled;
        _scroll.HorizontalScrollMode = ScrollMode.Disabled;
        _scroll.IsTabStop = false;
        _scroll.Content = _turns;
        _conversation.Children.Add(_scroll);
        _chip2 = Chip(_thumb2);
        _chip2.Margin = Theme.GapTopS1;
        _conversation.Children.Add(_chip2);
        _followUp = Input("Ask a follow-up…");
        _stop = new IconButton(26, Theme.IconSmall) { Icon = Icons.Stop, Foreground = Theme.PrimaryText, Command = ask.StopCommand, Tooltip = "Stop", Margin = Theme.GapLeftS1, VerticalAlignment = VerticalAlignment.Center };
        _send2 = new PillButton(Icons.ArrowUp, "Send") { UseAccent = true, Command = ask.SendCommand, Margin = Theme.GapLeftS2, VerticalAlignment = VerticalAlignment.Center };
        var followRow = new Grid { Margin = Theme.GapTopS1 };
        Row(followRow, _followUp, _stop, _send2,
            new PillButton(Icons.Crop, "Select area", quiet: true) { Command = ask.SelectAreaCommand, Margin = Theme.GapLeftS2, VerticalAlignment = VerticalAlignment.Center },
            new PillButton(Icons.NewChat, "New chat", quiet: true) { Command = ask.NewChatCommand, Margin = Theme.GapLeftS2, VerticalAlignment = VerticalAlignment.Center });
        _conversation.Children.Add(followRow);
        _panels.Children.Add(_conversation);

        // Panel cross-fade springs (opacity + a small rise), on the compositor.
        foreach (var panel in new FrameworkElement[] { _idle, _conversation })
        {
            var v = Anim.VisualOf(panel, translation: true);
            var (spring, set) = Motions.Value(v.Compositor, Motion.FadeIn, panel == _idle ? 1 : 0);
            v.StartAnimation("Opacity", Anim.Expr(v.Compositor, "Clamp(p.v, 0, 1)", ("p", set)));
            var rise = Anim.Expr(v.Compositor, "Vector3(0, (1 - Clamp(p.v, 0, 1)) * R, 0)", ("p", set));
            rise.SetScalarParameter("R", (float)Motion.StaggerRise);
            v.StartAnimation("Translation", rise);
            _fades[panel] = spring;
        }
        _shown = _idle;
        _hideTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); foreach (var (p, s) in _fades) if (s.Target == 0) p.Visibility = Visibility.Collapsed; };

        _scroll.ViewChanged += (_, e) =>
        {
            if (!e.IsIntermediate) _stickToBottom = _scroll.VerticalOffset >= _scroll.ScrollableHeight - 4;
        };
        _turns.SizeChanged += (_, _) => { if (_stickToBottom) ScrollToEnd(); };
        _root.SizeChanged += (_, _) => NotchSizeChanged?.Invoke();

        ask.PropertyChanged += OnAskChanged;
        ask.Turns.CollectionChanged += OnTurnsChanged;
        OnAskChanged(null, new PropertyChangedEventArgs(""));
        ShowState(ask.State, animate: false);
    }

    /// <summary>Raised when the size the notch should take on this page changes.</summary>
    public event Action? NotchSizeChanged;
    /// <summary>Raised when the user clicks into a text box or the answer (the window then takes focus).</summary>
    public event Action? FocusRequested;
    /// <summary>Hovering a screenshot thumbnail: show / hide the enlarged preview card.</summary>
    public event Action<ImageThumb>? ThumbPreviewRequested;
    public event Action? ThumbPreviewEnded;

    public PageNavigator Dots { get; }

    /// <summary>Blocks of the staggered entrance (same order as WPF: header, input row, caption + dots).</summary>
    public IEnumerable<(FrameworkElement element, int order)> StaggerBlocks =>
        new (FrameworkElement, int)[] { (_header, 0), (_idleInputRow, 1), (_idleRow2, 2) };

    /// <summary>The notch size for the current state and content (clamped to the Ask size tokens).</summary>
    public (double Width, double Height) DesiredNotchSize
    {
        get
        {
            double content = _root.ActualHeight + Theme.AskPadding.Top + Theme.AskPadding.Bottom;
            return _ask.State switch
            {
                AskState.Idle => (Theme.AskIdleWidth, Math.Clamp(content, Theme.AskIdleHeight, Theme.AskAnswerMaxHeight)),
                AskState.Thinking => (Theme.AskAnswerWidth, Math.Clamp(content, Theme.AskThinkingHeight, Theme.AskAnswerMaxHeight)),
                _ => (Theme.AskAnswerWidth, Math.Clamp(content, Theme.AskIdleHeight, Theme.AskAnswerMaxHeight)),
            };
        }
    }

    // ---- Focus -----------------------------------------------------------------------------------
    /// <summary>Focuses the box for the current state (idle input or follow-up), caret at the end.</summary>
    public void FocusInput()
    {
        var box = _ask.State == AskState.Idle ? _input : _followUp;
        void Go()
        {
            if (!box.Focus(FocusState.Programmatic)) return;
            box.SelectionStart = box.Text.Length;
        }
        if (box.IsLoaded && box.Visibility == Visibility.Visible && box.ActualWidth > 0) Go();
        else DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Go);
    }

    public bool HasTextFocus => _input.FocusState != FocusState.Unfocused || _followUp.FocusState != FocusState.Unfocused;

    /// <summary>While the notch owns focus, keep it on something in this page so keys keep arriving.</summary>
    public void KeepFocus()
    {
        if (_ask.State is AskState.Idle or AskState.Answer or AskState.Error) FocusInput();
        else Focus(FocusState.Programmatic);
    }

    /// <summary>Drops keyboard focus from the text boxes (the notch is handing focus back).</summary>
    public void ClearFocus()
    {
        if (XamlRoot == null) return;
        // Park focus on the page itself (no caret, no keys typed into a box).
        Focus(FocusState.Programmatic);
    }

    // ---- View model ----------------------------------------------------------------------------
    private void OnAskChanged(object? sender, PropertyChangedEventArgs e)
    {
        string? n = e.PropertyName;
        bool all = string.IsNullOrEmpty(n);
        if (all || n == nameof(AskViewModel.State)) ShowState(_ask.State, animate: !all);
        if (all || n == nameof(AskViewModel.InputText)) SyncText();
        if (all || n == nameof(AskViewModel.IsSendActive)) _send1.Active = _send2.Active = _ask.IsSendActive;
        if (all || n == nameof(AskViewModel.IsBusy)) _stop.Visibility = _ask.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        if (all || n == nameof(AskViewModel.AttachmentThumb))
        {
            var thumb = _ask.AttachmentThumb;
            _thumb1.SetBitmap(thumb);
            _thumb2.SetBitmap(thumb);
            bool has = thumb != null;
            _chip1.Visibility = _chip2.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (has && !all) { Motions.PopIn(_chip1); Motions.PopIn(_chip2); }
        }
    }

    private void SyncText()
    {
        _syncingText = true;
        foreach (var box in new[] { _input, _followUp })
            if (box.Text != _ask.InputText) { box.Text = _ask.InputText; box.SelectionStart = box.Text.Length; }
        _syncingText = false;
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems != null:
                foreach (TurnViewModel t in e.NewItems) _turns.Children.Add(new TurnView(t, this));
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems != null:
                foreach (TurnViewModel t in e.OldItems)
                    if (_turns.Children.OfType<TurnView>().FirstOrDefault(v => v.Turn == t) is { } view) { view.Detach(); _turns.Children.Remove(view); }
                break;
            default:
                foreach (var view in _turns.Children.OfType<TurnView>()) view.Detach();
                _turns.Children.Clear();
                foreach (var t in _ask.Turns) _turns.Children.Add(new TurnView(t, this));
                break;
        }
        // A new turn: jump to it (older turns stay reachable by scrolling up).
        _stickToBottom = true;
        ScrollToEnd();
    }

    private void ScrollToEnd() =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _scroll.ChangeView(null, _scroll.ScrollableHeight, null, disableAnimation: true));

    private void ShowState(AskState state, bool animate)
    {
        _root.Width = (state == AskState.Idle ? Theme.AskIdleWidth : Theme.AskAnswerWidth) - Theme.AskPadding.Left - Theme.AskPadding.Right;
        var target = state == AskState.Idle ? (FrameworkElement)_idle : _conversation;
        if (!ReferenceEquals(target, _shown) || !animate)
        {
            double hideAfter = 0;
            foreach (var (panel, spring) in _fades)
            {
                bool on = ReferenceEquals(panel, target);
                if (on) panel.Visibility = Visibility.Visible;
                if (!animate) { spring.Snap(on ? 1 : 0); if (!on) panel.Visibility = Visibility.Collapsed; }
                else
                {
                    spring.SetTarget(on ? 1 : 0, on ? Motion.FadeIn : Motion.FadeOut);
                    if (!on) hideAfter = Math.Max(hideAfter, spring.Model.RemainingDuration);
                }
            }
            if (hideAfter > 0) { _hideTimer.Interval = TimeSpan.FromSeconds(hideAfter); _hideTimer.Start(); }
            _shown = target;
        }
        NotchSizeChanged?.Invoke();
    }

    // ---- Building blocks -------------------------------------------------------------------------
    internal static Style TextStyle(string key) => (Style)Application.Current.Resources[key];

    /// <summary>The Ask text box (the WPF AskInput style): dark fill, hairline border that brightens on focus, global radius.</summary>
    private TextBox Input(string placeholder)
    {
        var box = new TextBox
        {
            PlaceholderText = placeholder,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Theme.Font,
            FontSize = Theme.FontBody,
            Padding = Theme.AskInputPadding,
            MinHeight = Theme.AskInputMinHeight,
            MaxHeight = Theme.AskInputMaxHeight,
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.AskInputMinHeight / 2),
            SelectionHighlightColor = Theme.AccentBrush,
            IsSpellCheckEnabled = false,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        InputStyle.Apply(box);
        box.TextChanged += (_, _) => { if (!_syncingText) _ask.InputText = box.Text; };
        // Enter sends; Shift+Enter is a new line.
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter) return;
            bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (shift) return;
            if (_ask.SendCommand.CanExecute(null)) _ask.SendCommand.Execute(null);
            e.Handled = true;
        };
        WatchClick(box);
        return box;
    }

    /// <summary>A click into a text box or the answer asks the window for keyboard focus.</summary>
    internal void WatchClick(UIElement el) =>
        el.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => FocusRequested?.Invoke()), handledEventsToo: true);

    /// <summary>Attached screenshot: the thumbnail in the image's own shape with a small × in the corner.</summary>
    private Grid Chip(ImageThumb thumb)
    {
        var chip = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
        chip.Children.Add(thumb);
        var close = IconButton.Overlay(Icons.Close, "Remove screenshot");
        close.Command = _ask.RemoveAttachmentCommand;
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Margin = Theme.AskChipClose;
        chip.Children.Add(close);
        HookThumb(thumb);
        return chip;
    }

    internal void HookThumb(ImageThumb thumb)
    {
        thumb.PreviewRequested += t => ThumbPreviewRequested?.Invoke(t);
        thumb.PreviewEnded += () => ThumbPreviewEnded?.Invoke();
    }

    /// <summary>A row: the first element stretches, the rest take their own width.</summary>
    private static void Row(Grid row, params FrameworkElement[] items)
    {
        for (int i = 0; i < items.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(items[i], i);
            row.Children.Add(items[i]);
        }
    }

    internal AskViewModel Ask => _ask;
    internal double ColumnWidth => Theme.AskAnswerWidth - Theme.AskPadding.Left - Theme.AskPadding.Right - Theme.S2;
}

/// <summary>
/// One exchange: the question card ("You", screenshot, question) and the answer block (logo +
/// "Claude", then the thinking pulse, the streamed Markdown answer, or the error with Retry / Open
/// Settings). Both blocks pop in, the answer a beat after the question; older turns are dimmed.
/// </summary>
internal sealed class TurnView : StackPanel
{
    private readonly AskPage _page;
    private readonly TextBlock _thinking = new(), _errorText = new();
    private readonly MarkdownView _answer = new();
    private readonly StackPanel _error = new(), _errorActions = new() { Orientation = Orientation.Horizontal };
    private readonly PillButton _settings;
    private readonly FrameworkElement _questionCard, _answerBlock;
    private bool _pulsing, _poppedIn;

    public TurnView(TurnViewModel turn, AskPage page)
    {
        Turn = turn;
        _page = page;
        Margin = Theme.TurnBottom;

        // QUESTION: subtle card, "You", screenshot, question text
        var q = new StackPanel();
        q.Children.Add(new TextBlock { Text = "You", Style = AskPage.TextStyle("CaptionText"), Foreground = Theme.SecondaryText, FontFamily = Theme.FontSemiBold, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (turn.Thumb != null)
        {
            var thumb = new ImageThumb(Theme.ThumbHeaderMaxWidth, Theme.ThumbHeaderMaxHeight) { Margin = Theme.GapTopS2 };
            thumb.SetBitmap(turn.Thumb);
            page.HookThumb(thumb);
            q.Children.Add(thumb);
        }
        q.Children.Add(new TextBlock
        {
            Text = turn.Question, TextWrapping = TextWrapping.Wrap, Margin = Theme.GapTopS1,
            FontFamily = Theme.Font, FontSize = Theme.QuestionFontSize, Foreground = Theme.QuestionTextBrush,
            IsTextSelectionEnabled = true, SelectionHighlightColor = Theme.AccentBrush,
        });
        _questionCard = new Border { Background = Theme.QuestionCardBrush, CornerRadius = Theme.RadiusSmall, Padding = Theme.QuestionCardPadding, Child = q };
        Children.Add(_questionCard);

        Children.Add(new Border { Height = 1, Background = Theme.HighlightBorderBrush, Margin = Theme.TurnGap });

        // ANSWER: logo + "Claude", then thinking / answer / error
        var a = new StackPanel();
        var who = new StackPanel { Orientation = Orientation.Horizontal };
        who.Children.Add(new ClaudeLogo(Theme.AnswerLogoSize, Theme.PrimaryText));
        who.Children.Add(new TextBlock { Text = "Claude", Style = AskPage.TextStyle("CaptionText"), Foreground = Theme.SecondaryText, FontFamily = Theme.FontSemiBold, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = Theme.GapLeftS2, VerticalAlignment = VerticalAlignment.Center });
        a.Children.Add(who);

        _thinking.Text = "Claude is thinking…";
        _thinking.Style = AskPage.TextStyle("SecondaryText");
        _thinking.Margin = Theme.GapTopS2;
        a.Children.Add(_thinking);

        _answer.Margin = Theme.GapTopS2;
        _answer.ColumnWidth = page.ColumnWidth;
        page.WatchClick(_answer);
        a.Children.Add(_answer);

        _error.Margin = Theme.GapTopS2;
        var errRow = new Grid();
        errRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        errRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        errRow.Children.Add(new VectorIcon { Data = Icons.Alert, Size = Theme.IconMedium, Foreground = Theme.DangerBrush, VerticalAlignment = VerticalAlignment.Top });
        _errorText.Style = AskPage.TextStyle("BaseText");
        _errorText.TextWrapping = TextWrapping.Wrap;
        _errorText.TextTrimming = TextTrimming.None;
        _errorText.Margin = Theme.GapLeftS2;
        _errorText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_errorText, 1);
        errRow.Children.Add(_errorText);
        _error.Children.Add(errRow);
        _errorActions.Margin = Theme.GapTopS2;
        _errorActions.Children.Add(new PillButton(Icons.Retry, "Retry") { Command = page.Ask.RetryCommand });
        _settings = new PillButton(Icons.Settings, "Open Settings", quiet: true) { Command = page.Ask.OpenSettingsCommand, Margin = Theme.GapLeftS2 };
        _errorActions.Children.Add(_settings);
        _error.Children.Add(_errorActions);
        a.Children.Add(_error);
        _answerBlock = a;
        Children.Add(a);

        turn.PropertyChanged += OnTurnChanged;
        Update(all: true);
        Loaded += (_, _) =>
        {
            if (_poppedIn) return;
            _poppedIn = true;
            Motions.PopIn((FrameworkElement)_questionCard);
            Motions.PopIn(_answerBlock, Motion.TurnAnswerDelayMs / 1000.0);
        };
    }

    public TurnViewModel Turn { get; }

    public void Detach()
    {
        Turn.PropertyChanged -= OnTurnChanged;
        SetPulse(false);
    }

    private void OnTurnChanged(object? sender, PropertyChangedEventArgs e) => Update(all: false, e.PropertyName);

    private void Update(bool all, string? name = null)
    {
        var t = Turn;
        if (all || name is nameof(TurnViewModel.State) or nameof(TurnViewModel.IsThinking) or nameof(TurnViewModel.HasAnswer) or nameof(TurnViewModel.IsError))
        {
            _thinking.Visibility = t.IsThinking ? Visibility.Visible : Visibility.Collapsed;
            _answer.Visibility = t.HasAnswer ? Visibility.Visible : Visibility.Collapsed;
            _error.Visibility = t.IsError ? Visibility.Visible : Visibility.Collapsed;
            SetPulse(t.IsThinking);
            if (t.State == TurnState.Done) _answer.Render(t.Answer, animate: false);
        }
        if (all || name == nameof(TurnViewModel.Answer)) _answer.Render(t.Answer, animate: t.IsStreaming);
        if (all || name == nameof(TurnViewModel.ErrorText)) _errorText.Text = t.ErrorText;
        if (all || name is nameof(TurnViewModel.ShowSettingsButton) or nameof(TurnViewModel.IsLatest))
        {
            _settings.Visibility = t.ShowSettingsButton ? Visibility.Visible : Visibility.Collapsed;
            _errorActions.Visibility = t.IsLatest ? Visibility.Visible : Visibility.Collapsed;
        }
        if (all || name == nameof(TurnViewModel.IsLatest)) Opacity = t.IsLatest ? 1 : Theme.TurnOlderOpacity;
    }

    /// <summary>"Claude is thinking…" breathes (a looping compositor animation) only while it shows.</summary>
    private void SetPulse(bool on)
    {
        if (on == _pulsing) return;
        _pulsing = on;
        var v = Anim.VisualOf(_thinking);
        if (!on || !Anim.Enabled) { v.StopAnimation("Opacity"); v.Opacity = 1; return; }
        var c = v.Compositor;
        var a = c.CreateScalarKeyFrameAnimation();
        const int keys = 24;
        var linear = c.CreateLinearEasingFunction();
        for (int i = 0; i <= keys; i++)
        {
            double p = (double)i / keys;
            a.InsertKeyFrame((float)p, (float)(0.45 + 0.55 * (0.5 + 0.5 * Math.Cos(2 * Math.PI * p))), linear);
        }
        a.Duration = TimeSpan.FromSeconds(Motion.ThinkingPulsePeriod);
        a.IterationBehavior = AnimationIterationBehavior.Forever;
        v.StartAnimation("Opacity", a);
    }
}
