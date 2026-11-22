using System.Runtime.InteropServices;
using DynamicNotch.Interop;

namespace DynamicNotch.Services;

/// <summary>
/// Tells a brightness change the user made with the keyboard's brightness keys apart from one Windows made
/// by itself (adaptive / auto brightness, content-adaptive brightness, a power plan dimming the screen).
/// <para>
/// Both kinds look the same to <see cref="BrightnessService"/>: WMI reports a new level either way. So instead
/// of trusting the level, this listens for the key itself. On most modern laptops (Surface and other
/// HID-keyboard designs) the brightness keys are HID "Consumer Control" buttons, usages 0x6F (Brightness
/// Increment) and 0x70 (Brightness Decrement). Raw Input delivers those reports to a hidden window even while
/// another app has focus (RIDEV_INPUTSINK), without blocking, swallowing or delaying them, and only when a
/// consumer key (media / brightness) is actually pressed, so it costs nothing while idle.
/// </para>
/// <para>
/// A change counts as the user's when a brightness key is held down or was released within
/// <see cref="WindowMs"/>. Fallback for PCs whose brightness keys aren't HID buttons (some laptops send them
/// through ACPI firmware instead): when no consumer-control device here declares the brightness usages,
/// any key press seen by the low-level keyboard hook within the same window counts (<see cref="FallbackKeyTick"/>).
/// </para>
/// </summary>
public sealed class BrightnessKeyService : IDisposable
{
    /// <summary>How long after a brightness key a level change still counts as the key's (WMI reports it a moment later).</summary>
    public const int WindowMs = 1500;
    private const ushort ConsumerPage = 0x0C, ConsumerControl = 0x01, BrightnessUp = 0x6F, BrightnessDown = 0x70;

    private HiddenWindow? _window;
    private readonly Dictionary<IntPtr, byte[]?> _preparsed = new();
    private long _lastKeyTick = long.MinValue / 2;
    private bool _held;

    /// <summary>True when a consumer-control device on this PC declares the brightness keys (set by <see cref="Start"/>).</summary>
    public bool HasHidBrightnessKeys { get; private set; }

    /// <summary>For PCs without HID brightness keys: the tick (Environment.TickCount64) of the last key press anywhere.</summary>
    public Func<long>? FallbackKeyTick { get; set; }

    /// <summary>Raised on the UI thread when a brightness key goes down (+1 = up, -1 = down).</summary>
    public event Action<int>? Pressed;

