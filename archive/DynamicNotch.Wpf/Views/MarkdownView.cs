using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DynamicNotch.Animation;
using DynamicNotch.Design;

namespace DynamicNotch.Views;

/// <summary>
/// Light Markdown for streamed answers, rendered into a read-only rich text box so the answer can be
/// selected with the mouse and copied with Ctrl+C: paragraphs, **bold**, `inline code`, bullet /
/// numbered lists, simple headings and fenced code blocks. Text that arrived since the previous render
/// is drawn with a brush whose alpha springs up, so each streamed chunk fades in softly.
/// </summary>
public sealed class MarkdownView : RichTextBox
{
    private static readonly Regex Numbered = new(@"^\s*(\d+)[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*[-*•]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^\s*#{1,6}\s+(.*)$", RegexOptions.Compiled);

    private readonly SolidColorBrush _fadeBrush = new(Theme.PrimaryText.Color);
    private readonly Spring _fade = new(Motion.StreamFade, 1);
    private int _shownChars;
    private int _emitted;
    private string _text = "";

    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Foreground = Theme.PrimaryText;
        SelectionBrush = Theme.AccentBrush;
        CaretBrush = Brushes.Transparent;
        FocusVisualStyle = null;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        // Minimal template: just the content host (no border, no focus chrome).
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        host.SetValue(FocusableProperty, false);
        Template = new ControlTemplate(typeof(RichTextBox)) { VisualTree = host };

