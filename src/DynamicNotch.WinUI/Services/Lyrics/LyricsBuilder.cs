using System.Globalization;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services.Lyrics;

/// <summary>
/// Turns a provider's raw lyrics into a <see cref="LyricsDocument"/>: word-synced (Lyricsfile or enhanced LRC) first,
/// then line-synced LRC, then plain text. Lines without word timings get estimated ones, spread across the line by
/// syllable count (not evenly), with a little extra on the last word (lines often end on a held note) and a pause after
/// commas; the vocal onsets refine them during playback (<see cref="WordTimingRefiner"/>). Gaps of a few seconds become
/// instrumental breaks (three dots).
/// </summary>
public static partial class LyricsBuilder
{
    // ---- Tuning ----
    /// <summary>A gap this long between singing and the next line becomes an instrumental break (seconds).</summary>
    public const double InterludeMinGap = 4.5;
    /// <summary>A word at least this long (and slow per syllable) is a held note.</summary>
    public const double HeldMinSeconds = 0.9, HeldPerSyllable = 0.5;
    /// <summary>Roughly how long a sung syllable lasts when nothing else is known (seconds).</summary>
    private const double SecondsPerSyllable = 0.27;

    public static LyricsDocument? Build(LyricsRaw raw, double songDuration)
    {
        List<ParsedLine>? parsed = null;
        var kind = LyricsKind.None;
        bool instrumental = raw.Instrumental;
        if (raw.Lyricsfile != null)
        {
            parsed = LyricsfileParser.Parse(raw.Lyricsfile, out bool inst);
            instrumental |= inst;
            if (parsed is { Count: > 0 }) kind = parsed.Any(l => l.Words is { Count: > 0 }) ? LyricsKind.WordSynced : LyricsKind.LineSynced;
            else parsed = null;
        }
        if (parsed == null && raw.Synced != null)
        {
            parsed = LrcParser.Parse(raw.Synced);
            if (parsed.Count > 0) kind = parsed.Any(l => l.Words is { Count: > 0 }) ? LyricsKind.WordSynced : LyricsKind.LineSynced;
            else parsed = null;
        }
        if (parsed == null && raw.Plain != null) return BuildPlain(raw);
        if (parsed == null) return instrumental ? new LyricsDocument { Kind = LyricsKind.None, Credit = raw.Credit, Provider = raw.Provider } : null;
        // Synced data that is all blank (an "instrumental" marker) isn't lyrics.
        if (parsed.All(p => p.Blank)) return raw.Plain != null ? BuildPlain(raw) : new LyricsDocument { Kind = LyricsKind.None, Credit = raw.Credit };
        return BuildSynced(parsed, kind, raw, songDuration > 0 ? songDuration : raw.Duration);
    }

