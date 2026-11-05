using System;
using System.IO;
using System.Linq;
using System.Threading;
using SQLExtended.Export;
using SQLExtended.SchemaCompare.Engine;

namespace SQLExtended.SchemaCompare.UI;

/// <summary>
/// A live database as one side of a compare. It is written in the source box as <c>mssql://server/database</c>, and
/// read by scripting it with the extension's own folder export into a cache folder, then reading that folder like
/// any other export. So comparing against the server and against an export of it can't disagree. How to sign in is
/// the host's business (<see cref="ICompareHost.ConnectionStringFor"/>).
/// </summary>
public static class DatabaseSide
{
    public const string Prefix = "mssql://";

    private static readonly string CacheRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SQLExtended", "SchemaCompare", "Databases");

    public static bool IsDatabase(string text) => text != null && text.Trim().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static string Format(string server, string database) => Prefix + server.Trim() + "/" + database.Trim();

    /// <summary>A server name can't hold '/', so the first one after the prefix splits it from the database (which can).</summary>
    public static bool TryParse(string text, out string server, out string database)
    {
        server = database = null;
        if (!IsDatabase(text)) return false;
        string rest = text.Trim().Substring(Prefix.Length);
        int slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1) return false;
        server = rest.Substring(0, slash);
        database = rest.Substring(slash + 1);
        return true;
    }

    /// <summary>
    /// Scripts the database into <paramref name="side"/>'s cache folder and reads it. The folder is emptied first: a
    /// script left from an earlier run would read as an object the database still has. Anything the export couldn't
    /// write becomes a warning on the set, because an object missing from it reads as "only on the other side".
    /// Blocking: call from a background thread.
    /// </summary>
    public static SchemaSet Load(string text, string connectionString, string side, Action<string> progress, CancellationToken ct = default)
    {
        if (!TryParse(text, out string server, out string database)) throw new ArgumentException($"'{text}' is not {Prefix}server/database.");

        string sideFolder = Path.Combine(CacheRoot, side);
        if (Directory.Exists(sideFolder)) Directory.Delete(sideFolder, recursive: true);
        string folder = Path.Combine(sideFolder, ExportFileNaming.SanitizeFileName(server), ExportFileNaming.SanitizeFileName(database));

        progress?.Invoke($"Scripting {database} on {server}…");
        var result = SchemaExportService.ExportToFolder(connectionString, new[] { database }, folder, folderPerDatabase: false, m => progress?.Invoke($"{server}/{database}: {m}"), ct);
        if (result.FailedDatabases.Count > 0) throw new InvalidOperationException(result.Warnings.FirstOrDefault() ?? $"{database} on {server} couldn't be scripted.");

        var set = ObjectExtractor.Load(folder, progress, ct);
        set.Kind = SourceKind.Database;
        foreach (var (_, type) in result.UnreadFolders) set.Warnings.Add($"{type}: couldn't be read from the server, so none are on this side. Any shown as only on the other side may not be.");
        foreach (string file in result.UnwrittenFiles) set.Warnings.Add($"Not scripted, so missing from this side: {SourceLoader.MakeRelative(folder, file)}");
        set.Warnings.AddRange(result.Warnings);
        return set;
    }
}
