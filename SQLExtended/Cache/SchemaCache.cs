using Microsoft.Data.SqlClient;
using SQLExtended.Cache.Models;
using SQLExtended.Decryption;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SQLExtended.Cache;

/// <summary>
/// Singleton shared schema cache. Combines fast in-memory ConcurrentDictionary lookups
/// with SQLite persistence across SSMS restarts.
///
/// All lookup methods return from memory (sub-millisecond).
/// Population methods query SQL Server and update both memory and SQLite.
/// </summary>
internal sealed class SchemaCache : ISchemaCache, IDisposable
{
    private static readonly Lazy<SchemaCache> _instance = new(() => new SchemaCache());
    public static SchemaCache Instance => _instance.Value;

    // In-memory storage: connectionKey → database → data
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DatabaseCacheData>> _memoryCache = new();
    private readonly ConcurrentDictionary<string, CacheState> _stateMap = new(); // "connKey|db" → state
    private readonly ConcurrentDictionary<string, DateTime> _lastRefreshMap = new(); // "connKey|db" → last full refresh

    private SchemaCacheSqliteStore _store;
    private Timer _periodicRefreshTimer;

    /// <summary>
    /// <c>DetectDdlChanges</c>, captured whenever the settings are applied. Held as a field rather than read
    /// in <see cref="OnPeriodicRefresh"/> because that runs on a timer thread, and the settings singleton
    /// must not be faulted in from one (Diagnostics/CLAUDE.md).
    /// </summary>
    private volatile bool _detectDdlChanges = true;
    private readonly ConcurrentDictionary<string, string> _connectionStrings = new(); // connKey → connString (for periodic refresh)

    // Limits concurrent SQL Server connections during cache loads to avoid pool exhaustion.
    // Each database load uses 1 connection; this caps how many databases load simultaneously.
    private static readonly SemaphoreSlim _loadSemaphore = new(3);

    public event EventHandler<CacheRefreshEventArgs> CacheRefreshed;

    private SchemaCache() { }

