using System.ComponentModel;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DynamicNotch.Views;

/// <summary>
/// The media player's Spotify panel. While Spotify is the player, a small arrow sits under the controls; it opens the
/// playlist panel in an extra row under the player (the page always lays that row out, so the player itself never
/// moves: the notch simply grows down over it, and shrinks back over it when the panel closes). A queue icon sits at
/// the end of the title. Opening a playlist or the queue sends the title, progress bar and controls out to the left one
/// after another and slides that view in from the right, beside the album art, which stays where it is (the queue
/// gives it a small pulse); Back reverses it. The lyrics layout and the panel don't share the notch: opening one closes
/// the other. Every motion is a compositor spring; with Windows animations off, things only fade.
/// </summary>
public sealed partial class MusicPage
{
    private SpotifyLibraryViewModel _library = null!;
    private readonly IconButton _panelArrow = new(26, Theme.IconSmall);
    private readonly Border _arrowWrap = new(); // joins the open-notch stagger (its own visual is the arrow's show / hide)
    private readonly Border _progressFlip = new(); // carries the progress bar's exit (the bar itself belongs to the stagger)
    private readonly QueueButton _queueButton = new();
    private SpotifyPanel _spotifyPanel = null!;
    private PlaylistView _playlistView = null!;
    private QueueView _queueView = null!;
    private readonly NoticePill _notice = new() { Margin = new Thickness(0, 0, 0, Theme.S3) };
    private CompSpring _panelOpen = null!, _panelShown = null!, _playlistIn = null!, _queueIn = null!, _arrowShow = null!, _arrowTurn = null!;
    private CompSpring[] _detailsOut = null!; // title · progress · controls, each leaving a beat after the last
    private Microsoft.UI.Composition.CompositionPropertySet _sp = null!;
    private SpotifyPanelView _shownView;

    /// <summary>The playlist panel (the window wires its search box's keyboard focus).</summary>
    public SpotifyPanel SpotifyPanel => _spotifyPanel;

    /// <summary>The panel opened or closed (the notch re-targets its height).</summary>
    public event Action? SpotifyPanelChanged;

    /// <summary>The last open / close involved the queue: the notch grows with a springier curve.</summary>
    public bool SpringyResize { get; private set; }

