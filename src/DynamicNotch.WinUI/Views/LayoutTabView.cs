using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DynamicNotch.Views;

/// <summary>Settings ▸ Layout: the layout it edits.</summary>
public sealed class LayoutTabViewModel
{
    public LayoutTabViewModel(PageRegistry pages, SettingsService settings) { Pages = pages; Settings = settings; }
    public PageRegistry Pages { get; }
    public SettingsService Settings { get; }
}

/// <summary>
/// Settings ▸ Layout: one place for where every feature lives. Each feature is a <b>Page</b> (swipe between
/// them in the notch), a <b>Header icon</b> (top right; opens it as a pop-up) or <b>Hidden</b>. A live preview
/// of the notch sits on top. Drag rows by the grip (or use the arrows) to reorder pages and icons. Settings is
/// always a header icon, and at least one feature stays a page. Everything applies to the notch straight away.
/// </summary>
internal sealed class LayoutTabView : Grid
{
    private readonly PageRegistry _pages;
    private readonly StackPanel _pageList = new() { Spacing = Theme.S2 }, _iconList = new() { Spacing = Theme.S2 }, _hiddenList = new() { Spacing = Theme.S2 };
    private readonly TextBlock _message = new();
    private readonly StackPanel _previewIcons = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    private readonly StackPanel _previewPages = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _previewPageName = new();
    private readonly Dictionary<string, Grid> _rows = new();
    private readonly Dictionary<string, Segmented> _picks = new();
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Canvas _dragLayer = new() { IsHitTestVisible = false }; // the lifted row floats here while dragged
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _placeholder = new();  // the drop indicator: where it will land
    private Grid? _dragRow;
    private string? _dragId;
    private double _grabY;
    private string? _justMoved;
    private const string EmptyTag = "layout:empty";

    public LayoutTabView(LayoutTabViewModel vm)
    {
        _pages = vm.Pages;
        var stack = new StackPanel { Margin = new Thickness(0, 0, Theme.S3, Theme.S2) };
        stack.Children.Add(SettingsUi.Title("Layout"));
        stack.Children.Add(SettingsUi.Subtitle("Choose how each feature appears: as a page you swipe to, an icon at the top right that opens it, or hidden. Drag to reorder."));
        stack.Children.Add(Preview());
        _message.Style = SettingsUi.StyleOf("CaptionText");
        _message.Foreground = Theme.DangerBrush;
        _message.Margin = Theme.GapTopS1;
        _message.Visibility = Visibility.Collapsed;
        stack.Children.Add(_message);
        stack.Children.Add(Section("Pages", "Swipe or use ← / → between them, left to right"));
        stack.Children.Add(_pageList);
        stack.Children.Add(Section("Header icons", "Top right of the open notch, left to right"));
        stack.Children.Add(_iconList);
        stack.Children.Add(Section("Hidden", "Not shown (the Ask Claude and palette hotkeys still work)"));
        stack.Children.Add(_hiddenList);
        _scroll.Content = stack;
        Children.Add(_scroll);
        Children.Add(_dragLayer);

        // Drop indicator: a dashed, accent-outlined slot the size of the dragged row.
        _placeholder.RadiusX = _placeholder.RadiusY = Theme.Radius.TopLeft;
        _placeholder.Stroke = Theme.AccentBrush;
        _placeholder.StrokeThickness = 1.5;
        _placeholder.StrokeDashArray = new Microsoft.UI.Xaml.Media.DoubleCollection { 4, 3 };
        _placeholder.Fill = Theme.CardHoverBrush;
        _placeholder.IsHitTestVisible = false;
        Motions.SlideIntoPlace(_placeholder);

        // The drag is captured by this whole view, not the grip: rows (and their grips) move between lists while
        // dragging, and an element leaving the tree loses its capture (that used to end the drag half-way).
        PointerMoved += (_, e) => { if (_dragRow != null) { Drag(e); e.Handled = true; } };
        PointerReleased += (_, e) => { if (_dragRow != null) { ReleasePointerCapture(e.Pointer); EndDrag(); e.Handled = true; } };
        PointerCanceled += (_, _) => EndDrag();
        PointerCaptureLost += (_, _) => EndDrag();
        Refresh();
    }

