using System.ComponentModel;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.ViewModels;
using DynamicNotch.Views.Lyrics;
using Microsoft.UI.Xaml;

namespace DynamicNotch;

/// <summary>
/// Lyrics on the notch: the closed notch shows the line being sung (not in minimal mode, while music plays and synced
/// lyrics are available, unless the focus timer has the closed notch), cross-fading with the usual closed content and
/// springing the notch's width to fit each line; the open media player grows taller for its lyrics layout. The lyric
/// timing also drives the glow's vocal reaction, and the audio analysis runs while lyrics show (its syllable onsets
/// refine estimated word timings).
/// </summary>
public sealed partial class NotchWindow
{
    private CollapsedLyricView _collapsedLyric = null!;
    private CompSpring _lyricShow = null!;
    private bool _lyricShown, _lyricOnClosed;

    private void BuildLyrics()
    {
        var n = _shape.Props;
        _collapsedLyric = new CollapsedLyricView(Vm.Lyrics, _shape.Compositor, n);
        ContentRoot.Children.Insert(ContentRoot.Children.IndexOf(CollapsedLayer) + 1, _collapsedLyric);
        // "Lyric" (0..1): the closed notch's line instead of its usual content (CollapsedLayer fades by 1 - Lyric).
        Anim.VisualOf(_collapsedLyric).StartAnimation("Opacity", Anim.Expr(_shape.Compositor, "Clamp(N.Collapsed, 0, 1) * Clamp(N.Lyric, 0, 1)", ("N", n)));
        Vm.Lyrics.PropertyChanged += OnLyricsChanged;
        _frame.LayoutChanged += () => { ApplyTargets(); UpdatePinnedRect(); };
        _frame.Glow.LyricVocal = Vm.Lyrics.VocalSignal;
    }

    private bool _applyingLyric;

    private void OnLyricsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applyingLyric) return; // raised by ApplyCollapsedLyric itself (it reads the result right after)
        if (e.PropertyName is not ("" or null or nameof(LyricsViewModel.CollapsedItem) or nameof(LyricsViewModel.ShowOnClosedNotch) or nameof(LyricsViewModel.Status))) return;
        try
        {
            int item = Vm.Lyrics.CollapsedItem;
            bool widthChanged = _collapsedLyric.Show(item);
            bool shown = item >= 0;
            if (shown != _lyricShown || widthChanged && shown) ApplyTargets();
            UpdateMusicReactiveForLyrics();
        }
        catch (Exception ex) { App.LogError(ex); } // raised from frame / dispatcher callbacks: never let it end the app
    }

    /// <summary>Whether the closed notch may show lyrics now (the view model then says which line, if any).</summary>
    private bool CollapsedLyricsAllowed => _state == NotchState.Collapsed && !Vm.MinimalNotch && Vm.Music.IsPlaying && !CompactTimerShown;

    /// <summary>Called from ApplyTargets: the closed notch's lyric line on / off, and its width (null = not shown).</summary>
    private double? ApplyCollapsedLyric()
    {
        if (_collapsedLyric is null) return null; // still building the window
        _applyingLyric = true;
        try { Vm.Lyrics.CollapsedEnabled = CollapsedLyricsAllowed; }
        finally { _applyingLyric = false; }
        int item = Vm.Lyrics.CollapsedItem;
        _collapsedLyric.Show(item);
        bool shown = item >= 0;
        if (_state == NotchState.Collapsed) _lyricOnClosed = shown;
        if (shown != _lyricShown || _lyricShow.Target != (shown ? 1 : 0))
        {
            _lyricShown = shown;
            if (_state == NotchState.Collapsed || !shown) _lyricShow.SetTarget(shown ? 1 : 0, Motion.LyricSwap);
        }
        return shown ? _collapsedLyric.DesiredWidth : null;
    }

    /// <summary>The audio analysis also runs while lyrics show (word refinement), when the music glow's capture is allowed.</summary>
    private void UpdateMusicReactiveForLyrics()
    {
        bool open = _state == NotchState.Expanded;
        UpdateMusicReactive(open && Vm.IsMusicPage && GlowScale > 0, Vm.Music.IsPlaying);
    }

    /// <summary>Lyrics visible somewhere (the panel, or the closed notch's line).</summary>
    private bool LyricsOnScreen => Vm.Lyrics.PanelVisible || _lyricShown && _state == NotchState.Collapsed;

    /// <summary>While pinned, clicks outside the open notch close it: keep that area in step with its height (lyrics layout).</summary>
    private void UpdatePinnedRect()
    {
        if (!_pinned || _keys == null) return;
        double h = Math.Max(Theme.ExpandedHeight, Math.Max(_morphFullHeight, ExpandedTarget().Height));
        double half = Theme.ExpandedWidth / 2 + Theme.CornerRadius;
        int cx = _winLeft + (int)Math.Round(Theme.HostWidth * _scale / 2);
        _keys.SetPinned(true, cx - (int)(half * _scale), _winTop, cx + (int)(half * _scale), _winTop + (int)(h * _scale));
    }
}
