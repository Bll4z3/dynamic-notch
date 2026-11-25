using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DynamicNotch.Services.Lyrics;

/// <summary>
/// Lyrics already fetched, one small JSON file per song in %LocalAppData%\DynamicNotch\lyrics, so a song loads
/// instantly the second time and works offline. "Not found" answers are kept too (for <see cref="NotFoundTtl"/>), so a
/// song without lyrics isn't asked for again on every play. All file work runs off the UI thread.
/// </summary>
public static class LyricsCache
{
    /// <summary>How long a "not found" answer is trusted before asking again.</summary>
    public static readonly TimeSpan NotFoundTtl = TimeSpan.FromDays(3);
    private const int FormatVersion = 1;

    public static string Folder { get; } = Path.Combine(SettingsService.DataFolder, "lyrics");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Raised after <see cref="ClearAsync"/> (the in-memory copies are dropped too).</summary>
    public static event Action? Cleared;

    public sealed class Entry
    {
        public int Version { get; set; } = FormatVersion;
        public string Key { get; set; } = "";
        public DateTimeOffset Fetched { get; set; }
        public bool Found { get; set; }
        /// <summary>The duration the song had when it was looked up (a different version of the same title won't match).</summary>
        public double Duration { get; set; }
        public LyricsRaw? Raw { get; set; }
    }

    private static string PathFor(string key)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(Folder, Convert.ToHexString(hash).ToLowerInvariant()[..24] + ".json");
    }

    /// <summary>The cached entry for this song (null: none, or a stale "not found", or another version of the song).</summary>
    public static Task<Entry?> GetAsync(LyricsQuery q) => Task.Run(() =>
    {
        try
        {
            var p = PathFor(q.Key);
            if (!File.Exists(p)) return null;
            var e = JsonSerializer.Deserialize<Entry>(File.ReadAllText(p), Json);
            if (e == null || e.Key != q.Key || e.Version != FormatVersion) return null;
            if (!e.Found && DateTimeOffset.Now - e.Fetched > NotFoundTtl) return null;
            if (q.DurationSeconds > 0 && e.Duration > 0 && Math.Abs(q.DurationSeconds - e.Duration) > 4) return null;
            return e;
        }
        catch { return null; }
    });

    public static Task PutAsync(LyricsQuery q, LyricsRaw? raw) => Task.Run(() =>
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var e = new Entry { Key = q.Key, Fetched = DateTimeOffset.Now, Found = raw != null, Duration = q.DurationSeconds, Raw = raw };
            var tmp = PathFor(q.Key) + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(e, Json));
            File.Move(tmp, PathFor(q.Key), overwrite: true);
        }
        catch { /* the cache is best-effort */ }
    });

    /// <summary>Files and bytes in the cache.</summary>
    public static Task<(int Files, long Bytes)> SizeAsync() => Task.Run(() =>
    {
        try
        {
            if (!Directory.Exists(Folder)) return (0, 0L);
            var files = new DirectoryInfo(Folder).GetFiles("*.json");
            return (files.Length, files.Sum(f => f.Length));
        }
        catch { return (0, 0L); }
    });

    /// <summary>Deletes every cached song (Settings ▸ Customisation ▸ Lyrics ▸ Clear lyrics cache).</summary>
    public static async Task<int> ClearAsync()
    {
        int n = await Task.Run(() =>
        {
            int count = 0;
            try
            {
                if (!Directory.Exists(Folder)) return 0;
                foreach (var f in Directory.GetFiles(Folder))
                {
                    try { File.Delete(f); count++; } catch { }
                }
            }
            catch { }
            return count;
        });
        Cleared?.Invoke();
        return n;
    }
}
