using Microsoft.Data.SqlClient;
using SQLExtended.Cache;
using SQLExtended.Cache.Models;
using SQLExtended.Decryption;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SQLExtended;

/// <summary>
/// Queries SQL Server metadata to build a readable schema script
/// showing the CREATE TABLE definition, primary keys, indexes, and foreign keys.
///
/// Uses the shared SchemaCache when available; falls back to direct SQL queries
/// when the cache is not ready (e.g., first connection before cache loads).
/// </summary>
internal static class SchemaQueryService
{
    // Script cache: "server|database|schema.table" → script (kept for fallback path)
    private static readonly ConcurrentDictionary<string, string> _scriptCache = new();

    /// <summary>
    /// Returns a formatted schema script for the given object name.
    /// Reads from the shared cache when available, otherwise queries SQL Server directly.
    /// </summary>
    public static string GetSchemaScript(string connectionString, string objectName)
    {
        return GetSchemaScript(connectionString, objectName, connectionKey: null);
    }

    /// <summary>
    /// Returns a formatted schema script for the given object name.
    /// When <paramref name="connectionKey"/> is supplied it is used for cache lookups
    /// instead of re-deriving from the connection string, avoiding mismatches
    /// between aliases and actual server names.
    /// </summary>
    public static string GetSchemaScript(string connectionString, string objectName, string connectionKey)
    {
        var (database, schema, name) = EditorHelper.ParseObjectName(objectName);
        string effectiveConnStr = ConnectionHelper.GetConnectionStringForDatabase(connectionString, database);

        string cacheKey = $"{connectionKey ?? effectiveConnStr}|{schema ?? "?"}|{name}";
        if (_scriptCache.TryGetValue(cacheKey, out string cached))
            return cached;

        // Try building from the shared schema cache first
        string script = TryBuildFromCache(effectiveConnStr, database, schema, name, connectionKey);
        if (script == null)
        {
            // Fall back to direct SQL queries
            script = BuildSchemaScriptDirect(effectiveConnStr, schema, name);
        }

        if (script != null)
        {
            _scriptCache[cacheKey] = script;

            // Trigger a background cache load for this database if not already loaded
            EnsureDatabaseCached(connectionString, database);
        }

        return script;
    }

    /// <summary>
    /// Clear all caches (script cache + quick info cache).
    /// </summary>
    public static void ClearCache()
    {
        _scriptCache.Clear();
        _quickInfoCache.Clear();
    }

    // Cache for quick info tooltips
    private static readonly ConcurrentDictionary<string, QuickInfoResult> _quickInfoCache = new();

    /// <summary>
    /// Lightweight check: does this object exist? Returns type and row count for tooltip.
    /// Uses the shared cache when available; falls back to a direct query.
    /// </summary>
    public static QuickInfoResult GetQuickInfo(string connectionString, string database, string schema, string objectName)
    {
        string effectiveConnStr = ConnectionHelper.GetConnectionStringForDatabase(connectionString, database);
        string cacheKey = $"{effectiveConnStr}|{schema ?? "?"}|{objectName}";

        if (_quickInfoCache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Try the shared schema cache first
        var result = TryGetQuickInfoFromCache(effectiveConnStr, database, schema, objectName);

        if (result == null)
        {
            // Fall back to direct SQL query
            result = GetQuickInfoDirect(effectiveConnStr, database, schema, objectName);

            // Trigger background cache load
            EnsureDatabaseCached(connectionString, database);
        }

        if (result != null)
            _quickInfoCache[cacheKey] = result;

        return result;
    }

    /// <summary>
    /// Result of a lightweight object existence check for hover tooltips.
    /// </summary>
    internal class QuickInfoResult
    {
        public string Schema { get; set; }
        public string ObjectName { get; set; }
        public string Database { get; set; }
        public string ObjectType { get; set; }
        public long RowCount { get; set; }
        public string ConnectionString { get; set; }

        public string ObjectTypeDisplay => ObjectType switch
        {
            "U" => "Table",
            "V" => "View",
            "P" => "Stored Procedure",
            "FN" => "Scalar Function",
            "IF" => "Inline Table Function",
            "TF" => "Table Function",
            _ => "Object"
        };

        public string QualifiedName => $"[{Schema}].[{ObjectName}]";
    }

    #region Cache-backed methods

    /// <summary>
    /// Tries to build a schema script entirely from the shared cache.
    /// Returns null if the cache doesn't have the data.
    /// </summary>
    private static string TryBuildFromCache(string connectionString, string database, string schema, string name, string connectionKey = null)
    {
        var cache = SchemaCache.Instance;
        string connKey = connectionKey ?? cache.GetConnectionKey(connectionString);

        // Use the database from the connection string if not specified
        if (string.IsNullOrEmpty(database))
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                database = builder.InitialCatalog;
            }
            catch (Exception ex)
            {
                // Returning null here is read by the caller as "not in the cache", which sends it down the
                // direct-query path - so a failure of this one is invisible even when it is the real cause.
                Diagnostics.SQLExtendedLog.Warning("SchemaQuery", "Building a schema script from the cache failed", ex);
                return null;
            }
        }

