using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicNotch.Services.Lyrics;

/// <summary>
/// Turns what a player reports ("Song (feat. X) - Remastered 2011", "Artist - Song (Official Video)" from a browser tab)
/// into the plain title and artist a lyrics database knows, and scores how closely two titles / artists match.
/// </summary>
public static partial class TitleCleaner
{
    // Bracketed bits that never belong to the song's name.
    [GeneratedRegex(@"\s*[\(\[\{][^\)\]\}]*\b(feat\.?|ft\.?|featuring|with|prod\.?|remaster(ed)?|official|audio|video|lyrics?|visuali[sz]er|explicit|clean|hd|hq|4k|m/?v|mono|stereo|single version|album version|bonus track|from\s+[""“].*?)\b[^\)\]\}]*[\)\]\}]", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseBrackets();

    // " - Remastered 2011", " - 2011 Remaster", " - Single Version", " - From "Film"", " - Radio Edit" and similar suffixes.
    [GeneratedRegex(@"\s+[-–—]\s+((\d{4}\s+)?remaster(ed)?(\s+\d{4})?(\s+version)?|single version|album version|mono( version)?|stereo( version)?|explicit|clean|radio edit|original mix|bonus track|from\s+.+|edit)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseSuffix();

    // "feat. Someone" outside brackets, to the end.
    [GeneratedRegex(@"\s+(feat\.?|ft\.?|featuring)\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex LooseFeat();

    [GeneratedRegex(@"\s*(\bexplicit|\(e\)|\[e\]|🅴)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingExplicit();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>The song's own title.</summary>
    public static string CleanTitle(string title, string artist)
    {
        string t = (title ?? "").Trim();
        // Browser tabs ("Artist - Song (Official Video)"): drop the artist prefix when it is the reported artist.
        int dash = IndexOfDash(t);
        if (dash > 0)
        {
            string left = t[..dash].Trim(), right = t[(dash + 1)..].Trim(' ', '-', '–', '—');
            string a = CleanArtist(artist);
            if (a.Length > 0 && Similarity(Normalize(left), Normalize(a)) > 0.7 && right.Length > 0) t = right;
        }
        for (int i = 0; i < 3; i++)
        {
            string before = t;
            t = NoiseBrackets().Replace(t, "");
            t = NoiseSuffix().Replace(t, "");
            t = LooseFeat().Replace(t, "");
            t = TrailingExplicit().Replace(t, "");
            if (t == before) break;
        }
        t = Spaces().Replace(t, " ").Trim(' ', '-', '–', '—', '|', '·');
        return t.Length > 0 ? t : (title ?? "").Trim();
    }

    /// <summary>The artist as a database knows it ("Artist - Topic", "ArtistVEVO" from YouTube cleaned up).</summary>
    public static string CleanArtist(string artist)
    {
        string a = (artist ?? "").Trim();
        if (a.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase)) a = a[..^8];
        if (a.EndsWith("VEVO", StringComparison.Ordinal) && a.Length > 4) a = a[..^4];
        a = LooseFeat().Replace(a, "");
        return Spaces().Replace(a, " ").Trim();
    }

    /// <summary>The first artist of "A, B & C" / "A x B" / "A feat. B".</summary>
    public static string PrimaryArtist(string artist)
    {
        string a = CleanArtist(artist);
        foreach (var sep in new[] { ", ", " & ", " x ", " X ", " and ", "; ", " / ", " vs. ", " with " })
        {
            int i = a.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0) a = a[..i];
        }
        return a.Trim();
    }

    private static int IndexOfDash(string s)
    {
        foreach (var d in new[] { " - ", " – ", " — " })
        {
            int i = s.IndexOf(d, StringComparison.Ordinal);
            if (i > 0) return i + 1;
        }
        return -1;
    }

    /// <summary>Lower case, no accents or punctuation, "&" as "and", single spaces: for comparisons and cache keys.</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var d = s.ToLowerInvariant().Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        bool space = false;
        foreach (char ch in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) { sb.Append(ch); space = false; }
            else if (!space && sb.Length > 0) { sb.Append(' '); space = true; }
        }
        return sb.ToString().Trim();
    }

    /// <summary>0..1: how alike two normalised strings are (word overlap, with one containing the other counting high).</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        if (a.Contains(b) || b.Contains(a)) return 0.85 * Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length) + 0.15;
        var wa = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var wb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        int common = wa.Count(wb.Contains);
        return 2.0 * common / (wa.Count + wb.Count);
    }

    /// <summary>Artist match that tolerates "A, B" vs "A" (one artist of a list is enough for a good score).</summary>
    public static double ArtistSimilarity(string reported, string candidate)
    {
        string a = Normalize(CleanArtist(reported)), b = Normalize(candidate);
        double best = Similarity(a, b);
        string pa = Normalize(PrimaryArtist(reported)), pb = Normalize(PrimaryArtist(candidate));
        best = Math.Max(best, Similarity(pa, pb) * 0.95);
        if (pa.Length > 0 && b.Contains(pa)) best = Math.Max(best, 0.9);
        return best;
    }
}
