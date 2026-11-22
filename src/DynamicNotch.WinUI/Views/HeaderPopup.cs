using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace DynamicNotch.Views;

/// <summary>
/// The pop-up behind the open notch's header icons. One card, right-aligned under the icons over a dimmed
/// backdrop, that holds whichever features are header icons (Settings ▸ Layout): narrow ones (Notifications,
/// Quick actions) take a side card, page-sized ones (Media Player, Clipboard, Focus…) take the full width.
/// It slides in from the right with a fade; clicking another icon cross-fades the panels inside the same card
/// (the incoming one slides in from the right) instead of closing and reopening. Closes on the backdrop, the
/// same icon again, Esc (the window asks), or the notch closing. Kept laid out while closed (transparent and
/// click-through), so opening only retargets compositor springs.
/// </summary>
public sealed class HeaderPopup : Grid
{
    private sealed class Panel
    {
        public required FrameworkElement View;
        public required CompSpring Show;
        public required double Width;
    }

    private readonly Border _dim = new(), _shadowHost = new();
    private readonly Grid _card = new();
    private readonly CompSpring _open;
    private readonly CompositionPropertySet _set;
    private readonly Compositor _c;
    private readonly Dictionary<PageId, Panel> _panels = new();
    private SpriteVisual? _caster;
    private int _keys;

