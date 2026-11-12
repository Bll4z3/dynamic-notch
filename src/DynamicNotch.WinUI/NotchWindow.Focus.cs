using System.ComponentModel;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DynamicNotch;

/// <summary>
/// Focus timer outside its page: the compact readout on the collapsed notch (a phase-coloured dot and
/// the remaining time, on the right where the music bits sit), the notification pill when a phase ends
/// while you're not looking at the timer, and the per-second tick demand (only while seconds are shown).
/// </summary>
public sealed partial class NotchWindow
{
    private readonly StackPanel _compactTimer = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _compactText = new();
    private readonly SolidColorBrush _compactDot = new(Theme.FocusB);

    /// <summary>The collapsed notch shows the timer (instead of the music bits) while a phase is running or paused.</summary>
    private bool CompactTimerShown => Vm.Pages.IsEnabled(PageId.Focus) && Vm.Focus.IsActive;

    private void BuildFocus()
    {
        _compactTimer.Children.Add(new Ellipse { Width = Theme.CollapsedTimerDot, Height = Theme.CollapsedTimerDot, Fill = _compactDot, VerticalAlignment = VerticalAlignment.Center });
        _compactText.Style = (Style)Application.Current.Resources["ClockTimeText"];
        _compactText.Margin = new Thickness(Theme.S1 + 2, 0, 0, 0);
        _compactTimer.Children.Add(_compactText);
        CollapsedRight.Children.Insert(0, _compactTimer);

        Vm.Focus.PropertyChanged += OnFocusChanged;
        Vm.Focus.PhaseEnded += OnPhaseEnded;
        UpdateCompactTimer();
    }

    private void OnFocusChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FocusTimerViewModel.Remaining)) { UpdateCompactText(); return; }
        ApplyTargets(); // started / paused / ended: the collapsed width and readout change
    }

    /// <summary>Called from ApplyTargets: readout visibility and who needs the 1 s tick.</summary>
    private void UpdateCompactTimer()
    {
        bool shown = CompactTimerShown;
        _compactTimer.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        _compactTimer.Opacity = Vm.Focus.Status == TimerStatus.Paused ? 0.55 : 1;
        _compactDot.Color = FocusRoutine.Colors(Vm.Focus.Current.Kind).B;
        Vm.Focus.SetTickDemand(
            page: _state == NotchState.Expanded && Vm.CurrentPage == PageId.Focus,
            compact: shown && _state == NotchState.Collapsed && !Vm.MinimalNotch);
        UpdateCompactText();
    }

    private void UpdateCompactText()
    {
        if (_compactTimer.Visibility == Visibility.Visible) _compactText.Text = FocusRoutine.Digits(Vm.Focus.Remaining);
    }

    /// <summary>A clear cue when a phase ends: the page plays its own; elsewhere a pill pops (sound is the timer's).</summary>
    private void OnPhaseEnded(FocusInterval finished, FocusInterval? next)
    {
        if (!Vm.Pages.IsEnabled(PageId.Focus)) return;
        if (_state == NotchState.Expanded && Vm.CurrentPage == PageId.Focus) return; // the page shows it
        bool waiting = Vm.Focus.Status == TimerStatus.Waiting;
        string title, body;
        if (next == null)
        {
            title = "Round complete";
            body = FocusRoutine.Summary(Vm.Focus.Round);
        }
        else if (finished.Kind == IntervalKind.Focus)
        {
            title = "Focus session done";
            body = $"{FocusRoutine.Name(next.Kind)} · {next.Minutes} min{(waiting ? " · tap to start" : "")}";
        }
        else
        {
            title = "Break's over";
            body = $"{FocusRoutine.Name(next.Kind)} · {next.Minutes} min{(waiting ? " · tap to start" : "")}";
        }
        Vm.EnqueuePill(new PillRequest(PillSource.System, title, body, Icons.Timer, OnClick: () =>
        {
            if (Vm.GoTo(PageId.Focus)) Pin();
        }));
    }
}
