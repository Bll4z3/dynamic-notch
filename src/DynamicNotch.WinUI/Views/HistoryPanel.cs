using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// The notification history (left half of page 4). Rows are newest first and use the small radius.
/// <list type="bullet">
/// <item>Clear all: the pill squashes, its label cross-fades into a springing check + "Cleared", the count
/// ticks down to 0, visible rows leave in a diagonal cascade (right 40 px, fade, scale 0.96, 25 ms apart,
/// capped at 300 ms; off-screen rows go at once) over a soft highlight sweeping down, then the empty
/// state springs in (the bell wobbles like a ring, "All caught up" rises). An Undo pill stays for 5 s and
/// brings the rows back with the reverse motion; the items are only purged when that window ends.</item>
/// <item>Single dismiss: × slides the row right and fades it, then the rows below spring up (FLIP). A row
/// can also be dragged right (rubber band to the left, velocity fling) or springs back if let go early.</item>
/// </list>
/// All motion is compositor springs; nothing runs while idle.
/// </summary>
public sealed class HistoryPanel : Grid
{
    private readonly HubViewModel _vm;
    private readonly Grid _header = new(), _listHost = new(), _empty = new(), _headerRight = new();
    private readonly StackPanel _list = new(), _emptyText = new();
    private readonly ScrollViewer _scroll = new();
    private readonly TextBlock _count = new(), _emptyTitle = new(), _emptyCaption = new();
    private readonly Border _sweep = new();
    private readonly ClearButton _clear = new();
    private readonly PillButton _undo;
    private readonly VectorIcon _bell = new();
    private readonly Dictionary<long, HistoryRow> _rows = new();
    private readonly CompSpring _emptyShow, _bellTilt, _undoShow, _sweepY, _sweepO, _headerRightShow;
    private readonly DispatcherQueueTimer _countTimer, _afterClear, _clearedReset;
    private double _countShown, _countFrom, _countTo;
    private DateTime _countStart;
    private bool _clearing, _restoring;

