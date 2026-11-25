using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace DynamicNotch.Services;

/// <summary>
/// Playlist and album covers for the notch's Spotify panel, cached so scrolling stays smooth: decoded images in memory
/// (least recently used dropped beyond <see cref="MemoryLimit"/>), the downloaded files on disk
/// (%LocalAppData%\DynamicNotch\spotify-art, oldest pruned beyond <see cref="DiskLimit"/>). Each image is decoded at
/// the size it's drawn at, and a cover asked for twice at once is downloaded once. UI thread only.
/// </summary>
public static class SpotifyArtCache
{
    private const int MemoryLimit = 400, DiskLimit = 1500;
    private static readonly string Folder = Path.Combine(SettingsService.DataFolder, "spotify-art");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Image)>> Memory = new();
    private static readonly LinkedList<(string Key, ImageSource Image)> Order = new();
    private static readonly Dictionary<string, Task<ImageSource?>> Loading = new();
    private static bool _pruned;

    /// <summary>The image already in memory (no wait), or null.</summary>
    public static ImageSource? Peek(string? url, int decodeWidth)
    {
        if (url == null || !Memory.TryGetValue(Key(url, decodeWidth), out var node)) return null;
        Order.Remove(node);
        Order.AddFirst(node);
        return node.Value.Image;
    }

    /// <summary>The cover at <paramref name="url"/>, decoded <paramref name="decodeWidth"/> DIPs wide (null if it can't be had).</summary>
    public static Task<ImageSource?> GetAsync(string? url, int decodeWidth)
    {
        if (string.IsNullOrEmpty(url)) return Task.FromResult<ImageSource?>(null);
        if (Peek(url, decodeWidth) is { } hit) return Task.FromResult<ImageSource?>(hit);
        string key = Key(url, decodeWidth);
        if (Loading.TryGetValue(key, out var running)) return running;
        var task = LoadAsync(url, key, decodeWidth);
        Loading[key] = task;
        return task;
    }

    private static async Task<ImageSource?> LoadAsync(string url, string key, int decodeWidth)
    {
        try
        {
            var bytes = await Task.Run(() => BytesAsync(url));
            if (bytes == null) return null;
            var image = new BitmapImage { DecodePixelWidth = decodeWidth, DecodePixelType = DecodePixelType.Logical };
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await image.SetSourceAsync(stream);
            Remember(key, image);
            return image;
        }
        catch { return null; }
        finally { Loading.Remove(key); }
    }

    /// <summary>The file from disk, else downloaded (and saved). Background thread.</summary>
    private static async Task<byte[]?> BytesAsync(string url)
    {
        string path = Path.Combine(Folder, Hash(url));
        try
        {
            if (File.Exists(path))
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
                return await File.ReadAllBytesAsync(path);
            }
        }
        catch { }
        byte[] bytes;
        try { bytes = await Http.GetByteArrayAsync(url); }
        catch { return null; } // offline: the row keeps its placeholder
        try
        {
            Directory.CreateDirectory(Folder);
            string temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, path, overwrite: true);
            if (!_pruned) { _pruned = true; Prune(); }
        }
        catch { }
        return bytes;
    }

    private static void Remember(string key, ImageSource image)
    {
        if (Memory.TryGetValue(key, out var old)) Order.Remove(old);
        var node = Order.AddFirst((key, image));
        Memory[key] = node;
        while (Order.Count > MemoryLimit && Order.Last is { } last)
        {
            Order.RemoveLast();
            Memory.Remove(last.Value.Key);
        }
    }

    /// <summary>Keeps the disk cache bounded: the least recently used files go first.</summary>
    private static void Prune()
    {
        try
        {
            var files = new DirectoryInfo(Folder).GetFiles();
            if (files.Length <= DiskLimit) return;
            foreach (var f in files.OrderBy(f => f.LastAccessTimeUtc).Take(files.Length - DiskLimit * 4 / 5))
                try { f.Delete(); } catch { }
        }
        catch { }
    }

    private static string Key(string url, int width) => width + "|" + url;
    private static string Hash(string url) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)))[..24].ToLowerInvariant();
}
