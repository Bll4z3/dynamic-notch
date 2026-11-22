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
    private Grid? _dragRow;
    private string? _dragId;
    private double _dragStartY, _dragShift;
    private string? _justMoved;

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
        Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Refresh();
    }

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
            if (ids.Count == 0)
                list.Children.Add(new TextBlock { Text = placement == Placement.Hidden ? "Nothing hidden" : "None", Style = SettingsUi.StyleOf("CaptionText"), Margin = new Thickness(Theme.S2, 0, 0, 0) });
        }
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
        ToolTipService.SetToolTip(grip, "Drag to reorder");
        grip.PointerEntered += (_, _) => grip.ProtectedCursorSet(InputSystemCursorShape.SizeNorthSouth);
        grip.PointerPressed += (_, e) => { if (grip.CapturePointer(e.Pointer)) BeginDrag(row, id, e); e.Handled = true; };
        grip.PointerMoved += (_, e) => { if (_dragRow == row) Drag(e); };
        grip.PointerReleased += (_, e) => { grip.ReleasePointerCapture(e.Pointer); EndDrag(); };
        grip.PointerCaptureLost += (_, _) => EndDrag();
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

    /// <summary>One place up / down within its group (pages or icons); the row slides there.</summary>
    private void Shift(string id, int delta)
    {
        if (!_rows.TryGetValue(id, out var row) || row.Parent is not StackPanel list) return;
        int i = list.Children.IndexOf(row), j = i + delta;
        if (j < 0 || j >= list.Children.Count || list.Children[j] is not Grid) return;
        if (list != _hiddenList) _pages.Shift(id, delta);
        list.Children.RemoveAt(i);
        list.Children.Insert(j, row);
        RefreshPreview();
    }

    // ---- Drag to reorder (within a group) ----------------------------------------------------------------------
    private void BeginDrag(Grid row, string id, PointerRoutedEventArgs e)
    {
        if (row.Parent is not StackPanel list) return;
        _dragRow = row;
        _dragId = id;
        _dragStartY = e.GetCurrentPoint(list).Position.Y;
        _dragShift = 0;
        var v = Anim.VisualOf(row, translation: true);
        v.ImplicitAnimations = null; // it follows the pointer; only the others glide
        v.StopAnimation("Translation");
        v.StopAnimation("Scale");
        Canvas.SetZIndex(row, 1);
        Motions.CentreOrigin(row);
        v.Scale = new Vector3(1.02f, 1.02f, 1);
    }

    private void Drag(PointerRoutedEventArgs e)
    {
        if (_dragRow?.Parent is not StackPanel list || _dragId == null) return;
        double dy = e.GetCurrentPoint(list).Position.Y - _dragStartY - _dragShift;
        double step = _dragRow.ActualHeight + list.Spacing;
        int i = list.Children.IndexOf(_dragRow);
        if (dy > step / 2 && i < list.Children.Count - 1) { Shift(_dragId, +1); _dragShift += step; dy -= step; }
        else if (dy < -step / 2 && i > 0) { Shift(_dragId, -1); _dragShift -= step; dy += step; }
        Anim.VisualOf(_dragRow, translation: true).Properties.InsertVector3("Translation", new Vector3(0, (float)dy, 0));
    }

    private void EndDrag()
    {
        if (_dragRow == null) return;
        var row = _dragRow;
        _dragRow = null;
        _dragId = null;
        var v = Anim.VisualOf(row, translation: true);
        var c = v.Compositor;
        var back = c.CreateVector3KeyFrameAnimation();
        back.InsertKeyFrame(1, Vector3.Zero, c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f)));
        back.Duration = TimeSpan.FromMilliseconds(220);
        v.StartAnimation("Translation", back);
        var scale = c.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1, Vector3.One);
        scale.Duration = TimeSpan.FromMilliseconds(180);
        v.StartAnimation("Scale", scale);
        Canvas.SetZIndex(row, 0);
        Motions.SlideIntoPlace(row);
    }
}
