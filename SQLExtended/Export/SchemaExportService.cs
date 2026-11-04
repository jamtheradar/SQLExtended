using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;
using SQLExtended.Decryption;
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SQLExtended.Export;

/// <summary>
/// Scripts database schema out of SQL Server, via SMO's <see cref="Scripter"/>. Three shapes:
/// a whole database into one .sql file, a single object into one .sql file, and a whole database (or
/// several) into a folder tree of one file per object.
/// All methods do blocking network and disk I/O and must be called from a background thread.
/// </summary>
internal static class SchemaExportService
{
    /// <summary>
    /// Scripts every user table, view, stored procedure and function in <paramref name="database"/>
    /// and writes the result to <paramref name="filePath"/>. Throws on failure.
    /// </summary>
    public static void ExportDatabase(string connectionString, string database, string filePath)
    {
        if (string.IsNullOrEmpty(connectionString))
            throw new InvalidOperationException("No connection available for the selected database.");

        var serverConnection = new ServerConnection { ConnectionString = connectionString };
        var server = new Server(serverConnection);

        // Fetch IsSystemObject up front so filtering doesn't trigger a round-trip per object, and
        // IsEncrypted with it — an encrypted module has to be pulled out of the batch below before it
        // reaches the scripter.
        server.SetDefaultInitFields(typeof(Table), nameof(Table.IsSystemObject));
        server.SetDefaultInitFields(typeof(View), nameof(View.IsSystemObject), nameof(View.IsEncrypted));
        server.SetDefaultInitFields(typeof(StoredProcedure), nameof(StoredProcedure.IsSystemObject), nameof(StoredProcedure.IsEncrypted));
        server.SetDefaultInitFields(typeof(UserDefinedFunction), nameof(UserDefinedFunction.IsSystemObject), nameof(UserDefinedFunction.IsEncrypted));

        var db = server.Databases[database];
        if (db == null)
            throw new InvalidOperationException($"Database '{database}' was not found on the server.");

        var scripter = new Scripter(server)
        {
            Options =
            {
                ScriptDrops = false,
                WithDependencies = false,
                Indexes = true,
                DriAll = true,
                Triggers = true,
                SchemaQualify = true,
                IncludeHeaders = false,
                NoCollation = true,
                ScriptBatchTerminator = false,
            }
        };

        // Encrypted modules are held back from the batch. SMO does not throw on one, it scripts a comment
        // saying the object is not transferable — and since that comment is the same for every encrypted
        // object, one left in the batch turns into a file that says nothing and reads as if it did.
        var objects = new List<SqlSmoObject>();
        var encrypted = new List<SqlSmoObject>();

        // A table with an encrypted trigger makes SMO throw for the whole batch, so those tables are scripted on their
        // own with triggers off; their plain triggers join the batch and the encrypted ones go with the other
        // encrypted modules to the end of the file.
        var encryptedTriggers = EncryptedTriggersByTable(db);
        var tablesWithoutTriggers = new List<SqlSmoObject>();
        var looseTriggers = new List<SqlSmoObject>();

        foreach (Table t in db.Tables)
        {
            if (t.IsSystemObject) continue;
            if (!encryptedTriggers.TryGetValue((t.Schema, t.Name), out var triggerNames)) { objects.Add(t); continue; }

            tablesWithoutTriggers.Add(t);
            foreach (Trigger tr in t.Triggers) (triggerNames.Contains(tr.Name) ? encrypted : looseTriggers).Add(tr);
        }

        foreach (View v in db.Views) if (!v.IsSystemObject) (IsEncrypted(v) ? encrypted : objects).Add(v);
        foreach (StoredProcedure sp in db.StoredProcedures) if (!sp.IsSystemObject) (IsEncrypted(sp) ? encrypted : objects).Add(sp);
        foreach (UserDefinedFunction fn in db.UserDefinedFunctions) if (!fn.IsSystemObject) (IsEncrypted(fn) ? encrypted : objects).Add(fn);

        var sb = new StringBuilder();
        sb.AppendLine($"-- Schema export for database [{database}]");
        sb.AppendLine($"USE [{database}]");
        sb.AppendLine("GO");
        sb.AppendLine();

        void AppendBatch(Scripter batchScripter, List<SqlSmoObject> batch)
        {
            if (batch.Count == 0) return;
            foreach (string statement in batchScripter.Script(batch.ToArray()))
            {
                sb.AppendLine(statement);
                sb.AppendLine("GO");
                sb.AppendLine();
            }
        }

        AppendBatch(scripter, objects);

        if (tablesWithoutTriggers.Count > 0)
        {
            var noTriggers = new Scripter(server) { Options = new ScriptingOptions(scripter.Options) { Triggers = false } };
            AppendBatch(noTriggers, tablesWithoutTriggers);
            AppendBatch(scripter, looseTriggers);
        }

        AppendEncrypted(sb, connectionString, database, encrypted);

        File.WriteAllText(filePath, sb.ToString());
    }

