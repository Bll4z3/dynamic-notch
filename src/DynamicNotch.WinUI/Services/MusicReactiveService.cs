using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DynamicNotch.Services;

/// <summary>
/// One analysis result, published every hop (~5 ms) for the renderer to read on its next frame. Continuous values are
/// 0..1 (already auto-gained and attack / release smoothed); Kick / Snare / Hats are envelopes that jump on each onset
/// and decay; the counters tell the renderer a new onset happened (compare with the last value it saw).
/// </summary>
public sealed class MusicFrame
{
    public float Sub, Kick, Snare, Vocal, Hats, Energy, Build;
    public int KickCount, SnareCount, HatCount, DropCount;
    public float KickStrength, SnareStrength, HatStrength, DropStrength;
    /// <summary>Tempo: beats per minute (0 = not locked), confidence 0..1, the last on-grid beat (Stopwatch ticks).</summary>
    public float Bpm, BpmConfidence;
    public long BeatTicks;
    public bool Signal;
    /// <summary>
    /// Vocals in detail: Presence (0..1, is someone singing: harmonic, tonal, centred, syllable-rate modulated energy in
    /// ~300 Hz–3.5 kHz), Hold (0..1, a sustained, steady note), Pitch (0..1 over A2–A5, smoothed) and onsets of sung
    /// syllables (a counter plus <see cref="MusicReactiveService.CopyVocalOnsets"/> for their exact times).
    /// </summary>
    public float VocalPresence, VocalHold, VocalPitch, Harmonicity, Syllabic, VocalOnsetStrength;
    public int VocalOnsetCount;
    // ---- Raw detector values, for the debug overlay ----
    public float KickFlux, KickThreshold, SnareFlux, SnareThreshold, HatFlux, HatThreshold, Flatness, Centre, RmsDb;
}

/// <summary>
/// Listens to what the speakers / headphones are playing (WASAPI loopback of the default output device, any app) and
/// analyses it for the music-reactive glow, entirely on its own thread:
/// <list type="bullet">
/// <item>Low latency: packets are drained every ~3 ms (a high-resolution waitable timer, no global timer change) and
/// analysed every 256 samples (5.3 ms at 48 kHz) over a 1024-sample window, so an onset reaches the renderer within a
/// frame or two of the sound.</item>
/// <item>Onsets (kick, snare, hi-hats): spectral flux per band (the rise in log magnitude across that band's bins) against
/// an adaptive threshold (rolling mean + sensitivity × standard deviation), re-armed only after the flux falls back, so
/// quiet and loud songs both trigger cleanly without double hits. Once a tempo is found, kicks far off the beat grid
/// need a stronger hit.</item>
/// <item>Sustained levels (sub-bass, vocals, energy): band energy auto-gained against each band's own recent peak, with
/// a fast attack and a controlled release. Vocals are midrange energy weighted by tonality (low spectral flatness) and
/// by how centred it is in the stereo image (mid vs side), which tells a voice from noise, hats and wide synths.</item>
/// <item>Build-ups (loudness rising steadily for seconds) and drops (a sudden big jump after a build-up or a quiet
/// breakdown), and the tempo (BPM) from the spacing of kicks.</item>
/// </list>
/// The newest <see cref="MusicFrame"/> is swapped in atomically (<see cref="Latest"/>); the renderer reads it once per
/// frame. Capture runs only between <see cref="Start"/> and <see cref="Stop"/>; stopping releases the device. Loopback
/// reads the output mix, not the microphone, so it needs no permission (some DRM-protected video is silent to it).
/// <para>
/// Core Audio is called directly (not through a library): the app's <see cref="AudioService"/> already holds the device
/// enumerator, and .NET hands every caller the same wrapper object, which a library's own wrapper class can't be cast
/// from. Casting to interfaces works whatever wrapper that is.
/// </para>
/// </summary>
public sealed class MusicReactiveService : IDisposable
{
    // ---- Tuning ----
    private const int FftLog2 = 10, FftSize = 1 << FftLog2; // 1024-sample window (≈ 21 ms at 48 kHz)
    private const int VocalSize = 2048;                     // the vocal analysis window (≈ 43 ms, 23 Hz bins: harmonics resolve)
    private const int RingSize = VocalSize;                 // the sample ring holds the longest window
    private const int Hop = 256;                            // analyse every 256 samples (≈ 5.3 ms): 75% overlap
    private const int PollMs = 3;                           // how often packets are drained
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71"); // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

    private readonly object _gate = new();
    private readonly float[] _mid = new float[RingSize], _side = new float[RingSize];
    private int _write, _pending;

    // ---- Vocal onsets (sung syllables), with the time each was heard, for the lyrics' word timing ----
    private readonly (long Ticks, float Strength)[] _onsets = new (long, float)[64];
    private int _onsetCount;

    /// <summary>
    /// Copies vocal onsets newer than <paramref name="seen"/> (a running count; pass the value it returned last time,
    /// or -1 to only learn the current count). Each has the Stopwatch time it was heard and a strength 0..1.
    /// </summary>
    public int CopyVocalOnsets(int seen, List<(long Ticks, float Strength)> into)
    {
        lock (_onsets)
        {
            if (seen < 0 || _onsetCount - seen > _onsets.Length) return _onsetCount;
            for (int n = seen; n < _onsetCount; n++) into.Add(_onsets[n % _onsets.Length]);
            return _onsetCount;
        }
    }

    private void AddOnset(long ticks, float strength)
    {
        lock (_onsets) { _onsets[_onsetCount % _onsets.Length] = (ticks, strength); _onsetCount++; }
    }
    private CancellationTokenSource? _cts;
    private MusicFrame? _latest;

