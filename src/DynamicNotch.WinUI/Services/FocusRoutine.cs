using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using DynamicNotch.Design;
using Windows.UI;

namespace DynamicNotch.Services;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum IntervalKind { Focus, ShortBreak, LongBreak }

/// <summary>One step of the focus routine (saved in settings.json).</summary>
public sealed class FocusInterval
{
    public IntervalKind Kind { get; set; }
    public int Minutes { get; set; }

    public FocusInterval() { }
    public FocusInterval(IntervalKind kind, int minutes) { Kind = kind; Minutes = minutes; }

    public FocusInterval Clone() => new(Kind, Minutes);
    [JsonIgnore] public TimeSpan Duration => TimeSpan.FromMinutes(Minutes);
}

/// <summary>Defaults, validation, labels, colours and formatting for the focus routine.</summary>
public static class FocusRoutine
{
    public const int MinMinutes = 1, MaxMinutes = 180, MaxIntervals = 24;

    /// <summary>Focus 25 · Short 5 · Focus 25 · Short 5 · Focus 25 · Short 5 · Focus 25 · Long 15.</summary>
    public static List<FocusInterval> Defaults() => new()
    {
        new(IntervalKind.Focus, 25), new(IntervalKind.ShortBreak, 5),
        new(IntervalKind.Focus, 25), new(IntervalKind.ShortBreak, 5),
        new(IntervalKind.Focus, 25), new(IntervalKind.ShortBreak, 5),
        new(IntervalKind.Focus, 25), new(IntervalKind.LongBreak, 15),
    };

    /// <summary>A usable copy: durations clamped to 1-180, at most 24 steps, never empty.</summary>
    public static List<FocusInterval> Sanitize(IEnumerable<FocusInterval>? routine)
    {
        var list = (routine ?? Enumerable.Empty<FocusInterval>())
            .Where(i => i != null && Enum.IsDefined(i.Kind))
            .Select(i => new FocusInterval(i.Kind, Math.Clamp(i.Minutes, MinMinutes, MaxMinutes)))
            .Take(MaxIntervals).ToList();
        return list.Count > 0 ? list : Defaults();
    }

    public static string Name(IntervalKind k) => k switch
    {
        IntervalKind.ShortBreak => "Short break",
        IntervalKind.LongBreak => "Long break",
        _ => "Focus",
    };

    public static (Color A, Color B) Colors(IntervalKind k) => k switch
    {
        IntervalKind.ShortBreak => (Theme.ShortBreakA, Theme.ShortBreakB),
        IntervalKind.LongBreak => (Theme.LongBreakA, Theme.LongBreakB),
        _ => (Theme.FocusA, Theme.FocusB),
    };

    public static TimeSpan Total(IReadOnlyList<FocusInterval> r) => TimeSpan.FromMinutes(r.Sum(i => i.Minutes));
    public static int FocusCount(IReadOnlyList<FocusInterval> r) => r.Count(i => i.Kind == IntervalKind.Focus);

    /// <summary>"2 h 10 min" / "25 min" / "3 h".</summary>
    public static string Duration(TimeSpan t)
    {
        int mins = (int)Math.Round(t.TotalMinutes);
        int h = mins / 60, m = mins % 60;
        if (h == 0) return $"{m} min";
        return m == 0 ? $"{h} h" : $"{h} h {m} min";
    }

    /// <summary>"2 h 10 min · 4 sessions" (the summary under the round panel and in Settings).</summary>
    public static string Summary(IReadOnlyList<FocusInterval> r)
    {
        int n = FocusCount(r);
        return $"{Duration(Total(r))} · {n} {(n == 1 ? "session" : "sessions")}";
    }

    /// <summary>Countdown digits: "18:42", or "1:05:00" from an hour up. Rounds up, so it reads 25:00 at the start.</summary>
    public static string Digits(TimeSpan remaining)
    {
        long s = Math.Max(0, (long)Math.Ceiling(remaining.TotalSeconds - 0.001));
        long h = s / 3600, m = s % 3600 / 60, sec = s % 60;
        return h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m:00}:{sec:00}";
    }
}

/// <summary>
/// The soft phase-end chime: two gentle sine notes with a slow fade, synthesised once in memory and
/// played asynchronously (no sound file, no extra dependency). Failures are ignored.
/// </summary>
public static class Chime
{
    private static byte[]? _wav; // kept alive: SND_MEMORY | SND_ASYNC reads it while playing

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern bool PlaySound(byte[]? data, IntPtr module, uint flags);
    private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;

    public static void Play()
    {
        try
        {
            _wav ??= Build();
            PlaySound(_wav, IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT);
        }
        catch { /* no audio device */ }
    }

    private static byte[] Build()
    {
        const int rate = 44100;
        const double length = 1.6, volume = 0.22;
        int n = (int)(rate * length);
        var samples = new short[n];
        // (frequency, start s) — E5 then A5, each a sine with its octave softly mixed in.
        (double f, double t0)[] notes = { (659.25, 0), (880.0, 0.18) };
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)rate, v = 0;
            foreach (var (f, t0) in notes)
            {
                double u = t - t0;
                if (u < 0) continue;
                double env = Math.Min(1, u / 0.012) * Math.Exp(-u * 3.2); // soft attack, long decay
                v += env * (Math.Sin(2 * Math.PI * f * u) + 0.18 * Math.Sin(4 * Math.PI * f * u));
            }
            samples[i] = (short)Math.Clamp(v * volume * 0.5 * short.MaxValue, short.MinValue, short.MaxValue);
        }
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = n * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