    // Not the folder export, and built on the schema viewer's SchemaQueryService — out of SchemaExporter's build.
#if !SCHEMA_EXPORTER
    /// <summary>
    /// Scripts a single table/view (CREATE statement plus indexes and foreign keys, via
    /// <see cref="SchemaQueryService"/>) and writes it to <paramref name="filePath"/>. Throws on failure.
    /// </summary>
    public static void ExportObject(string connectionString, string schema, string name, string filePath)
    {
        string qualified = string.IsNullOrEmpty(schema) ? name : $"{schema}.{name}";
        string script = SchemaQueryService.GetSchemaScript(connectionString, qualified);
        if (string.IsNullOrWhiteSpace(script))
            throw new InvalidOperationException($"No schema script could be generated for '{qualified}'.");

        File.WriteAllText(filePath, script);
    }
#endif

    #region Folder export (one file per object)

    /// <summary>
    /// Schemas SQL Server creates itself. Scripting these would put a dozen identical CREATE SCHEMA
    /// files in every export; only user schemas are a real difference between two servers.
    /// </summary>
    private static readonly HashSet<string> SystemSchemas = new(StringComparer.OrdinalIgnoreCase)
    {
        "dbo", "guest", "sys", "INFORMATION_SCHEMA",
        "db_owner", "db_accessadmin", "db_securityadmin", "db_ddladmin", "db_backupoperator",
        "db_datareader", "db_datawriter", "db_denydatareader", "db_denydatawriter",
    };

    /// <summary>
    /// UTF-8 with a BOM. The BOM is deliberate: without it SSMS can open a script holding non-ASCII
    /// identifiers as ANSI and mangle them. Both sides of a compare get it, so it costs no diff noise.
    /// </summary>
    private static readonly Encoding ScriptEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// Exports one file per object into <paramref name="rootFolder"/>, in a subfolder per object type.
    ///
    /// With <paramref name="folderPerDatabase"/> false the type folders sit directly under the root; with
    /// it true a database level is inserted above them. That distinction is the whole point: the export is
    /// meant to be pointed at a folder-compare tool, and a single database must land at the same relative
    /// paths on both sides — a server name or database name in the path would misalign every file.
    ///
    /// Nothing volatile is written: no export timestamps, no row counts, no manifest. A file differs only
    /// when the object's definition differs.
    /// </summary>
    public static SchemaFolderExportResult ExportToFolder(
        string connectionString,
        IReadOnlyList<string> databases,
        string rootFolder,
        bool folderPerDatabase,
        Action<string> progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(connectionString))
            throw new InvalidOperationException("No connection available for the selected server.");
        if (databases == null || databases.Count == 0)
            throw new InvalidOperationException("No databases were selected for export.");
        if (string.IsNullOrEmpty(rootFolder))
            throw new InvalidOperationException("No target folder was chosen.");

        var result = new SchemaFolderExportResult { RootFolder = rootFolder };

        var server = new Server(new ServerConnection { ConnectionString = connectionString });
        PrepareServer(server);

        var options = BuildScriptingOptions();

        // PrefetchObjects off: CollectGroups has already prefetched every scripted type for the whole database
        // (db.PrefetchObjects), and the scripter is handed one object per call. Left on, it re-ran its own prefetch
        // for that single object on every call — columns, indexes, keys, triggers, extended properties, one query
        // each — which measured at 1-2 s per table against a remote server, while procedures (no child collections)
        // went at 100+ a second. A 7,000-table database took almost four hours. The script text is the same either
        // way; anything not prefetched is still lazy-loaded on demand.
        var scripter = new Scripter(server) { Options = options, PrefetchObjects = false };

        // Round trips per type group, for the progress line: the only way to tell from a log whether a slow type
        // is the server being slow or SMO going back to it per object.
        var queries = new QueryTally();
        server.ConnectionContext.StatementExecuted += (_, e) => queries.Add(e.SqlStatement);

        Directory.CreateDirectory(rootFolder);

        // Cancellation is reported on the result rather than thrown, so the caller can still say how much
        // of the tree was written — the files already on disk are real and the user has to know about them.
        try
        {
            foreach (string database in databases)
            {
                ct.ThrowIfCancellationRequested();

                // One unreadable database costs that database, not the whole export — the same trade the
                // monitoring dashboards make per section. Its absence is recorded as a warning so the
                // resulting tree is never quietly short of a database.
                try
                {
                    var db = server.Databases[database];
                    if (db == null)
                    {
                        result.Warnings.Add($"{database}: not found on the server.");
                        result.FailedDatabases.Add(database);
                        continue;
                    }

                    string folder = folderPerDatabase
                        ? Path.Combine(rootFolder, ExportFileNaming.SanitizeFileName(database))
                        : rootFolder;

                    progress?.Invoke($"Exporting {database}…");
                    ExportDatabaseCore(db, folder, scripter, options, result, progress, connectionString, queries, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.Warnings.Add($"{database}: {RootMessage(ex)}");
                    result.FailedDatabases.Add(database);
                }
            }
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }

        return result;
    }

