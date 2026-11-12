using System.Diagnostics;
using System.Runtime.InteropServices;

// FrameMeter: hovers the notch, then counts how many frames the desktop actually presents
// (DXGI Desktop Duplication) during the expand and collapse morphs. Usage: FrameMeter [repeats] [closeWaitMs]
// It only moves the mouse cursor (no clicks, no typing) and puts it back afterwards.
internal static unsafe class Program
{
    private static IntPtr _dup;

    private static int Main(string[] args)
    {
        SetProcessDPIAware();
        if (!CreateDuplication()) { Console.WriteLine("Desktop Duplication unavailable"); return 1; }
        int repeats = args.Length > 0 && int.TryParse(args[0], out var r) ? r : 3;
        // Optional: ms to wait after leaving before counting the close (default 460 = the hover collapse delay).
        int closeWait = args.Length > 1 && int.TryParse(args[1], out var cw) ? cw : 460;

        int w = GetSystemMetrics(0);
        int cx = w / 2;
        _l = cx - 560; _r = cx + 560; _b = 480; // the notch, its shoulders and shadow at 150%
        if (args.Length > 0 && args[0] == "slides")
        {
            // `FrameMeter slides [count] [columnOffsetPx]`: no cursor moves; waits for an animation the app starts by itself
            // (e.g. `--cycle` page slides), then counts frames for the 500 ms after it starts.
            int n = args.Length > 1 && int.TryParse(args[1], out var nv) ? nv : 5;
            if (args.Length > 2 && int.TryParse(args[2], out var dx)) { _l += dx; _r += dx; } // optional column offset (px) from the centre
            Console.WriteLine("slide  fps");
            for (int k = 1; k <= n; k++)
            {
                while (Count(40) <= 0) { } // idle until the strip starts changing
                Console.WriteLine($"{k,5}  {Count(500),4:F1}");
                Thread.Sleep(700); // past the end of this slide
            }
            return 0;
        }
        if (args.Length > 0 && args[0] == "watch")
        {
            // `FrameMeter watch [seconds] [windowMs]`: no cursor moves at all; distinct frames per window (default 500 ms)
            // (for animations the app drives itself, e.g. page slides).
            int secs = args.Length > 1 && int.TryParse(args[1], out var sv) ? sv : 10;
            int win = args.Length > 2 && int.TryParse(args[2], out var wv) ? wv : 500; // window, ms
            var frames = new List<int>();
            for (int k = 0; k < secs * 1000 / win; k++) frames.Add((int)Math.Round(Count(win) * win / 1000.0));
            Console.WriteLine($"frames per {win} ms window:");
            Console.WriteLine(string.Join(" ", frames));
            return 0;
        }
        GetCursorPos(out var orig);
        Console.WriteLine("run  idleFps  openFps  closeFps");
        for (int i = 1; i <= repeats; i++)
        {
            MoveTo(cx, 900); Thread.Sleep(1500);
            Drain();
            double idle = Count(500);
            MoveTo(cx, 12); Thread.Sleep(20); MoveTo(cx + 2, 14);
            Thread.Sleep(70);
            double open = Count(500);
            Thread.Sleep(600);
            MoveTo(cx, 900);
            Thread.Sleep(closeWait);
            double close = Count(500);
            Console.WriteLine($"{i,3}  {idle,7:F1}  {open,7:F1}  {close,8:F1}");
        }
        SetCursorPos(orig.x, orig.y);
        return 0;
    }

    /// <summary>
    /// Distinct images per second in a 1px vertical strip through the notch centre. The notch's bottom
    /// edge sweeps through it on every frame of the morph, so each change is one animation frame.
    /// </summary>
    private static double Count(int ms)
    {
        int h = _b;
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
        var bmi = new byte[40];
        fixed (byte* pb = bmi) { *(int*)pb = 40; *(int*)(pb + 4) = 1; *(int*)(pb + 8) = -h; *(short*)(pb + 12) = 1; *(short*)(pb + 14) = 32; }
        IntPtr bits; IntPtr dib = CreateDIBSection(mem, bmi, 0, out bits, IntPtr.Zero, 0);
        SelectObject(mem, dib);
        var sw = Stopwatch.StartNew();
        ulong last = 0; int changes = 0;
        while (sw.ElapsedMilliseconds < ms)
        {
            BitBlt(mem, 0, 0, 1, h, screen, (_l + _r) / 2, 0, 0x00CC0020);
            ulong hash = 1469598103934665603UL;
            var px = (uint*)bits;
            for (int i = 0; i < h; i++) { hash ^= px[i]; hash *= 1099511628211UL; }
            if (hash != last) { changes++; last = hash; }
            Thread.SpinWait(2000);
        }
        double rate = (changes - 1) / sw.Elapsed.TotalSeconds;
        DeleteObject(dib); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        return Math.Max(0, rate);
    }

