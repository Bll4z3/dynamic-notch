using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace DynamicNotch.Services;

/// <summary>One download in progress (or just finished). <see cref="Total"/> is known only for the app's own update download.</summary>
public sealed record DownloadInfo(string Key, string Name, long Received, long? Total, bool Done, string? FinalPath, bool Failed = false)
{
    public double? Fraction => Total is > 0 ? Math.Clamp((double)Received / Total.Value, 0, 1) : null;
}

/// <summary>
/// Browser downloads in the user's Downloads folder, from file-system events only (no polling).
/// Chrome / Edge write "name.crdownload", Firefox writes "name.part" next to the final file, so a
/// growing temp file means a download in progress and its rename / removal means it finished.
/// Browsers don't say how big the file will be, so progress is shown as bytes received with an
/// indeterminate track. Updates are coalesced to <see cref="Design.Motion.DownloadThrottleMs"/>.
/// </summary>
public sealed class DownloadWatcherService : IDisposable
{
    private static readonly string[] TempExtensions = { ".crdownload", ".part", ".partial", ".download" };

    private FileSystemWatcher? _watcher;
    private readonly Dictionary<string, DownloadInfo> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherQueueTimer? _flush;
    private bool _enabled;

    /// <summary>Raised on the UI thread for a download's progress and, finally, with Done = true.</summary>
    public event Action<DownloadInfo>? Progress;

    public string? Folder { get; private set; }

    public bool Enabled
    {
        get => _enabled;
        set { if (value == _enabled) return; _enabled = value; if (value) Start(); else Stop(); }
    }

    private void Start()
    {
        Folder = KnownDownloadsFolder();
        if (Folder == null || !Directory.Exists(Folder)) return;
        _flush ??= CreateFlushTimer();
        try
        {
            _watcher = new FileSystemWatcher(Folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                IncludeSubdirectories = false,
                InternalBufferSize = 16 * 1024,
            };
            _watcher.Created += (_, e) => OnTouched(e.FullPath);
            _watcher.Changed += (_, e) => OnTouched(e.FullPath);
            _watcher.Renamed += (_, e) => OnRenamed(e.OldFullPath, e.FullPath);
            _watcher.Deleted += (_, e) => OnDeleted(e.FullPath);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Download watcher unavailable: {ex.Message}"); }
    }

    private void Stop()
    {
        try { if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); } } catch { }
        _watcher = null;
        Ui.Post(() => { _active.Clear(); _dirty.Clear(); _flush?.Stop(); });
    }

    private DispatcherQueueTimer CreateFlushTimer()
    {
        var t = Ui.Queue.CreateTimer();
        t.IsRepeating = false;
        t.Interval = TimeSpan.FromMilliseconds(Design.Motion.DownloadThrottleMs);
        t.Tick += (_, _) => Flush();
        return t;
    }

    private static bool IsTemp(string path) =>
        TempExtensions.Any(x => path.EndsWith(x, StringComparison.OrdinalIgnoreCase));

    /// <summary>"Report.pdf.crdownload" → "Report.pdf"; Edge's "Unconfirmed 123.crdownload" → null (name not known yet).</summary>
    private static string? DisplayName(string tempPath)
    {
        string name = Path.GetFileName(tempPath);
        foreach (var x in TempExtensions)
            if (name.EndsWith(x, StringComparison.OrdinalIgnoreCase)) { name = name[..^x.Length]; break; }
        if (name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase) || name.Length == 0) return null;
        // Firefox: "name.ext.part" may sit beside an empty placeholder "name.ext"; strip a random suffix if any.
        return name;
    }

    // FileSystemWatcher events arrive on a worker thread: hop to the UI thread for all state.
    private void OnTouched(string path)
    {
        if (!IsTemp(path)) return;
        long size = 0;
        try { size = new FileInfo(path).Length; } catch { }
        Ui.Post(() =>
        {
            if (!_enabled) return;
            string name = DisplayName(path) ?? (_active.TryGetValue(path, out var prev) ? prev.Name : "Downloading…");
            _active[path] = new DownloadInfo(path, name, size, null, false, null);
            Mark(path);
        });
    }

    private void OnRenamed(string oldPath, string newPath)
    {
        bool wasTemp = IsTemp(oldPath), isTemp = IsTemp(newPath);
        Ui.Post(() =>
        {
            if (!_enabled) return;
            if (wasTemp && isTemp)
            {
                // Edge: "Unconfirmed 1.crdownload" → "Real name.crdownload" once the user picked a name.
                if (_active.Remove(oldPath, out var info))
                {
                    _active[newPath] = info with { Key = newPath, Name = DisplayName(newPath) ?? info.Name };
                    _dirty.Remove(oldPath);
                    Mark(newPath);
                }
                return;
            }
            if (wasTemp && _active.Remove(oldPath, out var done))
            {
                _dirty.Remove(oldPath);
                long size = done.Received;
                try { size = new FileInfo(newPath).Length; } catch { }
                Progress?.Invoke(done with { Name = Path.GetFileName(newPath), Received = size, Done = true, FinalPath = newPath });
            }
        });
    }

    private void OnDeleted(string path)
    {
        if (!IsTemp(path)) return;
        Ui.Post(() =>
        {
            if (!_active.Remove(path, out var info)) return;
            _dirty.Remove(path);
            // Firefox deletes the .part after writing the real file; a cancelled download leaves nothing behind.
            string final = Path.Combine(Path.GetDirectoryName(path) ?? "", info.Name);
            bool exists = File.Exists(final) && new FileInfo(final).Length > 0;
            Progress?.Invoke(info with { Done = true, Failed = !exists, FinalPath = exists ? final : null });
        });
    }

    private void Mark(string path)
    {
        _dirty.Add(path);
        if (_flush != null && !_flush.IsRunning) _flush.Start();
    }

    private void Flush()
    {
        foreach (var key in _dirty)
            if (_active.TryGetValue(key, out var info)) Progress?.Invoke(info);
        _dirty.Clear();
    }

    public void Dispose() => Stop();

    // ---- Known folder ------------------------------------------------------------------------------
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    private static string? KnownDownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var p) == 0)
            {
                try { return Marshal.PtrToStringUni(p); }
                finally { Marshal.FreeCoTaskMem(p); }
            }
        }
        catch { }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(fallback) ? fallback : null;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
