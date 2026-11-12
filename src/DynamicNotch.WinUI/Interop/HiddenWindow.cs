using System.Runtime.InteropServices;

namespace DynamicNotch.Interop;

/// <summary>
/// An invisible top-level window on the UI thread that receives the hotkey and system broadcasts
/// (display, DPI and settings changes). Subclassing the WinUI window itself breaks WinUI's input
/// handling, so the notch window is left alone and these messages come here instead.
/// </summary>
internal sealed class HiddenWindow : IDisposable
{
    private readonly WndProcDelegate _proc;

    public HiddenWindow(string name)
    {
        _proc = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = name,
        };
        RegisterClassEx(ref wc);
        // A never-shown popup (not message-only, so it also gets broadcasts like WM_SETTINGCHANGE).
        Handle = CreateWindowEx(0x80 /* WS_EX_TOOLWINDOW */, name, name, 0x80000000 /* WS_POPUP */,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    public IntPtr Handle { get; private set; }

    /// <summary>Every message: (msg, wParam, lParam).</summary>
    public event Action<uint, IntPtr, IntPtr>? Message;

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try { Message?.Invoke(msg, wParam, lParam); } catch { }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DestroyWindow(Handle);
        Handle = IntPtr.Zero;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
