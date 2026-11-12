using System.ComponentModel;
using System.Numerics;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// Focus timer page. Left: the phase label ("Focus · 2 of 4"), a large ticking countdown, the clock time
/// the phase ends ("10:27 until done"), a thin gradient progress bar and the Pause / Resume pill with a
/// round reset button. Right: a "This round" card with one segment per interval (short breaks are dots),
/// numbered underneath, and the round's total length. Progress glides on the compositor between UI
/// updates; the digits roll per character; a phase change dips the digits and flashes a glow.
/// </summary>
public sealed class FocusPage : Grid
{
    private readonly FocusTimerViewModel _vm;
    private readonly Grid _left = new(), _right = new(), _segments = new(), _digitsHost = new();
    private readonly StackPanel _controls = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly Ellipse _phaseDot = new() { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
    private readonly CrossfadeText _label = new();
    private readonly TickerText _digits = new();
    private readonly Run _endTime = new(), _until = new();
    private readonly TextBlock _summary = new(), _roundLeft = new();
    private readonly SolidColorBrush _accent = new(Theme.FocusB), _dotBrush = new(Theme.FocusB);
    private readonly GradientBar _bar = new(Theme.MeterBarThickness);
    private readonly PillButton _toggle = new(Icons.Play, "Start");
    private readonly IconButton _reset = new(Theme.PillButtonHeight, Theme.IconSmall);
    private readonly List<Segment> _segs = new();
    private readonly Border _glowHost = new() { IsHitTestVisible = false };
    private CompositionColorGradientStop? _glowStop, _glowMid, _glowEdge;
    private CompSpring? _dip, _glow;
    private IReadOnlyList<FocusInterval>? _builtFor;

    public FocusPage(FocusTimerViewModel vm)
    {
        _vm = vm;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = Theme.GapS5 });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Theme.TimerPanelWidth) });

        // ---- Left: label · digits · end time · progress ----
        foreach (var h in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            _left.RowDefinitions.Add(new RowDefinition { Height = h });
        _left.VerticalAlignment = VerticalAlignment.Top;
        _left.Margin = new Thickness(Theme.S1, Theme.S1, 0, 0);
        var labelRow = new StackPanel { Orientation = Orientation.Horizontal };
        _phaseDot.Fill = _dotBrush;
        labelRow.Children.Add(_phaseDot);
        _label.SetStyle(t => { t.Style = StyleOf("BaseText"); t.FontFamily = Theme.FontSemiBold; t.FontWeight = FontWeights.SemiBold; t.Foreground = Theme.SecondaryText; });
        _label.Margin = Theme.GapLeftS2;
        _label.VerticalAlignment = VerticalAlignment.Center;
        labelRow.Children.Add(_label);
        _left.Children.Add(labelRow);

        // Fades out before the page edge (the pager clips anything past it).
        _glowHost.Width = 230;
        _glowHost.Height = 120;
        _glowHost.HorizontalAlignment = HorizontalAlignment.Left;
        _glowHost.VerticalAlignment = VerticalAlignment.Center;
        _glowHost.Margin = new Thickness(-14, -30, 0, -30);
        _digitsHost.Children.Add(_glowHost);
        _digits.CellStyle = t =>
        {
            t.Style = StyleOf("TimeText");
            t.FontSize = Theme.FontTimerDigits;
            t.Foreground = Theme.PrimaryText;
        };
        _digits.HorizontalAlignment = HorizontalAlignment.Left;
        _digitsHost.Children.Add(_digits);
        _digitsHost.Margin = new Thickness(-2, Theme.S1, 0, 0);
        _digitsHost.HorizontalAlignment = HorizontalAlignment.Left;
        SetRow(_digitsHost, 1);
        _left.Children.Add(_digitsHost);

        // One text line, two runs: "10:27 " in grey, "until done" in the phase colour.
        var until = new TextBlock
        {
            Style = StyleOf("SecondaryText"), FontSize = Theme.FontPillTitle, FontFamily = Theme.FontSemiBold,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, -2, 0, 0),
        };
        Typography.SetNumeralAlignment(until, FontNumeralAlignment.Tabular);
        _until.Foreground = _accent;
        until.Inlines.Add(_endTime);
        until.Inlines.Add(_until);
        SetRow(until, 2);
        _left.Children.Add(until);

        _bar.Margin = new Thickness(0, Theme.S3, Theme.S2, 0);
        SetRow(_bar, 3);
        _left.Children.Add(_bar);
        Children.Add(_left);

        // ---- Controls (bottom left) ----
        _toggle.Click += _vm.Toggle;
        _reset.Icon = Icons.Retry;
        _reset.Foreground = Theme.PrimaryText;
        _reset.Backing = Theme.QuietPillBrush;
        _reset.Tooltip = "Reset round";
        _reset.Margin = Theme.GapLeftS2;
        _reset.Click += _vm.Reset;
        _controls.Children.Add(_toggle);
        _controls.Children.Add(_reset);
        _controls.Margin = new Thickness(Theme.S1, 0, 0, Theme.S1);
        Children.Add(_controls);

        // ---- Right: "This round" ----
        _right.Background = Theme.CardBrush;
        _right.BorderBrush = Theme.CardBorderBrush;
        _right.BorderThickness = new Thickness(1);
        _right.CornerRadius = Theme.Radius;
        _right.Padding = new Thickness(Theme.S4 + 2, Theme.S4, Theme.S4 + 2, Theme.S4);
        foreach (var h in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
            _right.RowDefinitions.Add(new RowDefinition { Height = h });
        var head = new Grid();
        head.Children.Add(new TextBlock { Text = "This round", Style = StyleOf("SectionText") });
        _roundLeft.Style = StyleOf("CaptionText");
        _roundLeft.HorizontalAlignment = HorizontalAlignment.Right;
        _roundLeft.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(_roundLeft);
        _right.Children.Add(head);
        _segments.VerticalAlignment = VerticalAlignment.Center;
        _segments.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Theme.TimerLeadDot + 8) });
        _segments.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        SetRow(_segments, 1);
        _right.Children.Add(_segments);
        _summary.Style = StyleOf("CaptionText");
        _summary.Foreground = Theme.SecondaryText;
        SetRow(_summary, 2);
        _right.Children.Add(_summary);
        SetColumn(_right, 2);
        Children.Add(_right);

        Loaded += (_, _) => BuildMotion();
        vm.PropertyChanged += OnVmChanged;
        vm.PhaseEnded += (_, _) => PlayCue();
        Refresh();
    }

    /// <summary>Blocks for the notch's staggered entrance.</summary>
    public IEnumerable<(FrameworkElement element, int order)> StaggerBlocks =>
        new (FrameworkElement, int)[] { (_left, 1), (_right, 2), (_controls, 3) };

    private static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    private void BuildMotion()
    {
        if (_dip != null) return;
        var c = Anim.VisualOf(_digitsHost).Compositor;
        // Digits dip + spring back on a phase change.
        var (dip, set) = Motions.Value(c, Motion.PhaseChange, 1);
        _dip = dip;
        Motions.CentreOrigin(_digits);
        var dv = Anim.VisualOf(_digits);
        dv.StartAnimation("Scale", Anim.Expr(c, "Vector3(p.v, p.v, 1)", ("p", set)));
        dv.StartAnimation("Opacity", Anim.Expr(c, "Clamp(0.4 + 0.6 * (p.v - 0.86) / 0.14, 0, 1)", ("p", set)));

        // Soft glow behind the digits in the phase colour (brighter while running, flashes on phase change).
        var radial = c.CreateRadialGradientBrush();
        _glowStop = c.CreateColorGradientStop(0, GradientRing.WithAlpha(Theme.FocusB, 0x90));
        _glowEdge = c.CreateColorGradientStop(1, GradientRing.WithAlpha(Theme.FocusB, 0));
        _glowMid = c.CreateColorGradientStop(0.45f, GradientRing.WithAlpha(Theme.FocusB, 0x38));
        radial.ColorStops.Add(_glowStop);
        radial.ColorStops.Add(_glowMid);
        radial.ColorStops.Add(_glowEdge);
        var glow = c.CreateSpriteVisual();
        glow.Size = new Vector2((float)_glowHost.Width, (float)_glowHost.Height);
        glow.Brush = radial;
        var gp = c.CreatePropertySet();
        _glow = new CompSpring(gp, "g", Motion.GlowDim, 0);
        glow.StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.g, 0, 1)", ("p", gp)));
        ElementCompositionPreview.SetElementChildVisual(_glowHost, glow);
        Refresh();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FocusTimerViewModel.Remaining)) UpdateTexts();
        else Refresh();
    }

    private double GlowLevel => _vm.Status switch
    {
        TimerStatus.Running => 0.32,
        TimerStatus.Paused or TimerStatus.Waiting => 0.14,
        _ => 0.08,
    };

    private void PlayCue()
    {
        if (_dip == null || _glow == null) return;
        _dip.Snap(0.86);
        _dip.SetTarget(1, Motion.PhaseChange);
        _glow.Snap(1);
        _glow.SetTarget(GlowLevel, Motion.GlowCrossfade);
    }

    private void Refresh()
    {
        var cur = _vm.Current;
        var (a, b) = FocusRoutine.Colors(cur.Kind);
        _accent.Color = b;
        _dotBrush.Color = b;
        _bar.SetColors(a, b);
        if (_glowStop != null)
        {
            _glowStop.Color = GradientRing.WithAlpha(b, 0x90);
            _glowMid!.Color = GradientRing.WithAlpha(b, 0x38);
            _glowEdge!.Color = GradientRing.WithAlpha(b, 0);
        }
        _glow?.SetTarget(GlowLevel, Motion.GlowDim);
        _label.Text = _vm.PhaseLabel;

        // Main progress: glide on the compositor while running; hold still otherwise.
        double p = _vm.Progress;
        if (_vm.Status == TimerStatus.Running) _bar.SetLinear(p, 1, _vm.Remaining);
        else _bar.SetValue(p);

        (string icon, string text) = _vm.Status switch
        {
            TimerStatus.Running => (Icons.Pause, "Pause"),
            TimerStatus.Paused => (Icons.Play, "Resume"),
            TimerStatus.Waiting => (Icons.Play, "Start " + FocusRoutine.Name(cur.Kind).ToLowerInvariant()),
            TimerStatus.Done => (Icons.Play, "Start again"),
            _ => (Icons.Play, "Start"),
        };
        _toggle.SetContent(icon, text);
        _reset.Enabled = _vm.Status != TimerStatus.Idle;

        if (!ReferenceEquals(_builtFor, _vm.Round)) BuildSegments();
        UpdateSegments();
        _summary.Text = FocusRoutine.Summary(_vm.Round) + (_vm.RoutinePending ? " · edits apply next phase" : "");
        UpdateTexts();
    }

    private void UpdateTexts()
    {
        _digits.Text = FocusRoutine.Digits(_vm.Remaining);
        switch (_vm.Status)
        {
            case TimerStatus.Running:
                _endTime.Text = ClockService.FormatTime((DateTimeOffset.Now + _vm.Remaining).LocalDateTime) + " ";
                _until.Text = "until done";
                break;
            case TimerStatus.Paused: _endTime.Text = ""; _until.Text = "Paused"; break;
            case TimerStatus.Waiting: _endTime.Text = "Up next · "; _until.Text = FocusRoutine.Name(_vm.Current.Kind); break;
            case TimerStatus.Done: _endTime.Text = "Round complete · "; _until.Text = "nice work"; break;
            default: _endTime.Text = FocusRoutine.Duration(_vm.Current.Duration) + " · "; _until.Text = "ready when you are"; break;
        }
        // Whole round: what's left (minutes are enough here).
        var left = _vm.Remaining + TimeSpan.FromMinutes(_vm.Round.Skip(_vm.Index + 1).Sum(i => i.Minutes));
        _roundLeft.Text = _vm.Status == TimerStatus.Done ? "Done" : _vm.Status == TimerStatus.Idle ? "" : FocusRoutine.Duration(left) + " left";
    }

    // ---- Segments --------------------------------------------------------------------------------------
    private sealed class Segment
    {
        public required FocusInterval Interval;
        public GradientBar? Bar;
        public BreakDot? Dot;
        public TextBlock? Label;
    }

    private void BuildSegments()
    {
        _builtFor = _vm.Round;
        _segments.Children.Clear();
        _segments.ColumnDefinitions.Clear();
        _segs.Clear();
        int focusNo = 0;
        double half = Theme.TimerSegmentGap / 2;
        for (int i = 0; i < _vm.Round.Count; i++)
        {
            var it = _vm.Round[i];
            var seg = new Segment { Interval = it };
            if (it.Kind == IntervalKind.ShortBreak)
            {
                _segments.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                seg.Dot = new BreakDot { Margin = new Thickness(half, 0, half, 0), VerticalAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(seg.Dot, $"Short break · {it.Minutes} min");
                SetColumn(seg.Dot, i);
                _segments.Children.Add(seg.Dot);
            }
            else
            {
                _segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1, it.Minutes), GridUnitType.Star) });
                seg.Bar = new GradientBar(Theme.TimerSegmentHeight, leadingDot: true) { Margin = new Thickness(half, 0, half, 0), VerticalAlignment = VerticalAlignment.Center };
                var (a, b) = FocusRoutine.Colors(it.Kind);
                seg.Bar.SetColors(a, b, animate: false);
                SetColumn(seg.Bar, i);
                _segments.Children.Add(seg.Bar);
                seg.Label = new TextBlock
                {
                    Text = it.Kind == IntervalKind.Focus ? (++focusNo).ToString() : "Long",
                    Style = StyleOf("CaptionText"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = Theme.GapTopS1,
                };
                SetRow(seg.Label, 1);
                SetColumn(seg.Label, i);
                _segments.Children.Add(seg.Label);
                ToolTipService.SetToolTip(seg.Bar, $"{FocusRoutine.Name(it.Kind)} · {it.Minutes} min");
            }
            _segs.Add(seg);
        }
    }

    private void UpdateSegments()
    {
        var st = _vm.Status;
        bool done = st == TimerStatus.Done;
        for (int i = 0; i < _segs.Count; i++)
        {
            var s = _segs[i];
            bool completed = done || i < _vm.Index;
            bool current = !done && i == _vm.Index;
            if (s.Bar != null)
            {
                if (completed) s.Bar.SetValue(1);
                else if (!current) s.Bar.SetValue(0);
                else if (st == TimerStatus.Running) s.Bar.SetLinear(_vm.Progress, 1, _vm.Remaining);
                else s.Bar.SetValue(_vm.Progress);
                s.Bar.SetDot(current && st != TimerStatus.Idle, pulsing: current && st == TimerStatus.Running);
            }
            s.Dot?.SetState(completed ? BreakDot.State.Done : current ? (st == TimerStatus.Running ? BreakDot.State.Live : BreakDot.State.Held) : BreakDot.State.Upcoming);
            if (s.Label != null)
                s.Label.Foreground = current ? Theme.PrimaryText : completed ? Theme.SecondaryText : Theme.TertiaryText;
        }
    }

    /// <summary>A short break between focus segments: a small dot (filled when done, glowing while live).</summary>
    private sealed class BreakDot : Grid
    {
        public enum State { Upcoming, Live, Held, Done }
        private readonly Ellipse _halo = new(), _core = new();
        private readonly SolidColorBrush _coreBrush = new(Theme.TrackColor);
        private State? _state;

        public BreakDot()
        {
            Width = Height = Theme.TimerLeadDot + 6;
            _halo.Width = _halo.Height = Theme.TimerLeadDot + 6;
            _halo.Fill = new SolidColorBrush(GradientRing.WithAlpha(Theme.ShortBreakB, 0x60));
            _halo.Opacity = 0;
            _core.Width = _core.Height = Theme.TimerBreakDot + 1;
            _core.Fill = _coreBrush;
            foreach (var e in new[] { _halo, _core }) { e.HorizontalAlignment = HorizontalAlignment.Center; e.VerticalAlignment = VerticalAlignment.Center; }
            Children.Add(_halo);
            Children.Add(_core);
            Background = Theme.TransparentBrush; // tooltip target
            Motions.CentreOrigin(_halo);
        }

        public void SetState(State s)
        {
            if (_state == s) return;
            _state = s;
            _coreBrush.Color = s == State.Upcoming ? Theme.TrackColor : Theme.ShortBreakA;
            var v = Anim.VisualOf(_halo);
            v.StopAnimation("Opacity");
            v.StopAnimation("Scale");
            if (s == State.Live && Anim.Enabled)
            {
                var c = v.Compositor;
                var o = c.CreateScalarKeyFrameAnimation();
                o.InsertKeyFrame(0, 0.25f); o.InsertKeyFrame(0.5f, 1f); o.InsertKeyFrame(1, 0.25f);
                o.Duration = TimeSpan.FromSeconds(Motion.TimerPulsePeriod);
                o.IterationBehavior = AnimationIterationBehavior.Forever;
                v.StartAnimation("Opacity", o);
                var sc = c.CreateVector3KeyFrameAnimation();
                sc.InsertKeyFrame(0, new Vector3(0.7f, 0.7f, 1)); sc.InsertKeyFrame(0.5f, new Vector3(1.05f, 1.05f, 1)); sc.InsertKeyFrame(1, new Vector3(0.7f, 0.7f, 1));
                sc.Duration = o.Duration;
                sc.IterationBehavior = AnimationIterationBehavior.Forever;
                v.StartAnimation("Scale", sc);
            }
            else v.Opacity = s == State.Held ? 0.6f : 0f;
        }
    }
}

/// <summary>
/// Text that rolls per character: each character is its own <see cref="CrossfadeText"/>, so when the
/// countdown ticks only the digits that changed fade and slide (e.g. 18:42 → 18:41 moves one digit).
/// </summary>
public sealed class TickerText : StackPanel
{
    private readonly List<CrossfadeText> _cells = new();
    private string _text = "";

    public TickerText() { Orientation = Orientation.Horizontal; }

    /// <summary>Applied to every character cell.</summary>
    public Action<TextBlock>? CellStyle { get; set; }

    public string Text
    {
        get => _text;
        set
        {
            value ??= "";
            if (value == _text) return;
            if (value.Length != _text.Length)
            {
                Children.Clear();
                _cells.Clear();
                foreach (char _ in value)
                {
                    var cell = new CrossfadeText();
                    if (CellStyle != null) cell.SetStyle(CellStyle);
                    _cells.Add(cell);
                    Children.Add(cell);
                }
            }
            for (int i = 0; i < value.Length; i++) _cells[i].Text = value[i].ToString();
            _text = value;
        }
    }
}
