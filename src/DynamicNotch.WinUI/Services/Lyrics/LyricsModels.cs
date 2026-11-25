namespace DynamicNotch.Services.Lyrics;

/// <summary>How precisely a song's lyrics are timed (best first).</summary>
public enum LyricsKind { None, Plain, LineSynced, WordSynced }

/// <summary>What the lyrics panel / closed notch should show for the current song.</summary>
public enum LyricsStatus { Idle, Loading, Ready, NotFound, Instrumental, Offline }

/// <summary>The track as the media session reports it, plus the cleaned-up fields used for matching.</summary>
public sealed record LyricsQuery(string Title, string Artist, string Album, double DurationSeconds, string RawTitle, string RawArtist)
{
    /// <summary>Cache key: the cleaned artist and title (normalised), so "Song - Remastered 2011" finds "Song".</summary>
    public string Key => TitleCleaner.Normalize(Artist) + "\u001f" + TitleCleaner.Normalize(Title);
}

/// <summary>
/// Lyrics exactly as a provider returned them (kept raw in the cache, so better parsing in a later version applies to
/// songs already cached).
/// </summary>
public sealed class LyricsRaw
{
    public string Provider { get; set; } = "";
    /// <summary>Shown at the end of the lyrics ("Lyrics from LRCLIB").</summary>
    public string Credit { get; set; } = "";
    public long Id { get; set; }
    public string TrackName { get; set; } = "";
    public string ArtistName { get; set; } = "";
    public string AlbumName { get; set; } = "";
    public double Duration { get; set; }
    public bool Instrumental { get; set; }
    public bool HasWordSync { get; set; }
    /// <summary>LRC text (line timestamps, sometimes enhanced word tags).</summary>
    public string? Synced { get; set; }
    public string? Plain { get; set; }
    /// <summary>LRCLIB's Lyricsfile YAML (word-level timings when <see cref="HasWordSync"/>).</summary>
    public string? Lyricsfile { get; set; }
}

/// <summary>One word (or a word fragment) of a line. Times are in seconds of song position.</summary>
public sealed class LyricWord
{
    public string Text { get; init; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public int Syllables { get; set; } = 1;
    /// <summary>The timing was estimated (line-synced source), so the vocal onsets may refine it.</summary>
    public bool Estimated { get; set; }
    /// <summary>Inside brackets: an ad-lib or backing vocal (drawn smaller and softer).</summary>
    public bool Backing { get; init; }
    /// <summary>A long, held note (the fill sweeps slowly, the letters wave, the glow builds).</summary>
    public bool Held { get; set; }
    /// <summary>A space follows (false inside CJK text, which has none between characters).</summary>
    public bool SpaceAfter { get; init; } = true;
    public double Duration => Math.Max(0, End - Start);
}

/// <summary>One lyric line, an instrumental break (three dots) or nothing else.</summary>
public sealed class LyricLine
{
    public int Index { get; set; }
    public double Start { get; set; }
    /// <summary>When the last word ends (the singing stops; a break may follow).</summary>
    public double End { get; set; }
    /// <summary>When the next line (or break) starts.</summary>
    public double NextStart { get; set; } = double.MaxValue;
    public string Text { get; init; } = "";
    public List<LyricWord> Words { get; init; } = new();
    /// <summary>0 = the lead (or unknown), 1 / 2 = first / second singer of a duet, 3 = everyone.</summary>
    public int Agent { get; init; }
    /// <summary>The whole line is a backing vocal / ad-lib.</summary>
    public bool IsBacking { get; init; }
    /// <summary>An instrumental break: three dots that fill until the next line.</summary>
    public bool IsInterlude { get; init; }
    /// <summary>Compositor values for this line ("s0", "e0", … per word), created by the view model on first display.</summary>
    public Microsoft.UI.Composition.CompositionPropertySet? Times { get; set; }
}

/// <summary>A song's lyrics, ready to show.</summary>
public sealed class LyricsDocument
{
    public LyricsKind Kind { get; init; }
    public List<LyricLine> Lines { get; init; } = new();
    public string Credit { get; init; } = "";
    public string Provider { get; init; } = "";
    /// <summary>More than one singer is marked (lines align left / right).</summary>
    public bool HasDuet { get; init; }
    public bool IsSynced => Kind is LyricsKind.LineSynced or LyricsKind.WordSynced;

    /// <summary>The item (line or break) playing at <paramref name="t"/>: the last one that has started (-1 before the first).</summary>
    public int ItemAt(double t)
    {
        if (!IsSynced || Lines.Count == 0 || t < Lines[0].Start) return -1;
        int lo = 0, hi = Lines.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Lines[mid].Start <= t) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}

/// <summary>A provider that can't be reached (offline, timeout, rate limited): nothing is cached as "not found".</summary>
public sealed class LyricsUnavailableException : Exception
{
    public LyricsUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>
/// A lyrics source. LRCLIB is the built-in one; another (for example the official Musixmatch API with a paid key) can be
/// added to <see cref="LyricsService"/>'s provider list later. Providers return null when they have nothing for the
/// song and throw <see cref="LyricsUnavailableException"/> when they couldn't be asked.
/// </summary>
public interface ILyricsProvider
{
    /// <summary>Short id stored in the cache ("lrclib").</summary>
    string Id { get; }
    Task<LyricsRaw?> FetchAsync(LyricsQuery query, CancellationToken ct);
}
