using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace DynamicNotch;

/// <summary>
/// Stage 3 of the notch window: the clipboard hover preview (follows the cursor, flips near the right
/// edge, stays inside the notch) and the shelf drop target (files dragged over the notch spring it open
/// to the shelf; the outline glows and the drop zone marches until they're dropped or leave).
/// </summary>
public sealed partial class NotchWindow
{
    private bool _dragIn, _outgoingDrag, _previewFlipped;
    private CompSpring _dropGlow = null!;
    private DispatcherQueueTimer _dragLeaveTimer = null!;
    private Point _pointer; // last pointer position over the open notch, in the expanded frame's coordinates

    private void BuildClipboard()
    {
        _dropGlow = new CompSpring(_shape.Props, "Glow", Motion.Hover, 0);
        _dragLeaveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _dragLeaveTimer.IsRepeating = false;
        _dragLeaveTimer.Interval = TimeSpan.FromMilliseconds(180);
        _dragLeaveTimer.Tick += (_, _) => { _dragLeaveTimer.Stop(); SetDragIn(false); ScheduleCollapseCheck(); };

        var page = _frame.Shelf;
        page.OutgoingDrag += active => { _outgoingDrag = active; if (!active) ScheduleCollapseCheck(); else UpdateState(); };
        page.PreviewRequested += ShowPreview;
        page.PreviewEnded += () => _frame.Preview.SetShown(false);
        // Re-clamp once the card has laid out its new content (text / image sizes differ).
        _frame.Preview.SizeChanged2 += () => { if (_frame.Preview.IsShown) PositionPreview(snap: false); };
        // The card follows the cursor while it shows.
        ExpandedLayer.PointerMoved += (_, e) =>
        {
            _pointer = e.GetCurrentPoint(_frame).Position;
            if (_frame.Preview.IsShown) PositionPreview(snap: false);
        };

        NotchRoot.DragEnter += Notch_DragEnter;
        NotchRoot.DragOver += Notch_DragOver;
        NotchRoot.DragLeave += Notch_DragLeave;
        NotchRoot.Drop += Notch_Drop;
        ApplyDropTarget();
    }

    /// <summary>The shelf drop target goes with the Clipboard + Shelf page.</summary>
    private void ApplyDropTarget()
    {
        bool clip = Vm.Pages.IsEnabled(PageId.Clipboard);
        NotchRoot.AllowDrop = clip;
        if (!clip) _frame.Shelf.ClosePreview();
    }

    // ---- Preview card -----------------------------------------------------------------------------
    /// <summary>Dev: the preview card for the first clipboard item, as if the cursor were over its tile.</summary>
    public void ShowFirstPreview()
    {
        if (Vm.Clipboard.Items.FirstOrDefault() is not { } item) return;
        _pointer = new Point(120, 140);
        ShowPreview(item, _frame.Shelf);
    }

    private void ShowPreview(ClipItemViewModel item, FrameworkElement tile)
    {
        if (_state != NotchState.Expanded || !Vm.IsShelfPage) return;
        bool wasHidden = !_frame.Preview.IsShown;
        _frame.Preview.Show(item);
        _frame.Preview.SetShown(true, snapFromZero: wasHidden);
        _frame.Preview.UpdateLayout();
        PositionPreview(snap: wasHidden);
    }

    /// <summary>
    /// To the right of the cursor and slightly below it; flips to the left when it would pass the
    /// notch's right edge, and is clamped so it stays inside the notch (and the screen).
    /// </summary>
    private void PositionPreview(bool snap)
    {
        double w = _frame.Preview.CardWidth, h = _frame.Preview.CardHeight;
        if (w <= 0) return;
        double bodyW = _width.Value, bodyH = _height.Value;
        double bodyLeft = (Theme.ExpandedWidth - bodyW) / 2 + Theme.PreviewInset;
        double bodyRight = (Theme.ExpandedWidth + bodyW) / 2 - Theme.PreviewInset;
        double bodyBottom = bodyH - Theme.PreviewInset;

        double x = _pointer.X + Theme.PreviewCursorOffsetX;
        bool flip = x + w > bodyRight;
        if (flip) x = _pointer.X - Theme.PreviewCursorOffsetX - w;
        x = Math.Clamp(x, bodyLeft, Math.Max(bodyLeft, bodyRight - w));
        double y = Math.Clamp(_pointer.Y + Theme.PreviewCursorOffsetY, Theme.PreviewInset, Math.Max(Theme.PreviewInset, bodyBottom - h));
        _previewFlipped = flip; // pop out of the side facing the cursor
        _frame.Preview.Place(x, y, _previewFlipped, snap);
    }

    // ---- Shelf drop target: the notch springs open to the shelf page as soon as files arrive ----------
    private void SetDragIn(bool on)
    {
        if (_dragIn == on) return;
        _dragIn = on;
        _dropGlow.SetTarget(on ? 1 : 0, Motion.Hover);
        _frame.Shelf.SetDropActive(on);
        UpdateState();
    }

    private static bool HasFiles(DragEventArgs e) => e.DataView.Contains(StandardDataFormats.StorageItems);

    private void Notch_DragEnter(object sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();
        if (_outgoingDrag || !HasFiles(e) || !Vm.Pages.IsEnabled(PageId.Clipboard)) { e.AcceptedOperation = DataPackageOperation.None; return; }
        e.AcceptedOperation = DataPackageOperation.Link | DataPackageOperation.Copy;
        e.DragUIOverride.IsCaptionVisible = false;
        if (!_dragIn)
        {
            Vm.GoTo(PageId.Clipboard);
            SetDragIn(true);
        }
        e.Handled = true;
    }

    private void Notch_DragOver(object sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();
        e.AcceptedOperation = !_outgoingDrag && HasFiles(e) && Vm.Pages.IsEnabled(PageId.Clipboard)
            ? DataPackageOperation.Link | DataPackageOperation.Copy : DataPackageOperation.None;
        e.DragUIOverride.IsCaptionVisible = false;
        e.Handled = true;
    }

    private void Notch_DragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between child elements; confirm after a beat.
        _dragLeaveTimer.Stop();
        _dragLeaveTimer.Start();
    }

    private async void Notch_Drop(object sender, DragEventArgs e)
    {
        _dragLeaveTimer.Stop();
        e.Handled = true;
        if (!_outgoingDrag && HasFiles(e))
        {
            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                if (paths.Count > 0) Vm.Shelf.AddPaths(paths);
            }
            catch (Exception ex) { App.LogError(ex); }
            finally { deferral.Complete(); }
        }
        _hover = true; // stay open on the shelf after the drop, like hovering
        SetDragIn(false);
        ScheduleCollapseCheck();
    }
}
