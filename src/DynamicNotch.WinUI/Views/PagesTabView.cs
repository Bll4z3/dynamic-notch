using System.ComponentModel;
using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.UI;
using VirtualKey = Windows.System.VirtualKey;

namespace DynamicNotch.Views;

/// <summary>
/// "Pages" tab: one card row per notch page, in the user's order. Each row has a six-dot grip on the
/// left to drag it to a new place (the row lifts to 1.03 with a soft shadow, the others spring out of
/// the way, the drop settles with a spring), and a pill switch on the right (at least one page stays
/// on; switched-off pages are dimmed and can still be moved). Keyboard: focus a row (Tab) and press
/// Alt+↑ / Alt+↓. The order is saved at once and the notch follows live.
/// </summary>
internal sealed class PagesTabView : Grid
{
    private readonly PagesTabViewModel _vm;
    private readonly StackPanel _list = new();
    private readonly List<PageRowView> _views = new();
    private PageRowView? _drag;
    private int _dragFrom, _dragTo;
    private double _dragStartY, _slot;

    public PagesTabView(PagesTabViewModel vm)
    {
        _vm = vm;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new StackPanel();
        top.Children.Add(new TextBlock { Text = "Pages", Style = AskPage.TextStyle("TitleText") });
        top.Children.Add(new TextBlock
        {
            Text = "Choose which pages the notch shows, and drag them into the order you like.",
            Style = AskPage.TextStyle("SecondaryText"), TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None, Margin = Theme.GapTopS1,
        });
        _list.Margin = new Thickness(0, 0, Theme.S3, 0);
        foreach (var row in vm.Rows)
        {
            var view = new PageRowView(row);
            view.GripPressed += OnGripPressed;
            view.GripMoved += OnGripMoved;
            view.GripReleased += OnGripReleased;
            view.MoveRequested += delta => MoveByKeyboard(view, delta);
            _views.Add(view);
            _list.Children.Add(view);
        }
        var scroll = new ScrollViewer { Content = _list, Margin = Theme.GapTopS3, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 5 * (Theme.PageRowHeight + Theme.S2) };
        top.Children.Add(scroll);
        var message = new TextBlock { FontFamily = Theme.FontSemiBold, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = Theme.FontBody, Foreground = Theme.DangerBrush, Margin = Theme.GapTopS1 };
        top.Children.Add(message);
        Children.Add(top);
        var footer = new TextBlock { Text = "Changes apply instantly. Tip: focus a row and press Alt+↑ / Alt+↓ to move it.", Style = AskPage.TextStyle("CaptionText") };
        SetRow(footer, 1);
        Children.Add(footer);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PagesTabViewModel.Message)) message.Text = vm.Message; };
    }

    // ---- Drag to reorder ---------------------------------------------------------------------------------
    private void OnGripPressed(PageRowView view, double y)
    {
        _drag = view;
        _dragFrom = _dragTo = _list.Children.IndexOf(view);
        _dragStartY = y;
        _slot = view.ActualHeight + Theme.PageRowMargin.Bottom;
        Canvas.SetZIndex(view, 10);
        view.Lift(true);
    }

    private void OnGripMoved(PageRowView view, double y)
    {
        if (_drag != view) return;
        double dy = y - _dragStartY;
        int n = _list.Children.Count;
        // Keep the row within the list's bounds.
        dy = Math.Clamp(dy, -_dragFrom * _slot - 8, (n - 1 - _dragFrom) * _slot + 8);
        view.FollowPointer(dy);
        int to = Math.Clamp(_dragFrom + (int)Math.Round(dy / _slot), 0, n - 1);
        if (to == _dragTo) return;
        _dragTo = to;
        for (int i = 0; i < n; i++)
        {
            if (i == _dragFrom || _list.Children[i] is not PageRowView other) continue;
            double shift = i > _dragFrom && i <= to ? -_slot : i < _dragFrom && i >= to ? _slot : 0;
            other.SpringTo(shift); // the others make room
        }
    }

    private void OnGripReleased(PageRowView view)
    {
        if (_drag != view) return;
        _drag = null;
        Canvas.SetZIndex(view, 0);
        view.Lift(false);
        Commit(view, _dragFrom, _dragTo);
    }

    private void MoveByKeyboard(PageRowView view, int delta)
    {
        int from = _list.Children.IndexOf(view);
        int to = Math.Clamp(from + delta, 0, _list.Children.Count - 1);
        if (from == to) return;
        _slot = view.ActualHeight + Theme.PageRowMargin.Bottom;
        Commit(view, from, to);
        view.Focus(FocusState.Keyboard);
    }

    /// <summary>
    /// Saves the new order and moves the element in the list. FLIP: every row keeps its on-screen spot for
    /// a frame (offset = old position − new position) and springs to its new slot from there.
    /// </summary>
    private void Commit(PageRowView view, int from, int to)
    {
        var before = _list.Children.OfType<PageRowView>().Select(v => (v, pos: _list.Children.IndexOf(v) * _slot + v.Offset)).ToList();
        if (from != to)
        {
            _list.Children.Move((uint)from, (uint)to);
            _vm.Move(view.Row, to);
        }
        foreach (var (v, pos) in before)
        {
            double natural = _list.Children.IndexOf(v) * _slot;
            v.SettleFrom(pos - natural);
        }
    }

    // =======================================================================================================
    /// <summary>One page row: grip · icon tile · name + description · switch. Focusable for Alt+↑ / Alt+↓.</summary>
    private sealed class PageRowView : UserControl
    {
        private readonly Border _card = new(), _ring = new(), _shadowHost = new();
        private readonly Grid _content = new();
        private readonly Border _grip = new();
        private readonly CompSpring _y, _lift;
        private bool _gripDown;

        public PageRowView(PageRowViewModel row)
        {
            Row = row;
            IsTabStop = true;
            UseSystemFocusVisuals = false;
            Margin = Theme.PageRowMargin;
            Tag = SettingsWindow.NoDragTag;
            var root = new Grid();

            root.Children.Add(_shadowHost);
            _card.Background = Theme.CardBrush;
            _card.BorderBrush = Theme.CardEdgeBrush;
            _card.BorderThickness = new Thickness(1);
            _card.CornerRadius = Theme.Radius;
            _card.Padding = new Thickness(Theme.S1, Theme.S2, Theme.S3, Theme.S2);
            _card.MinHeight = Theme.PageRowHeight;
            root.Children.Add(_card);
            _ring.CornerRadius = new CornerRadius(Theme.CornerRadius + 2);
            _ring.Margin = new Thickness(-2);
            _ring.BorderBrush = Theme.AccentBrush;
            _ring.BorderThickness = new Thickness(1.5);
            _ring.Visibility = Visibility.Collapsed;
            _ring.IsHitTestVisible = false;
            root.Children.Add(_ring);

            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Grip: six dots, a bigger hit area, the move cursor.
            _grip.Width = Theme.GripHitWidth;
            _grip.Background = Theme.TransparentBrush;
            _grip.VerticalAlignment = VerticalAlignment.Stretch;
            _grip.Child = new VectorIcon { Data = Icons.Grip, Size = Theme.IconSmall, Foreground = Theme.TertiaryText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(_grip, "Drag to reorder");
            _grip.PointerEntered += (_, _) => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
            _grip.PointerExited += (_, _) => { if (!_gripDown) ProtectedCursor = null; };
            _grip.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(_grip).Properties.IsLeftButtonPressed) return;
                _gripDown = _grip.CapturePointer(e.Pointer);
                GripPressed?.Invoke(this, ParentY(e));
                e.Handled = true;
            };
            _grip.PointerMoved += (_, e) => { if (_gripDown) { GripMoved?.Invoke(this, ParentY(e)); e.Handled = true; } };
            _grip.PointerReleased += (_, e) => EndGrip(e.Pointer);
            _grip.PointerCaptureLost += (_, _) => EndGrip(null);
            _content.Children.Add(_grip);

            var tile = new Border
            {
                Width = Theme.PageIconTile, Height = Theme.PageIconTile, VerticalAlignment = VerticalAlignment.Center,
                CornerRadius = Theme.Radius, Background = Theme.TileBrush, BorderBrush = Theme.TileBorderBrush, BorderThickness = new Thickness(1),
                Child = row.UseClaudeLogo
                    ? new ClaudeLogo(Theme.IconMedium, Theme.PrimaryText) { HorizontalAlignment = HorizontalAlignment.Center }
                    : new VectorIcon { Data = row.Icon, Size = Theme.IconMedium, Foreground = Theme.PrimaryText, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            Grid.SetColumn(tile, 1);
            _content.Children.Add(tile);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS3 };
            text.Children.Add(new TextBlock { Text = row.Name, Style = AskPage.TextStyle("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = row.Description, Style = AskPage.TextStyle("CaptionText"), Foreground = Theme.SecondaryText, Margin = Theme.GapTopS1 });
            Grid.SetColumn(text, 2);
            _content.Children.Add(text);
            var sw = new PillSwitch { VerticalAlignment = VerticalAlignment.Center, Margin = Theme.GapLeftS3, IsOn = row.IsEnabled, IsTabStop = false };
            ToolTipService.SetToolTip(sw, row.Name);
            sw.Toggled += on => row.IsEnabled = on;
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PageRowViewModel.IsEnabled)) { sw.IsOn = row.IsEnabled; Dim(); }
                if (e.PropertyName == nameof(PageRowViewModel.ShakeToken)) sw.Shake();
            };
            Grid.SetColumn(sw, 3);
            _content.Children.Add(sw);
            _card.Child = _content;
            Content = root;
            Dim();

            // Motion: vertical offset (reflow / drag), lift (scale + shadow).
            var v = Anim.VisualOf(this, translation: true);
            var c = v.Compositor;
            var p = c.CreatePropertySet();
            _y = new CompSpring(p, "y", Motion.Reflow, 0, precision: 50);
            _lift = new CompSpring(p, "l", Motion.Pop, 0);
            v.StartAnimation("Translation", Anim.Expr(c, "Vector3(0, p.y, 0)", ("p", p)));
            var scale = Anim.Expr(c, "Vector3(1 + (L - 1) * p.l, 1 + (L - 1) * p.l, 1)", ("p", p));
            scale.SetScalarParameter("L", (float)Motion.RowLiftScale);
            v.StartAnimation("Scale", scale);
            SizeChanged += (_, e) => { v.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0); SizeShadow(e.NewSize); };
            BuildShadow(c, p);

            GotFocus += (_, _) => _ring.Visibility = FocusState == FocusState.Keyboard ? Visibility.Visible : Visibility.Collapsed;
            LostFocus += (_, _) => _ring.Visibility = Visibility.Collapsed;
            KeyDown += (_, e) =>
            {
                bool alt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                if (!alt || e.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
                MoveRequested?.Invoke(e.Key == VirtualKey.Up ? -1 : 1);
                e.Handled = true;
            };
        }

        public PageRowViewModel Row { get; }
        public double Offset => _y.Value;
        public event Action<PageRowView, double>? GripPressed, GripMoved;
        public event Action<PageRowView>? GripReleased;
        public event Action<int>? MoveRequested;

        private double ParentY(PointerRoutedEventArgs e) => e.GetCurrentPoint(Parent as UIElement).Position.Y;

        private void EndGrip(Pointer? pointer)
        {
            if (!_gripDown) return;
            _gripDown = false;
            if (pointer != null) _grip.ReleasePointerCapture(pointer);
            ProtectedCursor = null;
            GripReleased?.Invoke(this);
        }

        /// <summary>Switched-off pages are dimmed (but still movable and switchable).</summary>
        private void Dim()
        {
            foreach (var child in _content.Children)
                if (child is FrameworkElement fe && child is not PillSwitch) fe.Opacity = Row.IsEnabled ? 1 : Theme.DisabledRowOpacity;
        }

        public void Lift(bool on) => _lift.SetTarget(on ? 1 : 0, Motion.Pop);
        public void FollowPointer(double dy) => _y.Snap(dy);
        public void SpringTo(double y) => _y.SetTarget(y, Motion.Reflow);

        /// <summary>After the list order changed: start from <paramref name="offset"/> (the old spot) and settle at 0.</summary>
        public void SettleFrom(double offset)
        {
            _y.Snap(offset);
            _y.SetTarget(0, Motion.Reflow);
        }

        // Soft shadow under the lifted row (masked by the card's own shape, opacity follows the lift).
        private SpriteVisual? _caster;
        private CompositionVisualSurface? _surface;

        private void BuildShadow(Compositor c, CompositionPropertySet p)
        {
            _surface = c.CreateVisualSurface();
            _surface.SourceVisual = ElementCompositionPreview.GetElementVisual(_card);
            var shadow = c.CreateDropShadow();
            shadow.BlurRadius = (float)Theme.RowLiftShadowBlur;
            shadow.Offset = new Vector3(0, (float)Theme.RowLiftShadowOffsetY, 0);
            shadow.Color = Color.FromArgb(255, 0, 0, 0);
            shadow.Mask = c.CreateSurfaceBrush(_surface);
            shadow.StartAnimation("Opacity", Anim.Expr(c, $"Clamp(p.l, 0, 1) * {Anim.F(Theme.RowLiftShadowOpacity)}", ("p", p)));
            _caster = c.CreateSpriteVisual();
            _caster.Shadow = shadow;
            ElementCompositionPreview.SetElementChildVisual(_shadowHost, _caster);
        }

        private void SizeShadow(Windows.Foundation.Size size)
        {
            if (_caster != null) _caster.Size = new Vector2((float)_card.ActualWidth, (float)_card.ActualHeight);
            if (_surface != null) _surface.SourceSize = new Vector2((float)_card.ActualWidth, (float)_card.ActualHeight);
        }
    }
}
