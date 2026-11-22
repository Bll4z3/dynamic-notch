using DynamicNotch.Design;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DynamicNotch.Views;

/// <summary>
/// The app's text-field look (the WPF AskInput / KeyInput styles) applied to a WinUI TextBox or
/// PasswordBox through lightweight resources: the same dark fill and hairline border in every visual
/// state, the border brightening on focus, no light focus fill and no accent underline, and a radius of
/// min(R, height / 2).
/// </summary>
public static class InputStyle
{
    public static void Apply(Control box)
    {
        box.BorderThickness = new Thickness(1);
        // WinUI's text boxes have a 64 px minimum width: a narrower box (the Focus Timer minutes) was laid out
        // wider than its slot and clipped, so its centred digits sat off to the right.
        box.MinWidth = 0;
        var r = box.Resources;
        foreach (var state in new[] { "", "PointerOver", "Focused", "Disabled" })
        {
            r["TextControlBackground" + state] = Theme.InputBrush;
            r["TextControlForeground" + state] = Theme.PrimaryText;
            r["TextControlPlaceholderForeground" + state] = Theme.TertiaryText;
            r["TextControlBorderBrush" + state] = state == "Focused" ? Theme.DropZoneBrush : Theme.TileBorderBrush;
        }
        r["TextControlBorderThemeThicknessFocused"] = new Thickness(1);
        r["TextControlSelectionHighlightColor"] = Theme.AccentBrush;
        box.SizeChanged += (_, e) => box.CornerRadius = new CornerRadius(Math.Min(Theme.CornerRadius, e.NewSize.Height / 2));
    }
}
