using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;

namespace DynamicNotch.Views;

/// <summary>
/// Click-vs-drag handling for clipboard rows and shelf tiles. A press that moves past the system
/// drag threshold starts an OLE drag (so items can be dropped into chats, mail, folders); a press
/// that doesn't move is a click.
/// </summary>
public partial class ClipboardShelfPage : UserControl
{
    private Point _pressPoint;
    private bool _pressed;
    private bool _dragged;

    /// <summary>Raised while an outgoing drag is running (the window keeps the notch open).</summary>
    public event Action<bool>? OutgoingDrag;

    // Drop zone: dashed outline that brightens, scales up a touch and marches while a file hovers.
    private readonly Spring _drop = new(Motion.Hover, 0);
    private readonly ScaleTransform _dropScale = new(1, 1);
    private readonly TranslateTransform _dropIconShift = new();
    private bool _dropActive;
    private ShelfViewModel? _shelf;

    public ClipboardShelfPage()
    {
        InitializeComponent();
        DropOutline.StrokeDashArray = new DoubleCollection { Theme.DropZoneDash, Theme.DropZoneDash };
        DropZone.RenderTransform = _dropScale;
        DropIcon.RenderTransform = _dropIconShift;
        _drop.ValueChanged += _ => ApplyDrop();
        DataContextChanged += (_, _) =>
        {
            if (_shelf != null) _shelf.PropertyChanged -= OnShelfChanged;
            _shelf = (DataContext as MainViewModel)?.Shelf;
            if (_shelf != null) _shelf.PropertyChanged += OnShelfChanged;
            ApplyDrop();
        };
    }