    public HistoryPanel(HubViewModel vm)
    {
        _vm = vm;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(Theme.HubHeaderHeight) });
        RowDefinitions.Add(new RowDefinition { Height = Theme.GapS2 });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- Header: bell · "Notifications" · count | Undo · Clear all ----
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new VectorIcon { Data = Icons.Bell, Size = Theme.IconSmall, Foreground = Theme.SecondaryText, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = "Notifications", Style = StyleOf("SectionText"), Margin = Theme.GapLeftS2 });
        _count.Style = StyleOf("TimeText");
        _count.Foreground = Theme.TertiaryText;
        _count.Margin = Theme.GapLeftS2;
        _count.VerticalAlignment = VerticalAlignment.Center;
        title.Children.Add(_count);
        _header.Children.Add(title);

        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        _undo = new PillButton(Icons.Retry, "Undo", quiet: true) { Height = Theme.HubSmallPillHeight, Margin = Theme.GapRightS2, VerticalAlignment = VerticalAlignment.Center, Compact = true };
        _undo.Click += Undo;
        right.Children.Add(_undo);
        _clear.Click += ClearAll;
        right.Children.Add(_clear);
        _headerRight.Children.Add(right);
        _headerRight.HorizontalAlignment = HorizontalAlignment.Right;
        _header.Children.Add(_headerRight);
        Children.Add(_header);

        // ---- List over a highlight sweep; empty state on top ----
        _sweep.Height = Theme.SweepHeight;
        _sweep.VerticalAlignment = VerticalAlignment.Top;
        _sweep.IsHitTestVisible = false;
        _sweep.CornerRadius = Theme.RadiusSmall;
        _sweep.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 0 },
                new GradientStop { Color = Theme.SweepColor, Offset = 0.5 },
                new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 1 },
            },
        };
        _listHost.Children.Add(_sweep);
        _list.Spacing = Theme.HubRowGap;
        _scroll.Content = _list;
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.HorizontalScrollMode = ScrollMode.Disabled;
        _listHost.Children.Add(_scroll);

        _bell.Data = Icons.Bell;
        _bell.Size = Theme.IconHero;
        _bell.Foreground = Theme.SecondaryText;
        _bell.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyTitle.Text = "All caught up";
        _emptyTitle.Style = StyleOf("BaseText");
        _emptyTitle.FontFamily = Theme.FontSemiBold;
        _emptyTitle.FontWeight = FontWeights.SemiBold;
        _emptyTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyTitle.Margin = Theme.GapTopS2;
        _emptyCaption.Style = StyleOf("CaptionText");
        _emptyCaption.Foreground = Theme.SecondaryText;
        _emptyCaption.TextWrapping = TextWrapping.Wrap;
        _emptyCaption.TextTrimming = TextTrimming.None;
        _emptyCaption.TextAlignment = TextAlignment.Center;
        _emptyCaption.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyCaption.MaxWidth = 260;
        _emptyCaption.Margin = Theme.GapTopS1;
        _emptyText.Children.Add(_emptyTitle);
        _emptyText.Children.Add(_emptyCaption);
        var emptyStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        emptyStack.Children.Add(_bell);
        emptyStack.Children.Add(_emptyText);
        _empty.Children.Add(emptyStack);
        _empty.IsHitTestVisible = false;
        _listHost.Children.Add(_empty);
        SetRow(_listHost, 2);
        Children.Add(_listHost);

        // ---- Motion ----
        var c = Anim.VisualOf(this).Compositor;
        var p = c.CreatePropertySet();
        _emptyShow = new CompSpring(p, "e", Motion.Pop, 0);
        _bellTilt = new CompSpring(p, "t", Motion.BellWobble, 0, precision: 20);
        _undoShow = new CompSpring(p, "u", Motion.Pop, 0);
        _sweepY = new CompSpring(p, "sy", Motion.FadeIn, 0, precision: 50);
        _sweepO = new CompSpring(p, "so", Motion.FadeIn, 0);
        _headerRightShow = new CompSpring(p, "hr", Motion.FadeIn, 1);
        var bv = Anim.VisualOf(_bell);
        bv.CenterPoint = new Vector3((float)Theme.IconHero / 2, 2, 0); // swings from the top, like a bell
        bv.StartAnimation("RotationAngleInDegrees", Anim.Expr(c, "p.t", ("p", p)));
        bv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.e, 0, 1)", ("p", p)));
        var tv = Anim.VisualOf(_emptyText, translation: true);
        tv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.e, 0, 1)", ("p", p)));
        tv.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, (1 - Clamp(p.e, 0, 1)) * 8, 0)", ("p", p)));
        var uv = Anim.VisualOf(_undo);
        Motions.CentreOrigin(_undo);
        uv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.u, 0, 1)", ("p", p)));
        uv.StartAnimation("Scale", Anim.Expr(c, "Vector3(0.7 + 0.3 * p.u, 0.7 + 0.3 * p.u, 1)", ("p", p)));
        var sv = Anim.VisualOf(_sweep, translation: true);
        sv.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, p.sy, 0)", ("p", p)));
        sv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.so, 0, 1)", ("p", p)));
        Anim.VisualOf(_headerRight).StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.hr, 0, 1)", ("p", p)));

        _countTimer = Timer(16, repeating: true, StepCount);
        _afterClear = Timer(0, repeating: false, FinishClear);
        _clearedReset = Timer(Motion.UndoWindowMs, repeating: false, () => { _clear.ShowCleared(false); RefreshChrome(animate: true); });

        vm.History.CollectionChanged += OnHistoryChanged;
        vm.PropertyChanged += OnVmChanged;
        foreach (var item in vm.History) AddRow(item, _list.Children.Count, RowArrival.None);
        _countShown = vm.History.Count;
        RefreshChrome(animate: false);
    }

    /// <summary>Blocks for the notch's staggered entrance.</summary>
    public IEnumerable<(FrameworkElement element, int order)> StaggerBlocks => new (FrameworkElement, int)[] { (_header, 1), (_listHost, 2) };

    /// <summary>Fresh ages when the page comes into view.</summary>
    public void OnShown()
    {
        foreach (var r in _rows.Values) r.RefreshAge();
    }

    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    private DispatcherQueueTimer Timer(int ms, bool repeating, Action tick)
    {
        var t = DispatcherQueue.GetForCurrentThread().CreateTimer();
        t.IsRepeating = repeating;
        t.Interval = TimeSpan.FromMilliseconds(ms);
        t.Tick += (_, _) => { if (!repeating) t.Stop(); tick(); };
        return t;
    }

    // ---- Collection → rows ----------------------------------------------------------------------------
    private enum RowArrival { None, New, Restore }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                int i = e.NewStartingIndex;
                foreach (HistoryItem item in e.NewItems!)
                {
                    int at = i < 0 ? 0 : i++;
                    AddRow(item, at, _restoring ? RowArrival.Restore : RowArrival.New, _restoring ? Math.Min(at * Motion.RowStaggerMs, Motion.RowStaggerCapMs) : 0);
                }
                break;
            case NotifyCollectionChangedAction.Remove:
                foreach (HistoryItem item in e.OldItems!)
                    if (_rows.TryGetValue(item.Id, out var row)) Dismiss(row, fling: 0, alreadyRemoved: true);
                break;
            case NotifyCollectionChangedAction.Reset:
                if (!_clearing) { _list.Children.Clear(); _rows.Clear(); }
                break;
        }
        if (!_clearing) RefreshChrome(animate: true);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HubViewModel.EmptyText)) RefreshChrome(animate: false);
        if (e.PropertyName == nameof(HubViewModel.CanUndo))
        {
            _undoShow.SetTarget(_vm.CanUndo ? 1 : 0, _vm.CanUndo ? Motion.Pop : Motion.FadeOut);
            _undo.IsHitTestVisible = _vm.CanUndo;
            if (!_vm.CanUndo) RefreshChrome(animate: true);
        }
    }

    private void AddRow(HistoryItem item, int index, RowArrival arrival, int delayMs = 0)
    {
        var row = new HistoryRow(item);
        // Animate first (keeping the fling), then drop it from the history (that change finds no row left to animate).
        row.DismissRequested += velocity => { Dismiss(row, velocity, alreadyRemoved: false); _vm.Remove(item); };
        _rows[item.Id] = row;
        _list.Children.Insert(Math.Clamp(index, 0, _list.Children.Count), row);
        switch (arrival)
        {
            case RowArrival.New:
                row.EnterFromTop();
                _scroll.ChangeView(null, 0, null, disableAnimation: false);
                break;
            case RowArrival.Restore:
                row.EnterFromRight(delayMs / 1000.0);
                break;
        }
    }

    // ---- Single dismiss: slide out, then the rows below spring up ----------------------------------------
    private void Dismiss(HistoryRow row, double fling, bool alreadyRemoved)
    {
        if (!_rows.Remove(row.Item.Id)) return;
        row.IsHitTestVisible = false;
        double leave = row.LeaveRight(fling);
        var t = DispatcherQueue.GetForCurrentThread().CreateTimer();
        t.IsRepeating = false;
        t.Interval = TimeSpan.FromSeconds(Math.Max(0.05, leave * 0.7));
        t.Tick += (_, _) =>
        {
            t.Stop();
            int index = _list.Children.IndexOf(row);
            if (index < 0) return;
            double gap = row.ActualHeight + _list.Spacing;
            _list.Children.RemoveAt(index);
            // FLIP: the rows below jump back to where they were and spring up into the gap.
            for (int k = index; k < _list.Children.Count; k++)
                if (_list.Children[k] is HistoryRow below) below.SpringUpFrom(gap);
            RefreshChrome(animate: true);
        };
        t.Start();
    }

    // ---- Clear all --------------------------------------------------------------------------------------
    private void ClearAll()
    {
        if (_clearing || _rows.Count == 0) return;
        _clearing = true;
        _clear.ShowCleared(true);
        CountTo(0);

        // Visible rows cascade out; the ones scrolled out of view go at once.
        double viewTop = 0, viewBottom = _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : _listHost.ActualHeight;
        var visible = new List<HistoryRow>();
        foreach (var child in _list.Children.OfType<HistoryRow>().ToList())
        {
            double y;
            try { y = child.TransformToVisual(_scroll).TransformPoint(new Windows.Foundation.Point(0, 0)).Y; } catch { y = -1e6; }
            if (y + child.ActualHeight < viewTop || y > viewBottom) _list.Children.Remove(child);
            else visible.Add(child);
        }
        int last = 0;
        for (int k = 0; k < visible.Count; k++)
        {
            int delay = Math.Min(k * Motion.RowStaggerMs, Motion.RowStaggerCapMs);
            last = delay;
            visible[k].IsHitTestVisible = false;
            visible[k].LeaveRight(0, delay / 1000.0);
        }
        _rows.Clear();
        Sweep(viewBottom);
        _afterClear.Interval = TimeSpan.FromMilliseconds(last + Motion.SweepMs * 0.6);
        _afterClear.Start();
    }

    private void FinishClear()
    {
        _list.Children.Clear();
        _vm.ClearWithUndo(); // the items stay restorable until the undo window ends
        _clearing = false;
        RefreshChrome(animate: true);
        _clearedReset.Stop();
        _clearedReset.Start();
    }

    private void Undo()
    {
        if (!_vm.CanUndo) return;
        _clearedReset.Stop();
        _clear.ShowCleared(false);
        _restoring = true;
        try { _vm.Undo(); }
        finally { _restoring = false; }
        CountTo(_vm.History.Count);
        RefreshChrome(animate: true);
    }

    /// <summary>A soft white band sweeps down behind the leaving rows.</summary>
    private void Sweep(double height)
    {
        if (!Anim.Enabled) return;
        _sweepY.Snap(-Theme.SweepHeight);
        _sweepO.Snap(0);
        _sweepO.SetTarget(1, Motion.FadeIn);
        var c = _sweep.XamlRoot != null ? Anim.VisualOf(_sweep).Compositor : null;
        if (c == null) return;
        _sweepY.SetTarget(height, Motion.RowCollapse);
        var fade = DispatcherQueue.GetForCurrentThread().CreateTimer();
        fade.IsRepeating = false;
        fade.Interval = TimeSpan.FromMilliseconds(Motion.SweepMs * 0.55);
        fade.Tick += (_, _) => { fade.Stop(); _sweepO.SetTarget(0, Motion.FadeOut); };
        fade.Start();
    }

    // ---- Chrome: count, header buttons, empty state ----------------------------------------------------
    private bool _wasEmpty;

    private void RefreshChrome(bool animate)
    {
        int n = _vm.History.Count;
        if (!_countTimer.IsRunning) { _countShown = n; _count.Text = n > 0 ? n.ToString(CultureInfo.CurrentCulture) : ""; }
        bool showRight = n > 0 || _vm.CanUndo || _clearing;
        _headerRightShow.SetTarget(showRight ? 1 : 0, showRight ? Motion.FadeIn : Motion.FadeOut);
        _headerRight.IsHitTestVisible = showRight;
        _clear.IsHitTestVisible = n > 0 && !_clearing;
        if (n > 0 && !_vm.CanUndo && !_clearing) _clear.ShowCleared(false);

        bool empty = n == 0 && !_clearing;
        string text = _vm.EmptyText;
        _emptyCaption.Text = text == "You're all caught up" ? "" : text;
        _emptyCaption.Visibility = string.IsNullOrEmpty(_emptyCaption.Text) ? Visibility.Collapsed : Visibility.Visible;
        if (empty && !_wasEmpty)
        {
            if (animate && Anim.Enabled)
            {
                _emptyShow.Snap(0);
                _emptyShow.SetTarget(1, Motion.Pop);
                _bellTilt.Snap(0);
                _bellTilt.Impulse(Motion.BellWobbleImpulse); // a damped ring: about ±12°, decaying
            }
            else _emptyShow.Snap(1);
        }
        else if (!empty && _wasEmpty) _emptyShow.SetTarget(0, Motion.FadeOut);
        else if (!empty) _emptyShow.Snap(0);
        _wasEmpty = empty;
    }

    private void CountTo(double target)
    {
        if (!Anim.Enabled) { _countShown = target; _count.Text = target > 0 ? target.ToString(CultureInfo.CurrentCulture) : ""; return; }
        _countFrom = _countShown;
        _countTo = target;
        _countStart = DateTime.UtcNow;
        _countTimer.Start();
    }

    private void StepCount()
    {
        double t = Math.Clamp((DateTime.UtcNow - _countStart).TotalMilliseconds / Motion.CountUpMs, 0, 1);
        _countShown = Math.Round(_countFrom + (_countTo - _countFrom) * (1 - Math.Pow(1 - t, 3)));
        _count.Text = _countShown > 0 ? _countShown.ToString(CultureInfo.CurrentCulture) : "";
        if (t >= 1) { _countTimer.Stop(); RefreshChrome(animate: true); }
    }

    // =======================================================================================================
    /// <summary>"Clear all" pill: press squash; its label cross-fades into a springing check + "Cleared".</summary>
    private sealed class ClearButton : Grid
    {
        private readonly TextBlock _label = new(), _done = new();
        private readonly VectorIcon _check = new();
        private readonly StackPanel _doneRow = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        private readonly CompSpring _state;
        private readonly Border _fill = new();

        public ClearButton()
        {
            Height = Theme.HubSmallPillHeight;
            MinWidth = Theme.ClearPillMinWidth;
            Background = Theme.TransparentBrush;
            var radius = new CornerRadius(Theme.HubSmallPillHeight / 2);
            var back = new Border { Background = Theme.QuietPillBrush, BorderBrush = Theme.HighlightBorderBrush, BorderThickness = new Thickness(1), CornerRadius = radius };
            _fill.Background = Theme.PillLightenBrush;
            _fill.CornerRadius = radius;
            Children.Add(back);
            Children.Add(_fill);
            _label.Text = "Clear all";
            foreach (var t in new[] { _label, _done })
            {
                t.FontFamily = Theme.FontSemiBold;
                t.FontWeight = FontWeights.SemiBold;
                t.FontSize = Theme.FontCaption;
                t.Foreground = Theme.PrimaryText;
                t.VerticalAlignment = VerticalAlignment.Center;
                t.HorizontalAlignment = HorizontalAlignment.Center;
                t.IsHitTestVisible = false;
            }
            _done.Text = "Cleared";
            _done.Margin = Theme.GapLeftS1;
            _check.Data = Icons.Check;
            _check.Size = Theme.IconSmall - 2;
            _check.Foreground = Theme.SuccessBrush;
            _check.VerticalAlignment = VerticalAlignment.Center;
            _doneRow.Children.Add(_check);
            _doneRow.Children.Add(_done);
            var labels = new Grid { Margin = new Thickness(Theme.S3, 0, Theme.S3, 0) };
            labels.Children.Add(_label);
            labels.Children.Add(_doneRow);
            Children.Add(labels);

            var c = Anim.VisualOf(this).Compositor;
            var (state, set) = Motions.Value(c, Motion.Pop, 0);
            _state = state;
            Anim.VisualOf(_label).StartAnimation("Opacity", Anim.Expr(c, "Clamp(1 - p.v * 2, 0, 1)", ("p", set)));
            Anim.VisualOf(_done).StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v * 1.4 - 0.4, 0, 1)", ("p", set)));
            var cv = Anim.VisualOf(_check);
            cv.CenterPoint = new Vector3((float)(Theme.IconSmall - 2) / 2, (float)(Theme.IconSmall - 2) / 2, 0);
            cv.StartAnimation("Scale", Anim.Expr(c, "Vector3(Clamp(p.v, 0, 1.3), Clamp(p.v, 0, 1.3), 1)", ("p", set)));
            cv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", set)));
            Motions.HoverFade(this, _fill);
            Motions.PressScale(this);
            Tapped += (_, e) => { e.Handled = true; Click?.Invoke(); };
        }

        public event Action? Click;

        public void ShowCleared(bool cleared) => _state.SetTarget(cleared ? 1 : 0, cleared ? Motion.Pop : Motion.FadeIn);
    }

    // =======================================================================================================
    /// <summary>
    /// One history row: logo / glyph tile, title, age, app · body, × on hover. Its translation, opacity and
    /// scale are springs on its own property set; a horizontal drag moves it directly (rubber band to the
    /// left) and a release past the threshold or with enough speed flings it away.
    /// </summary>
    private sealed class HistoryRow : Grid
    {
        private readonly CompSpring _x, _y, _o, _s;
        private readonly TextBlock _age;
        private readonly IconButton _close;
        private Windows.Foundation.Point _start;
        private bool _pressed, _dragging, _dragged;
        private uint _pointerId;
        private readonly List<(double x, DateTime t)> _samples = new();

        public HistoryRow(HistoryItem item)
        {
            Item = item;
            MinHeight = Theme.HubRowMinHeight;
            Background = Theme.TransparentBrush;
            var card = new Border { Background = Theme.CardBrush, BorderBrush = Theme.CardBorderBrush, BorderThickness = new Thickness(1), CornerRadius = Theme.RadiusSmall };
            var hover = new Border { Background = Theme.CardHoverBrush, CornerRadius = Theme.RadiusSmall };
            Children.Add(card);
            Children.Add(hover);
            Motions.HoverFade(this, hover);

            var g = new Grid { Padding = Theme.HubRowPadding };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var tile = new Border
            {
                Width = Theme.HubRowIcon, Height = Theme.HubRowIcon, CornerRadius = Theme.RadiusSmall, VerticalAlignment = VerticalAlignment.Top,
                Background = Theme.TileBrush, BorderBrush = Theme.TileBorderBrush, BorderThickness = new Thickness(1),
            };
            var tileContent = new Grid();
            tileContent.Children.Add(new VectorIcon { Data = item.Glyph, Size = Theme.IconSmall, Foreground = Theme.PrimaryText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            tile.Child = tileContent;
            if (item.Logo != null) _ = LoadLogoAsync(item, tileContent);
            g.Children.Add(tile);

            var text = new StackPanel { Margin = Theme.GapLeftS2, VerticalAlignment = VerticalAlignment.Center };
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(new TextBlock { Text = item.Title, Style = StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold });
            _age = new TextBlock { Style = StyleOf("TimeText"), Foreground = Theme.TertiaryText, Margin = Theme.GapLeftS2, VerticalAlignment = VerticalAlignment.Center };
            SetColumn(_age, 1);
            top.Children.Add(_age);
            text.Children.Add(top);
            string sub = string.Join(" · ", new[] { item.AppName, item.Body }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (sub.Length > 0) text.Children.Add(new TextBlock { Text = sub, Style = StyleOf("CaptionText"), Foreground = Theme.SecondaryText, Margin = Theme.GapTopS1 });
            SetColumn(text, 1);
            g.Children.Add(text);

            _close = new IconButton(20, Theme.OverlayIconSize) { Icon = Icons.Close, Foreground = Theme.SecondaryText, Tooltip = "Dismiss", Margin = Theme.GapLeftS1, VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
            _close.Click += () => DismissRequested?.Invoke(0);
            SetColumn(_close, 2);
            g.Children.Add(_close);
            Children.Add(g);
            RefreshAge();

            var visual = Anim.VisualOf(this, translation: true);
            var c = visual.Compositor;
            var p = c.CreatePropertySet();
            _x = new CompSpring(p, "x", Motion.RowLeave, 0, precision: 50);
            _y = new CompSpring(p, "y", Motion.RowCollapse, 0, precision: 50);
            _o = new CompSpring(p, "o", Motion.RowLeave, 1);
            _s = new CompSpring(p, "s", Motion.RowLeave, 1);
            visual.StartAnimation("Translation", Anim.Expr(c, "Vector3(p.x, p.y, 0)", ("p", p)));
            visual.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.o, 0, 1)", ("p", p)));
            visual.StartAnimation("Scale", Anim.Expr(c, "Vector3(p.s, p.s, 1)", ("p", p)));
            SizeChanged += (_, e) => visual.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);

            PointerEntered += (_, _) => _close.Opacity = 1;
            PointerExited += (_, _) => { if (!_dragging) _close.Opacity = 0; };
            PointerPressed += OnPressed;
            PointerMoved += OnMoved;
            PointerReleased += OnReleased;
            PointerCaptureLost += (_, _) => { if (_dragging) Release(0); _pressed = false; };
            Tapped += (_, e) =>
            {
                if (_dragged) { _dragged = false; e.Handled = true; return; }
                if (Item.OnClick is { } click) { click(); DismissRequested?.Invoke(0); }
            };
        }

        public HistoryItem Item { get; }
        /// <summary>The row asks to be dismissed (argument: fling velocity in DIPs/s, 0 for a plain dismiss).</summary>
        public event Action<double>? DismissRequested;

        public void RefreshAge() => _age.Text = Age(Item.Time);

        public void EnterFromTop()
        {
            if (!Anim.Enabled) return;
            _y.Snap(-Theme.S2); _o.Snap(0); _s.Snap(Motion.RowLeaveScale);
            _y.SetTarget(0, Motion.RowEnter); _o.SetTarget(1, Motion.FadeIn); _s.SetTarget(1, Motion.RowEnter);
        }

        /// <summary>Undo: the reverse of the clear cascade.</summary>
        public void EnterFromRight(double delay)
        {
            if (!Anim.Enabled) return;
            _x.Snap(Motion.RowLeaveDistance); _o.Snap(0); _s.Snap(Motion.RowLeaveScale);
            _x.SetTarget(0, Motion.RowEnter, delay); _o.SetTarget(1, Motion.FadeIn, delay); _s.SetTarget(1, Motion.RowEnter, delay);
        }

        /// <summary>Slides right, fades and shrinks. Returns roughly how long it takes (s).</summary>
        public double LeaveRight(double fling, double delay = 0)
        {
            double distance = fling > 0 ? ActualWidth + Theme.S4 : Math.Max(Motion.RowLeaveDistance, _x.Value + Motion.RowLeaveDistance);
            _x.SetTarget(distance, Motion.RowLeave, delay);
            if (fling > 0) _x.Impulse(fling);
            _o.SetTarget(0, Motion.RowLeave, delay);
            _s.SetTarget(Motion.RowLeaveScale, Motion.RowLeave, delay);
            return delay + _o.Model.RemainingDuration;
        }

        public void SpringUpFrom(double gap)
        {
            if (!Anim.Enabled) return;
            _y.Snap(_y.Value + gap);
            _y.SetTarget(0, Motion.RowCollapse);
        }

        // ---- Swipe to dismiss ----
        private void OnPressed(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(this);
            if (!pt.Properties.IsLeftButtonPressed) return;
            _pressed = true;
            _dragging = false;
            _start = pt.Position;
            _pointerId = e.Pointer.PointerId;
        }

        private void OnMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_pressed || e.Pointer.PointerId != _pointerId) return;
            var pt = e.GetCurrentPoint(this).Position;
            double dx = pt.X - _start.X, dy = pt.Y - _start.Y;
            if (!_dragging)
            {
                if (Math.Abs(dx) < Motion.SwipeStartDistance || Math.Abs(dx) < Math.Abs(dy)) return;
                _dragging = CapturePointer(e.Pointer);
                _samples.Clear();
            }
            // Free to the right; rubber band to the left.
            double x = dx >= 0 ? dx : dx * Motion.SwipeLeftResistance;
            _x.Snap(x);
            _o.Snap(Math.Clamp(1 - Math.Max(0, x) / Math.Max(1, ActualWidth), 0.2, 1));
            _samples.Add((x, DateTime.UtcNow));
            if (_samples.Count > 5) _samples.RemoveAt(0);
            e.Handled = true;
        }

        private void OnReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging) { _pressed = false; return; }
            ReleasePointerCapture(e.Pointer);
            double v = 0;
            if (_samples.Count >= 2)
            {
                var (x0, t0) = _samples[0];
                var (x1, t1) = _samples[^1];
                double dt = (t1 - t0).TotalSeconds;
                if (dt > 0.001) v = (x1 - x0) / dt;
            }
            Release(v);
            e.Handled = true;
        }

        private void Release(double velocity)
        {
            _pressed = false;
            _dragging = false;
            _dragged = true; // the tap that ends a drag isn't a click
            bool commit = _x.Value > ActualWidth * Motion.SwipeCommitFraction || velocity > Motion.SwipeFlingVelocity;
            if (commit) DismissRequested?.Invoke(Math.Max(velocity, Motion.SwipeFlingVelocity));
            else
            {
                _x.SetTarget(0, Motion.RowSnapBack);
                if (velocity != 0) _x.Impulse(velocity);
                _o.SetTarget(1, Motion.FadeIn);
            }
        }

        private static async Task LoadLogoAsync(HistoryItem item, Grid host)
        {
            var source = await ImageLoader.ToSourceAsync(item.Logo);
            if (source == null) return;
            host.Children.Clear();
            host.Children.Add(new Border { CornerRadius = Theme.RadiusSmall, Margin = new Thickness(-1), Background = new ImageBrush { ImageSource = source, Stretch = Stretch.UniformToFill } });
        }

        private static string Age(DateTime t)
        {
            var d = DateTime.Now - t;
            if (d.TotalMinutes < 1) return "now";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes}m";
            if (d.TotalDays < 1) return $"{(int)d.TotalHours}h";
            return t.ToString("d MMM", CultureInfo.CurrentCulture);
        }
    }
}
