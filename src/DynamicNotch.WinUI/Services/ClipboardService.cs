using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using DynamicNotch.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DynamicNotch.Services;

public enum ClipKind { Text, Link, Image }

/// <summary>One captured clipboard entry (immutable content).</summary>
public sealed class ClipContent
{
    public required ClipKind Kind { get; init; }
    public string? Text { get; init; }
    public SoftwareBitmap? Image { get; init; }
    public required string Fingerprint { get; init; }
}

/// <summary>
/// Listens with AddClipboardFormatListener (WM_CLIPBOARDUPDATE on a hidden window), no polling, and
/// reads through the WinRT clipboard. Skips content that password managers mark as private and
/// ignores the copies this app makes itself. History lives in memory only.
/// </summary>
public sealed class ClipboardService : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private readonly HiddenWindow _window = new("DynamicNotch.Clipboard");
    private string? _selfFingerprint;
    private DateTime _selfCopyAt = DateTime.MinValue;
    private bool _listening, _readQueued, _reading;

    private static readonly uint FmtExcludeMonitor = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint FmtViewerIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore");
    private static readonly uint FmtCanIncludeHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");

    public ClipboardService()
    {
        _window.Message += (msg, _, _) =>
        {
            if (msg != WM_CLIPBOARDUPDATE || _readQueued) return;
            // One update arrives per clipboard write (after the writer closes the clipboard).
            _readQueued = true;
            Ui.Post(async () => { _readQueued = false; await ReadAsync(); });
        };
    }

    /// <summary>Raised on the UI thread for genuinely new clipboard content.</summary>
    public event Action<ClipContent>? Captured;

    /// <summary>Start / stop the clipboard listener (Clipboard + Shelf page switched on / off).</summary>
    public void SetListening(bool on)
    {
        if (on == _listening || _window.Handle == IntPtr.Zero) return;
        _listening = on;
        if (on) AddClipboardFormatListener(_window.Handle);
        else RemoveClipboardFormatListener(_window.Handle);
    }

    private async Task ReadAsync()
    {
        if (_reading) return;
        _reading = true;
        try
        {
            if (IsClipboardFormatAvailable(FmtExcludeMonitor) || IsClipboardFormatAvailable(FmtViewerIgnore)) return;
            ClipContent? content = null;
            for (int attempt = 0; attempt < 3 && content == null; attempt++)
            {
                try
                {
                    var view = Clipboard.GetContent();
                    if (IsClipboardFormatAvailable(FmtCanIncludeHistory) && await IsHistoryOptOutAsync(view)) return;
                    content = await TryReadAsync(view);
                    break;
                }
                catch (COMException) { await Task.Delay(30); } // clipboard busy
            }
            if (content == null) return;
            if (content.Fingerprint == _selfFingerprint || DateTime.UtcNow - _selfCopyAt < TimeSpan.FromMilliseconds(700))
            {
                _selfFingerprint = null;
                return;
            }
            Captured?.Invoke(content);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Clipboard read failed: {ex.Message}"); }
        finally { _reading = false; }
    }

    private static async Task<bool> IsHistoryOptOutAsync(DataPackageView view)
    {
        try
        {
            if (await view.GetDataAsync("CanIncludeInClipboardHistory") is IRandomAccessStream s)
            {
                var bytes = new byte[4];
                using var stream = s.AsStreamForRead();
                return await stream.ReadAsync(bytes) == 4 && BitConverter.ToInt32(bytes, 0) == 0;
            }
        }
        catch { }
        return false;
    }

    private static async Task<ClipContent?> TryReadAsync(DataPackageView view)
    {
        if (view.Contains(StandardDataFormats.Bitmap))
        {
            var img = await ImageLoader.LoadAsync(await view.GetBitmapAsync(), uint.MaxValue);
            if (img == null) return null;
            var pixels = new byte[img.PixelWidth * img.PixelHeight * 4];
            img.CopyToBuffer(pixels.AsBuffer());
            return new ClipContent { Kind = ClipKind.Image, Image = img, Fingerprint = "img:" + Convert.ToHexString(MD5.HashData(pixels)) };
        }
        if (view.Contains(StandardDataFormats.Text))
        {
            var text = await view.GetTextAsync();
            if (string.IsNullOrWhiteSpace(text)) return null;
            var trimmed = text.Trim();
            bool isLink = !trimmed.Contains(' ') && !trimmed.Contains('\n') &&
                          Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
                          (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == "mailto");
            return new ClipContent { Kind = isLink ? ClipKind.Link : ClipKind.Text, Text = text, Fingerprint = "txt:" + text };
        }
        return null;
    }

    /// <summary>Put an item back on the clipboard without re-capturing it as new.</summary>
    public async void CopyBack(ClipContent c)
    {
        try
        {
            _selfFingerprint = c.Fingerprint;
            _selfCopyAt = DateTime.UtcNow;
            var package = await ToPackageAsync(c);
            Clipboard.SetContent(package);
            Clipboard.Flush(); // keep it after the app quits
        }
        catch (Exception) { _selfFingerprint = null; }
    }

    /// <summary>The item as a data package (copy back, drag out).</summary>
    public static async Task<DataPackage> ToPackageAsync(ClipContent c)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        if (c.Kind == ClipKind.Image && c.Image != null)
        {
            var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetSoftwareBitmap(c.Image.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
                ? SoftwareBitmap.Convert(c.Image, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight) : c.Image);
            await encoder.FlushAsync();
            stream.Seek(0);
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
        }
        else if (c.Text != null)
        {
            package.SetText(c.Text);
            if (c.Kind == ClipKind.Link && Uri.TryCreate(c.Text.Trim(), UriKind.Absolute, out var uri)) package.SetWebLink(uri);
        }
        return package;
    }

    public void Dispose()
    {
        SetListening(false);
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
}
