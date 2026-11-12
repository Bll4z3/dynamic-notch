using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicNotch.Interop;

namespace DynamicNotch.Services;

public enum ClipKind { Text, Link, Image }

/// <summary>One captured clipboard entry (immutable content).</summary>
public sealed class ClipContent
{
    public required ClipKind Kind { get; init; }
    public string? Text { get; init; }
    public BitmapSource? Image { get; init; }
    public required string Fingerprint { get; init; }
}

/// <summary>
/// Listens with AddClipboardFormatListener (WM_CLIPBOARDUPDATE) — no polling. Skips content that
/// password managers mark as private and ignores the copies this app makes itself.
/// </summary>
public sealed class ClipboardService : IDisposable
{
    private HwndSource? _source;
    private string? _selfFingerprint;
    private DateTime _selfCopyAt = DateTime.MinValue;

    private static readonly uint FmtExcludeMonitor = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint FmtViewerIgnore = Native.RegisterClipboardFormat("Clipboard Viewer Ignore");
    private static readonly uint FmtCanIncludeHistory = Native.RegisterClipboardFormat("CanIncludeInClipboardHistory");

    /// <summary>Raised on the UI thread for genuinely new clipboard content.</summary>
    public event Action<ClipContent>? Captured;

    private bool _listening;

    public void Attach(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(hwnd);
        _source.AddHook(WndProc);
        SetListening(true);
    }

    /// <summary>Start / stop the clipboard listener (Clipboard + Shelf page switched on / off).</summary>
    public void SetListening(bool on)
    {
        if (_source == null || on == _listening) return;
        _listening = on;
        if (on) Native.AddClipboardFormatListener(_source.Handle);
        else Native.RemoveClipboardFormatListener(_source.Handle);
    }

    private bool _readQueued;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE && !_readQueued)
        {
            // One update arrives per clipboard write (after the writer closes the clipboard).
            // Read it at Normal priority so busy rendering can't starve it and drop quick copies.
            _readQueued = true;
            _source?.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                _readQueued = false;
                Read();
            }));
        }
        return IntPtr.Zero;
    }

    private void Read()
    {
        try
        {
            if (Native.IsClipboardFormatAvailable(FmtExcludeMonitor) ||
                Native.IsClipboardFormatAvailable(FmtViewerIgnore) ||
                Native.IsClipboardFormatAvailable(FmtCanIncludeHistory) && IsHistoryOptOut())
                return;

            ClipContent? content = null;
            for (int attempt = 0; attempt < 3 && content == null; attempt++)
            {
                try { content = TryReadClipboard(); break; }
                catch (COMException) { Thread.Sleep(30); } // clipboard busy
            }
            if (content == null) return;
            if (content.Fingerprint == _selfFingerprint || DateTime.UtcNow - _selfCopyAt < TimeSpan.FromMilliseconds(700))
            {
                _selfFingerprint = null;
                return;
            }
            Captured?.Invoke(content);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Clipboard read failed: {ex.Message}");
        }
    }

    private static bool IsHistoryOptOut()
    {
        try
        {
            if (Clipboard.GetData("CanIncludeInClipboardHistory") is System.IO.MemoryStream ms && ms.Length >= 4)
            {
                var b = ms.ToArray();
                return BitConverter.ToInt32(b, 0) == 0;
            }
        }
        catch { }
        return false;
    }

    private static ClipContent? TryReadClipboard()
    {
        if (Clipboard.ContainsImage())
        {
            var img = Clipboard.GetImage();
            if (img == null) return null;
            var normalized = new FormatConvertedBitmap(img, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int stride = normalized.PixelWidth * 4;
            var pixels = new byte[stride * normalized.PixelHeight];
            normalized.CopyPixels(pixels, stride, 0);
            var frozen = BitmapSource.Create(normalized.PixelWidth, normalized.PixelHeight, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
            frozen.Freeze();
            return new ClipContent { Kind = ClipKind.Image, Image = frozen, Fingerprint = "img:" + Convert.ToHexString(MD5.HashData(pixels)) };
        }
        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText();
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
    public void CopyBack(ClipContent c)
    {
        try
        {
            _selfFingerprint = c.Fingerprint;
            _selfCopyAt = DateTime.UtcNow;
            if (c.Kind == ClipKind.Image && c.Image != null) Clipboard.SetImage(c.Image);
            else if (c.Text != null) Clipboard.SetText(c.Text);
        }
        catch (COMException) { _selfFingerprint = null; }
    }

    public void Dispose()
    {
        if (_source != null)
        {
            SetListening(false);
            _source.RemoveHook(WndProc);
        }
    }
}
