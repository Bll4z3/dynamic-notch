using System.Collections.Specialized;
using System.ComponentModel;
using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DynamicNotch.Views;

/// <summary>
/// Page 2, Clipboard + Shelf (same layout and behaviour as the WPF page). Clipboard items are compact
/// tiles: click to copy back, drag out into another app, hover to zoom with a little lift and reveal
/// pin / delete, and after a short hover a large preview card springs out next to the cursor. The shelf
/// keeps file paths dropped on the notch: click to open, drag out to share, × / Clear all. A dashed
/// drop zone shows when the shelf is empty and lights up and marches while files hover over the notch.
/// </summary>
public sealed class ClipboardShelfPage : Grid
{
    private readonly ClipboardViewModel _clip;
    private readonly ShelfViewModel _shelf;
    private readonly Border _clipCard = new(), _shelfCard = new();
    private readonly VariableSizedWrapGrid _clipGrid = new(), _shelfGrid = new();
    private readonly TextBlock _clipEmpty = new();
    private readonly TextButton _clearAll = new();
    private readonly Dictionary<ClipItemViewModel, ClipTile> _clipTiles = new();
    private readonly Dictionary<ShelfItemViewModel, FrameworkElement> _shelfTiles = new();
    private readonly DropZone _drop;
    private readonly DispatcherQueueTimer _previewDelay, _previewGrace;
    private (ClipItemViewModel item, FrameworkElement tile)? _pendingPreview;
    private bool _previewOpen, _pressing;

    public ClipboardShelfPage(ClipboardViewModel clipboard, ShelfViewModel shelf)
    {
        _clip = clipboard;
        _shelf = shelf;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = Theme.GapS3 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ---- Clipboard ----
        var clipBody = new Grid();
        _clipEmpty.Text = "Copy something and it shows up here";
        _clipEmpty.Style = StyleOf("CaptionText");
        _clipEmpty.HorizontalAlignment = HorizontalAlignment.Center;
        _clipEmpty.VerticalAlignment = VerticalAlignment.Center;
        // Dense tile grid. No ScrollViewer: it would clip tiles as they zoom on hover.
        _clipGrid.Orientation = Orientation.Horizontal;
        _clipGrid.ItemWidth = _clipGrid.ItemHeight = Theme.ClipTileSize + Theme.ClipTileMargin.Left + Theme.ClipTileMargin.Right;
        clipBody.Children.Add(_clipEmpty);
        clipBody.Children.Add(_clipGrid);
        Card(_clipCard, Icons.Clipboard, "Clipboard", null, clipBody);
        Children.Add(_clipCard);

        // ---- Shelf ----
        var shelfBody = new Grid();
        _drop = new DropZone();
        Canvas.SetZIndex(_drop, 1);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            Content = _shelfGrid,
        };
        _shelfGrid.Orientation = Orientation.Horizontal;
        _shelfGrid.ItemWidth = Theme.ShelfTileWidth + Theme.ClipTileMargin.Left + Theme.ClipTileMargin.Right;
        _shelfGrid.ItemHeight = Theme.ShelfTileHeight + Theme.ClipTileMargin.Top + Theme.ClipTileMargin.Bottom;
        shelfBody.Children.Add(_drop);
        shelfBody.Children.Add(scroll);
        _clearAll.SetContent(null, "Clear all");
        _clearAll.Command = _shelf.ClearAllCommand;
        _clearAll.HorizontalAlignment = HorizontalAlignment.Right;
        _clearAll.VerticalAlignment = VerticalAlignment.Center;
        Card(_shelfCard, Icons.Shelf, "Shelf", _clearAll, shelfBody);
        SetColumn(_shelfCard, 2);
        Children.Add(_shelfCard);

        var q = DispatcherQueue.GetForCurrentThread();
        _previewDelay = q.CreateTimer();
        _previewDelay.IsRepeating = false;
        _previewDelay.Interval = TimeSpan.FromMilliseconds(Motion.PreviewHoverDelayMs);
        _previewDelay.Tick += (_, _) =>
        {
            _previewDelay.Stop();
            if (_pendingPreview is not { } p || _pressing) return;
            _previewOpen = true;
            PreviewRequested?.Invoke(p.item, p.tile);
        };
        _previewGrace = q.CreateTimer();
        _previewGrace.IsRepeating = false;
        _previewGrace.Interval = TimeSpan.FromMilliseconds(Motion.PreviewCloseGraceMs);
        _previewGrace.Tick += (_, _) => { _previewGrace.Stop(); if (_pendingPreview == null) ClosePreview(); };

