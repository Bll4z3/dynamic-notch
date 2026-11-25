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
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DynamicNotch.Views.Lyrics;

/// <summary>
/// The current lyric line in the closed notch, centred, its words lighting up as they're sung (the same fill and glow
/// as the panel, scaled to fit). When the line changes the old one lifts and fades away while the new one rises in,
/// blurred to sharp; the notch's width springs to fit each line (within <see cref="MaxNotchWidth"/>). A line too long
/// for that glides sideways to keep the sung word in view, its ends fading into the notch. Short instrumental breaks
/// show the three dots.
/// </summary>
public sealed class CollapsedLyricView : Grid
{
    // ---- Tuning ----
    public const double FontSize = 13.5;
    /// <summary>Space between the text and the notch's ends, and the widest the notch gets for a line.</summary>
    public const double SidePadding = 24, MaxNotchWidth = 540;
    private const double Rise = 9;          // how far lines rise in / lift away (DIPs)
    private const double EdgeFade = 22;     // the soft fade at the ends of an overflowing line

    private readonly LyricsViewModel _vm;
    private readonly Compositor _c;
    private readonly CompositionPropertySet _notch;
    private readonly Slot _a, _b;
    private Slot _front;
    private readonly Rectangle _fadeL = new(), _fadeR = new();
    private readonly CompSpring _fade;
    private readonly DispatcherQueueTimer _cleanup;
    private Slot? _leaving;
    private LyricColours _colours = LyricColours.Default;
    private int _item = -2;
    private LyricsDocument? _doc;

    /// <summary>The notch width this line wants (the window springs the closed notch to it).</summary>
    public double DesiredWidth { get; private set; } = Theme.CollapsedWidth;

    private sealed class Slot
    {
        public required Grid Host;
        public required CompositionPropertySet P;  // "A" (always 1: the current line), "Bm" (blur), "St" (0 below, 1 shown, 2 above), "X" glide
        public required CompSpring St, Bm, X;
        public readonly List<LyricWordView> Words = new();
        public WordFlow? Flow;
        public LyricLine? Line;
        public double TextWidth;
    }

    public CollapsedLyricView(LyricsViewModel vm, Compositor c, CompositionPropertySet notch)
    {
        _vm = vm;
        _c = c;
        _notch = notch;
        Width = MaxNotchWidth;
        Height = Theme.CollapsedHeight;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Top;
        IsHitTestVisible = false;
        _a = NewSlot();
        _b = NewSlot();
        _front = _a;

        // Soft ends for a line that overflows: the notch is pure black, so a black-to-clear gradient fades the text out.
        foreach (var (r, left) in new[] { (_fadeL, true), (_fadeR, false) })
        {
            r.Width = EdgeFade;
            r.Height = Theme.CollapsedHeight - 4;
            r.VerticalAlignment = VerticalAlignment.Top;
            r.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            r.Fill = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(left ? 0 : 1, 0), EndPoint = new Windows.Foundation.Point(left ? 1 : 0, 0),
                GradientStops = { new GradientStop { Offset = 0, Color = Theme.NotchColor }, new GradientStop { Offset = 1, Color = Color.FromArgb(0, 0, 0, 0) } },
            };
            r.IsHitTestVisible = false;
            Children.Add(r);
            var v = Anim.VisualOf(r, translation: true);
            // At the body's ends (they move with the notch width), a little inside its rounded corners.
            var t = Anim.Expr(c, $"Vector3({(left ? "" : "-")}((MW - N.W) / 2 + 12), 0, 0)", ("N", notch));
            t.SetScalarParameter("MW", (float)MaxNotchWidth);
            v.StartAnimation("Translation", t);
        }
        var (fade, fadeSet) = Motions.Value(c, Motion.FadeIn, 0);
        _fade = fade;
        foreach (var r in new[] { _fadeL, _fadeR }) Anim.VisualOf(r).StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", fadeSet)));

        _cleanup = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _cleanup.IsRepeating = false;
        _cleanup.Interval = TimeSpan.FromMilliseconds(520);
        _cleanup.Tick += (_, _) =>
        {
            _cleanup.Stop();
            try { if (_leaving != null && !ReferenceEquals(_leaving, _front)) Clear(_leaving); }
            catch (Exception ex) { App.LogError(ex); } // a timer callback must never throw (it would end the app)
            _leaving = null;
        };

