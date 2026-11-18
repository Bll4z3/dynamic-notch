using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DynamicNotch.Services;

/// <summary>The notch events that can make a sound (Settings ▸ Customisation ▸ Sounds).</summary>
public static class SoundEvents
{
    public static readonly (string Id, string Name, bool DefaultOn)[] All =
    {
        ("page", "Page change", true),
        ("expand", "Notch expand", false),     // frequent: off by default
        ("collapse", "Notch collapse", false),
        ("pill", "Pill appears", true),
        ("notification", "Notification received", true),
        ("plug", "Charger plugged in", true),
        ("unplug", "Charger unplugged", true),
        ("lowbattery", "Low battery", true),
        ("capson", "Caps Lock on", true),
        ("capsoff", "Caps Lock off", true),
        ("tick", "Volume / brightness tick", true),
        ("toggleon", "Toggle on", true),
        ("toggleoff", "Toggle off", true),
        ("asksent", "Ask message sent", true),
        ("askdone", "Ask answer complete", true),
        ("update", "Update available", true),
        ("error", "Error", true),
    };
}

/// <summary>
/// UI sounds: the active preset's buffers are read once from the embedded WAVs (synthesised by
/// tools/SoundGen), scaled to the notch's own volume once per volume change, and played from memory with
/// PlaySound (asynchronous, through the default output on the shared system-sounds stream: it never ducks
/// or pauses the user's music, and it follows the system mute). Rate-limited (≥ 60 ms between sounds, the
/// drag tick ≥ 120 ms); silent when sounds are off, the preset is Off, the event is switched off, or the
/// system output is muted. No audio stream stays open, so nothing runs between sounds.
/// </summary>
public sealed class SoundService
{
    private const int MinGapMs = 60, TickGapMs = 120;
    private readonly ThemeService _theme;
    private readonly AudioService _audio;
    private readonly Dictionary<string, byte[]> _scaled = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string _loadedPreset = "";
    private double _loadedVolume = -1;
    private long _last = -10_000, _lastTick = -10_000;
    private byte[]? _playing; // kept alive while PlaySound reads it

    public SoundService(ThemeService theme, AudioService audio)
    {
        _theme = theme;
        _audio = audio;
        theme.Changed += p => { if (p.HasFlag(ThemeService.Part.Sounds)) Invalidate(); };
    }

    /// <summary>Raised when a sound starts (with its length in ms): the Customisation preview pulses its bars.</summary>
    public event Action<string, int>? Played;

    public bool IsEventOn(string id) =>
        _theme.Current.SoundEvents.TryGetValue(id, out var on) ? on : SoundEvents.All.FirstOrDefault(e => e.Id == id).DefaultOn;

    /// <summary>Plays an event's sound if sounds are on for it (rate-limited).</summary>
    public void Play(string id)
    {
        var s = _theme.Current;
        if (!s.SoundsEnabled || s.SoundPreset == "off" || !IsEventOn(id)) return;
        PlayCore(s.SoundPreset, id, force: false);
    }

    /// <summary>Plays a sound for the preview / per-event play button, even if that event is switched off.</summary>
    public void Preview(string id, string? preset = null)
    {
        var p = preset ?? _theme.Current.SoundPreset;
        if (p == "off") return;
        PlayCore(p, id, force: true);
    }

    private void PlayCore(string preset, string id, bool force)
    {
        long now = _clock.ElapsedMilliseconds;
        if (!force && now - _last < MinGapMs) return;
        if (id == "tick" && now - _lastTick < TickGapMs) return;
        try { if (_audio.Current.Muted || _audio.Current.Level <= 0.001) return; } catch { }
        var wav = Get(preset, id);
        if (wav == null) return;
        _last = now;
        if (id == "tick") _lastTick = now;
        _playing = wav;
        try { PlaySound(wav, IntPtr.Zero, SND_ASYNC | SND_MEMORY | SND_NODEFAULT); } catch { return; }
        Played?.Invoke(id, WavLengthMs(wav));
    }

    private void Invalidate()
    {
        if (_loadedPreset != _theme.Current.SoundPreset || Math.Abs(_loadedVolume - _theme.Current.SoundVolume) > 0.001) _scaled.Clear();
    }

    private byte[]? Get(string preset, string id)
    {
        double volume = _theme.Current.SoundVolume;
        if (preset != _loadedPreset || Math.Abs(volume - _loadedVolume) > 0.001)
        {
            _scaled.Clear();
            _loadedPreset = preset;
            _loadedVolume = volume;
        }
        if (_scaled.TryGetValue(id, out var cached)) return cached;
        var raw = Load($"Sounds.{preset}_{id}.wav");
        if (raw == null) return null;
        // Modest by design: full slider = 70% of the synthesised level, with a gentle curve.
        var scaled = Scale(raw, 0.7 * volume * volume);
        _scaled[id] = scaled;
        return scaled;
    }

    private static byte[]? Load(string name)
    {
        using var s = typeof(SoundService).Assembly.GetManifestResourceStream(name);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>A copy of a 16-bit PCM WAV with its samples multiplied by <paramref name="gain"/>.</summary>
    private static byte[] Scale(byte[] wav, double gain)
    {
        var copy = (byte[])wav.Clone();
        const int dataStart = 44; // the generator writes the canonical 44-byte header
        for (int i = dataStart; i + 1 < copy.Length; i += 2)
        {
            short v = (short)(copy[i] | copy[i + 1] << 8);
            short g = (short)Math.Clamp(Math.Round(v * gain), short.MinValue, short.MaxValue);
            copy[i] = (byte)g;
            copy[i + 1] = (byte)(g >> 8);
        }
        return copy;
    }

    private static int WavLengthMs(byte[] wav)
    {
        int rate = BitConverter.ToInt32(wav, 24), bytesPerSec = BitConverter.ToInt32(wav, 28);
        return bytesPerSec > 0 ? (int)((wav.Length - 44) * 1000L / bytesPerSec) : 150;
    }

    [DllImport("winmm.dll", SetLastError = false)]
    private static extern bool PlaySound(byte[]? data, IntPtr module, uint flags);
    private const uint SND_ASYNC = 0x0001, SND_NODEFAULT = 0x0002, SND_MEMORY = 0x0004;
}
