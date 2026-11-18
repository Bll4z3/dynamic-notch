using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.Views;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace DynamicNotch;

/// <summary>
/// Stage 2 of the notch window: the open notch (header, pages, footer, glow) and the shared-element
/// flights between the collapsed notch and the open one (battery ring, clock, album art). The flights
/// are compositor expressions over the morph progress, the page spring and each pair's measured layout
/// rectangles, so the elements glide with the shape at the display's refresh rate.
/// </summary>
public sealed partial class NotchWindow
{
    private ExpandedFrame _frame = null!;
    private readonly List<Matched> _matched = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _centresTimer;

    /// <summary>A collapsed element and its expanded twin that fly between each other as the notch morphs.</summary>
    private sealed class Matched
    {
        public required FrameworkElement Collapsed, Expanded;
        /// <summary>Layout rectangles (window DIPs) and flags the expressions read: ax/ay/aw/ah, bx/by/bw/bh,
        /// Active (state allows it), Near (page index, -1 none), Want (1 = only near that page, 0 = except near it).</summary>
        public required CompositionPropertySet M;
        public Func<bool> Allowed = () => true;
        public Func<int> PageIndex = () => -1;
    }

    /// <summary>Dev: the navigator's icon state without a hover.</summary>
    public void ShowNavigatorIcons() => _frame.Navigator.ShowIcons();
    /// <summary>Dev: the Notifications + Controls page (for --perf-popovers).</summary>
    internal Views.HubPage DevHub => _frame.Hub;

    private void BuildExpanded()
    {
        _frame = new ExpandedFrame(Vm, _shape.Props) { HorizontalAlignment = HorizontalAlignment.Center };
        ExpandedLayer.Children.Add(_frame);

        // The header ring/time only exist on pages 1-2 (the Ask page gets its own header in stage 4).
        AddMatched(NotchRing, _frame.HeaderRing, edge: +1, parentShifted: false, want: 0,
            page: () => Vm.Pages.IndexOf(PageId.Ask), allowed: () => true);
        AddMatched(CollapsedTime, _frame.HeaderTime, edge: 0, parentShifted: false, want: 0,
            page: () => Vm.Pages.IndexOf(PageId.Ask), allowed: () => true);
        AddMatched(MiniArt, _frame.Music.ArtElement, edge: -1, parentShifted: true, want: 1,
            page: () => Vm.Pages.IndexOf(PageId.Music), allowed: () => Vm.Music.IsPlaying && !Vm.MinimalNotch && !CompactTimerShown, pageScrolled: true);
        UpdateMatchedFlags();
    }

    /// <param name="edge">+1 = the collapsed element hugs the left body edge, -1 = right edge, 0 = centred.</param>
    /// <param name="parentShifted">True when the collapsed element's parent already carries the right-edge shift.</param>
    /// <param name="want">1 = the pair flies only near <paramref name="page"/>; 0 = it flies except near it.</param>
    /// <param name="pageScrolled">True when the expanded element sits in the pager (moves with the page spring).</param>
    private void AddMatched(FrameworkElement collapsed, FrameworkElement expanded, int edge, bool parentShifted, int want,
                            Func<int> page, Func<bool> allowed, bool pageScrolled = false)
    {
        var c = _shape.Compositor;
        var m = c.CreatePropertySet();
        foreach (var k in new[] { "ax", "ay", "bx", "by" }) m.InsertScalar(k, 0);
        foreach (var k in new[] { "aw", "ah", "bw", "bh" }) m.InsertScalar(k, 1);
        m.InsertScalar("Active", 0);
        m.InsertScalar("Near", -1);
        m.InsertScalar("Want", want);
        var pair = new Matched { Collapsed = collapsed, Expanded = expanded, M = m, Allowed = allowed, PageIndex = page };
        _matched.Add(pair);

        // Intermediate values, each a chained expression on the pair's own property set (one long
        // inline expression is more than the compositor accepts).
        string CH = Anim.F(Theme.CollapsedHeight), CPW = Anim.F(Theme.CollapsedPlayingWidth);
        foreach (var k in new[] { "p", "es", "onPage", "act", "t", "acx", "acy", "bcx", "bcy", "ch", "ccx", "ccy" }) m.InsertScalar(k, 0);
        Chain(m, "p", $"Clamp((N.H - {CH}) / Max(1, N.Full - {CH}), 0, 1)");
        Chain(m, "es", $"({CPW} - N.W) / 2");
        Chain(m, "onPage", "M.Near >= 0 ? (Abs(N.Page - M.Near) < 0.5 ? 1 : 0) : 0");
        Chain(m, "act", "M.Active * (M.Want * M.onPage + (1 - M.Want) * (1 - M.onPage))");
        Chain(m, "t", "M.act * M.p + (1 - M.act)"); // inactive: the expanded element rests in place
        Chain(m, "acx", $"M.ax + M.aw / 2 + {edge} * M.es");
        Chain(m, "acy", "M.ay + M.ah / 2");
        Chain(m, "bcx", pageScrolled ? "M.bx + M.bw / 2 - N.Page * N.Slot" : "M.bx + M.bw / 2");
        Chain(m, "bcy", "M.by + M.bh / 2");
        Chain(m, "ch", "M.ah + (M.bh - M.ah) * M.t");
        Chain(m, "ccx", "M.acx + (M.bcx - M.acx) * M.t");
        Chain(m, "ccy", "M.acy + (M.bcy - M.acy) * M.t");
        string parent = parentShifted ? " + M.es" : ""; // undo the right group's own shift (-es)

        // Collapsed element: follows the edge and flies toward the expanded slot, fading early.
        Motions.CentreOrigin(collapsed);
        string cs = "(M.act * M.ch / M.ah + (1 - M.act))";
        Bind(collapsed, "Scale", $"Vector3({cs}, {cs}, 1)", m, translation: false);
        Bind(collapsed, "Translation", $"Vector3(M.act * M.ccx + (1 - M.act) * M.acx - (M.ax + M.aw / 2){parent}, M.act * (M.ccy - M.acy), 0)", m, translation: true);
        Bind(collapsed, "Opacity", $"M.act * (1 - {Anim.SmoothStep(0, 0.45, "M.p")}) + (1 - M.act)", m, translation: false);

        // Expanded element: starts at the collapsed element's spot and size, settles in place.
        Motions.CentreOrigin(expanded);
        string bs = "(M.act * M.ch / M.bh + (1 - M.act))";
        Bind(expanded, "Scale", $"Vector3({bs}, {bs}, 1)", m, translation: false);
        Bind(expanded, "Translation", "Vector3(M.act * (M.ccx - M.bcx), M.act * (M.ccy - M.bcy), 0)", m, translation: true);
        Bind(expanded, "Opacity", Anim.SmoothStep(Motion.MatchedFadeFrom, Motion.MatchedFadeTo, "M.p"), m, translation: false);

        // Re-measure whenever either side's layout changes (e.g. the clock text width).
        collapsed.SizeChanged += (_, _) => Measure(pair);
        expanded.SizeChanged += (_, _) => Measure(pair);
        collapsed.Loaded += (_, _) => Measure(pair);
        expanded.Loaded += (_, _) => Measure(pair);
    }

