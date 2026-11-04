using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

namespace SQLExtended.SchemaExporter;

/// <summary>What the last clean export of one database saw.</summary>
internal sealed class DatabaseState
{
    public string Fingerprint { get; set; }

    /// <summary>When the database was last exported in full (UTC), which is what the periodic full scan counts from.</summary>
    public DateTime LastFullScanUtc { get; set; }
}

/// <summary>One server's state file.</summary>
internal sealed class ServerState
{
    /// <summary>The server folder in the history this state describes. State recorded against another folder is ignored.</summary>
    public string OutputFolder { get; set; }

    public Dictionary<string, DatabaseState> Databases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Decides whether a database can be skipped because its schema fingerprint hasn't changed since its last clean export.
/// Every rule leans towards exporting: a skip that should have been an export leaves a change out of the history, while
/// an export that could have been a skip only costs time. Pure logic and file I/O, so the test project can link it.
/// </summary>
internal static class ChangeDetection
{
    /// <summary>
    /// The state file, or an empty state when it is missing, unreadable, or was written for a different output folder.
    /// In each of those cases nothing can be vouched for, so every database is exported.
    /// </summary>
    public static ServerState Load(string path, string outputFolder)
    {
        try
        {
            if (File.Exists(path))
            {
                var state = JsonConvert.DeserializeObject<ServerState>(File.ReadAllText(path));
                if (state != null && string.Equals(state.OutputFolder, outputFolder, StringComparison.OrdinalIgnoreCase))
                {
                    // Rebuilt case-insensitively: database names are, and the deserialiser makes a default dictionary.
                    state.Databases = new Dictionary<string, DatabaseState>(state.Databases ?? new(), StringComparer.OrdinalIgnoreCase);
                    return state;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { /* start over */ }

        return new ServerState { OutputFolder = outputFolder };
    }

    /// <summary>Written to a temporary file and moved into place, so a crash mid-write can't leave half a file.</summary>
    public static void Save(string path, ServerState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(state, Newtonsoft.Json.Formatting.Indented));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }

    /// <summary>
    /// Why the database can be skipped, for the log, or null when it must be exported. It is skipped only when all of
    /// these hold: there is a fingerprint from tonight and one from a clean export, they match, the database's folder
    /// is in the history (a re-cloned or moved repository may not have it), and the last full export is recent enough.
    /// </summary>
    public static string SkipReason(DatabaseState previous, string fingerprint, DateTime nowUtc, int fullScanEveryDays, bool folderExists)
    {
        if (previous == null || string.IsNullOrEmpty(fingerprint) || string.IsNullOrEmpty(previous.Fingerprint)) return null;
        if (!string.Equals(previous.Fingerprint, fingerprint, StringComparison.Ordinal)) return null;
        if (!folderExists) return null;

        // Also covers a clock that went backwards: an "export" in the future is not one to trust.
        var age = nowUtc - previous.LastFullScanUtc;
        if (age < TimeSpan.Zero) return null;

        if (fullScanEveryDays > 0)
        {
            if (age >= TimeSpan.FromDays(fullScanEveryDays)) return null;
            var due = previous.LastFullScanUtc.AddDays(fullScanEveryDays).ToLocalTime();
            return $"unchanged since {previous.LastFullScanUtc.ToLocalTime():yyyy-MM-dd HH:mm} (next full scan after {due:yyyy-MM-dd HH:mm})";
        }

        return $"unchanged since {previous.LastFullScanUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    /// <summary>
    /// Whether an export may have its fingerprint recorded. A fingerprint recorded after a partial export would freeze the
    /// gap: the database would be skipped, with the gap still in the history, until its schema next changed. So any
    /// failure disqualifies it, with one exception: encrypted modules that weren't decrypted while decryption is off.
    /// Those are the same every night, so they don't make the export partial. With decryption on, a module or trigger
    /// that wasn't decrypted is a DAC that failed tonight and may work tomorrow, so it disqualifies the export.
    /// </summary>
    public static bool IsClean(bool databaseFailed, int unreadFolders, int unwrittenFiles, int notDecryptedModules, int notDecryptedTriggers, bool decrypting)
    {
        if (databaseFailed || unreadFolders > 0) return false;
        if (unwrittenFiles != notDecryptedModules) return false;   // something other than an encrypted module was lost
        if (decrypting && notDecryptedModules + notDecryptedTriggers > 0) return false;
        return true;
    }
}
