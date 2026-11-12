using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.ViewModels;

namespace DynamicNotch.Views;

/// <summary>
/// Page 3, "Ask". Cross-fades between the empty box and the conversation (stacked question / answer
/// turns) with springs, reports the notch size it wants (which the window springs to), and keeps the
/// conversation scrolled to the newest turn unless the user scrolled up.
/// </summary>
public partial class AskPage : UserControl
{
    private AskViewModel? _ask;
    private readonly Dictionary<FrameworkElement, Spring> _fades = new();
    private FrameworkElement? _shown;
    private bool _stickToBottom = true;

    public AskPage()
    {
        InitializeComponent();
        foreach (var panel in new FrameworkElement[] { IdlePanel, ConversationPanel })
        {
            var move = new TranslateTransform();
            panel.RenderTransform = move;
            var spring = new Spring(Motion.FadeIn, panel == IdlePanel ? 1 : 0);
            spring.ValueChanged += v =>
            {
                panel.Opacity = Math.Clamp(v, 0, 1);
                BlurFx.Reveal(panel, v, Motion.EnterBlur);
                move.Y = (1 - Math.Clamp(v, 0, 1)) * Motion.StaggerRise;
                if (v <= 0.01 && spring.Target == 0) panel.Visibility = Visibility.Collapsed;
            };
            _fades[panel] = spring;
        }
        _shown = IdlePanel;

        HotkeyCaption.Text = $"{AppConfig.AskHotkeyLabel}  ·  Esc to cancel";
        AnswerScroll.ScrollChanged += (_, e) =>
        {
            if (e.ExtentHeightChange == 0) _stickToBottom = AnswerScroll.VerticalOffset >= AnswerScroll.ScrollableHeight - 4;
            else if (_stickToBottom) AnswerScroll.ScrollToEnd(); // content grew (streaming): follow it
        };
        Root.SizeChanged += (_, _) => NotchSizeChanged?.Invoke();
        DataContextChanged += (_, _) => Attach((DataContext as MainViewModel)?.Ask);
        ApplyWidth(AskState.Idle);
    }

    /// <summary>Raised when the size the notch should take on this page changes.</summary>
    public event Action? NotchSizeChanged;
    /// <summary>Raised when the user clicks into a text box or the answer (the window then takes focus).</summary>
    public event Action? FocusRequested;
    /// <summary>Hovering a screenshot thumbnail: show / hide the enlarged preview card.</summary>
    public event Action<ImageSource>? ThumbPreviewRequested;
    public event Action? ThumbPreviewEnded;

    /// <summary>The notch size for the current state and content (clamped to the Ask size tokens).</summary>
    public Size DesiredNotchSize
    {
        get
        {
            var state = _ask?.State ?? AskState.Idle;
            double content = Root.ActualHeight + Theme.AskPadding.Top + Theme.AskPadding.Bottom;
            return state switch
            {
                AskState.Idle => new Size(Theme.AskIdleWidth, Math.Clamp(content, Theme.AskIdleHeight, Theme.AskAnswerMaxHeight)),
                AskState.Thinking => new Size(Theme.AskAnswerWidth, Math.Clamp(content, Theme.AskThinkingHeight, Theme.AskAnswerMaxHeight)),
                _ => new Size(Theme.AskAnswerWidth, Math.Clamp(content, Theme.AskIdleHeight, Theme.AskAnswerMaxHeight)),
            };
        }
    }

    /// <summary>Focuses the box for the current state (idle input or follow-up).</summary>
    public void FocusInput()
    {
        var box = _ask?.State is null or AskState.Idle ? Input : FollowUp;
        if (box.IsVisible) { box.Focus(); Keyboard.Focus(box); box.CaretIndex = box.Text.Length; }
        else Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { box.Focus(); Keyboard.Focus(box); }));
    }

    public bool HasTextFocus => Input.IsKeyboardFocused || FollowUp.IsKeyboardFocused;

    /// <summary>While the notch owns focus, keep it on something in this page so keys keep arriving.</summary>
    public void KeepFocus()
    {
        if (_ask?.State is AskState.Idle or AskState.Answer or AskState.Error) FocusInput();
        else Root.Focus();
    }

    private void Attach(AskViewModel? ask)
    {
        if (_ask != null)
        {
            _ask.PropertyChanged -= OnAskChanged;
            _ask.Turns.CollectionChanged -= OnTurnsChanged;
        }
        _ask = ask;
        if (_ask == null) return;
        _ask.PropertyChanged += OnAskChanged;
        _ask.Turns.CollectionChanged += OnTurnsChanged;
        ShowState(_ask.State, animate: false);
    }

    private void OnAskChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AskViewModel.State) && _ask != null) ShowState(_ask.State, animate: true);
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // A new turn: jump to it (older turns stay reachable by scrolling up).
        _stickToBottom = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => AnswerScroll.ScrollToEnd()));
    }

    private void ShowState(AskState state, bool animate)
    {
        ApplyWidth(state);
        var target = state == AskState.Idle ? IdlePanel : ConversationPanel;
        if (!ReferenceEquals(target, _shown) || !animate)
        {
            foreach (var (panel, spring) in _fades)
            {
                bool on = ReferenceEquals(panel, target);
                if (on) panel.Visibility = Visibility.Visible;
                if (!animate) { spring.Snap(on ? 1 : 0); if (!on) panel.Visibility = Visibility.Collapsed; }
                else spring.SetTarget(on ? 1 : 0, on ? Motion.FadeIn : Motion.FadeOut);
            }
            _shown = target;
        }
        NotchSizeChanged?.Invoke();
    }

    private void ApplyWidth(AskState state)
    {
        double w = state == AskState.Idle ? Theme.AskIdleWidth : Theme.AskAnswerWidth;
        Root.Width = w - Theme.AskPadding.Left - Theme.AskPadding.Right;
    }

    // ---- Input ---------------------------------------------------------------------------------
    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter is a new line (AcceptsReturn handles it).
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            if (_ask?.SendCommand.CanExecute(null) == true) _ask.SendCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Input_PreviewMouseDown(object sender, MouseButtonEventArgs e) => FocusRequested?.Invoke();

    private void Thumb_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is ImageThumb { Source: { } img }) ThumbPreviewRequested?.Invoke(img);
    }

    private void Thumb_MouseLeave(object sender, MouseEventArgs e) => ThumbPreviewEnded?.Invoke();
}
