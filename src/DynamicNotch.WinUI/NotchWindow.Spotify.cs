using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// The media player's Spotify panel in the notch: opening it grows the notch down on its own spring (and pins the notch,
/// as a click would, so it stays open while you browse); closing shrinks it back. The search box takes keyboard focus
/// only when you click it (the window otherwise never activates), and hands it back to the app you were in on Esc in
/// an empty box, when the panel closes or when the notch closes. With the keyboard hook (no focus), Esc steps back:
/// a playlist to the panel, a search to the playlists, the panel closed, then the notch.
/// </summary>
public sealed partial class NotchWindow
{
    private bool _spotifyResize; // the next retarget is the Spotify panel opening / closing

    private void BuildSpotifyPanel()
    {
        var music = _frame.Music;
        music.SpotifyPanelChanged += () =>
        {
            bool open = Vm.SpotifyLibrary.IsOpen;
            if (open && _state == NotchState.Expanded && !_pinned) Pin();
            if (!open && _hasFocus && !Vm.IsAskPage && !Vm.IsPaletteOpen) ReleaseFocus(restore: true);
            _spotifyResize = true;
            ApplyTargets();
            _spotifyResize = false;
            UpdatePinnedRect();
        };
        music.SpotifyPanel.FocusRequested += () =>
        {
            if (_state != NotchState.Expanded) return;
            if (!_hasFocus) TakeFocus();
            Ui.Post(() => { music.SpotifyPanel.FocusBox(); UpdateKeyCapture(); });
        };
        music.SpotifyPanel.FocusReleaseRequested += () => { if (_hasFocus) ReleaseFocus(restore: true); };
    }

    /// <summary>Dev (--spotify-panel search:…): puts the caret in the search box (in-window focus only, no foreground change).</summary>
    internal void DevFocusSpotifySearch() => _frame.Music.SpotifyPanel.FocusBox();

    /// <summary>Esc through the hook: steps back through the Spotify panel first. True when it handled the key.</summary>
    private bool EscapeClosesSpotify() => _state == NotchState.Expanded && Vm.IsMusicPage && _frame.Music.EscapeSpotify();
}
