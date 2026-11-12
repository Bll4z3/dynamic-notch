using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.ViewModels;

namespace DynamicNotch.Views;

public partial class MusicPage : UserControl
{
    private MusicViewModel? _vm;
    private readonly Spring _artPop = new(Motion.Pop, 1) { Precision = 1 };
    private readonly ScaleTransform _artScale = new(1, 1);

    // Album-art accent: blends between colours with a spring.
    private readonly SolidColorBrush _accent = new(Theme.AccentColor);
    private readonly Spring _accentBlend = new(Motion.RingFill, 1);
    private Color _accentFrom = Theme.AccentColor, _accentTo = Theme.AccentColor;

    public MusicPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        Seek.ScrubStarted += () => _vm?.BeginScrub();
        Seek.Scrubbing += f => _vm?.PreviewScrub(f);
        Seek.ScrubCommitted += f => _vm?.CommitScrub(f);
        Seek.FillBrush = _accent;

        // Album art "pop" when the track changes (stacked inside the press-scale transform).
        var inner = (FrameworkElement)ArtCard.Child;
        inner.RenderTransformOrigin = new Point(0.5, 0.5);
        inner.RenderTransform = _artScale;
        _artPop.ValueChanged += v => { _artScale.ScaleX = _artScale.ScaleY = v; };

        _accentBlend.ValueChanged += v =>
        {
            var c = Theme.Lerp(_accentFrom, _accentTo, v);
            _accent.Color = c;
        };
        _accentBlend.Snap(1);
    }

    /// <summary>The element that carries the shared-element (mini art ⇄ album art) transform.</summary>
    public FrameworkElement ArtElement => ArtHost;

    /// <summary>Vertical midpoints (in this page's coordinates) of the cover and of the details column.</summary>
    public (double cover, double details) MeasureCentres()
    {
        var art = ArtHost.TranslatePoint(new Point(0, ArtHost.ActualHeight / 2), this);
        var info = Details.TranslatePoint(new Point(0, Details.ActualHeight / 2), this);
        return (art.Y, info.Y);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmChanged;
            _vm.TrackChanged -= OnTrackChanged;
        }
        _vm = e.NewValue as MusicViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmChanged;
            _vm.TrackChanged += OnTrackChanged;
            UpdatePlayGlyph();
            UpdateAccent();
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or null or nameof(MusicViewModel.IsPlaying)) UpdatePlayGlyph();
        if (e.PropertyName is "" or null or nameof(MusicViewModel.Artwork)) UpdateAccent();
    }

    private void UpdateAccent()
    {
        var art = _vm?.Artwork;
        var target = art == null ? Theme.AccentColor : DynamicNotch.Services.AlbumColorService.Extract(art);
        if (target == _accentTo) return;
        _accentFrom = _accent.Color;
        _accentTo = target;
        _accentBlend.Snap(0);
        _accentBlend.SetTarget(1, Motion.RingFill);
    }

    private bool _lastPlaying;

    private void UpdatePlayGlyph()
    {
        if (_vm == null) return;
        bool playing = _vm.IsPlaying;
        PlayButton.Content = playing ? Icons.Pause : Icons.Play;
        if (playing != _lastPlaying)
        {
            _lastPlaying = playing;
            // Subtle squash on the art when play state flips.
            _artPop.SetTarget(playing ? 1 : 0.94, Motion.Pop);
        }
    }

    private void OnTrackChanged()
    {
        _artPop.Snap(0.88);
        _artPop.SetTarget(_vm?.IsPlaying == false ? 0.94 : 1, Motion.Pop);
    }

    private void ArtCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (_vm?.HasMultipleSessions == true) _vm.CycleSessionCommand.Execute(null);
        e.Handled = true;
    }
}