    private void BuildSpotify(SpotifyLibraryViewModel library, Compositor c)
    {
        _library = library;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(Theme.SpotifyPanelHeight) });

        // ---- The arrow under the controls ----
        _panelArrow.Icon = Icons.ChevronDown;
        _arrowWrap.Child = _panelArrow;
        _arrowWrap.HorizontalAlignment = HorizontalAlignment.Center;
        _arrowWrap.VerticalAlignment = VerticalAlignment.Bottom;
        _arrowWrap.Margin = new Thickness(0, 0, 0, -2);
        SetColumn(_arrowWrap, 2);
        SetRowSpan(_arrowWrap, 5);
        Canvas.SetZIndex(_arrowWrap, 2);
        _panelArrow.Click += () => _library.TogglePanel();
        Children.Add(_arrowWrap);
        _queueButton.Click += () => _library.OpenQueue();

        // ---- The panel (its own row); the playlist and queue views (right of the art, the whole height) ----
        _spotifyPanel = new SpotifyPanel(library);
        SetRow(_spotifyPanel, 5);
        SetColumnSpan(_spotifyPanel, 3);
        Children.Add(_spotifyPanel);
        _playlistView = new PlaylistView(library);
        _queueView = new QueueView(library, _vm);
        foreach (var view in new FrameworkElement[] { _playlistView, _queueView })
        {
            SetColumn(view, 2);
            SetRowSpan(view, 6);
            Canvas.SetZIndex(view, 2);
            Children.Add(view);
        }
        SetRowSpan(_notice, 6);
        SetColumnSpan(_notice, 3);
        Canvas.SetZIndex(_notice, 5);
        Children.Add(_notice);
        library.Notice += (text, problem) => _notice.Show(text, problem);

        // ---- Motion ----
        var p = _sp = c.CreatePropertySet();
        p.InsertScalar("mv", Anim.Enabled ? 1 : 0); // with Windows animations off: fades only, no movement
        _panelOpen = new CompSpring(p, "o", Motion.SpotifyPanelOpen, 0);
        _playlistIn = new CompSpring(p, "l", Motion.SpotifyViewSwap, 0);
        _queueIn = new CompSpring(p, "q", Motion.SpotifyViewSwap, 0);
        _arrowShow = new CompSpring(p, "a", Motion.FadeIn, 0);
        _arrowTurn = new CompSpring(p, "t", Motion.SpotifyPanelOpen, 0);
        _panelShown = new CompSpring(p, "pn", Motion.SpotifyPanelOpen, 0); // 1 only while the panel itself is the view
        _detailsOut = new[] { new CompSpring(p, "d0", Motion.SpotifyDetailsOut, 0), new CompSpring(p, "d1", Motion.SpotifyDetailsOut, 0), new CompSpring(p, "d2", Motion.SpotifyDetailsOut, 0) };
        string open = "Clamp(p.o, 0, 1)", side = "Max(Clamp(p.l, 0, 1), Clamp(p.q, 0, 1))";
        // The panel drops in a touch as the notch grows over it, and gives way to the playlist / queue view.
        var pv = Anim.VisualOf(_spotifyPanel, translation: true);
        // Its opacity follows "the panel is the view" (not "the notch is tall"), so opening the queue straight from the
        // player never flashes the search bar on the way.
        pv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.pn, 0, 1)", ("p", p)));
        pv.StartAnimation("Translation", Anim.Expr(c, $"Vector3(-26 * {side} * p.mv, (1 - {open}) * -12 * p.mv, 0)", ("p", p)));
        // Title, progress bar and controls slide left and fade, one after another.
        var blocks = new FrameworkElement[] { _titleFlip, _progressFlip, _transportFlip };
        for (int i = 0; i < blocks.Length; i++)
        {
            var bv = Anim.VisualOf(blocks[i], translation: true);
            string d = $"Clamp(p.d{i}, 0, 1)";
            bv.StartAnimation("Opacity", Anim.Expr(c, $"1 - {d}", ("p", p)));
            bv.StartAnimation("Translation", Anim.Expr(c, $"Vector3(-22 * {d} * p.mv, 0, 0)", ("p", p)));
        }
        // The playlist / queue view arrives from the right.
        foreach (var (view, key) in new (FrameworkElement, string)[] { (_playlistView, "l"), (_queueView, "q") })
        {
            var lv = Anim.VisualOf(view, translation: true);
            lv.StartAnimation("Opacity", Anim.Expr(c, $"Clamp(p.{key}, 0, 1)", ("p", p)));
            lv.StartAnimation("Translation", Anim.Expr(c, $"Vector3((1 - Clamp(p.{key}, 0, 1)) * 34 * p.mv, 0, 0)", ("p", p)));
        }
        var av = Anim.VisualOf(_panelArrow, translation: true);
        av.StartAnimation("Opacity", Anim.Expr(c, $"Clamp(p.a, 0, 1) * (1 - {side})", ("p", p)));
        av.StartAnimation("Translation", Anim.Expr(c, $"Vector3(-26 * {side} * p.mv, (1 - Clamp(p.a, 0, 1)) * -4 * p.mv, 0)", ("p", p)));
        var iv = Anim.VisualOf(_panelArrow.IconElement);
        iv.CenterPoint = new System.Numerics.Vector3((float)Theme.IconSmall / 2, (float)Theme.IconSmall / 2, 0);
        iv.StartAnimation("RotationAngleInDegrees", Anim.Expr(c, "180 * p.t", ("p", p)));

        library.PropertyChanged += OnLibraryChanged;
        _lyricsVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "" or null or nameof(LyricsViewModel.IsOpen))
            {
                if (_lyricsVm.IsOpen && _library.IsOpen) _library.Close(); // lyrics take the notch over
                UpdateArrow(animate: IsLoaded);
            }
        };
        ApplySpotifyView(animate: false);
    }

    private void OnLibraryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or null or nameof(SpotifyLibraryViewModel.View)) ApplySpotifyView(animate: IsLoaded);
        else if (e.PropertyName is nameof(SpotifyLibraryViewModel.IsSpotifySource) or nameof(SpotifyLibraryViewModel.CanShowQueue)) UpdateArrow(animate: IsLoaded);
    }

    private void ApplySpotifyView(bool animate)
    {
        var view = _library.View;
        var was = _shownView;
        bool wasOpen = was != SpotifyPanelView.Closed, open = view != SpotifyPanelView.Closed;
        bool wasSide = was is SpotifyPanelView.Playlist or SpotifyPanelView.Queue, side = view is SpotifyPanelView.Playlist or SpotifyPanelView.Queue;
        _shownView = view;
        _sp.InsertScalar("mv", Anim.Enabled ? 1 : 0);
        if (open && _lyricsVm.IsOpen) _lyricsVm.TogglePanel(); // one or the other
        double o = open ? 1 : 0, l = view == SpotifyPanelView.Playlist ? 1 : 0, q = view == SpotifyPanelView.Queue ? 1 : 0;
        if (animate)
        {
            _panelOpen.SetTarget(o, open ? Motion.SpotifyPanelOpen : Motion.FadeOut);
            bool panel = view == SpotifyPanelView.Panel;
            _panelShown.SetTarget(panel ? 1 : 0, panel ? Motion.SpotifyPanelOpen : Motion.FadeOut, panel && wasSide ? 0.08 : 0);
            _playlistIn.SetTarget(l, Motion.SpotifyViewSwap, l > 0 && wasSide ? 0 : l > 0 ? 0.08 : 0);
            _queueIn.SetTarget(q, Motion.SpotifyViewSwap, q > 0 && !wasSide ? 0.08 : 0);
            _arrowTurn.SetTarget(o, Motion.SpotifyPanelOpen);
            if (side != wasSide)
            {
                // Leaving: title first, then the progress bar, then the controls. Coming back: the reverse.
                for (int i = 0; i < _detailsOut.Length; i++)
                {
                    double delay = (side ? i : _detailsOut.Length - 1 - i) * 0.045 + (side ? 0 : 0.06);
                    _detailsOut[i].SetTarget(side ? 1 : 0, side ? Motion.SpotifyDetailsOut : Motion.SpotifyViewSwap, Anim.Enabled ? delay : 0);
                }
            }
        }
        else
        {
            _panelOpen.Snap(o); _playlistIn.Snap(l); _queueIn.Snap(q); _arrowTurn.Snap(o);
            _panelShown.Snap(view == SpotifyPanelView.Panel ? 1 : 0);
            foreach (var d in _detailsOut) d.Snap(side ? 1 : 0);
        }
        // The album art anchors the queue transition with a small pulse.
        if (animate && Anim.Enabled && (view == SpotifyPanelView.Queue) != (was == SpotifyPanelView.Queue)) _artPop?.Impulse(-0.75);
        if (view == SpotifyPanelView.Queue && was != SpotifyPanelView.Queue) _queueView.Shown();
        else if (was == SpotifyPanelView.Queue && view != SpotifyPanelView.Queue) _queueView.Hidden();

        _spotifyPanel.IsHitTestVisible = view == SpotifyPanelView.Panel;
        _playlistView.IsHitTestVisible = view == SpotifyPanelView.Playlist;
        _queueView.IsHitTestVisible = view == SpotifyPanelView.Queue;
        _details.IsHitTestVisible = !side;
        if (!open) _notice.Hide();
        UpdateArrow(animate);
        if (open != wasOpen)
        {
            SpringyResize = view == SpotifyPanelView.Queue || was == SpotifyPanelView.Queue;
            SpotifyPanelChanged?.Invoke();
        }
    }

    /// <summary>The arrow (and the queue icon) show while Spotify plays; the arrow not in the lyrics layout, nor over a side view.</summary>
    private void UpdateArrow(bool animate)
    {
        bool show = _library.IsSpotifySource && !_lyricsLayout;
        _panelArrow.IsHitTestVisible = show && _library.View is SpotifyPanelView.Closed or SpotifyPanelView.Panel;
        _panelArrow.Tooltip = _library.IsOpen ? "Hide playlists" : "Playlists and search";
        if (animate && Anim.Enabled) _arrowShow.SetTarget(show ? 1 : 0, show ? Motion.FadeIn : Motion.FadeOut);
        else _arrowShow.Snap(show ? 1 : 0);
        var queue = _library.CanShowQueue ? Visibility.Visible : Visibility.Collapsed;
        if (_queueButton.Visibility != queue) _queueButton.Visibility = queue; // the title re-measures (ellipsis if needed)
    }

    /// <summary>The cover changed (null: no cover).</summary>
    private void OnArtAccent(Windows.UI.Color? color)
    {
        _queueView?.PlayerChanged(); // the "Now playing" stand-in follows the player while Spotify sends none
    }

    /// <summary>Esc while the panel is open: a playlist / the queue goes back, a search clears, the panel closes. True when handled.</summary>
    public bool EscapeSpotify()
    {
        switch (_library.View)
        {
            case SpotifyPanelView.Playlist: _library.BackToPanel(); return true;
            case SpotifyPanelView.Queue: _library.CloseQueue(); return true;
            case SpotifyPanelView.Panel when _library.IsSearching: _library.ClearSearch(); return true;
            case SpotifyPanelView.Panel: _library.Close(); return true;
            default: return false;
        }
    }
}
