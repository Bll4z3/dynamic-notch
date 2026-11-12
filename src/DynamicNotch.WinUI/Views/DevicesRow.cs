using System.Collections.Specialized;
using System.ComponentModel;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DynamicNotch.Views;

/// <summary>
/// Connected devices (bottom-right of the open notch): one battery ring per device, right-aligned,
/// newest on the far right. Items pop in, leaving items fade and shrink out in place, and the rest
/// slide to their new slots with a spring (compositor Translation only, never layout per frame).
/// </summary>
public sealed class DevicesRow : Grid
{
    private sealed class Item
    {
        public required DeviceItemViewModel Vm;
        public required Grid Slot;
        public required Grid Body;
        public required BatteryRing Ring;
        public required CompSpring X;
    }

    private readonly List<Item> _items = new();

    public DevicesRow(DevicesViewModel vm)
    {
        Height = Theme.DeviceRingSize;
        HorizontalAlignment = HorizontalAlignment.Right;
        foreach (var d in vm.Items) Add(d, animate: false);
        vm.Items.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (var i in _items) Children.Remove(i.Slot);
                _items.Clear();
                foreach (var d in vm.Items) Add(d, animate: false);
            }
            if (e.OldItems != null)
                foreach (DeviceItemViewModel d in e.OldItems) Remove(d);
            if (e.NewItems != null)
                foreach (DeviceItemViewModel d in e.NewItems) Add(d, animate: true);
            Reflow();
        };
        Reflow();
    }

    private void Add(DeviceItemViewModel d, bool animate)
    {
        var ring = new BatteryRing
        {
            RingSize = Theme.DeviceRingSize,
            Thickness = Theme.DeviceRingThickness,
            NumberOnHover = true,
            Glyph = d.Glyph,
            Percent = d.RingPercent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var body = new Grid { Background = Theme.TransparentBrush };
        body.Children.Add(ring);
        ToolTipService.SetToolTip(body, d.Tooltip);
        var slot = new Grid { Width = Theme.DeviceSlotWidth, HorizontalAlignment = HorizontalAlignment.Right };
        slot.Children.Add(body);
        Children.Add(slot);

        var visual = Anim.VisualOf(slot, translation: true);
        var (x, set) = Motions.Value(visual.Compositor, Motion.Reflow, 0, precision: 50);
        visual.StartAnimation("Translation", Anim.Expr(visual.Compositor, "Vector3(p.v, 0, 0)", ("p", set)));
        var item = new Item { Vm = d, Slot = slot, Body = body, Ring = ring, X = x };
        _items.Add(item);

        d.PropertyChanged += (_, e) => OnItemChanged(item, e);
        if (animate) body.Loaded += (_, _) => Motions.PopIn(body);
        x.Snap(SlotX(item)); // new items appear in their slot
    }

    private void OnItemChanged(Item item, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(DeviceItemViewModel.RingPercent):
                item.Ring.Percent = item.Vm.RingPercent;
                break;
            case nameof(DeviceItemViewModel.Tooltip):
                ToolTipService.SetToolTip(item.Body, item.Vm.Tooltip);
                break;
            case nameof(DeviceItemViewModel.IsLeaving) when item.Vm.IsLeaving:
                Motions.Leave(item.Body);
                Reflow(); // the others slide over right away
                break;
        }
    }

    private void Remove(DeviceItemViewModel d)
    {
        var item = _items.FirstOrDefault(i => i.Vm == d);
        if (item == null) return;
        _items.Remove(item);
        Children.Remove(item.Slot);
    }

    /// <summary>Slot offset from the right edge: the newest active item sits on the far right.</summary>
    private double SlotX(Item item)
    {
        var active = _items.Where(i => !i.Vm.IsLeaving).ToList();
        int index = active.IndexOf(item);
        if (index < 0) return item.X.Target; // leaving: stay put while fading out
        return -(active.Count - 1 - index) * Theme.DeviceSlotWidth;
    }

    private void Reflow()
    {
        Width = Math.Max(1, _items.Count(i => !i.Vm.IsLeaving)) * Theme.DeviceSlotWidth;
        foreach (var i in _items) i.X.SetTarget(SlotX(i), Motion.Reflow);
    }
}
