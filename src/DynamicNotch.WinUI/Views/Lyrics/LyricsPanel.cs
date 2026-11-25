using System.ComponentModel;
using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.Services.Lyrics;
using DynamicNotch.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DynamicNotch.Views.Lyrics;

/// <summary>
/// The lyrics panel beside the album art in the media player's lyrics layout.
/// <list type="bullet">
/// <item>The current line sits at the focus point (a little above the middle), full size and bright, its words lighting
/// up as they're sung. Past lines dim, upcoming ones are softer, and lines further away get smaller and blurred.</item>
/// <item>Lines scroll with a spring when the current line changes, each lower line a beat after the one above (a soft
/// cascade). The mouse wheel scrolls by hand: auto-follow pauses, a "Back to current" button appears, and following
/// resumes after a few seconds or on click.</item>
/// <item>Click a line to play from it (with a ripple), right-click to copy it, hover for a highlight.</item>
/// <item>Instrumental breaks show three dots that fill until the next line. Plain (unsynced) lyrics scroll gently with
/// the track's progress. A short shimmer shows while lyrics load; a tidy message when there are none.</item>
/// </list>
/// </summary>
public sealed class LyricsPanel : Grid
{
    // ---- Tuning ----
    /// <summary>The current line's font size (the text-size setting scales it).</summary>
    public const double BaseFontSize = 20;
    /// <summary>Where the current line sits, as a share of the panel's height from the top.</summary>
    private const double FocusShare = 0.4;
    /// <summary>Each lower line starts scrolling this much after the one above (s), up to <see cref="StaggerCap"/>.</summary>
    private const double ScrollStagger = 0.028, StaggerCap = 0.16;
    /// <summary>Lines further than this from the current one use a still style (no springs, no lit copies).</summary>
    private const int StyleWindow = 9, LitWindow = 4, ReleaseWindow = 7;
    /// <summary>Inactive lines are drawn this much smaller, so the current line is crisp at full size (1 / 1.05).</summary>
    private const double RestScale = 0.952;