    private Placement PlacementFor(StackPanel list) => list == _pageList ? Placement.Page : list == _iconList ? Placement.Icon : Placement.Hidden;

    private static FrameworkElement Section(string title, string caption)
    {
        var s = new StackPanel { Margin = new Thickness(Theme.S1, Theme.S4, 0, Theme.S2) };
        s.Children.Add(new TextBlock { Text = title, Style = SettingsUi.StyleOf("SectionText") });
        s.Children.Add(new TextBlock { Text = caption, Style = SettingsUi.StyleOf("CaptionText"), Margin = new Thickness(0, 2, 0, 0) });
        return s;
    }

    // ---- Live preview: a little notch with the header icons and the page icons ---------------------------------
    private FrameworkElement Preview()
    {
        var notch = new Grid
        {
            Width = 420, Height = 132, HorizontalAlignment = HorizontalAlignment.Center, Margin = Theme.GapTopS3,
            Background = Theme.NotchBrush, CornerRadius = new CornerRadius(0, 0, 26, 26), Padding = new Thickness(18, 12, 18, 12),
            BorderBrush = Theme.SettingsBorderBrush, BorderThickness = new Thickness(1, 0, 1, 1),
        };
        notch.Children.Add(new TextBlock { Text = "12:34", FontFamily = Theme.FontBold, FontWeight = FontWeights.Bold, FontSize = 14, Foreground = Theme.PrimaryText, VerticalAlignment = VerticalAlignment.Top });
        notch.Children.Add(_previewIcons);
        _previewPageName.Style = SettingsUi.StyleOf("CaptionText");
        _previewPageName.HorizontalAlignment = HorizontalAlignment.Center;
        _previewPageName.VerticalAlignment = VerticalAlignment.Center;
        notch.Children.Add(_previewPageName);
        notch.Children.Add(_previewPages);
        return notch;
    }

    private void RefreshPreview()
    {
        _previewIcons.Children.Clear();
        foreach (var id in _pages.IconIds)
        {
            string icon = id == PageRegistry.SettingsId ? Icons.Settings : PageRegistry.Def(Enum.Parse<PageId>(id)).Icon;
            var el = new VectorIcon { Data = icon, Size = 13, Foreground = Theme.SecondaryText };
            _previewIcons.Children.Add(el);
            if (id == _justMoved) Motions.PopIn(el);
        }
        _previewPages.Children.Clear();
        var pages = _pages.Enabled;
        for (int i = 0; i < pages.Count; i++)
        {
            var pill = new Border
            {
                Height = 22, Padding = new Thickness(8, 0, 8, 0), CornerRadius = new CornerRadius(11),
                Background = i == 0 ? Theme.SettingsTabFillBrush : Theme.TransparentBrush,
                Child = new VectorIcon { Data = PageRegistry.Def(pages[i]).Icon, Size = 12, Foreground = i == 0 ? Theme.PrimaryText : Theme.TertiaryText, VerticalAlignment = VerticalAlignment.Center },
            };
            _previewPages.Children.Add(pill);
            if (pages[i].ToString() == _justMoved) Motions.PopIn(pill);
        }
        _previewPageName.Text = pages.Count > 0 ? PageRegistry.Def(pages[0]).Name : "";
    }

    // ---- Lists ---------------------------------------------------------------------------------------------
    private void Refresh()
    {
        foreach (var (list, placement) in new[] { (_pageList, Placement.Page), (_iconList, Placement.Icon), (_hiddenList, Placement.Hidden) })
        {
            var ids = placement switch
            {
                Placement.Page => _pages.Enabled.Select(p => p.ToString()).ToList(),
                Placement.Icon => _pages.IconIds.ToList(),
                _ => _pages.Ordered.Where(d => _pages.PlacementOf(d.Id) == Placement.Hidden).Select(d => d.Id.ToString()).ToList(),
            };
            list.Children.Clear();
            foreach (var id in ids)
            {
                var row = _rows.TryGetValue(id, out var r) ? r : _rows[id] = Row(id);
                if (row.Parent is StackPanel old) old.Children.Remove(row);
                list.Children.Add(row);
                if (id == _justMoved) Motions.PopIn(row);
            }
            // Every list keeps its "empty" caption (shown only while it has no rows), so a list stays a drop target.
            list.Children.Add(new TextBlock { Text = placement == Placement.Hidden ? "Nothing hidden" : "None", Style = SettingsUi.StyleOf("CaptionText"), Margin = new Thickness(Theme.S2, 0, 0, 0), Tag = EmptyTag });
            foreach (var id in ids) if (_picks.TryGetValue(id, out var pick)) pick.Value = placement.ToString();
        }
        UpdateEmptyLabels();
        RefreshPreview();
        _justMoved = null;
    }

