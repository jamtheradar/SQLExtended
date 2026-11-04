using Microsoft.Data.SqlClient;
using SQLExtended.Export;
using SQLExtended.Settings;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace SQLExtended.SchemaExporter;

/// <summary>
/// Exports one server into a staging folder with the extension's own <see cref="SchemaExportService.ExportToFolder"/>
/// (server layout: a folder per database), then mirrors it into the server's folder in the history.
/// </summary>
internal static class ServerExporter
{
    private sealed class DatabaseInfo(string name, bool accessible)
    {
        public string Name { get; } = name;
        public bool Accessible { get; } = accessible;
    }

    public static MirrorResult Export(ServerConfig server, ExporterConfig config, RunLog log, bool fullScan, CancellationToken ct)
    {
        string connectionString = Environment.ExpandEnvironmentVariables(server.ConnectionString);

        // Throws when the server can't be reached, which is the point: nothing is mirrored, so nothing is deleted.
        var onServer = ListDatabases(connectionString);

        var include = new HashSet<string>(server.Databases, StringComparer.OrdinalIgnoreCase);
        var exclude = new HashSet<string>(server.ExcludeDatabases, StringComparer.OrdinalIgnoreCase);
        bool allDatabases = include.Count == 0;

        var selected = new List<string>();
        foreach (var db in onServer)
        {
            if (!allDatabases && !include.Contains(db.Name)) continue;
            if (exclude.Contains(db.Name)) continue;

            // Offline, restoring, a non-readable AG secondary, or no user mapping for this login. Its folder is
            // kept as it was — it still exists, it just can't be read tonight.
            if (!db.Accessible) { log.Warn($"{server.Name}: {db.Name} is not accessible (offline, restoring, or no access) — its previous scripts are kept."); continue; }

            selected.Add(db.Name);
        }

        foreach (string missing in include.Where(n => onServer.All(d => !string.Equals(d.Name, n, StringComparison.OrdinalIgnoreCase))))
            log.Warn($"{server.Name}: {missing} is listed in the config but not on the server — its previous scripts are kept.");

        string staging = Path.Combine(Path.GetTempPath(), "SQLExtended.SchemaExporter", Guid.NewGuid().ToString("N"));
        try
        {
            bool decrypting = server.DecryptEncryptedModules ?? config.DecryptEncryptedModules;
            SQLExtendedSettings.Current.DecryptEncryptedModules = decrypting;

            string target = Path.Combine(config.OutputPath, server.Folder);
            var total = new MirrorResult();
            var serverClock = Stopwatch.StartNew();
            int skipped = 0;

            string statePath = Path.Combine(config.StateDirectory, server.Folder + ".json");
            var state = config.SkipUnchangedDatabases ? ChangeDetection.Load(statePath, target) : null;

            // One database at a time, each mirrored as soon as it is done: the repository fills as the run goes
            // instead of all at once at the end of the server (which on a big server looked like nothing happening
            // at all), and the log says which database the time went on.
            for (int i = 0; i < selected.Count; i++)
            {
                string db = selected[i];
                string label = $"{server.Name}/{db} ({i + 1}/{selected.Count})";
                var clock = Stopwatch.StartNew();

                // Taken BEFORE the export: a change made while the export runs then still differs from it tomorrow.
                string fingerprint = null;
                if (state != null)
                {
                    try { fingerprint = SchemaFingerprint.Compute(connectionString, db, decrypting); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        log.Warn($"{label}: could not read the schema fingerprint, so it is exported in full — {ex.Message}");
                    }

                    if (!fullScan)
                    {
                        state.Databases.TryGetValue(db, out var previous);
                        bool folderExists = Directory.Exists(Path.Combine(target, ExportFileNaming.SanitizeFileName(db)));
                        string skip = ChangeDetection.SkipReason(previous, fingerprint, DateTime.UtcNow, config.FullScanEveryDays, folderExists);
                        if (skip != null)
                        {
                            log.Info($"{label}: {skip} — skipped.");
                            skipped++;
                            continue;
                        }
                    }
                }

                log.Info($"{label}: exporting…");

                var result = SchemaExportService.ExportToFolder(connectionString, new[] { db }, staging, folderPerDatabase: true, progress: m => log.Progress($"{label}: {m}"), ct: ct);

                // A part-finished database is no basis for deciding what was dropped. Throwing leaves its folder alone;
                // databases already mirrored stay mirrored, and are committed with the rest of the server's changes.
                if (result.Cancelled) throw new OperationCanceledException();

                foreach (string warning in result.Warnings) log.Warn($"{server.Name}: {warning}");

                var scope = new MirrorScope();
                if (!result.FailedDatabases.Contains(db, StringComparer.OrdinalIgnoreCase))
                    scope.ExportedDatabases.Add(ExportFileNaming.SanitizeFileName(db));
                foreach (var (d, folder) in result.UnreadFolders)
                    scope.UnreadFolders.Add(Path.Combine(ExportFileNaming.SanitizeFileName(d), folder));
                foreach (string path in result.UnwrittenFiles)
                    scope.UnwrittenFiles.Add(RelativeTo(staging, path));

                var step = Stopwatch.StartNew();
                var mirror = SchemaMirror.Apply(staging, target, scope);
                Accumulate(total, mirror);
                log.Progress($"{label}: compared with the repository in {Elapsed(step)}");

                log.Info($"{label}: {result.FilesWritten:N0} scripts in {Elapsed(clock)}" + (result.Failed > 0 ? $", {result.Failed} failed" : "")
                         + $" — {mirror.Added} added, {mirror.Updated} changed, {mirror.Deleted} removed" + (mirror.Kept > 0 ? $", {mirror.Kept} kept" : ""));

                // Recorded only after a clean export, and saved per database so a run that dies part-way keeps what it
                // proved. An unclean one forgets the database, so tomorrow exports it again rather than skipping a gap.
                if (state != null)
                {
                    bool clean = fingerprint != null && ChangeDetection.IsClean(
                        result.FailedDatabases.Contains(db, StringComparer.OrdinalIgnoreCase), result.UnreadFolders.Count, result.UnwrittenFiles.Count,
                        result.NotDecryptedModules, result.NotDecryptedTriggers, decrypting);

                    if (clean) state.Databases[db] = new DatabaseState { Fingerprint = fingerprint, LastFullScanUtc = DateTime.UtcNow };
                    else state.Databases.Remove(db);

                    try { ChangeDetection.Save(statePath, state); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        log.Warn($"{server.Name}: could not save the schema fingerprints to {statePath}, so tomorrow exports in full — {ex.Message}");
                    }
                }

                // Staging only ever holds the database in hand, so a big server never needs its whole tree twice on disk.
                step.Restart();
                TryDelete(staging, log);
                log.Progress($"{label}: staging folder removed in {Elapsed(step)}");
            }

            // Only when the config asks for every database does a missing one mean "dropped". With an explicit list
            // the tool can't tell a dropped database from a typo, so it removes nothing.
            if (allDatabases)
            {
                var stillThere = new HashSet<string>(onServer.Select(d => ExportFileNaming.SanitizeFileName(d.Name)), StringComparer.OrdinalIgnoreCase);
                var dropped = new MirrorScope();
                foreach (string folder in SchemaMirror.ExistingDatabaseFolders(target).Where(f => !stillThere.Contains(f)))
                {
                    log.Info($"{server.Name}: {folder} is no longer on the server — removing its scripts.");
                    dropped.DroppedDatabases.Add(folder);
                }
                if (dropped.DroppedDatabases.Count > 0) Accumulate(total, SchemaMirror.Apply(staging, target, dropped));
            }

            log.Info($"{server.Name}: {total.Added} added, {total.Updated} changed, {total.Deleted} removed" + (total.Kept > 0 ? $", {total.Kept} kept from the previous run" : "")
                     + (skipped > 0 ? $", {skipped} unchanged database{(skipped == 1 ? "" : "s")} skipped" : "") + $" — {Elapsed(serverClock)}");
            return total;
        }
        finally
        {
            TryDelete(staging, log);
        }
    }

