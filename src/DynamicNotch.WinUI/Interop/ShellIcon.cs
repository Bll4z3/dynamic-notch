using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace DynamicNotch.Interop;

/// <summary>The shell's large (32px) icon for a path as a SoftwareBitmap; works for missing files via the extension.</summary>
internal static class ShellIcon
{
    public static SoftwareBitmap? Get(string path, bool exists, bool isDirectory)
    {
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_LARGEICON, attrs = 0;
        if (!exists) { flags |= SHGFI_USEFILEATTRIBUTES; attrs = isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL; }
        if (SHGetFileInfo(path, attrs, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags) == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try { return FromHIcon(info.hIcon); }
        catch { return null; }
        finally { DestroyIcon(info.hIcon); }
    }

    /// <summary>Draws the icon into a 32-bit top-down DIB (with alpha) and copies it out premultiplied.</summary>
    private static SoftwareBitmap? FromHIcon(IntPtr hIcon)
    {
        const int size = 32;
        var bmi = new BITMAPINFO { biSize = 40, biWidth = size, biHeight = -size, biPlanes = 1, biBitCount = 32 };
        IntPtr screen = GetDC(IntPtr.Zero), dc = CreateCompatibleDC(screen);
        IntPtr dib = CreateDIBSection(dc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(dc, dib);
        try
        {
            DrawIconEx(dc, 0, 0, hIcon, size, size, 0, IntPtr.Zero, 3 /* DI_NORMAL */);
            var px = new byte[size * size * 4];
            Marshal.Copy(bits, px, 0, px.Length);
            // DrawIconEx writes premultiplied BGRA for 32-bit icons; old icons without alpha come out with A = 0.
            bool anyAlpha = false;
            for (int i = 3; i < px.Length; i += 4) if (px[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha)
                for (int i = 0; i < px.Length; i += 4)
                    if (px[i] != 0 || px[i + 1] != 0 || px[i + 2] != 0) px[i + 3] = 255;
            return SoftwareBitmap.CreateCopyFromBuffer(px.AsBuffer(), BitmapPixelFormat.Bgra8, size, size, BitmapAlphaMode.Premultiplied);
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(dib);
            DeleteDC(dc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    private const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_ATTRIBUTE_DIRECTORY = 0x10;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO psfi, uint cb, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
