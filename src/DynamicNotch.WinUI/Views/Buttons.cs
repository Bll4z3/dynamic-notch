using System.Windows.Input;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DynamicNotch.Views;

/// <summary>
/// Icon button (the WPF IconButton style): vector icon, round hover fill that springs in, press squash,
/// 35% opacity when disabled. Never takes focus. Plain Grid, so no WinUI button chrome or focus visuals.
/// </summary>
public sealed class IconButton : Grid
{
    private readonly Border _back = new(), _fill = new();
    private readonly VectorIcon _icon = new();
    private bool _enabled = true, _pressed;

    public IconButton(double size = Theme.TransportButtonSize, double iconSize = Theme.IconMedium)
    {
        Width = Height = size;
        Background = Theme.TransparentBrush;
        _fill.Background = Theme.CardHoverBrush;
        _fill.CornerRadius = Theme.Radius; // clamps to a circle on these sizes
        _icon.Size = iconSize;
        _icon.Foreground = Theme.PrimaryText;
        _icon.HorizontalAlignment = HorizontalAlignment.Center;
        _icon.VerticalAlignment = VerticalAlignment.Center;
        _back.CornerRadius = Theme.Radius;
        Children.Add(_back);
        Children.Add(_fill);
        Children.Add(_icon);

        Motions.HoverFade(this, _fill, () => _enabled);
        Motions.PressScale(this, () => _enabled);
        PointerPressed += (_, e) => { if (!_enabled) return; _pressed = true; CapturePointer(e.Pointer); e.Handled = true; };
        PointerReleased += (_, e) =>
        {
            if (!_pressed) return;
            _pressed = false;
            ReleasePointerCapture(e.Pointer);
            var p = e.GetCurrentPoint(this).Position;
            if (p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight) Invoke();
            e.Handled = true;
        };
        PointerCaptureLost += (_, _) => _pressed = false;
        Tapped += (_, e) => e.Handled = true; // a tap on the button must not also reach its container (e.g. copy a tile)
    }

    /// <summary>The overlay-button look: a solid backing and the small radius (clipboard tiles).</summary>
    public static IconButton Overlay(string icon, string tooltip) => new(Theme.OverlayButtonSize, Theme.OverlayIconSize)
    {
        Icon = icon, Tooltip = tooltip, Backing = Theme.OverlayButtonBrush, Radius = Theme.RadiusSmall,
    };

    public Brush? Backing { get => _back.Background; set => _back.Background = value; }
    public CornerRadius Radius { get => _fill.CornerRadius; set => _fill.CornerRadius = _back.CornerRadius = value; }
    public string? Icon { get => _icon.Data; set => _icon.Data = value; }
    public double IconSize { get => _icon.Size; set => _icon.Size = value; }
    public Brush? Foreground { get => _icon.Foreground; set => _icon.Foreground = value; }
    public ICommand? Command { get; set; }
    public event Action? Click;

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; Opacity = value ? 1 : 0.35; }
    }

    public string? Tooltip { set => ToolTipService.SetToolTip(this, value); }

    private void Invoke()
    {
        Click?.Invoke();
        if (Command?.CanExecute(null) == true) Command.Execute(null);
    }
}

/// <summary>
/// Pill button (the WPF PillButton / QuietPillButton styles): radius = half the height, icon + label,
/// a lighten overlay that springs in on hover, press squash. The quiet variant is dark with a hairline
/// border. <see cref="Active"/> = false dims it to 40% (Send with nothing to send); the command decides
/// whether a click does anything. Never takes focus.
/// </summary>
public sealed class PillButton : Grid
{
    private readonly Border _back = new(), _lighten = new();
    private readonly VectorIcon _icon = new();
    private readonly TextBlock _label = new();

    public PillButton(string? icon, string label, bool quiet = false)
    {
        Height = Theme.PillButtonHeight;
        Background = Theme.TransparentBrush;
        var radius = new CornerRadius(Theme.PillButtonHeight / 2);
        _back.CornerRadius = _lighten.CornerRadius = radius;
        _back.Background = quiet ? Theme.QuietPillBrush : Theme.PillButtonBrush;
        if (quiet) { _back.BorderBrush = Theme.HighlightBorderBrush; _back.BorderThickness = new Thickness(1); }
        _lighten.Background = Theme.PillLightenBrush;
        var fg = quiet ? Theme.PrimaryText : Theme.PillButtonTextBrush;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = Theme.PillButtonPadding, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (icon != null)
        {
            _icon.Data = icon;
            _icon.Size = Theme.IconSmall;
            _icon.Foreground = fg;
            _icon.VerticalAlignment = VerticalAlignment.Center;
            _icon.Margin = new Thickness(0, 0, Theme.PillIconGap, 0);
            row.Children.Add(_icon);
        }
        _label.Text = label;
        _label.FontFamily = Theme.FontSemiBold;
        _label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _label.FontSize = Theme.FontBody;
        _label.Foreground = fg;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.IsHitTestVisible = false;
        row.Children.Add(_label);

        Children.Add(_back);
        Children.Add(_lighten);
        Children.Add(row);
        Motions.HoverFade(this, _lighten);
        Motions.PressScale(this);
        Tapped += (_, e) =>
        {
            e.Handled = true;
            Click?.Invoke();
            if (Command?.CanExecute(null) == true) Command.Execute(null);
        };
    }

    public ICommand? Command { get; set; }
    public event Action? Click;
    public string? Tooltip { set => ToolTipService.SetToolTip(this, value); }

    /// <summary>Changes the icon and label (for buttons built with an icon, e.g. Pause ⇄ Resume).</summary>
    public void SetContent(string icon, string label)
    {
        _icon.Data = icon;
        _label.Text = label;
    }

    /// <summary>False = 40% opacity (still clickable; the command ignores it).</summary>
    public bool Active { set => Opacity = value ? 1 : 0.4; }
}

/// <summary>Text button (the WPF TextButton style): a capsule hover fill that springs in, press squash.</summary>
public sealed class TextButton : Grid
{
    private readonly Border _fill = new();
    private readonly StackPanel _content = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 0) };

    public TextButton()
    {
        Height = 22;
        Background = Theme.TransparentBrush;
        _fill.Background = Theme.CardHoverBrush;
        _fill.CornerRadius = new CornerRadius(11); // half the height: a pill
        _content.HorizontalAlignment = HorizontalAlignment.Center;
        _content.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(_fill);
        Children.Add(_content);
        Motions.HoverFade(this, _fill);
        Motions.PressScale(this);
        Tapped += (_, e) => { Click?.Invoke(); if (Command?.CanExecute(null) == true) Command.Execute(null); e.Handled = true; };
    }

    public ICommand? Command { get; set; }
    public event Action? Click;

    public void SetContent(string? icon, string text)
    {
        _content.Children.Clear();
        if (icon != null)
            _content.Children.Add(new VectorIcon { Data = icon, Size = Theme.IconSmall, Foreground = Theme.SecondaryText, VerticalAlignment = VerticalAlignment.Center });
        _content.Children.Add(new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.Resources["CaptionText"],
            Foreground = Theme.SecondaryText,
            Margin = icon != null ? Theme.GapLeftS2 : new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        });
    }
}
