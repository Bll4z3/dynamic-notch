using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services.Lyrics;

/// <summary>A line straight out of a lyrics format, before words are split and timed.</summary>
internal sealed class ParsedLine
{
    public double Start;
    public double? End;
    public string Text = "";
    /// <summary>Word-level timings when the source has them (text keeps its trailing space).</summary>
    public List<(string Text, double Start, double? End)>? Words;
    public int Agent;
    /// <summary>An empty timed line: the singing stops here.</summary>
    public bool Blank => string.IsNullOrWhiteSpace(Text) && (Words == null || Words.Count == 0);
}

/// <summary>
/// LRC: "[mm:ss.xx]text" lines (several time tags per line allowed), an optional "[offset:±ms]" tag, enhanced word
/// tags ("&lt;mm:ss.xx&gt;word") and duet prefixes ("v1:", "v2:", "M:", "F:", "D:").
/// </summary>
internal static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"<(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)>")]
    private static partial Regex WordTag();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    [GeneratedRegex(@"^\s*(v1|v2|v3|bg|M|F|D)\s*:\s*", RegexOptions.None)]
    private static partial Regex AgentPrefix();

    public static List<ParsedLine> Parse(string lrc)
    {
        var lines = new List<ParsedLine>();
        double offset = 0;
        foreach (var raw in lrc.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var off = OffsetTag().Match(line);
            if (off.Success) { offset = int.Parse(off.Groups[1].Value, CultureInfo.InvariantCulture) / 1000.0; continue; }
            var tags = new List<double>();
            int pos = 0;
            Match m;
            while ((m = TimeTag().Match(line, pos)).Success && m.Index == pos)
            {
                tags.Add(ToSeconds(m.Groups[1].Value, m.Groups[2].Value));
                pos = m.Index + m.Length;
            }
            if (tags.Count == 0) continue; // metadata ([ar:], [ti:]) or untimed text
            string text = line[pos..];
            int agent = 0;
            var ap = AgentPrefix().Match(text);
            if (ap.Success)
            {
                agent = ap.Groups[1].Value switch { "v1" or "M" => 1, "v2" or "F" => 2, "D" or "v3" => 3, _ => 0 };
                text = text[ap.Length..];
            }
            List<(string, double, double?)>? words = null;
            if (WordTag().IsMatch(text)) (text, words) = ParseWords(text);
            foreach (var t in tags)
                lines.Add(new ParsedLine { Start = Math.Max(0, t - offset), Text = text.Trim(), Words = words?.Select(w => (w.Item1, Math.Max(0, w.Item2 - offset), w.Item3 - offset)).ToList(), Agent = agent });
        }
        lines.Sort((a, b) => a.Start.CompareTo(b.Start));
        return lines;
    }

    /// <summary>Enhanced LRC: "&lt;00:12.30&gt;Hel&lt;00:12.55&gt;lo &lt;00:13.10&gt;world&lt;00:14.00&gt;" (a final tag ends the last word).</summary>
    private static (string text, List<(string, double, double?)> words) ParseWords(string text)
    {
        var words = new List<(string, double, double?)>();
        var plain = new StringBuilder();
        var tags = WordTag().Matches(text);
        string lead = text[..tags[0].Index];
        if (!string.IsNullOrWhiteSpace(lead)) plain.Append(lead);
        for (int i = 0; i < tags.Count; i++)
        {
            double t = ToSeconds(tags[i].Groups[1].Value, tags[i].Groups[2].Value);
            int from = tags[i].Index + tags[i].Length, to = i + 1 < tags.Count ? tags[i + 1].Index : text.Length;
            string chunk = text[from..to];
            plain.Append(chunk);
            if (chunk.Trim().Length == 0)
            {
                // A tag with no text after it ends the previous word.
                if (words.Count > 0 && words[^1].Item3 == null) words[^1] = (words[^1].Item1, words[^1].Item2, t);
                continue;
            }
            if (words.Count > 0 && words[^1].Item3 == null) words[^1] = (words[^1].Item1, words[^1].Item2, t);
            words.Add((chunk, t, null));
        }
        return (plain.ToString(), words);
    }

    private static double ToSeconds(string min, string sec)
    {
        sec = sec.Replace(':', '.');
        return int.Parse(min, CultureInfo.InvariantCulture) * 60 + double.Parse(sec, CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// LRCLIB's Lyricsfile (YAML): <c>lines[]</c> with <c>text</c>, <c>start_ms</c>, <c>end_ms</c> and, when word-synced,
/// <c>words[]</c> (<c>text</c>, <c>start_ms</c>, optional <c>end_ms</c>). Unknown keys are ignored; a singer key
/// (<c>agent</c> / <c>singer</c> / <c>voice</c>) or a background flag is used when present.
/// </summary>
internal static class LyricsfileParser
{
    public static List<ParsedLine>? Parse(string yaml, out bool instrumental)
    {
        instrumental = false;
        if (MiniYaml.Parse(yaml) is not Dictionary<string, object?> root) return null;
        if (root.TryGetValue("metadata", out var meta) && meta is Dictionary<string, object?> md && Bool(md, "instrumental")) instrumental = true;
        if (!root.TryGetValue("lines", out var linesNode) || linesNode is not List<object?> items) return null;
        var lines = new List<ParsedLine>();
        var agents = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item is not Dictionary<string, object?> l) continue;
            var start = Ms(l, "start_ms");
            if (start == null) continue;
            var pl = new ParsedLine { Start = start.Value, End = Ms(l, "end_ms"), Text = Str(l, "text") ?? "" };
            string? agent = Str(l, "agent") ?? Str(l, "singer") ?? Str(l, "voice") ?? Str(l, "vocalist");
            if (!string.IsNullOrEmpty(agent))
            {
                if (!agents.TryGetValue(agent, out int a)) agents[agent] = a = Math.Min(3, agents.Count + 1);
                pl.Agent = a;
            }
            if (l.TryGetValue("words", out var wn) && wn is List<object?> ws)
            {
                pl.Words = new();
                foreach (var w in ws)
                {
                    if (w is not Dictionary<string, object?> wd) continue;
                    var ws0 = Ms(wd, "start_ms");
                    string text = Str(wd, "text") ?? "";
                    if (ws0 == null || text.Length == 0) continue;
                    bool bg = Bool(wd, "background") || Bool(wd, "bg");
                    if (bg && !text.TrimStart().StartsWith('(')) text = "(" + text.TrimEnd() + ")" + (text.EndsWith(' ') ? " " : "");
                    pl.Words.Add((text, ws0.Value, Ms(wd, "end_ms")));
                }
                if (pl.Words.Count == 0) pl.Words = null;
            }
            if (Bool(l, "background") && !pl.Text.StartsWith('(')) pl.Text = "(" + pl.Text + ")";
            lines.Add(pl);
        }
        lines.Sort((a, b) => a.Start.CompareTo(b.Start));
        return lines;
    }

    private static string? Str(Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) ? v as string : null;

    private static double? Ms(Dictionary<string, object?> d, string key) =>
        Str(d, key) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ? ms / 1000.0 : null;

    private static bool Bool(Dictionary<string, object?> d, string key) => Str(d, key) is { } s && s.Equals("true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Just enough YAML for Lyricsfile documents: block mappings and sequences (including "- key: value" items and
/// sequences at their key's indent), plain / single-quoted / double-quoted scalars (also across lines) and literal or
/// folded block scalars. Every scalar comes back as a string.
/// </summary>
internal static class MiniYaml
{
    private readonly record struct Line(int Indent, string Text, string Raw);

    public static object? Parse(string yaml)
    {
        var lines = new List<Line>();
        foreach (var raw in yaml.Replace("\r", "").Split('\n'))
        {
            string t = raw.TrimStart(' ');
            lines.Add(new Line(raw.Length - t.Length, t.TrimEnd(), raw));
        }
        int i = 0;
        Skip(lines, ref i);
        if (i >= lines.Count) return null;
        try { return Node(lines, ref i, lines[i].Indent); }
        catch { return null; }
    }

    private static void Skip(List<Line> lines, ref int i)
    {
        while (i < lines.Count && (lines[i].Text.Length == 0 || lines[i].Text.StartsWith('#') || lines[i].Text == "---")) i++;
    }

    private static object? Node(List<Line> lines, ref int i, int indent)
    {
        Skip(lines, ref i);
        if (i >= lines.Count) return null;
        var l = lines[i];
        if (l.Text == "-" || l.Text.StartsWith("- ")) return Sequence(lines, ref i, l.Indent);
        if (KeyOf(l.Text, out _, out _)) return Mapping(lines, ref i, l.Indent);
        i++;
        return Scalar(l.Text, lines, ref i, indent);
    }

    private static List<object?> Sequence(List<Line> lines, ref int i, int indent)
    {
        var list = new List<object?>();
        while (true)
        {
            Skip(lines, ref i);
            if (i >= lines.Count) break;
            var l = lines[i];
            if (l.Indent != indent || !(l.Text == "-" || l.Text.StartsWith("- "))) break;
            string rest = l.Text.Length > 1 ? l.Text[2..].TrimStart() : "";
            if (rest.Length == 0) { i++; list.Add(Node(lines, ref i, indent + 1)); continue; }
            // "- key: value": the item is a mapping whose keys sit at the column after the dash.
            int col = indent + (l.Text.Length - rest.Length);
            lines[i] = new Line(col, rest, l.Raw);
            list.Add(Node(lines, ref i, col));
        }
        return list;
    }

    private static Dictionary<string, object?> Mapping(List<Line> lines, ref int i, int indent)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (true)
        {
            Skip(lines, ref i);
            if (i >= lines.Count) break;
            var l = lines[i];
            if (l.Indent != indent || !KeyOf(l.Text, out var key, out var value)) break;
            i++;
            if (value.Length == 0)
            {
                int j = i;
                Skip(lines, ref j);
                if (j < lines.Count && (lines[j].Indent > indent || lines[j].Indent == indent && (lines[j].Text == "-" || lines[j].Text.StartsWith("- "))))
                { i = j; map[key] = Node(lines, ref i, lines[j].Indent); }
                else map[key] = null;
            }
            else if (value[0] is '|' or '>') map[key] = Block(lines, ref i, indent, value);
            else map[key] = Scalar(value, lines, ref i, indent);
        }
        return map;
    }

    /// <summary>"key: value" (the key may be quoted); false for anything else.</summary>
    private static bool KeyOf(string text, out string key, out string value)
    {
        key = value = "";
        if (text.StartsWith("- ")) return false;
        int colon;
        if (text.StartsWith('\'') || text.StartsWith('"'))
        {
            int close = text.IndexOf(text[0], 1);
            if (close < 0 || close + 1 >= text.Length || text[close + 1] != ':') return false;
            key = text[1..close];
            colon = close + 1;
        }
        else
        {
            colon = -1;
            for (int k = 0; k < text.Length; k++)
                if (text[k] == ':' && (k + 1 == text.Length || text[k + 1] == ' ')) { colon = k; break; }
            if (colon <= 0) return false;
            key = text[..colon].Trim();
            if (key.Contains(' ') && key.Contains('\'')) return false;
        }
        value = colon + 1 < text.Length ? text[(colon + 1)..].Trim() : "";
        return true;
    }

    private static string Scalar(string first, List<Line> lines, ref int i, int indent)
    {
        if (first.StartsWith('\''))
        {
            var sb = new StringBuilder(first[1..]);
            while (!ClosesSingle(sb) && i < lines.Count) { sb.Append(' ').Append(lines[i].Text); i++; }
            string s = sb.ToString();
            int end = FindSingleEnd(s);
            return (end >= 0 ? s[..end] : s).Replace("''", "'");
        }
        if (first.StartsWith('"'))
        {
            var sb = new StringBuilder(first[1..]);
            while (FindDoubleEnd(sb.ToString()) < 0 && i < lines.Count) { sb.Append(' ').Append(lines[i].Text); i++; }
            string s = sb.ToString();
            int end = FindDoubleEnd(s);
            return Unescape(end >= 0 ? s[..end] : s);
        }
        // Plain: may continue on more-indented lines that aren't keys or items.
        var plain = new StringBuilder(StripComment(first));
        while (i < lines.Count && lines[i].Text.Length > 0 && lines[i].Indent > indent && !lines[i].Text.StartsWith("- ") && !KeyOf(lines[i].Text, out _, out _))
        { plain.Append(' ').Append(StripComment(lines[i].Text)); i++; }
        return plain.ToString().Trim();
    }

    private static string StripComment(string s)
    {
        int h = s.IndexOf(" #", StringComparison.Ordinal);
        return h >= 0 ? s[..h] : s;
    }

    private static bool ClosesSingle(StringBuilder sb) => FindSingleEnd(sb.ToString()) >= 0;

    private static int FindSingleEnd(string s)
    {
        for (int k = 0; k < s.Length; k++)
        {
            if (s[k] != '\'') continue;
            if (k + 1 < s.Length && s[k + 1] == '\'') { k++; continue; }
            return k;
        }
        return -1;
    }

    private static int FindDoubleEnd(string s)
    {
        for (int k = 0; k < s.Length; k++)
        {
            if (s[k] == '\\') { k++; continue; }
            if (s[k] == '"') return k;
        }
        return -1;
    }

    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int k = 0; k < s.Length; k++)
        {
            char c = s[k];
            if (c != '\\' || k + 1 >= s.Length) { sb.Append(c); continue; }
            char n = s[++k];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'u' when k + 4 < s.Length && int.TryParse(s.AsSpan(k + 1, 4), NumberStyles.HexNumber, null, out int cp):
                    sb.Append((char)cp); k += 4; break;
                default: sb.Append(n); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>"|" / "|-" / ">" block scalars: every following line indented deeper (blank lines kept).</summary>
    private static string Block(List<Line> lines, ref int i, int indent, string header)
    {
        bool folded = header[0] == '>', keep = header.Contains('+'), strip = header.Contains('-');
        var body = new List<string>();
        int blockIndent = -1;
        while (i < lines.Count)
        {
            var l = lines[i];
            if (l.Text.Length == 0) { body.Add(""); i++; continue; }
            if (l.Indent <= indent) break;
            if (blockIndent < 0) blockIndent = l.Indent;
            body.Add(l.Raw.Length >= blockIndent ? l.Raw[blockIndent..].TrimEnd() : l.Text);
            i++;
        }
        while (body.Count > 0 && body[^1].Length == 0 && !keep) body.RemoveAt(body.Count - 1);
        string text = folded ? string.Join(" ", body).Replace("  ", "\n") : string.Join("\n", body);
        return strip || !folded && !keep ? text : text + "\n";
    }
}