        foreach (var item in _clip.Items) AddClip(item, animate: false);
        foreach (var item in _shelf.Items) AddShelf(item, animate: false);
        SyncClipOrder();
        SyncShelfOrder();
        _clip.Items.CollectionChanged += OnClipItemsChanged;
        _shelf.Items.CollectionChanged += OnShelfItemsChanged;
        _clip.PropertyChanged += (_, _) => UpdateEmpty();
        _shelf.PropertyChanged += (_, _) => UpdateEmpty();
        UpdateEmpty();
    }

    /// <summary>Raised while an outgoing drag is running (the window keeps the notch open).</summary>
    public event Action<bool>? OutgoingDrag;
    /// <summary>Show / move the preview card for this tile (the window positions and animates it).</summary>
    public event Action<ClipItemViewModel, FrameworkElement>? PreviewRequested;
    /// <summary>Hide the preview card.</summary>
    public event Action? PreviewEnded;

    /// <summary>Blocks for the staggered entrance.</summary>
    public IEnumerable<(FrameworkElement element, int order)> StaggerBlocks =>
        new (FrameworkElement, int)[] { (_clipCard, 1), (_shelfCard, 2) };

    /// <summary>Called by the window while files are dragged over the notch.</summary>
    public void SetDropActive(bool active) => _drop.SetActive(active);

    /// <summary>Closes the preview right away (click, drag, page change).</summary>
    public void ClosePreview()
    {
        _previewDelay.Stop();
        _previewGrace.Stop();
        if (!_previewOpen) return;
        _previewOpen = false;
        PreviewEnded?.Invoke();
    }

    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    /// <summary>The card look: near-black panel, hairline border, global radius, header with icon + title.</summary>
    private static void Card(Border card, string icon, string title, FrameworkElement? action, FrameworkElement body)
    {
        card.Background = Theme.CardBrush;
        card.BorderBrush = Theme.CardBorderBrush;
        card.BorderThickness = new Thickness(1);
        card.CornerRadius = Theme.Radius;
        card.Padding = Theme.CardPadding;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var header = new Grid { Margin = Theme.CardHeaderMargin, Height = Theme.CardHeaderHeight };
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        label.Children.Add(new VectorIcon { Data = icon, Size = Theme.IconSmall, Foreground = Theme.SecondaryText, VerticalAlignment = VerticalAlignment.Center });
        label.Children.Add(new TextBlock { Text = title, Style = StyleOf("SectionText"), Margin = Theme.GapLeftS2 });
        header.Children.Add(label);
        if (action != null) header.Children.Add(action);
        grid.Children.Add(header);
        SetRow(body, 1);
        grid.Children.Add(body);
        card.Child = grid;
    }

    private void UpdateEmpty()
    {
        _clipEmpty.Visibility = _clip.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        _clearAll.Visibility = _shelf.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        _drop.SetEmpty(_shelf.IsEmpty);
    }

    // =========================================================================================
    // Clipboard tiles
    // =========================================================================================
    private void OnClipItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _clipGrid.Children.Clear();
            _clipTiles.Clear();
            foreach (var item in _clip.Items) AddClip(item, animate: false);
        }
        if (e.OldItems != null)
            foreach (ClipItemViewModel item in e.OldItems)
                if (!_clip.Items.Contains(item) && _clipTiles.Remove(item, out var t)) _clipGrid.Children.Remove(t.Slot);
        if (e.NewItems != null)
            foreach (ClipItemViewModel item in e.NewItems)
                if (!_clipTiles.ContainsKey(item)) AddClip(item, animate: true);
        SyncClipOrder();
        UpdateEmpty();
    }

    /// <summary>Children follow the view model's order (moves are instant, like the WPF list).</summary>
    private void SyncClipOrder()
    {
        for (int i = 0; i < _clip.Items.Count; i++)
        {
            if (!_clipTiles.TryGetValue(_clip.Items[i], out var t)) continue;
            int at = _clipGrid.Children.IndexOf(t.Slot);
            if (at != i && at >= 0) { _clipGrid.Children.RemoveAt(at); _clipGrid.Children.Insert(Math.Min(i, _clipGrid.Children.Count), t.Slot); }
        }
    }

    private void AddClip(ClipItemViewModel item, bool animate)
    {
        var tile = new ClipTile(item);
        _clipTiles[item] = tile;
        _clipGrid.Children.Add(tile.Slot);
        if (animate) tile.Slot.Loaded += (_, _) => Motions.PopIn(tile.Slot);

        tile.Tile.PointerEntered += (_, _) => OnTileEnter(tile);
        tile.Tile.PointerExited += (_, _) => OnTileLeave(tile);
        tile.Tile.PointerPressed += (_, _) => { _pressing = true; ClosePreview(); };
        tile.Tile.PointerReleased += (_, _) => _pressing = false;
        tile.Tile.PointerCaptureLost += (_, _) => _pressing = false;
        tile.Tile.Tapped += (_, e) => { item.CopyCommand.Execute(null); e.Handled = true; };
        tile.Tile.CanDrag = true;
        tile.Tile.DragStarting += async (_, args) =>
        {
            _pressing = false;
            ClosePreview();
            OutgoingDrag?.Invoke(true);
            var deferral = args.GetDeferral();
            try
            {
                var package = await ClipboardService.ToPackageAsync(item.Content);
                var view = package.GetView();
                if (view.Contains(StandardDataFormats.Bitmap)) args.Data.SetBitmap(await view.GetBitmapAsync());
                if (view.Contains(StandardDataFormats.Text)) args.Data.SetText(await view.GetTextAsync());
                if (view.Contains(StandardDataFormats.WebLink)) args.Data.SetWebLink(await view.GetWebLinkAsync());
                args.Data.RequestedOperation = DataPackageOperation.Copy;
            }
            finally { deferral.Complete(); }
        };
        tile.Tile.DropCompleted += (_, _) => OutgoingDrag?.Invoke(false);
    }

    // ---- Hover: a slight zoom and lift, then a large preview card after a short hover ------------------
    private void OnTileEnter(ClipTile tile)
    {
        Canvas.SetZIndex(tile.Slot, 100);
        tile.Hover(true);
        _previewGrace.Stop();
        _pendingPreview = (tile.Item, tile.Tile);
        if (_previewOpen)
        {
            // Already open: glide to this tile instead of closing and reopening.
            PreviewRequested?.Invoke(tile.Item, tile.Tile);
            return;
        }
        _previewDelay.Stop();
        _previewDelay.Start();
    }

    private void OnTileLeave(ClipTile tile)
    {
        Canvas.SetZIndex(tile.Slot, 50); // stay on top while settling back
        tile.Hover(false);
        _previewDelay.Stop();
        _pendingPreview = null;
        _previewGrace.Stop();
        _previewGrace.Start();
    }

    /// <summary>One clipboard tile: image / text snippet / link icon, pin mark, hover actions, copied flash.</summary>
    private sealed class ClipTile
    {
        public readonly ClipItemViewModel Item;
        public readonly Grid Slot = new();   // carries the pop in / leave motion
        public readonly Border Tile = new(); // carries the hover zoom and lift
        private readonly VectorIcon _pinMark;
        private readonly IconButton _pin, _delete;
        private readonly Border _copied;
        private CompSpring? _zoom, _lift;
        private bool _hover;

        public ClipTile(ClipItemViewModel item)
        {
            Item = item;
            Slot.Width = Slot.Height = Theme.ClipTileSize;
            Slot.Margin = Theme.ClipTileMargin;
            Tile.CornerRadius = Theme.RadiusSmall;
            Tile.Background = item.IsImage ? Theme.NotchBrush : Theme.TileBrush;
            Tile.BorderBrush = Theme.TileBorderBrush;
            Tile.BorderThickness = new Thickness(1);
            var inner = new Grid { Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, Theme.ClipTileSize - 2, Theme.ClipTileSize - 2) } };

            if (item.IsImage)
            {
                var brush = new ImageBrush { Stretch = item.ImageStretch };
                inner.Children.Add(new Border { Margin = Theme.ClipTileInsetMargin, Background = brush });
                _ = LoadAsync(brush);
            }
            if (item.IsText)
                inner.Children.Add(new TextBlock
                {
                    Text = item.Snippet, Margin = Theme.ClipSnippetMargin, TextWrapping = TextWrapping.Wrap,
                    FontFamily = Theme.Font, FontSize = Theme.ClipSnippetFontSize, LineHeight = Theme.ClipSnippetLineHeight,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight, Foreground = Theme.SecondaryText, IsHitTestVisible = false,
                });
            if (item.IsLink)
                inner.Children.Add(new VectorIcon { Data = Icons.Link, Size = Theme.IconMedium, Foreground = Theme.PrimaryText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });

            // Pinned marker (hidden while hovering, when the action overlay shows).
            _pinMark = new VectorIcon { Data = Icons.Unpin, Size = 9, Margin = new Thickness(0, 0, 7, 6), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Foreground = Theme.SecondaryText };
            inner.Children.Add(_pinMark);

            // Hover-only overlay actions.
            _pin = IconButton.Overlay(item.PinGlyph, "Pin");
            _pin.HorizontalAlignment = HorizontalAlignment.Left;
            _pin.VerticalAlignment = VerticalAlignment.Top;
            _pin.Margin = Theme.PadS1;
            _pin.Command = item.PinCommand;
            _delete = IconButton.Overlay(Icons.Close, "Delete");
            _delete.HorizontalAlignment = HorizontalAlignment.Right;
            _delete.VerticalAlignment = VerticalAlignment.Top;
            _delete.Margin = Theme.PadS1;
            _delete.Command = item.DeleteCommand;
            inner.Children.Add(_pin);
            inner.Children.Add(_delete);

            // "Copied" flash: a green outline, no text.
            _copied = new Border { CornerRadius = Theme.RadiusSmall, BorderThickness = new Thickness(2), BorderBrush = Theme.SuccessBrush, IsHitTestVisible = false };
            inner.Children.Add(_copied);

            Tile.Child = inner;
            Slot.Children.Add(Tile);
            ToolTipService.SetToolTip(Tile, null);
            Update();
            item.PropertyChanged += OnItemChanged;
            Tile.Loaded += (_, _) =>
            {
                if (_zoom != null) return;
                var v = Anim.VisualOf(Tile, translation: true);
                var c = v.Compositor;
                var p = c.CreatePropertySet();
                _zoom = new CompSpring(p, "z", Motion.TileHover, 1);
                _lift = new CompSpring(p, "l", Motion.Hover, 0, precision: 50);
                Motions.CentreOrigin(Tile);
                v.StartAnimation("Scale", Anim.Expr(c, "Vector3(p.z, p.z, 1)", ("p", p)));
                v.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, -p.l, 0)", ("p", p)));
            };
        }

        private async Task LoadAsync(ImageBrush brush) => brush.ImageSource = await ImageLoader.ToSourceAsync(Item.Image);

        public void Hover(bool on)
        {
            _hover = on;
            _zoom?.SetTarget(on ? Theme.ClipTileHoverScale : 1, Motion.TileHover);
            _lift?.SetTarget(on ? Motion.TileLift : 0, Motion.Hover);
            Update();
        }

        private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ClipItemViewModel.IsLeaving) && Item.IsLeaving) { Motions.Leave(Slot); return; }
            if (e.PropertyName == nameof(ClipItemViewModel.PinGlyph)) _pin.Icon = Item.PinGlyph;
            Update();
        }

        private void Update()
        {
            _pin.Visibility = _delete.Visibility = _hover ? Visibility.Visible : Visibility.Collapsed;
            _pinMark.Visibility = Item.IsPinned && !_hover ? Visibility.Visible : Visibility.Collapsed;
            _copied.Visibility = Item.JustCopied ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // =========================================================================================
    // Shelf tiles
    // =========================================================================================
    private void OnShelfItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _shelfGrid.Children.Clear();
            _shelfTiles.Clear();
            foreach (var item in _shelf.Items) AddShelf(item, animate: false);
        }
        if (e.OldItems != null)
            foreach (ShelfItemViewModel item in e.OldItems)
                if (_shelfTiles.Remove(item, out var t)) _shelfGrid.Children.Remove(t);
        if (e.NewItems != null)
            foreach (ShelfItemViewModel item in e.NewItems)
                if (!_shelfTiles.ContainsKey(item)) AddShelf(item, animate: true);
        SyncShelfOrder();
        UpdateEmpty();
    }

    private void SyncShelfOrder()
    {
        for (int i = 0; i < _shelf.Items.Count; i++)
        {
            if (!_shelfTiles.TryGetValue(_shelf.Items[i], out var t)) continue;
            int at = _shelfGrid.Children.IndexOf(t);
            if (at != i && at >= 0) { _shelfGrid.Children.RemoveAt(at); _shelfGrid.Children.Insert(Math.Min(i, _shelfGrid.Children.Count), t); }
        }
    }

    private void AddShelf(ShelfItemViewModel item, bool animate)
    {
        var tile = new Grid
        {
            Width = Theme.ShelfTileWidth, Height = Theme.ShelfTileHeight, Margin = Theme.ClipTileMargin,
            Background = Theme.TransparentBrush, CanDrag = true,
        };
        ToolTipService.SetToolTip(tile, item.Path);
        var fill = new Border { CornerRadius = Theme.Radius, Background = Theme.CardHoverBrush };
        var body = new StackPanel { Margin = Theme.PadS1, VerticalAlignment = VerticalAlignment.Center };
        var iconBrush = new ImageBrush { Stretch = Stretch.Uniform };
        body.Children.Add(new Border { Width = Theme.ShelfIconSize, Height = Theme.ShelfIconSize, Background = iconBrush });
        body.Children.Add(new TextBlock
        {
            Text = item.Name, Style = StyleOf("BaseText"), FontSize = Theme.ShelfNameFontSize, Foreground = Theme.SecondaryText,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 26, Margin = Theme.GapTopS1,
        });
        var remove = IconButton.Overlay(Icons.Close, "Remove from shelf");
        remove.HorizontalAlignment = HorizontalAlignment.Right;
        remove.VerticalAlignment = VerticalAlignment.Top;
        remove.Margin = Theme.PadS1;
        remove.Command = item.RemoveCommand;
        remove.Visibility = Visibility.Collapsed;
        tile.Children.Add(fill);
        tile.Children.Add(body);
        tile.Children.Add(remove);
        Motions.HoverFade(tile, fill);
        Motions.PressScale(body);
        tile.PointerEntered += (_, _) => remove.Visibility = Visibility.Visible;
        tile.PointerExited += (_, _) => remove.Visibility = Visibility.Collapsed;
        tile.Tapped += (_, e) => { item.OpenCommand.Execute(null); e.Handled = true; };
        tile.DragStarting += async (_, args) =>
        {
            item.Refresh();
            if (!item.Exists) { args.Cancel = true; return; }
            OutgoingDrag?.Invoke(true);
            var deferral = args.GetDeferral();
            try
            {
                IStorageItem storage = item.IsDirectory
                    ? await StorageFolder.GetFolderFromPathAsync(item.Path)
                    : await StorageFile.GetFileFromPathAsync(item.Path);
                args.Data.SetStorageItems(new[] { storage });
                args.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Link | DataPackageOperation.Move;
            }
            catch { args.Cancel = true; OutgoingDrag?.Invoke(false); }
            finally { deferral.Complete(); }
        };
        tile.DropCompleted += (_, _) => OutgoingDrag?.Invoke(false);

        async void Refresh()
        {
            body.Opacity = item.Exists ? 1 : 0.35;
            iconBrush.ImageSource = await ImageLoader.ToSourceAsync(item.Icon);
        }
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShelfItemViewModel.IsLeaving) && item.IsLeaving) Motions.Leave(tile);
            else Refresh();
        };
        Refresh();
        _shelfTiles[item] = tile;
        _shelfGrid.Children.Add(tile);
        if (animate) tile.Loaded += (_, _) => Motions.PopIn(tile);
    }

    // =========================================================================================
    // Drop zone
    // =========================================================================================
    /// <summary>
    /// Dashed drop zone: always shown when the shelf is empty, and over the tiles while a file is dragged
    /// in. While something hovers it brightens, thickens, scales up a touch and the dashes march
    /// (a looping compositor animation on the dash offset, only while active).
    /// </summary>
    private sealed class DropZone : Grid
    {
        private readonly Border _fill = new(), _outlineHost = new();
        private readonly Grid _icons = new();
        private CompositionPropertySet? _p;
        private CompSpring? _v;
        private CompositionSpriteShape? _outline;
        private CompositionRoundedRectangleGeometry? _rect;
        private ShapeVisual? _shapes;
        private bool _active, _empty = true;

        public DropZone()
        {
            IsHitTestVisible = false;
            _fill.CornerRadius = Theme.Radius;
            _fill.Background = Theme.CardBrush;
            Children.Add(_fill);
            Children.Add(_outlineHost);
            var hint = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            _icons.Width = _icons.Height = Theme.IconMedium;
            _icons.HorizontalAlignment = HorizontalAlignment.Center;
            _icons.Children.Add(new VectorIcon { Data = Icons.Upload, Size = Theme.IconMedium, Foreground = Theme.SecondaryText });
            _icons.Children.Add(new VectorIcon { Data = Icons.Upload, Size = Theme.IconMedium, Foreground = Theme.PrimaryText });
            hint.Children.Add(_icons);
            hint.Children.Add(new TextBlock { Text = "Drop files here", Style = StyleOf("CaptionText"), Margin = Theme.GapTopS2, HorizontalAlignment = HorizontalAlignment.Center });
            Children.Add(hint);
            Loaded += (_, _) => Build();
            SizeChanged += (_, e) => Resize(e.NewSize);
        }

        private void Build()
        {
            if (_p != null) return;
            var c = ElementCompositionPreview.GetElementVisual(this).Compositor;
            _p = c.CreatePropertySet();
            _p.InsertScalar("Empty", _empty ? 1 : 0);
            _v = new CompSpring(_p, "v", Motion.Hover, 0);

            _shapes = c.CreateShapeVisual();
            _rect = c.CreateRoundedRectangleGeometry();
            _rect.CornerRadius = new Vector2((float)Theme.CornerRadius);
            _outline = c.CreateSpriteShape(_rect);
            _outline.StrokeBrush = c.CreateColorBrush(Theme.DropZoneColor);
            _outline.StrokeDashArray.Add((float)Theme.DropZoneDash);
            _outline.StrokeDashArray.Add((float)Theme.DropZoneDash);
            _outline.StrokeDashCap = CompositionStrokeCap.Round;
            _outline.StrokeStartCap = _outline.StrokeEndCap = CompositionStrokeCap.Round;
            _outline.StartAnimation("StrokeThickness", Expr($"{Anim.F(Theme.DropZoneThickness)} * (1 + 0.3 * Clamp(p.v, 0, 1.1))"));
            _shapes.Shapes.Add(_outline);
            ElementCompositionPreview.SetElementChildVisual(_outlineHost, _shapes);
            _shapes.StartAnimation("Opacity", Expr("0.7 + 0.3 * Clamp(p.v, 0, 1.1)"));

            var self = Anim.VisualOf(this);
            Motions.CentreOrigin(this);
            self.StartAnimation("Opacity", Expr("Max(p.Empty, Clamp(p.v, 0, 1))"));
            self.StartAnimation("Scale", Expr("Vector3(1 + 0.02 * Clamp(p.v, 0, 1.1), 1 + 0.02 * Clamp(p.v, 0, 1.1), 1)"));
            // Cover the tiles while dropping onto a full shelf.
            Anim.VisualOf(_fill).StartAnimation("Opacity", Expr("Clamp(p.v, 0, 1) * (1 - p.Empty) * 0.92"));
            Anim.VisualOf(_icons, translation: true).StartAnimation("Translation", Expr("Vector3(0, -3 * Clamp(p.v, 0, 1.1), 0)"));
            Anim.VisualOf(_icons.Children[1]).StartAnimation("Opacity", Expr("Clamp((p.v - 0.4) * 5, 0, 1)"));
            Resize(new Windows.Foundation.Size(ActualWidth, ActualHeight));

            ExpressionAnimation Expr(string e)
            {
                var x = c.CreateExpressionAnimation(e);
                x.SetReferenceParameter("p", _p);
                return x;
            }
        }

        private void Resize(Windows.Foundation.Size size)
        {
            if (_rect == null || _shapes == null) return;
            float inset = (float)Theme.DropZoneThickness;
            _shapes.Size = new Vector2((float)size.Width, (float)size.Height);
            _rect.Offset = new Vector2(inset);
            _rect.Size = new Vector2(Math.Max(0, (float)size.Width - 2 * inset), Math.Max(0, (float)size.Height - 2 * inset));
        }

        public void SetEmpty(bool empty)
        {
            _empty = empty;
            _p?.InsertScalar("Empty", empty ? 1 : 0);
        }

        public void SetActive(bool active)
        {
            if (active == _active || _v == null || _outline == null) return;
            _active = active;
            _v.SetTarget(active ? 1 : 0, Motion.Hover);
            if (active && Anim.Enabled)
            {
                // Marching ants: one dash period, looped. Dash units are stroke widths, like the WPF outline.
                var c = _outline.Compositor;
                var a = c.CreateScalarKeyFrameAnimation();
                double period = 2 * Theme.DropZoneDash;
                a.InsertKeyFrame(0, 0);
                a.InsertKeyFrame(1, (float)-period, c.CreateLinearEasingFunction());
                a.Duration = TimeSpan.FromSeconds(period / (Motion.DropZoneDashSpeed / Theme.DropZoneThickness));
                a.IterationBehavior = AnimationIterationBehavior.Forever;
                _outline.StartAnimation("StrokeDashOffset", a);
            }
            else _outline.StopAnimation("StrokeDashOffset");
        }
    }
}
