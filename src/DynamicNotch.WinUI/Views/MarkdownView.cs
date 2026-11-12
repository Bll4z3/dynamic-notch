using System.Text.RegularExpressions;
using DynamicNotch.Animation;
using DynamicNotch.Design;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI;

namespace DynamicNotch.Views;

/// <summary>
/// Light Markdown for streamed answers (same subset as the WPF view), rendered into a selectable
/// RichTextBlock so the answer can be selected and copied with Ctrl+C: paragraphs, **bold**,
/// `inline code` (highlighted), bullet / numbered lists, simple headings and fenced code blocks.
/// Text that arrived since the previous render is drawn with a brush whose alpha animates up, so each
/// streamed chunk fades in softly.
/// </summary>
public sealed class MarkdownView : Grid
{
    private readonly RichTextBlock _rtb = new();
    private BlockCollection Blocks => _rtb.Blocks;
    private static readonly Regex Numbered = new(@"^\s*(\d+)[.)]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*[-*•]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^\s*#{1,6}\s+(.*)$", RegexOptions.Compiled);
    private const double ListIndent = Theme.S4 + 2;

    private SolidColorBrush _fadeBrush = new(Theme.PrimaryTextColor);
    private readonly List<Run> _codeRuns = new();
    private readonly TextHighlighter _codeHighlight = new() { Background = Theme.InlineCodeBrush, Foreground = Theme.PrimaryText };
    private Storyboard? _fade;
    private int _shownChars, _emitted;
    private string _text = "";

    public MarkdownView()
    {
        _rtb.IsTextSelectionEnabled = true;
        _rtb.SelectionHighlightColor = Theme.AccentBrush;
        _rtb.FontFamily = Theme.Font;
        _rtb.FontSize = Theme.AskAnswerFontSize;
        _rtb.LineHeight = Theme.AskAnswerLineHeight;
        _rtb.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _rtb.Foreground = Theme.PrimaryText;
        _rtb.TextWrapping = TextWrapping.Wrap;
        _rtb.TextHighlighters.Add(_codeHighlight);
        Children.Add(_rtb);
    }

    /// <summary>Width available to code blocks (they span the answer column).</summary>
    public double ColumnWidth { get; set; } = 400;

    /// <summary>Replace the text. With <paramref name="animate"/> the newly arrived tail fades in.</summary>
    public void Render(string text, bool animate)
    {
        if (text == _text && Blocks.Count > 0) return;
        bool append = animate && Anim.Enabled && text.StartsWith(_text, StringComparison.Ordinal);
        int previous = append ? _shownChars : int.MaxValue;
        _text = text;

        _fade?.Stop();
        // A fresh brush per render: the previous tail is now solid text.
        _fadeBrush = new SolidColorBrush(append ? Color.FromArgb(0, 0xF5, 0xF5, 0xF7) : Theme.PrimaryTextColor);
        Blocks.Clear();
        _codeRuns.Clear();
        _emitted = 0;
        Build(text, previous);
        _shownChars = _emitted;
        HighlightCode();
        if (append) FadeIn();
    }

    private void FadeIn()
    {
        // The StreamFade spring (0 → 1 alpha), sampled into colour keyframes.
        var model = new SpringModel(Motion.StreamFade, 0);
        model.SetTarget(1);
        double duration = Math.Max(0.05, model.SettleDuration()), start = SpringModel.Now;
        var a = new ColorAnimationUsingKeyFrames { EnableDependentAnimation = true };
        const int keys = 16;
        for (int i = 1; i <= keys; i++)
        {
            double t = duration * i / keys;
            double alpha = i == keys ? 1 : Math.Clamp(model.ValueAt(start + t), 0, 1);
            a.KeyFrames.Add(new LinearColorKeyFrame
            {
                KeyTime = TimeSpan.FromSeconds(t),
                Value = Color.FromArgb((byte)Math.Round(255 * alpha), Theme.PrimaryTextColor.R, Theme.PrimaryTextColor.G, Theme.PrimaryTextColor.B),
            });
        }
        Storyboard.SetTarget(a, _fadeBrush);
        Storyboard.SetTargetProperty(a, "Color");
        _fade = new Storyboard();
        _fade.Children.Add(a);
        _fade.Begin();
    }