        var state = cache.GetState(connKey, database);
        if (state != CacheState.Ready && state != CacheState.Stale)
            return null;

        // Find the object
        var obj = cache.FindObject(connKey, database, schema, name);
        if (obj == null) return null;

        schema = obj.SchemaName;
        var sb = new StringBuilder();

        if (obj.ObjectType == "V")
        {
            // A cached view with no definition is an encrypted one the cache did not decrypt (or was
            // loaded before decryption was turned on). Falling back to the direct path gives it a chance to
            // be decrypted on demand, which printing a message here would not.
            if (string.IsNullOrEmpty(obj.Definition)) return null;

            sb.AppendLine($"-- View: [{schema}].[{name}]");
            sb.AppendLine(obj.Definition);
        }
        else if (obj.ObjectType == "U")
        {
            // Build CREATE TABLE from cached columns
            var columns = cache.GetColumns(connKey, database, schema, name);
            if (columns.Count == 0) return null; // Cache incomplete, fall back

            var indexes = cache.GetIndexes(connKey, database, schema, name);
            var foreignKeys = cache.GetForeignKeys(connKey, database, schema, name);

            sb.AppendLine(BuildCreateTableFromCache(schema, name, columns, indexes));
            sb.AppendLine();
            sb.AppendLine(BuildIndexesFromCache(schema, name, indexes));
            sb.AppendLine(BuildForeignKeysFromCache(schema, name, foreignKeys));
        }
        else
        {
            // For procs/functions, show definition if available
            if (!string.IsNullOrEmpty(obj.Definition))
            {
                sb.AppendLine($"-- {obj.ObjectTypeDisplay}: [{schema}].[{name}]");
                sb.AppendLine(obj.Definition);
            }
            else
            {
                return null; // No definition in cache, fall back
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildCreateTableFromCache(string schema, string tableName,
        IReadOnlyList<CachedColumn> columns, IReadOnlyList<CachedIndex> indexes)
    {
        var pkIndex = indexes.FirstOrDefault(i => i.IsPrimaryKey);
        var pkColumns = pkIndex?.KeyColumnNames().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE [{schema}].[{tableName}]");
        sb.AppendLine("(");

        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            if (col.IsComputed)
            {
                sb.Append($"    [{col.ColumnName}] AS {col.ComputedDefinition}");
            }
            else
            {
                sb.Append($"    [{col.ColumnName}] {col.DataType}");

                if (col.IsIdentity)
                    sb.Append(" IDENTITY(1,1)");

                sb.Append(col.IsNullable ? " NULL" : " NOT NULL");

                if (!string.IsNullOrEmpty(col.DefaultDefinition))
                    sb.Append($" DEFAULT {col.DefaultDefinition}");
            }

            if (i < columns.Count - 1 || pkIndex != null)
                sb.Append(",");

            sb.AppendLine();
        }

        if (pkIndex != null)
        {
            sb.AppendLine($"    CONSTRAINT [{pkIndex.IndexName}] PRIMARY KEY {PkClustering(pkIndex.IndexType)} ({pkIndex.KeyColumns})");
        }

        sb.AppendLine(");");

        // Row count from object
        return sb.ToString();
    }

    /// <summary>
    /// Maps a primary key index's <c>type_desc</c> to the clustering keyword used in
    /// CREATE TABLE. A PK is only CLUSTERED when SQL Server reports it so — many tables
    /// use a NONCLUSTERED PK with a different clustering key, so we must not assume.
    /// Unknown/missing values fall back to CLUSTERED (SQL Server's own default).
    /// </summary>
    private static string PkClustering(string indexType) =>
        string.Equals(indexType, "NONCLUSTERED", StringComparison.OrdinalIgnoreCase)
            ? "NONCLUSTERED"
            : "CLUSTERED";

    private static string BuildIndexesFromCache(string schema, string tableName, IReadOnlyList<CachedIndex> indexes)
    {
        var nonPkIndexes = indexes.Where(i => !i.IsPrimaryKey).ToList();
        if (nonPkIndexes.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("-- ============================================");
        sb.AppendLine("-- INDEXES");
        sb.AppendLine("-- ============================================");

        foreach (var ix in nonPkIndexes)
        {
            string unique = ix.IsUnique ? "UNIQUE " : "";
            sb.Append($"CREATE {unique}{ix.IndexType} INDEX [{ix.IndexName}]");
            sb.AppendLine($" ON [{schema}].[{tableName}] ({ix.KeyColumns})");

            if (!string.IsNullOrEmpty(ix.IncludedColumns))
                sb.AppendLine($"    INCLUDE ({ix.IncludedColumns})");
            if (!string.IsNullOrEmpty(ix.FilterDefinition))
                sb.AppendLine($"    WHERE {ix.FilterDefinition}");

            sb.AppendLine("GO");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string BuildForeignKeysFromCache(string schema, string tableName, IReadOnlyList<CachedForeignKey> foreignKeys)
    {
        if (foreignKeys.Count == 0) return "";

        var sb = new StringBuilder();
        sb.AppendLine("-- ============================================");
        sb.AppendLine("-- FOREIGN KEYS");
        sb.AppendLine("-- ============================================");

        foreach (var fk in foreignKeys)
        {
            sb.AppendLine($"ALTER TABLE [{schema}].[{tableName}]");
            sb.Append($"    ADD CONSTRAINT [{fk.ForeignKeyName}] FOREIGN KEY ({fk.Columns})");
            sb.AppendLine($" REFERENCES [{fk.ReferencedSchema}].[{fk.ReferencedTable}] ({fk.ReferencedColumns})");

            if (fk.DeleteAction != "NO_ACTION")
                sb.AppendLine($"    ON DELETE {fk.DeleteAction.Replace("_", " ")}");
            if (fk.UpdateAction != "NO_ACTION")
                sb.AppendLine($"    ON UPDATE {fk.UpdateAction.Replace("_", " ")}");

            sb.AppendLine("GO");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static QuickInfoResult TryGetQuickInfoFromCache(string connectionString, string database, string schema, string objectName)
    {
        var cache = SchemaCache.Instance;
        string connKey = cache.GetConnectionKey(connectionString);

        if (string.IsNullOrEmpty(database))
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                database = builder.InitialCatalog;
            }
            catch { return null; }
        }

        var state = cache.GetState(connKey, database);
        if (state != CacheState.Ready && state != CacheState.Stale)
            return null;

        var obj = cache.FindObject(connKey, database, schema, objectName);
        if (obj == null) return null;

        return new QuickInfoResult
        {
            Schema = obj.SchemaName,
            ObjectName = obj.ObjectName,
            Database = database,
            ObjectType = obj.ObjectType,
            RowCount = obj.RowCount,
            ConnectionString = connectionString
        };
    }

    /// <summary>
    /// Ensures the database is loading/loaded in the shared cache.
    /// Fires and forgets — does not block the caller.
    /// </summary>
    private static void EnsureDatabaseCached(string connectionString, string database)
    {
        try
        {
            var cache = SchemaCache.Instance;
            string connKey = cache.GetConnectionKey(connectionString);

            if (string.IsNullOrEmpty(database))
            {
                try
                {
                    var builder = new SqlConnectionStringBuilder(connectionString);
                    database = builder.InitialCatalog;
                }
                catch { return; }
            }

            if (string.IsNullOrEmpty(database)) return;

            var state = cache.GetState(connKey, database);
            if (state == CacheState.NotLoaded || state == CacheState.Error)
            {
                _ = cache.LoadDatabaseAsync(connectionString, database);
            }
        }
        catch { }
    }

    #endregion

    #region Direct SQL query fallback (original code)

    private static string BuildSchemaScriptDirect(string connectionString, string schema, string objectName)
    {
        using var conn = SqlConnectionFactory.Create(connectionString);
        conn.Open();

        if (string.IsNullOrEmpty(schema))
            schema = ResolveSchema(conn, objectName);

        if (schema == null) return null;

        string objectType = GetObjectType(conn, schema, objectName);
        if (objectType == null) return null;

        var sb = new StringBuilder();

        if (objectType == "U")
        {
            sb.AppendLine(BuildCreateTable(conn, schema, objectName));
            sb.AppendLine();
            sb.AppendLine(BuildIndexes(conn, schema, objectName));
            sb.AppendLine(BuildForeignKeys(conn, schema, objectName));
        }
        else
        {
            // View / procedure / function / trigger — show the module definition.
            sb.AppendLine($"-- {ModuleTypeLabel(objectType)}: [{schema}].[{objectName}]");
            sb.AppendLine(GetObjectDefinition(conn, connectionString, schema, objectName));
        }

        return sb.ToString().TrimEnd();
    }

    private static QuickInfoResult GetQuickInfoDirect(string connectionString, string database, string schema, string objectName)
    {
        try
        {
            using var conn = SqlConnectionFactory.Create(connectionString);
            conn.Open();

            if (string.IsNullOrEmpty(schema))
                schema = ResolveSchema(conn, objectName);

            if (schema == null) return null;

            const string sql = @"
                SELECT o.type, p.rows
                FROM sys.objects o
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                LEFT JOIN (
                    SELECT object_id, SUM(rows) as rows
                    FROM sys.partitions
                    WHERE index_id IN (0,1)
                    GROUP BY object_id
                ) p ON o.object_id = p.object_id
                WHERE o.name = @name AND s.name = @schema
                  AND o.type IN ('U','V','P','FN','IF','TF')";

            using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@name", objectName);
            cmd.Parameters.AddWithValue("@schema", schema);
            cmd.CommandTimeout = 5;

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;

            return new QuickInfoResult
            {
                Schema = schema,
                ObjectName = objectName,
                Database = database,
                ObjectType = (reader.GetString(0) ?? "").Trim(),
                RowCount = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1)),
                ConnectionString = connectionString
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The schema an unqualified name means, the way SQL Server resolves it: the caller's default schema first, then
    /// <c>dbo</c>. Every scriptable type, not only tables and views — <c>EXEC usp_GetOrders</c> names a procedure.
    /// </summary>
    private static string ResolveSchema(SqlConnection conn, string objectName)
    {
        const string sql = @"
                SELECT TOP 1 s.name
                FROM sys.objects o
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.name = @name AND o.type IN ('U','V','P','PC','FN','IF','TF','FS','TR')
                ORDER BY CASE WHEN s.name = SCHEMA_NAME() THEN 0 WHEN s.name = 'dbo' THEN 1 ELSE 2 END";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", objectName);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>An object as the server names it, with the table or view a trigger hangs off.</summary>
    internal sealed class ResolvedObject
    {
        public string Database { get; set; }
        public string Schema { get; set; }
        public string Name { get; set; }
        /// <summary><c>sys.objects.type</c>, trimmed: U, V, P, FN, TR, SN…</summary>
        public string Type { get; set; }
        public string ParentSchema { get; set; }
        public string ParentName { get; set; }
        public string ParentType { get; set; }

        public string QualifiedName => $"{Database}.{Schema}.{Name}";
    }

    /// <summary>
    /// Finds the object a name in the editor refers to — one, two or three part — in the database it names or, failing
    /// that, the connection's own. Unqualified names resolve as <see cref="ResolveSchema"/> does. Null when there is no
    /// such object, which includes one the login has no permission on: <c>sys.objects</c> only lists what the caller can see.
    /// Runs a query, so keep it off the UI thread.
    /// </summary>
    internal static ResolvedObject ResolveObject(string connectionString, string objectName)
    {
        var (database, schema, name) = EditorHelper.ParseObjectName(objectName);
        if (string.IsNullOrEmpty(name)) return null;

        string effectiveConnStr = ConnectionHelper.GetConnectionStringForDatabase(connectionString, database);
        using var conn = SqlConnectionFactory.Create(effectiveConnStr);
        conn.Open();

        const string sql = @"
                SELECT TOP 1 DB_NAME(), s.name, o.name, o.type, ps.name, po.name, po.type
                FROM sys.objects o
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                LEFT JOIN sys.objects po ON po.object_id = o.parent_object_id
                LEFT JOIN sys.schemas ps ON ps.schema_id = po.schema_id
                WHERE o.name = @name AND (@schema IS NULL OR s.name = @schema)
                  AND o.type IN ('U','V','P','PC','FN','IF','TF','FS','FT','TR','SN')
                ORDER BY CASE WHEN s.name = SCHEMA_NAME() THEN 0 WHEN s.name = 'dbo' THEN 1 ELSE 2 END";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@schema", (object)schema ?? DBNull.Value);
        cmd.CommandTimeout = 10;

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        string Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i).Trim();
        return new ResolvedObject
        {
            Database = Text(0),
            Schema = Text(1),
            Name = Text(2),
            Type = Text(3),
            ParentSchema = Text(4),
            ParentName = Text(5),
            ParentType = Text(6)
        };
    }

    private static string ModuleTypeLabel(string objectType) => objectType?.Trim() switch
    {
        "V" => "View",
        "P" or "PC" => "Procedure",
        "FN" => "Scalar Function",
        "IF" => "Inline Function",
        "TF" => "Table Function",
        "FS" => "CLR Function",
        "TR" => "Trigger",
        _ => "Object"
    };

    private static string GetObjectType(SqlConnection conn, string schema, string objectName)
    {
        const string sql = @"
                SELECT o.type
                FROM sys.objects o
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.name = @name AND s.name = @schema
                  AND o.type IN ('U','V','P','PC','FN','IF','TF','FS','TR')";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", objectName);
        cmd.Parameters.AddWithValue("@schema", schema);
        return (cmd.ExecuteScalar() as string)?.Trim();
    }

    private static string GetObjectDefinition(SqlConnection conn, string connectionString, string schema, string objectName)
    {
        const string sql = @"
                SELECT m.definition
                FROM sys.sql_modules m
                JOIN sys.objects o ON m.object_id = o.object_id
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.name = @name AND s.name = @schema";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", objectName);
        cmd.Parameters.AddWithValue("@schema", schema);

        // A NULL definition on a row that exists means one thing: the module is encrypted. (Without VIEW
        // DEFINITION there would be no row at all.)
        if (cmd.ExecuteScalar() is string definition) return definition;

        return DecryptedDefinition(conn, connectionString, schema, objectName);
    }

    /// <summary>
    /// The best available text for a module defined WITH ENCRYPTION: the decrypted definition when that is
    /// switched on and works, and otherwise a comment saying why not. Recovering it costs an ALTER inside a
    /// rolled-back transaction over an administrator connection — see <see cref="ModuleDecryptionService"/>
    /// — which is why it is a setting and why the reason for not doing it is worth spelling out here rather
    /// than leaving a blank the reader has to interpret.
    /// </summary>
    private static string DecryptedDefinition(SqlConnection conn, string connectionString, string schema, string objectName)
    {
        if (!ModuleDecryptionService.Enabled)
        {
            return "-- This object is defined WITH ENCRYPTION, so SQL Server does not return its text."
                 + Environment.NewLine
                 + "-- Turn on \"Decrypt encrypted modules\" in SQLExtended settings to recover it.";
        }

        var module = LoadEncryptedModuleInfo(conn, schema, objectName);
        if (module == null)
            return "-- Definition not available.";

        string text = ModuleDecryptionService.DecryptSingle(connectionString, conn.Database, module, out string error);
        if (text != null)
        {
            return "-- This object is defined WITH ENCRYPTION; the definition below was decrypted."
                 + Environment.NewLine + text;
        }

        return "-- This object is defined WITH ENCRYPTION and could not be decrypted."
             + Environment.NewLine + "-- " + error;
    }

    /// <summary>
    /// Type code and (for a DML trigger) owning table of one object — everything the decryption service
    /// needs to write a dummy definition that will compile.
    /// </summary>
    private static EncryptedModule LoadEncryptedModuleInfo(SqlConnection conn, string schema, string objectName)
    {
        const string sql = @"
                SELECT o.type, o.modify_date, ps.name AS parent_schema, po.name AS parent_name
                FROM sys.objects o
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                LEFT JOIN sys.objects po ON o.parent_object_id = po.object_id
                LEFT JOIN sys.schemas ps ON po.schema_id = ps.schema_id
                WHERE o.name = @name AND s.name = @schema";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", objectName);
        cmd.Parameters.AddWithValue("@schema", schema);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        return new EncryptedModule
        {
            Schema = schema,
            Name = objectName,
            ObjectType = reader.GetString(0)?.Trim(),
            ModifyDate = reader.IsDBNull(1) ? (DateTime?)null : reader.GetDateTime(1),
            ParentSchema = reader.IsDBNull(2) ? null : reader.GetString(2),
            ParentName = reader.IsDBNull(3) ? null : reader.GetString(3),
        };
    }

