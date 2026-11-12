using System.Collections.ObjectModel;
using System.Windows.Input;
using Microsoft.UI.Xaml;
using DynamicNotch.Design;
using DynamicNotch.Services;

namespace DynamicNotch.ViewModels;

public sealed class ClipboardViewModel : ObservableObject
{
    /// <summary>How many un-pinned items to keep. Change this to grow the history.</summary>
    public const int HistoryLimit = 10;

    /// <summary>Pinned items on top of the history; together they fill the 3-row tile grid.</summary>
    public const int MaxPinned = 5;

    /// <summary>Time given to the leave animation before the item is removed from the list.</summary>
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromMilliseconds(260);

    private readonly ClipboardService _service;

    public ClipboardViewModel(ClipboardService service)
    {
        _service = service;
        _service.Captured += OnCaptured;
    }

    public ObservableCollection<ClipItemViewModel> Items { get; } = new();

    public bool IsEmpty => Items.Count == 0;

    private void OnCaptured(ClipContent content)
    {
        var existing = Items.FirstOrDefault(i => i.Content.Fingerprint == content.Fingerprint && !i.IsLeaving);
        if (existing != null)
        {
            // Same content copied again: bring it back to the top (instant, no duplicate).
            Items.Remove(existing);
            Items.Insert(InsertIndexFor(existing.IsPinned), existing);
            return;
        }

        var item = new ClipItemViewModel(content, this);
        Items.Insert(InsertIndexFor(false), item);
        TrimHistory();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Used by `--demo` to preview the tiles without touching the real clipboard.</summary>
    internal void AddPreview(ClipContent content) => OnCaptured(content);

    private int InsertIndexFor(bool pinned) => pinned ? 0 : Items.Count(i => i.IsPinned);

    private void TrimHistory()
    {
        var unpinned = Items.Where(i => !i.IsPinned && !i.IsLeaving).ToList();
        foreach (var old in unpinned.Skip(HistoryLimit)) Remove(old);
    }

    internal void CopyBack(ClipItemViewModel item)
    {
        _service.CopyBack(item.Content);
        if (Items.IndexOf(item) != InsertIndexFor(item.IsPinned))
        {
            Items.Remove(item);
            Items.Insert(InsertIndexFor(item.IsPinned), item);
        }
        item.FlashCopied();
    }

    internal void Remove(ClipItemViewModel item)
    {
        if (item.IsLeaving) return;
        item.IsLeaving = true;
        var t = new DispatcherTimer { Interval = LeaveDelay };
        t.Tick += (_, _) =>
        {
            t.Stop();
            Items.Remove(item);
            OnPropertyChanged(nameof(IsEmpty));
        };
        t.Start();
    }

    internal void TogglePin(ClipItemViewModel item)
    {
        if (!item.IsPinned && Items.Count(i => i.IsPinned) >= MaxPinned) return;
        item.IsPinned = !item.IsPinned;
        Items.Remove(item);
        Items.Insert(InsertIndexFor(item.IsPinned), item);
        TrimHistory();
    }
}

public sealed class ClipItemViewModel : ObservableObject
{
    private readonly ClipboardViewModel _owner;

    public ClipItemViewModel(ClipContent content, ClipboardViewModel owner)
    {
        Content = content;
        _owner = owner;
        CopyCommand = new RelayCommand(() => _owner.CopyBack(this));
        DeleteCommand = new RelayCommand(() => _owner.Remove(this));
        PinCommand = new RelayCommand(() => _owner.TogglePin(this));
    }

    public ClipContent Content { get; }
    public ICommand CopyCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand PinCommand { get; }

    public bool IsImage => Content.Kind == ClipKind.Image;
    public bool IsLink => Content.Kind == ClipKind.Link;
    public bool IsText => Content.Kind == ClipKind.Text;
    public Windows.Graphics.Imaging.SoftwareBitmap? Image => Content.Image;

    /// <summary>
    /// UniformToFill only when the image's aspect matches the (square) tile so nothing is cropped;
    /// otherwise Uniform letterboxed on a dark backing.
    /// </summary>
    public Microsoft.UI.Xaml.Media.Stretch ImageStretch
    {
        get
        {
            if (Content.Image is not { PixelWidth: > 0, PixelHeight: > 0 } img) return Microsoft.UI.Xaml.Media.Stretch.Uniform;
            double aspect = (double)img.PixelWidth / img.PixelHeight;
            return Math.Abs(aspect - 1) < 0.02 ? Microsoft.UI.Xaml.Media.Stretch.UniformToFill : Microsoft.UI.Xaml.Media.Stretch.Uniform;
        }
    }

    /// <summary>The first few characters, shown clipped inside a text tile.</summary>
    public string Snippet
    {
        get
        {
            var s = Collapse(Content.Text ?? "");
            return s.Length > 40 ? s[..40] : s;
        }
    }

    public string Preview => Content.Kind switch
    {
        ClipKind.Image => $"Image · {Content.Image?.PixelWidth}×{Content.Image?.PixelHeight}",
        _ => Collapse(Content.Text ?? ""),
    };

    public string KindGlyph => Content.Kind switch
    {
        ClipKind.Link => Icons.Link,
        ClipKind.Image => Icons.Image,
        _ => Icons.Text,
    };

    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set { if (Set(ref _isPinned, value)) OnPropertyChanged(nameof(PinGlyph)); }
    }

    public string PinGlyph => IsPinned ? Icons.Unpin : Icons.Pin;

    private bool _isLeaving;
    public bool IsLeaving { get => _isLeaving; set => Set(ref _isLeaving, value); }

    private bool _justCopied;
    public bool JustCopied { get => _justCopied; private set => Set(ref _justCopied, value); }

    internal void FlashCopied()
    {
        JustCopied = true;
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        t.Tick += (_, _) => { t.Stop(); JustCopied = false; };
        t.Start();
    }

    private static string Collapse(string s)
    {
        var one = string.Join(' ', s.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return one.Length > 120 ? one[..120] + "…" : one;
    }
}
