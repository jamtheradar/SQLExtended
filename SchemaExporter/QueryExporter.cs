using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace SQLExtended.SchemaExporter;

/// <summary>
/// Exports one <c>queries</c> entry: runs its SQL against each target, turns the rows into files
/// (<see cref="QueryOutput"/>) and mirrors each folder (<see cref="ReportMirror.Apply"/>, which owns it outright).
///
/// The same rule as everywhere else in this tool: nothing that fails may look like a drop. A folder is written only
/// when every target writing into it read cleanly, returned at least minRows rows, and wouldn't delete more than
/// maxDeletePercent of what's there. Otherwise it is left exactly as it was.
/// </summary>
internal static class QueryExporter
{
    public sealed class Result
    {
        /// <summary>At least one target or folder failed, or was refused by a brake. Its folder was left alone.</summary>
        public bool Failed { get; set; }
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Deleted { get; set; }
    }

    private static readonly Regex OrderBy = new(@"\border\s+by\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static Result Export(QueryConfig query, ExporterConfig config, RunLog log, bool force, CancellationToken ct)
    {
        var result = new Result();
        var options = query.Output;

        if (options.Mode == QueryExportMode.SingleFile && !OrderBy.IsMatch(query.Query))
            log.Warn($"{query.Name}: singleFile without ORDER BY — rows can come back in a different order and show as changes.");

        // Every target is read before anything is written, grouped by the folder it writes into.
        foreach (var folder in query.Targets.GroupBy(t => t.Folder, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            bool clean = true;

            foreach (var target in folder)
            {
                string label = query.Targets.Count == 1 ? query.Name : $"{query.Name} [{target.Name}]";
                try
                {
                    var (columns, rows) = Read(query, target, ct);

                    string problem = QueryOutput.ValidateColumns(options, columns);
                    if (problem != null) { log.Error($"{label}: {problem} Folder left as it was."); clean = false; continue; }

                    if (rows.Count < query.MinRows)
                    {
                        log.Error($"{label}: {rows.Count} row{(rows.Count == 1 ? "" : "s")}, fewer than minRows ({query.MinRows}) — assuming the query broke. Folder left as it was.");
                        clean = false;
                        continue;
                    }

                    var built = QueryOutput.BuildFiles(options, target.Name, columns, rows, message => log.Warn($"{label}: {message}"));
                    foreach (var pair in built)
                    {
                        // Targets sharing a folder: their fileName should include {target}. The first one in keeps the file.
                        if (files.ContainsKey(pair.Key)) { log.Warn($"{label}: {pair.Key} was already written by another target — skipped. Put {{{QueryOutput.TargetToken}}} in fileName."); continue; }
                        files.Add(pair.Key, pair.Value);
                    }
                    log.Progress($"{label}: {rows.Count:N0} row{(rows.Count == 1 ? "" : "s")}.");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException or IOException or ArgumentException or InvalidCastException)
                {
                    ct.ThrowIfCancellationRequested(); // a cancelled command surfaces as a SqlException
                    log.Error($"{label}: query failed, folder left as it was — {Innermost(ex)}");
                    clean = false;
                }
            }

            if (!clean) { result.Failed = true; continue; }

            string folderPath = Path.Combine(config.OutputPath, folder.Key);
            var (existing, deleted) = QueryOutput.CountDeletions(folderPath, files);
            if (QueryOutput.ExceedsDeleteBrake(existing, deleted, query.MaxDeletePercent))
            {
                if (!force)
                {
                    log.Error($"{query.Name}: would delete {deleted} of {existing} files in {folder.Key}, more than maxDeletePercent ({query.MaxDeletePercent}%). " +
                        "Folder left as it was. If that's right, run once with --force.");
                    result.Failed = true;
                    continue;
                }
                log.Warn($"{query.Name}: deleting {deleted} of {existing} files in {folder.Key} (--force).");
            }

            var mirrored = ReportMirror.Apply(folderPath, files);

            // Every file as written: CRLF text and raw bytes stay byte for byte, whatever core.autocrlf says.
            string attributes = Path.Combine(folderPath, ReportMirror.GitAttributesFile);
            if (!File.Exists(attributes)) File.WriteAllText(attributes, "* -text\n");

            result.Added += mirrored.Added;
            result.Updated += mirrored.Updated;
            result.Deleted += mirrored.Deleted;
            log.Info($"{query.Name}: {folder.Key} — {files.Count} file{(files.Count == 1 ? "" : "s")}, {mirrored.Added} added, {mirrored.Updated} changed, {mirrored.Deleted} removed.");
        }

        return result;
    }

    /// <summary>
    /// Runs the query and reads its first result set. Inside a transaction that is always rolled back, so a query that
    /// writes (by mistake, or through a procedure) changes nothing. Still give the login read-only rights.
    /// </summary>
    private static (List<string> Columns, List<object[]> Rows) Read(QueryConfig query, QueryTargetConfig target, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(Environment.ExpandEnvironmentVariables(target.ConnectionString));
        if (!string.IsNullOrWhiteSpace(target.Database)) builder.InitialCatalog = target.Database;

        using var connection = new SqlConnection(builder.ConnectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            using var command = new SqlCommand(query.Query, connection, transaction) { CommandTimeout = query.CommandTimeoutSeconds };
            using var registration = ct.Register(command.Cancel);
            using var reader = command.ExecuteReader();

            // A statement before the SELECT (SET NOCOUNT, a temp table) gives no result set of its own.
            while (reader.FieldCount == 0 && reader.NextResult()) { }
            if (reader.FieldCount == 0) throw new InvalidOperationException("the query returned no result set.");

            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<object[]>();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(values);
            }
            return (columns, rows);
        }
        finally
        {
            try { transaction.Rollback(); } catch (InvalidOperationException) { /* already rolled back by the server */ }
        }
    }

    private static string Innermost(Exception ex)
    {
        while (ex.InnerException != null) ex = ex.InnerException;
        return ex.Message;
    }
}