    #endregion

    #region CREATE TABLE builder (direct SQL fallback)

    private static string BuildCreateTable(SqlConnection conn, string schema, string tableName)
    {
        var columns = GetColumns(conn, schema, tableName);
        var pkColumns = GetPrimaryKeyColumns(conn, schema, tableName);

        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE [{schema}].[{tableName}]");
        sb.AppendLine("(");

        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            sb.Append($"    [{col.Name}] {col.TypeDefinition}");

            if (col.IsIdentity)
                sb.Append($" IDENTITY({col.IdentitySeed},{col.IdentityIncrement})");

            sb.Append(col.IsNullable ? " NULL" : " NOT NULL");

            if (!string.IsNullOrEmpty(col.DefaultConstraint))
                sb.Append($" {col.DefaultConstraint}");

            if (i < columns.Count - 1 || pkColumns.Count > 0)
                sb.Append(",");

            sb.AppendLine();
        }

        if (pkColumns.Count > 0)
        {
            var (pkName, pkType) = GetPrimaryKeyInfo(conn, schema, tableName);
            string pkCols = string.Join(", ", pkColumns.Select(c => $"[{c}]"));
            sb.AppendLine($"    CONSTRAINT [{pkName}] PRIMARY KEY {PkClustering(pkType)} ({pkCols})");
        }