    private void OnShelfChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShelfViewModel.IsEmpty)) ApplyDrop();
    }

    /// <summary>Called by the window while files are dragged over the notch.</summary>
    public void SetDropActive(bool active)
    {
        if (active == _dropActive) return;
        _dropActive = active;
        _drop.SetTarget(active ? 1 : 0, Motion.Hover);
        if (active && SpringAnimator.AnimationsEnabled)
            SpringAnimator.Instance.RunWhile(dt =>
            {
                DropOutline.StrokeDashOffset -= dt * Motion.DropZoneDashSpeed / Theme.DropZoneThickness;
                return _dropActive;
            });
    }

    private void ApplyDrop()
    {
        double v = Math.Clamp(_drop.Value, 0, 1.1);
        bool empty = _shelf?.IsEmpty ?? true;
        DropZone.Opacity = Math.Max(empty ? 1 : 0, Math.Clamp(v, 0, 1));
        DropFill.Opacity = Math.Clamp(v, 0, 1) * (empty ? 0 : 0.92); // cover tiles while dropping onto a full shelf
        DropOutline.Opacity = 0.7 + 0.3 * v;
        DropOutline.StrokeThickness = Theme.DropZoneThickness * (1 + 0.3 * v);
        _dropScale.ScaleX = _dropScale.ScaleY = 1 + 0.02 * v;
        _dropIconShift.Y = -3 * v;
        DropIcon.Foreground = v > 0.5 ? Theme.PrimaryText : Theme.SecondaryText;
    }

    // ---- Clipboard rows ------------------------------------------------------------------------
    private void Clip_MouseDown(object sender, MouseButtonEventArgs e) => BeginPress(e);

    private void Clip_MouseMove(object sender, MouseEventArgs e)
    {
        if (!ShouldStartDrag(e) || ((FrameworkElement)sender).DataContext is not ClipItemViewModel item) return;
        var data = new DataObject();
        if (item.Content.Kind == ClipKind.Image && item.Content.Image != null) data.SetImage(item.Content.Image);
        if (item.Content.Text != null)
        {
            data.SetText(item.Content.Text);
            if (item.Content.Kind == ClipKind.Link) data.SetData("UniformResourceLocatorW", item.Content.Text);
        }
        RunDrag((DependencyObject)sender, data, DragDropEffects.Copy);
    }

    private void Clip_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (EndPress(e, sender) && ((FrameworkElement)sender).DataContext is ClipItemViewModel item)
            item.CopyCommand.Execute(null);
    }

    // ---- Clipboard tile hover: a slight lift, then a large preview card after a short hover --------
    private sealed class TileMotion
    {
        public readonly ScaleTransform Scale = new(1, 1);
        public readonly TranslateTransform Shift = new();
        public readonly Spring Zoom = new(Motion.TileHover, 1);
        public readonly Spring Lift = new(Motion.Hover, 0) { Precision = 50 };
    }

    private readonly ConditionalWeakTable<FrameworkElement, TileMotion> _tileMotion = new();
    private DispatcherTimer? _previewDelay, _previewGrace;
    private (ClipItemViewModel item, FrameworkElement tile)? _pendingPreview;
    private bool _previewOpen;

    /// <summary>Show / move the preview card for this tile (window positions and animates it).</summary>
    public event Action<ClipItemViewModel, FrameworkElement>? PreviewRequested;
    /// <summary>Hide the preview card.</summary>
    public event Action? PreviewEnded;

    /// <summary>The clipboard column; the preview card opens just to its right.</summary>
    public FrameworkElement ClipboardColumn => ClipCard;

    private TileMotion MotionFor(FrameworkElement container) => _tileMotion.GetValue(container, c =>
    {
        var m = new TileMotion();
        c.RenderTransformOrigin = new Point(0.5, 0.5);
        c.RenderTransform = new TransformGroup { Children = { m.Scale, m.Shift } };
        m.Zoom.ValueChanged += v => m.Scale.ScaleX = m.Scale.ScaleY = v;
        m.Lift.ValueChanged += v => m.Shift.Y = -v;
        return m;
    });

    /// <summary>The ItemsControl item container (child of the WrapPanel) holding a tile.</summary>
    private static FrameworkElement? ContainerOf(DependencyObject tile)
    {
        DependencyObject? d = tile;
        while (d != null)
        {
            var parent = VisualTreeHelper.GetParent(d);
            if (parent is WrapPanel && d is FrameworkElement fe) return fe;
            d = parent;
        }
        return null;
    }

    private void Tile_MouseEnter(object sender, MouseEventArgs e)
    {
        if (ContainerOf((DependencyObject)sender) is not { } hovered) return;
        var m = MotionFor(hovered);
        Panel.SetZIndex(hovered, 100);
        m.Zoom.SetTarget(Theme.ClipTileHoverScale, Motion.TileHover);
        m.Lift.SetTarget(Motion.TileLift, Motion.Hover);

        if (((FrameworkElement)sender).DataContext is not ClipItemViewModel item) return;
        _previewGrace?.Stop();
        _pendingPreview = (item, (FrameworkElement)sender);
        if (_previewOpen)
        {
            // Already open: glide to this tile instead of closing and reopening.
            PreviewRequested?.Invoke(item, (FrameworkElement)sender);
            return;
        }
        _previewDelay ??= NewTimer(Motion.PreviewHoverDelayMs, () =>
        {
            if (_pendingPreview is not { } p || _pressed) return;
            _previewOpen = true;
            PreviewRequested?.Invoke(p.item, p.tile);
        });
        _previewDelay.Stop();
        _previewDelay.Start();
    }

    private void Tile_MouseLeave(object sender, MouseEventArgs e)
    {
        if (ContainerOf((DependencyObject)sender) is { } left)
        {
            Panel.SetZIndex(left, 50); // stay on top while settling back
            var m = MotionFor(left);
            m.Zoom.SetTarget(1, Motion.TileHover);
            m.Lift.SetTarget(0, Motion.Hover);
        }
        _previewDelay?.Stop();
        _pendingPreview = null;
        _previewGrace ??= NewTimer(Motion.PreviewCloseGraceMs, () => { if (_pendingPreview == null) ClosePreview(); });
        _previewGrace.Stop();
        _previewGrace.Start();
    }

    /// <summary>Closes the preview right away (click, drag, page change).</summary>
    public void ClosePreview()
    {
        _previewDelay?.Stop();
        _previewGrace?.Stop();
        if (!_previewOpen) return;
        _previewOpen = false;
        PreviewEnded?.Invoke();
    }

    private static DispatcherTimer NewTimer(int ms, Action tick)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); tick(); };
        return t;
    }

    // ---- Shelf tiles ---------------------------------------------------------------------------
    private void Shelf_MouseDown(object sender, MouseButtonEventArgs e) => BeginPress(e);

    private void Shelf_MouseMove(object sender, MouseEventArgs e)
    {
        if (!ShouldStartDrag(e) || ((FrameworkElement)sender).DataContext is not ShelfItemViewModel item) return;
        item.Refresh();
        if (!item.Exists) { _pressed = false; return; }
        var files = new StringCollection { item.Path };
        var data = new DataObject();
        data.SetFileDropList(files);
        RunDrag((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Link | DragDropEffects.Move);
    }

    private void Shelf_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (EndPress(e, sender) && ((FrameworkElement)sender).DataContext is ShelfItemViewModel item)
            item.OpenCommand.Execute(null);
    }

    // ---- Shared --------------------------------------------------------------------------------
    private void BeginPress(MouseButtonEventArgs e)
    {
        ClosePreview();
        if (e.OriginalSource is DependencyObject src && FindParent<Button>(src) != null) { _pressed = false; return; }
        _pressPoint = e.GetPosition(this);
        _pressed = true;
        _dragged = false;
    }

    private bool ShouldStartDrag(MouseEventArgs e)
    {
        if (!_pressed || _dragged || e.LeftButton != MouseButtonState.Pressed) return false;
        var d = e.GetPosition(this) - _pressPoint;
        return Math.Abs(d.X) > SystemParameters.MinimumHorizontalDragDistance ||
               Math.Abs(d.Y) > SystemParameters.MinimumVerticalDragDistance;
    }

    private bool EndPress(MouseButtonEventArgs e, object sender)
    {
        bool click = _pressed && !_dragged;
        _pressed = false;
        if (click) e.Handled = true;
        return click;
    }

    private void RunDrag(DependencyObject source, DataObject data, DragDropEffects effects)
    {
        _dragged = true;
        _pressed = false;
        OutgoingDrag?.Invoke(true);
        try { DragDrop.DoDragDrop(source, data, effects); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Drag failed: {ex.Message}"); }
        finally { OutgoingDrag?.Invoke(false); }
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