    private static void ExportDatabaseCore(
        Database db, string folder, Scripter scripter, ScriptingOptions options,
        SchemaFolderExportResult result, Action<string> progress, string connectionString, QueryTally queries, CancellationToken ct)
    {
        var groups = CollectGroups(db, options, result, progress, ct);
        if (groups.Count == 0) return;

        var encryptedTriggers = EncryptedTriggersByTable(db, result);
        Scripter tableScripter = null;   // triggers off, for the tables in encryptedTriggers; made on first use

        // Resolved for the whole database up front: decryption opens a dedicated administrator connection,
        // of which the instance permits exactly one, so it is done once for every encrypted object here
        // rather than once per object as the loop reaches it.
        var decrypted = ResolveEncrypted(connectionString, db, groups, encryptedTriggers, result, progress, ct);

        Directory.CreateDirectory(folder);

        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();

            string dir = Path.Combine(folder, group.Folder);
            Directory.CreateDirectory(dir);
            progress?.Invoke($"Scripting {db.Name} — {group.Folder} ({group.Objects.Count:N0})…");
            var groupClock = System.Diagnostics.Stopwatch.StartNew();
            queries.Reset();

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var obj in group.Objects)
            {
                ct.ThrowIfCancellationRequested();

                var (schema, name) = NameOf(obj);
                string fileName = ExportFileNaming.UniqueFileName(used, schema, name);
                string filePath = Path.Combine(dir, fileName);

                try
                {
                    string script;

                    if (IsEncrypted(obj))
                    {
                        // No file at all when the text could not be recovered. Writing SMO's "not
                        // transferable" placeholder instead would be worse than the gap: the placeholder is
                        // byte-identical for every encrypted object, so two servers whose procedures differ
                        // would compare equal — the diff would be wrong in the one direction this export is
                        // trusted for.
                        if (decrypted == null || !decrypted.TryGetValue($"{schema}.{name}", out string definition))
                        {
                            result.Warnings.Add($"{db.Name}/{group.Folder}/{name}: defined WITH ENCRYPTION and could not be decrypted — no file was written.");
                            result.Failed++;
                            result.NotDecryptedModules++;
                            result.UnwrittenFiles.Add(filePath);
                            continue;
                        }

                        script = EncryptedScript(definition);
                    }
                    else if (obj is Table table && encryptedTriggers.TryGetValue((table.Schema, table.Name), out var triggerNames))
                    {
                        tableScripter ??= new Scripter(db.Parent) { Options = BuildScriptingOptions(triggers: false), PrefetchObjects = false };
                        script = ScriptTableWithEncryptedTriggers(table, triggerNames, scripter, tableScripter, decrypted, result, $"{db.Name}/{group.Folder}/{name}");
                    }
                    else
                    {
                        script = ScriptOne(scripter, obj);
                    }

                    if (string.IsNullOrWhiteSpace(script))
                    {
                        result.Warnings.Add($"{db.Name}/{group.Folder}/{name}: SMO produced no script.");
                        result.Failed++;
                        result.UnwrittenFiles.Add(filePath);
                        continue;
                    }

                    // Catches the same placeholder arriving indirectly — an encrypted trigger, say, scripted
                    // inside its table's file, where the object itself never looked encrypted.
                    if (script.IndexOf(EncryptedModuleCrypto.SmoEncryptedMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                        result.Warnings.Add($"{db.Name}/{group.Folder}/{name}: the script contains an encrypted object SMO could not transfer, so part of this file is a placeholder.");

                    File.WriteAllText(filePath, script, ScriptEncoding);
                    result.FilesWritten++;

                    if (result.FilesWritten % 25 == 0)
                        progress?.Invoke($"Exporting {db.Name} — {group.Folder}, {result.FilesWritten:N0} file{(result.FilesWritten == 1 ? "" : "s")} written…");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.Failed++;
                    result.UnwrittenFiles.Add(filePath);
                    result.Warnings.Add($"{db.Name}/{group.Folder}/{name}: {RootMessage(ex)}");
                }
            }

            progress?.Invoke($"Scripted {db.Name} — {group.Folder}: {group.Objects.Count:N0} in {groupClock.Elapsed.TotalSeconds:N1}s, {queries.Count:N0} queries");

            // More queries than objects means SMO went back to the server per object. Name the repeated queries, so the
            // log says which property or child collection the prefetch missed, instead of leaving it to a guess.
            // The floor keeps a small type's fixed setup queries (a table's first script reads server settings) out of it.
            if (queries.Count > Math.Max(group.Objects.Count, 50))
                foreach (var (shape, count) in queries.Top(3))
                    progress?.Invoke($"  {count:N0}× {shape}");

            // An object type with nothing in it leaves no folder behind, so the two sides of a compare
            // don't differ by an empty directory.
            TryDeleteIfEmpty(dir);
        }
    }

    /// <summary>
    /// Prefetches the fields the scripter needs. Without this SMO lazy-loads per property and a database
    /// of a few hundred objects turns into thousands of round trips.
    /// </summary>
    private static void PrepareServer(Server server)
    {
        foreach (var type in new[] { typeof(Table), typeof(View), typeof(StoredProcedure), typeof(UserDefinedFunction), typeof(UserDefinedTableType) })
        {
            try { server.SetDefaultInitFields(type, allFields: true); } catch { /* slower path only */ }
        }
    }