    private Grid Row(string id)
    {
        bool isSettings = id == PageRegistry.SettingsId;
        var def = isSettings ? null : PageRegistry.Def(Enum.Parse<PageId>(id));
        var row = new Grid { MinHeight = 52, Tag = SettingsWindow.NoDragTag };
        row.Children.Add(new Border { Background = Theme.CardSurfaceBrush, BorderBrush = Theme.CardEdgeBrush, BorderThickness = new Thickness(1), CornerRadius = Theme.Radius });
        var line = new Grid { Margin = new Thickness(Theme.S1, Theme.S2, Theme.S3, Theme.S2) };
        foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto })
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = w });

        var grip = new Border { Width = Theme.GripHitWidth, Background = Theme.TransparentBrush, VerticalAlignment = VerticalAlignment.Stretch,
            Child = new VectorIcon { Data = Icons.Grip, Size = Theme.IconSmall, Foreground = Theme.TertiaryText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        ToolTipService.SetToolTip(grip, isSettings ? "Drag to reorder" : "Drag to reorder, or into another section");
        grip.PointerEntered += (_, _) => grip.ProtectedCursorSet(InputSystemCursorShape.SizeAll);
        grip.PointerPressed += (_, e) => { e.Handled = true; if (e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) BeginDrag(row, id, e); };
        line.Children.Add(grip);

        var tile = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = Theme.TileBrush, BorderBrush = Theme.TileBorderBrush, BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapRightS2,
            Child = new VectorIcon { Data = def?.Icon ?? Icons.Settings, Size = Theme.IconSmall, Foreground = Theme.PrimaryText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        Grid.SetColumn(tile, 1);
        line.Children.Add(tile);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS1 };
        text.Children.Add(new TextBlock { Text = def?.Name ?? "Settings", Style = SettingsUi.StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = def?.Description ?? "Always a header icon, so you can always get back here", Style = SettingsUi.StyleOf("CaptionText"), Foreground = Theme.SecondaryText, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 2);
        line.Children.Add(text);

        var up = new IconButton(28, Theme.IconSmall) { Icon = Icons.ChevronUp, Foreground = Theme.SecondaryText, Tooltip = "Move up", VerticalAlignment = VerticalAlignment.Center };
        up.Click += () => Shift(id, -1);
        Grid.SetColumn(up, 3);
        line.Children.Add(up);
        var down = new IconButton(28, Theme.IconSmall) { Icon = Icons.ChevronDown, Foreground = Theme.SecondaryText, Tooltip = "Move down", VerticalAlignment = VerticalAlignment.Center };
        down.Click += () => Shift(id, +1);
        Grid.SetColumn(down, 4);
        line.Children.Add(down);

        if (def != null)
        {
            var pick = new Segmented(("Page", "Page"), ("Icon", "Icon"), ("Hidden", "Hidden")) { Width = 210, VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS3 };
            pick.Value = _pages.PlacementOf(def.Id).ToString();
            pick.Selected += value =>
            {
                var target = Enum.Parse<Placement>(value);
                if (!_pages.SetPlacement(def.Id, target))
                {
                    pick.Value = _pages.PlacementOf(def.Id).ToString();
                    ShowMessage("Keep at least one page: make another feature a page first.");
                    return;
                }
                _message.Visibility = Visibility.Collapsed;
                _justMoved = id;
                Refresh();
            };
            Grid.SetColumn(pick, 5);
            line.Children.Add(pick);
            _picks[id] = pick;
        }
        else
        {
            var always = new TextBlock { Text = "Always an icon", Style = SettingsUi.StyleOf("CaptionText"), VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS3 };
            Grid.SetColumn(always, 5);
            line.Children.Add(always);
        }
        row.Children.Add(line);
        Motions.SlideIntoPlace(row);
        return row;
    }

    private void ShowMessage(string text)
    {
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
        Motions.PopIn(_message);
    }


    /// <summary>
    /// ↑ / ↓: one place within its section, saved at once; the row slides there. The neighbour is what moves in the
    /// panel, so the clicked row (and the arrow under the pointer) stays put in the tree and repeated clicks keep
    /// working. (Moving the clicked row itself took its button out of the tree mid-click, and Hidden wasn't saved.)
    /// </summary>
    private void Shift(string id, int delta)
    {
        if (_dragRow != null || !_rows.TryGetValue(id, out var row) || row.Parent is not StackPanel list) return;
        var rows = list.Children.OfType<Grid>().ToList();
        int i = rows.IndexOf(row), j = i + delta;
        if (i < 0 || j < 0 || j >= rows.Count) return;
        if (!_pages.MoveTo(id, PlacementFor(list), j)) return;
        var other = rows[j];
        list.Children.Remove(other);
        int at = list.Children.IndexOf(row);
        list.Children.Insert(delta > 0 ? at : at + 1, other);
        RefreshPreview();
    }

    // ---- Drag and drop: reorder within a section or move between Pages, Header icons and Hidden ------------------
    /// <summary>Lifts the row out of its list into the drag layer, leaving the dashed drop slot in its place.</summary>
    private void BeginDrag(Grid row, string id, PointerRoutedEventArgs e)
    {
        if (_dragRow != null || row.Parent is not StackPanel list) return;
        if (!CapturePointer(e.Pointer)) return;
        var at = row.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
        _grabY = e.GetCurrentPoint(this).Position.Y - at.Y;
        _dragRow = row;
        _dragId = id;
        _message.Visibility = Visibility.Collapsed;

        var v = Anim.VisualOf(row, translation: true);
        v.ImplicitAnimations = null; // it follows the pointer exactly; only the others glide
        v.StopAnimation("Translation");
        v.Properties.InsertVector3("Translation", Vector3.Zero);
        double width = row.ActualWidth;
        _placeholder.Height = row.ActualHeight;
        int index = list.Children.IndexOf(row);
        list.Children.RemoveAt(index);
        list.Children.Insert(index, _placeholder);
        row.Width = width;
        Canvas.SetLeft(row, at.X);
        Canvas.SetTop(row, at.Y);
        _dragLayer.Children.Add(row);

        // Lift: a touch bigger and lighter, animated.
        Motions.CentreOrigin(row);
        var c = v.Compositor;
        var lift = c.CreateVector3KeyFrameAnimation();
        lift.InsertKeyFrame(1, new Vector3(1.025f, 1.025f, 1), c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f)));
        lift.Duration = TimeSpan.FromMilliseconds(160);
        v.StartAnimation("Scale", lift);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 0.94f);
        fade.Duration = TimeSpan.FromMilliseconds(160);
        v.StartAnimation("Opacity", fade);
    }

    /// <summary>The row follows the pointer; the drop slot moves to where it would land (the rows around it glide).</summary>
    private void Drag(PointerRoutedEventArgs e)
    {
        if (_dragRow == null || _dragId == null) return;
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) { EndDrag(); return; } // never stuck to a released mouse
        double y = point.Position.Y;
        Canvas.SetTop(_dragRow, y - _grabY);
        if (DropTarget(y) is { } t) MovePlaceholder(t.list, t.index);
        AutoScroll(y);
    }

    /// <summary>The section and position under the pointer, or null where the row can't go.</summary>
    private (StackPanel list, int index)? DropTarget(double y)
    {
        bool settings = _dragId == PageRegistry.SettingsId;
        bool lastPage = _dragId != null && Enum.TryParse<PageId>(_dragId, out var pid) && _pages.IsEnabled(pid) && _pages.Enabled.Count <= 1;
        StackPanel? best = null;
        double bestDistance = double.MaxValue;
        foreach (var list in new[] { _pageList, _iconList, _hiddenList })
        {
            if (settings && list != _iconList) continue; // Settings is always a header icon
            if (lastPage && list != _pageList) continue; // keep at least one page
            double top = list.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            double bottom = top + Math.Max(list.ActualHeight, 24);
            double d = y < top ? top - y : y > bottom ? y - bottom : 0;
            if (d < bestDistance) { bestDistance = d; best = list; }
        }
        if (best == null) return null;
        int index = 0;
        foreach (var r in best.Children.OfType<Grid>())
        {
            double mid = r.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, r.ActualHeight / 2)).Y;
            if (y > mid) index++;
        }
        return (best, index);
    }

    /// <summary>Rows of <paramref name="list"/> above the drop slot (its index within the section).</summary>
    private int SlotIndex(StackPanel list)
    {
        int slot = list.Children.IndexOf(_placeholder);
        return list.Children.OfType<Grid>().Count(r => list.Children.IndexOf(r) < slot);
    }

    private void MovePlaceholder(StackPanel list, int index)
    {
        if (_placeholder.Parent == list && SlotIndex(list) == index) return;
        if (_placeholder.Parent is StackPanel from) from.Children.Remove(_placeholder);
        var rows = list.Children.OfType<Grid>().ToList();
        int at = index < rows.Count ? list.Children.IndexOf(rows[index])
            : rows.Count > 0 ? list.Children.IndexOf(rows[^1]) + 1 : 0;
        list.Children.Insert(at, _placeholder);
        UpdateEmptyLabels();
    }

    /// <summary>"None" / "Nothing hidden" only while a section has no rows and the drop slot isn't in it.</summary>
    private void UpdateEmptyLabels()
    {
        foreach (var list in new[] { _pageList, _iconList, _hiddenList })
        {
            bool empty = !list.Children.OfType<Grid>().Any() && _placeholder.Parent != list;
            foreach (var label in list.Children.OfType<TextBlock>().Where(t => Equals(t.Tag, EmptyTag)))
                label.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Near the top / bottom edge of the list area, scroll so far sections can be reached.</summary>
    private void AutoScroll(double y)
    {
        double top = _scroll.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        double h = _scroll.ActualHeight, edge = 48;
        double speed = y < top + edge ? -(top + edge - y) : y > top + h - edge ? y - (top + h - edge) : 0;
        if (speed != 0) _scroll.ChangeView(null, Math.Clamp(_scroll.VerticalOffset + speed * 0.5, 0, _scroll.ScrollableHeight), null, disableAnimation: true);
    }

    /// <summary>Drops the row where the slot is: saves the move (applies to the notch at once) and the row glides home.</summary>
    private void EndDrag()
    {
        if (_dragRow == null) return;
        var row = _dragRow;
        string id = _dragId!;
        _dragRow = null;
        _dragId = null;
        var from = new Windows.Foundation.Point(Canvas.GetLeft(row), Canvas.GetTop(row));
        _dragLayer.Children.Remove(row);
        row.Width = double.NaN;

        // Save: the section the slot is in, at the slot's position (one change, applied to the notch straight away).
        if (_placeholder.Parent is StackPanel list)
        {
            int index = SlotIndex(list);
            list.Children.Remove(_placeholder);
            if (!_pages.MoveTo(id, PlacementFor(list), index))
                ShowMessage("Keep at least one page: make another feature a page first.");
        }
        Refresh(); // rebuild the lists from the saved layout: the row lands in its slot, its switch shows the section
        UpdateLayout();

        // The row glides from where it was dropped into its slot and settles back to its normal size.
        var v = Anim.VisualOf(row, translation: true);
        var c = v.Compositor;
        var to = row.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f));
        var back = c.CreateVector3KeyFrameAnimation();
        back.InsertKeyFrame(0, new Vector3((float)(from.X - to.X), (float)(from.Y - to.Y), 0));
        back.InsertKeyFrame(1, Vector3.Zero, ease);
        back.Duration = TimeSpan.FromMilliseconds(240);
        v.StartAnimation("Translation", back);
        var scale = c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1, Vector3.One, ease);
        scale.Duration = TimeSpan.FromMilliseconds(200);
        v.StartAnimation("Scale", scale);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 1f);
        fade.Duration = TimeSpan.FromMilliseconds(200);
        v.StartAnimation("Opacity", fade);
        Motions.SlideIntoPlace(row);
    }
}
