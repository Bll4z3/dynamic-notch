using Microsoft.UI.Dispatching;

namespace DynamicNotch.Services;

/// <summary>The UI thread's dispatcher queue (replaces WPF's Application.Current.Dispatcher in the services).</summary>
public static class Ui
{
    private static DispatcherQueue? _queue;

    /// <summary>Call once on the UI thread at startup.</summary>
    public static void Initialize() => _queue = DispatcherQueue.GetForCurrentThread();

    public static DispatcherQueue Queue => _queue ?? throw new InvalidOperationException("Ui.Initialize was not called.");

    /// <summary>Runs <paramref name="action"/> on the UI thread, asynchronously.</summary>
    public static void Post(Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal) =>
        _queue?.TryEnqueue(priority, () => action());
}
