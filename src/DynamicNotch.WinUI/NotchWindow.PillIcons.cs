using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DynamicNotch;

/// <summary>
/// System pills (Wi-Fi, VPN, Bluetooth, audio output, USB drives, displays) get a coloured, animated icon: the icon tile
/// takes a soft tint of the kind's colour and <see cref="PillIconView"/> plays its short motion once the pill's content
/// has revealed. App notifications and other pills keep the plain glyph / app logo and the neutral tile.
/// </summary>
public sealed partial class NotchWindow
{
    private PillIconView _pillIcon = null!;

    private void BuildPillIcon()
    {
        _pillIcon = new PillIconView(Theme.IconMedium) { Visibility = Visibility.Collapsed };
        if (PillIconBox.Child is Panel host) host.Children.Add(_pillIcon);
    }

    /// <summary>Called for every pill shown: animated + tinted for system kinds, plain otherwise.</summary>
    private void ApplyPillIcon(PillRequest p)
    {
        var color = p.Logo == null ? PillIconView.ColorFor(p.Anim) : null;
        if (color is not { } c)
        {
            _pillIcon.Clear();
            PillGlyph.Visibility = Visibility.Visible;
            PillIconBox.Background = Theme.TileBrush;
            PillIconBox.BorderBrush = Theme.TileBorderBrush;
            return;
        }
        PillGlyph.Visibility = Visibility.Collapsed;
        PillIconBox.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x2A, c.R, c.G, c.B));
        PillIconBox.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, c.R, c.G, c.B));
        // Start once the pill has room: right away when a pill is already showing, else as the notch grows into it.
        double delay = _state == NotchState.Pill && _pillContent.Value > 0.5 ? 0.06 : 0.16;
        _pillIcon.Play(p.Anim, p.Glyph, delay);
    }
}
