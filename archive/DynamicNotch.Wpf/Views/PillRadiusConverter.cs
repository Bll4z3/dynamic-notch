using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace DynamicNotch.Views;

/// <summary>
/// Pills are fully rounded: CornerRadius = height / 2, computed from the element's actual height,
/// so the ends are always perfect semicircles with no flat section, whatever the pill's size.
/// Usage: CornerRadius="{Binding ActualHeight, RelativeSource={RelativeSource Self}, Converter={StaticResource PillRadius}}"
/// </summary>
public sealed class PillRadiusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double h && h > 0 && !double.IsInfinity(h) ? new CornerRadius(h / 2) : new CornerRadius(0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
