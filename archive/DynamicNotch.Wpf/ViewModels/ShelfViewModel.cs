using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicNotch.Interop;
using DynamicNotch.Services;

namespace DynamicNotch.ViewModels;

public sealed class ShelfViewModel : ObservableObject
{
    private readonly ShelfService _service;

    public ShelfViewModel(ShelfService service)
    {
        _service = service;
        foreach (var p in _service.Paths) Items.Add(new ShelfItemViewModel(p, this));
        ClearAllCommand = new RelayCommand(ClearAll, () => Items.Count > 0);
    }

    public ObservableCollection<ShelfItemViewModel> Items { get; } = new();
    public ICommand ClearAllCommand { get; }
    public bool IsEmpty => Items.Count == 0;

    public void AddPaths(IEnumerable<string> paths)
    {
        var added = _service.Add(paths);
        for (int i = added.Count - 1; i >= 0; i--) Items.Insert(0, new ShelfItemViewModel(added[i], this));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Re-check which files still exist (greys out moved/deleted ones).</summary>
    public void RefreshExistence()
    {
        foreach (var i in Items) i.Refresh();
    }

    internal void Remove(ShelfItemViewModel item)
    {
        if (item.IsLeaving) return;
        item.IsLeaving = true;
        _service.Remove(item.Path);
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        t.Tick += (_, _) => { t.Stop(); Items.Remove(item); OnPropertyChanged(nameof(IsEmpty)); };
        t.Start();
    }

    private void ClearAll()
    {
        _service.Clear();
        foreach (var i in Items) i.IsLeaving = true;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        t.Tick += (_, _) => { t.Stop(); Items.Clear(); OnPropertyChanged(nameof(IsEmpty)); };
        t.Start();
    }
}

public sealed class ShelfItemViewModel : ObservableObject
{
    private readonly ShelfViewModel _owner;

    public ShelfItemViewModel(string path, ShelfViewModel owner)
    {
        Path = path;
        _owner = owner;
        OpenCommand = new RelayCommand(Open);
        RemoveCommand = new RelayCommand(() => _owner.Remove(this));
        Refresh();
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
    public ICommand OpenCommand { get; }
    public ICommand RemoveCommand { get; }

    private bool _exists;
    public bool Exists { get => _exists; private set => Set(ref _exists, value); }

    private BitmapSource? _icon;
    public BitmapSource? Icon { get => _icon; private set => Set(ref _icon, value); }

    private bool _isLeaving;
    public bool IsLeaving { get => _isLeaving; set => Set(ref _isLeaving, value); }

    public bool IsDirectory => Directory.Exists(Path);

    public void Refresh()
    {
        bool exists = ShelfService.Exists(Path);
        if (exists == _exists && _icon != null) return;
        Exists = exists;
        Icon = Native.GetShellIcon(Path, exists, IsDirectory);
    }

    private void Open()
    {
        Refresh();
        if (!Exists) return;
        try { Process.Start(new ProcessStartInfo(Path) { UseShellExecute = true }); }
        catch (Exception ex) { Debug.WriteLine($"Open failed: {ex.Message}"); }
    }
}
