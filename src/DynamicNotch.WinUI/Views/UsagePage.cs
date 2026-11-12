using System.ComponentModel;
using System.Globalization;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using DynamicNotch.Services;
using DynamicNotch.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DynamicNotch.Views;

/// <summary>
/// Claude Usage page: a header ("Claude usage" · "Updated 30s ago"), two meter cards (5-hour session,
/// weekly) with gradient rings that shift colour with usage, an optional slimmer row per model-specific
/// weekly limit, and a footer note that the data comes from an unofficial endpoint. The loading /
/// numbers / unavailable states cross-fade. Labels ("Resets in…", "Updated…") refresh once a second,
/// only while the page is visible.
/// </summary>
public sealed class UsagePage : Grid
{
    private readonly UsageViewModel _vm;
    private readonly Grid _header = new(), _body = new(), _meters = new(), _cards = new();
    private readonly StackPanel _models = new();
    private readonly Border _footer = new();
    private readonly TextBlock _updated = new();
    private readonly Meter _five, _week;
    private readonly StackPanel _unavailable = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _reason = new(), _loading = new();
    private readonly CompSpring _metersFade, _unavailableFade, _loadingFade;
    private readonly List<ModelRow> _modelRows = new();
    private readonly DispatcherQueueTimer _labels;

    public UsagePage(UsageViewModel vm)
    {
        _vm = vm;
        foreach (var h in new[] { GridLength.Auto, Theme.GapS2, new GridLength(1, GridUnitType.Star), Theme.GapS1, GridLength.Auto })
            RowDefinitions.Add(new RowDefinition { Height = h });

        // ---- Header ----
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new ClaudeLogo(Theme.IconSmall, Theme.SecondaryText) { VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = "Claude usage", Style = StyleOf("SectionText"), Margin = Theme.GapLeftS2 });
        _header.Children.Add(title);
        _updated.Style = StyleOf("CaptionText");
        _updated.HorizontalAlignment = HorizontalAlignment.Right;
        _updated.VerticalAlignment = VerticalAlignment.Center;
        _header.Children.Add(_updated);
        Children.Add(_header);

        // ---- Meters: two cards side by side, model rows underneath ----
        SetRow(_body, 2);
        _cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _cards.ColumnDefinitions.Add(new ColumnDefinition { Width = Theme.GapS3 });
        _cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _five = new Meter("5-hour session");
        _week = new Meter("Weekly");
        SetColumn(_week, 2);
        _cards.Children.Add(_five);
        _cards.Children.Add(_week);
        _meters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _meters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _meters.VerticalAlignment = VerticalAlignment.Center;
        _meters.Children.Add(_cards);
        _models.Margin = Theme.GapTopS2;
        SetRow(_models, 1);
        _meters.Children.Add(_models);
        _body.Children.Add(_meters);

        // ---- Unavailable / loading ----
        var alert = new VectorIcon { Data = Icons.Alert, Size = Theme.IconLarge, Foreground = Theme.SecondaryText, HorizontalAlignment = HorizontalAlignment.Center };
        _unavailable.Children.Add(alert);
        _unavailable.Children.Add(new TextBlock { Text = "Usage unavailable", Style = StyleOf("TitleText"), HorizontalAlignment = HorizontalAlignment.Center, Margin = Theme.GapTopS2 });
        _reason.Style = StyleOf("SecondaryText");
        _reason.HorizontalAlignment = HorizontalAlignment.Center;
        _reason.TextAlignment = TextAlignment.Center;
        _reason.TextWrapping = TextWrapping.Wrap;
        _reason.MaxWidth = 420;
        _reason.Margin = Theme.GapTopS1;
        _unavailable.Children.Add(_reason);
        _body.Children.Add(_unavailable);
        _loading.Text = "Loading usage…";
        _loading.Style = StyleOf("SecondaryText");
        _loading.HorizontalAlignment = HorizontalAlignment.Center;
        _loading.VerticalAlignment = VerticalAlignment.Center;
        _body.Children.Add(_loading);
        Children.Add(_body);

        // ---- Footer note (the endpoint is unofficial) ----
        SetRow(_footer, 4);
        _footer.Child = new TextBlock { Text = "Unofficial Claude endpoint · may change or stop working", Style = StyleOf("CaptionText") };
        Children.Add(_footer);

        var c = Anim.VisualOf(this).Compositor;
        _metersFade = Fader(c, _meters);
        _unavailableFade = Fader(c, _unavailable);
        _loadingFade = Fader(c, _loading);

        _labels = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _labels.Interval = TimeSpan.FromSeconds(1);
        _labels.Tick += (_, _) => RefreshLabels();

