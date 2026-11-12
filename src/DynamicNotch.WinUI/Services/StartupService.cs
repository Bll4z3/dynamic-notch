using Microsoft.Win32;
using Windows.ApplicationModel;

namespace DynamicNotch.Services;

/// <summary>
/// "Start with Windows". Packaged builds use the MSIX StartupTask declared in AppxManifest.xml;
/// a plain debug run falls back to the per-user Run registry key.
/// </summary>
public static class StartupService
{
    private const string TaskId = "DynamicNotchStartup";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "DynamicNotch";

    public static async Task<bool> IsEnabledAsync()
    {
        if (NotificationService.HasPackageIdentity())
        {
            try
            {
                var task = await StartupTask.GetAsync(TaskId);
                return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
            }
            catch { return false; }
        }
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) != null;
    }

    /// <summary>Returns the resulting state (the user or policy may refuse).</summary>
    public static async Task<bool> SetEnabledAsync(bool enable)
    {
        if (NotificationService.HasPackageIdentity())
        {
            try
            {
                var task = await StartupTask.GetAsync(TaskId);
                if (enable)
                {
                    var state = await task.RequestEnableAsync();
                    return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
                }
                task.Disable();
                return false;
            }
            catch { return false; }
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable)
        {
            var exe = Environment.ProcessPath;
            if (exe != null) key.SetValue(RunValue, $"\"{exe}\"");
        }
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
        return enable;
    }
}
