using System.Diagnostics;
using System.IO;

namespace DynamicNotch.Services;

/// <summary>
/// Gets Claude Code to renew its own sign-in without the user opening it. Claude Code's access token
/// only lasts a few hours and only Claude Code refreshes it, so when it has expired (or is about to)
/// we run <c>claude auth status</c> hidden, with no window. That command refreshes and saves the token
/// as a side effect, sends no message to Claude and uses none of the plan's usage, then exits.
///
/// Claude Code stays the only writer of its credentials file; we only start the process and wait.
/// Its output (which includes account details) is read and discarded, never logged. Attempts are
/// limited to one every few minutes, so a broken install can't keep spawning processes.
/// </summary>
internal static class ClaudeCliRefresher
{
    private static readonly TimeSpan MinGap = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset _lastAttempt = DateTimeOffset.MinValue;

    /// <summary>True when the CLI ran and exited cleanly. The caller re-reads the credentials file to see the new token.</summary>
    public static async Task<bool> TryRefreshAsync(CancellationToken ct)
    {
        if (!await Gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            // Another refresh is already running: wait for it rather than starting a second one.
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            Gate.Release();
            return true;
        }
        try
        {
            if (DateTimeOffset.UtcNow - _lastAttempt < MinGap) return false;
            _lastAttempt = DateTimeOffset.UtcNow;

            var psi = StartInfo();
            if (psi == null) return false;

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start()) return false;
            proc.StandardInput.Close();
            // Drain both streams so the process can't block on a full pipe; the text is thrown away.
            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            var errTask = proc.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                return false;
            }
            try { await Task.WhenAll(outTask, errTask).ConfigureAwait(false); } catch { }
            return proc.ExitCode == 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return false; // CLI missing, blocked or crashed: the caller falls back to "open Claude Code"
        }
        finally
        {
            Gate.Release();
        }
    }

    private static ProcessStartInfo? StartInfo()
    {
        string? exe = FindClaude();
        if (exe == null) return null;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        ProcessStartInfo psi;
        if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // npm installs a .cmd shim, which has to go through cmd.exe.
            psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(exe);
        }
        else
        {
            psi = new ProcessStartInfo(exe);
        }
        psi.ArgumentList.Add("auth");
        psi.ArgumentList.Add("status");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WindowStyle = ProcessWindowStyle.Hidden;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.WorkingDirectory = home; // not a project folder, so no project settings or trust prompts apply
        return psi;
    }

    /// <summary>Claude Code's native install location first, then anything named claude on PATH.</summary>
    private static string? FindClaude()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string native = Path.Combine(home, ".local", "bin", "claude.exe");
        if (File.Exists(native)) return native;

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var name in new[] { "claude.exe", "claude.cmd" })
            {
                try
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { } // malformed PATH entry
            }
        }
        return null;
    }
}
