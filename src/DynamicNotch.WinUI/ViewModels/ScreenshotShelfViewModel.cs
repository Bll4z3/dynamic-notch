using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using DynamicNotch.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DynamicNotch.ViewModels;

/// <summary>One screenshot on the shelf: a real file (so it can be dragged out as one) and a small preview.</summary>
public sealed class ScreenshotItem : ObservableObject
{
    public ScreenshotItem(string path, bool owned, DateTime time) { _path = path; Owned = owned; Time = time; }

    private string _path;
    /// <summary>The image file. Replaced by the marked-up copy after Mark up.</summary>
    public string Path { get => _path; set => Set(ref _path, value); }
    /// <summary>True when the app wrote the file (a clipboard-only snip or a mark-up): Delete removes the file too.</summary>
    public bool Owned { get; set; }
    public DateTime Time { get; }

    private SoftwareBitmap? _thumb;
    /// <summary>Preview-sized copy (long edge ≤ 640 px), loaded off the UI thread.</summary>
    public SoftwareBitmap? Thumbnail { get => _thumb; set => Set(ref _thumb, value); }
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }

    /// <summary>Bumped whenever the image changed (mark-up), so views reload their picture.</summary>
    public int Version { get; private set; }
    public void Touch() { Version++; OnPropertyChanged(nameof(Version)); }
}

/// <summary>
/// The screenshot shelf: the newest screenshots (newest first, up to the "keep" setting), the selected one,
/// and what the action row does: copy, copy the text in it (Windows OCR, on this PC), save a copy, delete,
/// and the hand-off to Ask (a <see cref="Screenshot"/> for the API) and Mark up. The list (file paths only)
/// is saved to screenshots.json so the shelf survives a restart; entries whose file is gone are dropped.
/// </summary>
public sealed class ScreenshotShelfViewModel : ObservableObject
{
    private const string StoreFile = "screenshots.json";
    private readonly SettingsService _settings;
    private readonly ScreenshotService _service;

    public ScreenshotShelfViewModel(SettingsService settings, ScreenshotService service)
    {
        _settings = settings;
        _service = service;
        foreach (var e in SettingsService.ReadJson<List<StoredShot>>(StoreFile) ?? new())
            if (File.Exists(e.Path)) Items.Add(new ScreenshotItem(e.Path, e.Owned, e.Time));
        Selected = Items.FirstOrDefault();
        foreach (var item in Items) _ = LoadThumbAsync(item);
    }

    private sealed record StoredShot(string Path, bool Owned, DateTime Time);

    public ObservableCollection<ScreenshotItem> Items { get; } = new();

    private ScreenshotItem? _selected;
    public ScreenshotItem? Selected { get => _selected; set { if (Set(ref _selected, value)) OnPropertyChanged(nameof(HasSelection)); } }
    public bool HasSelection => _selected != null;

    /// <summary>A short confirmation for the page to show ("Copied", "Text copied"…), or an error.</summary>
    public event Action<string, bool>? Toast;
    public void Notify(string text, bool error) => Toast?.Invoke(text, error);

    /// <summary>The folder Save copies into (Settings, or the Windows Screenshots folder).</summary>
    public string SaveFolder => string.IsNullOrWhiteSpace(_settings.Current.ScreenshotSaveFolder) ? ScreenshotService.ScreenshotsFolder : _settings.Current.ScreenshotSaveFolder!;