    private readonly LyricsViewModel _vm;
    private readonly Compositor _c;
    private readonly Grid _viewport = new() { Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
    private readonly StackPanel _list = new();
    private readonly RectangleGeometry _clip = new();
    private readonly List<LyricRow> _rows = new();
    private readonly Grid _shimmer = new(), _empty = new();
    private readonly TextBlock _emptyTitle = new(), _emptySub = new();
    private readonly VectorIcon _emptyIcon = new();
    private readonly PillButton _back = new(Icons.ChevronUp, "Back to current", quiet: true) { Compact = true };
    private readonly Border _toast = new();
    private readonly TextBlock _toastText = new();
    private readonly CompSpring _listFade, _shimmerFade, _emptyFade, _backShow, _toastShow;
    private readonly DispatcherQueueTimer _resume, _clearTimer, _toastTimer, _plainTimer, _layoutTimer;
    private LyricColours _colours = LyricColours.Default;
    private bool _shown, _dirty = true, _follow = true, _laidOut;
    private double _manual;
    private LyricsDocument? _builtFor;

    public LyricsPanel(LyricsViewModel vm, Compositor compositor)
    {
        _vm = vm;
        _c = compositor;
        _viewport.Clip = _clip;
        _viewport.Children.Add(_list);
        _list.VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_viewport);
        _viewport.SizeChanged += (_, e) =>
        {
            _clip.Rect = new Windows.Foundation.Rect(-40, 0, e.NewSize.Width + 80, e.NewSize.Height);
            ScheduleLayout();
        };
        _list.SizeChanged += (_, _) => ScheduleLayout();
        _viewport.PointerWheelChanged += OnWheel;

        var q = DispatcherQueue.GetForCurrentThread();
        _resume = Timer(q, Motion.LyricFollowResumeMs, ResumeFollow);
        _clearTimer = Timer(q, 230, () => { if (_vm.Status != LyricsStatus.Ready) ClearRows(); });
        _toastTimer = Timer(q, 1900, () => _toastShow!.SetTarget(0, Motion.FadeOut));
        _layoutTimer = Timer(q, 0, OnLayout);
        _plainTimer = q.CreateTimer();
        _plainTimer.Interval = TimeSpan.FromMilliseconds(500);
        _plainTimer.Tick += (_, _) => { if (_follow) Retarget(animate: true, stagger: false, Motion.LyricScroll); };

        // ---- Fades: the lines, the loading shimmer, the empty state ----
        _listFade = Fade(_list, 0, rise: 10);
        BuildShimmer();
        _shimmerFade = Fade(_shimmer, 0, rise: 0);
        BuildEmpty();
        _emptyFade = Fade(_empty, 0, rise: 6);

        // ---- "Back to current" (after a manual scroll) and the message pill ----
        _back.HorizontalAlignment = HorizontalAlignment.Right;
        _back.VerticalAlignment = VerticalAlignment.Bottom;
        _back.Height = 26;
        _back.Margin = new Thickness(0, 0, 6, 8);
        _back.Click += ResumeFollow;
        Children.Add(_back);
        _backShow = Pop(_back);
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.VerticalAlignment = VerticalAlignment.Bottom;
        _toast.Margin = new Thickness(0, 0, 0, 10);
        _toast.Padding = new Thickness(12, 5, 12, 6);
        _toast.Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1C, 0x1C, 0x20));
        _toast.BorderBrush = Theme.HighlightBorderBrush;
        _toast.BorderThickness = new Thickness(1);
        _toast.CornerRadius = new CornerRadius(13);
        _toast.IsHitTestVisible = false;
        _toastText.Style = (Style)Application.Current.Resources["CaptionText"];
        _toastText.Foreground = Theme.PrimaryText;
        _toast.Child = _toastText;
        Children.Add(_toast);
        _toastShow = Pop(_toast);

        _vm.DocumentChanged += OnDocument;
        _vm.ActiveChanged += (_, _) => OnActive();
        _vm.ClockJumped += () => { _manual = 0; _follow = true; _backShow.SetTarget(0, Motion.FadeOut); _back.IsHitTestVisible = false; Restyle(true); Retarget(true, false, Motion.LyricScroll); };
        _vm.Message += ShowMessage;
        _vm.DisplayChanged += () => { _dirty = true; if (_shown) Rebuild(); else Prebuild(); };
        _vm.PropertyChanged += OnVmChanged;
        _back.IsHitTestVisible = false;
        OnDocument();
    }

    private static DispatcherQueueTimer Timer(DispatcherQueue q, int ms, Action tick)
    {
        var t = q.CreateTimer();
        t.IsRepeating = false;
        t.Interval = TimeSpan.FromMilliseconds(ms);
        t.Tick += (_, _) =>
        {
            t.Stop();
            try { tick(); } catch (Exception ex) { App.LogError(ex); } // a timer callback must never throw (it would end the app)
        };
        return t;
    }

    private CompSpring Fade(UIElement el, double initial, double rise)
    {
        var v = Anim.VisualOf(el, translation: true);
        var (spring, set) = Motions.Value(_c, Motion.FadeIn, initial);
        v.StartAnimation("Opacity", Anim.Expr(_c, "Clamp(p.v, 0, 1)", ("p", set)));
        if (rise > 0)
        {
            var t = Anim.Expr(_c, "Vector3(0, (1 - p.v) * R, 0)", ("p", set));
            t.SetScalarParameter("R", (float)rise);
            v.StartAnimation("Translation", t);
        }
        return spring;
    }

    private CompSpring Pop(FrameworkElement el)
    {
        var v = Anim.VisualOf(el, translation: true);
        var (spring, set) = Motions.Value(_c, Motion.Pop, 0);
        Motions.CentreOrigin(el);
        v.StartAnimation("Opacity", Anim.Expr(_c, "Clamp(p.v, 0, 1)", ("p", set)));
        v.StartAnimation("Scale", Anim.Expr(_c, "Vector3(0.9 + 0.1 * p.v, 0.9 + 0.1 * p.v, 1)", ("p", set)));
        v.StartAnimation("Translation", Anim.Expr(_c, "Vector3(0, (1 - p.v) * 6, 0)", ("p", set)));
        return spring;
    }

    // =================================================================================================================
    // Showing / hiding, colours
    // =================================================================================================================
    /// <summary>The panel is on screen (lyrics layout, notch open on the media player). Lines build on first show.</summary>
    public void SetShown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        if (shown)
        {
            // Lines are normally built ahead of time (Prebuild). If not, build them just after this frame, so the
            // layout morph that is starting right now isn't held up by creating the rows.
            if (_dirty || !ReferenceEquals(_builtFor, _vm.Document)) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_shown && (_dirty || !ReferenceEquals(_builtFor, _vm.Document))) Rebuild();
            });
            else { Restyle(false); Retarget(false, false, Motion.LyricScroll); }
            UpdatePlainTimer();
        }
        else
        {
            foreach (var r in _rows) r.Deactivate();
            _plainTimer.Stop();
        }
    }

    /// <summary>The album colours the words light up in.</summary>
    public void SetPalette(GlowPalette? palette)
    {
        var c = LyricColours.From(palette);
        if (c == _colours) return;
        _colours = c;
        foreach (var r in _rows) r.Recolour(c);
    }

    /// <summary>The band where the current line sits (panel coordinates): the vocal glow flows behind it.</summary>
    public Windows.Foundation.Rect FocusBand
    {
        get
        {
            double y = _viewport.ActualHeight * FocusShare;
            return new Windows.Foundation.Rect(0, y - 16, Math.Max(1, _viewport.ActualWidth), 32);
        }
    }

    // =================================================================================================================
    // Document and state
    // =================================================================================================================
    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or null or nameof(LyricsViewModel.IsPlaying)) UpdatePlainTimer();
    }

    private void OnDocument()
    {
        var status = _vm.Status;
        switch (status)
        {
            case LyricsStatus.Ready:
                _clearTimer.Stop();
                _shimmerFade.SetTarget(0, Motion.FadeOut);
                _emptyFade.SetTarget(0, Motion.FadeOut);
                _dirty = true;
                if (_shown) Rebuild();
                else Prebuild();
                break;
            case LyricsStatus.Loading:
                // The old lyrics fade out, then a short shimmer (only if loading takes a moment: cached songs skip it).
                _listFade.SetTarget(0, Motion.FadeOut);
                _emptyFade.SetTarget(0, Motion.FadeOut);
                _shimmerFade.SetTarget(1, Motion.FadeIn, delay: 0.2);
                _clearTimer.Stop();
                _clearTimer.Start();
                break;
            case LyricsStatus.Idle:
                _listFade.SetTarget(0, Motion.FadeOut);
                _shimmerFade.SetTarget(0, Motion.FadeOut);
                ShowEmpty(Icons.Music, "Nothing playing", "Play something to see its lyrics");
                _clearTimer.Stop();
                _clearTimer.Start();
                break;
            default:
                _listFade.SetTarget(0, Motion.FadeOut);
                _shimmerFade.SetTarget(0, Motion.FadeOut);
                if (status == LyricsStatus.Instrumental) ShowEmpty(Icons.Music, "No lyrics available", "This one's instrumental");
                else if (status == LyricsStatus.Offline) ShowEmpty(Icons.Globe, "No lyrics available", "You're offline, and this song isn't saved yet");
                else ShowEmpty(Icons.Quote, "No lyrics available", "Couldn't find lyrics for this song");
                _clearTimer.Stop();
                _clearTimer.Start();
                break;
        }
        UpdatePlainTimer();
    }

    /// <summary>
    /// Builds the lines while the panel is hidden (at low priority, when the app is idle), so they're laid out and ready
    /// by the time lyrics open: the open is just the morph. Hidden lines aren't activated (no lit copies or glows).
    /// </summary>
    private void Prebuild() => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
    {
        try { if (!_shown && _dirty && _vm.Status == LyricsStatus.Ready) Rebuild(); }
        catch (Exception ex) { App.LogError(ex); }
    });

    private void Rebuild()
    {
        _dirty = false;
        ClearRows();
        var doc = _vm.Document;
        _builtFor = doc;
        if (doc == null || _vm.Status != LyricsStatus.Ready || doc.Lines.Count == 0) return;
        double font = BaseFontSize * _vm.TextScale;
        bool synced = doc.IsSynced;
        foreach (var line in doc.Lines)
        {
            var align = Align(line, doc);
            LyricRow row = line.IsInterlude && _vm.Clock != null
                ? new InterludeRow(_c, _vm.Clock, line, Math.Round(font * 0.42), align)
                : new LyricLineView(_c, _vm, line, synced ? font : font * 0.82, align) { Seekable = synced };
            if (!synced) row.ApplyStyle(0.86, 1, 0, 0, animate: false);
            else row.ApplyStyle(0.4, RestScale, 0, 0, animate: false);
            _rows.Add(row);
            _list.Children.Add(row);
        }
        if (!string.IsNullOrEmpty(doc.Credit))
        {
            var credit = new CreditRow(_c, doc.Credit);
            _rows.Add(credit);
            _list.Children.Add(credit);
        }
        _laidOut = false;
        _manual = 0;
        _follow = true;
        _backShow.SetTarget(0, Motion.FadeOut);
        _back.IsHitTestVisible = false;
        ScheduleLayout();
        _listFade.SetTarget(1, Motion.FadeIn, delay: 0.05);
    }

    private static HorizontalAlignment Align(LyricLine line, LyricsDocument doc) =>
        !doc.HasDuet ? HorizontalAlignment.Left : line.Agent switch { 2 => HorizontalAlignment.Right, 3 => HorizontalAlignment.Center, _ => HorizontalAlignment.Left };

    private void ClearRows()
    {
        foreach (var r in _rows) r.Deactivate();
        _rows.Clear();
        _list.Children.Clear();
        _builtFor = null;
    }

    private void ShowEmpty(string icon, string title, string sub)
    {
        _emptyIcon.Data = icon;
        _emptyTitle.Text = title;
        _emptySub.Text = sub;
        _emptyFade.SetTarget(1, Motion.FadeIn, delay: 0.12);
    }

    private void ScheduleLayout()
    {
        _layoutTimer.Stop();
        _layoutTimer.Start();
    }

    /// <summary>After layout: first time, everything takes its place at once; after a resize, it glides.</summary>
    private void OnLayout()
    {
        if (_rows.Count == 0 || _viewport.ActualHeight <= 0) return;
        bool animate = _laidOut;
        _laidOut = true;
        Restyle(animate);
        Retarget(animate, stagger: false, Motion.LyricScroll);
    }

    // =================================================================================================================
    // Current line: style and scroll
    // =================================================================================================================
    private void OnActive()
    {
        if (_rows.Count == 0 || !_vm.HasSynced) return;
        double before = AutoTarget();
        Restyle(animate: _shown);
        if (_follow) Retarget(_shown, stagger: true, Motion.LyricScroll);
        else _manual += before - AutoTarget(); // reading elsewhere: the view stays where it is
    }

    /// <summary>Style by distance from the current line (and lit copies only near it).</summary>
    private void Restyle(bool animate)
    {
        if (!_vm.HasSynced || _vm.Document is not { } doc) return;
        int a = _vm.ActiveIndex;
        foreach (var r in _rows)
        {
            if (r is CreditRow) continue;
            int d = a >= 0 ? r.Index - a : r.Index + 1;
            var (o, s, act, blur) = StyleFor(d, r is InterludeRow);
            bool near = Math.Abs(d) <= StyleWindow;
            double delay = d > 0 && animate ? Math.Min(d * ScrollStagger, StaggerCap) : 0;
            r.ApplyStyle(o, s, act, blur, animate && near && _shown, delay);
            if (!_shown) continue;
            if (Math.Abs(d) <= LitWindow) r.Activate(_colours);
            else if (Math.Abs(d) > ReleaseWindow) r.Deactivate();
            if (blur > 0 && Math.Abs(d) <= StyleWindow) r.NeedBlur();
        }
    }

    private static (double o, double s, double act, double blur) StyleFor(int d, bool interlude)
    {
        if (interlude) return d == 0 ? (1, 1, 1, 0) : (0, 0.9, 0, 0);
        if (d == 0) return (1, 1, 1, 0);
        int n = Math.Abs(d);
        double scale = RestScale - 0.014 * Math.Min(n - 1, 4);
        if (d < 0)
            return (n == 1 ? 0.42 : Math.Max(0.16, 0.34 - 0.06 * (n - 2)), scale, 0, n >= 2 ? Math.Min(1, 0.45 * (n - 1)) : 0);
        return (n == 1 ? 0.56 : n == 2 ? 0.44 : Math.Max(0.18, 0.34 - 0.05 * (n - 3)), scale, 0, n >= 2 ? Math.Min(1, 0.38 * (n - 1)) : 0);
    }

    private double FocusY => _viewport.ActualHeight * FocusShare;

    private (double min, double max) Bounds()
    {
        if (_rows.Count == 0) return (0, 0);
        var first = _rows[0];
        var last = _rows[^1];
        double min = first.ActualOffset.Y + first.ActualHeight / 2 - FocusY;
        double max = last.ActualOffset.Y + last.ActualHeight - _viewport.ActualHeight * 0.55;
        return (min, Math.Max(min, max));
    }

    /// <summary>The scroll that puts the current line at the focus point (plain lyrics: follows the track's progress).</summary>
    private double AutoTarget()
    {
        if (_rows.Count == 0) return 0;
        if (!_vm.HasSynced)
        {
            var (min, max) = Bounds();
            double content = _rows[^1].ActualOffset.Y + _rows[^1].ActualHeight;
            double t = Math.Clamp((_vm.Progress - 0.04) / 0.92, 0, 1);
            return Math.Clamp(-FocusY * 0.4 + t * (content - _viewport.ActualHeight * 0.5), Math.Min(min, -FocusY * 0.4), max);
        }
        int a = _vm.ActiveIndex;
        var row = a >= 0 && a < _rows.Count ? _rows[a] : _rows[0];
        return row.ActualOffset.Y + row.ActualHeight / 2 - FocusY;
    }

    private void Retarget(bool animate, bool stagger, SpringPreset preset)
    {
        if (_rows.Count == 0) return;
        var (min, max) = Bounds();
        double auto = AutoTarget();
        double target = Math.Clamp(auto + _manual, Math.Min(min, auto), Math.Max(max, auto));
        double vh = _viewport.ActualHeight;
        int a = Math.Max(0, _vm.ActiveIndex);
        animate &= Anim.Enabled;
        for (int k = 0; k < _rows.Count; k++)
        {
            var r = _rows[k];
            double top = r.ActualOffset.Y, bottom = top + r.ActualHeight, now = r.Y.Value;
            bool visibleNow = bottom + now > -80 && top + now < vh + 80;
            bool visibleThen = bottom - target > -80 && top - target < vh + 80;
            if (animate && (visibleNow || visibleThen))
            {
                double delay = stagger ? Math.Clamp((k - (a - 1)) * ScrollStagger, 0, StaggerCap) : 0;
                r.Y.SetTarget(-target, preset, delay);
            }
            else r.Y.Snap(-target);
        }
    }

    // =================================================================================================================
    // Manual scrolling
    // =================================================================================================================
    private void OnWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_rows.Count == 0) return;
        e.Handled = true;
        int delta = e.GetCurrentPoint(_viewport).Properties.MouseWheelDelta;
        var (min, max) = Bounds();
        double auto = AutoTarget();
        double current = Math.Clamp(auto + _manual, Math.Min(min, auto), Math.Max(max, auto));
        double next = Math.Clamp(current - delta * 0.6, min, max);
        _manual = next - auto;
        if (_follow)
        {
            _follow = false;
            _back.IsHitTestVisible = true;
            _backShow.SetTarget(1, Motion.Pop);
        }
        _resume.Stop();
        _resume.Start();
        Retarget(animate: true, stagger: false, Motion.LyricWheel);
    }

    private void ResumeFollow()
    {
        _resume.Stop();
        if (_follow && _manual == 0) return;
        _follow = true;
        _manual = 0;
        _back.IsHitTestVisible = false;
        _backShow.SetTarget(0, Motion.FadeOut);
        Retarget(animate: true, stagger: false, Motion.LyricScroll);
    }

    private void UpdatePlainTimer()
    {
        bool want = _shown && _vm.Status == LyricsStatus.Ready && _vm.Document is { IsSynced: false } && _vm.IsPlaying;
        if (want && !_plainTimer.IsRunning) _plainTimer.Start();
        else if (!want && _plainTimer.IsRunning) _plainTimer.Stop();
    }

    // =================================================================================================================
    // Messages
    // =================================================================================================================
    private void ShowMessage(string text)
    {
        if (!_shown) return;
        _toastText.Text = text;
        _toastShow.Snap(Math.Min(_toastShow.Value, 0.6));
        _toastShow.SetTarget(1, Motion.Pop);
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromMilliseconds(text.Length > 24 ? 2800 : 1600);
        _toastTimer.Start();
    }

    // =================================================================================================================
    // Loading shimmer and the empty state
    // =================================================================================================================
    private void BuildShimmer()
    {
        _shimmer.IsHitTestVisible = false;
        _shimmer.VerticalAlignment = VerticalAlignment.Center;
        _shimmer.Margin = new Thickness(10, 0, 10, 0);
        var stack = new StackPanel { Spacing = 12 };
        double[] widths = { 0.78, 0.92, 0.6, 0.84 };
        for (int i = 0; i < widths.Length; i++)
        {
            var bar = new Grid { Height = 14, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF)) };
            double share = widths[i];
            stack.SizeChanged += (_, e) => bar.Width = Math.Max(40, e.NewSize.Width * share);
            var clip = new RectangleGeometry();
            bar.Clip = clip;
            bar.SizeChanged += (_, e) => clip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
            // A soft highlight sweeping across, each bar a little after the one above.
            var sweep = new Border
            {
                Width = 120, HorizontalAlignment = HorizontalAlignment.Left,
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 0),
                    GradientStops =
                    {
                        new GradientStop { Offset = 0, Color = Color.FromArgb(0, 0xFF, 0xFF, 0xFF) },
                        new GradientStop { Offset = 0.5, Color = Color.FromArgb(0x2C, 0xFF, 0xFF, 0xFF) },
                        new GradientStop { Offset = 1, Color = Color.FromArgb(0, 0xFF, 0xFF, 0xFF) },
                    },
                },
            };
            bar.Children.Add(sweep);
            int delayMs = i * 90;
            bar.Loaded += (_, _) =>
            {
                var v = Anim.VisualOf(sweep, translation: true);
                var a = _c.CreateScalarKeyFrameAnimation();
                a.InsertKeyFrame(0, -130);
                a.InsertKeyFrame(1, 420, _c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(0.6f, 1)));
                a.Duration = TimeSpan.FromMilliseconds(1300);
                a.DelayTime = TimeSpan.FromMilliseconds(delayMs);
                a.IterationBehavior = AnimationIterationBehavior.Forever;
                v.StartAnimation("Translation.X", a);
            };
            stack.Children.Add(bar);
        }
        _shimmer.Children.Add(stack);
        Children.Add(_shimmer);
    }

    private void BuildEmpty()
    {
        _empty.IsHitTestVisible = false;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4 };
        _emptyIcon.Size = 26;
        _emptyIcon.Foreground = Theme.TertiaryText;
        _emptyIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyIcon.Margin = new Thickness(0, 0, 0, 6);
        stack.Children.Add(_emptyIcon);
        _emptyTitle.Style = (Style)Application.Current.Resources["BaseText"];
        _emptyTitle.FontFamily = Theme.FontSemiBold;
        _emptyTitle.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _emptyTitle.Foreground = Theme.SecondaryText;
        _emptyTitle.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(_emptyTitle);
        _emptySub.Style = (Style)Application.Current.Resources["CaptionText"];
        _emptySub.HorizontalAlignment = HorizontalAlignment.Center;
        _emptySub.TextAlignment = TextAlignment.Center;
        stack.Children.Add(_emptySub);
        _empty.Children.Add(stack);
        Children.Add(_empty);
    }
}