    /// <summary>The newest analysis (null before the first); safe to read from any thread.</summary>
    public MusicFrame? Latest => Volatile.Read(ref _latest);

    /// <summary>Beat sensitivity 0..1 (Settings): higher catches softer onsets. Read on every hop.</summary>
    public float Sensitivity { get => Volatile.Read(ref _sensitivity); set => Volatile.Write(ref _sensitivity, Math.Clamp(value, 0, 1)); }
    private float _sensitivity = 0.5f;

    public bool IsRunning => _cts != null;

    public void Start()
    {
        lock (_gate)
        {
            if (_cts != null) return;
            var cts = _cts = new CancellationTokenSource();
            var thread = new Thread(() => Run(cts.Token)) { IsBackground = true, Name = "Music-reactive glow", Priority = ThreadPriority.AboveNormal };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }
    }

    /// <summary>Stops capturing (the device is released on the analysis thread a moment later).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
        }
        Volatile.Write(ref _latest, null);
    }

    public void Dispose() => Stop();

    // ---- Capture loop ----------------------------------------------------------------------------------------
    private void Run(CancellationToken ct)
    {
        var analyser = new Analyser();
        Loopback? loop = null;
        long retryAt = 0, checkDeviceAt = 0, lastData = Environment.TickCount64;
        using var tick = HighResolutionTimer.Create();
        var clock = Stopwatch.StartNew();
        double lastLoop = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                long now = Environment.TickCount64;
                if (loop == null && now >= retryAt)
                {
                    loop = Loopback.TryOpen(out string? error);
                    if (loop == null) { retryAt = now + 2000; Log($"capture failed: {error}"); }
                    else Log($"capture started: {loop.Describe()}");
                    checkDeviceAt = now + 2000;
                }
                if (loop != null && now >= checkDeviceAt)
                {
                    checkDeviceAt = now + 2000;
                    if (!loop.IsStillDefault()) { Log("default output changed"); loop.Dispose(); loop = null; continue; }
                }
                if (tick != null ? !tick.Wait(PollMs, ct) : ct.WaitHandle.WaitOne(PollMs)) { if (ct.IsCancellationRequested) break; }

                double t = clock.Elapsed.TotalSeconds, elapsed = Math.Clamp(t - lastLoop, 0, 0.1);
                lastLoop = t;
                int rate = loop?.SampleRate ?? 48000;
                int got = 0;
                if (loop != null && !loop.Read((m, s) => { Append(m, s); got++; })) { Log("capture lost (device gone)"); loop.Dispose(); loop = null; retryAt = 0; }
                if (got > 0) lastData = Environment.TickCount64;
                // Loopback sends nothing at all while no app plays: feed silence in real time so everything decays.
                else if (Environment.TickCount64 - lastData > 60) for (int i = 0, n = (int)(elapsed * rate); i < n; i++) Append(0, 0);

                while (_pending >= Hop)
                {
                    _pending -= Hop;
                    // When this hop's sound was heard: samples still waiting, plus about a third of a window (the flux
                    // responds once the onset is well inside the window).
                    long heard = Stopwatch.GetTimestamp() - (long)((_pending + FftSize * 0.35) / rate * Stopwatch.Frequency);
                    var frame = analyser.Step(_mid, _side, _write, rate, Hop / (float)rate, Sensitivity);
                    if (frame.VocalOnsetStrength > 0) AddOnset(heard, frame.VocalOnsetStrength);
                    Volatile.Write(ref _latest, frame);
                    if (Profile && Environment.TickCount64 - _lastLog > 1000)
                    {
                        _lastLog = Environment.TickCount64;
                        Log($"sub={frame.Sub:F2} kick#{frame.KickCount} snare#{frame.SnareCount} hats#{frame.HatCount} vocal={frame.Vocal:F2} pres={frame.VocalPresence:F2} hold={frame.VocalHold:F2} harm={frame.Harmonicity:F2} syl={frame.Syllabic:F2} pitch={frame.VocalPitch:F2} von#{frame.VocalOnsetCount} (flat={frame.Flatness:F2} centre={frame.Centre:F2}) energy={frame.Energy:F2} build={frame.Build:F2} drops={frame.DropCount} bpm={frame.Bpm:F0} ({frame.BpmConfidence:F2}) rms={frame.RmsDb:F1}");
                    }
                }
            }
        }
        catch (Exception ex) { App.LogError(ex); }
        finally { loop?.Dispose(); }
    }

    private void Append(float mid, float side)
    {
        _mid[_write] = mid;
        _side[_write] = side;
        _write = (_write + 1) & (RingSize - 1);
        if (_pending < FftSize * 4) _pending++;
    }

    // ---- Profile-mode diagnostics (DYNAMICNOTCH_PROFILE=1 → frames.log) -------------------------------------------
    internal static readonly bool Profile = Environment.GetEnvironmentVariable("DYNAMICNOTCH_PROFILE") == "1";
    private long _lastLog;

    internal static void Log(string message)
    {
        if (!Profile) return;
        try { File.AppendAllText(Path.Combine(SettingsService.DataFolder, "frames.log"), $"[{DateTime.Now:HH:mm:ss.fff}] [glow] {message}{Environment.NewLine}"); }
        catch { }
    }

    // ---- A ~3 ms tick without changing the system timer resolution -----------------------------------------------
    private sealed class HighResolutionTimer : IDisposable
    {
        private readonly SafeWaitHandle _handle;
        private readonly TimerWaitHandle _wait;

        private sealed class TimerWaitHandle : WaitHandle
        {
            public TimerWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
        }

        private HighResolutionTimer(IntPtr handle)
        {
            _handle = new SafeWaitHandle(handle, ownsHandle: true);
            _wait = new TimerWaitHandle(_handle);
        }

        /// <summary>Null where high-resolution waitable timers don't exist (before Windows 10 1803): the caller then waits normally.</summary>
        public static HighResolutionTimer? Create()
        {
            const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 0x2, TIMER_ALL_ACCESS = 0x1F0003;
            var h = CreateWaitableTimerExW(IntPtr.Zero, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
            return h == IntPtr.Zero ? null : new HighResolutionTimer(h);
        }

        /// <summary>Waits about <paramref name="ms"/> (or until cancelled). False when cancelled.</summary>
        public bool Wait(int ms, CancellationToken ct)
        {
            long due = -ms * 10_000L; // relative, in 100 ns units
            if (!SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) return !ct.WaitHandle.WaitOne(ms);
            return WaitHandle.WaitAny(new[] { _wait, ct.WaitHandle }) == 0;
        }

        public void Dispose() { _wait.Dispose(); }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, bool resume);
    }

    // ---- WASAPI loopback (shared mode, polled) ----------------------------------------------------------------
    private sealed class Loopback : IDisposable
    {
        private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000, AUDCLNT_BUFFERFLAGS_SILENT = 0x2, CLSCTX_ALL = 0x17;
        private readonly IMMDeviceEnumerator _enumerator;
        private readonly IAudioClient _client;
        private readonly IAudioCaptureClient _capture;
        private readonly string _deviceId;
        private readonly int _channels, _bytesPerSample;
        private readonly bool _float;

        public int SampleRate { get; }

        private Loopback(IMMDeviceEnumerator enumerator, string deviceId, IAudioClient client, IAudioCaptureClient capture, int rate, int channels, int bits, bool isFloat)
        {
            _enumerator = enumerator; _deviceId = deviceId; _client = client; _capture = capture;
            SampleRate = rate; _channels = channels; _bytesPerSample = bits / 8; _float = isFloat;
        }

        public static Loopback? TryOpen(out string? error)
        {
            error = null;
            IntPtr fmt = IntPtr.Zero;
            try
            {
                var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
                enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device);
                device.GetId(out string id);
                var iid = typeof(IAudioClient).GUID;
                device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object o);
                var client = (IAudioClient)o;
                client.GetMixFormat(out fmt);
                int tag = Marshal.ReadInt16(fmt, 0), channels = Marshal.ReadInt16(fmt, 2), rate = Marshal.ReadInt32(fmt, 4), bits = Marshal.ReadInt16(fmt, 14);
                bool isFloat = tag == 3 || tag == unchecked((short)0xFFFE) && Marshal.PtrToStructure<Guid>(fmt + 24) == IeeeFloatSubtype;
                // A short buffer (40 ms): packets are drained every few ms anyway, so nothing waits in it.
                client.Initialize(0 /* shared */, AUDCLNT_STREAMFLAGS_LOOPBACK, 400_000, 0, fmt, IntPtr.Zero);
                var ciid = typeof(IAudioCaptureClient).GUID;
                client.GetService(ref ciid, out object c);
                client.Start();
                return new Loopback(enumerator, id, client, (IAudioCaptureClient)c, rate, channels, bits, isFloat);
            }
            catch (Exception ex) { error = $"{ex.GetType().Name}: {ex.Message}"; return null; }
            finally { if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt); }
        }

        public string Describe() => $"{SampleRate} Hz, {_channels} ch, {_bytesPerSample * 8}-bit {(_float ? "float" : "PCM")}";

        public bool IsStillDefault()
        {
            try { _enumerator.GetDefaultAudioEndpoint(0, 1, out var d); d.GetId(out string id); return id == _deviceId; }
            catch { return false; }
        }

        /// <summary>Drains every waiting packet as (mid, side) pairs. False when the device is gone.</summary>
        public bool Read(Action<float, float> sample)
        {
            try
            {
                while (true)
                {
                    _capture.GetNextPacketSize(out uint packet);
                    if (packet == 0) return true;
                    _capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
                    bool silent = (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                    int ch = Math.Max(1, _channels), bps = _bytesPerSample;
                    for (int i = 0; i < frames; i++)
                    {
                        if (silent) { sample(0, 0); continue; }
                        float l = Sample(data, (i * ch) * bps), r = ch > 1 ? Sample(data, (i * ch + 1) * bps) : l;
                        float rest = 0;
                        for (int c = 2; c < ch; c++) rest += Sample(data, (i * ch + c) * bps);
                        sample((l + r) * 0.5f + rest / ch, (l - r) * 0.5f);
                    }
                    _capture.ReleaseBuffer(frames);
                }
            }
            catch (COMException) { return false; } // AUDCLNT_E_DEVICE_INVALIDATED and friends
        }

        private float Sample(IntPtr data, int offset)
        {
            IntPtr p = data + offset;
            return _float && _bytesPerSample == 4 ? BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p))
                 : _bytesPerSample == 2 ? Marshal.ReadInt16(p) / 32768f
                 : _bytesPerSample == 4 ? Marshal.ReadInt32(p) / 2147483648f
                 : 0;
        }

        public void Dispose()
        {
            try { _client.Stop(); } catch { }
            try { Marshal.ReleaseComObject(_capture); } catch { }
            try { Marshal.ReleaseComObject(_client); } catch { }
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        void GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        void Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
        void GetBufferSize(out uint frames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        void GetMixFormat(out IntPtr format);
        void GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr handle);
        void GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint frames);
        void GetNextPacketSize(out uint frames);
    }

    // =================================================================================================================
    // Analysis
    // =================================================================================================================
    private sealed class Analyser
    {
        private readonly float[] _window = new float[FftSize];
        private readonly float[] _re = new float[FftSize], _im = new float[FftSize], _sre = new float[FftSize], _sim = new float[FftSize];
        private readonly float[] _mag = new float[FftSize / 2], _sideMag = new float[FftSize / 2], _log = new float[FftSize / 2], _prevLog = new float[FftSize / 2];
        private readonly float _windowGain;

        // ---- Tuning: onset detectors (flux statistics memory, refractory gap, minimum flux, share of the recent peak) ----
        private readonly Onset _kick = new(tau: 1.2f, refractory: 0.16f, minFlux: 0.05f, peakShare: 0.22f);
        private readonly Onset _snareCrack = new(tau: 1.2f, refractory: 0.14f, minFlux: 0.03f, peakShare: 0.3f);
        private readonly Onset _snareBody = new(tau: 1.2f, refractory: 0.0f, minFlux: 0.02f, peakShare: 0f);
        private readonly Onset _hats = new(tau: 1.0f, refractory: 0.06f, minFlux: 0.025f, peakShare: 0.2f);
        // ---- Tuning: transient gates (how many dB a band must jump within ~10 ms vs its ~150 ms average) ----
        private const float KickJumpDb = 1.2f, SnareJumpDb = 1.5f;
        // ---- Tuning: sustained levels (auto-gain range in dB, attack / release in seconds, response curve) ----
        private readonly Level _sub = new(rangeDb: 20, attack: 0.03f, release: 0.30f, curve: 1.4f);
        private readonly Level _vocalLevel = new(rangeDb: 14, attack: 0.10f, release: 0.50f, curve: 1.0f, peakFall: 1.5f);
        private readonly Level _vocalShare = new(rangeDb: 9, attack: 0.15f, release: 0.60f, curve: 1.3f, peakFall: 0.8f);
        private readonly Level _energy = new(rangeDb: 24, attack: 1.2f, release: 2.5f, curve: 1.2f, peakFall: 1.0f);

        private float _kickEnv, _snareEnv, _hatEnv, _vocal, _build, _silentFor = 10, _warm;
        private float _eFast = -90, _eMed = -90, _eLong = -90, _slope;
        private float _lowFast = -90, _lowSlow = -90, _crackFast = -90, _crackSlow = -90;
        private double _now, _dipAt = -100, _lastDrop = -100;
        private int _kicks, _snares, _hatsCount, _drops;
        private bool _signal;
        private readonly Tempo _tempo = new();

        public Analyser()
        {
            for (int i = 0; i < FftSize; i++) { _window[i] = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (FftSize - 1))); _windowGain += _window[i]; }
        }

        public MusicFrame Step(float[] mid, float[] side, int write, int rate, float dt, float sensitivity)
        {
            _now += dt;
            double sq = 0;
            for (int i = 0; i < FftSize; i++)
            {
                int j = (write - FftSize + i) & (RingSize - 1); // the newest FftSize samples, oldest → newest
                float m = mid[j], s = side[j];
                sq += m * m;
                _re[i] = m * _window[i]; _im[i] = 0;
                _sre[i] = s * _window[i]; _sim[i] = 0;
            }
            float rmsDb = Db((float)Math.Sqrt(sq / FftSize));
            Fft(_re, _im);
            Fft(_sre, _sim);
            float norm = 2f / _windowGain;
            for (int k = 0; k < FftSize / 2; k++)
            {
                _mag[k] = MathF.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * norm;
                _sideMag[k] = MathF.Sqrt(_sre[k] * _sre[k] + _sim[k] * _sim[k]) * norm;
                _log[k] = MathF.Log10(1 + 1000 * _mag[k]); // log-compressed magnitude: flux follows loudness changes, not absolute level
            }
            float binHz = (float)rate / FftSize;
            int Bin(float hz) => Math.Clamp((int)MathF.Round(hz / binHz), 1, FftSize / 2 - 1);

            // ---- Silence gate; the loudness trackers restart when music starts (never climbing up from silence) ----
            float rms = Math.Max(rmsDb, -70);
            if (rmsDb > -58) { _silentFor = 0; _signal = true; }
            else if ((_silentFor += dt) > 1.0f) _signal = false;
            bool live = rmsDb > -66;
            if (!_signal) _warm = 0;
            else if (_warm == 0) { _eFast = _eMed = _eLong = rms; _slope = 0; _build = 0; _warm = dt; }
            else _warm += dt;

            // ---- Sensitivity → how many standard deviations above the rolling mean an onset needs ----
            float sigma = 3.0f - 1.8f * sensitivity; // 0 → 3σ (only clear hits) … 0.5 → 2.1σ … 1 → 1.2σ (soft ones too)

            // ---- Kick: a flux onset in ~45–120 Hz AND a sudden jump in that band's level (a kick's attack is abrupt,
            //      a bass note swells); off the tempo grid it needs a stronger hit ----
            float kickFlux = Flux(Bin(45), Bin(120));
            float lowDb = Math.Max(Db(BandEnergy(_mag, Bin(40), Bin(130))), -80);
            _lowFast = Ema(_lowFast, lowDb, 0.010f, dt);
            _lowSlow = Ema(_lowSlow, lowDb, 0.150f, dt);
            float gridGate = _tempo.Locked && !_tempo.NearBeat(_now, 0.18) ? 1.35f : 1f;
            bool kick = _kick.Step(kickFlux, _now, sigma, dt, out float kickStrength, gridGate, allowed: live && _lowFast - _lowSlow > KickJumpDb);
            if (kick) { _kicks++; _kickEnv = Math.Max(_kickEnv, kickStrength); _tempo.Onset(_now, kickStrength); }
            _kickEnv *= MathF.Exp(-dt / 0.13f); // snappy

            // ---- Snare / clap: a crack (2–5 kHz) onset with a sudden jump, while the body (150–300 Hz) rises clearly ----
            float crackFlux = Flux(Bin(2000), Bin(5000)), bodyFlux = Flux(Bin(150), Bin(300));
            float crackDb = Math.Max(Db(BandEnergy(_mag, Bin(2000), Bin(5000))), -90);
            _crackFast = Ema(_crackFast, crackDb, 0.010f, dt);
            _crackSlow = Ema(_crackSlow, crackDb, 0.150f, dt);
            bool bodyRising = _snareBody.Rising(bodyFlux, dt, sigmas: 0.5f);
            bool snare = _snareCrack.Step(crackFlux, _now, sigma, dt, out float snareStrength,
                                          allowed: live && bodyRising && _crackFast - _crackSlow > SnareJumpDb);
            if (snare) { _snares++; _snareEnv = Math.Max(_snareEnv, snareStrength); }
            _snareEnv *= MathF.Exp(-dt / 0.16f);

            // ---- Hi-hats / cymbals: onsets in 6–16 kHz ----
            float hatFlux = Flux(Bin(6000), Bin(16000));
            bool hat = _hats.Step(hatFlux, _now, sigma * 1.05f, dt, out float hatStrength, allowed: live);
            if (hat) { _hatsCount++; _hatEnv = Math.Max(_hatEnv, hatStrength); }
            _hatEnv *= MathF.Exp(-dt / 0.12f);

            // ---- Sub-bass / bassline: sustained energy 20–250 Hz ----
            float sub = _sub.Step(live ? Db(BandEnergy(_mag, Bin(20), Bin(250))) : -120, dt);

            // ---- Vocals: centred energy in ~300 Hz–3.5 kHz (mid minus side removes wide instruments) and its share of
            //      the mix, gated by how voice-like it is (see Voice): high while someone sings, calm when they stop ----
            int va = Bin(300), vb = Bin(3500);
            double logSum = 0, sum = 0, sideSum = 0, centrePow = 0;
            for (int i = va; i <= vb; i++)
            {
                double p = _mag[i] * _mag[i] + 1e-12, sp = _sideMag[i] * _sideMag[i];
                logSum += Math.Log(p); sum += p; sideSum += sp;
                centrePow += Math.Max(0, p - 1.2 * sp);
            }
            int nb = vb - va + 1;
            float flatness = (float)(Math.Exp(logSum / nb) / (sum / nb));
            float centre = (float)(sum / (sum + sideSum + 1e-12));
            float centreDb = Db((float)Math.Sqrt(centrePow));
            float vocalLevel = _vocalLevel.Step(live ? centreDb : -120, dt);
            float share = _vocalShare.Step(live ? centreDb - Math.Max(rmsDb, -80) : -60, dt);
            var voice = Voice(mid, write, rate, dt, live, centre, share, drumHit: kick || snare);
            float vocalTarget = MathF.Pow(vocalLevel, 0.65f) * (0.45f + 0.55f * share) * MathF.Pow(voice.Presence, 0.6f);
            if (kick || snare) vocalTarget = Math.Min(vocalTarget, _vocal); // drums aren't singing
            // A quick swell when the voice comes in, a gentle settle when it stops.
            _vocal += (vocalTarget - _vocal) * (1 - MathF.Exp(-dt / (vocalTarget > _vocal ? 0.06f : 0.32f)));

            // ---- Overall energy: loudness over a few seconds, auto-gained ----
            float energy = _energy.Step(live ? rmsDb : -120, dt);

            // ---- Build-ups (loudness rising steadily for seconds) and drops (a sudden big jump after a build-up or a
            //      quiet breakdown); only once the song has been playing for a few seconds ----
            float eMedPrev = _eMed;
            _eFast = Ema(_eFast, rms, 0.12f, dt);
            _eMed = Ema(_eMed, rms, 0.8f, dt);
            _eLong = Ema(_eLong, rms, 6f, dt);
            _slope = Ema(_slope, (_eMed - eMedPrev) / dt, 1.5f, dt); // dB per second, smoothed
            bool settled = _warm > 4;
            if (settled && _eMed < _eLong - 5) _dipAt = _now;     // a quiet breakdown
            if (settled && live && _slope > 0.7f && _eMed > _eLong - 8) _build = Math.Min(1, _build + dt / 4f);
            else _build = Math.Max(0, _build - dt / 1.5f);
            bool drop = settled && kick && kickStrength > 0.7f && _eFast - _eMed > 3.5f
                        && (_build > 0.35f || _now - _dipAt < 6) && _now - _lastDrop > 6;
            float dropStrength = 0;
            if (drop) { _drops++; _lastDrop = _now; dropStrength = Math.Clamp(0.6f + _build * 0.4f, 0.6f, 1f); _build = 0; }

            _prevLogSwap();
            return new MusicFrame
            {
                Sub = sub, Kick = _kickEnv, Snare = _snareEnv, Vocal = Math.Clamp(_vocal, 0, 1), Hats = _hatEnv, Energy = energy, Build = _build,
                KickCount = _kicks, SnareCount = _snares, HatCount = _hatsCount, DropCount = _drops,
                KickStrength = kick ? kickStrength : 0, SnareStrength = snare ? snareStrength : 0, HatStrength = hat ? hatStrength : 0, DropStrength = dropStrength,
                Bpm = _tempo.Locked ? _tempo.Bpm : 0, BpmConfidence = _tempo.Confidence, BeatTicks = _tempo.BeatTicks(_now),
                Signal = _signal,
                VocalPresence = voice.Presence, VocalHold = voice.Hold, VocalPitch = voice.Pitch, Harmonicity = voice.Harmonicity,
                Syllabic = voice.Syllabic, VocalOnsetStrength = voice.Onset, VocalOnsetCount = _vocalOnsets,
                KickFlux = kickFlux, KickThreshold = _kick.Threshold, SnareFlux = crackFlux, SnareThreshold = _snareCrack.Threshold,
                HatFlux = hatFlux, HatThreshold = _hats.Threshold, Flatness = flatness, Centre = centre, RmsDb = rmsDb,
            };
        }

        private void _prevLogSwap() => Array.Copy(_log, _prevLog, _log.Length);

        // =============================================================================================================
        // Voice: a 2048-sample spectrum of the mid channel (23 Hz bins, so a singer's harmonics separate)
        // =============================================================================================================
        // ---- Tuning: harmonic salience (ratio of the harmonics' level to the band's mean) that counts as fully harmonic,
        //      syllable-rate modulation (dB) that counts as fully "speech-like", presence attack / release (s) ----
        private const float HarmonicFloor = 1.15f, HarmonicRange = 0.6f;
        private const float ModulationFloorDb = 0.8f, ModulationRangeDb = 2.6f;
        private const float PresenceAttack = 0.10f, PresenceRelease = 0.45f;

        private readonly float[] _vWindow = new float[VocalSize];
        private readonly float[] _vre = new float[VocalSize], _vim = new float[VocalSize];
        private readonly float[] _vlog = new float[VocalSize / 2], _vprev = new float[VocalSize / 2];
        private float _vWindowGain;
        private readonly Onset _vocalOnset = new(tau: 0.8f, refractory: 0.1f, minFlux: 0.012f, peakShare: 0.18f);
        private float _presence, _hold, _semis = -1, _semisSlow, _pitchOut, _envFast = -90, _envSlow = -90, _modAbs;
        private double _lastVocalOnset = -10;
        private int _vocalOnsets;

        private readonly record struct VoiceResult(float Presence, float Hold, float Pitch, float Harmonicity, float Syllabic, float Onset);

        /// <summary>
        /// Is someone singing, and how: a voice is harmonic (a pitch with many harmonics standing well above the band),
        /// tonal (low spectral flatness), centred in the stereo image and modulated at syllable rate (3–8 Hz); a pad is
        /// harmonic but steady, drums and hats are neither. Also the pitch (smoothed, octave slips folded), held notes
        /// (steady pitch and level, no new syllable) and syllable onsets (a rise across the band, or a clear pitch step
        /// on a legato phrase).
        /// </summary>
        private VoiceResult Voice(float[] mid, int write, int rate, float dt, bool live, float centre, float share, bool drumHit)
        {
            if (_vWindowGain == 0)
                for (int i = 0; i < VocalSize; i++) { _vWindow[i] = 0.5f * (1 - MathF.Cos(2 * MathF.PI * i / (VocalSize - 1))); _vWindowGain += _vWindow[i]; }
            for (int i = 0; i < VocalSize; i++)
            {
                int j = (write - VocalSize + i) & (RingSize - 1);
                _vre[i] = mid[j] * _vWindow[i];
                _vim[i] = 0;
            }
            Fft(_vre, _vim);
            float binHz = (float)rate / VocalSize, norm = 2f / _vWindowGain;
            int top = Math.Min(VocalSize / 2 - 2, (int)(4200 / binHz) + 2);
            int a = (int)(300 / binHz), b = (int)(3500 / binHz), la = (int)(150 / binHz), lb = (int)(4000 / binHz);
            double flatLog = 0, flatSum = 0, bandLog = 0, energy = 0;
            int flatN = 0, bandN = 0;
            for (int k = 1; k <= top; k++)
            {
                float m = MathF.Sqrt(_vre[k] * _vre[k] + _vim[k] * _vim[k]) * norm;
                _vlog[k] = MathF.Log10(1 + 1000 * m);
                if (k >= a && k <= b) { double p = (double)m * m + 1e-12; flatLog += Math.Log(p); flatSum += p; energy += p; flatN++; }
                if (k >= la && k <= lb) { bandLog += _vlog[k]; bandN++; }
            }
            float vflat = flatN > 0 ? (float)(Math.Exp(flatLog / flatN) / (flatSum / flatN)) : 1;
            float meanLog = (float)(bandLog / Math.Max(1, bandN));

            // Harmonic salience: for candidate pitches A2–A5 (1/16 octave apart), the weighted mean log level at the first
            // harmonics (the louder of each bin pair, so vibrato never falls between bins) against the band's mean.
            float best = 0, bestF0 = 0;
            for (int c = 0; c < 48; c++)
            {
                float f0 = 110f * MathF.Pow(2, c / 16f), s = 0, w = 0, wt = 1;
                for (int h = 1; h <= 12; h++, wt *= 0.86f)
                {
                    float f = f0 * h;
                    if (f > 4000) break;
                    if (f < 150) continue;
                    int k0 = (int)(f / binHz);
                    s += Math.Max(_vlog[k0], _vlog[k0 + 1]) * wt;
                    w += wt;
                }
                if (w <= 0) continue;
                s /= w;
                if (s > best) { best = s; bestF0 = f0; }
            }
            float harmonic = live && meanLog > 0.02f ? Math.Clamp((best / (meanLog + 1e-3f) - HarmonicFloor) / HarmonicRange, 0, 1) : 0;

            // Syllable-rate modulation of the band's level: a voice rises and falls 3–8 times a second.
            float eDb = Db((float)Math.Sqrt(energy));
            _envFast = Ema(_envFast, Math.Max(eDb, -90), 0.03f, dt);
            _envSlow = Ema(_envSlow, Math.Max(eDb, -90), 0.35f, dt);
            _modAbs += (MathF.Abs(_envFast - _envSlow) - _modAbs) * (1 - MathF.Exp(-dt / 0.5f));
            float syllabic = live ? Math.Clamp((_modAbs - ModulationFloorDb) / ModulationRangeDb, 0, 1) : 0;

            // Presence: every cue at once (a product, so a pad or a guitar alone doesn't read as a voice).
            float tonal = Math.Clamp((0.5f - vflat) / 0.32f, 0, 1);
            float centred = Math.Clamp((centre - 0.55f) / 0.3f, 0, 1);
            float target = live
                ? Math.Clamp(1.8f * MathF.Pow(harmonic, 0.7f) * (0.35f + 0.65f * tonal) * (0.4f + 0.6f * centred) * (0.55f + 0.45f * syllabic) * Math.Clamp(share * 1.6f, 0, 1), 0, 1)
                : 0;
            _presence += (target - _presence) * (1 - MathF.Exp(-dt / (target > _presence ? PresenceAttack : PresenceRelease)));

            // Pitch: smoothed, with octave slips folded back (the detector can jump an octave on the same note).
            if (harmonic > 0.3f && bestF0 > 0)
            {
                float semis = 12 * MathF.Log2(bestF0 / 110f);
                if (_semis < 0) _semis = _semisSlow = semis;
                float d = semis - _semis;
                if (MathF.Abs(MathF.Abs(d) - 12) < 0.8f) semis -= 12 * MathF.Sign(d);
                _semis += (semis - _semis) * (1 - MathF.Exp(-dt / 0.04f));
                _semisSlow += (semis - _semisSlow) * (1 - MathF.Exp(-dt / 0.2f));
            }
            _pitchOut += (Math.Clamp(Math.Max(0, _semis) / 36f, 0, 1) - _pitchOut) * (1 - MathF.Exp(-dt / 0.12f));

            // Syllable onsets: a rise across the band while singing (drum hits excluded), or a pitch step on a legato phrase.
            float vflux = 0;
            for (int k = a; k <= b; k++) { float dd = _vlog[k] - _vprev[k]; if (dd > 0) vflux += dd; }
            vflux /= Math.Max(1, b - a + 1);
            Array.Copy(_vlog, _vprev, top + 1);
            float onset = 0;
            if (_vocalOnset.Step(vflux, _now, 1.6f, dt, out float vs, allowed: live && _presence > 0.22f && !drumHit && vflat < 0.5f))
                onset = vs * (0.6f + 0.4f * _presence);
            else if (_presence > 0.45f && harmonic > 0.4f && MathF.Abs(_semis - _semisSlow) > 1.6f && _now - _lastVocalOnset > 0.14)
            {
                onset = 0.45f;
                _semisSlow = _semis; // one step, one onset
            }
            if (onset > 0) { _vocalOnsets++; _lastVocalOnset = _now; }

            // Held note: singing, steady pitch and level, no new syllable for a moment.
            bool steady = _presence > 0.45f && harmonic > 0.3f && MathF.Abs(_semis - _semisSlow) < 0.8f && _now - _lastVocalOnset > 0.22 && MathF.Abs(_envFast - _envSlow) < 4;
            _hold += ((steady ? 1 : 0) - _hold) * (1 - MathF.Exp(-dt / (steady ? 0.3f : 0.12f)));

            return new VoiceResult(Math.Clamp(_presence, 0, 1), _hold, _pitchOut, harmonic, syllabic, onset);
        }

        /// <summary>Spectral flux of a band: the mean rise in log magnitude across its bins since the last hop.</summary>
        private float Flux(int a, int b)
        {
            float f = 0;
            for (int i = a; i <= b; i++) { float d = _log[i] - _prevLog[i]; if (d > 0) f += d; }
            return f / (b - a + 1);
        }

        private static float BandEnergy(float[] mag, int a, int b)
        {
            double p = 0;
            for (int i = a; i <= b; i++) p += mag[i] * mag[i];
            return (float)Math.Sqrt(p);
        }

        private static float Ema(float v, float x, float tau, float dt) => v < -89 ? x : v + (x - v) * (1 - MathF.Exp(-dt / tau));
        private static float Db(float v) => 20 * MathF.Log10(v + 1e-9f);

        /// <summary>In-place radix-2 FFT (iterative Cooley–Tukey).</summary>
        private static void Fft(float[] re, float[] im)
        {
            int n = re.Length;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                double ang = -2 * Math.PI / len;
                float wr = (float)Math.Cos(ang), wi = (float)Math.Sin(ang);
                for (int i = 0; i < n; i += len)
                {
                    float cr = 1, ci = 0;
                    for (int q = 0; q < len / 2; q++)
                    {
                        int a = i + q, b = a + len / 2;
                        float tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        float ncr = cr * wr - ci * wi;
                        ci = cr * wi + ci * wr;
                        cr = ncr;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Onset detection on one band's flux: fires the moment the flux crosses mean + k·σ of its own recent history (and a
    /// minimum, and a share of the band's recent peak flux), then re-arms only once the flux has fallen back near the
    /// mean, so one hit fires once. A candidate that isn't <c>allowed</c> (its transient gate failed) doesn't use the
    /// detector up, so the real hit a moment later still fires.
    /// </summary>
    private sealed class Onset
    {
        private readonly float _tau, _refractory, _minFlux, _peakShare;
        private float _mean, _var = 1e-4f, _peak;
        private bool _armed = true;
        private double _last = -10;

        public Onset(float tau, float refractory, float minFlux, float peakShare)
        { _tau = tau; _refractory = refractory; _minFlux = minFlux; _peakShare = peakShare; }

        public float Threshold { get; private set; }

        public bool Step(float flux, double now, float k, float dt, out float strength, float gate = 1, bool allowed = true)
        {
            float sd = MathF.Sqrt(_var);
            _peak = Math.Max(flux, _peak * MathF.Exp(-dt / 2f)); // the band's recent peak flux, falling over ~2 s
            float thr = Math.Max(Math.Max(_minFlux, _mean + k * gate * sd), _peakShare * _peak);
            Threshold = thr;
            strength = 0;
            bool hit = false;
            if (_armed && allowed && flux > thr && now - _last > _refractory)
            {
                hit = true;
                _armed = false;
                _last = now;
                strength = Math.Clamp(0.55f + (flux - thr) / (3 * sd + _minFlux), 0.55f, 1f);
            }
            else if (!_armed && flux < _mean + 0.5f * sd) _armed = true;
            Learn(flux, dt, sd);
            return hit;
        }

        /// <summary>Not an onset trigger: is this band rising clearly right now (above mean + sigmas·σ)?</summary>
        public bool Rising(float flux, float dt, float sigmas = 0.3f)
        {
            float sd = MathF.Sqrt(_var);
            bool rising = flux > Math.Max(_minFlux * 0.5f, _mean + sigmas * sd);
            Learn(flux, dt, sd);
            return rising;
        }

        private void Learn(float flux, float dt, float sd)
        {
            float f = Math.Min(flux, _mean + 4 * sd + _minFlux); // one huge hit doesn't raise the bar for long
            float a = 1 - MathF.Exp(-dt / _tau);
            float d = f - _mean;
            _mean += a * d;
            _var += a * (d * d - _var);
        }
    }

    /// <summary>A sustained level: auto-gained against its own recent peak (which falls slowly), curved, then smoothed.</summary>
    private sealed class Level
    {
        private readonly float _range, _attack, _release, _curve, _peakFall;
        private float _peak = -60, _value;

        public Level(float rangeDb, float attack, float release, float curve, float peakFall = 4)
        { _range = rangeDb; _attack = attack; _release = release; _curve = curve; _peakFall = peakFall; }

        public float Step(float db, float dt)
        {
            _peak = db > _peak ? db : Math.Max(-70, _peak - _peakFall * dt);
            float target = MathF.Pow(Math.Clamp((db - (_peak - _range)) / _range, 0, 1), _curve);
            float tau = target > _value ? _attack : _release;
            _value += (target - _value) * (1 - MathF.Exp(-dt / tau));
            return _value;
        }
    }

    /// <summary>
    /// Tempo from the spacing of kicks: every pair of recent kicks (within 2 s) votes for its interval, folded into
    /// 80–160 BPM; the strongest vote is the tempo once enough kicks agree. The beat grid is anchored on the latest kick.
    /// </summary>
    private sealed class Tempo
    {
        private readonly List<(double t, float s)> _onsets = new();
        private readonly float[] _votes = new float[81]; // 80..160 BPM
        private double _anchor = -10;

        public float Bpm { get; private set; }
        public float Confidence { get; private set; }
        public bool Locked => Confidence > 0.22f && _onsets.Count >= 6 && Bpm > 0;
        private double Period => 60.0 / Math.Max(1, Bpm);

        public void Onset(double now, float strength)
        {
            // Snap the anchor to the grid when the kick is on it; otherwise start a new grid from this kick.
            _anchor = now;
            _onsets.Add((now, strength));
            _onsets.RemoveAll(o => now - o.t > 8);
            Array.Clear(_votes);
            float total = 0;
            for (int i = 0; i < _onsets.Count; i++)
            for (int j = i + 1; j < _onsets.Count; j++)
            {
                double d = _onsets[j].t - _onsets[i].t;
                if (d < 0.25 || d > 2) continue;
                double bpm = 60 / d;
                while (bpm < 80) bpm *= 2;
                while (bpm >= 160) bpm /= 2;
                float w = _onsets[i].s * _onsets[j].s * (float)Math.Exp(-(now - _onsets[j].t) / 4);
                int b = (int)Math.Round(bpm) - 80;
                for (int n = -1; n <= 1; n++) { int c = b + n; if (c >= 0 && c < _votes.Length) _votes[c] += n == 0 ? w : w * 0.5f; }
                total += w * 2;
            }
            int best = 0;
            for (int c = 1; c < _votes.Length; c++) if (_votes[c] > _votes[best]) best = c;
            Bpm = total > 0 ? best + 80 : 0;
            Confidence = total > 0 ? Math.Clamp(_votes[best] / total * 2.5f, 0, 1) : 0;
        }

        public bool NearBeat(double now, double tolerance)
        {
            if (Bpm <= 0) return true;
            double phase = ((now - _anchor) / Period) % 1;
            return phase < tolerance || phase > 1 - tolerance;
        }

        /// <summary>The last on-grid beat as Stopwatch ticks (the renderer derives the beat phase from it).</summary>
        public long BeatTicks(double now)
        {
            if (!Locked) return 0;
            double since = (now - _anchor) % Period;
            return Stopwatch.GetTimestamp() - (long)(since * Stopwatch.Frequency);
        }
    }
}
