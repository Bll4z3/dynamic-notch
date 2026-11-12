using DynamicNotch.Design;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DynamicNotch.Views;

/// <summary>One icon from <see cref="Icons"/> at a given size, with the app's constant stroke weight.</summary>
public sealed class VectorIcon : Canvas
{
    private string? _data;
    private double _size = Theme.IconMedium;
    private Brush? _foreground;

    public VectorIcon()
    {
        IsHitTestVisible = false;
        Width = Height = _size;
    }

    public string? Data { get => _data; set { if (_data == value) return; _data = value; Rebuild(); } }

    public double Size { get => _size; set { _size = value; Width = Height = value; Rebuild(); } }

    public Brush? Foreground { get => _foreground; set { _foreground = value; Rebuild(); } }

    private void Rebuild()
    {
        Children.Clear();
        var path = Icons.CreatePath(_data, _size, _foreground ?? Theme.PrimaryText);
        if (path != null) Children.Add(path);
    }
}
