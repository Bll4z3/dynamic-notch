using System.Runtime.InteropServices;

namespace DynamicNotch.Services;

public readonly record struct VolumeState(double Level, bool Muted);

/// <summary>
/// Master volume and mute of the default playback device (and the default microphone's mute, for the
/// quick actions), through Core Audio: IAudioEndpointVolume with its change callback, plus an
/// IMMNotificationClient so a new default device is picked up. Fully event-driven: Windows calls back
/// on its own thread, the service posts to the UI thread. The state at start-up raises nothing.
/// </summary>
public sealed class AudioService : IDisposable
{
    private static Guid _ourContext = Guid.NewGuid(); // marks changes this app made (they still show the pill)

    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _render, _capture;
    private VolumeCallback? _renderCallback, _captureCallback;
    private DeviceCallback? _deviceCallback;
    private readonly object _gate = new();

    /// <summary>Raised on the UI thread when the playback volume or mute changes (not at start-up).</summary>
    public event Action<VolumeState>? VolumeChanged;
    /// <summary>Raised on the UI thread when the microphone's mute changes.</summary>
    public event Action<bool>? MicMuteChanged;

    public bool IsAvailable => _render != null;
    public bool HasMicrophone => _capture != null;

    public void Start()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            _deviceCallback = new DeviceCallback(this);
            _enumerator.RegisterEndpointNotificationCallback(_deviceCallback);
            Attach(EDataFlow.Render);
            Attach(EDataFlow.Capture);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Core Audio unavailable: {ex.Message}"); }
    }

    public VolumeState Current
    {
        get
        {
            try
            {
                var v = _render;
                if (v == null) return new VolumeState(0, false);
                v.GetMasterVolumeLevelScalar(out float level);
                v.GetMute(out bool muted);
                return new VolumeState(level, muted);
            }
            catch { return new VolumeState(0, false); }
        }
    }

    public bool MicMuted
    {
        get { try { if (_capture == null) return false; _capture.GetMute(out bool m); return m; } catch { return false; } }
    }

    /// <summary>Sets the playback volume (0..1); unmutes when raised above zero.</summary>
    public void SetVolume(double level)
    {
        try
        {
            var v = _render;
            if (v == null) return;
            level = Math.Clamp(level, 0, 1);
            v.SetMasterVolumeLevelScalar((float)level, ref _ourContext);
            v.GetMute(out bool muted);
            if (muted && level > 0.001) v.SetMute(false, ref _ourContext);
        }
        catch { }
    }

    public void ToggleMute()
    {
        try { var v = _render; if (v == null) return; v.GetMute(out bool m); v.SetMute(!m, ref _ourContext); } catch { }
    }

    public void SetMicMuted(bool muted)
    {
        try { _capture?.SetMute(muted, ref _ourContext); } catch { }
    }

    private void Attach(EDataFlow flow)
    {
        lock (_gate)
        {
            Detach(flow);
            if (_enumerator == null) return;
            try
            {
                _enumerator.GetDefaultAudioEndpoint(flow, ERole.Multimedia, out var device);
                var iid = typeof(IAudioEndpointVolume).GUID;
                device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out object o);
                var vol = (IAudioEndpointVolume)o;
                if (flow == EDataFlow.Render)
                {
                    _render = vol;
                    _renderCallback = new VolumeCallback(data => Ui.Post(() => VolumeChanged?.Invoke(new VolumeState(data.Level, data.Muted))));
                    vol.RegisterControlChangeNotify(_renderCallback);
                }
                else
                {
                    _capture = vol;
                    bool last = MicMuted;
                    _captureCallback = new VolumeCallback(data =>
                    {
                        if (data.Muted == last) return;
                        last = data.Muted;
                        Ui.Post(() => MicMuteChanged?.Invoke(data.Muted));
                    });
                    vol.RegisterControlChangeNotify(_captureCallback);
                }
            }
            catch { /* no device of that kind */ }
        }
    }

    private void Detach(EDataFlow flow)
    {
        try
        {
            if (flow == EDataFlow.Render && _render != null)
            {
                if (_renderCallback != null) _render.UnregisterControlChangeNotify(_renderCallback);
                Marshal.ReleaseComObject(_render);
                _render = null;
                _renderCallback = null;
            }
            else if (flow == EDataFlow.Capture && _capture != null)
            {
                if (_captureCallback != null) _capture.UnregisterControlChangeNotify(_captureCallback);
                Marshal.ReleaseComObject(_capture);
                _capture = null;
                _captureCallback = null;
            }
        }
        catch { }
    }

    private void OnDefaultChanged(EDataFlow flow)
    {
        // Re-attach off the callback thread (Core Audio forbids blocking calls inside its callbacks).
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Attach(flow);
            if (flow == EDataFlow.Capture) { bool m = MicMuted; Ui.Post(() => MicMuteChanged?.Invoke(m)); }
        });
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Detach(EDataFlow.Render);
            Detach(EDataFlow.Capture);
            try { if (_enumerator != null && _deviceCallback != null) _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback); } catch { }
            _enumerator = null;
        }
    }

    // ---- Callbacks --------------------------------------------------------------------------------
    private readonly record struct VolumeData(float Level, bool Muted);

    [ComVisible(true)]
    private sealed class VolumeCallback : IAudioEndpointVolumeCallback
    {
        private readonly Action<VolumeData> _onChange;
        public VolumeCallback(Action<VolumeData> onChange) => _onChange = onChange;

        public int OnNotify(IntPtr data)
        {
            try
            {
                // AUDIO_VOLUME_NOTIFICATION_DATA: GUID guidEventContext; BOOL bMuted; float fMasterVolume; ...
                bool muted = Marshal.ReadInt32(data, 16) != 0;
                float level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
                _onChange(new VolumeData(level, muted));
            }
            catch { }
            return 0;
        }
    }

    [ComVisible(true)]
    private sealed class DeviceCallback : IMMNotificationClient
    {
        private readonly AudioService _owner;
        public DeviceCallback(AudioService owner) => _owner = owner;
        public int OnDeviceStateChanged(string id, int state) => 0;
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) => 0;
        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? id)
        {
            if (role == ERole.Multimedia) _owner.OnDefaultChanged(flow);
            return 0;
        }
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }

    // ---- Core Audio interop ------------------------------------------------------------------------
    internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }
    internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey { public Guid fmtid; public int pid; }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, int mask, out IntPtr devices);
        void GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        void RegisterEndpointNotificationCallback(IMMNotificationClient client);
        void UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolumeCallback
    {
        [PreserveSig] int OnNotify(IntPtr data);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float levelDb, ref Guid context);
        void SetMasterVolumeLevelScalar(float level, ref Guid context);
        void GetMasterVolumeLevel(out float levelDb);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        void GetChannelVolumeLevel(uint channel, out float levelDb);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        void GetVolumeStepInfo(out uint step, out uint count);
        void VolumeStepUp(ref Guid context);
        void VolumeStepDown(ref Guid context);
        void QueryHardwareSupport(out uint mask);
        void GetVolumeRange(out float min, out float max, out float increment);
    }
}
