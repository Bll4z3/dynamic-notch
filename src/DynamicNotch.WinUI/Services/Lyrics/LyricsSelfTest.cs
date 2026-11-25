using System.Diagnostics;
using System.IO;
using System.Text;

namespace DynamicNotch.Services.Lyrics;

/// <summary>
/// Dev: <c>DynamicNotch.exe --lyrics-selftest "Artist|Title|Album|Seconds" … --out report.txt</c> looks each song up
/// (cache, then LRCLIB), builds its lyrics and writes what came back: kind, lines, breaks, held notes and the first
/// lines' word timings. Runs headless before the single-instance check, so it works while the notch is running.
/// </summary>
internal static class LyricsSelfTest
{
    public static void Run(string[] args)
    {
        int outIdx = Array.FindIndex(args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase));
        string outPath = outIdx >= 0 && outIdx + 1 < args.Length ? args[outIdx + 1] : Path.Combine(Path.GetTempPath(), "lyrics-selftest.txt");
        var songs = args.SkipWhile(a => !a.Equals("--lyrics-selftest", StringComparison.OrdinalIgnoreCase)).Skip(1)
                        .TakeWhile(a => !a.StartsWith("--")).ToList();
        var sb = new StringBuilder();
        var service = new LyricsService();
        foreach (var spec in songs)
        {
            var p = spec.Split('|');
            string artist = p.ElementAtOrDefault(0) ?? "", title = p.ElementAtOrDefault(1) ?? "", album = p.ElementAtOrDefault(2) ?? "";
            double dur = double.TryParse(p.ElementAtOrDefault(3), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
            var q = new LyricsQuery(TitleCleaner.CleanTitle(title, artist), TitleCleaner.CleanArtist(artist), album, dur, title, artist);
            sb.AppendLine($"=== {artist} — {title}  [album '{album}', {dur}s]");
            sb.AppendLine($"    cleaned: '{q.Artist}' — '{q.Title}'");
            var sw = Stopwatch.StartNew();
            try
            {
                var r = service.LookupAsync(q, CancellationToken.None).GetAwaiter().GetResult();
                sb.AppendLine($"    lookup: {sw.ElapsedMilliseconds} ms, {(r.FromCache ? "cache" : "network")}, raw={(r.Raw == null ? "none" : $"'{r.Raw.ArtistName} — {r.Raw.TrackName}' ({r.Raw.AlbumName}, {r.Raw.Duration}s) wordSync={r.Raw.HasWordSync} synced={r.Raw.Synced != null} plain={r.Raw.Plain != null} instrumental={r.Raw.Instrumental}")}{(r.Unavailable ? " UNAVAILABLE" : "")}");
                if (r.Raw == null) { sb.AppendLine(); continue; }
                var doc = LyricsBuilder.Build(r.Raw, dur);
                if (doc == null) { sb.AppendLine("    document: null"); sb.AppendLine(); continue; }
                int lines = doc.Lines.Count(l => !l.IsInterlude), breaks = doc.Lines.Count(l => l.IsInterlude);
                int words = doc.Lines.Sum(l => l.Words.Count), held = doc.Lines.Sum(l => l.Words.Count(w => w.Held));
                int backing = doc.Lines.Sum(l => l.Words.Count(w => w.Backing));
                sb.AppendLine($"    document: {doc.Kind}, {lines} lines, {breaks} breaks, {words} words ({held} held, {backing} backing), duet={doc.HasDuet}, credit='{doc.Credit}'");
                foreach (var l in doc.Lines.Take(7))
                {
                    if (l.IsInterlude) { sb.AppendLine($"    [{l.Start,7:0.00} → {l.End,7:0.00}]  ••• (break {l.End - l.Start:0.0}s)"); continue; }
                    sb.AppendLine($"    [{l.Start,7:0.00} → {l.End,7:0.00}]  {l.Text}");
                    sb.AppendLine("        " + string.Join("  ", l.Words.Select(w => $"{w.Text}@{w.Start:0.00}+{w.Duration:0.00}{(w.Held ? "H" : "")}{(w.Backing ? "b" : "")}{(w.Estimated ? "" : "*")}")));
                }
                var heldWords = doc.Lines.SelectMany(l => l.Words).Where(w => w.Held).Take(6).Select(w => $"{w.Text}({w.Duration:0.0}s)");
                sb.AppendLine($"    held: {string.Join(", ", heldWords)}");
            }
            catch (Exception ex) { sb.AppendLine($"    ERROR {ex.GetType().Name}: {ex.Message}"); }
            sb.AppendLine();
        }
        File.WriteAllText(outPath, sb.ToString());
    }
}