    /// <summary>Inline code gets a soft background (a highlighter over its character range).</summary>
    private void HighlightCode()
    {
        // Highlighter ranges are plain character indices in the control's own text, not TextPointer
        // offsets (those also count element boundaries, so they drift). In that text every block
        // ends with "\r\n" and an inline UI object (the code block) takes no characters.
        _codeHighlight.Ranges.Clear();
        if (_codeRuns.Count == 0) return;
        int index = 0;
        foreach (var block in Blocks)
        {
            if (block is not Paragraph p) continue;
            foreach (var inline in p.Inlines)
            {
                if (inline is not Run r) continue;
                if (r.Text.Length > 0 && _codeRuns.Contains(r)) _codeHighlight.Ranges.Add(new TextRange { StartIndex = index, Length = r.Text.Length });
                index += r.Text.Length;
            }
            index += BlockBreakLength;
        }
    }

    /// <summary>Characters a block break takes in the highlighter's index space (measured: none).</summary>
    private const int BlockBreakLength = 0;

    // ---- Blocks ------------------------------------------------------------------------------
    private static readonly Thickness BlockGap = new(0, 0, 0, Theme.S2);

    private void Build(string text, int fadeFrom)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        Paragraph? lastListItem = null;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            Blocks.Add(MakeParagraph(string.Join(" ", paragraph), fadeFrom, bold: false));
            paragraph.Clear();
        }
        void EndList()
        {
            if (lastListItem != null) lastListItem.Margin = new Thickness(ListIndent, 0, 0, Theme.S2);
            lastListItem = null;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(); EndList();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal); i++) code.Add(lines[i]);
                Blocks.Add(MakeCodeBlock(string.Join("\n", code)));
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) { FlushParagraph(); EndList(); continue; }

            Match m;
            if ((m = Heading.Match(line)).Success)
            {
                FlushParagraph(); EndList();
                Blocks.Add(MakeParagraph(m.Groups[1].Value, fadeFrom, bold: true));
                continue;
            }
            bool isBullet = (m = Bullet.Match(line)).Success;
            Match n = isBullet ? Match.Empty : Numbered.Match(line);
            if (isBullet || n.Success)
            {
                FlushParagraph();
                // List item: a hanging indent, the marker in the gutter.
                var p = new Paragraph { Margin = new Thickness(ListIndent, 0, 0, Theme.S1 / 2), TextIndent = -ListIndent };
                p.Inlines.Add(new Run { Text = (isBullet ? "•" : n.Groups[1].Value + ".") + "\t", Foreground = Theme.SecondaryText });
                AddInlines(p.Inlines, isBullet ? m.Groups[1].Value : n.Groups[2].Value, fadeFrom, bold: false);
                Blocks.Add(p);
                lastListItem = p;
                continue;
            }
            EndList();
            paragraph.Add(line.Trim());
        }
        FlushParagraph();
        EndList();
    }

    private Paragraph MakeParagraph(string text, int fadeFrom, bool bold)
    {
        var p = new Paragraph { Margin = BlockGap };
        AddInlines(p.Inlines, text, fadeFrom, bold);
        return p;
    }

    /// <summary>Code block: rounded panel (global radius) with selectable monospace text.</summary>
    private Block MakeCodeBlock(string code)
    {
        _emitted += code.Length;
        var box = new TextBlock
        {
            Text = code,
            IsTextSelectionEnabled = true,
            SelectionHighlightColor = Theme.AccentBrush,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Theme.MonoFont,
            FontSize = Theme.MonoFontSize,
            Foreground = Theme.PrimaryText,
        };
        var border = new Border
        {
            Background = Theme.InputBrush,
            BorderBrush = Theme.TileBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = Theme.Radius,
            Padding = new Thickness(Theme.S3, Theme.S2, Theme.S3, Theme.S2),
            Width = Math.Max(40, ColumnWidth),
            Child = box,
        };
        var p = new Paragraph { Margin = BlockGap };
        p.Inlines.Add(new InlineUIContainer { Child = border });
        return p;
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

    private Run MakeRun(string s, bool bold, bool mono, Brush brush)
    {
        // The bundled Inter files are one family per weight, so bold text uses the bold file.
        var run = new Run { Text = s, Foreground = brush, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, FontFamily = bold ? Theme.FontBold : Theme.Font };
        if (mono)
        {
            run.FontFamily = Theme.MonoFont;
            run.FontSize = Theme.MonoFontSize;
            _codeRuns.Add(run);
        }
        return run;
    }
}