    public HeaderPopup()
    {
        IsHitTestVisible = false;
        _dim.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
        _dim.CornerRadius = Theme.RadiusSmall;
        _dim.Tapped += (_, e) => { e.Handled = true; Close(); };
        Children.Add(_dim);
        _shadowHost.HorizontalAlignment = HorizontalAlignment.Right;
        _shadowHost.Width = Theme.HeaderPopupWidth;
        _shadowHost.IsHitTestVisible = false;
        Children.Add(_shadowHost);

        _card.Width = Theme.HeaderPopupWidth;
        _card.HorizontalAlignment = HorizontalAlignment.Right;
        _card.Background = Theme.NotchBrush;
        _card.BorderBrush = Theme.HighlightBorderBrush;
        _card.BorderThickness = new Thickness(1);
        _card.CornerRadius = Theme.RadiusSmall;
        _card.Padding = Theme.HeaderPopupPadding;
        Children.Add(_card);

        _c = Anim.VisualOf(_card).Compositor;
        _set = _c.CreatePropertySet();
        _open = new CompSpring(_set, "o", Motion.PopoverOpen, 0);

        // Slides in from the right edge with a fade (and slides back out the same way). Clamped, so a spring
        // overshoot never pushes it past its spot.
        var cv = Anim.VisualOf(_card, translation: true);
        cv.StartAnimation("Translation", Anim.Expr(_c, $"Vector3((1 - Clamp(p.o, 0, 1)) * {Anim.F(Motion.HeaderPopupSlide)}, 0, 0)", ("p", _set)));
        cv.StartAnimation("Opacity", Anim.Expr(_c, "Clamp(p.o * 1.4, 0, 1)", ("p", _set)));
        Anim.VisualOf(_dim).StartAnimation("Opacity", Anim.Expr(_c, $"Clamp(p.o, 0, 1) * {Anim.F(Theme.HeaderPopupDimOpacity)}", ("p", _set)));
        _card.SizeChanged += (_, e) =>
        {
            _shadowHost.Width = e.NewSize.Width;
            if (_caster != null) SoftShadow.Layout(_caster, new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height));
        };
    }

    /// <summary>The panel showing now (null while closed).</summary>
    public PageId? Current { get; private set; }
    public bool IsOpen => Current != null;

    /// <summary>Raised after it opened, switched or closed (header icons show their state; the notch resizes).</summary>
    public event Action<PageId?>? Changed;
    /// <summary>A panel came into view (the owner refreshes it: ages, live values, unread).</summary>
    public event Action<PageId>? Shown;
    /// <summary>Asked before closing: true when something inside (a Wi-Fi popover) closed instead.</summary>
    public Func<PageId, bool>? CloseInner { get; set; }

    public bool Has(PageId id) => _panels.ContainsKey(id);

    /// <summary>Adopts <paramref name="view"/> (already detached from any parent) as the panel for <paramref name="id"/>.</summary>
    public void SetPanel(PageId id, FrameworkElement view, double width)
    {
        if (_panels.TryGetValue(id, out var existing))
        {
            if (ReferenceEquals(existing.View, view)) { existing.Width = width; return; }
            RemovePanel(id);
        }
        string key = "k" + _keys++;
        var show = new CompSpring(_set, key, Motion.HeaderPanelSwap, Current == id ? 1 : 0);
        view.IsHitTestVisible = Current == id;
        _card.Children.Add(view);
        var v = Anim.VisualOf(view, translation: true);
        v.StartAnimation("Opacity", Anim.Expr(_c, $"Clamp(p.{key}, 0, 1)", ("p", _set)));
        v.StartAnimation("Translation", Anim.Expr(_c, $"Vector3((1 - Clamp(p.{key}, 0, 1)) * {Anim.F(Motion.HeaderPanelSlide)}, 0, 0)", ("p", _set)));
        _panels[id] = new Panel { View = view, Show = show, Width = width };
    }

    /// <summary>Gives a panel's view back (detached, its pop-up motion cleared), e.g. it became a page.</summary>
    public FrameworkElement? RemovePanel(PageId id)
    {
        if (!_panels.Remove(id, out var p)) return null;
        if (Current == id) Close(force: true);
        _card.Children.Remove(p.View);
        var v = Anim.VisualOf(p.View, translation: true);
        v.StopAnimation("Opacity");
        v.StopAnimation("Translation");
        v.Opacity = 1;
        v.Properties.InsertVector3("Translation", Vector3.Zero);
        p.View.IsHitTestVisible = true;
        return p.View;
    }

    /// <summary>Opens <paramref name="id"/>; switches when another one is open; closes when it's the one showing.</summary>
    public void Toggle(PageId id, FrameworkElement? from)
    {
        if (Current == id) { Close(); return; }
        Open(id);
    }

    /// <summary>Opens (or switches to) <paramref name="id"/>; does nothing when it's already showing.</summary>
    public void Open(PageId id)
    {
        if (Current == id || !_panels.TryGetValue(id, out var panel)) return;
        bool wasOpen = IsOpen;
        Current = id;
        _card.Width = panel.Width;
        foreach (var (pid, p) in _panels)
        {
            bool on = pid == id;
            p.View.IsHitTestVisible = on;
            if (!wasOpen) p.Show.Snap(on ? 1 : 0); // opening: the card's own spring carries it in
            else p.Show.SetTarget(on ? 1 : 0, Motion.HeaderPanelSwap);
        }
        if (!wasOpen)
        {
            IsHitTestVisible = true;
            EnsureShadow();
            _open.SetTarget(1, Motion.HeaderPopupOpen);
        }
        Shown?.Invoke(id);
        Changed?.Invoke(id);
    }

    /// <summary>Closes it (an open Wi-Fi / Bluetooth popover first, unless <paramref name="force"/>). True when something was open.</summary>
    public bool Close(bool force = false)
    {
        if (Current is not { } id) return false;
        if (!force && CloseInner?.Invoke(id) == true) return true;
        Current = null;
        IsHitTestVisible = false;
        foreach (var p in _panels.Values) p.View.IsHitTestVisible = false;
        _open.SetTarget(0, Motion.HeaderPopupClose);
        Changed?.Invoke(null);
        return true;
    }

    /// <summary>Closes at once, inner popovers included (the notch is collapsing).</summary>
    public void CloseAll()
    {
        if (Current is { } id) CloseInner?.Invoke(id);
        Close(force: true);
    }

    private void EnsureShadow()
    {
        if (_caster != null) return;
        _caster = SoftShadow.Create(_c, Theme.CornerRadiusSmall, Theme.PopoverShadowBlur, 1, Theme.PopoverShadowOffsetY);
        _caster.StartAnimation("Opacity", Anim.Expr(_c, $"Clamp(p.o, 0, 1) * {Anim.F(Theme.PopoverShadowOpacity)}", ("p", _set)));
        ElementCompositionPreview.SetElementChildVisual(_shadowHost, _caster);
        // Slides with the card: the same spring expression (a visual's Translation can't be read from another).
        Anim.VisualOf(_shadowHost, translation: true).StartAnimation("Translation",
            Anim.Expr(_c, $"Vector3((1 - Clamp(p.o, 0, 1)) * {Anim.F(Motion.HeaderPopupSlide)}, 0, 0)", ("p", _set)));
        if (_card.ActualWidth > 0) SoftShadow.Layout(_caster, new Vector2((float)_card.ActualWidth, (float)_card.ActualHeight));
    }
}
