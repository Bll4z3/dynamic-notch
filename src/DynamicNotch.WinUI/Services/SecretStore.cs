using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicNotch.Services;

/// <summary>
/// The Claude API key, stored only on this PC and encrypted for the current Windows user with DPAPI
/// (CryptProtectData, current-user scope: the same protection as ProtectedData with
/// DataProtectionScope.CurrentUser). Other Windows users, and copies of the file on another PC,
/// can't decrypt it. The key is never written in plain text, never logged and never put in settings.
/// The rest of the app only goes through Get / Set / Remove / HasKey.
/// </summary>
public sealed class SecretStore
{
    private static readonly string KeyPath = Path.Combine(SettingsService.DataFolder, "claude.key");
    // Extra entropy so the blob is only useful to this app, not to any DPAPI caller for this user.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DynamicNotch.ClaudeKey.v1");

    /// <summary>Raised on the UI thread whenever the key is saved or removed.</summary>
    public event Action? Changed;

    public bool HasKey => File.Exists(KeyPath);

    /// <summary>The decrypted key, or null if none is saved (or it can't be decrypted on this account).</summary>
    public string? Get()
    {
        try
        {
            if (!File.Exists(KeyPath)) return null;
            var plain = Unprotect(File.ReadAllBytes(KeyPath));
            if (plain == null) return null;
            try { return Encoding.UTF8.GetString(plain); }
            finally { Array.Clear(plain); }
        }
        catch { return null; }
    }

    public void Set(string key)
    {
        key = key.Trim();
        if (key.Length == 0) throw new ArgumentException("Empty key");
        var plain = Encoding.UTF8.GetBytes(key);
        try
        {
            var blob = Protect(plain) ?? throw new InvalidOperationException("Windows couldn't encrypt the key.");
            Directory.CreateDirectory(SettingsService.DataFolder);
            File.WriteAllBytes(KeyPath, blob);
        }
        finally { Array.Clear(plain); }
        Changed?.Invoke();
    }

    public void Remove()
    {
        try { if (File.Exists(KeyPath)) File.Delete(KeyPath); } catch { }
        Changed?.Invoke();
    }

    /// <summary>"sk-ant-…abcd": only the last 4 characters, never the full key.</summary>
    public string? Masked()
    {
        var k = Get();
        if (string.IsNullOrEmpty(k)) return null;
        string tail = k.Length >= 4 ? k[^4..] : k;
        return (k.StartsWith("sk-ant-", StringComparison.Ordinal) ? "sk-ant-…" : "…") + tail;
    }

    // ---- DPAPI -----------------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr h);

    private static byte[]? Protect(byte[] data) => Run(data, protect: true);
    private static byte[]? Unprotect(byte[] data) => Run(data, protect: false);

    private static byte[]? Run(byte[] data, bool protect)
    {
        var inHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var input = new DATA_BLOB { cbData = data.Length, pbData = inHandle.AddrOfPinnedObject() };
            var entropy = new DATA_BLOB { cbData = Entropy.Length, pbData = entHandle.AddrOfPinnedObject() };
            bool ok = protect
                ? CryptProtectData(ref input, "Dynamic Notch Claude key", ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out output);
            if (!ok) return null;
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally { LocalFree(output.pbData); }
        }
        finally
        {
            inHandle.Free();
            entHandle.Free();
        }
    }
}