        vm.PropertyChanged += OnVmChanged;
        Refresh(animate: false);
    }

    /// <summary>Blocks for the notch's staggered entrance.</summary>
    public IEnumerable<(FrameworkElement element, int order)> StaggerBlocks =>
        new (FrameworkElement, int)[] { (_header, 1), (_body, 2), (_footer, 4) };

    internal static Style StyleOf(string key) => (Style)Application.Current.Resources[key];

    private static CompSpring Fader(Compositor c, UIElement el)
    {
        var (spring, set) = Motions.Value(c, Motion.StateFade, 0);
        Anim.VisualOf(el).StartAnimation("Opacity", Anim.Expr(c, "Clamp(p.v, 0, 1)", ("p", set)));
        return spring;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UsageViewModel.IsVisible))
        {
            if (_vm.IsVisible) { RefreshLabels(); _labels.Start(); } else _labels.Stop();
            return;
        }
        Refresh(animate: true);
    }

    private void Refresh(bool animate)
    {
        var s = _vm.Snapshot;
        bool numbers = s != null, unavailable = _vm.UnavailableReason != null, loading = !numbers && !unavailable;
        void Fade(CompSpring f, bool on) { if (animate) f.SetTarget(on ? 1 : 0, Motion.StateFade); else f.Snap(on ? 1 : 0); }
        Fade(_metersFade, numbers);
        Fade(_unavailableFade, unavailable);
        Fade(_loadingFade, loading);
        _meters.IsHitTestVisible = numbers;
        _reason.Text = _vm.UnavailableReason ?? "";

        if (s != null)
        {
            _five.Visibility = s.FiveHour != null ? Visibility.Visible : Visibility.Collapsed;
            _week.Visibility = s.Weekly != null ? Visibility.Visible : Visibility.Collapsed;
            // One meter only: it takes the whole row.
            SetColumn(_week, s.FiveHour == null ? 0 : 2);
            SetColumnSpan(_five, s.Weekly == null ? 3 : 1);
            SetColumnSpan(_week, s.FiveHour == null ? 3 : 1);
            if (s.FiveHour != null) _five.SetWindow(s.FiveHour);
            if (s.Weekly != null) _week.SetWindow(s.Weekly);

            while (_modelRows.Count < s.Models.Count) { var r = new ModelRow(); _modelRows.Add(r); _models.Children.Add(r); }
            for (int i = 0; i < _modelRows.Count; i++)
            {
                bool on = i < s.Models.Count;
                _modelRows[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (on) _modelRows[i].SetWindow(s.Models[i]);
            }
        }
        RefreshLabels();
    }

    private void RefreshLabels()
    {
        var s = _vm.Snapshot;
        var now = DateTimeOffset.Now;
        if (s == null) { _updated.Text = ""; return; }
        string ago = Ago(now - s.FetchedAt);
        _updated.Text = _vm.StaleNote is { } note ? $"{note} · updated {ago}" : $"Updated {ago}";
        _updated.Foreground = _vm.StaleNote != null ? Theme.SecondaryText : Theme.TertiaryText;
        _five.RefreshLabels(now);
        _week.RefreshLabels(now);
        foreach (var r in _modelRows) r.RefreshLabels(now);
    }

    // ---- Formatting -----------------------------------------------------------------------------------
    internal static string Ago(TimeSpan t) =>
        t.TotalSeconds < 10 ? "just now"
        : t.TotalSeconds < 60 ? $"{(int)t.TotalSeconds}s ago"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} min ago"
        : $"{(int)t.TotalHours} h ago";

    /// <summary>"Resets in 2 h 14 min" / "Resets in 3 d 4 h" / "Resets in 12 min" / "Resetting…".</summary>
    internal static string Countdown(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } a) return "Reset time unknown";
        var t = a - now;
        if (t <= TimeSpan.FromSeconds(30)) return "Resetting…";
        if (t.TotalDays >= 1) return $"Resets in {(int)t.TotalDays} d {t.Hours} h";
        if (t.TotalHours >= 1) return $"Resets in {(int)t.TotalHours} h {t.Minutes} min";
        return $"Resets in {Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))} min";
    }

    /// <summary>Local clock time: "at 14:30" today, "Mon 09:00" otherwise (follows the 12/24-hour setting).</summary>
    internal static string ClockText(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is not { } a) return "";
        var local = a.ToLocalTime().DateTime;
        string time = ClockService.FormatTime(local);
        if (local.Date == now.LocalDateTime.Date) return $"at {time}";
        if (local.Date == now.LocalDateTime.Date.AddDays(1)) return $"tomorrow {time}";
        return $"{local.ToString("ddd", CultureInfo.CurrentCulture)} {time}";
    }

    /// <summary>One big meter card: gradient ring with the % in the middle, label, countdown and clock time.</summary>
    private sealed class Meter : Grid
    {
        private readonly GradientRing _ring = new(Theme.UsageRingSize, Theme.UsageRingThickness);
        private readonly CrossfadeText _percent = new();
        private readonly TextBlock _countdown = new(), _clock = new();
        private UsageWindow? _window;

        public Meter(string label)
        {
            Background = Theme.CardBrush;
            BorderBrush = Theme.CardBorderBrush;
            BorderThickness = new Thickness(1);
            CornerRadius = Theme.Radius;
            Padding = Theme.UsageCardPadding;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _percent.SetStyle(t =>
            {
                t.Style = (Style)Application.Current.Resources["TimeText"];
                t.FontSize = Theme.FontUsagePercent;
                t.Foreground = Theme.PrimaryText;
            });
            _percent.HorizontalAlignment = HorizontalAlignment.Center;
            _percent.VerticalAlignment = VerticalAlignment.Center;
            _ring.Center = _percent;
            _ring.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(_ring);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Theme.S4, 0, 0, 0) };
            text.Children.Add(new TextBlock { Text = label, Style = StyleOf("BaseText"), FontFamily = Theme.FontSemiBold, FontWeight = FontWeights.SemiBold });
            _countdown.Style = StyleOf("SecondaryText");
            _countdown.Margin = Theme.GapTopS2;
            text.Children.Add(_countdown);
            _clock.Style = StyleOf("CaptionText");
            _clock.Margin = Theme.GapTopS1;
            text.Children.Add(_clock);
            SetColumnOf(text, 1);
            g.Children.Add(text);
            Children.Add(g);
        }

        private static void SetColumnOf(FrameworkElement el, int col) => Grid.SetColumn(el, col);

        public void SetWindow(UsageWindow w)
        {
            _window = w;
            var (a, b) = Theme.UsageColors(w.Percent);
            _ring.Set(w.Percent / 100, a, b);
            _percent.Text = $"{Math.Round(w.Percent):0}%";
            ToolTipService.SetToolTip(this, $"{w.Percent:0.#}% used");
        }

        public void RefreshLabels(DateTimeOffset now)
        {
            if (_window == null) return;
            _countdown.Text = Countdown(_window.ResetsAt, now);
            _clock.Text = ClockText(_window.ResetsAt, now);
        }
    }

    /// <summary>A slimmer row for a model-specific weekly limit: label · gradient bar · % · reset.</summary>
    private sealed class ModelRow : Grid
    {
        private readonly GradientBar _bar = new(Theme.MeterBarThickness);
        private readonly TextBlock _label = new(), _percent = new(), _reset = new();
        private UsageWindow? _window;

        public ModelRow()
        {
            Background = Theme.CardBrush;
            BorderBrush = Theme.CardBorderBrush;
            BorderThickness = new Thickness(1);
            CornerRadius = Theme.Radius;
            Padding = new Thickness(Theme.S4, Theme.S2, Theme.S4, Theme.S2);
            Margin = new Thickness(0, 0, 0, Theme.S1);
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            _label.Style = StyleOf("CaptionText");
            _label.Foreground = Theme.SecondaryText;
            _label.FontFamily = Theme.FontSemiBold;
            _label.FontWeight = FontWeights.SemiBold;
            _label.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(_label);
            _bar.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_bar, 1);
            g.Children.Add(_bar);
            _percent.Style = StyleOf("BadgeText");
            _percent.HorizontalAlignment = HorizontalAlignment.Right;
            _percent.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_percent, 2);
            g.Children.Add(_percent);
            _reset.Style = StyleOf("CaptionText");
            _reset.HorizontalAlignment = HorizontalAlignment.Right;
            _reset.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_reset, 3);
            g.Children.Add(_reset);
            Children.Add(g);
        }

        public void SetWindow(UsageWindow w)
        {
            _window = w;
            _label.Text = w.Label;
            var (a, b) = Theme.UsageColors(w.Percent);
            _bar.SetColors(a, b);
            _bar.SetValue(w.Percent / 100);
            _percent.Text = $"{Math.Round(w.Percent):0}%";
        }

        public void RefreshLabels(DateTimeOffset now)
        {
            if (_window == null) return;
            string clock = ClockText(_window.ResetsAt, now);
            _reset.Text = string.IsNullOrEmpty(clock) ? "" : "Resets " + clock.Replace("at ", "");
            ToolTipService.SetToolTip(this, Countdown(_window.ResetsAt, now));
        }
    }
}
