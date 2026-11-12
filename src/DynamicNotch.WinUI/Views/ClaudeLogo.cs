using System.Text.RegularExpressions;
using System.Xml.Linq;
using DynamicNotch.Design;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace DynamicNotch.Views;

/// <summary>
/// The official Claude logo, loaded from <c>Assets/claude-logo.svg</c> (or <c>.png</c>) next to the app,
/// as in the WPF app. Nothing is drawn or approximated: the SVG's own &lt;path d&gt; data is rendered,
/// tinted with <see cref="Foreground"/>, or the PNG is shown as is. When neither file is there the
/// app's sparkle icon stands in.
/// </summary>
public sealed class ClaudeLogo : Grid
{
    private static readonly string Folder = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets");
    private static (string[] paths, double x, double y, double w, double h)? _svg;
    private static string? _png;
    private static bool _loaded;

    public ClaudeLogo(double size, Brush foreground)
    {
        IsHitTestVisible = false;
        VerticalAlignment = VerticalAlignment.Center;
        Width = Height = size;
        Load();
        if (_svg is { } svg)
        {
            // Fit the viewBox into the icon box, keeping its aspect.
            double k = Math.Min(size / svg.w, size / svg.h);
            var canvas = new Canvas { Width = size, Height = size };
            foreach (var d in svg.paths)
            {
                try
                {
                    var p = (Path)XamlReader.Load("<Path xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" Data=\"" + d + "\" />");
                    p.Fill = foreground;
                    p.RenderTransform = new CompositeTransform
                    {
                        ScaleX = k, ScaleY = k,
                        TranslateX = (size - svg.w * k) / 2 - svg.x * k,
                        TranslateY = (size - svg.h * k) / 2 - svg.y * k,
                    };
                    canvas.Children.Add(p);
                }
                catch { /* unsupported path data: skip it */ }
            }
            Children.Add(canvas);
        }
        else if (_png != null)
        {
            Children.Add(new Image { Source = new BitmapImage(new Uri(_png)), Width = size, Height = size, Stretch = Stretch.Uniform });
        }
        else
        {
            Children.Add(new VectorIcon { Data = Icons.Sparkle, Size = size, Foreground = foreground });
        }
    }

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var svgPath = System.IO.Path.Combine(Folder, "claude-logo.svg");
            if (File.Exists(svgPath))
            {
                var root = XDocument.Load(svgPath).Root!;
                var paths = root.Descendants().Where(e => e.Name.LocalName == "path")
                    .Select(e => (string?)e.Attribute("d")).Where(d => !string.IsNullOrWhiteSpace(d))
                    .Select(d => Regex.Replace(d!, @"\s+", " ").Trim()).ToArray();
                var vb = ((string?)root.Attribute("viewBox") ?? "0 0 24 24")
                    .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                if (paths.Length > 0 && vb.Length == 4 && vb[2] > 0 && vb[3] > 0) _svg = (paths, vb[0], vb[1], vb[2], vb[3]);
                return;
            }
            var pngPath = System.IO.Path.Combine(Folder, "claude-logo.png");
            if (File.Exists(pngPath)) _png = pngPath;
        }
        catch { /* fall back to the sparkle */ }
    }
}
