using System.IO;

namespace DynamicNotch.Services;

/// <summary>
/// The shelf stores file PATHS (never copies) and persists them to shelf.json between launches.
/// </summary>
public sealed class ShelfService
{
    private const string FileName = "shelf.json";
    private readonly List<string> _paths = new();

    public IReadOnlyList<string> Paths => _paths;

    public event Action? Changed;

    public void Load()
    {
        _paths.Clear();
        var saved = SettingsService.ReadJson<List<string>>(FileName);
        if (saved != null) _paths.AddRange(saved.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Adds paths (newest first). Returns the ones actually added.</summary>
    public IReadOnlyList<string> Add(IEnumerable<string> paths)
    {
        var added = new List<string>();
        foreach (var p in paths)
        {
            var full = Path.GetFullPath(p);
            if (_paths.Contains(full, StringComparer.OrdinalIgnoreCase)) continue;
            _paths.Insert(0, full);
            added.Add(full);
        }
        if (added.Count > 0) Save();
        return added;
    }

    public void Remove(string path)
    {
        if (_paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0) Save();
    }

    public void Clear()
    {
        _paths.Clear();
        Save();
    }

    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private void Save()
    {
        SettingsService.WriteJson(FileName, _paths);
        Changed?.Invoke();
    }
}
