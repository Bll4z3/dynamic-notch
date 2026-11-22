using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace DynamicNotch.Services.Palette;

/// <summary>An installed app: its display name and the AppsFolder id (an AUMID for Store apps, a path for the rest).</summary>
public sealed record AppEntry(string Name, string Id);

/// <summary>
/// Every app in the Start menu, desktop and Store apps alike, read from the shell's AppsFolder (the same
/// list Start shows) on a background STA thread, refreshed when the palette opens if it's over 5 minutes
/// old. Launching goes through "shell:AppsFolder\{id}", which works for both kinds. Icons come from the
/// shell's own image factory, loaded lazily per row and cached.
/// </summary>
public sealed class AppIndex
{
    private volatile IReadOnlyList<AppEntry> _apps = Array.Empty<AppEntry>();
    private DateTime _loaded = DateTime.MinValue;
    private bool _loading;
    private readonly Dictionary<string, SoftwareBitmap?> _icons = new();

    public IReadOnlyList<AppEntry> Apps => _apps;
    /// <summary>Raised on the UI thread when a (re)load finished, so open results can include the apps.</summary>
    public event Action? Loaded;

    public void RefreshIfStale()
    {
        if (_loading || DateTime.Now - _loaded < TimeSpan.FromMinutes(5)) return;
        _loading = true;
        var t = new Thread(() =>
        {
            try
            {
                var list = new List<AppEntry>();
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType != null)
                {
                    dynamic shell = Activator.CreateInstance(shellType)!;
                    dynamic folder = shell.NameSpace("shell:AppsFolder");
                    dynamic items = folder.Items();
                    int count = items.Count;
                    for (int i = 0; i < count; i++)
                    {
                        dynamic item = items.Item(i);
                        string name = item.Name, id = item.Path;
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                        if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                        list.Add(new AppEntry(name, id));
                    }
                    Marshal.FinalReleaseComObject(folder);
                    Marshal.FinalReleaseComObject(shell);
                }
                _apps = list.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).OrderBy(a => a.Name).ToList();
                _loaded = DateTime.Now;
                Ui.Post(() => Loaded?.Invoke());
            }
            catch (Exception ex) { App.LogError(ex); }
            finally { _loading = false; }
        }) { IsBackground = true, Name = "Palette app index" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public static void Launch(string id)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{id}") { UseShellExecute = true }); } catch { }
    }

    /// <summary>The app's icon (32 px), from the shell; cached. Runs the shell call on a worker thread.</summary>
    public async Task<SoftwareBitmap?> IconAsync(string id)
    {
        if (_icons.TryGetValue(id, out var cached)) return cached;
        var bmp = await ShellImage.LoadAsync($"shell:AppsFolder\\{id}", 32);
        _icons[id] = bmp;
        return bmp;
    }
}

/// <summary>A shell item's icon / thumbnail as a SoftwareBitmap (IShellItemImageFactory).</summary>
public static class ShellImage
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int, bool), Task<SoftwareBitmap?>> Cache = new(StringTupleComparer.Instance);

    /// <summary>Cached per path and size: the same file or app showing again (while typing) costs nothing.</summary>
    public static Task<SoftwareBitmap?> LoadAsync(string parsingName, int size, bool thumbnail = false)
    {
        if (Cache.Count > 400) Cache.Clear();
        return Cache.GetOrAdd((parsingName, size, thumbnail), k => LoadUncachedAsync(k.Item1, k.Item2, k.Item3));
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string, int, bool)>
    {
        public static readonly StringTupleComparer Instance = new();
        public bool Equals((string, int, bool) a, (string, int, bool) b) => a.Item2 == b.Item2 && a.Item3 == b.Item3 && string.Equals(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, int, bool) k) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item1), k.Item2, k.Item3);
    }

    private static Task<SoftwareBitmap?> LoadUncachedAsync(string parsingName, int size, bool thumbnail) => Task.Run(() =>
    {
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            if (SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null) return null;
            try
            {
                int flags = thumbnail ? 0 : 0x4 /* SIIGBF_ICONONLY */;
                if (factory.GetImage(new SIZE { cx = size, cy = size }, flags, out hbmp) != 0 || hbmp == IntPtr.Zero) return null;
            }
            finally { Marshal.ReleaseComObject(factory); }
            return FromHBitmap(hbmp);
        }
        catch { return null; }
        finally { if (hbmp != IntPtr.Zero) DeleteObject(hbmp); }
    });

    private static SoftwareBitmap? FromHBitmap(IntPtr hbmp)
    {
        if (GetObject(hbmp, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmBitsPixel != 32) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var pixels = new byte[w * h * 4];
        IntPtr dc = GetDC(IntPtr.Zero);
        try { if (GetDIBits(dc, hbmp, 0, (uint)h, pixels, ref info, 0) == 0) return null; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        // The shell hands out premultiplied BGRA (alpha 0 everywhere means an opaque, alpha-less bitmap).
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}
