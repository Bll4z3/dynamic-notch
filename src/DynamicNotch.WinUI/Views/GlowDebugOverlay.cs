using System.Diagnostics;
using DynamicNotch.Design;
using DynamicNotch.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace DynamicNotch.Views;

/// <summary>
/// Settings ▸ Customisation ▸ Glow debug overlay: live analysis on the media page. Kick / snare / hi-hat rows show the
/// band's spectral flux against its adaptive threshold (the marker is the threshold) and flash when an onset fires;
/// sub, vocal, energy and build-up rows show their level; the last line shows tempo, vocal tonality / centre and loudness.
/// Updated from the glow's per-frame loop, only while it's visible.
/// </summary>
public sealed class GlowDebugOverlay : UserControl
{
    private sealed class Row
    {
        public required ScaleTransform Fill;
        public Ellipse? Flash;
        public long FlashAt;
    }

    private readonly Row _kick, _snare, _hats, _sub, _vocal, _voice, _hold, _energy, _build;
    private readonly TextBlock _status = new();
    private int _kicks = -1, _snares = -1, _hatCount = -1, _drops = -1, _vocalOnsets = -1;
    private long _dropAt;
    private readonly TextBlock _dropText = new();

    public GlowDebugOverlay()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        var stack = new StackPanel { Spacing = 2 };
        _kick = AddRow(stack, "Kick", Color.FromArgb(255, 255, 90, 90), onset: true);
        _snare = AddRow(stack, "Snare", Color.FromArgb(255, 255, 200, 80), onset: true);
        _hats = AddRow(stack, "Hats", Color.FromArgb(255, 120, 220, 255), onset: true);
        _sub = AddRow(stack, "Sub", Color.FromArgb(255, 200, 120, 255), onset: false);
        _vocal = AddRow(stack, "Vocal", Color.FromArgb(255, 120, 255, 160), onset: false);
        _voice = AddRow(stack, "Voice", Color.FromArgb(255, 90, 210, 140), onset: true); // presence; flashes on sung syllables
        _hold = AddRow(stack, "Held", Color.FromArgb(255, 170, 255, 210), onset: false);
        _energy = AddRow(stack, "Energy", Color.FromArgb(255, 230, 230, 230), onset: false);
        _build = AddRow(stack, "Build", Color.FromArgb(255, 255, 150, 220), onset: false);
        _status.FontFamily = Theme.MonoFont;
        _status.FontSize = 9.5;
        _status.Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 255, 255, 255));
        stack.Children.Add(_status);
        _dropText.FontFamily = Theme.MonoFont;
        _dropText.FontSize = 10;
        _dropText.Text = "DROP";
        _dropText.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 80, 200));
        _dropText.Opacity = 0;
        stack.Children.Add(_dropText);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 6, 8, 6),
            Child = stack,
        };
    }

    private static Row AddRow(StackPanel stack, string label, Color colour, bool onset)
    {
        var grid = new Grid { Height = 11 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.Children.Add(new TextBlock { Text = label, FontFamily = Theme.MonoFont, FontSize = 9.5, Foreground = new SolidColorBrush(Color.FromArgb(0xE0, 255, 255, 255)), VerticalAlignment = VerticalAlignment.Center });
        var track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)) };
        var fillScale = new ScaleTransform { ScaleX = 0 };
        track.Children.Add(new Rectangle { Fill = new SolidColorBrush(colour), HorizontalAlignment = HorizontalAlignment.Stretch, RenderTransform = fillScale });
        if (onset) track.Children.Add(new Rectangle { Width = 1.5, Fill = new SolidColorBrush(Colors.White), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(47, 0, 0, 0) }); // the threshold
        Grid.SetColumn(track, 1);
        grid.Children.Add(track);
        Ellipse? flash = null;
        if (onset)
        {
            flash = new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(colour), Opacity = 0.12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(flash, 2);
            grid.Children.Add(flash);
        }
        stack.Children.Add(grid);
        return new Row { Fill = fillScale, Flash = flash };
    }

    /// <summary>One frame of live values (null: no music).</summary>
    public void Update(MusicFrame? f, bool visible)
    {
        if (!visible || Visibility != Visibility.Visible) return;
        long now = Stopwatch.GetTimestamp();
        if (f == null)
        {
            foreach (var r in new[] { _kick, _snare, _hats, _sub, _vocal, _voice, _hold, _energy, _build }) r.Fill.ScaleX = 0;
            if (_status.Text != "no music") _status.Text = "no music";
            _kicks = _snares = _hatCount = _drops = _vocalOnsets = -1;
            Fade(now);
            return;
        }
        // Onset rows: flux / threshold, so the threshold sits at the marker (half way).
        _kick.Fill.ScaleX = Ratio(f.KickFlux, f.KickThreshold);
        _snare.Fill.ScaleX = Ratio(f.SnareFlux, f.SnareThreshold);
        _hats.Fill.ScaleX = Ratio(f.HatFlux, f.HatThreshold);
        _sub.Fill.ScaleX = f.Sub;
        _vocal.Fill.ScaleX = f.Vocal;
        _voice.Fill.ScaleX = f.VocalPresence;
        _hold.Fill.ScaleX = f.VocalHold;
        if (_vocalOnsets >= 0 && f.VocalOnsetCount != _vocalOnsets) _voice.FlashAt = now;
        _vocalOnsets = f.VocalOnsetCount;
        _energy.Fill.ScaleX = f.Energy;
        _build.Fill.ScaleX = f.Build;
        if (_kicks >= 0 && f.KickCount != _kicks) _kick.FlashAt = now;
        if (_snares >= 0 && f.SnareCount != _snares) _snare.FlashAt = now;
        if (_hatCount >= 0 && f.HatCount != _hatCount) _hats.FlashAt = now;
        if (_drops >= 0 && f.DropCount != _drops) _dropAt = now;
        _kicks = f.KickCount; _snares = f.SnareCount; _hatCount = f.HatCount; _drops = f.DropCount;
        string status = $"{(f.Bpm > 0 ? $"{f.Bpm:0} bpm ({f.BpmConfidence:0.00})" : "bpm —")}  {f.RmsDb:0} dB\nflat {f.Flatness:0.00}  centre {f.Centre:0.00}\nharm {f.Harmonicity:0.00}  syl {f.Syllabic:0.00}  pitch {f.VocalPitch:0.00}";
        if (_status.Text != status) _status.Text = status;
        Fade(now);
    }

    private static double Ratio(float flux, float threshold) => threshold <= 0 ? 0 : Math.Clamp(flux / (2 * threshold), 0, 1);

    private void Fade(long now)
    {
        foreach (var r in new[] { _kick, _snare, _hats, _voice })
        {
            double since = (now - r.FlashAt) / (double)Stopwatch.Frequency;
            if (r.Flash != null) r.Flash.Opacity = since < 0.12 ? 1 : 0.12;
        }
        double drop = (now - _dropAt) / (double)Stopwatch.Frequency;
        _dropText.Opacity = drop < 1.2 ? 1 : 0;
    }
}