    /// <summary>
    /// Initialize SQLite store and load any previously cached data into memory.
    /// Call once during package initialization.
    /// </summary>
    public void Initialize()
    {
        var settings = Settings.SQLExtendedSettings.Current;

        _store = new SchemaCacheSqliteStore();
        _store.Initialize(settings.MaxCacheAgeDays);

        // Load previously cached databases from SQLite into memory
        var cached = _store.GetCachedDatabases();
        foreach (var (connKey, db) in cached)
        {
            try
            {
                var data = _store.LoadDatabase(connKey, db);
                if (data != null)
                {
                    StampCacheData(connKey, db, data);
                    SetState(connKey, db, CacheState.Stale); // Mark as stale until refreshed
                }
            }
            catch
            {
                // Skip corrupt cache entries
            }
        }

        ApplyRefreshSettings();
        Settings.SQLExtendedSettings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged(object sender, EventArgs e) => ApplyRefreshSettings();

    /// <summary>
    /// Arms (or disarms) the periodic incremental refresh from <c>AutoRefreshIntervalMinutes</c>, and takes a
    /// copy of <c>DetectDdlChanges</c> for the timer thread to read.
    ///
    /// <para>Re-applied on every settings save rather than only at startup, because the dialog does not say
    /// "applies on the next start" for these two - it says "set to 0 to disable", and a disable that needs an
    /// SSMS restart to take effect is the same silence this whole pass is about. An interval of 0 disposes
    /// the timer outright; explicit and connection-triggered refreshes are untouched either way.</para>
    /// </summary>
    private void ApplyRefreshSettings()
    {
        var settings = Settings.SQLExtendedSettings.Current;
        _detectDdlChanges = settings.DetectDdlChanges;

        _periodicRefreshTimer?.Dispose();
        _periodicRefreshTimer = null;

        int minutes = settings.AutoRefreshIntervalMinutes;
        if (minutes <= 0)
            return;

        var period = TimeSpan.FromMinutes(minutes);
        _periodicRefreshTimer = new Timer(OnPeriodicRefresh, null, period, period);
    }

    // --- State ---

    public CacheState GetState(string connectionKey, string database)
    {
        string key = $"{connectionKey}|{database}";
        return _stateMap.TryGetValue(key, out var state) ? state : CacheState.NotLoaded;
    }

    private void SetState(string connectionKey, string database, CacheState state)
    {
        string key = $"{connectionKey}|{database}";
        _stateMap[key] = state;
    }

    public int GetObjectCount(string connectionKey, string database)
    {
        if (_memoryCache.TryGetValue(connectionKey, out var dbs) &&
            dbs.TryGetValue(database, out var data))
            return data.Objects.Count;
        return 0;
    }

    // --- Population ---

    public async Task LoadDatabaseAsync(string connectionString, string database, bool forceFullRefresh = false)
    {
        string connKey = GetConnectionKey(connectionString);
        string effectiveConnStr = ConnectionHelper.GetConnectionStringForDatabase(connectionString, database);

        // Remember the connection string for periodic refresh
        _connectionStrings[connKey + "|" + database] = effectiveConnStr;

        // Check if we already have fresh data (unless forced)
        if (!forceFullRefresh && GetState(connKey, database) == CacheState.Ready)
            return;

        SetState(connKey, database, CacheState.Loading);

        await _loadSemaphore.WaitAsync();
        try
        {
            var data = await SchemaCacheLoader.LoadFullAsync(effectiveConnStr, database);

            // Stamp connection/database info on all objects
            StampCacheData(connKey, database, data);

            SetState(connKey, database, CacheState.Ready);
            _lastRefreshMap[$"{connKey}|{database}"] = DateTime.UtcNow;

            // Persist to SQLite (fire-and-forget on background thread)
            _ = Task.Run(() =>
            {
                try { _store?.SaveDatabase(connKey, database, data); }
                catch { /* SQLite write failure is non-critical */ }
            });

            CacheRefreshed?.Invoke(this, new CacheRefreshEventArgs(connKey, database, CacheState.Ready, data.Objects.Count));
        }
        catch (Exception ex)
        {
            // The state is all the UI gets — "Error loading schema" in the cache window, "Schema: Error" on
            // the toolbar — and neither can say why. This is the only record of the reason, and for a server
            // that cannot be connected to at all (an Azure database behind an auth mode the harvested
            // connection string cannot express, a firewall, a missing permission) it is the whole diagnosis.
            Diagnostics.SQLExtendedLog.Error("SchemaCache", $"Full load failed for {connKey}/{database}", ex);

            SetState(connKey, database, CacheState.Error);
            CacheRefreshed?.Invoke(this, new CacheRefreshEventArgs(connKey, database, CacheState.Error, 0));
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    public async Task IncrementalRefreshAsync(string connectionString, string database)
    {
        string connKey = GetConnectionKey(connectionString);
        string effectiveConnStr = ConnectionHelper.GetConnectionStringForDatabase(connectionString, database);
        string dbKey = $"{connKey}|{database}";

        if (!_lastRefreshMap.TryGetValue(dbKey, out var lastRefresh))
        {
            // No previous refresh — do a full load instead
            await LoadDatabaseAsync(connectionString, database, forceFullRefresh: true);
            return;
        }

        await _loadSemaphore.WaitAsync();
        try
        {
            var modifiedObjects = await Task.Run(() =>
                SchemaCacheLoader.LoadModifiedSince(effectiveConnStr, lastRefresh));

            if (modifiedObjects.Count == 0)
                return;

            // Update in-memory cache
            if (_memoryCache.TryGetValue(connKey, out var dbs) &&
                dbs.TryGetValue(database, out var data))
            {
                foreach (var obj in modifiedObjects)
                {
                    obj.ConnectionKey = connKey;
                    obj.DatabaseName = database;

                    // Replace or add the object
                    var existing = data.Objects.FindIndex(o =>
                        o.SchemaName == obj.SchemaName && o.ObjectName == obj.ObjectName);
                    if (existing >= 0)
                        data.Objects[existing] = obj;
                    else
                        data.Objects.Add(obj);
                }
            }

            _lastRefreshMap[dbKey] = DateTime.UtcNow;
            CacheRefreshed?.Invoke(this, new CacheRefreshEventArgs(
                connKey, database, CacheState.Ready, GetObjectCount(connKey, database)));
        }
        catch (Exception ex)
        {
            // Non-critical — the previous load's data stays served — but a refresh that has been failing
            // silently for an hour is why the cache looks stale.
            Diagnostics.SQLExtendedLog.Warning("SchemaCache", $"Incremental refresh failed for {connKey}/{database}", ex);
        }
        finally
        {
            _loadSemaphore.Release();
        }
    }

    /// <summary>
    /// Triggers a full refresh for all known connection+database pairs. Fire-and-forget.
    /// </summary>
    public void RefreshAllAsync()
    {
        foreach (var kvp in _connectionStrings)
        {
            try
            {
                var parts = kvp.Key.Split('|');
                if (parts.Length != 2) continue;
                string database = parts[1];
                string connStr = kvp.Value;
                _ = LoadDatabaseAsync(connStr, database, forceFullRefresh: true);
            }
            catch { }
        }
    }

    public void ClearDatabase(string connectionKey, string database)
    {
        if (_memoryCache.TryGetValue(connectionKey, out var dbs))
            dbs.TryRemove(database, out _);
        _stateMap.TryRemove($"{connectionKey}|{database}", out _);
        _lastRefreshMap.TryRemove($"{connectionKey}|{database}", out _);
        _connectionStrings.TryRemove($"{connectionKey}|{database}", out _);
        _store?.ClearDatabase(connectionKey, database);
    }

    public void ClearAll()
    {
        _memoryCache.Clear();
        _stateMap.Clear();
        _lastRefreshMap.Clear();
        _connectionStrings.Clear();
        _store?.ClearAll();

        // Decrypted module text is memoised separately, keyed by object version rather than by database, so
        // clearing the cache has to say so explicitly — otherwise "Clear All Cache" would leave the one kind
        // of definition a user might most want re-read behind.
        ModuleDecryptionService.ClearCache();

        // Same reasoning for the system catalog: it is keyed per server rather than per database,
        // so nothing above touches it — and it memoises servers whose catalog could not be read,
        // which makes this the only way to retry one in the same session.
        SystemCatalogCache.Instance.Clear();

        // And for linked servers, which memoise failures per remote server the same way.
        LinkedServerCache.Instance.Clear();
    }

    // --- Lookups (all from in-memory cache) ---

    public IReadOnlyList<CachedDatabase> GetDatabases(string connectionKey)
    {
        if (!_memoryCache.TryGetValue(connectionKey, out var dbs))
            return Array.Empty<CachedDatabase>();

        return dbs.Keys.Select(name => new CachedDatabase { Name = name }).ToList();
    }

    public IReadOnlyList<CachedObject> GetObjects(string connectionKey, string database, string schema = null, string typeFilter = null)
    {
        var data = GetDatabaseData(connectionKey, database);
        if (data == null) return Array.Empty<CachedObject>();

        IEnumerable<CachedObject> objects = data.Objects;
        if (!string.IsNullOrEmpty(schema))
            objects = objects.Where(o => string.Equals(o.SchemaName, schema, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(typeFilter))
            objects = objects.Where(o => string.Equals(o.ObjectType, typeFilter, StringComparison.OrdinalIgnoreCase));

        return objects.ToList();
    }

    public CachedObject FindObject(string connectionKey, string database, string schema, string name)
    {
        var data = GetDatabaseData(connectionKey, database);
        if (data == null) return null;

        if (!string.IsNullOrEmpty(schema))
        {
            return data.Objects.FirstOrDefault(o =>
                string.Equals(o.SchemaName, schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.ObjectName, name, StringComparison.OrdinalIgnoreCase));
        }

        // No schema specified — prefer dbo, then first match
        return data.Objects.FirstOrDefault(o =>
                string.Equals(o.ObjectName, name, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.SchemaName, "dbo", StringComparison.OrdinalIgnoreCase))
            ?? data.Objects.FirstOrDefault(o =>
                string.Equals(o.ObjectName, name, StringComparison.OrdinalIgnoreCase));
    }

    public (string Database, string Schema, string Name)? ResolveSynonym(string connectionKey, string database, string schema, string name)
    {
        var obj = FindObject(connectionKey, database, schema, name);
        if (obj?.ObjectType?.Trim() != "SN")
            return null;
        var target = SynonymTarget.Parse(obj.BaseObjectName);
        if (target == null || target.Server != null)
            return null;
        return (target.Database ?? database, target.Schema ?? "dbo", target.Name);
    }

    // Columns, indexes and parameters follow a synonym to its target when the name has none of its own: "FROM dbo.Syn s
    // WHERE s." should list the base table's columns, and "EXEC dbo.SynProc" its parameters. A synonym cannot point at
    // another synonym, so one hop is all there is. A target in a database not yet cached just yields nothing.
    public IReadOnlyList<CachedColumn> GetColumns(string connectionKey, string database, string schema, string tableName)
    {
        var columns = GetOwnColumns(connectionKey, database, schema, tableName);
        return columns.Count == 0 && ResolveSynonym(connectionKey, database, schema, tableName) is { } t
            ? GetOwnColumns(connectionKey, t.Database, t.Schema, t.Name)
            : columns;
    }

    private IReadOnlyList<CachedColumn> GetOwnColumns(string connectionKey, string database, string schema, string tableName)
    {
        var data = GetDatabaseData(connectionKey, database);
        if (data == null) return Array.Empty<CachedColumn>();

        return data.Columns.Where(c =>
            string.Equals(c.SchemaName, schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.TableName, tableName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Ordinal)
            .ToList();
    }

    public IReadOnlyList<CachedIndex> GetIndexes(string connectionKey, string database, string schema, string tableName)
    {
        var indexes = GetOwnIndexes(connectionKey, database, schema, tableName);
        return indexes.Count == 0 && ResolveSynonym(connectionKey, database, schema, tableName) is { } t
            ? GetOwnIndexes(connectionKey, t.Database, t.Schema, t.Name)
            : indexes;
    }

    private IReadOnlyList<CachedIndex> GetOwnIndexes(string connectionKey, string database, string schema, string tableName)
    {
        var data = GetDatabaseData(connectionKey, database);
        if (data == null) return Array.Empty<CachedIndex>();

        return data.Indexes.Where(i =>
            string.Equals(i.SchemaName, schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(i.TableName, tableName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public IReadOnlyList<CachedForeignKey> GetForeignKeys(string connectionKey, string database, string schema, string tableName)
    {
        var data = GetDatabaseData(connectionKey, database);
        if (data == null) return Array.Empty<CachedForeignKey>();

        return data.ForeignKeys.Where(fk =>
            string.Equals(fk.SchemaName, schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(fk.TableName, tableName, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public IReadOnlyList<CachedForeignKey> GetReferencingForeignKeys(string connectionKey, string database, string schema, string tableName)
    {
        var data = GetDatabaseData(connectionKey, database);
        if (data == null) return Array.Empty<CachedForeignKey>();

        // Schema is only compared when both sides name one. A parsed reference usually omits it, and
        // requiring a match there would drop every incoming key for a table written unqualified - which
        // on screen is a table that simply has no relationships rather than a lookup that did not apply.
        return data.ForeignKeys.Where(fk =>
            string.Equals(fk.ReferencedTable, tableName, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(schema) || string.IsNullOrEmpty(fk.ReferencedSchema) ||
             string.Equals(fk.ReferencedSchema, schema, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public IReadOnlyList<CachedParameter> GetParameters(string connectionKey, string database, string schema, string objectName)
    {
        var parameters = GetOwnParameters(connectionKey, database, schema, objectName);
        return parameters.Count == 0 && ResolveSynonym(connectionKey, database, schema, objectName) is { } t
            ? GetOwnParameters(connectionKey, t.Database, t.Schema, t.Name)
            : parameters;
    }

    private IReadOnlyList<CachedParameter> GetOwnParameters(string connectionKey, string database, string schema, string objectName)
    {
        var data = GetDatabaseData(connectionKey, database);
        if (data == null) return Array.Empty<CachedParameter>();

        return data.Parameters.Where(p =>
            string.Equals(p.SchemaName, schema, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.ObjectName, objectName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Ordinal)
            .ToList();
    }

    // --- Search ---

    public IReadOnlyList<SearchResult> Search(string connectionKey, string database, string searchTerm, SearchOptions options = null)
    {
        options ??= new SearchOptions();
        var results = new List<SearchResult>();

        // In-memory search for object names (case-insensitive substring, or whole word)
        var data = GetDatabaseData(connectionKey, database);
        if (data != null)
        {
            // Parse comma-separated type filter once
            HashSet<string> typeFilters = null;
            if (!string.IsNullOrEmpty(options.TypeFilter))
            {
                typeFilters = new HashSet<string>(
                    options.TypeFilter.Split(','),
                    StringComparer.OrdinalIgnoreCase);
            }

            if (options.SearchObjectNames)
            {
                foreach (var obj in data.Objects)
                {
                    if (SearchTextMatcher.Contains(obj.ObjectName, searchTerm, options.WholeWord))
                    {
                        if (typeFilters != null && !typeFilters.Contains(obj.ObjectType))
                            continue;

                        results.Add(new SearchResult
                        {
                            SchemaName = obj.SchemaName,
                            ObjectName = obj.ObjectName,
                            ObjectType = obj.ObjectType,
                            MatchLocation = "ObjectName",
                            MatchDetail = obj.ObjectName
                        });
                        if (results.Count >= options.MaxResults) return results;
                    }
                }
            }

            if (options.SearchColumnNames)
            {
                // Parent object types - for the type filter and for the "COLUMN · VIEW" label. Built on the first
                // matching column rather than up front, so a search that matches no column pays nothing for it.
                Dictionary<(string schema, string name), string> objectTypes = null;

                foreach (var col in data.Columns)
                {
                    if (SearchTextMatcher.Contains(col.ColumnName, searchTerm, options.WholeWord))
                    {
                        if (objectTypes == null)
                        {
                            objectTypes = new Dictionary<(string, string), string>(data.Objects.Count, EqualityComparer<(string, string)>.Default);
                            foreach (var obj in data.Objects)
                                objectTypes[(obj.SchemaName, obj.ObjectName)] = obj.ObjectType;
                        }

                        objectTypes.TryGetValue((col.SchemaName, col.TableName), out var parentType);
                        if (typeFilters != null && parentType != null && !typeFilters.Contains(parentType))
                            continue;

                        results.Add(new SearchResult
                        {
                            SchemaName = col.SchemaName,
                            ObjectName = col.TableName,
                            ObjectType = "Column",
                            MatchLocation = "ColumnName",
                            MatchDetail = col.ColumnName,
                            ParentObjectType = parentType
                        });
                        if (results.Count >= options.MaxResults) return results;
                    }
                }
            }
        }

        // For definition search, delegate to SQLite (has the full text indexed)
        if (options.SearchDefinitions && _store != null)
        {
            try
            {
                var storeResults = _store.Search(connectionKey, database, searchTerm, new SearchOptions
                {
                    SearchObjectNames = false,
                    SearchColumnNames = false,
                    SearchDefinitions = true,
                    TypeFilter = options.TypeFilter,
                    WholeWord = options.WholeWord,
                    ExcludeNameMatchesFromDefinitions = options.SearchObjectNames,
                    MaxResults = options.MaxResults - results.Count
                });
                results.AddRange(storeResults);
            }
            catch
            {
                // SQLite search failure is non-critical
            }
        }

        return results;
    }

    // --- Connection key helpers ---

    public string GetConnectionKey(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            return builder.DataSource?.ToLowerInvariant() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Returns all known connection keys and their associated connection strings.
    /// Used by ObjectExplorerHelper to enumerate cached servers.
    /// </summary>
    public List<(string ConnectionKey, string ConnectionString)> GetKnownConnectionKeys()
    {
        var result = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in _connectionStrings)
        {
            // Keys are "connKey|database", extract the connKey part
            string key = kvp.Key;
            int pipe = key.IndexOf('|');
            string connKey = pipe >= 0 ? key.Substring(0, pipe) : key;

            if (seen.Add(connKey))
                result.Add((connKey, kvp.Value));
        }

        return result;
    }

    /// <summary>
    /// Returns a snapshot of every database currently held in the cache, grouped (by the caller)
    /// per server. Unions the in-memory store (authoritative for what's actually cached, including
    /// data hydrated from SQLite at startup) with the state map (which also tracks databases that
    /// are mid-load or errored before any objects land in memory).
    /// </summary>
    public IReadOnlyList<CacheSnapshotEntry> GetCacheSnapshot()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // "connKey|db"

        foreach (var connKvp in _memoryCache)
            foreach (var db in connKvp.Value.Keys)
                keys.Add($"{connKvp.Key}|{db}");

        foreach (var stateKey in _stateMap.Keys)
            keys.Add(stateKey);

        var result = new List<CacheSnapshotEntry>(keys.Count);
        foreach (var key in keys)
        {
            int pipe = key.IndexOf('|');
            if (pipe <= 0 || pipe == key.Length - 1) continue;

            string connKey = key.Substring(0, pipe);
            string database = key.Substring(pipe + 1);

            var objects = GetObjects(connKey, database);

            result.Add(new CacheSnapshotEntry
            {
                ConnectionKey = connKey,
                Database = database,
                State = GetState(connKey, database),
                ObjectCount = objects.Count,
                ObjectTypeCounts = BuildTypeCounts(objects),
                LastRefreshUtc = _lastRefreshMap.TryGetValue(key, out var t) ? t : (DateTime?)null,
                ConnectionString = _connectionStrings.TryGetValue(key, out var cs) ? cs : null
            });
        }

        return result;
    }

    // Maps sys.objects type codes to display categories, in the order they should appear.
    private static readonly (string Label, string[] Types)[] _typeGroups =
    {
        ("Tables", new[] { "U" }),
        ("Views", new[] { "V" }),
        ("Procedures", new[] { "P" }),
        ("Functions", new[] { "FN", "IF", "TF" }),
        ("Synonyms", new[] { "SN" }),
        ("Table types", new[] { "TT" }),
    };

    private static IReadOnlyList<ObjectTypeCount> BuildTypeCounts(IReadOnlyList<CachedObject> objects)
    {
        if (objects == null || objects.Count == 0)
            return Array.Empty<ObjectTypeCount>();

        var byType = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var obj in objects)
        {
            string type = obj.ObjectType?.Trim() ?? "";
            byType.TryGetValue(type, out int n);
            byType[type] = n + 1;
        }

        var result = new List<ObjectTypeCount>();
        var accounted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (label, types) in _typeGroups)
        {
            int count = 0;
            foreach (var t in types)
            {
                if (byType.TryGetValue(t, out int n)) count += n;
                accounted.Add(t);
            }
            if (count > 0)
                result.Add(new ObjectTypeCount { Label = label, Count = count });
        }

        // Anything not in a known group (defensive — keeps the breakdown total honest).
        int other = byType.Where(kv => !accounted.Contains(kv.Key)).Sum(kv => kv.Value);
        if (other > 0)
            result.Add(new ObjectTypeCount { Label = "Other", Count = other });

        return result;
    }

    // --- Private helpers ---

    private DatabaseCacheData GetDatabaseData(string connectionKey, string database)
    {
        if (_memoryCache.TryGetValue(connectionKey, out var dbs) &&
            dbs.TryGetValue(database, out var data))
            return data;
        return null;
    }

    private void StampCacheData(string connectionKey, string database, DatabaseCacheData data)
    {
        foreach (var obj in data.Objects)
        {
            obj.ConnectionKey = connectionKey;
            obj.DatabaseName = database;
        }

        var dbs = _memoryCache.GetOrAdd(connectionKey, _ => new ConcurrentDictionary<string, DatabaseCacheData>(StringComparer.OrdinalIgnoreCase));
        dbs[database] = data;
    }

    private void OnPeriodicRefresh(object state)
    {
        // "Detect DDL changes ... and auto-refresh affected objects" is exactly what an incremental refresh
        // is - LoadModifiedSince against modify_date - so off, there is nothing for this tick to do. The
        // timer is left running: the setting can be turned back on without an SSMS restart.
        if (!_detectDdlChanges)
            return;

        foreach (var kvp in _connectionStrings)
        {
            try
            {
                var parts = kvp.Key.Split('|');
                if (parts.Length != 2) continue;
                string connKey = parts[0];
                string database = parts[1];
                string connStr = kvp.Value;

                if (GetState(connKey, database) == CacheState.Ready)
                {
                    // Fire-and-forget incremental refresh
                    _ = IncrementalRefreshAsync(connStr, database);
                }
            }
            catch
            {
                // Periodic refresh failure is non-critical
            }
        }
    }

    public void Dispose()
    {
        Settings.SQLExtendedSettings.Changed -= OnSettingsChanged;
        _periodicRefreshTimer?.Dispose();
        _store?.Dispose();
    }
}
