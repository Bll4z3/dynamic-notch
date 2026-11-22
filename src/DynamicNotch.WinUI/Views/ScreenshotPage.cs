using System.Collections.Specialized;
using System.ComponentModel;
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
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DynamicNotch.Views;

/// <summary>
/// The Screenshots page: the selected screenshot large (with its name, size and age beside it), the
/// action row under it (Mark up · Ask Claude · Copy · Copy text · Save · Delete) and the shelf: a
/// scrollable strip of the recent screenshots along the bottom. Every picture can be dragged straight
/// out into another app as a real image file; it lifts with a shadow while it's dragged.
/// Motion: a new screenshot drops into the preview with a spring bounce and the action buttons fade and
/// slide in one after another; strip thumbnails pop in, and on delete shrink and fade while the others
/// slide over to close the gap (implicit offset animations). All transform / opacity on the compositor.
/// </summary>
public sealed class ScreenshotPage : Grid
{
    private readonly ScreenshotShelfViewModel _vm;
    private readonly Grid _top = new(), _content = new(), _empty = new();
    private readonly Border _preview = new(), _previewShadowHost = new();
    private readonly Image _previewImage = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _name = new(), _details = new(), _toast = new();
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = Theme.S2 };
    private readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal, Spacing = Theme.S2 };
    private readonly ScrollViewer _stripScroll = new();
    private readonly Dictionary<ScreenshotItem, Thumb> _thumbs = new();
    private readonly List<CompSpring> _actionSprings = new();
    private readonly CompSpring _drop, _toastShow, _lift;
    private readonly DispatcherQueueTimer _toastTimer;
    private readonly PillButton _markup, _ask, _copy, _text, _save, _delete;

    public ScreenshotPage(ScreenshotShelfViewModel vm)
    {
        _vm = vm;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- Empty state ----
        var emptyStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = Theme.S1 };
        emptyStack.Children.Add(new VectorIcon { Data = Icons.Screenshot, Size = Theme.IconHero, Foreground = Theme.SecondaryText, HorizontalAlignment = HorizontalAlignment.Center });
        emptyStack.Children.Add(new TextBlock { Text = "No screenshots yet", Style = StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = Theme.GapTopS1 });
        emptyStack.Children.Add(new TextBlock { Text = "Press Win + Shift + S and your screenshot lands here", Style = StyleOf("CaptionText"), Foreground = Theme.SecondaryText, HorizontalAlignment = HorizontalAlignment.Center });
        _empty.Children.Add(emptyStack);
        _empty.IsHitTestVisible = false;
        Children.Add(_empty);

        // ---- Content: [preview | details] / action row / gap / strip ----
        foreach (var h in new[] { new GridLength(Theme.ShotPreviewHeight), new GridLength(Theme.S2), new GridLength(Theme.ShotActionHeight), new GridLength(Theme.S2), new GridLength(1, GridUnitType.Star) })
            _content.RowDefinitions.Add(new RowDefinition { Height = h });
        Children.Add(_content);

        _top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Theme.S4) });
        _top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var previewHost = new Grid { Width = Theme.ShotPreviewWidth, Height = Theme.ShotPreviewHeight };
        _previewShadowHost.IsHitTestVisible = false;
        previewHost.Children.Add(_previewShadowHost);
        _preview.CornerRadius = Theme.RadiusSmall;
        _preview.Background = Theme.TileBrush;
        _preview.BorderBrush = Theme.TileBorderBrush;
        _preview.BorderThickness = new Thickness(1);
        _preview.Child = _previewImage;
        _preview.CanDrag = true;
        ToolTipService.SetToolTip(_preview, "Drag into another app");
        previewHost.Children.Add(_preview);
        _top.Children.Add(previewHost);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        info.Children.Add(new TextBlock { Text = "Screenshot", Style = StyleOf("SectionText") });
        _name.Style = StyleOf("BaseText");
        _name.FontFamily = Theme.FontSemiBold;
        _name.FontWeight = FontWeights.SemiBold;
        _name.FontSize = Theme.FontBody + 1;
        _name.TextTrimming = TextTrimming.CharacterEllipsis;
        info.Children.Add(_name);
        _details.Style = StyleOf("CaptionText");
        _details.Foreground = Theme.SecondaryText;
        info.Children.Add(_details);
        _toast.Style = StyleOf("CaptionText");
        _toast.FontFamily = Theme.FontSemiBold;
        _toast.FontWeight = FontWeights.SemiBold;
        _toast.Margin = Theme.GapTopS2;
        info.Children.Add(_toast);
        Grid.SetColumn(info, 2);
        _top.Children.Add(info);
        _content.Children.Add(_top);

        // Action row: two labelled primary actions, then the quieter ones.
        _markup = Action(Icons.Pen, "Mark up", "Draw, highlight, add arrows or text, crop", quiet: false);
        _ask = Action(Icons.Sparkle, "Ask Claude", "Ask a question about this screenshot", quiet: true);
        _copy = Action(Icons.Copy, "Copy", "Copy the image", quiet: true);
        _text = Action(Icons.ScanText, "Copy text", "Read the text in it (on this PC) and copy it", quiet: true);
        _save = Action(Icons.Save, "Save", "Save a copy", quiet: true);
        _delete = Action(Icons.Delete, "Delete", "Remove it from the shelf", quiet: true);
        _markup.Click += () => { if (_vm.Selected is { } s) MarkupRequested?.Invoke(s); };
        _ask.Click += () => { if (_vm.Selected is { } s) AskRequested?.Invoke(s); };
        _copy.Click += async () => { if (_vm.Selected is { } s) await _vm.CopyAsync(s); };
        _text.Click += async () => { if (_vm.Selected is { } s) { _text.Active = false; await _vm.CopyTextAsync(s); _text.Active = true; } };
        _save.Click += async () => { if (_vm.Selected is { } s) { await _vm.SaveAsync(s); RefreshSelected(animate: false); } };
        _delete.Click += () => { if (_vm.Selected is { } s) DeleteAnimated(s); };
        Grid.SetRow(_actions, 2);
        _content.Children.Add(_actions);

        // Shelf strip: horizontal, scrolls with the wheel (vertical wheel → sideways).
        _stripScroll.Content = _strip;
        _stripScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _stripScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _stripScroll.HorizontalScrollMode = ScrollMode.Enabled;
        _stripScroll.VerticalScrollMode = ScrollMode.Disabled;
        _stripScroll.PointerWheelChanged += (_, e) =>
        {
            int delta = e.GetCurrentPoint(_stripScroll).Properties.MouseWheelDelta;
            _stripScroll.ChangeView(_stripScroll.HorizontalOffset - delta, null, null);
            e.Handled = true;
        };
        Grid.SetRow(_stripScroll, 4);
        _content.Children.Add(_stripScroll);

        // ---- Motion ----
        var c = Anim.VisualOf(this).Compositor;
        var p = c.CreatePropertySet();
        _drop = new CompSpring(p, "d", Motion.ShotDrop, 1);
        _toastShow = new CompSpring(p, "t", Motion.FadeIn, 0);
        _lift = new CompSpring(p, "l", Motion.Pop, 0);
        var pv = Anim.VisualOf(_preview, translation: true);
        pv.CenterPoint = new Vector3((float)Theme.ShotPreviewWidth / 2, (float)Theme.ShotPreviewHeight / 2, 0);
        // Drop-in: from above and a little small, overshooting into place (the spring's bounce).
        pv.StartAnimation("Translation", Anim.Expr(c, $"Vector3(0, (1 - p.d) * {Anim.F(-Motion.ShotDropDistance)}, 0)", ("p", p)));
        string s = "(0.86 + 0.14 * p.d) * (1 + 0.04 * p.l)";
        pv.StartAnimation("Scale", Anim.Expr(c, $"Vector3({s}, {s}, 1)", ("p", p)));
        pv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.d * 1.5, 0, 1)", ("p", p)));
        var shadow = SoftShadow.Create(c, Theme.CornerRadiusSmall, 18, 1, 6);
        SoftShadow.Layout(shadow, new Vector2((float)Theme.ShotPreviewWidth, (float)Theme.ShotPreviewHeight));
        shadow.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.l, 0, 1) * 0.8", ("p", p)));
        ElementCompositionPreview.SetElementChildVisual(_previewShadowHost, shadow);
        var sv = ElementCompositionPreview.GetElementVisual(_previewShadowHost);
        sv.StartAnimation("Scale", Anim.Expr(c, "pv.Scale", ("pv", pv)));
        sv.StartAnimation("CenterPoint", Anim.Expr(c, "pv.CenterPoint", ("pv", pv)));
        var tv = Anim.VisualOf(_toast, translation: true);
        tv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.t, 0, 1)", ("p", p)));
        tv.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, (1 - Clamp(p.t, 0, 1)) * 4, 0)", ("p", p)));
        foreach (UIElement b in _actions.Children)
        {
            var bv = Anim.VisualOf(b, translation: true);
            var (spring, set) = Motions.Value(c, Motion.ShotAction, 1);
            _actionSprings.Add(spring);
            bv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", set)));
            bv.StartAnimation("Translation", Anim.Expr(c, "Vector3((1 - Clamp(p.v, 0, 1)) * -10, 0, 0)", ("p", set)));
        }

        _preview.DragStarting += async (_, args) =>
        {
            if (_vm.Selected is not { } item) { args.Cancel = true; return; }
            var deferral = args.GetDeferral();
            try { await FillDrag(args, item); } finally { deferral.Complete(); }
            _lift.SetTarget(1, Motion.Pop);
            OutgoingDrag?.Invoke(true);
        };
        _preview.DropCompleted += (_, _) => { _lift.SetTarget(0, Motion.Pop); OutgoingDrag?.Invoke(false); };

        _toastTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _toastTimer.IsRepeating = false;
        _toastTimer.Interval = TimeSpan.FromMilliseconds(Motion.ShotToastMs);
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toastShow.SetTarget(0, Motion.FadeOut); };
        vm.Toast += ShowToast;

        foreach (var item in vm.Items) AddThumb(item, _strip.Children.Count, animate: false);
        vm.Items.CollectionChanged += OnItemsChanged;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(ScreenshotShelfViewModel.Selected)) RefreshSelected(animate: false); };
        RefreshSelected(animate: false);
    }

    /// <summary>Mark up / Ask Claude were clicked (the window opens the editor / goes to Ask).</summary>
    public event Action<ScreenshotItem>? MarkupRequested, AskRequested;
    /// <summary>An outgoing drag started / ended (the window keeps the notch open meanwhile).</summary>
    public event Action<bool>? OutgoingDrag;

    /// <summary>Blocks for the notch's staggered entrance.</summary>
    public IEnumerable<(FrameworkElement element, int order)> StaggerBlocks =>
        new (FrameworkElement, int)[] { (_top, 1), (_actions, 2), (_stripScroll, 3), (_empty, 1) };

    /// <summary>A new screenshot arrived: it drops into the preview and the buttons slide in after it.</summary>
    public void PlayArrival()
    {
        if (!Anim.Enabled) return;
        _drop.Snap(0);
        _drop.SetTarget(1, Motion.ShotDrop);
        for (int i = 0; i < _actionSprings.Count; i++)
        {
            _actionSprings[i].Snap(0);
            _actionSprings[i].SetTarget(1, Motion.ShotAction, Motion.ShotActionDelay + i * Motion.ShotActionStagger);
        }
    }

    // ---- Selection / details ----------------------------------------------------------------------------
    private async void RefreshSelected(bool animate)
    {
        var item = _vm.Selected;
        bool any = item != null;
        _content.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (it, thumb) in _thumbs) thumb.Selected = ReferenceEquals(it, item);
        if (item == null) return;
        _name.Text = System.IO.Path.GetFileNameWithoutExtension(item.Path);
        string size = item.PixelWidth > 0 ? $"{item.PixelWidth} × {item.PixelHeight}" : "";
        string where = _vm.IsSaved(item) ? "Saved" : item.Owned ? "Not saved yet" : System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(item.Path) ?? "");
        _details.Text = string.Join(" · ", new[] { size, Age(item.Time), where }.Where(t => !string.IsNullOrEmpty(t)));
        _save.SetContent(_vm.IsSaved(item) ? Icons.Folder : Icons.Save, _vm.IsSaved(item) ? "Show" : "Save");
        _save.Tooltip = _vm.IsSaved(item) ? "Show it in File Explorer" : $"Save a copy to {System.IO.Path.GetFileName(_vm.SaveFolder.TrimEnd('\\'))}";
        var source = await ImageLoader.ToSourceAsync(item.Thumbnail);
        if (ReferenceEquals(item, _vm.Selected)) _previewImage.Source = source;
    }

    private void ShowToast(string text, bool error)
    {
        _toast.Text = text;
        _toast.Foreground = error ? Theme.DangerBrush : Theme.SuccessBrush;
        _toastShow.SetTarget(1, Motion.FadeIn);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private static string Age(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalSeconds < 60) return "Just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        return t.ToString("d MMM");
    }

    /// <summary>Fresh ages when the page comes into view.</summary>
    public void OnShown() => RefreshSelected(animate: false);

    // ---- Strip ---------------------------------------------------------------------------------------
    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            int at = e.NewStartingIndex;
            foreach (ScreenshotItem item in e.NewItems) AddThumb(item, at++, animate: true);
        }
        else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems != null)
        {
            foreach (ScreenshotItem item in e.OldItems)
                if (_thumbs.Remove(item, out var t)) t.Leave(() => _strip.Children.Remove(t));
        }
        else if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _strip.Children.Clear();
            _thumbs.Clear();
            foreach (var item in _vm.Items) AddThumb(item, _strip.Children.Count, animate: false);
        }
        RefreshSelected(animate: false);
    }

    private void AddThumb(ScreenshotItem item, int index, bool animate)
    {
        var t = new Thumb(item);
        t.Tapped += (_, e) => { e.Handled = true; _vm.Selected = item; };
        t.DragStarting += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try { await FillDrag(args, item); } finally { deferral.Complete(); }
            t.Lift(true);
            OutgoingDrag?.Invoke(true);
        };
        t.DropCompleted += (_, _) => { t.Lift(false); OutgoingDrag?.Invoke(false); };
        _thumbs[item] = t;
        _strip.Children.Insert(Math.Clamp(index, 0, _strip.Children.Count), t);
        if (animate) t.Enter();
        if (index == 0) _stripScroll.ChangeView(0, null, null);
    }

    private void DeleteAnimated(ScreenshotItem item)
    {
        // The preview shrinks away with it; the strip tile animates in OnItemsChanged.
        if (Anim.Enabled) { _drop.SetTarget(0.4, Motion.FadeOut); }
        _vm.Delete(item);
        if (_vm.Selected != null && Anim.Enabled) _drop.SetTarget(1, Motion.ShotDrop);
    }

    private static async Task FillDrag(DragStartingEventArgs args, ScreenshotItem item)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            args.Data.SetStorageItems(new[] { file });
            args.Data.SetBitmap(Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
            args.Data.RequestedOperation = DataPackageOperation.Copy;
            args.AllowedOperations = DataPackageOperation.Copy;
            if (item.Thumbnail != null) args.DragUI.SetContentFromSoftwareBitmap(SmallCopy(item.Thumbnail), new Windows.Foundation.Point(40, 24));
        }
        catch { args.Cancel = true; }
    }

    private static Windows.Graphics.Imaging.SoftwareBitmap SmallCopy(Windows.Graphics.Imaging.SoftwareBitmap b) =>
        Windows.Graphics.Imaging.SoftwareBitmap.Convert(b, Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

    private PillButton Action(string icon, string label, string tip, bool quiet)
    {
        var b = new PillButton(icon, label, quiet) { Height = Theme.ShotActionHeight, Tooltip = tip, Compact = true };
        _actions.Children.Add(b);
        return b;
    }

    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    /// <summary>One shelf thumbnail: rounded, a ring when selected, hover lift, drag-out, pop-in / shrink-out.</summary>
    private sealed class Thumb : Grid
    {
        private readonly Border _ring = new(), _hover = new();
        private readonly Image _img = new() { Stretch = Stretch.UniformToFill };
        private readonly CompSpring _show, _lift, _sel;
        private readonly ScreenshotItem _item;

        public Thumb(ScreenshotItem item)
        {
            _item = item;
            Width = Theme.ShotThumbWidth;
            Height = Theme.ShotThumbHeight;
            CanDrag = true;
            Background = Theme.TransparentBrush;
            ToolTipService.SetToolTip(this, "Click to view · drag into another app");
            var clip = new Border { CornerRadius = Theme.RadiusSmall, Background = Theme.TileBrush, Child = _img };
            Children.Add(clip);
            _hover.CornerRadius = Theme.RadiusSmall;
            _hover.Background = Theme.PillLightenBrush;
            Children.Add(_hover);
            _ring.CornerRadius = Theme.RadiusSmall;
            _ring.BorderBrush = Theme.PrimaryText;
            _ring.BorderThickness = new Thickness(1.5);
            _ring.IsHitTestVisible = false;
            Children.Add(_ring);
            Motions.HoverFade(this, _hover);

            var v = Anim.VisualOf(this);
            var c = v.Compositor;
            var p = c.CreatePropertySet();
            _show = new CompSpring(p, "s", Motion.Pop, 1);
            _lift = new CompSpring(p, "l", Motion.Pop, 0);
            _sel = new CompSpring(p, "e", Motion.Hover, 0);
            v.CenterPoint = new Vector3((float)Theme.ShotThumbWidth / 2, (float)Theme.ShotThumbHeight / 2, 0);
            v.StartAnimation("Scale", Anim.Expr(c, "Vector3(p.s * (1 + 0.08 * p.l), p.s * (1 + 0.08 * p.l), 1)", ("p", p)));
            v.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.s, 0, 1)", ("p", p)));
            Anim.VisualOf(_ring).StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.e, 0, 1)", ("p", p)));
            // Neighbours slide over (rather than jump) when a tile is added or removed.
            var offset = c.CreateVector3KeyFrameAnimation();
            offset.Target = "Offset";
            offset.InsertExpressionKeyFrame(1, "this.FinalValue", c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f)));
            offset.Duration = TimeSpan.FromMilliseconds(260);
            var implicitSet = c.CreateImplicitAnimationCollection();
            implicitSet["Offset"] = offset;
            v.ImplicitAnimations = implicitSet;

            Load();
            item.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(ScreenshotItem.Thumbnail) or nameof(ScreenshotItem.Version)) Load(); };
        }

        private async void Load() => _img.Source = await ImageLoader.ToSourceAsync(_item.Thumbnail);

        public bool Selected { set => _sel.SetTarget(value ? 1 : 0, Motion.Hover); }

        public void Enter()
        {
            if (!Anim.Enabled) return;
            _show.Snap(0);
            _show.SetTarget(1, Motion.Pop, 0.12);
        }

        public void Lift(bool on) => _lift.SetTarget(on ? 1 : 0, Motion.Pop);

        /// <summary>Shrinks and fades, then <paramref name="removed"/> takes it out (the others slide over).</summary>
        public void Leave(Action removed)
        {
            IsHitTestVisible = false;
            if (!Anim.Enabled) { removed(); return; }
            _show.SetTarget(0, Motion.FadeOut);
            var t = DispatcherQueue.GetForCurrentThread().CreateTimer();
            t.IsRepeating = false;
            t.Interval = TimeSpan.FromMilliseconds(200);
            t.Tick += (_, _) => { t.Stop(); removed(); };
            t.Start();
        }
    }
}