        Document = NewDocument();
        _fade.ValueChanged += v =>
        {
            var c = Theme.PrimaryText.Color;
            _fadeBrush.Color = Color.FromArgb((byte)(c.A * Math.Clamp(v, 0, 1)), c.R, c.G, c.B);
        };
    }

    private static FlowDocument NewDocument() => new()
    {
        PagePadding = new Thickness(0),
        FontFamily = Theme.Font,
        FontSize = Theme.AskAnswerFontSize,
        LineHeight = Theme.AskAnswerLineHeight,
        Foreground = Theme.PrimaryText,
        TextAlignment = TextAlignment.Left,
    };

    /// <summary>Bindable text: re-renders on change (with the streaming fade while <see cref="Streaming"/> is true).</summary>
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView),
        new PropertyMetadata("", (d, e) => ((MarkdownView)d).Render((string?)e.NewValue ?? "", ((MarkdownView)d).Streaming)));

    public static readonly DependencyProperty StreamingProperty = DependencyProperty.Register(
        nameof(Streaming), typeof(bool), typeof(MarkdownView), new PropertyMetadata(false));

    public string Markdown { get => (string)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }
    public bool Streaming { get => (bool)GetValue(StreamingProperty); set => SetValue(StreamingProperty, value); }

    /// <summary>Replace the text. When <paramref name="animate"/> is false everything appears at once.</summary>
    public void Render(string text, bool animate = true)
    {
        if (text == _text && Document.Blocks.Count > 0) return;
        bool append = animate && text.StartsWith(_text, StringComparison.Ordinal) && SpringAnimator.AnimationsEnabled;
        int previous = append ? _shownChars : int.MaxValue;
        _text = text;
        var doc = NewDocument();
        _emitted = 0;
        Build(doc, text, previous);
        Document = doc;
        _shownChars = _emitted;
        if (append) { _fade.Snap(0); _fade.SetTarget(1, Motion.StreamFade); }
        else _fade.Snap(1);
    }

    // ---- Blocks ------------------------------------------------------------------------------
    private static readonly Thickness BlockGap = new(0, 0, 0, Theme.S2);

    private void Build(FlowDocument doc, string text, int fadeFrom)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        List? list = null;
        bool listNumbered = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            doc.Blocks.Add(MakeParagraph(string.Join(" ", paragraph), fadeFrom, bold: false));
            paragraph.Clear();
        }
        void EndList() => list = null;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(); EndList();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++) code.Add(lines[i]);
                doc.Blocks.Add(MakeCodeBlock(string.Join("\n", code)));
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) { FlushParagraph(); EndList(); continue; }

            Match m;
            if ((m = Heading.Match(line)).Success)
            {
                FlushParagraph(); EndList();
                doc.Blocks.Add(MakeParagraph(m.Groups[1].Value, fadeFrom, bold: true));
                continue;
            }
            bool isBullet = (m = Bullet.Match(line)).Success;
            Match n = isBullet ? Match.Empty : Numbered.Match(line);
            if (isBullet || n.Success)
            {
                FlushParagraph();
                bool numbered = !isBullet;
                if (list == null || listNumbered != numbered)
                {
                    list = new List
                    {
                        MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = BlockGap,
                        Padding = new Thickness(Theme.S4 + 2, 0, 0, 0),
                    };
                    if (numbered && int.TryParse(n.Groups[1].Value, out int start)) list.StartIndex = start;
                    listNumbered = numbered;
                    doc.Blocks.Add(list);
                }
                var item = new ListItem();
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, Theme.S1 / 2) };
                AddInlines(p.Inlines, isBullet ? m.Groups[1].Value : n.Groups[2].Value, fadeFrom, bold: false);
                item.Blocks.Add(p);
                list.ListItems.Add(item);
                continue;
            }
            EndList();
            paragraph.Add(line.Trim());
        }
        FlushParagraph();
    }

    private Paragraph MakeParagraph(string text, int fadeFrom, bool bold)
    {
        var p = new Paragraph { Margin = BlockGap };
        if (bold) p.FontWeight = FontWeights.Bold;
        AddInlines(p.Inlines, text, fadeFrom, bold);
        return p;
    }

    /// <summary>Code block: rounded panel (global radius) with selectable monospace text.</summary>
    private Block MakeCodeBlock(string code)
    {
        _emitted += code.Length;
        var box = new TextBox
        {
            Text = code,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Theme.MonoFont,
            FontSize = Theme.MonoFontSize,
            Foreground = Theme.PrimaryText,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            SelectionBrush = Theme.AccentBrush,
            CaretBrush = Brushes.Transparent,
            Padding = new Thickness(0),
        };
        return new BlockUIContainer(new Border
        {
            Background = Theme.InputBrush,
            BorderBrush = Theme.TileBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.Radius,
            Padding = new Thickness(Theme.S3, Theme.S2, Theme.S3, Theme.S2),
            Child = box,
        }) { Margin = BlockGap };
    }

    // ---- Inlines: **bold** and `code` -----------------------------------------------------------
    private void AddInlines(InlineCollection inlines, string text, int fadeFrom, bool bold)
    {
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    Emit(inlines, text.Substring(i + 1, end - i - 1), fadeFrom, bold, mono: true);
                    i = end + 1;
                    continue;
                }
            }
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i)
                {
                    AddInlines(inlines, text.Substring(i + 2, end - i - 2), fadeFrom, bold: true);
                    i = end + 2;
                    continue;
                }
            }
            int next = NextMarker(text, i + 1);
            Emit(inlines, text.Substring(i, next - i), fadeFrom, bold, mono: false);
            i = next;
        }
    }

    private static int NextMarker(string text, int from)
    {
        for (int j = from; j < text.Length; j++)
            if (text[j] == '`' || (text[j] == '*' && j + 1 < text.Length && text[j + 1] == '*')) return j;
        return text.Length;
    }

    /// <summary>Adds text, splitting it where the previously shown part ends so the new tail fades in.</summary>
    private void Emit(InlineCollection inlines, string s, int fadeFrom, bool bold, bool mono)
    {
        if (s.Length == 0) return;
        int start = _emitted;
        _emitted += s.Length;
        int split = Math.Clamp(fadeFrom - start, 0, s.Length);
        if (split > 0) inlines.Add(MakeRun(s[..split], bold, mono, Theme.PrimaryText));
        if (split < s.Length) inlines.Add(MakeRun(s[split..], bold, mono, _fadeBrush));
    }

    private static Run MakeRun(string s, bool bold, bool mono, Brush brush)
    {
        var run = new Run(s) { FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, Foreground = brush };
        if (mono)
        {
            run.FontFamily = Theme.MonoFont;
            run.FontSize = Theme.MonoFontSize;
            run.Background = Theme.InlineCodeBrush;
        }
        return run;
    }
}