    private void Chain(CompositionPropertySet m, string name, string expression)
    {
        var e = _shape.Compositor.CreateExpressionAnimation(expression);
        e.SetReferenceParameter("N", _shape.Props);
        e.SetReferenceParameter("M", m);
        m.StartAnimation(name, e);
    }

    private void Bind(UIElement el, string property, string expression, CompositionPropertySet m, bool translation)
    {
        var e = _shape.Compositor.CreateExpressionAnimation(expression);
        e.SetReferenceParameter("N", _shape.Props);
        e.SetReferenceParameter("M", m);
        try { Anim.VisualOf(el, translation).StartAnimation(property, e); }
        catch (Exception ex) { App.LogError(new InvalidOperationException($"{property}: {expression}", ex)); }
    }

    /// <summary>Layout rectangles in window DIPs, ignoring compositor transforms (they aren't part of XAML layout).</summary>
    private void Measure(Matched pair)
    {
        void Put(FrameworkElement el, string x, string y, string w, string h)
        {
            if (el.ActualWidth <= 0 || el.ActualHeight <= 0 || el.XamlRoot == null) return;
            Point o;
            try { o = el.TransformToVisual(Root).TransformPoint(new Point(0, 0)); } catch { return; }
            pair.M.InsertScalar(x, (float)o.X);
            pair.M.InsertScalar(y, (float)o.Y);
            pair.M.InsertScalar(w, (float)el.ActualWidth);
            pair.M.InsertScalar(h, (float)el.ActualHeight);
        }
        Put(pair.Collapsed, "ax", "ay", "aw", "ah");
        Put(pair.Expanded, "bx", "by", "bw", "bh");
    }

    /// <summary>Shared elements only travel between collapsed and expanded (never to a pill), on the right pages.</summary>
    private void UpdateMatchedFlags()
    {
        foreach (var pair in _matched)
        {
            pair.M.InsertScalar("Active", _state is not (NotchState.Pill or NotchState.Activity) && pair.Allowed() ? 1 : 0);
            pair.M.InsertScalar("Near", pair.PageIndex());
        }
    }

    // ---- Album glow ------------------------------------------------------------------------------
    /// <summary>The current song's extracted colour (null without artwork), for "Follow album colour".</summary>
    private Windows.UI.Color? _albumColor;

    private void UpdateGlowColor(bool animate)
    {
        var album = AlbumColorService.Extract(Vm.Music.Artwork);
        _albumColor = Vm.Music.Artwork != null ? album : null;
        _frame.Glow.SetColor(GlowColorFor(album), animate);
        _theme?.SetAlbumColor(_albumColor, _state == NotchState.Expanded && Vm.IsMusicPage);
    }

    /// <summary>Brighter while playing, dimmed while paused; the breathing pulse only while open and playing (never in Low power).</summary>
    private void UpdateGlowLevel()
    {
        bool open = _state == NotchState.Expanded;
        bool playing = Vm.Music.IsPlaying;
        double level = !open || Vm.Pages.IndexOf(PageId.Music) < 0 ? 0 : playing ? Theme.GlowMaxOpacity : Theme.GlowPausedOpacity;
        _frame.Glow.SetLevel(level * GlowScale);
        _frame.Glow.SetPulsing(open && playing && GlowScale > 0 && !Motion.LowPower);
    }

    /// <summary>Profile mode: confirm the music details column is centred on the cover (logged to frames.log).</summary>
    private void LogMusicCentres()
    {
        if (Environment.GetEnvironmentVariable("DYNAMICNOTCH_PROFILE") != "1") return;
        var t = _centresTimer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        t.IsRepeating = false;
        t.Interval = TimeSpan.FromMilliseconds(1500);
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_state != NotchState.Expanded || !Vm.IsMusicPage) return;
            var (cover, details) = _frame.Music.MeasureCentres();
            try
            {
                File.AppendAllText(Path.Combine(SettingsService.DataFolder, "frames.log"),
                    $"[{DateTime.Now:HH:mm:ss}] [WinUI] music centres: cover={cover:F2} details={details:F2} diff={details - cover:F2} DIP{Environment.NewLine}");
            }
            catch { }
        };
        t.Start();
    }
}
