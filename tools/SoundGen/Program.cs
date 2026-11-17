// Dynamic Notch sound generator.
// Every sound is synthesised here from oscillators (sine / FM / square / filtered noise) and envelopes, so the
// set is original and royalty-free. Six voices (presets) × 17 events, 40–300 ms each, 32 kHz mono 16-bit WAV.
// Usage: dotnet run --project tools/SoundGen [-- <output dir>]
using System.Text;

const int Rate = 32000;
string outDir = args.Length > 0 ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "DynamicNotch.WinUI", "Assets", "Sounds"));
Directory.CreateDirectory(outDir);

// Each event: notes as (semitones from the voice's base pitch, start s, length s) plus an overall gain.
var events = new (string id, (double semi, double at, double len)[] notes, double gain)[]
{
    ("page",         new[] { (0.0, 0.0, 0.055) }, 0.55),
    ("expand",       new[] { (0.0, 0.0, 0.07), (7.0, 0.045, 0.09) }, 0.5),
    ("collapse",     new[] { (7.0, 0.0, 0.07), (0.0, 0.045, 0.09) }, 0.45),
    ("pill",         new[] { (12.0, 0.0, 0.09) }, 0.6),
    ("notification", new[] { (7.0, 0.0, 0.11), (12.0, 0.075, 0.16) }, 0.7),
    ("plug",         new[] { (0.0, 0.0, 0.09), (4.0, 0.06, 0.09), (7.0, 0.12, 0.14) }, 0.65),
    ("unplug",       new[] { (7.0, 0.0, 0.09), (4.0, 0.06, 0.09), (0.0, 0.12, 0.14) }, 0.6),
    ("lowbattery",   new[] { (0.0, 0.0, 0.11), (-5.0, 0.13, 0.16) }, 0.7),
    ("capson",       new[] { (12.0, 0.0, 0.045) }, 0.5),
    ("capsoff",      new[] { (5.0, 0.0, 0.045) }, 0.45),
    ("tick",         new[] { (19.0, 0.0, 0.03) }, 0.3),
    ("toggleon",     new[] { (7.0, 0.0, 0.06) }, 0.5),
    ("toggleoff",    new[] { (0.0, 0.0, 0.06) }, 0.45),
    ("asksent",      new[] { (5.0, 0.0, 0.07), (12.0, 0.04, 0.1) }, 0.5),
    ("askdone",      new[] { (0.0, 0.0, 0.1), (7.0, 0.07, 0.1), (12.0, 0.14, 0.16) }, 0.6),
    ("update",       new[] { (4.0, 0.0, 0.1), (9.0, 0.08, 0.15) }, 0.6),
    ("error",        new[] { (0.0, 0.0, 0.1), (-1.0, 0.1, 0.14) }, 0.65),
};

var rng = new Random(20261117);
int files = 0;
foreach (var voice in new[] { "soft", "glass", "pop", "bubble", "retro", "mechanical" })
{
    foreach (var (id, notes, gain) in events)
    {
        double total = notes.Max(n => n.at + n.len) + Tail(voice);
        int count = (int)(total * Rate);
        var buf = new double[count];
        foreach (var (semi, at, len) in notes) Render(voice, buf, semi, at, len);
        // Event-specific colour on top of the voice.
        if (id == "asksent") Whoosh(buf, 0.12);
        if (id == "error" && voice != "mechanical") Buzz(buf);
        Normalise(buf, gain * VoiceGain(voice));
        Write(Path.Combine(outDir, $"{voice}_{id}.wav"), buf);
        files++;
    }
}
Console.WriteLine($"Wrote {files} sounds to {outDir}");

static double Tail(string voice) => voice switch { "glass" => 0.12, "soft" => 0.06, _ => 0.03 };
static double VoiceGain(string voice) => voice switch { "mechanical" => 0.9, "retro" => 0.55, "glass" => 0.75, _ => 0.8 };
static double BaseHz(string voice) => voice switch { "soft" => 660, "glass" => 1320, "pop" => 520, "bubble" => 440, "retro" => 880, _ => 300 };

