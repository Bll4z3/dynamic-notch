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

    /// <summary>
    /// Portable build moved to another folder (or the value was left by another copy): if the Run value exists but
    /// launches a different exe, point it at this one so "Start with Windows" keeps working. Callers skip packaged
    /// and dev builds (see <see cref="StartMenuService.IsPortableInstall"/>).
    /// </summary>
    public static void RepairRunValue(string exe)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(RunValue) is not string current) return;
        string target = current.Trim();
        if (target.StartsWith('"'))
        {
            int end = target.IndexOf('"', 1);
            target = end > 0 ? target[1..end] : target.Trim('"');
        }
        if (!StartMenuService.SamePath(target, exe)) key.SetValue(RunValue, $"\"{exe}\"");
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