    // =================================================================================================================
    // Synced
    // =================================================================================================================
    private static LyricsDocument BuildSynced(List<ParsedLine> parsed, LyricsKind kind, LyricsRaw raw, double songDuration)
    {
        var lines = new List<LyricLine>();
        double songEnd = songDuration > 0 ? songDuration : (parsed[^1].End ?? parsed[^1].Start) + 6;
        for (int i = 0; i < parsed.Count; i++)
        {
            var p = parsed[i];
            if (p.Blank) continue;
            // The line may run until the next timed line (blank ones included: they mark where the singing stops).
            double next = i + 1 < parsed.Count ? parsed[i + 1].Start : songEnd;
            double limit = p.End is { } e && e > p.Start ? Math.Min(next, e) : next;
            limit = Math.Max(limit, p.Start + 0.3);
            var words = p.Words is { Count: > 0 } ? TimedWords(p, limit) : EstimatedWords(p.Text, p.Start, limit, next);
            if (words.Count == 0) continue;
            bool backingLine = words.All(w => w.Backing);
            lines.Add(new LyricLine
            {
                Start = words[0].Start,
                End = words[^1].End,
                Text = string.Concat(words.Select(w => w.Text + (w.SpaceAfter ? " " : ""))).Trim(),
                Words = words,
                Agent = p.Agent,
                IsBacking = backingLine,
            });
        }

        // Instrumental breaks: the intro and any long gap between the singing and the next line.
        var items = new List<LyricLine>();
        double prevEnd = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            double gapFrom = i == 0 ? 0 : prevEnd;
            if (l.Start - gapFrom >= InterludeMinGap)
                items.Add(new LyricLine { Start = i == 0 ? 0 : gapFrom + 0.25, End = l.Start, IsInterlude = true });
            items.Add(l);
            prevEnd = Math.Max(prevEnd, l.End);
        }
        for (int i = 0; i < items.Count; i++)
        {
            items[i].Index = i;
            items[i].NextStart = i + 1 < items.Count ? items[i + 1].Start : songEnd;
            if (items[i].IsInterlude) items[i].End = items[i].NextStart;
            // A word can't run into the next line.
            foreach (var w in items[i].Words)
            {
                w.End = Math.Min(w.End, items[i].NextStart);
                w.Held = w.Duration >= HeldMinSeconds && w.Duration / Math.Max(1, w.Syllables) >= HeldPerSyllable;
            }
            if (items[i].Words.Count > 0) items[i].End = Math.Min(items[i].End, items[i].NextStart);
        }
        var agents = items.Where(l => !l.IsInterlude).Select(l => l.Agent).Where(a => a is 1 or 2).Distinct().Count();
        return new LyricsDocument { Kind = kind, Lines = items, Credit = raw.Credit, Provider = raw.Provider, HasDuet = agents >= 2 };
    }

    /// <summary>Words with their own timings (Lyricsfile / enhanced LRC): each ends where the next starts, the last at the line's end.</summary>
    private static List<LyricWord> TimedWords(ParsedLine p, double lineEnd)
    {
        var src = p.Words!;
        var words = new List<LyricWord>();
        bool backing = false;
        for (int i = 0; i < src.Count; i++)
        {
            var (text, start, end) = src[i];
            string t = text.Trim();
            if (t.Length == 0) continue;
            bool opens = t.StartsWith('(') || t.StartsWith('['), closes = t.EndsWith(')') || t.EndsWith(']');
            bool isBacking = backing || opens;
            if (opens) backing = true;
            if (closes) backing = false;
            double nextStart = i + 1 < src.Count ? src[i + 1].Start : Math.Max(lineEnd, start + 0.2);
            double e = end is { } ee && ee > start ? ee : nextStart;
            if (i == src.Count - 1) e = end is { } le && le > start ? le : Math.Max(lineEnd, start + 0.25);
            words.Add(new LyricWord
            {
                Text = t, Start = start, End = Math.Max(start + 0.05, e), Syllables = Syllables(t),
                Backing = isBacking, SpaceAfter = text.EndsWith(' ') || i == src.Count - 1,
            });
        }
        return words;
    }

    /// <summary>
    /// Line-synced text: split into words (CJK into characters) and spread across the sung part of the line by syllables.
    /// The sung part is shorter than the gap to the next line when that gap is long (a pause or break follows).
    /// </summary>
    private static List<LyricWord> EstimatedWords(string text, double start, double limit, double next)
    {
        var tokens = Tokenize(text);
        if (tokens.Count == 0) return new();
        int syllables = tokens.Sum(t => t.Syllables);
        double natural = syllables * SecondsPerSyllable + tokens.Count * 0.05 + 0.35;
        double available = Math.Max(0.4, limit - start);
        // Fill most of the line, but never more than ~2.2× a natural pace (a long gap after it is a pause, not slow singing).
        double sung = Math.Min(available - Math.Min(0.12, available * 0.1), Math.Min(natural * 2.2, Math.Max(natural * 1.25, available * 0.82)));
        sung = Math.Max(Math.Min(available, 0.3), sung);
        var weights = new double[tokens.Count];
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            double w = t.Syllables + 0.25;
            if (t.Text.Length > 0 && ",;:—–".Contains(t.Text[^1])) w += 0.55; // a breath after a comma
            if (i == tokens.Count - 1) w = w * 1.35 + 0.3;                       // lines tend to end on a longer note
            weights[i] = w;
        }
        double total = weights.Sum(), t0 = start;
        var words = new List<LyricWord>(tokens.Count);
        for (int i = 0; i < tokens.Count; i++)
        {
            double d = sung * weights[i] / total;
            words.Add(new LyricWord
            {
                Text = tokens[i].Text, Start = t0, End = t0 + d, Syllables = tokens[i].Syllables, Estimated = true,
                Backing = tokens[i].Backing, SpaceAfter = tokens[i].SpaceAfter,
            });
            t0 += d;
        }
        return words;
    }

    private readonly record struct Token(string Text, int Syllables, bool Backing, bool SpaceAfter);

    private static List<Token> Tokenize(string text)
    {
        var list = new List<Token>();
        bool backing = false;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int pi = 0; pi < parts.Length; pi++)
        {
            string part = parts[pi];
            bool opens = part.StartsWith('(') || part.StartsWith('['), closes = part.EndsWith(')') || part.EndsWith(']');
            bool isBacking = backing || opens;
            if (opens) backing = true;
            if (closes) backing = false;
            if (IsCjk(part))
            {
                // One "word" per character (punctuation stays with the character before it).
                var chars = new List<string>();
                var e = StringInfo.GetTextElementEnumerator(part);
                while (e.MoveNext())
                {
                    string ch = e.GetTextElement();
                    if (chars.Count > 0 && !char.IsLetterOrDigit(ch, 0)) chars[^1] += ch; else chars.Add(ch);
                }
                for (int c = 0; c < chars.Count; c++)
                    list.Add(new Token(chars[c], 1, isBacking, c == chars.Count - 1));
            }
            else list.Add(new Token(part, Syllables(part), isBacking, true));
        }
        return list;
    }

    private static bool IsCjk(string s)
    {
        foreach (char c in s)
            if (c is >= '぀' and <= 'ヿ' or >= '㐀' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿') return true;
        return false;
    }

    [GeneratedRegex("[aeiouyàáâãäåæèéêëìíîïòóôõöøùúûüýÿœ]+")]
    private static partial Regex VowelGroups();

    /// <summary>Syllables in a word: vowel groups with the usual English corrections (silent e, -ed, -es, -le); 1 per CJK character.</summary>
    public static int Syllables(string word)
    {
        if (string.IsNullOrEmpty(word)) return 1;
        if (IsCjk(word)) return Math.Max(1, word.Count(c => char.IsLetter(c)));
        var w = new string(word.ToLowerInvariant().Where(c => char.IsLetter(c) || c == '\'').ToArray()).Replace("'", "");
        if (w.Length == 0) return word.Any(char.IsDigit) ? Math.Max(1, (int)Math.Round(word.Count(char.IsDigit) * 1.3)) : 1;
        if (w.Length <= 3) return 1;
        string core = w;
        if (core.EndsWith("es") && !core.EndsWith("ses") && !core.EndsWith("zes") && !core.EndsWith("ches") && !core.EndsWith("shes")) core = core[..^2];
        else if (core.EndsWith("ed") && !core.EndsWith("ted") && !core.EndsWith("ded")) core = core[..^2];
        else if (core.EndsWith('e') && !core.EndsWith("le") && !core.EndsWith("ee")) core = core[..^1];
        int n = VowelGroups().Matches(core).Count;
        if (w.EndsWith("le") && w.Length > 2 && !"aeiouy".Contains(w[^3])) n++; // "lit-tle"
        return Math.Clamp(n, 1, 8);
    }

    // =================================================================================================================
    // Plain
    // =================================================================================================================
    private static LyricsDocument BuildPlain(LyricsRaw raw)
    {
        var lines = new List<LyricLine>();
        foreach (var l in raw.Plain!.Replace("\r", "").Split('\n'))
        {
            string t = l.Trim();
            if (t.Length == 0)
            {
                // Keep one empty line between verses (it reads as a stanza break).
                if (lines.Count > 0 && lines[^1].Words.Count > 0) lines.Add(new LyricLine { Index = lines.Count, Text = "" });
                continue;
            }
            var words = Tokenize(t).Select(tk => new LyricWord { Text = tk.Text, Syllables = tk.Syllables, Backing = tk.Backing, SpaceAfter = tk.SpaceAfter }).ToList();
            lines.Add(new LyricLine { Index = lines.Count, Text = t, Words = words, IsBacking = words.All(w => w.Backing) });
        }
        while (lines.Count > 0 && lines[^1].Words.Count == 0) lines.RemoveAt(lines.Count - 1);
        return new LyricsDocument { Kind = LyricsKind.Plain, Lines = lines, Credit = raw.Credit, Provider = raw.Provider };
    }
}