void Render(string voice, double[] buf, double semi, double at, double len)
{
    double f0 = BaseHz(voice) * Math.Pow(2, semi / 12.0);
    int start = (int)(at * Rate), n = (int)((len + Tail(voice)) * Rate);
    double phase = 0, phase2 = 0, lp = 0;
    for (int i = 0; i < n && start + i < buf.Length; i++)
    {
        double t = i / (double)Rate, u = t / len;
        double v = 0;
        switch (voice)
        {
            case "soft": // pure sine, gentle attack, exponential fade
            {
                double env = Math.Min(1, t / 0.008) * Math.Exp(-t * 26);
                phase += 2 * Math.PI * f0 / Rate;
                v = env * (Math.Sin(phase) + 0.12 * Math.Sin(2 * phase));
                break;
            }
            case "glass": // inharmonic partials (like a struck glass), long ring
            {
                double env = Math.Min(1, t / 0.002) * Math.Exp(-t * 14);
                phase += 2 * Math.PI * f0 / Rate;
                v = env * (Math.Sin(phase) + 0.45 * Math.Sin(2.76 * phase) * Math.Exp(-t * 20) + 0.2 * Math.Sin(5.4 * phase) * Math.Exp(-t * 40));
                break;
            }
            case "pop": // sine with a fast downward pitch drop
            {
                double f = f0 * (1 + 0.9 * Math.Exp(-t * 90));
                double env = Math.Min(1, t / 0.001) * Math.Exp(-t * 45);
                phase += 2 * Math.PI * f / Rate;
                v = env * Math.Sin(phase);
                break;
            }
            case "bubble": // upward glide with a soft FM wobble
            {
                double f = f0 * (0.7 + 0.45 * Math.Min(1, u));
                double env = Math.Sin(Math.PI * Math.Min(1, t / (len + 0.02)));
                phase2 += 2 * Math.PI * f * 2 / Rate;
                phase += 2 * Math.PI * f / Rate + 0.35 * Math.Sin(phase2) / Rate * 400;
                v = env * Math.Sin(phase);
                break;
            }
            case "retro": // band-limited square (odd harmonics), stepped envelope
            {
                double env = t < len ? 1 : Math.Max(0, 1 - (t - len) / 0.02);
                phase += 2 * Math.PI * f0 / Rate;
                for (int h = 1; h <= 7; h += 2) v += Math.Sin(h * phase) / h;
                v *= env * 0.8;
                break;
            }
            default: // mechanical: a filtered noise click plus a short low thump
            {
                double click = Math.Exp(-t * 400) * (rng.NextDouble() * 2 - 1);
                lp += 0.25 * (click - lp);
                double thump = Math.Exp(-t * 60) * Math.Sin(2 * Math.PI * (f0 * 0.4 + 40 * semi / 12.0) * t);
                v = 1.4 * lp + 0.6 * thump + 0.5 * click * Math.Exp(-t * 900);
                break;
            }
        }
        buf[start + i] += v;
    }
}

void Whoosh(double[] buf, double len)
{
    double lp = 0;
    int n = Math.Min(buf.Length, (int)(len * Rate));
    for (int i = 0; i < n; i++)
    {
        double t = i / (double)Rate;
        double env = Math.Sin(Math.PI * t / len);
        lp += (0.05 + 0.4 * t / len) * ((rng.NextDouble() * 2 - 1) - lp); // brightening noise
        buf[i] += 0.35 * env * lp;
    }
}

static void Buzz(double[] buf)
{
    for (int i = 0; i < buf.Length; i++)
    {
        double t = i / (double)Rate;
        buf[i] *= 0.75 + 0.25 * Math.Sign(Math.Sin(2 * Math.PI * 38 * t)); // a soft rough edge
    }
}

static void Normalise(double[] buf, double gain)
{
    double peak = buf.Max(Math.Abs);
    if (peak <= 0) return;
    int fade = Math.Min(buf.Length, Rate / 200); // 5 ms fade-out: no click at the end
    for (int i = 0; i < buf.Length; i++)
    {
        double g = gain / peak;
        if (i >= buf.Length - fade) g *= (buf.Length - i) / (double)fade;
        buf[i] *= g;
    }
}

static void Write(string path, double[] buf)
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    int data = buf.Length * 2;
    w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data); w.Write(Encoding.ASCII.GetBytes("WAVE"));
    w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
    w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data);
    foreach (var s in buf) w.Write((short)Math.Clamp(Math.Round(s * short.MaxValue), short.MinValue, short.MaxValue));
    w.Flush();
    File.WriteAllBytes(path, ms.ToArray());
}