    private static double CountDuplication(int ms)
    {
        var sw = Stopwatch.StartNew();
        long frames = 0;
        while (sw.ElapsedMilliseconds < ms)
        {
            int hr = Acquire((uint)Math.Max(1, ms - sw.ElapsedMilliseconds), out var info);
            if (hr == unchecked((int)0x887A0027)) continue; // DXGI_ERROR_WAIT_TIMEOUT
            if (hr < 0) break;
            if (info.LastPresentTime != 0 && TouchesNotch(info.TotalMetadataBufferSize)) frames++;
            Release();
        }
        return frames / sw.Elapsed.TotalSeconds;
    }

    /// <summary>Notch area in screen pixels (top centre); only frames that change pixels here count.</summary>
    private static int _l, _r, _b;

    private static bool TouchesNotch(uint metaSize)
    {
        if (metaSize == 0) return false;
        var buf = new byte[metaSize];
        uint needed;
        fixed (byte* p = buf)
        {
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, byte*, uint*, int>)Slot(_dup, 9))(_dup, metaSize, p, &needed);
            if (hr < 0) return false;
            var rects = (int*)p;
            for (int i = 0; i < needed / 16; i++)
            {
                int l = rects[i * 4], t = rects[i * 4 + 1], r = rects[i * 4 + 2], b = rects[i * 4 + 3];
                if (r > _l && l < _r && t < _b && b > 0) return true;
            }
        }
        return false;
    }

    private static void Drain()
    {
        while (Acquire(0, out _) >= 0) Release();
    }

    // ---- Raw COM over vtables --------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct FrameInfo
    {
        public long LastPresentTime, LastMouseUpdateTime;
        public uint AccumulatedFrames; public int RectsCoalesced, ProtectedContentMaskedOut;
        public int PtrX, PtrY, PtrVisible; public uint TotalMetadataBufferSize, PointerShapeBufferSize;
    }

    private static IntPtr Slot(IntPtr obj, int i) => (*(IntPtr**)obj)[i];

    private static int Acquire(uint timeout, out FrameInfo info)
    {
        FrameInfo fi; IntPtr res;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, FrameInfo*, IntPtr*, int>)Slot(_dup, 8))(_dup, timeout, &fi, &res);
        info = fi;
        if (hr >= 0 && res != IntPtr.Zero) Marshal.Release(res);
        return hr;
    }

    private static void Release() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_dup, 14))(_dup);

    private static bool CreateDuplication()
    {
        IntPtr device, context; int level;
        if (D3D11CreateDevice(IntPtr.Zero, 1 /* HARDWARE */, IntPtr.Zero, 0x20 /* BGRA */, IntPtr.Zero, 0, 7, out device, out level, out context) < 0) return false;
        var iidDxgiDevice = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        if (Marshal.QueryInterface(device, ref iidDxgiDevice, out var dxgiDevice) < 0) return false;
        IntPtr adapter, output;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(dxgiDevice, 7))(dxgiDevice, &adapter) < 0) return false;
        if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(adapter, 7))(adapter, 0, &output) < 0) return false;
        var iidOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        if (Marshal.QueryInterface(output, ref iidOutput1, out var output1) < 0) return false;
        IntPtr dup;
        if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(output1, 22))(output1, device, &dup) < 0) return false;
        _dup = dup;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr levels, uint numLevels, uint sdk, out IntPtr device, out int level, out IntPtr context);
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public MOUSEINPUT mi; }

    /// <summary>
    /// Moves the cursor, then sends a real absolute mouse-move input to the same spot. WinUI apps react to real
    /// pointer input only, which SetCursorPos alone doesn't produce (WPF reacted to it).
    /// </summary>
    private static void MoveTo(int x, int y)
    {
        SetCursorPos(x, y);
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        var input = new INPUT { type = 0, mi = new MOUSEINPUT
        {
            dx = (int)Math.Round(x * 65535.0 / (w - 1)), dy = (int)Math.Round(y * 65535.0 / (h - 1)),
            flags = 0x0001 | 0x8000, // MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE (primary monitor)
        } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, byte[] bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}

internal static partial class Gdi { }
