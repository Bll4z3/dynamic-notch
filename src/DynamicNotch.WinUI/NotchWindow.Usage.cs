using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using DynamicNotch.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DynamicNotch;

/// <summary>
/// Claude usage rings next to the battery ring (Settings ▸ Customisation ▸ Claude usage rings): the 5-hour and / or
/// weekly limit, on the closed notch, in the open notch's header, or both. They read the Claude Usage page's view model
/// (same request, polling and back-off; it keeps polling while a ring is on screen). On the closed notch they hug the
/// left edge after the battery ring and the notch widens symmetrically to make room (the clock stays centred); when a
/// ring shows in both places it flies between them with the morph, like the battery ring and the clock.
/// </summary>
public sealed partial class NotchWindow
{
    private UsageRing _closedFiveHour = null!, _closedWeekly = null!;
    private readonly StackPanel _closedUsage = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Spacing = Theme.UsageRingGap };
    private bool _usageApplied;

    private void BuildUsageRings()
    {
        _closedFiveHour = new UsageRing(UsageRing.Kind.FiveHour, Theme.NotchRingSize, Theme.NotchRingThickness) { Visibility = Visibility.Collapsed };
        _closedWeekly = new UsageRing(UsageRing.Kind.Weekly, Theme.NotchRingSize, Theme.NotchRingThickness) { Visibility = Visibility.Collapsed };
        _closedUsage.Children.Add(_closedFiveHour);
        _closedUsage.Children.Add(_closedWeekly);
        CollapsedLayer.Children.Add(_closedUsage);
        PlaceClosedUsage();

        // Each ring flies between its closed and open spot when it's shown in both (same rules as the battery ring).
        var pairs = new (UsageRing closed, UsageRing open)[] { (_closedFiveHour, _frame.HeaderFiveHour), (_closedWeekly, _frame.HeaderWeekly) };
        foreach (var (closed, open) in pairs)
            AddMatched(closed, open, edge: +1, parentShifted: false, want: 0, page: () => -1,
                allowed: () => Vm.Overlay == NotchOverlay.None && !Vm.MinimalNotch && closed.Visibility == Visibility.Visible && open.Visibility == Visibility.Visible);

        Vm.Usage.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "" or null or nameof(UsageViewModel.Snapshot) or nameof(UsageViewModel.UnavailableReason)) RefreshUsageData();
        };
        Vm.Status.PropertyChanged += (_, e) => { if (e.PropertyName is "" or null or nameof(StatusViewModel.HasBattery)) PlaceClosedUsage(); };
        RefreshUsageData();
    }

    /// <summary>After the battery ring (or at the edge when there's no battery).</summary>
    private void PlaceClosedUsage()
    {
        double left = Theme.CollapsedPadding.Left + (Vm.Status.HasBattery ? Theme.NotchRingSize + Theme.UsageRingGap : 0);
        if (_closedUsage.Margin.Left == left) return;
        _closedUsage.Margin = new Thickness(left, 0, 0, 0);
        RemeasureMatchedSoon();
    }

    private void RefreshUsageData()
    {
        var snap = Vm.Usage.Snapshot;
        string? why = Vm.Usage.UnavailableReason;
        _closedFiveHour.SetData(snap?.FiveHour, why);
        _frame.HeaderFiveHour.SetData(snap?.FiveHour, why);
        _closedWeekly.SetData(snap?.Weekly, why);
        _frame.HeaderWeekly.SetData(snap?.Weekly, why);
    }

    private CustomSettings? UsageSettings => _theme?.Current;
    private bool UsageOnClosed => UsageSettings?.UsageRingPlace is "closed" or "both";
    private bool UsageOnOpen => UsageSettings?.UsageRingPlace is "open" or "both";

    /// <summary>How many usage rings the closed notch shows (it widens by that much on each side).</summary>
    private int ClosedUsageCount => UsageSettings is not { } s || !UsageOnClosed || Vm.MinimalNotch ? 0
        : (s.UsageRingFiveHour ? 1 : 0) + (s.UsageRingWeekly ? 1 : 0);

    /// <summary>Extra width for the closed notch (each side) so the rings fit without crowding the clock.</summary>
    private double ClosedUsageExtra => ClosedUsageCount * (Theme.NotchRingSize + Theme.UsageRingGap);

    /// <summary>Shows / hides each ring for the settings (popping in and out), then re-measures the flights.</summary>
    private void ApplyUsageRings(bool animate)
    {
        if (UsageSettings is not { } s) return;
        animate &= _usageApplied && Anim.Enabled;
        _usageApplied = true;
        bool closed = UsageOnClosed && !Vm.MinimalNotch, open = UsageOnOpen;
        bool headerChanges = (_frame.HeaderFiveHour.Visibility == Visibility.Visible) != (open && s.UsageRingFiveHour)
                          || (_frame.HeaderWeekly.Visibility == Visibility.Visible) != (open && s.UsageRingWeekly);
        if (headerChanges && animate) _frame.SlideHeaderOnce();
        _closedFiveHour.SetShown(closed && s.UsageRingFiveHour, animate);
        _closedWeekly.SetShown(closed && s.UsageRingWeekly, animate);
        _frame.HeaderFiveHour.SetShown(open && s.UsageRingFiveHour, animate);
        _frame.HeaderWeekly.SetShown(open && s.UsageRingWeekly, animate);
        ApplyTargets();
        RemeasureMatchedSoon();
    }

    /// <summary>Polling for the rings: allowed while any ring is on; refreshing while one is on screen.</summary>
    private void UpdateUsageDemand()
    {
        if (UsageSettings is not { } s) return;
        bool wanted = s.UsageRingFiveHour || s.UsageRingWeekly;
        bool onScreen = wanted && (_state == NotchState.Expanded ? UsageOnOpen : _state == NotchState.Collapsed && ClosedUsageCount > 0);
        Vm.Usage.SetRingDemand(wanted, onScreen);
    }

    /// <summary>The usage rings show the % while the notch is hovered (like the battery ring).</summary>
    private void SetUsageNumbers(bool on)
    {
        if (_closedFiveHour == null) return;
        _closedFiveHour.ShowNumber = on;
        _closedWeekly.ShowNumber = on;
    }

    /// <summary>
    /// Something moved in the header or on the closed notch without changing its own size (a ring appeared next to it):
    /// once layout has run, every flight re-measures its two ends.
    /// </summary>
    private void RemeasureMatchedSoon()
    {
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            foreach (var pair in _matched) Measure(pair);
            UpdateMatchedFlags();
        });
    }
}