        _vm.ActiveWordChanged += OnWord;
        _vm.DisplayChanged += () => Refresh();
        _vm.DocumentChanged += () => { if (!ReferenceEquals(_doc, _vm.Document)) Refresh(); };
    }

    private Slot NewSlot()
    {
        var host = new Grid { IsHitTestVisible = false };
        Children.Insert(0, host);
        var p = _c.CreatePropertySet();
        p.InsertScalar("A", 1);
        var slot = new Slot
        {
            Host = host, P = p,
            St = new CompSpring(p, "St", Motion.LyricSwap, 0),
            Bm = new CompSpring(p, "Bm", Motion.LyricSwap, 0),
            X = new CompSpring(p, "X", Motion.LyricScroll, 0, precision: 40),
        };
        var v = Anim.VisualOf(host, translation: true);
        v.StartAnimation("Opacity", Anim.Expr(_c, "Clamp(1 - Abs(1 - P.St), 0, 1)", ("P", p)));
        var tr = Anim.Expr(_c, "Vector3(-P.X, (1 - P.St) * R, 0)", ("P", p));
        tr.SetScalarParameter("R", (float)Rise);
        v.StartAnimation("Translation", tr);
        v.StartAnimation("Scale", Anim.Expr(_c, "Vector3(1 - 0.04 * Clamp(Abs(1 - P.St), 0, 1), 1 - 0.04 * Clamp(Abs(1 - P.St), 0, 1), 1)", ("P", p)));
        host.SizeChanged += (_, e) => v.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
        return slot;
    }

    /// <summary>The album colours the words light up in.</summary>
    public void SetPalette(GlowPalette? palette)
    {
        _colours = LyricColours.From(palette);
        foreach (var w in _a.Words.Concat(_b.Words)) if (w.IsActive) w.Recolour(_colours);
    }

    /// <summary>Shows a document item (<see cref="LyricsViewModel.CollapsedItem"/>; -1 = nothing). True when the width changed.</summary>
    public bool Show(int item)
    {
        var doc = _vm.Document;
        if (item == _item && ReferenceEquals(doc, _doc)) return false;
        _item = item;
        _doc = doc;
        double before = DesiredWidth;
        var outgoing = _front;
        if (outgoing.Line != null || outgoing.Host.Children.Count > 0)
        {
            outgoing.St.SetTarget(2, Motion.LyricSwap);
            _leaving = outgoing;
            _cleanup.Stop();
            _cleanup.Start();
        }
        if (item < 0 || doc == null || item >= doc.Lines.Count)
        {
            _front = outgoing;
            _fade.SetTarget(0, Motion.FadeOut);
            return false;
        }
        var incoming = ReferenceEquals(outgoing, _a) ? _b : _a;
        if (ReferenceEquals(incoming, _leaving)) { _cleanup.Stop(); _leaving = null; }
        Clear(incoming);
        _front = incoming;
        Build(incoming, doc.Lines[item]);
        incoming.St.Snap(0);
        incoming.St.SetTarget(1, Motion.LyricSwap);
        if (incoming.Words.Count > 0) { incoming.Bm.Snap(1); incoming.Bm.SetTarget(0, Motion.LyricSwap); }
        return Math.Abs(DesiredWidth - before) > 0.5;
    }

    /// <summary>Rebuilds what's showing (text size, word-by-word, a new document).</summary>
    private void Refresh()
    {
        int item = _item;
        _item = -2;
        if (item >= 0) Show(_vm.CollapsedItem);
    }

    private void Build(Slot slot, LyricLine line)
    {
        slot.Line = line;
        slot.X.Snap(0);
        if (line.IsInterlude && _vm.Clock != null)
        {
            var dots = new InterludeRow(_c, _vm.Clock, line, 5, HorizontalAlignment.Center) { Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            dots.ApplyStyle(1, 1, 1, 0, animate: false);
            slot.Host.Children.Add(dots);
            slot.TextWidth = 40;
            DesiredWidth = Theme.CollapsedWidth;
            _fade.SetTarget(0, Motion.FadeOut);
            return;
        }
        var flow = new WordFlow { NoWrap = true, Align = HorizontalAlignment.Center, SpaceWidth = Math.Round(FontSize * 0.28), VerticalAlignment = VerticalAlignment.Center };
        for (int i = 0; i < line.Words.Count; i++)
        {
            var w = line.Words[i];
            bool backing = w.Backing || line.IsBacking;
            var view = new LyricWordView(w, i, backing ? Theme.FontMedium : Theme.FontSemiBold, backing ? FontSize * 0.86 : FontSize, backing);
            slot.Words.Add(view);
            flow.Children.Add(view);
        }
        slot.Flow = flow;
        slot.Host.Children.Add(flow);
        flow.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        slot.TextWidth = flow.NaturalWidth;
        double maxText = MaxNotchWidth - 2 * SidePadding;
        bool overflow = slot.TextWidth > maxText;
        DesiredWidth = Math.Clamp(slot.TextWidth + 2 * SidePadding, Theme.CollapsedWidth, MaxNotchWidth);
        // An overflowing line starts at the left end and glides; a fitting one stays centred.
        flow.HorizontalAlignment = overflow ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        flow.Margin = overflow ? new Thickness(SidePadding, 0, 0, 0) : new Thickness(0);
        flow.Width = overflow ? slot.TextWidth + 2 : double.NaN;
        _fade.SetTarget(overflow ? 1 : 0, overflow ? Motion.FadeIn : Motion.FadeOut);
        if (_vm.Clock is { } clock && _vm.TimesFor(line) is { } times)
        {
            foreach (var w in slot.Words) w.BindBase(clock, times, slot.P);
            void Activate()
            {
                foreach (var w in slot.Words) { w.Activate(_colours); w.EnsureSoft(); }
            }
            if (slot.Words.Count > 0 && slot.Words[0].ActualWidth > 0) Activate();
            else
            {
                void OnSize(object s, SizeChangedEventArgs e) { flow.SizeChanged -= OnSize; if (ReferenceEquals(slot.Line, line)) Activate(); }
                flow.SizeChanged += OnSize;
            }
        }
    }

    private void Clear(Slot slot)
    {
        foreach (var w in slot.Words) w.Deactivate();
        slot.Words.Clear();
        slot.Host.Children.Clear();
        slot.Flow = null;
        slot.Line = null;
        slot.TextWidth = 0;
    }

    /// <summary>A long line glides so the word being sung stays in view (a little left of centre).</summary>
    private void OnWord(int line, int word)
    {
        var slot = _front;
        if (slot.Line == null || slot.Line.Index != line || slot.Flow == null) return;
        double visible = MaxNotchWidth - 2 * SidePadding;
        if (slot.TextWidth <= visible || word < 0 || word >= slot.Words.Count) return;
        var w = slot.Words[word];
        double centre = w.ActualOffset.X + w.ActualWidth / 2;
        double target = Math.Clamp(centre - visible * 0.42, 0, slot.TextWidth - visible);
        slot.X.SetTarget(target, Motion.LyricScroll);
    }
}
