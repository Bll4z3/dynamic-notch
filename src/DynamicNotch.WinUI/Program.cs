using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DynamicNotch;

public static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // One notch per user session (the same lock the WPF app used, so the two never run together).
        // Checked before XAML starts so a second launch costs nothing.
        using var singleInstance = new Mutex(true, @"Local\DynamicNotch.SingleInstance", out bool first);
        if (!first) return;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(args);
        });
        GC.KeepAlive(singleInstance);
    }
}
