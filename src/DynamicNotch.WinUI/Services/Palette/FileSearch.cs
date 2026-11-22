using System.Data.OleDb;

namespace DynamicNotch.Services.Palette;

/// <summary>A file or folder found by name.</summary>
public sealed record FileHit(string Path, string Name, bool IsFolder);

/// <summary>
/// File and folder search by name through the Windows Search index (the same index File Explorer's search box
/// uses), so it's instant even on a big drive. Queries run off the UI thread; a newer query cancels the older one.
/// Only indexed locations are found (Settings ▸ Privacy ▸ Searching Windows decides which).
/// </summary>
public static class FileSearch
{
    private const string Connection = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

    public static Task<IReadOnlyList<FileHit>> SearchAsync(string term, int max, CancellationToken ct) => Task.Run<IReadOnlyList<FileHit>>(() =>
    {
        var list = new List<FileHit>();
        term = term.Trim();
        if (term.Length < 2) return list;
        // Only letters, digits and a few safe characters reach the query text (no SQL / CONTAINS injection).
        string safe = new string(term.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.').ToArray()).Trim();
        if (safe.Length < 2) return list;
        string words = string.Join(" AND ", safe.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => $"\"{w}*\""));
        string sql = $"SELECT TOP {max} System.ItemPathDisplay, System.ItemNameDisplay, System.ItemType FROM SystemIndex " +
                     $"WHERE SCOPE='file:' AND CONTAINS(System.FileName, '{words}') ORDER BY System.Search.Rank DESC, System.DateModified DESC";
        try
        {
            using var conn = new OleDbConnection(Connection);
            conn.Open();
            using var cmd = new OleDbCommand(sql, conn);
            using var r = cmd.ExecuteReader();
            while (r != null && r.Read())
            {
                if (ct.IsCancellationRequested) break;
                string path = r[0] as string ?? "";
                if (path.Length == 0) continue;
                string name = r[1] as string ?? Path.GetFileName(path);
                bool folder = string.Equals(r[2] as string, "Directory", StringComparison.OrdinalIgnoreCase);
                list.Add(new FileHit(path, name, folder));
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"File search failed: {ex.Message}"); }
        return list;
    }, ct);
}