    // ---- Adding / removing -----------------------------------------------------------------------------
    /// <summary>A new screenshot arrived: on the front of the shelf and selected; the oldest drop off past the limit.</summary>
    public async Task<ScreenshotItem> AddAsync(string path, bool owned)
    {
        var existing = Items.FirstOrDefault(i => i.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { Selected = existing; return existing; }
        var item = new ScreenshotItem(path, owned, DateTime.Now);
        await LoadThumbAsync(item); // the drop-in animation wants the picture ready
        Items.Insert(0, item);
        Selected = item;
        int keep = Math.Clamp(_settings.Current.ScreenshotKeep, 3, 30);
        while (Items.Count > keep) Drop(Items[^1], deleteOwnedFile: true);
        Persist();
        return item;
    }

    /// <summary>Delete: off the shelf, and the file too when the app wrote it.</summary>
    public void Delete(ScreenshotItem item)
    {
        int i = Items.IndexOf(item);
        Drop(item, deleteOwnedFile: true);
        if (ReferenceEquals(Selected, item) || Selected == null) Selected = Items.Count == 0 ? null : Items[Math.Clamp(i, 0, Items.Count - 1)];
        Persist();
    }

    /// <summary>The keep setting went down: trim now.</summary>
    public void ApplyLimit()
    {
        int keep = Math.Clamp(_settings.Current.ScreenshotKeep, 3, 30);
        bool changed = false;
        while (Items.Count > keep) { Drop(Items[^1], deleteOwnedFile: true); changed = true; }
        if (Selected != null && !Items.Contains(Selected)) Selected = Items.FirstOrDefault();
        if (changed) Persist();
    }

    private void Drop(ScreenshotItem item, bool deleteOwnedFile)
    {
        Items.Remove(item);
        if (deleteOwnedFile && item.Owned) { try { File.Delete(item.Path); } catch { } }
    }

    /// <summary>Mark up saved a new image for <paramref name="item"/> (an app-owned file).</summary>
    public async Task ReplaceAsync(ScreenshotItem item, string newPath)
    {
        string old = item.Path;
        bool oldOwned = item.Owned;
        item.Path = newPath;
        item.Owned = true;
        if (oldOwned && !old.Equals(newPath, StringComparison.OrdinalIgnoreCase)) { try { File.Delete(old); } catch { } }
        await LoadThumbAsync(item);
        item.Touch();
        Persist();
    }

    private void Persist() =>
        SettingsService.WriteJson(StoreFile, Items.Select(i => new StoredShot(i.Path, i.Owned, i.Time)).ToList());

    private static async Task LoadThumbAsync(ScreenshotItem item)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            item.PixelWidth = (int)decoder.PixelWidth;
            item.PixelHeight = (int)decoder.PixelHeight;
            double k = Math.Min(1.0, 640.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * k)),
                ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * k)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            item.Thumbnail = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        }
        catch { /* unreadable: the tile shows a placeholder */ }
    }

    // ---- Actions --------------------------------------------------------------------------------------
    /// <summary>Copies the image (as a picture and as the file) to the clipboard.</summary>
    public async Task CopyAsync(ScreenshotItem item)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
            package.SetStorageItems(new[] { file });
            Clipboard.SetContent(package);
            Clipboard.Flush();
            Toast?.Invoke("Copied", false);
        }
        catch { Toast?.Invoke("Couldn't copy it", true); }
    }

    /// <summary>Reads the text in the screenshot with Windows' built-in OCR (nothing leaves the PC) and copies it.</summary>
    public async Task CopyTextAsync(ScreenshotItem item)
    {
        try
        {
            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) { Toast?.Invoke("Text recognition isn't available", true); return; }
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            double max = Windows.Media.Ocr.OcrEngine.MaxImageDimension;
            double k = Math.Min(1.0, max / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var transform = new BitmapTransform { ScaledWidth = (uint)Math.Max(1, decoder.PixelWidth * k), ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * k) };
            using var bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            var result = await engine.RecognizeAsync(bmp);
            string text = string.Join(Environment.NewLine, result.Lines.Select(l => l.Text)).Trim();
            if (text.Length == 0) { Toast?.Invoke("No text found", true); return; }
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush();
            Toast?.Invoke(result.Lines.Count == 1 ? "Text copied" : $"Text copied · {result.Lines.Count} lines", false);
        }
        catch { Toast?.Invoke("Couldn't read the text", true); }
    }

    /// <summary>True when the file already sits in the save folder (Snipping Tool saved it there).</summary>
    public bool IsSaved(ScreenshotItem item) =>
        string.Equals(Path.GetDirectoryName(item.Path)?.TrimEnd('\\'), SaveFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Saves a copy into the save folder; when it's already there, shows it in Explorer instead.</summary>
    public async Task SaveAsync(ScreenshotItem item)
    {
        if (IsSaved(item)) { Reveal(item); return; }
        try
        {
            Directory.CreateDirectory(SaveFolder);
            string name = Path.GetFileNameWithoutExtension(item.Path);
            string ext = Path.GetExtension(item.Path);
            string target = Path.Combine(SaveFolder, name + ext);
            for (int n = 2; File.Exists(target); n++) target = Path.Combine(SaveFolder, $"{name} ({n}){ext}");
            _service.IgnoreNextWrite(target);
            await Task.Run(() => File.Copy(item.Path, target));
            if (item.Owned) { try { File.Delete(item.Path); } catch { } }
            item.Path = target;
            item.Owned = false; // it's the user's file now: Delete won't remove it
            Persist();
            OnPropertyChanged(nameof(Selected));
            Toast?.Invoke($"Saved to {Path.GetFileName(SaveFolder.TrimEnd('\\'))}", false);
        }
        catch { Toast?.Invoke("Couldn't save it", true); }
    }

    public static void Reveal(ScreenshotItem item)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.Path}\""); } catch { }
    }

    /// <summary>The screenshot as Ask's attachment: a JPEG (long edge ≤ the API limit) plus a preview copy.</summary>
    public static async Task<Screenshot?> ToAskAttachmentAsync(ScreenshotItem item)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(item.Path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            int w = (int)decoder.PixelWidth, h = (int)decoder.PixelHeight;
            double k = Math.Min(1.0, (double)AppConfig.ScreenshotMaxEdge / Math.Max(w, h));
            uint aw = (uint)Math.Max(1, Math.Round(w * k)), ah = (uint)Math.Max(1, Math.Round(h * k));
            using var full = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = aw, ScaledHeight = ah, InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            string base64;
            using (var jpeg = new InMemoryRandomAccessStream())
            {
                var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(AppConfig.ScreenshotJpegQuality / 100.0, Windows.Foundation.PropertyType.Single) };
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, jpeg, options);
                encoder.SetSoftwareBitmap(full);
                await encoder.FlushAsync();
                var bytes = new byte[jpeg.Size];
                jpeg.Seek(0);
                await jpeg.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
                base64 = Convert.ToBase64String(bytes);
            }
            double t = Math.Min(1.0, Math.Min((double)Design.Theme.ScreenshotPreviewPixelsW / aw, (double)Design.Theme.ScreenshotPreviewPixelsH / ah));
            stream.Seek(0);
            var thumb = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = (uint)Math.Max(1, w * k * t), ScaledHeight = (uint)Math.Max(1, h * k * t), InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            return new Screenshot(base64, thumb, (int)aw, (int)ah);
        }
        catch { return null; }
    }
}
