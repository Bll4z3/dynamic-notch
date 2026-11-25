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
/// moves: the notch simply grows down over it, and shrinks back over it when the panel closes). Opening a playlist
/// slides the details column and the panel away to the left and the playlist view in from the right, beside the album
/// art, which stays where it is; Back reverses it. The lyrics layout and the panel don't share the notch: opening one
/// closes the other. Every motion is a compositor spring.
/// </summary>
public sealed partial class MusicPage
{
    private SpotifyLibraryViewModel _library = null!;
    private readonly IconButton _panelArrow = new(26, Theme.IconSmall);
    private readonly Border _arrowWrap = new(); // joins the open-notch stagger (its own visual is the arrow's show / hide)
    private SpotifyPanel _spotifyPanel = null!;
    private PlaylistView _playlistView = null!;
    private readonly NoticePill _notice = new() { Margin = new Thickness(0, 0, 0, Theme.S3) };
    private CompSpring _panelOpen = null!, _playlistIn = null!, _arrowShow = null!, _arrowTurn = null!;
    private SpotifyPanelView _shownView;

    /// <summary>The playlist panel (the window wires its search box's keyboard focus).</summary>
    public SpotifyPanel SpotifyPanel => _spotifyPanel;

    /// <summary>The panel opened or closed (the notch re-targets its height).</summary>
    public event Action? SpotifyPanelChanged;

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

        // ---- The panel (its own row) and the playlist view (right of the art, the whole height) ----
        _spotifyPanel = new SpotifyPanel(library);
        SetRow(_spotifyPanel, 5);
        SetColumnSpan(_spotifyPanel, 3);
        Children.Add(_spotifyPanel);
        _playlistView = new PlaylistView(library);
        SetColumn(_playlistView, 2);
        SetRowSpan(_playlistView, 6);
        Canvas.SetZIndex(_playlistView, 2);
        Children.Add(_playlistView);
        SetRowSpan(_notice, 6);
        SetColumnSpan(_notice, 3);
        Canvas.SetZIndex(_notice, 5);
        Children.Add(_notice);
        library.Notice += (text, problem) => _notice.Show(text, problem);

        // ---- Motion ----
        var p = c.CreatePropertySet();
        _panelOpen = new CompSpring(p, "o", Motion.SpotifyPanelOpen, 0);
        _playlistIn = new CompSpring(p, "l", Motion.SpotifyViewSwap, 0);
        _arrowShow = new CompSpring(p, "a", Motion.FadeIn, 0);
        _arrowTurn = new CompSpring(p, "t", Motion.SpotifyPanelOpen, 0);
        string open = "Clamp(p.o, 0, 1)", list = "Clamp(p.l, 0, 1)";
        // The panel drops in a touch as the notch grows over it, and gives way to the playlist view.
        var pv = Anim.VisualOf(_spotifyPanel, translation: true);
        pv.StartAnimation("Opacity", Anim.Expr(c, $"{open} * (1 - {list})", ("p", p)));
        pv.StartAnimation("Translation", Anim.Expr(c, $"Vector3(-26 * {list}, (1 - {open}) * -12, 0)", ("p", p)));
        // The details column and the arrow slide left and fade; the playlist view arrives from the right.
        var dv = Anim.VisualOf(_details, translation: true);
        dv.StartAnimation("Opacity", Anim.Expr(c, $"1 - {list}", ("p", p)));
        dv.StartAnimation("Translation", Anim.Expr(c, $"Vector3(-26 * {list}, 0, 0)", ("p", p)));
        var lv = Anim.VisualOf(_playlistView, translation: true);
        lv.StartAnimation("Opacity", Anim.Expr(c, list, ("p", p)));
        lv.StartAnimation("Translation", Anim.Expr(c, $"Vector3((1 - {list}) * 34, 0, 0)", ("p", p)));
        var av = Anim.VisualOf(_panelArrow, translation: true);
        av.StartAnimation("Opacity", Anim.Expr(c, $"Clamp(p.a, 0, 1) * (1 - {list})", ("p", p)));
        av.StartAnimation("Translation", Anim.Expr(c, $"Vector3(-26 * {list}, (1 - Clamp(p.a, 0, 1)) * -4, 0)", ("p", p)));
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
        if (e.PropertyName is "" or null or nameof(SpotifyLibraryViewModel.View)) ApplySpotifyView(animate: IsLoaded && Anim.Enabled);
        else if (e.PropertyName == nameof(SpotifyLibraryViewModel.IsSpotifySource)) UpdateArrow(animate: IsLoaded);
    }

    private void ApplySpotifyView(bool animate)
    {
        var view = _library.View;
        bool wasOpen = _shownView != SpotifyPanelView.Closed, open = view != SpotifyPanelView.Closed;
        _shownView = view;
        if (open && _lyricsVm.IsOpen) _lyricsVm.TogglePanel(); // one or the other
        double o = open ? 1 : 0, l = view == SpotifyPanelView.Playlist ? 1 : 0;
        if (animate)
        {
            _panelOpen.SetTarget(o, open ? Motion.SpotifyPanelOpen : Motion.FadeOut);
            _playlistIn.SetTarget(l, Motion.SpotifyViewSwap);
            _arrowTurn.SetTarget(o, Motion.SpotifyPanelOpen);
        }
        else { _panelOpen.Snap(o); _playlistIn.Snap(l); _arrowTurn.Snap(o); }
        _spotifyPanel.IsHitTestVisible = view == SpotifyPanelView.Panel;
        _playlistView.IsHitTestVisible = view == SpotifyPanelView.Playlist;
        _details.IsHitTestVisible = view != SpotifyPanelView.Playlist;
        if (!open) _notice.Hide();
        UpdateArrow(animate);
        if (open != wasOpen) SpotifyPanelChanged?.Invoke();
    }

    /// <summary>The arrow shows while Spotify plays (not in the lyrics layout, nor over a playlist's songs).</summary>
    private void UpdateArrow(bool animate)
    {
        bool show = _library.IsSpotifySource && !_lyricsLayout;
        _panelArrow.IsHitTestVisible = show && _library.View != SpotifyPanelView.Playlist;
        _panelArrow.Tooltip = _library.IsOpen ? "Hide playlists" : "Playlists and search";
        if (animate) _arrowShow.SetTarget(show ? 1 : 0, show ? Motion.FadeIn : Motion.FadeOut);
        else _arrowShow.Snap(show ? 1 : 0);
    }

    /// <summary>Esc while the panel is open: a playlist goes back to the panel, the panel closes. True when handled.</summary>
    public bool EscapeSpotify()
    {
        switch (_library.View)
        {
            case SpotifyPanelView.Playlist: _library.BackToPanel(); return true;
            case SpotifyPanelView.Panel when _library.IsSearching: _library.ClearSearch(); return true;
            case SpotifyPanelView.Panel: _library.Close(); return true;
            default: return false;
        }
    }
}