    /// <summary>
    /// Scripting options chosen for comparability, not for deployment:
    /// <c>IncludeHeaders</c> off (its header carries a script date, which would make every file differ on
    /// every export), <c>IncludeDatabaseContext</c> off (no USE per file), and <c>NoCollation</c> left
    /// off — SMO then emits COLLATE only where a column departs from the database default, which is a
    /// real difference worth seeing rather than the noise scripting every collation would produce.
    /// </summary>
    private static ScriptingOptions BuildScriptingOptions(bool triggers = true) => new()
    {
        ScriptDrops = false,
        WithDependencies = false,
        Indexes = true,
        DriAll = true,
        Triggers = triggers,
        ExtendedProperties = true,
        SchemaQualify = true,
        SchemaQualifyForeignKeysReferences = true,
        IncludeHeaders = false,
        IncludeDatabaseContext = false,
        Permissions = false,
        NoCollation = false,
        ScriptBatchTerminator = false,
    };

    /// <summary>
    /// Scripts one object into the text of its file. Statement order is left exactly as SMO produced it:
    /// SMO's child collections (indexes, constraints, triggers) are keyed and enumerated by name, so the
    /// output is already stable across servers, and re-sorting it here would risk splitting a module's
    /// leading SET batches from its body for no gain.
    /// </summary>
    private static string ScriptOne(Scripter scripter, SqlSmoObject obj)
    {
        var sb = new StringBuilder();

        foreach (string statement in scripter.Script(new[] { obj }))
        {
            string text = (statement ?? "").Trim();
            if (text.Length == 0) continue;

            sb.AppendLine(NormalizeLineEndings(text));
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Forces CRLF throughout. A definition stored with bare LFs would otherwise show up as a
    /// whole-file difference against the same definition stored with CRLFs.
    /// </summary>
    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    private sealed class ExportGroup
    {
        public string Folder { get; set; }
        public List<SqlSmoObject> Objects { get; set; }
    }

    /// <summary>
    /// Enumerates the objects to export, grouped into the folder each type goes in. Every group is
    /// collected in its own try/catch: a type the edition or permission set doesn't allow costs one
    /// folder and a warning, not the export.
    /// </summary>
    private static List<ExportGroup> CollectGroups(Database db, ScriptingOptions options, SchemaFolderExportResult result, Action<string> progress, CancellationToken ct)
    {
        var groups = new List<ExportGroup>();

        // Table triggers, indexes, keys, checks and defaults are scripted inside their table's file
        // (DriAll/Indexes/Triggers above), so they are not groups of their own.
        Add("Tables", typeof(Table), () =>
        {
            var list = new List<SqlSmoObject>();
            foreach (Table t in db.Tables) if (!t.IsSystemObject) list.Add(t);
            return list;
        });

        Add("Views", typeof(View), () =>
        {
            var list = new List<SqlSmoObject>();
            foreach (View v in db.Views) if (!v.IsSystemObject) list.Add(v);
            return list;
        });

        Add("Stored Procedures", typeof(StoredProcedure), () =>
        {
            var list = new List<SqlSmoObject>();
            foreach (StoredProcedure sp in db.StoredProcedures) if (!sp.IsSystemObject) list.Add(sp);
            return list;
        });

        Add("Functions", typeof(UserDefinedFunction), () =>
        {
            var list = new List<SqlSmoObject>();
            foreach (UserDefinedFunction fn in db.UserDefinedFunctions) if (!fn.IsSystemObject) list.Add(fn);
            return list;
        });

        // The remaining collections carry no IsSystemObject, so anything SQL Server ships is filtered
        // by its schema instead — the built-in ones all live in sys.
        Add("Table Types", null, () =>
        {
            var list = new List<SqlSmoObject>();
            foreach (UserDefinedTableType tt in db.UserDefinedTableTypes)
                if (!string.Equals(tt.Schema, "sys", StringComparison.OrdinalIgnoreCase)) list.Add(tt);
            return list;
        });

        Add("User-Defined Types", null, () =>
        {
            var list = new List<SqlSmoObject>();
            foreach (UserDefinedDataType dt in db.UserDefinedDataTypes)
                if (!string.Equals(dt.Schema, "sys", StringComparison.OrdinalIgnoreCase)) list.Add(dt);
            return list;
        });

        Add("Sequences", null, () =>
        {
            var list = new List<SqlSmoObject>();
            foreach (Sequence s in db.Sequences) list.Add(s);
            return list;
        });

        Add("Synonyms", null, () =>
        {
            var list = new List<SqlSmoObject>();
            foreach (Synonym s in db.Synonyms) list.Add(s);
            return list;
        });

        Add("Schemas", null, () =>
        {
            var list = new List<SqlSmoObject>();
            foreach (Schema s in db.Schemas) if (!SystemSchemas.Contains(s.Name)) list.Add(s);
            return list;
        });

        Add("Database Triggers", null, () =>
        {
            var list = new List<SqlSmoObject>();
            foreach (DatabaseDdlTrigger tr in db.Triggers) list.Add(tr);
            return list;
        });

        return groups;

        void Add(string folder, Type prefetch, Func<List<SqlSmoObject>> select)
        {
            ct.ThrowIfCancellationRequested();
            // The prefetch is one large read per type and the slowest silent stretch of a big database's export.
            progress?.Invoke($"Reading {db.Name} — {folder}…");
            try
            {
                // Modules are enumerated BEFORE the prefetch, tables after it. Enumerating loads each object with the
                // default init fields (PrepareServer: all of them), and the prefetch then fills in the scripting fields on
                // those same objects. Modules need that order: the procedure (and, on LocalDB, view) prefetch leaves out
                // IsEncrypted, which the scripter reads first, so with the prefetch first it re-initialised every module
                // one at a time. Tables need the opposite: their default fields include row counts and space used, and
                // loading those for 7,185 tables took 6.7 minutes, against 23 seconds when the prefetch creates them.
                bool enumerateFirst = prefetch != null && prefetch != typeof(Table);
                List<SqlSmoObject> objects = enumerateFirst ? select() : null;

                if (prefetch != null && (objects == null || objects.Count > 0))
                {
                    // Costs speed, not correctness, so it doesn't fail the folder. But say so: every object of the type
                    // then goes back to the server on its own.
                    try { db.PrefetchObjects(prefetch, options); }
                    catch (Exception ex) { progress?.Invoke($"Prefetch of {db.Name} — {folder} failed, so each object will be read on its own: {RootMessage(ex)}"); }
                }

                if (prefetch == typeof(View) && objects?.Count > 0) LoadViewColumns(db, folder, progress);

                objects ??= select();
                if (objects.Count == 0) return;

                objects.Sort(CompareByName);
                groups.Add(new ExportGroup { Folder = folder, Objects = objects });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Warnings.Add($"{db.Name}: could not read {folder} — {RootMessage(ex)}");
                result.UnreadFolders.Add((db.Name, folder));
            }
        }
    }

    /// <summary>
    /// Loads every view's columns, with every column property, in one query. The view prefetch loads the columns with
    /// only a few properties on some servers, and scripting then reads one it left out, which re-initialises that column
    /// alone, looked up by name: 5,779 queries for 1,434 views, 8.4 minutes, against one query here. The column default
    /// init fields are widened only for this call and then put back, so table columns load exactly as before.
    /// </summary>
    private static void LoadViewColumns(Database db, string folder, Action<string> progress)
    {
        var server = db.Parent;
        var previous = server.GetDefaultInitFields(typeof(Column));
        try
        {
            server.SetDefaultInitFields(typeof(Column), allFields: true);
            // forScripting: false loads the default init fields just widened; true loads SMO's scripting subset, the
            // same short list the prefetch used.
            db.InitChildCollection(new Microsoft.SqlServer.Management.Sdk.Sfc.Urn("View/Column"), forScripting: false);
        }
        catch (Exception ex)
        {
            progress?.Invoke($"Loading the columns of {db.Name} — {folder} failed, so each will be read on its own: {RootMessage(ex)}");
        }
        finally
        {
            try { server.SetDefaultInitFields(typeof(Column), previous); } catch { /* speed only */ }
        }
    }

    /// <summary>
    /// The statements SMO sends while one type is scripted: a count for the progress line, and the most repeated
    /// statement shapes for when the count says SMO went back to the server per object.
    /// </summary>
    private sealed class QueryTally
    {
        private readonly object gate = new();
        private readonly Dictionary<string, int> shapes = new(StringComparer.Ordinal);
        private int count;

        public int Count { get { lock (gate) return count; } }

        public void Reset() { lock (gate) { count = 0; shapes.Clear(); } }

        public void Add(string sql)
        {
            string shape = Shape(sql);
            lock (gate)
            {
                count++;
                if (shape == null) return;
                if (shapes.TryGetValue(shape, out int n)) shapes[shape] = n + 1;
                else if (shapes.Count < 200) shapes[shape] = 1;
            }
        }

        public List<(string Shape, int Count)> Top(int n)
        {
            lock (gate) return shapes.OrderByDescending(p => p.Value).Take(n).Select(p => (p.Key, p.Value)).ToList();
        }

        // The start (column list) says which property set was missing and the tail (FROM … WHERE) which catalog it came
        // from; the middle is mostly SMO's CASE expressions. "use [db]" is half of every pair and says nothing.
        private static string Shape(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return null;
            string flat = string.Join(" ", sql.Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            if (flat.StartsWith("use ", StringComparison.OrdinalIgnoreCase)) return null;
            return flat.Length <= 320 ? flat : flat.Substring(0, 140) + " … " + flat.Substring(flat.Length - 180);
        }
    }

    private static int CompareByName(SqlSmoObject a, SqlSmoObject b)
    {
        var (schemaA, nameA) = NameOf(a);
        var (schemaB, nameB) = NameOf(b);

        int bySchema = string.Compare(schemaA ?? "", schemaB ?? "", StringComparison.OrdinalIgnoreCase);
        return bySchema != 0 ? bySchema : string.Compare(nameA ?? "", nameB ?? "", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Schema and name of an SMO object. Schemas and DDL triggers have no owning schema, so they get a
    /// bare name — <see cref="ScriptSchemaObjectBase"/> must be tested first since it derives from
    /// <see cref="NamedSmoObject"/>.
    /// </summary>
    private static (string Schema, string Name) NameOf(SqlSmoObject obj) => obj switch
    {
        ScriptSchemaObjectBase schemaScoped => (schemaScoped.Schema, schemaScoped.Name),
        NamedSmoObject named => (null, named.Name),
        _ => (null, obj?.ToString() ?? "unknown"),
    };

    #endregion

    #region Encrypted modules

    /// <summary>
    /// Whether SMO says this object is defined WITH ENCRYPTION. Reading the property can itself fail on an
    /// object the login cannot fully see, and an unanswerable question here should cost the object its
    /// special handling, not the export — a false answer just sends it down the normal scripting path,
    /// where SMO's own placeholder is caught afterwards.
    /// </summary>
    private static bool IsEncrypted(SqlSmoObject obj)
    {
        try
        {
            return obj switch
            {
                StoredProcedure p => p.IsEncrypted,
                View v => v.IsEncrypted,
                UserDefinedFunction f => f.IsEncrypted,
                Trigger t => t.IsEncrypted,
                _ => false,
            };
        }
        catch { return false; }
    }

    /// <summary>
    /// The decryption service's view of an SMO object. The type code decides the shape of the throwaway
    /// definition, and ALTER FUNCTION cannot move a function between its scalar, inline and
    /// multi-statement forms — so the function's own kind has to be carried across, not guessed.
    /// </summary>
    private static EncryptedModule ToModule(SqlSmoObject obj)
    {
        try
        {
            return obj switch
            {
                StoredProcedure p => new EncryptedModule { Schema = p.Schema, Name = p.Name, ObjectType = "P" },
                // A DML trigger lives in its table's schema, and ALTER TRIGGER has to name that table.
                Trigger { Parent: Table t } tr => new EncryptedModule { Schema = t.Schema, Name = tr.Name, ObjectType = "TR", ParentSchema = t.Schema, ParentName = t.Name },
                View v => new EncryptedModule { Schema = v.Schema, Name = v.Name, ObjectType = "V" },
                UserDefinedFunction f => new EncryptedModule
                {
                    Schema = f.Schema,
                    Name = f.Name,
                    ObjectType = f.FunctionType switch
                    {
                        UserDefinedFunctionType.Inline => "IF",
                        UserDefinedFunctionType.Table => "TF",
                        _ => "FN",
                    },
                },
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Decrypts every encrypted object in the collected groups, in one pass. Returns null when there is
    /// nothing encrypted, when decryption is switched off, or when no administrator connection could be
    /// opened — in each of those cases the objects are skipped with a warning rather than written badly.
    /// </summary>
    private static Dictionary<string, string> ResolveEncrypted(
        string connectionString, Database db, List<ExportGroup> groups, Dictionary<(string, string), HashSet<string>> encryptedTriggers,
        SchemaFolderExportResult result, Action<string> progress, CancellationToken ct)
    {
        var modules = new List<EncryptedModule>();
        foreach (var group in groups)
        {
            foreach (var obj in group.Objects)
            {
                if (obj is Table table && encryptedTriggers.TryGetValue((table.Schema, table.Name), out var names))
                {
                    foreach (Trigger trigger in EncryptedTriggersOf(table, names))
                    {
                        var triggerModule = ToModule(trigger);
                        if (triggerModule != null) modules.Add(triggerModule);
                    }
                    continue;
                }

                if (!IsEncrypted(obj)) continue;
                var module = ToModule(obj);
                if (module != null) modules.Add(module);
            }
        }

        if (modules.Count == 0) return null;

        if (!ModuleDecryptionService.Enabled)
        {
            result.Warnings.Add($"{db.Name}: {modules.Count} object{(modules.Count == 1 ? " is" : "s are")} defined WITH ENCRYPTION and {(modules.Count == 1 ? "was" : "were")} skipped. "
                              + "Turn on \"Decrypt encrypted modules\" in SQLExtended settings to include them.");
            return null;
        }

        progress?.Invoke($"Decrypting {modules.Count} encrypted module{(modules.Count == 1 ? "" : "s")} in {db.Name}…");
        var outcome = ModuleDecryptionService.Decrypt(connectionString, db.Name, modules, progress: null, ct: ct);

        if (!string.IsNullOrEmpty(outcome.DacError))
            result.Warnings.Add($"{db.Name}: encrypted objects were skipped — {outcome.DacError}");

        foreach (string warning in outcome.Warnings)
            result.Warnings.Add($"{db.Name}: {warning}");

        return outcome.Definitions;
    }

    /// <summary>
    /// The encrypted DML triggers in a database, by (table schema, table name). SMO cannot script a table that has
    /// one: reading the trigger's TextHeader throws ("The text is encrypted"), and the exception takes the whole
    /// table with it. In the folder export that table's file was never written; in the single-file export the batch
    /// failed and took every table with it. Found with one catalog query rather than by walking each table's
    /// Triggers collection, which would be a round trip per table on exactly the large databases where that hurts.
    /// Empty on failure, which leaves the old behaviour: the table fails with a warning.
    /// </summary>
    private static Dictionary<(string, string), HashSet<string>> EncryptedTriggersByTable(Database db, SchemaFolderExportResult result = null)
    {
        const string sql = @"
SELECT s.name, t.name, tr.name
FROM sys.triggers AS tr
JOIN sys.tables AS t ON t.object_id = tr.parent_id
JOIN sys.schemas AS s ON s.schema_id = t.schema_id
WHERE OBJECTPROPERTY(tr.object_id, 'IsEncrypted') = 1;";

        // Ordinal: a case-sensitive collation can hold both [dbo].[Foo] and [dbo].[foo], and SMO's names are exact.
        var map = new Dictionary<(string, string), HashSet<string>>();
        try
        {
            using DataSet ds = db.ExecuteWithResults(sql);
            foreach (DataRow row in ds.Tables[0].Rows)
            {
                var key = ((string)row[0], (string)row[1]);
                if (!map.TryGetValue(key, out var names)) map[key] = names = new HashSet<string>(StringComparer.Ordinal);
                names.Add((string)row[2]);
            }
        }
        catch (Exception ex)
        {
            result?.Warnings.Add($"{db.Name}: could not list encrypted triggers, so tables that have one will fail — {RootMessage(ex)}");
        }
        return map;
    }

    private static List<Trigger> EncryptedTriggersOf(Table table, HashSet<string> names)
    {
        var list = new List<Trigger>();
        foreach (Trigger trigger in table.Triggers)
            if (names.Contains(trigger.Name)) list.Add(trigger);
        return list;
    }

    /// <summary>
    /// A table that has an encrypted trigger, scripted as SMO would have scripted it whole: the table with triggers
    /// off, then each trigger in the collection's own order. Plain triggers are scripted by SMO; encrypted ones are
    /// the decrypted text plus SMO's ENABLE/DISABLE statement. One that could not be decrypted becomes a comment
    /// naming it: the table is the object this file stands for, and losing all of it over one trigger (what
    /// happened before) is a far bigger gap than the trigger. The comment is fixed text, so it does not churn.
    /// </summary>
    private static string ScriptTableWithEncryptedTriggers(
        Table table, HashSet<string> encryptedNames, Scripter scripter, Scripter tableScripter,
        Dictionary<string, string> decrypted, SchemaFolderExportResult result, string where)
    {
        var sb = new StringBuilder(ScriptOne(tableScripter, table));

        foreach (Trigger trigger in table.Triggers)
        {
            if (!encryptedNames.Contains(trigger.Name))
            {
                sb.Append(ScriptOne(scripter, trigger));
                continue;
            }

            if (decrypted != null && decrypted.TryGetValue($"{table.Schema}.{trigger.Name}", out string definition))
            {
                sb.Append(EncryptedScript(definition));
                sb.AppendLine(TriggerStateStatement(table, trigger));
                sb.AppendLine("GO");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine($"-- Trigger {EncryptedModuleCrypto.Quote(table.Schema, trigger.Name)} is defined WITH ENCRYPTION and could not be decrypted, so it is not in this script.");
                sb.AppendLine();
                result?.Warnings.Add($"{where}: trigger {trigger.Name} is defined WITH ENCRYPTION and could not be decrypted — the table was scripted without it.");
                if (result != null) result.NotDecryptedTriggers++;
            }
        }

        return sb.ToString();
    }

    /// <summary>The statement SMO writes after every trigger it scripts. Disabled is a real difference worth keeping.</summary>
    private static string TriggerStateStatement(Table table, Trigger trigger)
    {
        bool enabled = true;
        try { enabled = trigger.IsEnabled; } catch { /* assume enabled, the engine's default */ }
        return $"ALTER TABLE {EncryptedModuleCrypto.Quote(table.Schema, table.Name)} {(enabled ? "ENABLE" : "DISABLE")} TRIGGER [{trigger.Name.Replace("]", "]]")}]";
    }

    /// <summary>
    /// The file text of a decrypted module. The leading ALTER is rewritten to CREATE because what comes
    /// back is whatever text was last submitted — SMO normalises this for every object it scripts itself,
    /// and an export where one file says ALTER and its counterpart says CREATE is a difference the compare
    /// would report and nobody would want.
    /// </summary>
    private static string EncryptedScript(string definition)
    {
        var sb = new StringBuilder();
        sb.AppendLine(NormalizeLineEndings(EncryptedModuleCrypto.NormalizeToCreate(definition).Trim()));
        sb.AppendLine("GO");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Appends the encrypted modules held back from the single-file export, decrypting them if that is
    /// switched on. Whatever could not be recovered is named in a comment rather than passed over silently:
    /// this file is read as the database's schema, and an object missing from it without explanation is
    /// indistinguishable from an object that does not exist.
    /// </summary>
    private static void AppendEncrypted(StringBuilder sb, string connectionString, string database, List<SqlSmoObject> encrypted)
    {
        if (encrypted.Count == 0) return;

        var modules = new List<EncryptedModule>();
        foreach (var obj in encrypted)
        {
            var module = ToModule(obj);
            if (module != null) modules.Add(module);
        }

        ModuleDecryptionResult outcome = null;
        if (ModuleDecryptionService.Enabled && modules.Count > 0)
        {
            try { outcome = ModuleDecryptionService.Decrypt(connectionString, database, modules); }
            catch (Exception ex) { sb.AppendLine($"-- Encrypted objects could not be decrypted: {RootMessage(ex)}"); sb.AppendLine(); }
        }

        if (outcome != null && !string.IsNullOrEmpty(outcome.DacError))
        {
            sb.AppendLine($"-- Encrypted objects could not be decrypted: {outcome.DacError}");
            sb.AppendLine();
        }

        foreach (var module in modules)
        {
            if (outcome != null && outcome.Definitions.TryGetValue(module.Key, out string definition))
            {
                sb.AppendLine($"-- [{module.Schema}].[{module.Name}] is defined WITH ENCRYPTION; the definition below was decrypted.");
                sb.AppendLine(EncryptedModuleCrypto.NormalizeToCreate(definition).Trim());
            }
            else
            {
                sb.AppendLine($"-- [{module.Schema}].[{module.Name}] is defined WITH ENCRYPTION and could not be scripted.");
            }

            sb.AppendLine("GO");
            sb.AppendLine();
        }
    }

    #endregion

    #region Re-export cleanup

    /// <summary>
    /// Counts .sql files a previous export left under <paramref name="rootFolder"/>. Only folders named
    /// in <see cref="ExportFileNaming.TypeFolders"/> are looked at, at one or two levels down (the
    /// single-database and per-database layouts), so nothing else in the folder is ever counted.
    /// </summary>
    public static int CountExistingScripts(string rootFolder)
        => OwnedScriptFolders(rootFolder).Sum(dir => ScriptFilesIn(dir).Count);

    /// <summary>
    /// Deletes the .sql files of a previous export, then any folder that emptied as a result. Returns how
    /// many files went. Only ever touches .sql files inside the export's own type folders.
    ///
    /// Re-exporting without this would leave the scripts of dropped objects sitting in the tree, and a
    /// folder compare would report them as objects present on both servers — the diff would be wrong in
    /// the one direction the tool is trusted for.
    /// </summary>
    public static int DeleteExistingScripts(string rootFolder)
    {
        int deleted = 0;

        foreach (string dir in OwnedScriptFolders(rootFolder))
        {
            foreach (string file in ScriptFilesIn(dir))
            {
                try { File.Delete(file); deleted++; } catch { /* left in place; the re-export overwrites it */ }
            }
            TryDeleteIfEmpty(dir);
        }

        if (Directory.Exists(rootFolder))
        {
            foreach (string dir in SafeGetDirectories(rootFolder))
                TryDeleteIfEmpty(dir);
        }

        return deleted;
    }

    private static List<string> OwnedScriptFolders(string rootFolder)
    {
        var folders = new List<string>();
        if (string.IsNullOrEmpty(rootFolder) || !Directory.Exists(rootFolder)) return folders;

        foreach (string dir in SafeGetDirectories(rootFolder))
        {
            if (ExportFileNaming.IsTypeFolder(Path.GetFileName(dir)))
            {
                folders.Add(dir);
                continue;
            }

            // Otherwise it may be a database folder from a per-database export.
            foreach (string sub in SafeGetDirectories(dir))
            {
                if (ExportFileNaming.IsTypeFolder(Path.GetFileName(sub)))
                    folders.Add(sub);
            }
        }

        return folders;
    }

    /// <summary>
    /// The .sql files in a folder. Filters on the extension itself rather than trusting the "*.sql"
    /// wildcard — Windows matches three-character extension patterns against longer extensions too, so
    /// the pattern alone would also pick up a .sqlproj.
    /// </summary>
    private static List<string> ScriptFilesIn(string dir)
    {
        try
        {
            return Directory.GetFiles(dir, "*.sql")
                .Where(f => string.Equals(Path.GetExtension(f), ".sql", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch { return new List<string>(); }
    }

    private static string[] SafeGetDirectories(string dir)
    {
        try { return Directory.GetDirectories(dir); } catch { return Array.Empty<string>(); }
    }

    private static void TryDeleteIfEmpty(string dir)
    {
        try
        {
            if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0)
                Directory.Delete(dir);
        }
        catch { /* leaving an empty folder behind is harmless */ }
    }

    #endregion

    /// <summary>
    /// Innermost exception message. SMO wraps almost everything in a generic
    /// "An exception occurred while executing a Transact-SQL statement", which on its own says nothing
    /// about which permission or object was the problem.
    /// </summary>
    private static string RootMessage(Exception ex)
    {
        while (ex.InnerException != null) ex = ex.InnerException;
        return ex.Message;
    }
}

/// <summary>Outcome of a folder export: what was written, what didn't make it, and why.</summary>
internal sealed class SchemaFolderExportResult
{
    public string RootFolder { get; set; }
    public int FilesWritten { get; set; }
    public int Failed { get; set; }

    /// <summary>True when the user stopped the export part-way. The files already written are still there.</summary>
    public bool Cancelled { get; set; }

    public List<string> Warnings { get; } = new();

    // What did not make it, in a form a caller can act on rather than only read. The window just shows the
    // warnings; SchemaExporter mirrors the tree into a git history, where a database, a type folder or a file
    // missing from this run would otherwise be committed as dropped. Each of these says "keep what was there".

    /// <summary>Databases that were not exported at all (not found, or failed outright). Raw names, not folder names.</summary>
    public List<string> FailedDatabases { get; } = new();

    /// <summary>Type folders whose objects could not be enumerated, as (raw database name, type folder).</summary>
    public List<(string Database, string Folder)> UnreadFolders { get; } = new();

    /// <summary>Full paths of files an object should have been written to and was not.</summary>
    public List<string> UnwrittenFiles { get; } = new();

    // Encrypted objects whose text was not recovered: modules (no file, and also in Failed and UnwrittenFiles) and DML
    // triggers (a comment in their table's file). SchemaExporter's change detection needs to tell these apart from other
    // failures: with decryption off they are the same every night, so they don't make an export "unclean".

    public int NotDecryptedModules { get; set; }
    public int NotDecryptedTriggers { get; set; }
}
