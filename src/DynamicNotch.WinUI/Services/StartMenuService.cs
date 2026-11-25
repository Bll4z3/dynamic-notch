using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DynamicNotch.Services;

/// <summary>
/// The portable zip has no installer, so on start-up it adds itself to the Start menu: a per-user
/// "Dynamic Notch.lnk" pointing at the running exe, rewritten only when missing or when the folder was moved.
/// The in-app updater replaces files in place, so the shortcut stays valid across updates.
/// Packaged (MSIX) builds already have a Start entry, and dev builds (run from the repo's bin folder) are left alone.
/// </summary>
public static class StartMenuService
{
    private const string ShortcutName = "Dynamic Notch.lnk";

    /// <summary>Unpackaged release build: the zip extracted somewhere (not a package, not the repo's bin output).</summary>
    public static bool IsPortableInstall(string? exe) =>
        exe != null && !NotificationService.HasPackageIdentity() && !IsDevBuild(exe);

    /// <summary>
    /// True when the exe sits under a project's <c>bin\</c> folder (bin\x64\Debug\…, bin\…\publish): a folder named
    /// "bin" whose parent holds a .csproj. Dev builds share settings.json with the real app, so they must not
    /// repoint the Start shortcut or the Run value at themselves.
    /// </summary>
    public static bool IsDevBuild(string exe)
    {
        try
        {
            for (var dir = new DirectoryInfo(Path.GetDirectoryName(exe)!); dir?.Parent != null; dir = dir.Parent)
            {
                if (dir.Name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                    && dir.Parent.EnumerateFiles("*.csproj").Any())
                    return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Creates or repairs the Start menu shortcut and the "Start with Windows" Run value. Never throws.</summary>
    public static void EnsureRegistered()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!IsPortableInstall(exe)) return;
            EnsureShortcut(exe!);
            StartupService.RepairRunValue(exe!);
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    private static void EnsureShortcut(string exe)
    {
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs); // %AppData%\…\Start Menu\Programs
        if (string.IsNullOrEmpty(programs)) return;
        string lnk = Path.Combine(programs, ShortcutName);

        if (File.Exists(lnk) && SamePath(ReadTarget(lnk), exe)) return;

        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe)!);
            link.SetIconLocation(exe, 0);
            link.SetDescription("Dynamic Notch");
            Directory.CreateDirectory(programs);
            ((IPersistFile)link).Save(lnk, true);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }

    private static string? ReadTarget(string lnk)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(lnk, 0 /* STGM_READ */);
            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 4 /* SLGP_RAWPATH */);
            return Environment.ExpandEnvironmentVariables(sb.ToString());
        }
        catch { return null; } // unreadable shortcut: rewrite it
        finally { Marshal.FinalReleaseComObject(link); }
    }

    internal static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
