using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using DynamicNotch.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DynamicNotch.Services;

/// <summary>
/// Notices new screenshots for the screenshot shelf, and only screenshots. Two signals, both event-driven:
/// <list type="number">
/// <item><b>The Screenshots folder</b> (the Windows "Screenshots" known folder, wherever it's redirected, e.g.
/// OneDrive\Pictures\Screenshots). Snipping Tool saves every Win+Shift+S / PrtScn capture there automatically
/// (its default), and Win+PrtScn always does. A FileSystemWatcher reports new .png / .jpg files. Other apps'
/// image copies never land there, so this can't misfire.</item>
/// <item><b>The clipboard</b>, for when Snipping Tool's auto-save is off (or the classic PrtScn copy): a new bitmap
/// counts only when the clipboard's owner is Snipping Tool's process (SnippingTool / ScreenClippingHost /
/// ScreenSketch), or Print Screen was pressed just before. Copying an image in a browser, Paint or Discord
/// leaves a different owner, so it's ignored. That capture is saved by the app (it owns that file).</item>
/// </list>
/// Both can fire for the same snip (clipboard first, file a moment later): a clipboard capture waits
/// <see cref="FileWaitMs"/> for the file and uses it when it comes; a file that arrives on its own is used as is.
/// </summary>
public sealed class ScreenshotService : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    /// <summary>How long a clipboard snip waits for Snipping Tool's saved file before the app saves it itself.</summary>
    private const int FileWaitMs = 1600;
    private static readonly string[] SnipProcesses = { "SnippingTool", "ScreenClippingHost", "ScreenSketch" };

    private readonly HiddenWindow _window = new("DynamicNotch.Screenshots");
    private FileSystemWatcher? _watcher;
    private bool _running, _listening;
    private long _lastFileTick = long.MinValue / 2;
    private long _pendingClip; // tick of a clipboard snip waiting for its file (0 = none)
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ownWrites = new(StringComparer.OrdinalIgnoreCase);

    public ScreenshotService()
    {
        _window.Message += (msg, _, _) => { if (msg == WM_CLIPBOARDUPDATE && _running) OnClipboard(); };
    }

    /// <summary>The Windows Screenshots folder (Pictures\Screenshots unless redirected).</summary>
    public static string ScreenshotsFolder { get; } = ResolveScreenshotsFolder();

    /// <summary>Where the app writes captures it saves itself (clipboard-only snips, mark-ups).</summary>
    public static string OwnFolder { get; } = Path.Combine(SettingsService.DataFolder, "Screenshots");

    /// <summary>For PrtScn: when Print Screen last went down (from the keyboard hook).</summary>
    public Func<long>? PrintScreenTick { get; set; }

    /// <summary>Raised on the UI thread with a new screenshot file and whether the app wrote it.</summary>
    public event Action<string, bool>? Captured;

    /// <summary>Starts / stops listening (the shelf switched on / off in Settings).</summary>
    public void SetRunning(bool on)
    {
        if (on == _running) return;
        _running = on;
        if (on)
        {
            try
            {
                Directory.CreateDirectory(ScreenshotsFolder);
                _watcher = new FileSystemWatcher(ScreenshotsFolder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName };
                _watcher.Created += OnFile;
                _watcher.Renamed += (s, e) => OnFile(s, e);
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { Debug.WriteLine($"Screenshot folder watch failed: {ex.Message}"); }
            if (!_listening && _window.Handle != IntPtr.Zero) _listening = AddClipboardFormatListener(_window.Handle);
        }
        else
        {
            _watcher?.Dispose();
            _watcher = null;
            if (_listening) { RemoveClipboardFormatListener(_window.Handle); _listening = false; }
        }
    }

    /// <summary>A file the app itself is about to write into a watched folder (Save): not a new screenshot.</summary>
    public void IgnoreNextWrite(string path) { lock (_ownWrites) _ownWrites.Add(path); }

    // ---- Folder --------------------------------------------------------------------------------------
    private void OnFile(object sender, FileSystemEventArgs e)
    {
        string ext = Path.GetExtension(e.FullPath);
        if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) return;
        lock (_ownWrites) if (_ownWrites.Remove(e.FullPath)) return;
        string path = e.FullPath;
        _ = Task.Run(async () =>
        {
            // The file is created first and written a moment later: wait until it can be read whole.
            for (int i = 0; i < 40; i++)
            {
                try
                {
                    using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (f.Length > 0) break;
                }
                catch (IOException) { }
                catch { return; }
                await Task.Delay(75);
            }
            Ui.Post(() =>
            {
                if (!_running || !File.Exists(path) || !_seen.Add(path)) return;
                _lastFileTick = Environment.TickCount64;
                _pendingClip = 0; // a clipboard snip waiting for this file: the file wins
                Captured?.Invoke(path, false);
            });
        });
    }

    // ---- Clipboard -----------------------------------------------------------------------------------
    private void OnClipboard()
    {
        if (!IsSnipOwner()) return;
        long now = Environment.TickCount64;
        if (now - _lastFileTick < FileWaitMs) return; // the file for this snip already arrived
        _pendingClip = now;
        _ = WaitThenSaveAsync(now);
    }

    private bool IsSnipOwner()
    {
        try
        {
            if (!IsClipboardFormatAvailable(CF_BITMAP) && !IsClipboardFormatAvailable(CF_DIB)) return false;
            if (PrintScreenTick is { } prt && Environment.TickCount64 - prt() < 2000) return true;
            var owner = GetClipboardOwner();
            if (owner == IntPtr.Zero) return false;
            GetWindowThreadProcessId(owner, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            return SnipProcesses.Any(n => p.ProcessName.Equals(n, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private async Task WaitThenSaveAsync(long tick)
    {
        // Read the image now (the clipboard may change while we wait), save it only if no file shows up.
        SoftwareBitmap? bmp = null;
        for (int attempt = 0; attempt < 3 && bmp == null; attempt++)
        {
            try
            {
                var view = Clipboard.GetContent();
                if (!view.Contains(StandardDataFormats.Bitmap)) return;
                bmp = await ImageLoader.LoadAsync(await view.GetBitmapAsync(), uint.MaxValue);
            }
            catch (COMException) { await Task.Delay(40); }
            catch { return; }
        }
        if (bmp == null) return;
        await Task.Delay(FileWaitMs);
        if (_pendingClip != tick || !_running) return; // Snipping Tool saved it: the folder watcher took it
        _pendingClip = 0;
        try
        {
            Directory.CreateDirectory(OwnFolder);
            string path = Path.Combine(OwnFolder, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png");
            await SavePngAsync(bmp, path);
            _seen.Add(path);
            Captured?.Invoke(path, true);
        }
        catch (Exception ex) { Debug.WriteLine($"Saving clipboard screenshot failed: {ex.Message}"); }
    }

    public static async Task SavePngAsync(SoftwareBitmap bmp, string path)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bmp.BitmapPixelFormat == BitmapPixelFormat.Bgra8 && bmp.BitmapAlphaMode != BitmapAlphaMode.Premultiplied
            ? bmp : SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight));
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private static string ResolveScreenshotsFolder()
    {
        try
        {
            var id = new Guid("b7bede81-df94-4682-a7d8-57a52620b86f"); // FOLDERID_Screenshots
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0)
            {
                string path = Marshal.PtrToStringUni(p) ?? "";
                Marshal.FreeCoTaskMem(p);
                if (path.Length > 0) return path;
            }
        }
        catch { }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
    }

    public void Dispose()
    {
        SetRunning(false);
        _window.Dispose();
    }

    private const uint CF_BITMAP = 2, CF_DIB = 8;
    [DllImport("user32.dll")] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