    /// <summary>Starts listening. Call on the UI thread (the hidden window's messages arrive on its loop).</summary>
    public void Start()
    {
        HasHidBrightnessKeys = DeclaresBrightnessKeys();
        if (!HasHidBrightnessKeys) return;
        _window = new HiddenWindow("DynamicNotch.BrightnessKeys");
        _window.Message += OnMessage;
        var dev = new RAWINPUTDEVICE[] { new() { usUsagePage = ConsumerPage, usUsage = ConsumerControl, dwFlags = RIDEV_INPUTSINK, hwndTarget = _window.Handle } };
        if (!RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            HasHidBrightnessKeys = false; // couldn't listen: behave like a PC without HID keys
    }

    /// <summary>Whether a brightness change happening now was asked for with the brightness keys.</summary>
    public bool RecentlyPressed
    {
        get
        {
            long now = Environment.TickCount64;
            if (HasHidBrightnessKeys) return _held || now - _lastKeyTick <= WindowMs;
            return FallbackKeyTick is { } f && now - f() <= WindowMs;
        }
    }

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != WM_INPUT) return;
        uint size = 0;
        uint header = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, header) != 0 || size == 0 || size > 4096) return;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, header) != size) return;
            var h = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (h.dwType != RIM_TYPEHID) return;
            int offset = (int)header;
            int reportSize = Marshal.ReadInt32(buffer, offset);
            int count = Marshal.ReadInt32(buffer, offset + 4);
            if (reportSize <= 0 || count <= 0 || offset + 8 + (long)reportSize * count > size) return;
            var preparsed = Preparsed(h.hDevice);
            int dir = 0;
            var report = new byte[reportSize];
            for (int i = 0; i < count; i++)
            {
                Marshal.Copy(buffer + offset + 8 + i * reportSize, report, 0, reportSize);
                dir = BrightnessUsage(report, preparsed);
                if (dir != 0) break;
            }
            if (dir != 0)
            {
                bool repeat = _held;
                _held = true;
                _lastKeyTick = Environment.TickCount64;
                if (!repeat) Pressed?.Invoke(dir);
            }
            else if (_held)
            {
                _held = false; // released (a consumer report with no brightness usage)
                _lastKeyTick = Environment.TickCount64;
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>+1 / -1 when the report has Brightness Increment / Decrement down, else 0.</summary>
    private static int BrightnessUsage(byte[] report, byte[]? preparsed)
    {
        if (preparsed != null)
        {
            var usages = new ushort[32];
            uint len = (uint)usages.Length;
            if (HidP_GetUsages(HidP_Input, ConsumerPage, 0, usages, ref len, preparsed, report, (uint)report.Length) == HIDP_STATUS_SUCCESS)
            {
                for (int i = 0; i < len; i++)
                {
                    if (usages[i] == BrightnessUp) return 1;
                    if (usages[i] == BrightnessDown) return -1;
                }
                return 0;
            }
        }
        // No descriptor to go by: consumer arrays are 16-bit little-endian usage ids after the report id.
        for (int i = 1; i + 1 < report.Length; i += 2)
        {
            if (report[i + 1] != 0) continue;
            if (report[i] == BrightnessUp) return 1;
            if (report[i] == BrightnessDown) return -1;
        }
        return 0;
    }

    private byte[]? Preparsed(IntPtr device)
    {
        if (_preparsed.TryGetValue(device, out var cached)) return cached;
        byte[]? data = null;
        uint size = 0;
        if (GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, null, ref size) == 0 && size > 0 && size < 65536)
        {
            var buf = new byte[size];
            if (GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, buf, ref size) == size) data = buf;
        }
        _preparsed[device] = data;
        return data;
    }

    /// <summary>True when any HID consumer-control collection on this PC has a button for Brightness Increment / Decrement.</summary>
    public static bool DeclaresBrightnessKeys()
    {
        try
        {
            uint n = 0;
            uint entry = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
            if (GetRawInputDeviceList(null, ref n, entry) != 0 || n == 0) return false;
            var list = new RAWINPUTDEVICELIST[n];
            if (GetRawInputDeviceList(list, ref n, entry) == uint.MaxValue) return false;
            foreach (var d in list)
            {
                if (d.dwType != RIM_TYPEHID) continue;
                uint size = 0;
                if (GetRawInputDeviceInfo(d.hDevice, RIDI_PREPARSEDDATA, null, ref size) != 0 || size == 0 || size >= 65536) continue;
                var pp = new byte[size];
                if (GetRawInputDeviceInfo(d.hDevice, RIDI_PREPARSEDDATA, pp, ref size) != size) continue;
                if (HidP_GetCaps(pp, out var caps) != HIDP_STATUS_SUCCESS || caps.UsagePage != ConsumerPage || caps.Usage != ConsumerControl) continue;
                ushort count = caps.NumberInputButtonCaps;
                if (count == 0) continue;
                var bcaps = new HIDP_BUTTON_CAPS[count];
                if (HidP_GetButtonCaps(HidP_Input, bcaps, ref count, pp) != HIDP_STATUS_SUCCESS) continue;
                for (int i = 0; i < count; i++)
                {
                    var b = bcaps[i];
                    if (b.UsagePage != ConsumerPage) continue;
                    ushort min = b.UsageMin, max = b.IsRange != 0 ? b.UsageMax : b.UsageMin;
                    if (min <= BrightnessDown && max >= BrightnessUp) return true; // overlaps 0x6F..0x70
                }
            }
        }
        catch { }
        return false;
    }

    public void Dispose()
    {
        if (_window != null)
        {
            try
            {
                var dev = new RAWINPUTDEVICE[] { new() { usUsagePage = ConsumerPage, usUsage = ConsumerControl, dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero } };
                RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            }
            catch { }
            _window.Dispose();
            _window = null;
        }
    }

    // ---- Interop -------------------------------------------------------------------------------------
    private const uint WM_INPUT = 0x00FF, RID_INPUT = 0x10000003, RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIDEV_INPUTSINK = 0x00000100, RIDEV_REMOVE = 0x00000001, RIM_TYPEHID = 2;
    private const int HidP_Input = 0, HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice, wParam; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    /// <summary>HIDP_BUTTON_CAPS (72 bytes); the Range / NotRange union is read through its first fields.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct HIDP_BUTTON_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(3)] public byte IsAlias;
        [FieldOffset(4)] public ushort BitField;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(8)] public ushort LinkUsage;
        [FieldOffset(10)] public ushort LinkUsagePage;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(13)] public byte IsStringRange;
        [FieldOffset(14)] public byte IsDesignatorRange;
        [FieldOffset(15)] public byte IsAbsolute;
        // ULONG ReportCount + Reserved[9] occupy 16..55; the union starts at 56.
        [FieldOffset(56)] public ushort UsageMin; // Range.UsageMin, or NotRange.Usage
        [FieldOffset(58)] public ushort UsageMax; // Range.UsageMax
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")] private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, byte[]? data, ref uint size);
    [DllImport("user32.dll")] private static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[]? list, ref uint count, uint size);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(byte[] preparsed, out HIDP_CAPS caps);
    [DllImport("hid.dll")] private static extern int HidP_GetButtonCaps(int reportType, [Out] HIDP_BUTTON_CAPS[] caps, ref ushort length, byte[] preparsed);
    [DllImport("hid.dll")] private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [Out] ushort[] usages, ref uint length, byte[] preparsed, byte[] report, uint reportLength);
}
