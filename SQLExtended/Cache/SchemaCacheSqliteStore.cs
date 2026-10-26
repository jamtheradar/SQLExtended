using SQLExtended.Cache.Models;
using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;

namespace SQLExtended.Cache;

/// <summary>
/// SQLite persistence layer for the schema cache. Stores data at
/// %APPDATA%\SQLExtended\SSMS\schema-cache.db so the cache survives SSMS restarts.
/// </summary>
internal sealed class SchemaCacheSqliteStore : IDisposable
{
    private readonly string _dbPath;
    private SQLiteConnection _conn;

    public SchemaCacheSqliteStore()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dir = Path.Combine(appData, "SQLExtended", "SSMS");
        Directory.CreateDirectory(dir);
        _dbPath = Path.Combine(dir, "schema-cache.db");
    }

    /// <summary>
    /// Opens the store and purges entries older than <paramref name="maxAgeDays"/>. The age is passed in
    /// rather than read here: this runs behind <c>SchemaCache.Initialize</c>, and the settings singleton
    /// must be faulted in from the UI thread (Diagnostics/CLAUDE.md). A non-positive value purges nothing.
    /// </summary>
    public void Initialize(int maxAgeDays)
    {
        _conn = new SQLiteConnection($"Data Source={_dbPath};Version=3;Journal Mode=WAL;");
        _conn.Open();
        CreateSchema();
        PurgeStaleEntries(maxAgeDays);
    }

    /// <summary>
    /// CREATE TABLE IF NOT EXISTS leaves a cache file from an older build at its old shape, so columns added since are
    /// added here. Rows already in it keep NULLs until the next full load — for the synonym columns that reads as
    /// "target unknown", which completion already handles.
    /// </summary>
    private void AddMissingObjectColumns()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var info = new SQLiteCommand("PRAGMA table_info(cache_objects)", _conn))
        using (var reader = info.ExecuteReader())
        {
            while (reader.Read())
                existing.Add(reader.GetString(1));
        }

        foreach (string column in new[] { "base_object_name", "base_type" })
        {
            if (existing.Contains(column))
                continue;
            using var alter = new SQLiteCommand($"ALTER TABLE cache_objects ADD COLUMN {column} TEXT", _conn);
            alter.ExecuteNonQuery();
        }
    }

    private void CreateSchema()
    {
        const string ddl = @"
            CREATE TABLE IF NOT EXISTS cache_databases (
                connection_key TEXT NOT NULL,
                database_name TEXT NOT NULL,
                last_full_refresh TEXT,
                last_incremental TEXT,
                PRIMARY KEY (connection_key, database_name)
            );

            CREATE TABLE IF NOT EXISTS cache_objects (
                connection_key TEXT NOT NULL,
                database_name TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                object_name TEXT NOT NULL,
                object_type TEXT NOT NULL,
                row_count INTEGER,
                create_date TEXT,
                modify_date TEXT,
                definition TEXT,
                base_object_name TEXT,
                base_type TEXT,
                PRIMARY KEY (connection_key, database_name, schema_name, object_name)
            );

            CREATE TABLE IF NOT EXISTS cache_columns (
                connection_key TEXT NOT NULL,
                database_name TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                column_name TEXT NOT NULL,
                ordinal INTEGER,
                data_type TEXT,
                max_length INTEGER,
                precision INTEGER,
                scale INTEGER,
                is_nullable INTEGER,
                is_identity INTEGER,
                is_computed INTEGER,
                computed_definition TEXT,
                default_definition TEXT,
                description TEXT,
                PRIMARY KEY (connection_key, database_name, schema_name, table_name, column_name)
            );

            CREATE TABLE IF NOT EXISTS cache_indexes (
                connection_key TEXT NOT NULL,
                database_name TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                index_name TEXT NOT NULL,
                index_type TEXT,
                is_unique INTEGER,
                is_primary_key INTEGER,
                key_columns TEXT,
                included_columns TEXT,
                filter_definition TEXT,
                PRIMARY KEY (connection_key, database_name, schema_name, table_name, index_name)
            );

            CREATE TABLE IF NOT EXISTS cache_foreign_keys (
                connection_key TEXT NOT NULL,
                database_name TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                fk_name TEXT NOT NULL,
                fk_columns TEXT,
                ref_schema TEXT,
                ref_table TEXT,
                ref_columns TEXT,
                delete_action TEXT,
                update_action TEXT,
                PRIMARY KEY (connection_key, database_name, schema_name, table_name, fk_name)
            );

            CREATE TABLE IF NOT EXISTS cache_parameters (
                connection_key TEXT NOT NULL,
                database_name TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                object_name TEXT NOT NULL,
                parameter_name TEXT NOT NULL,
                ordinal INTEGER,
                data_type TEXT,
                max_length INTEGER,
                is_output INTEGER,
                has_default INTEGER,
                PRIMARY KEY (connection_key, database_name, schema_name, object_name, parameter_name)
            );";

        using var cmd = new SQLiteCommand(ddl, _conn);
        cmd.ExecuteNonQuery();

        AddMissingObjectColumns();

        // Create FTS5 table if not exists — requires checking first since CREATE VIRTUAL TABLE IF NOT EXISTS isn't supported in older SQLite
        try
        {
            using var check = new SQLiteCommand("SELECT 1 FROM cache_objects_fts LIMIT 1", _conn);
            check.ExecuteScalar();
        }
        catch
        {
            const string fts = @"
                CREATE VIRTUAL TABLE cache_objects_fts USING fts5(
                    object_name,
                    definition,
                    schema_name,
                    content='cache_objects',
                    content_rowid='rowid'
                );";
            try
            {
                using var ftsCmd = new SQLiteCommand(fts, _conn);
                ftsCmd.ExecuteNonQuery();
            }
            catch
            {
                // FTS5 may not be available in all SQLite builds — search will fall back to LIKE
            }
        }
    }

    /// <summary>
    /// Auto-purge entries whose last full refresh is older than <paramref name="maxAgeDays"/> days.
    /// Zero or less keeps everything - a cache nobody asked to expire is not stale, it is just cold, and
    /// dropping it costs a full reload of every database on the next completion.
    /// </summary>
    private void PurgeStaleEntries(int maxAgeDays)
    {
        if (maxAgeDays <= 0)
            return;

        string cutoff = DateTime.UtcNow.AddDays(-maxAgeDays).ToString("o");
        const string sql = @"
            DELETE FROM cache_objects WHERE connection_key || '|' || database_name IN (
                SELECT connection_key || '|' || database_name FROM cache_databases
                WHERE last_full_refresh < @cutoff
            );
            DELETE FROM cache_columns WHERE connection_key || '|' || database_name IN (
                SELECT connection_key || '|' || database_name FROM cache_databases
                WHERE last_full_refresh < @cutoff
            );
            DELETE FROM cache_indexes WHERE connection_key || '|' || database_name IN (
                SELECT connection_key || '|' || database_name FROM cache_databases
                WHERE last_full_refresh < @cutoff
            );
            DELETE FROM cache_foreign_keys WHERE connection_key || '|' || database_name IN (
                SELECT connection_key || '|' || database_name FROM cache_databases
                WHERE last_full_refresh < @cutoff
            );
            DELETE FROM cache_parameters WHERE connection_key || '|' || database_name IN (
                SELECT connection_key || '|' || database_name FROM cache_databases
                WHERE last_full_refresh < @cutoff
            );
            DELETE FROM cache_databases WHERE last_full_refresh < @cutoff;";

        using var cmd = new SQLiteCommand(sql, _conn);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Saves a full database cache load to SQLite. Replaces existing data for this connection+database.
    /// </summary>
    public void SaveDatabase(string connectionKey, string database, DatabaseCacheData data)
    {
        using var tx = _conn.BeginTransaction();
        try
        {
            // Clear existing data for this database
            ClearDatabaseInternal(connectionKey, database, tx);

            // Insert metadata
            using (var cmd = new SQLiteCommand(
                "INSERT OR REPLACE INTO cache_databases (connection_key, database_name, last_full_refresh, last_incremental) VALUES (@ck, @db, @ts, @ts)", _conn, tx))
            {
                cmd.Parameters.AddWithValue("@ck", connectionKey);
                cmd.Parameters.AddWithValue("@db", database);
                cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("o"));
                cmd.ExecuteNonQuery();
            }

            // Bulk insert objects
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO cache_objects (connection_key, database_name, schema_name, object_name, object_type, row_count, create_date, modify_date, definition, base_object_name, base_type)
                  VALUES (@ck, @db, @sn, @on, @ot, @rc, @cd, @md, @def, @bon, @bt)", _conn, tx))
            {
                cmd.Parameters.Add("@ck", System.Data.DbType.String).Value = connectionKey;
                cmd.Parameters.Add("@db", System.Data.DbType.String).Value = database;
                var pSn = cmd.Parameters.Add("@sn", System.Data.DbType.String);
                var pOn = cmd.Parameters.Add("@on", System.Data.DbType.String);
                var pOt = cmd.Parameters.Add("@ot", System.Data.DbType.String);
                var pRc = cmd.Parameters.Add("@rc", System.Data.DbType.Int64);
                var pCd = cmd.Parameters.Add("@cd", System.Data.DbType.String);
                var pMd = cmd.Parameters.Add("@md", System.Data.DbType.String);
                var pDef = cmd.Parameters.Add("@def", System.Data.DbType.String);
                var pBon = cmd.Parameters.Add("@bon", System.Data.DbType.String);
                var pBt = cmd.Parameters.Add("@bt", System.Data.DbType.String);

                foreach (var obj in data.Objects)
                {
                    pSn.Value = obj.SchemaName;
                    pOn.Value = obj.ObjectName;
                    pOt.Value = obj.ObjectType;
                    pRc.Value = obj.RowCount;
                    pCd.Value = obj.CreateDate?.ToString("o") ?? (object)DBNull.Value;
                    pMd.Value = obj.ModifyDate?.ToString("o") ?? (object)DBNull.Value;
                    pDef.Value = (object)obj.Definition ?? DBNull.Value;
                    pBon.Value = (object)obj.BaseObjectName ?? DBNull.Value;
                    pBt.Value = (object)obj.BaseObjectType ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }

            // Bulk insert columns
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO cache_columns (connection_key, database_name, schema_name, table_name, column_name, ordinal, data_type, max_length, precision, scale, is_nullable, is_identity, is_computed, computed_definition, default_definition, description)
                  VALUES (@ck, @db, @sn, @tn, @cn, @ord, @dt, @ml, @pr, @sc, @in, @ii, @ic, @cd, @dd, @desc)", _conn, tx))
            {
                cmd.Parameters.Add("@ck", System.Data.DbType.String).Value = connectionKey;
                cmd.Parameters.Add("@db", System.Data.DbType.String).Value = database;
                var pSn = cmd.Parameters.Add("@sn", System.Data.DbType.String);
                var pTn = cmd.Parameters.Add("@tn", System.Data.DbType.String);
                var pCn = cmd.Parameters.Add("@cn", System.Data.DbType.String);
                var pOrd = cmd.Parameters.Add("@ord", System.Data.DbType.Int32);
                var pDt = cmd.Parameters.Add("@dt", System.Data.DbType.String);
                var pMl = cmd.Parameters.Add("@ml", System.Data.DbType.Int32);
                var pPr = cmd.Parameters.Add("@pr", System.Data.DbType.Int32);
                var pSc = cmd.Parameters.Add("@sc", System.Data.DbType.Int32);
                var pIn = cmd.Parameters.Add("@in", System.Data.DbType.Int32);
                var pIi = cmd.Parameters.Add("@ii", System.Data.DbType.Int32);
                var pIc = cmd.Parameters.Add("@ic", System.Data.DbType.Int32);
                var pCd = cmd.Parameters.Add("@cd", System.Data.DbType.String);
                var pDd = cmd.Parameters.Add("@dd", System.Data.DbType.String);
                var pDesc = cmd.Parameters.Add("@desc", System.Data.DbType.String);

                foreach (var col in data.Columns)
                {
                    pSn.Value = col.SchemaName;
                    pTn.Value = col.TableName;
                    pCn.Value = col.ColumnName;
                    pOrd.Value = col.Ordinal;
                    pDt.Value = col.DataType;
                    pMl.Value = col.MaxLength;
                    pPr.Value = col.Precision;
                    pSc.Value = col.Scale;
                    pIn.Value = col.IsNullable ? 1 : 0;
                    pIi.Value = col.IsIdentity ? 1 : 0;
                    pIc.Value = col.IsComputed ? 1 : 0;
                    pCd.Value = (object)col.ComputedDefinition ?? DBNull.Value;
                    pDd.Value = (object)col.DefaultDefinition ?? DBNull.Value;
                    pDesc.Value = (object)col.Description ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }

            // Bulk insert indexes
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO cache_indexes (connection_key, database_name, schema_name, table_name, index_name, index_type, is_unique, is_primary_key, key_columns, included_columns, filter_definition)
                  VALUES (@ck, @db, @sn, @tn, @ixn, @ixt, @iu, @ipk, @kc, @ic, @fd)", _conn, tx))
            {
                cmd.Parameters.Add("@ck", System.Data.DbType.String).Value = connectionKey;
                cmd.Parameters.Add("@db", System.Data.DbType.String).Value = database;
                var pSn = cmd.Parameters.Add("@sn", System.Data.DbType.String);
                var pTn = cmd.Parameters.Add("@tn", System.Data.DbType.String);
                var pIxn = cmd.Parameters.Add("@ixn", System.Data.DbType.String);
                var pIxt = cmd.Parameters.Add("@ixt", System.Data.DbType.String);
                var pIu = cmd.Parameters.Add("@iu", System.Data.DbType.Int32);
                var pIpk = cmd.Parameters.Add("@ipk", System.Data.DbType.Int32);
                var pKc = cmd.Parameters.Add("@kc", System.Data.DbType.String);
                var pIc = cmd.Parameters.Add("@ic", System.Data.DbType.String);
                var pFd = cmd.Parameters.Add("@fd", System.Data.DbType.String);

                foreach (var ix in data.Indexes)
                {
                    pSn.Value = ix.SchemaName;
                    pTn.Value = ix.TableName;
                    pIxn.Value = (object)ix.IndexName ?? DBNull.Value;
                    pIxt.Value = ix.IndexType;
                    pIu.Value = ix.IsUnique ? 1 : 0;
                    pIpk.Value = ix.IsPrimaryKey ? 1 : 0;
                    pKc.Value = (object)ix.KeyColumns ?? DBNull.Value;
                    pIc.Value = (object)ix.IncludedColumns ?? DBNull.Value;
                    pFd.Value = (object)ix.FilterDefinition ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }

            // Bulk insert foreign keys
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO cache_foreign_keys (connection_key, database_name, schema_name, table_name, fk_name, fk_columns, ref_schema, ref_table, ref_columns, delete_action, update_action)
                  VALUES (@ck, @db, @sn, @tn, @fkn, @fkc, @rs, @rt, @rc, @da, @ua)", _conn, tx))
            {
                cmd.Parameters.Add("@ck", System.Data.DbType.String).Value = connectionKey;
                cmd.Parameters.Add("@db", System.Data.DbType.String).Value = database;
                var pSn = cmd.Parameters.Add("@sn", System.Data.DbType.String);
                var pTn = cmd.Parameters.Add("@tn", System.Data.DbType.String);
                var pFkn = cmd.Parameters.Add("@fkn", System.Data.DbType.String);
                var pFkc = cmd.Parameters.Add("@fkc", System.Data.DbType.String);
                var pRs = cmd.Parameters.Add("@rs", System.Data.DbType.String);
                var pRt = cmd.Parameters.Add("@rt", System.Data.DbType.String);
                var pRc = cmd.Parameters.Add("@rc", System.Data.DbType.String);
                var pDa = cmd.Parameters.Add("@da", System.Data.DbType.String);
                var pUa = cmd.Parameters.Add("@ua", System.Data.DbType.String);

                foreach (var fk in data.ForeignKeys)
                {
                    pSn.Value = fk.SchemaName;
                    pTn.Value = fk.TableName;
                    pFkn.Value = fk.ForeignKeyName;
                    pFkc.Value = fk.Columns;
                    pRs.Value = fk.ReferencedSchema;
                    pRt.Value = fk.ReferencedTable;
                    pRc.Value = fk.ReferencedColumns;
                    pDa.Value = fk.DeleteAction;
                    pUa.Value = fk.UpdateAction;
                    cmd.ExecuteNonQuery();
                }
            }

            // Bulk insert parameters
            using (var cmd = new SQLiteCommand(
                @"INSERT INTO cache_parameters (connection_key, database_name, schema_name, object_name, parameter_name, ordinal, data_type, max_length, is_output, has_default)
                  VALUES (@ck, @db, @sn, @on, @pn, @ord, @dt, @ml, @io, @hd)", _conn, tx))
            {
                cmd.Parameters.Add("@ck", System.Data.DbType.String).Value = connectionKey;
                cmd.Parameters.Add("@db", System.Data.DbType.String).Value = database;
                var pSn = cmd.Parameters.Add("@sn", System.Data.DbType.String);
                var pOn = cmd.Parameters.Add("@on", System.Data.DbType.String);
                var pPn = cmd.Parameters.Add("@pn", System.Data.DbType.String);
                var pOrd = cmd.Parameters.Add("@ord", System.Data.DbType.Int32);
                var pDt = cmd.Parameters.Add("@dt", System.Data.DbType.String);
                var pMl = cmd.Parameters.Add("@ml", System.Data.DbType.Int32);
                var pIo = cmd.Parameters.Add("@io", System.Data.DbType.Int32);
                var pHd = cmd.Parameters.Add("@hd", System.Data.DbType.Int32);

                foreach (var p in data.Parameters)
                {
                    pSn.Value = p.SchemaName;
                    pOn.Value = p.ObjectName;
                    pPn.Value = p.ParameterName;
                    pOrd.Value = p.Ordinal;
                    pDt.Value = p.DataType;
                    pMl.Value = p.MaxLength;
                    pIo.Value = p.IsOutput ? 1 : 0;
                    pHd.Value = p.HasDefault ? 1 : 0;
                    cmd.ExecuteNonQuery();
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// Loads cached data for a specific database from SQLite, or null if not cached.
    /// </summary>
    public DatabaseCacheData LoadDatabase(string connectionKey, string database)
    {
        // Check if we have data for this database
        using (var cmd = new SQLiteCommand(
            "SELECT last_full_refresh FROM cache_databases WHERE connection_key = @ck AND database_name = @db", _conn))
        {
            cmd.Parameters.AddWithValue("@ck", connectionKey);
            cmd.Parameters.AddWithValue("@db", database);
            if (cmd.ExecuteScalar() == null)
                return null;
        }

        var data = new DatabaseCacheData();

        // Load objects
        using (var cmd = new SQLiteCommand(
            "SELECT schema_name, object_name, object_type, row_count, create_date, modify_date, definition, base_object_name, base_type FROM cache_objects WHERE connection_key = @ck AND database_name = @db", _conn))
        {
            cmd.Parameters.AddWithValue("@ck", connectionKey);
            cmd.Parameters.AddWithValue("@db", database);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                data.Objects.Add(new CachedObject
                {
                    ConnectionKey = connectionKey,
                    DatabaseName = database,
                    SchemaName = reader.GetString(0),
                    ObjectName = reader.GetString(1),
                    ObjectType = reader.GetString(2),
                    RowCount = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    CreateDate = reader.IsDBNull(4) ? null : (DateTime?)DateTime.Parse(reader.GetString(4)),
                    ModifyDate = reader.IsDBNull(5) ? null : (DateTime?)DateTime.Parse(reader.GetString(5)),
                    Definition = reader.IsDBNull(6) ? null : reader.GetString(6),
                    BaseObjectName = reader.IsDBNull(7) ? null : reader.GetString(7),
                    BaseObjectType = reader.IsDBNull(8) ? null : reader.GetString(8)
                });
            }
        }

        // Load columns
        using (var cmd = new SQLiteCommand(
            "SELECT schema_name, table_name, column_name, ordinal, data_type, is_nullable, is_identity, is_computed, computed_definition, default_definition, description FROM cache_columns WHERE connection_key = @ck AND database_name = @db ORDER BY schema_name, table_name, ordinal", _conn))
        {
            cmd.Parameters.AddWithValue("@ck", connectionKey);
            cmd.Parameters.AddWithValue("@db", database);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                data.Columns.Add(new CachedColumn
                {
                    SchemaName = reader.GetString(0),
                    TableName = reader.GetString(1),
                    ColumnName = reader.GetString(2),
                    Ordinal = reader.GetInt32(3),
                    DataType = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    IsNullable = reader.GetInt32(5) != 0,
                    IsIdentity = reader.GetInt32(6) != 0,
                    IsComputed = reader.GetInt32(7) != 0,
                    ComputedDefinition = reader.IsDBNull(8) ? null : reader.GetString(8),
                    DefaultDefinition = reader.IsDBNull(9) ? null : reader.GetString(9),
                    Description = reader.IsDBNull(10) ? null : reader.GetString(10)
                });
            }
        }

        // Load indexes
        using (var cmd = new SQLiteCommand(
            "SELECT schema_name, table_name, index_name, index_type, is_unique, is_primary_key, key_columns, included_columns, filter_definition FROM cache_indexes WHERE connection_key = @ck AND database_name = @db", _conn))
        {
            cmd.Parameters.AddWithValue("@ck", connectionKey);
            cmd.Parameters.AddWithValue("@db", database);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                data.Indexes.Add(new CachedIndex
                {
                    SchemaName = reader.GetString(0),
                    TableName = reader.GetString(1),
                    IndexName = reader.IsDBNull(2) ? null : reader.GetString(2),
                    IndexType = reader.IsDBNull(3) ? null : reader.GetString(3),
                    IsUnique = reader.GetInt32(4) != 0,
                    IsPrimaryKey = reader.GetInt32(5) != 0,
                    KeyColumns = reader.IsDBNull(6) ? null : reader.GetString(6),
                    IncludedColumns = reader.IsDBNull(7) ? null : reader.GetString(7),
                    FilterDefinition = reader.IsDBNull(8) ? null : reader.GetString(8)
                });
            }
        }

        // Load foreign keys
        using (var cmd = new SQLiteCommand(
            "SELECT schema_name, table_name, fk_name, fk_columns, ref_schema, ref_table, ref_columns, delete_action, update_action FROM cache_foreign_keys WHERE connection_key = @ck AND database_name = @db", _conn))
        {
            cmd.Parameters.AddWithValue("@ck", connectionKey);
            cmd.Parameters.AddWithValue("@db", database);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                data.ForeignKeys.Add(new CachedForeignKey
                {
                    SchemaName = reader.GetString(0),
                    TableName = reader.GetString(1),
                    ForeignKeyName = reader.GetString(2),
                    Columns = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    ReferencedSchema = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    ReferencedTable = reader.IsDBNull(5) ? "" : reader.GetString(5),
                    ReferencedColumns = reader.IsDBNull(6) ? "" : reader.GetString(6),
                    DeleteAction = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    UpdateAction = reader.IsDBNull(8) ? "" : reader.GetString(8)
                });
            }
        }

        // Load parameters
        using (var cmd = new SQLiteCommand(
            "SELECT schema_name, object_name, parameter_name, ordinal, data_type, max_length, is_output, has_default FROM cache_parameters WHERE connection_key = @ck AND database_name = @db ORDER BY schema_name, object_name, ordinal", _conn))
        {
            cmd.Parameters.AddWithValue("@ck", connectionKey);
            cmd.Parameters.AddWithValue("@db", database);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                data.Parameters.Add(new CachedParameter
                {
                    SchemaName = reader.GetString(0),
                    ObjectName = reader.GetString(1),
                    ParameterName = reader.GetString(2),
                    Ordinal = reader.GetInt32(3),
                    DataType = reader.IsDBNull(4) ? "" : reader.GetString(4),
                    MaxLength = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                    IsOutput = reader.GetInt32(6) != 0,
                    HasDefault = reader.GetInt32(7) != 0
                });
            }
        }

        return data;
    }

    /// <summary>
    /// Returns all connection_key + database_name pairs that have cached data.
    /// </summary>
    public List<(string connectionKey, string database)> GetCachedDatabases()
    {
        var result = new List<(string, string)>();
        using var cmd = new SQLiteCommand("SELECT connection_key, database_name FROM cache_databases", _conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetString(0), reader.GetString(1)));
        return result;
    }

    public void ClearDatabase(string connectionKey, string database)
    {
        using var tx = _conn.BeginTransaction();
        ClearDatabaseInternal(connectionKey, database, tx);
        tx.Commit();
    }

    public void ClearAll()
    {
        using var cmd = new SQLiteCommand(@"
            DELETE FROM cache_objects;
            DELETE FROM cache_columns;
            DELETE FROM cache_indexes;
            DELETE FROM cache_foreign_keys;
            DELETE FROM cache_parameters;
            DELETE FROM cache_databases;", _conn);
        cmd.ExecuteNonQuery();
    }

    private void ClearDatabaseInternal(string connectionKey, string database, SQLiteTransaction tx)
    {
        string[] tables = { "cache_objects", "cache_columns", "cache_indexes", "cache_foreign_keys", "cache_parameters" };
        foreach (var table in tables)
        {
            using var cmd = new SQLiteCommand($"DELETE FROM {table} WHERE connection_key = @ck AND database_name = @db", _conn, tx);
            cmd.Parameters.AddWithValue("@ck", connectionKey);
            cmd.Parameters.AddWithValue("@db", database);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Full-text search across object names and definitions using FTS5.
    /// Falls back to LIKE if FTS5 is not available.
    /// </summary>
    public List<SearchResult> Search(string connectionKey, string database, string searchTerm, SearchOptions options)
    {
        var results = new List<SearchResult>();
        int maxResults = options?.MaxResults ?? 200;

        // Try FTS5 first for definition search
        if (options?.SearchDefinitions != false || options?.SearchObjectNames != false)
        {
            try
            {
                return SearchWithFts(connectionKey, database, searchTerm, options);
            }
            catch
            {
                // Fall through to LIKE-based search
            }
        }

        return SearchWithLike(connectionKey, database, searchTerm, options);
    }

    private List<SearchResult> SearchWithFts(string connectionKey, string database, string searchTerm, SearchOptions options)
    {
        // FTS5 search — to be implemented when FTS index triggers are set up
        // For now, fall through to LIKE
        return SearchWithLike(connectionKey, database, searchTerm, options);
    }

    private static (string clause, Action<SQLiteCommand> addParams) BuildTypeFilterClause(string typeFilter, string objectTypeColumn = "object_type")
    {
        if (string.IsNullOrEmpty(typeFilter))
            return ("", _ => { });

        var types = typeFilter.Split(',');
        var placeholders = new string[types.Length];
        for (int i = 0; i < types.Length; i++)
            placeholders[i] = $"@tf{i}";

        string clause = $" AND {objectTypeColumn} IN ({string.Join(",", placeholders)})";
        void addParams(SQLiteCommand cmd)
        {
            for (int i = 0; i < types.Length; i++)
                cmd.Parameters.AddWithValue($"@tf{i}", types[i]);
        }
        return (clause, addParams);
    }

    private List<SearchResult> SearchWithLike(string connectionKey, string database, string searchTerm, SearchOptions options)
    {
        var results = new List<SearchResult>();
        int maxResults = options?.MaxResults ?? 200;
        string likePattern = SearchTextMatcher.BuildSqliteLikePattern(searchTerm);
        var (typeClause, addTypeParams) = BuildTypeFilterClause(options?.TypeFilter);

        // Whole word has no LIKE form, so LIKE narrows to the substring matches and the word check runs here.
        // The SQL LIMIT then has to go (-1 is SQLite's "no limit"): applied before the check, substring-only
        // rows would fill it and the whole-word ones behind them would never be read.
        bool wholeWord = options?.WholeWord == true;
        int SqlLimit(int remaining) => wholeWord ? -1 : remaining;

        // Search object names
        if (options?.SearchObjectNames != false)
        {
            using var cmd = new SQLiteCommand(
                $@"SELECT schema_name, object_name, object_type FROM cache_objects
                  WHERE connection_key = @ck AND database_name = @db
                    AND object_name LIKE @pattern ESCAPE '\' COLLATE NOCASE{typeClause}
                  LIMIT @limit", _conn);
            cmd.Parameters.AddWithValue("@ck", connectionKey);
            cmd.Parameters.AddWithValue("@db", database);
            cmd.Parameters.AddWithValue("@pattern", likePattern);
            cmd.Parameters.AddWithValue("@limit", SqlLimit(maxResults));
            addTypeParams(cmd);
            using var reader = cmd.ExecuteReader();
            while (reader.Read() && results.Count < maxResults)
            {
                if (wholeWord && !SearchTextMatcher.Contains(reader.GetString(1), searchTerm, true)) continue;
                results.Add(new SearchResult
                {
                    SchemaName = reader.GetString(0),
                    ObjectName = reader.GetString(1),
                    ObjectType = reader.GetString(2),
                    MatchLocation = "ObjectName",
                    MatchDetail = reader.GetString(1)
                });
            }
        }

        // Search column names — join to cache_objects to filter by parent object type
        if (options?.SearchColumnNames != false && results.Count < maxResults)
        {
            string colTypeClause = string.IsNullOrEmpty(options?.TypeFilter)
                ? ""
                : BuildTypeFilterClause(options.TypeFilter, "o.object_type").clause;

            // Both forms read the parent's object_type for the "COLUMN · VIEW" label; the join is a primary-key
            // lookup. Unfiltered it is a LEFT JOIN, so a column whose parent row is missing is still found (the
            // label then just says COLUMN) — an INNER JOIN would quietly drop it, which reads as "no matches".
            string colSql = string.IsNullOrEmpty(colTypeClause)
                ? @"SELECT c.schema_name, c.table_name, c.column_name, o.object_type FROM cache_columns c
                    LEFT JOIN cache_objects o ON o.connection_key = c.connection_key
                      AND o.database_name = c.database_name
                      AND o.schema_name = c.schema_name
                      AND o.object_name = c.table_name
                    WHERE c.connection_key = @ck AND c.database_name = @db
                      AND c.column_name LIKE @pattern ESCAPE '\' COLLATE NOCASE
                    LIMIT @limit"
                : $@"SELECT c.schema_name, c.table_name, c.column_name, o.object_type FROM cache_columns c
                    INNER JOIN cache_objects o ON o.connection_key = c.connection_key
                      AND o.database_name = c.database_name
                      AND o.schema_name = c.schema_name
                      AND o.object_name = c.table_name
                    WHERE c.connection_key = @ck AND c.database_name = @db
                      AND c.column_name LIKE @pattern ESCAPE '\' COLLATE NOCASE{colTypeClause}
                    LIMIT @limit";

            using var cmd = new SQLiteCommand(colSql, _conn);
            cmd.Parameters.AddWithValue("@ck", connectionKey);
            cmd.Parameters.AddWithValue("@db", database);
            cmd.Parameters.AddWithValue("@pattern", likePattern);
            cmd.Parameters.AddWithValue("@limit", SqlLimit(maxResults - results.Count));
            if (!string.IsNullOrEmpty(colTypeClause))
                BuildTypeFilterClause(options.TypeFilter, "o.object_type").addParams(cmd);
            using var reader = cmd.ExecuteReader();
            while (reader.Read() && results.Count < maxResults)
            {
                if (wholeWord && !SearchTextMatcher.Contains(reader.GetString(2), searchTerm, true)) continue;
                results.Add(new SearchResult
                {
                    SchemaName = reader.GetString(0),
                    ObjectName = reader.GetString(1),
                    ObjectType = "Column",
                    MatchLocation = "ColumnName",
                    MatchDetail = reader.GetString(2),
                    ParentObjectType = reader.IsDBNull(3) ? null : reader.GetString(3)
                });
            }
        }

        // Search definitions. When a name pass ran, an object whose name matches is left out, as that pass lists
        // it; without one (Find References is definitions-only) it is the answer, and leaving it out lost a
        // vPerson that uses Person. Under whole word both halves are word checks and run here, not in SQL: a
        // substring NOT LIKE on the name would drop a vPersonAddress whose definition uses "person" as a word,
        // though its name is no whole-word hit. The definition is only read back for that check — it can be large.
        if (options?.SearchDefinitions != false && results.Count < maxResults)
        {
            bool excludeNameMatches = options?.ExcludeNameMatchesFromDefinitions == true;
            string selectDefinition = wholeWord ? ", definition" : "";
            string excludeName = wholeWord || !excludeNameMatches ? "" : @" AND object_name NOT LIKE @pattern ESCAPE '\' COLLATE NOCASE";
            using var cmd = new SQLiteCommand(
                $@"SELECT schema_name, object_name, object_type{selectDefinition} FROM cache_objects
                  WHERE connection_key = @ck AND database_name = @db
                    AND definition LIKE @pattern ESCAPE '\' COLLATE NOCASE{excludeName}{typeClause}
                  LIMIT @limit", _conn);
            cmd.Parameters.AddWithValue("@ck", connectionKey);
            cmd.Parameters.AddWithValue("@db", database);
            cmd.Parameters.AddWithValue("@pattern", likePattern);
            cmd.Parameters.AddWithValue("@limit", SqlLimit(maxResults - results.Count));
            addTypeParams(cmd);
            using var reader = cmd.ExecuteReader();
            while (reader.Read() && results.Count < maxResults)
            {
                if (wholeWord && ((excludeNameMatches && SearchTextMatcher.Contains(reader.GetString(1), searchTerm, true))
                                  || reader.IsDBNull(3) || !SearchTextMatcher.Contains(reader.GetString(3), searchTerm, true)))
                    continue;
                results.Add(new SearchResult
                {
                    SchemaName = reader.GetString(0),
                    ObjectName = reader.GetString(1),
                    ObjectType = reader.GetString(2),
                    MatchLocation = "Definition",
                    MatchDetail = $"Found in definition"
                });
            }
        }

        return results;
    }

    public void Dispose()
    {
        _conn?.Dispose();
    }
}
