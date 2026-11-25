namespace DynamicNotch.Services.Lyrics;

/// <summary>The answer to a lookup: lyrics (or null for none), and whether no provider could be reached.</summary>
public readonly record struct LyricsLookup(LyricsRaw? Raw, bool Unavailable, bool FromCache);

/// <summary>
/// Finds lyrics for a song: the in-memory copies, then the disk cache (<see cref="LyricsCache"/>), then each provider in
/// turn (LRCLIB today; add more to <see cref="Providers"/>). Every network call is async with a timeout and can be
/// cancelled (a track change cancels the lookup for the previous song). Answers are cached, "not found" included,
/// except when no provider could be reached (offline): then nothing is cached and the song is asked for again later.
/// </summary>
public sealed class LyricsService
{
    /// <summary>Longest a whole lookup may take before the song is shown as having no lyrics.</summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(14);
    private const int MemoryEntries = 40;

    private readonly Dictionary<string, (LyricsRaw? Raw, double Duration, DateTimeOffset At)> _memory = new();
    private readonly LinkedList<string> _order = new();

    /// <summary>Providers in order of preference: the first that has the song wins.</summary>
    public List<ILyricsProvider> Providers { get; } = new() { new LrclibProvider() };

    public LyricsService()
    {
        LyricsCache.Cleared += () => { lock (_memory) { _memory.Clear(); _order.Clear(); } };
    }

    public async Task<LyricsLookup> LookupAsync(LyricsQuery q, CancellationToken ct)
    {
        lock (_memory)
        {
            if (_memory.TryGetValue(q.Key, out var m) && (m.Raw != null || DateTimeOffset.Now - m.At < LyricsCache.NotFoundTtl)
                && !(q.DurationSeconds > 0 && m.Duration > 0 && Math.Abs(q.DurationSeconds - m.Duration) > 4))
                return new LyricsLookup(m.Raw, false, true);
        }
        var cached = await LyricsCache.GetAsync(q);
        ct.ThrowIfCancellationRequested();
        if (cached != null)
        {
            Remember(q, cached.Raw);
            return new LyricsLookup(cached.Raw, false, true);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LookupTimeout);
        bool anyAnswered = false;
        foreach (var p in Providers)
        {
            try
            {
                var raw = await p.FetchAsync(q, timeout.Token);
                anyAnswered = true;
                if (raw != null)
                {
                    Remember(q, raw);
                    _ = LyricsCache.PutAsync(q, raw);
                    return new LyricsLookup(raw, false, false);
                }
            }
            catch (LyricsUnavailableException ex) { MusicReactiveService.Log($"lyrics: {p.Id} unavailable: {ex.Message}"); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { MusicReactiveService.Log($"lyrics: {p.Id} timed out"); }
        }
        ct.ThrowIfCancellationRequested();
        if (!anyAnswered) return new LyricsLookup(null, true, false);
        Remember(q, null);
        _ = LyricsCache.PutAsync(q, null);
        return new LyricsLookup(null, false, false);
    }

    private void Remember(LyricsQuery q, LyricsRaw? raw)
    {
        lock (_memory)
        {
            if (_memory.ContainsKey(q.Key)) _order.Remove(q.Key);
            _memory[q.Key] = (raw, q.DurationSeconds, DateTimeOffset.Now);
            _order.AddFirst(q.Key);
            while (_order.Count > MemoryEntries) { _memory.Remove(_order.Last!.Value); _order.RemoveLast(); }
        }
    }
}