        sb.AppendLine(");");

        long rowCount = GetRowCountEstimate(conn, schema, tableName);
        sb.AppendLine();
        sb.AppendLine($"-- Estimated row count: {rowCount:N0}");

        return sb.ToString();
    }

    private static List<ColumnInfo> GetColumns(SqlConnection conn, string schema, string tableName)
    {
        const string sql = @"
                SELECT
                    c.name AS ColumnName,
                    t.name AS TypeName,
                    c.max_length,
                    c.precision,
                    c.scale,
                    c.is_nullable,
                    c.is_identity,
                    ic.seed_value,
                    ic.increment_value,
                    dc.definition AS DefaultDefinition,
                    dc.name AS DefaultName,
                    c.is_computed,
                    cc.definition AS ComputedDefinition
                FROM sys.columns c
                JOIN sys.types t ON c.user_type_id = t.user_type_id
                JOIN sys.objects o ON c.object_id = o.object_id
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                LEFT JOIN sys.identity_columns ic ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                LEFT JOIN sys.default_constraints dc ON c.default_object_id = dc.object_id
                LEFT JOIN sys.computed_columns cc ON c.object_id = cc.object_id AND c.column_id = cc.column_id
                WHERE o.name = @name AND s.name = @schema
                ORDER BY c.column_id";

        var columns = new List<ColumnInfo>();
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", tableName);
        cmd.Parameters.AddWithValue("@schema", schema);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var col = new ColumnInfo
            {
                Name = reader.GetString(0),
                IsNullable = reader.GetBoolean(5),
                IsIdentity = reader.GetBoolean(6),
                IsComputed = reader.GetBoolean(11)
            };

            string typeName = reader.GetString(1);
            short maxLength = reader.GetInt16(2);
            byte precision = reader.GetByte(3);
            byte scale = reader.GetByte(4);

            if (col.IsComputed)
            {
                col.TypeDefinition = $"AS {reader.GetString(12)}";
            }
            else
            {
                col.TypeDefinition = FormatDataType(typeName, maxLength, precision, scale);
            }

            if (col.IsIdentity && !reader.IsDBNull(7))
            {
                col.IdentitySeed = reader.GetValue(7)?.ToString() ?? "1";
                col.IdentityIncrement = reader.GetValue(8)?.ToString() ?? "1";
            }

            if (!reader.IsDBNull(10) && !reader.IsDBNull(9))
            {
                col.DefaultConstraint = $"CONSTRAINT [{reader.GetString(10)}] DEFAULT {reader.GetString(9)}";
            }

            columns.Add(col);
        }

        return columns;
    }

    private static string FormatDataType(string typeName, short maxLength, byte precision, byte scale)
    {
        switch (typeName.ToLower())
        {
            case "varchar":
            case "nvarchar":
            case "char":
            case "nchar":
            case "binary":
            case "varbinary":
                string len = maxLength == -1 ? "MAX" :
                    (typeName.StartsWith("n") ? (maxLength / 2).ToString() : maxLength.ToString());
                return $"{typeName}({len})";

            case "decimal":
            case "numeric":
                return $"{typeName}({precision},{scale})";

            case "float":
                return precision <= 24 ? "real" : "float";

            case "datetime2":
            case "datetimeoffset":
            case "time":
                return scale > 0 ? $"{typeName}({scale})" : typeName;

            default:
                return typeName;
        }
    }

    private static List<string> GetPrimaryKeyColumns(SqlConnection conn, string schema, string tableName)
    {
        const string sql = @"
                SELECT col.name
                FROM sys.indexes ix
                JOIN sys.index_columns ic ON ix.object_id = ic.object_id AND ix.index_id = ic.index_id
                JOIN sys.columns col ON ic.object_id = col.object_id AND ic.column_id = col.column_id
                JOIN sys.objects o ON ix.object_id = o.object_id
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE ix.is_primary_key = 1 AND o.name = @name AND s.name = @schema
                ORDER BY ic.key_ordinal";

        var cols = new List<string>();
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", tableName);
        cmd.Parameters.AddWithValue("@schema", schema);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) cols.Add(reader.GetString(0));
        return cols;
    }

    private static (string Name, string IndexType) GetPrimaryKeyInfo(SqlConnection conn, string schema, string tableName)
    {
        const string sql = @"
                SELECT ix.name, ix.type_desc
                FROM sys.indexes ix
                JOIN sys.objects o ON ix.object_id = o.object_id
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE ix.is_primary_key = 1 AND o.name = @name AND s.name = @schema";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", tableName);
        cmd.Parameters.AddWithValue("@schema", schema);
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            string name = reader.IsDBNull(0) ? "PK_" + tableName : reader.GetString(0);
            string indexType = reader.IsDBNull(1) ? null : reader.GetString(1);
            return (name, indexType);
        }
        return ("PK_" + tableName, null);
    }

    #endregion

    #region Index builder (direct SQL fallback)

    private static string BuildIndexes(SqlConnection conn, string schema, string tableName)
    {
        const string sql = @"
                SELECT
                    ix.name AS IndexName,
                    ix.type_desc AS IndexType,
                    ix.is_unique,
                    ix.is_primary_key,
                    ix.filter_definition,
                    STUFF((
                        SELECT ', ' + col2.name + CASE WHEN ic2.is_descending_key = 1 THEN ' DESC' ELSE '' END
                        FROM sys.index_columns ic2
                        JOIN sys.columns col2 ON ic2.object_id = col2.object_id AND ic2.column_id = col2.column_id
                        WHERE ic2.object_id = ix.object_id AND ic2.index_id = ix.index_id AND ic2.is_included_column = 0
                        ORDER BY ic2.key_ordinal
                        FOR XML PATH(''), TYPE
                    ).value('.', 'nvarchar(max)'), 1, 2, '') AS KeyColumns,
                    STUFF((
                        SELECT ', ' + col3.name
                        FROM sys.index_columns ic3
                        JOIN sys.columns col3 ON ic3.object_id = col3.object_id AND ic3.column_id = col3.column_id
                        WHERE ic3.object_id = ix.object_id AND ic3.index_id = ix.index_id AND ic3.is_included_column = 1
                        ORDER BY ic3.key_ordinal
                        FOR XML PATH(''), TYPE
                    ).value('.', 'nvarchar(max)'), 1, 2, '') AS IncludedColumns
                FROM sys.indexes ix
                JOIN sys.objects o ON ix.object_id = o.object_id
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.name = @name AND s.name = @schema
                  AND ix.type > 0  -- exclude heap
                  AND ix.is_primary_key = 0  -- PK already shown in CREATE TABLE
                ORDER BY ix.name";

        var sb = new StringBuilder();
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", tableName);
        cmd.Parameters.AddWithValue("@schema", schema);

        using var reader = cmd.ExecuteReader();
        bool hasIndexes = false;

        while (reader.Read())
        {
            if (!hasIndexes)
            {
                sb.AppendLine("-- ============================================");
                sb.AppendLine("-- INDEXES");
                sb.AppendLine("-- ============================================");
                hasIndexes = true;
            }

            string ixName = reader.GetString(0);
            string ixType = reader.GetString(1);
            bool isUnique = reader.GetBoolean(2);
            string filterDef = reader.IsDBNull(4) ? null : reader.GetString(4);
            string keyCols = reader.IsDBNull(5) ? "" : reader.GetString(5);
            string includedCols = reader.IsDBNull(6) ? null : reader.GetString(6);

            string uniqueStr = isUnique ? "UNIQUE " : "";
            sb.Append($"CREATE {uniqueStr}{ixType} INDEX [{ixName}]");
            sb.AppendLine($" ON [{schema}].[{tableName}] ({keyCols})");

            if (!string.IsNullOrEmpty(includedCols))
                sb.AppendLine($"    INCLUDE ({includedCols})");

            if (!string.IsNullOrEmpty(filterDef))
                sb.AppendLine($"    WHERE {filterDef}");

            sb.AppendLine("GO");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    #endregion

    #region Foreign keys (direct SQL fallback)

    private static string BuildForeignKeys(SqlConnection conn, string schema, string tableName)
    {
        const string sql = @"
                SELECT
                    fk.name AS FKName,
                    STUFF((
                        SELECT ', ' + col2.name
                        FROM sys.foreign_key_columns fkc2
                        JOIN sys.columns col2 ON fkc2.parent_object_id = col2.object_id AND fkc2.parent_column_id = col2.column_id
                        WHERE fkc2.constraint_object_id = fk.object_id
                        ORDER BY fkc2.constraint_column_id
                        FOR XML PATH(''), TYPE
                    ).value('.', 'nvarchar(max)'), 1, 2, '') AS Columns,
                    rs.name AS ReferencedSchema,
                    rt.name AS ReferencedTable,
                    STUFF((
                        SELECT ', ' + rcol2.name
                        FROM sys.foreign_key_columns fkc3
                        JOIN sys.columns rcol2 ON fkc3.referenced_object_id = rcol2.object_id AND fkc3.referenced_column_id = rcol2.column_id
                        WHERE fkc3.constraint_object_id = fk.object_id
                        ORDER BY fkc3.constraint_column_id
                        FOR XML PATH(''), TYPE
                    ).value('.', 'nvarchar(max)'), 1, 2, '') AS ReferencedColumns,
                    fk.delete_referential_action_desc,
                    fk.update_referential_action_desc
                FROM sys.foreign_keys fk
                JOIN sys.objects o ON fk.parent_object_id = o.object_id
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                JOIN sys.objects rt ON fk.referenced_object_id = rt.object_id
                JOIN sys.schemas rs ON rt.schema_id = rs.schema_id
                WHERE o.name = @name AND s.name = @schema
                ORDER BY fk.name";

        var sb = new StringBuilder();
        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", tableName);
        cmd.Parameters.AddWithValue("@schema", schema);

        using var reader = cmd.ExecuteReader();
        bool hasFKs = false;

        while (reader.Read())
        {
            if (!hasFKs)
            {
                sb.AppendLine("-- ============================================");
                sb.AppendLine("-- FOREIGN KEYS");
                sb.AppendLine("-- ============================================");
                hasFKs = true;
            }

            string fkName = reader.GetString(0);
            string cols = reader.GetString(1);
            string refSchema = reader.GetString(2);
            string refTable = reader.GetString(3);
            string refCols = reader.GetString(4);
            string deleteAction = reader.GetString(5);
            string updateAction = reader.GetString(6);

            sb.AppendLine($"ALTER TABLE [{schema}].[{tableName}]");
            sb.Append($"    ADD CONSTRAINT [{fkName}] FOREIGN KEY ({cols})");
            sb.AppendLine($" REFERENCES [{refSchema}].[{refTable}] ({refCols})");

            if (deleteAction != "NO_ACTION")
                sb.AppendLine($"    ON DELETE {deleteAction.Replace("_", " ")}");
            if (updateAction != "NO_ACTION")
                sb.AppendLine($"    ON UPDATE {updateAction.Replace("_", " ")}");

            sb.AppendLine("GO");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    #endregion

    private static long GetRowCountEstimate(SqlConnection conn, string schema, string tableName)
    {
        const string sql = @"
                SELECT SUM(p.rows)
                FROM sys.partitions p
                JOIN sys.objects o ON p.object_id = o.object_id
                JOIN sys.schemas s ON o.schema_id = s.schema_id
                WHERE o.name = @name AND s.name = @schema AND p.index_id IN (0, 1)";

        using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@name", tableName);
        cmd.Parameters.AddWithValue("@schema", schema);
        var result = cmd.ExecuteScalar();
        return result is long l ? l : 0;
    }

    private class ColumnInfo
    {
        public string Name { get; set; }
        public string TypeDefinition { get; set; }
        public bool IsNullable { get; set; }
        public bool IsIdentity { get; set; }
        public bool IsComputed { get; set; }
        public string IdentitySeed { get; set; }
        public string IdentityIncrement { get; set; }
        public string DefaultConstraint { get; set; }
    }
}
