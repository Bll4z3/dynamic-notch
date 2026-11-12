using System.Windows;
using System.Windows.Controls;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;

namespace DynamicNotch.Views;

/// <summary>Large hover preview of a clipboard item: full image, readable text, or the whole URL.</summary>
public partial class ClipboardPreviewCard : UserControl
{
    public ClipboardPreviewCard() => InitializeComponent();

    public ClipItemViewModel? Item { get; private set; }

    /// <summary>Show just an image (the Ask screenshot), fitted inside the given box.</summary>
    public void ShowImage(System.Windows.Media.ImageSource img, double maxWidth, double maxHeight)
    {
        Item = null;
        Text.Visibility = LinkPanel.Visibility = Visibility.Collapsed;
        if (img.Width <= 0 || img.Height <= 0) return;
        double s = Math.Min(maxWidth / img.Width, maxHeight / img.Height);
        ImageBox.Width = Math.Max(1, img.Width * s);
        ImageBox.Height = Math.Max(1, img.Height * s);
        ImageFill.ImageSource = img;
        ImageFrame.Visibility = Visibility.Visible;
    }

    public void Show(ClipItemViewModel item)
    {
        Item = item;
        ImageFrame.Visibility = Text.Visibility = LinkPanel.Visibility = Visibility.Collapsed;
        switch (item.Content.Kind)
        {
            case ClipKind.Image when item.Image is { PixelWidth: > 0, PixelHeight: > 0 } img:
                // Fit inside the max preview box, keeping the aspect, so the whole picture shows.
                double s = Math.Min(Theme.PreviewImageMaxWidth / img.PixelWidth, Theme.PreviewImageMaxHeight / img.PixelHeight);
                s = Math.Min(s, 2.0); // don't blow tiny images up past 2x
                ImageBox.Width = Math.Max(1, img.PixelWidth * s);
                ImageBox.Height = Math.Max(1, img.PixelHeight * s);
                ImageFill.ImageSource = img;
                ImageFrame.Visibility = Visibility.Visible;
                break;
            case ClipKind.Link:
                LinkText.Text = Cap(item.Content.Text ?? "");
                LinkPanel.Visibility = Visibility.Visible;
                break;
            default:
                Text.Text = Cap(item.Content.Text ?? "");
                Text.Visibility = Visibility.Visible;
                break;
        }
    }

    private static string Cap(string s)
    {
        s = s.Trim();
        return s.Length > Theme.PreviewMaxChars ? s[..Theme.PreviewMaxChars].TrimEnd() + "…" : s;
    }
}