    private static void TryDelete(string staging, RunLog log)
    {
        try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        catch (Exception ex) { log.Warn($"Could not remove the staging folder {staging}: {ex.Message}"); }
    }

    private static void Accumulate(MirrorResult total, MirrorResult part)
    {
        total.Added += part.Added;
        total.Updated += part.Updated;
        total.Deleted += part.Deleted;
        total.Kept += part.Kept;
    }

    private static string Elapsed(Stopwatch clock) => clock.Elapsed.TotalMinutes >= 1 ? $"{(int)clock.Elapsed.TotalMinutes}m {clock.Elapsed.Seconds}s" : $"{clock.Elapsed.TotalSeconds:N1}s";

    /// <summary>
    /// Every database on the server, with whether it can be scripted. Not only the accessible ones: an offline database
    /// has to be told apart from a dropped one, or a night it's offline would delete its history.
    /// System databases, snapshots and the distribution database are left out — none is anyone's schema.
    /// </summary>
    private static List<DatabaseInfo> ListDatabases(string connectionString)
    {
        const string sql = @"
SELECT d.name,
       CAST(CASE WHEN d.state = 0 AND HAS_DBACCESS(d.name) = 1 THEN 1 ELSE 0 END AS bit) AS accessible
FROM sys.databases AS d
WHERE d.database_id > 4 AND d.source_database_id IS NULL AND d.is_distributor = 0
ORDER BY d.name;";

        var list = new List<DatabaseInfo>();
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        using var reader = command.ExecuteReader();
        while (reader.Read())
            list.Add(new DatabaseInfo(reader.GetString(0), reader.GetBoolean(1)));
        return list;
    }

    private static string RelativeTo(string root, string path)
    {
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path.Substring(prefix.Length) : path;
    }
}
