using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Dispatching;

namespace DynamicNotch.Services;

public enum UpdateStatus { Idle, Checking, UpToDate, Available, Downloading, Ready, Failed }

/// <summary>A newer release on GitHub and the download that fits this PC.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? AssetName, string? AssetUrl, long AssetSize, string? Sha256);

/// <summary>
/// Update checks against the project's GitHub Releases. The NETWORK RULE allows exactly this: one
/// anonymous request to api.github.com (no identifiers, a generic User-Agent), made automatically at
/// most once a day and only when "Check for updates automatically" is on, plus a download the user
/// starts. The download is checked against GitHub's SHA-256 digest when the release provides one,
/// unzipped next to the settings, and installed by a tiny script that waits for the app to quit,
/// copies the new files over the old ones and starts the new version. Packaged (MSIX) or read-only
/// installs aren't replaced in place: they get the release page instead.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly HttpClient Http = CreateClient();
    private readonly SettingsService _settings;
    private DispatcherQueueTimer? _timer;
    private CancellationTokenSource? _downloadCts;
    private string? _readyFolder;

    public UpdateService(SettingsService settings) => _settings = settings;

    public static Version CurrentVersion { get; } = ReadCurrentVersion();
    public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

    public UpdateStatus Status { get; private set; }
    public UpdateInfo? Available { get; private set; }
    public double Progress { get; private set; }
    public string Message { get; private set; } = "";
    public DateTimeOffset? LastChecked => _settings.Current.LastUpdateCheck;

    /// <summary>Raised on the UI thread whenever Status / Progress / Message change.</summary>
    public event Action? Changed;
    /// <summary>Raised on the UI thread the first time a check finds a given newer version.</summary>
    public event Action<UpdateInfo>? UpdateFound;
    /// <summary>Download progress for the activity pill (raised on the UI thread).</summary>
    public event Action<DownloadInfo>? DownloadProgress;
    /// <summary>The app should quit now so the installer script can replace its files.</summary>
    public event Action? QuitRequested;

    /// <summary>In-place install works for the unpackaged zip build in a folder this user can write to.</summary>
    public static bool CanInstallInPlace => !NotificationService.HasPackageIdentity() && IsWritable(AppContext.BaseDirectory);

    // ---- Scheduling --------------------------------------------------------------------------------
    public void Start()
    {
        CleanUpOldDownloads();
        _timer = Ui.Queue.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += async (_, _) => { await AutoCheckAsync(); Schedule(); };
        Schedule(first: true);
        _settings.Changed += () => Schedule();
    }

    private void Schedule(bool first = false)
    {
        if (_timer == null) return;
        _timer.Stop();
        if (!_settings.Current.AutoCheckUpdates) return;
        var last = _settings.Current.LastUpdateCheck ?? DateTimeOffset.MinValue;
        var due = last + AppConfig.UpdateCheckInterval - DateTimeOffset.Now;
        // Never at the very moment the app starts (let it settle), and at most once a day.
        var min = first ? AppConfig.UpdateFirstCheckDelay : TimeSpan.FromMinutes(1);
        _timer.Interval = due < min ? min : due;
        _timer.Start();
    }

    private async Task AutoCheckAsync()
    {
        if (!_settings.Current.AutoCheckUpdates || Status is UpdateStatus.Checking or UpdateStatus.Downloading or UpdateStatus.Ready) return;
        await CheckAsync(manual: false);
    }

    // ---- Check -------------------------------------------------------------------------------------
    public async Task CheckAsync(bool manual = true)
    {
        if (Status is UpdateStatus.Checking or UpdateStatus.Downloading) return;
        Set(UpdateStatus.Checking, "Checking for updates…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{AppConfig.UpdateRepo}/releases/latest");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await Http.SendAsync(req, cts.Token);
            _settings.Update(s => s.LastUpdateCheck = DateTimeOffset.Now);
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound) { Set(UpdateStatus.UpToDate, "No releases published yet"); return; }
            res.EnsureSuccessStatusCode();
            var info = Parse(await res.Content.ReadAsStringAsync(cts.Token));
            if (info == null) { Set(UpdateStatus.Failed, "Couldn't read the latest release"); return; }
            if (info.Version <= CurrentVersion)
            {
                Available = null;
                Set(UpdateStatus.UpToDate, $"You're up to date (v{CurrentVersionText})");
                return;
            }
            Available = info;
            Set(UpdateStatus.Available, $"Version {info.Version.ToString(3)} is available");
            if (manual || _settings.Current.LastAnnouncedVersion != info.Tag)
            {
                _settings.Update(s => s.LastAnnouncedVersion = info.Tag);
                UpdateFound?.Invoke(info);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (manual) Set(UpdateStatus.Failed, "Couldn't reach GitHub. Check your connection.");
            else if (Available != null) Set(UpdateStatus.Available, $"Version {Available.Version.ToString(3)} is available");
            else Set(UpdateStatus.Idle, "");
        }
    }

    internal static UpdateInfo? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V').Split('-')[0], out var version)) return null;
        string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        if (!IsTrustedUrl(page)) page = $"https://github.com/{AppConfig.UpdateRepo}/releases/latest";

        string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        string? name = null, url = null, sha = null;
        long size = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                string n = a.GetProperty("name").GetString() ?? "";
                if (!n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || !n.Contains("win-" + arch, StringComparison.OrdinalIgnoreCase)) continue;
                string u = a.GetProperty("browser_download_url").GetString() ?? "";
                if (!IsTrustedUrl(u)) continue;
                name = n; url = u;
                size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                if (a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String && dg.GetString() is { } digest &&
                    digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = digest[7..];
                break;
            }
        }
        return new UpdateInfo(version.Build < 0 ? new Version(version.Major, version.Minor, 0) : version, tag, page, name, url, size, sha);
    }

    private static bool IsTrustedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    // ---- Download + install ------------------------------------------------------------------------
    /// <summary>
    /// The user's "Download" / "Update": installs in place when possible, otherwise opens the release page.
    /// Safe to call repeatedly (a running download is left alone; a finished one installs).
    /// </summary>
    public async Task GetUpdateAsync()
    {
        var info = Available;
        if (info == null) { await CheckAsync(manual: true); info = Available; if (info == null) return; }
        if (Status == UpdateStatus.Ready && _readyFolder != null) { InstallAndRestart(); return; }
        if (Status == UpdateStatus.Downloading) return;
        if (!CanInstallInPlace || info.AssetUrl == null) { OpenReleasePage(); return; }

        _downloadCts = new CancellationTokenSource();
        var ct = _downloadCts.Token;
        string folder = Path.Combine(SettingsService.DataFolder, "updates");
        string zip = Path.Combine(folder, info.AssetName!);
        string key = "update:" + info.Tag;
        string label = $"Dynamic Notch {info.Version.ToString(3)}";
        try
        {
            Directory.CreateDirectory(folder);
            Progress = 0;
            Set(UpdateStatus.Downloading, $"Downloading {info.Version.ToString(3)}…");
            using (var req = new HttpRequestMessage(HttpMethod.Get, info.AssetUrl))
            using (var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                res.EnsureSuccessStatusCode();
                if (res.RequestMessage?.RequestUri is { } final && !IsTrustedUrl(final.ToString())) throw new InvalidDataException("Unexpected download host");
                long total = res.Content.Headers.ContentLength ?? info.AssetSize;
                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(zip);
                var buffer = new byte[81920];
                long received = 0;
                var lastReport = Stopwatch.StartNew();
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    received += n;
                    if (lastReport.ElapsedMilliseconds >= Design.Motion.DownloadThrottleMs)
                    {
                        lastReport.Restart();
                        Progress = total > 0 ? (double)received / total : 0;
                        Changed?.Invoke();
                        DownloadProgress?.Invoke(new DownloadInfo(key, label, received, total > 0 ? total : null, false, null));
                    }
                }
            }

            if (info.Sha256 != null)
            {
                await using var check = File.OpenRead(zip);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(check, ct));
                if (!hash.Equals(info.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The download didn't match GitHub's checksum");
            }

            string extract = Path.Combine(folder, info.Tag);
            if (Directory.Exists(extract)) Directory.Delete(extract, recursive: true);
            await Task.Run(() => ZipFile.ExtractToDirectory(zip, extract), ct);
            _readyFolder = FindAppFolder(extract) ?? throw new InvalidDataException("The download doesn't contain DynamicNotch.exe");
            try { File.Delete(zip); } catch { }
            Progress = 1;
            Set(UpdateStatus.Ready, $"Version {info.Version.ToString(3)} is ready. Restart to update.");
            DownloadProgress?.Invoke(new DownloadInfo(key, label, info.AssetSize, info.AssetSize, true, null));
        }
        catch (OperationCanceledException)
        {
            Set(UpdateStatus.Available, $"Version {info.Version.ToString(3)} is available");
            DownloadProgress?.Invoke(new DownloadInfo(key, label, 0, null, true, null, Failed: true));
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            Set(UpdateStatus.Failed, ex is InvalidDataException ? ex.Message : "The download failed. Try again, or get it from GitHub.");
            Status = UpdateStatus.Available; // the button stays usable
            DownloadProgress?.Invoke(new DownloadInfo(key, label, 0, null, true, null, Failed: true));
        }
        finally { _downloadCts?.Dispose(); _downloadCts = null; }
    }

    public void CancelDownload() => _downloadCts?.Cancel();

    /// <summary>Starts the installer script and asks the app to quit; the script relaunches the new version.</summary>
    public void InstallAndRestart()
    {
        if (_readyFolder == null) return;
        string target = AppContext.BaseDirectory.TrimEnd('\\');
        string exe = Path.Combine(target, "DynamicNotch.exe");
        int pid = Environment.ProcessId;
        string script = Path.Combine(SettingsService.DataFolder, "updates", "install-update.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" 2>NUL | find \"{pid}\" >NUL && (ping -n 2 127.0.0.1 >NUL & goto wait)\r\n" +
            $"robocopy \"{_readyFolder.TrimEnd('\\')}\" \"{target}\" /E /R:5 /W:1 /NFL /NDL /NJH /NJS /NP >NUL\r\n" +
            $"start \"\" \"{exe}\"\r\n");
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{script}\"\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            QuitRequested?.Invoke();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            Set(UpdateStatus.Failed, "Couldn't start the installer. Get the update from GitHub instead.");
        }
    }

    public void OpenReleasePage()
    {
        string url = Available?.PageUrl ?? $"https://github.com/{AppConfig.UpdateRepo}/releases/latest";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static string? FindAppFolder(string root)
    {
        if (File.Exists(Path.Combine(root, "DynamicNotch.exe"))) return root;
        return Directory.EnumerateFiles(root, "DynamicNotch.exe", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault();
    }

    /// <summary>Removes leftovers of updates that are now installed (or older).</summary>
    private static void CleanUpOldDownloads()
    {
        try
        {
            string folder = Path.Combine(SettingsService.DataFolder, "updates");
            if (!Directory.Exists(folder)) return;
            foreach (var dir in Directory.GetDirectories(folder))
                if (Version.TryParse(Path.GetFileName(dir).TrimStart('v', 'V'), out var v) && v <= CurrentVersion)
                    Directory.Delete(dir, recursive: true);
            foreach (var f in Directory.GetFiles(folder, "*.zip")) File.Delete(f);
        }
        catch { }
    }

    private void Set(UpdateStatus status, string message)
    {
        Status = status;
        Message = message;
        Changed?.Invoke();
    }

    private static bool IsWritable(string folder)
    {
        try
        {
            string probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static Version ReadCurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // Generic client name only: no user, machine or install identifiers are ever sent.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicNotch-UpdateCheck");
        return c;
    }

    public void Dispose()
    {
        _timer?.Stop();
        _downloadCts?.Cancel();
    }
}
