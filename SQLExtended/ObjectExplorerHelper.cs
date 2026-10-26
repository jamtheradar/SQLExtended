using Microsoft.Data.SqlClient;
using Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer;
using Microsoft.VisualStudio.Shell;
using SQLExtended.Cache;
using SQLExtended.ObjectExplorer;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace SQLExtended;

/// <summary>
/// Enumerates connected servers and databases from the SSMS Object Explorer.
/// Uses IObjectExplorerService from SqlWorkbench.Interfaces.dll (undocumented SSMS internal).
/// Falls back to cached connection keys when Object Explorer access fails.
/// </summary>
internal static class ObjectExplorerHelper
{
    /// <summary>
    /// Information about a connected server visible in Object Explorer.
    /// </summary>
    internal sealed class ServerInfo
    {
        public string ServerName { get; set; }
        public string ConnectionString { get; set; }
        public string DisplayName => ServerName ?? "Unknown";
    }

    /// <summary>
    /// The kind of Object Explorer node, derived from <see cref="INodeInformation.UrnPath"/>.
    /// </summary>
    internal enum NodeKind { Unknown, Server, Database, Table, View, JobsFolder, Job, AlwaysOn, Replication }

    /// <summary>
    /// Parsed context for a right-clicked Object Explorer node: its kind, the server/database/object
    /// it refers to, and a connection string targeting that database.
    /// </summary>
    internal sealed class NodeContext
    {
        public NodeKind Kind { get; set; }
        public string Server { get; set; } = "";
        public string Database { get; set; } = "";
        public string Schema { get; set; } = "";
        public string ObjectName { get; set; } = "";
        public string ConnectionString { get; set; }

        /// <summary>schema.name (or just name when no schema), for display / scripting.</summary>
        public string QualifiedObjectName =>
            string.IsNullOrEmpty(Schema) ? ObjectName : $"{Schema}.{ObjectName}";
    }

    /// <summary>
    /// Builds a <see cref="NodeContext"/> from an Object Explorer node. Parses the node's
    /// <see cref="INodeInformation.UrnPath"/> (node kind) and <see cref="INodeInformation.NavigationContext"/>
    /// (an XPath-like URN carrying the server/database/object names), then resolves a connection string
    /// targeting the node's database by reusing <see cref="ExtractServerInfoFromINode"/>.
    /// Returns null if the node is null or its kind isn't one we act on.
    /// </summary>
    internal static NodeContext GetNodeContext(INodeInformation node)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (node == null) return null;

        try
        {
            var ctx = new NodeContext { Kind = KindFromUrnPath(node.UrnPath) };
            if (ctx.Kind == NodeKind.Unknown)
                return null;

            // NavigationContext looks like: Server[@Name='S']/Database[@Name='D']/Table[@Name='T' and @Schema='dbo']
            string nav = node.NavigationContext ?? "";
            foreach (string part in nav.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("Server[@Name='", StringComparison.Ordinal))
                    ctx.Server = StripUrnValue(part, "Server[@Name='");
                else if (part.StartsWith("Database[@Name='", StringComparison.Ordinal))
                    ctx.Database = StripUrnValue(part, "Database[@Name='");
                else if (part.StartsWith("Table[@Name='", StringComparison.Ordinal))
                    (ctx.ObjectName, ctx.Schema) = SplitNameAndSchema(part, "Table[@Name='");
                else if (part.StartsWith("View[@Name='", StringComparison.Ordinal))
                    (ctx.ObjectName, ctx.Schema) = SplitNameAndSchema(part, "View[@Name='");
            }

            // Resolve a connection string for the node's server, then point it at the node's database.
            var serverInfo = ExtractServerInfoFromINode(node);
            string serverConn = serverInfo?.ConnectionString;
            if (string.IsNullOrEmpty(serverConn))
            {
                try { serverConn = ConnectionHelper.GetActiveConnectionString(); } catch { }
            }
            if (string.IsNullOrEmpty(ctx.Server) && serverInfo != null)
                ctx.Server = serverInfo.ServerName ?? "";

            ctx.ConnectionString = string.IsNullOrEmpty(ctx.Database)
                ? serverConn
                : ConnectionHelper.GetConnectionStringForDatabase(serverConn, ctx.Database);

            return ctx;
        }
        catch
        {
            return null;
        }
    }

    private static NodeKind KindFromUrnPath(string urnPath)
    {
        // UrnPath is a slash-delimited type path, e.g. "Server", "Server/Database", "Server/Database/Table".
        string path = (urnPath ?? "").Trim();

        switch (path)
        {
            case "Server": return NodeKind.Server;
            case "Server/Database": return NodeKind.Database;
            case "Server/Database/Table": return NodeKind.Table;
            case "Server/Database/View": return NodeKind.View;
            // The Agent subtree hangs off JobServer, not Database — the Jobs folder and a single job both
            // give the Agent Jobs dashboard something to open against.
            case "Server/JobServer/JobsFolder": return NodeKind.JobsFolder;
            case "Server/JobServer/Job": return NodeKind.Job;
        }

        // The Always On and replication subtrees are matched by substring rather than by an exact path, on purpose.
        // Both are reached through *folder* nodes, and a folder contributes a segment to UrnPath that is not stated
        // anywhere readable: SSMS's own hierarchy XML (ObjectExplorer.dll, embedded sqlexplorerhier.xml and
        // objectexplorerreplication.xml) declares `<Object name='AvailabilityGroups' base='Folder'>` and
        // `<Object name='Replication' base='Folder'>` with no UniqueName at all, so the exact spelling of the
        // segment is decided by the node builder at runtime and has varied. Matching on the distinctive word means
        // every node in the subtree — the folder, a group, a publication, a subscription — offers the right
        // dashboard, and an SSMS version that renames a folder does not silently drop the menu.
        //
        // These run after the exact matches so they can never shadow a more specific kind, and each word is
        // specific enough not to collide: no server, database, table or Agent node path contains either.
        if (path.IndexOf("AvailabilityGroup", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf("AvailabilityDatabase", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf("AlwaysOn", StringComparison.OrdinalIgnoreCase) >= 0)
            return NodeKind.AlwaysOn;

        if (path.IndexOf("Replication", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf("Publication", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf("Subscription", StringComparison.OrdinalIgnoreCase) >= 0)
            return NodeKind.Replication;

        return NodeKind.Unknown;
    }

    private static string StripUrnValue(string part, string prefix) =>
        part.Substring(prefix.Length).TrimEnd(']', '\'');

    private static (string Name, string Schema) SplitNameAndSchema(string part, string prefix)
    {
        // e.g. Table[@Name='Orders' and @Schema='dbo']
        string inner = part.Substring(prefix.Length);
        string[] halves = inner.Split(new[] { "' and @Schema='" }, StringSplitOptions.None);
        string name = halves.Length > 0 ? halves[0].TrimEnd(']', '\'') : "";
        string schema = halves.Length > 1 ? halves[1].TrimEnd(']', '\'') : "";
        return (name, schema);
    }

    /// <summary>
    /// Returns all servers currently connected in Object Explorer.
    /// Must be called on the UI thread.
    /// Falls back to the schema cache's known connections if Object Explorer access fails.
    /// </summary>
    public static List<ServerInfo> GetConnectedServers()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var servers = TryGetFromObjectExplorer();
        if (servers != null && servers.Count > 0)
            return servers;

        // Fallback: build from cached connection keys
        return GetServersFromCache();
    }

    /// <summary>
    /// Queries sys.databases on the given server to get a list of all user databases.
    /// Runs synchronously — call from a background thread for large servers.
    ///
    /// <para>The cache's own databases go in first and the server's list is merged over them, so a server
    /// that cannot be reached still returns something usable. <b>That fallback is also the hazard.</b> Every
    /// consumer of this — "cache all databases" in the Schema Cache window, SQL Search's all-databases
    /// scope, Schema Validation — reads what comes back as the whole server, and a short list looks exactly
    /// like a complete one. On Azure SQL Database a contained user cannot connect to master at all, which
    /// makes the short list the normal case there rather than an edge one. So each stage reports into
    /// <see cref="Diagnostics.SQLExtendedLog"/>, and the failure line says how much of the list is missing.</para>
    ///
    /// <para>Databases this login cannot open are excluded, as they are in the completion path's own
    /// enumeration (<c>SqlCompletionSource.GetDatabaseNames</c>) — listing one costs a cache load per
    /// database that fails on connect, which reads as the server being broken rather than as a database
    /// nobody granted. The difference here is that <c>HAS_DBACCESS</c> is <b>selected rather than filtered
    /// on</b>, so the ones left out can be counted and reported instead of quietly going missing.</para>
    ///
    /// <para>The cached names merged in above are not access-checked — the cache is per server and persists
    /// across sessions, so it can hold a database an earlier login could open and this one cannot.</para>
    /// </summary>
    public static List<string> GetDatabases(string connectionString)
    {
        var databases = new List<string>();
        var cache = SchemaCache.Instance;
        string connKey = cache.GetConnectionKey(connectionString);
        int fromCache = 0;

        // First check the cache for known databases.
        try
        {
            var cachedDbs = cache.GetDatabases(connKey);
            if (cachedDbs != null && cachedDbs.Count > 0)
            {
                foreach (var db in cachedDbs)
                {
                    databases.Add(db.Name);
                    fromCache++;
                }
            }
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Warning("ObjectExplorer", $"Could not read the cached database list for {connKey}", ex);
        }

        // Also query sys.databases for the complete list. Its own try/catch, and not because it is tidier:
        // sharing one with the read above made "the server refused us" and "the cache was empty"
        // indistinguishable, and it put the reader loop inside the same handler — so a failure part way
        // through the rows truncated the list silently instead of reporting it.
        try
        {
            string masterConnStr = GetMasterConnectionString(connectionString);

            if (Diagnostics.SQLExtendedLog.Enabled && !PointsAtMaster(masterConnStr))
            {
                // GetMasterConnectionString hands back its input when the string will not parse, so the
                // enumeration quietly runs against whatever database the caller was already on. On a box
                // instance that still answers for the whole server; on Azure SQL Database it answers for
                // master plus that one database, which is a plausible-looking and much shorter list.
                Diagnostics.SQLExtendedLog.Warning("ObjectExplorer",
                    $"Could not point the database enumeration at master for {connKey}; it is running against the connection's own database instead.");
            }

            int totalOnline = 0, noAccess = 0, added = 0;

            using (var conn = SqlConnectionFactory.Create(masterConnStr))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    // HAS_DBACCESS is selected rather than pushed into the WHERE clause, which is what the
                    // completion path's enumeration does. Filtering server-side would be shorter and would
                    // silently shrink the list — and a short list that looks complete is the failure mode this
                    // codebase keeps running into. Read as a column, the rows this login cannot open can be
                    // counted and said out loud below.
                    //
                    // It returns NULL for a database that is not in a state to be opened, so ISNULL is the
                    // difference between "no access" and no row at all. The ONLINE filter already covers most
                    // of that; nothing guarantees the two predicates are evaluated in the order they are written.
                    cmd.CommandText = @"
                        SELECT name, ISNULL(HAS_DBACCESS(name), 0) AS has_access
                        FROM sys.databases
                        WHERE state_desc = 'ONLINE'
                          AND database_id > 4  -- exclude system databases
                        ORDER BY name";
                    cmd.CommandTimeout = 10;

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string name = reader.GetString(0);
                            totalOnline++;

                            if (Convert.ToInt32(reader.GetValue(1)) != 1)
                            {
                                // Listing it costs one cache load per database that fails on connect, which
                                // reads as the server being broken rather than as a database this login was
                                // never granted.
                                noAccess++;
                                continue;
                            }

                            if (!databases.Contains(name))
                            {
                                databases.Add(name);
                                added++;
                            }
                        }
                    }
                }
            }

            int accessible = totalOnline - noAccess;

            if (totalOnline == 0)
            {
                // A server with no user databases and a login that can reach master but see nothing in it
                // produce the same empty grid, and every all-databases action then does nothing at all.
                Diagnostics.SQLExtendedLog.Warning("ObjectExplorer",
                    $"master on {connKey} listed no user databases (ONLINE, database_id > 4). Every all-databases action will find nothing.");
            }
            else if (accessible == 0)
            {
                Diagnostics.SQLExtendedLog.Warning("ObjectExplorer",
                    $"All {totalOnline} database(s) on {connKey} are online but none can be opened by this login. Every all-databases action will find nothing.");
            }
            else
            {
                string skipped = noAccess == 0 ? "" : $", {noAccess} skipped as not open to this login";
                Diagnostics.SQLExtendedLog.Info("ObjectExplorer",
                    $"Enumerated {accessible} of {totalOnline} database(s) on {connKey} — {added} new, {fromCache} already known from the cache{skipped}.");
            }
        }
        catch (Exception ex)
        {
            // Returning the cached list is deliberate: something usable beats nothing. But the caller cannot
            // tell a partial list from a complete one, so this line is the only place the difference exists.
            Diagnostics.SQLExtendedLog.Warning("ObjectExplorer",
                databases.Count == 0
                    ? $"Could not enumerate databases on {connKey} (connecting to master), and nothing was cached — every all-databases action will find nothing."
                    : $"Could not enumerate databases on {connKey} (connecting to master); falling back to the {databases.Count} database(s) already cached, which may not be all of them.",
                ex);
        }

        databases.Sort(StringComparer.OrdinalIgnoreCase);
        return databases;
    }

    /// <summary>
    /// Whether <paramref name="connectionString"/> actually names master. Compared on the parsed catalog
    /// rather than on the text, since the builder normalises what it was given and a string that already
    /// said master in another spelling would otherwise read as a failed rewrite.
    /// </summary>
    private static bool PointsAtMaster(string connectionString)
    {
        try
        {
            return string.Equals(new SqlConnectionStringBuilder(connectionString).InitialCatalog, "master", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the SSMS <see cref="IObjectExplorerService"/> via ServiceCacheProxy with a
    /// Package.GetGlobalService fallback. Returns null if Object Explorer isn't available yet.
    /// </summary>
    internal static IObjectExplorerService GetObjectExplorerService()
    {
        IObjectExplorerService oeService = null;
        try { oeService = ServiceCacheProxy.GetService(typeof(IObjectExplorerService)) as IObjectExplorerService; }
        catch { }
        if (oeService == null)
            try { oeService = Package.GetGlobalService(typeof(IObjectExplorerService)) as IObjectExplorerService; }
            catch { }
        return oeService;
    }

    /// <summary>
    /// Returns the Object Explorer's underlying WinForms TreeView (boxed) via reflection,
    /// or null if the service or tree can't be found.
    /// </summary>
    internal static object GetObjectExplorerTree()
    {
        var oeService = GetObjectExplorerService();
        return oeService == null ? null : FindTreeView(oeService);
    }

    private static List<ServerInfo> TryGetFromObjectExplorer()
    {
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            IObjectExplorerService oeService = GetObjectExplorerService();
            if (oeService == null)
                return null;

            var servers = new List<ServerInfo>();

            // The IObjectExplorerService interface has no "get all connected servers" method.
            // We reflect into the concrete implementation to find the underlying TreeView control.
            // The OE TreeView's root nodes are the connected server nodes.
            try
            {
                var treeView = FindTreeView(oeService);
                if (treeView != null)
                {
                    // TreeView.Nodes contains the root-level nodes (one per connected server)
                    var nodesProperty = treeView.GetType().GetProperty("Nodes",
                        BindingFlags.Instance | BindingFlags.Public);
                    var nodes = nodesProperty?.GetValue(treeView);

                    if (nodes is System.Collections.IEnumerable enumerable)
                        AddServerInfos(enumerable, servers);
                }
            }
            catch { }

            // Fallback: at least get the currently selected server
            if (servers.Count == 0)
            {
                try
                {
                    oeService.GetSelectedNodes(out int count, out INodeInformation[] nodes);
                    if (nodes != null)
                    {
                        foreach (var node in nodes)
                        {
                            var info = ExtractServerInfoFromINode(node);
                            if (info != null && !servers.Exists(s =>
                                string.Equals(s.ServerName, info.ServerName, StringComparison.OrdinalIgnoreCase)))
                                servers.Add(info);
                        }
                    }
                }
                catch { }
            }

            return servers.Count > 0 ? servers : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Adds a ServerInfo for each real server node, descending through any SQLExtended server-group
    /// folders (<see cref="ServerGroupFolderService"/>) so grouped servers are still enumerated.
    /// </summary>
    private static void AddServerInfos(System.Collections.IEnumerable nodes, List<ServerInfo> servers)
    {
        foreach (var treeNode in nodes)
        {
            if (IsGroupFolderNode(treeNode))
            {
                if (EnumerateChildren(treeNode) is System.Collections.IEnumerable children)
                    AddServerInfos(children, servers);
                continue;
            }

            var info = ExtractServerInfoFromTreeNode(treeNode);
            if (info != null && !servers.Exists(s =>
                string.Equals(s.ServerName, info.ServerName, StringComparison.OrdinalIgnoreCase)))
                servers.Add(info);
        }
    }

    /// <summary>True when a tree node is one of the group folders we inserted (checked via its Tag).</summary>
    private static bool IsGroupFolderNode(object node)
    {
        try
        {
            var tag = node?.GetType().GetProperty("Tag", BindingFlags.Instance | BindingFlags.Public)?.GetValue(node) as string;
            return ServerGroupFolderService.GroupFolderTag.Equals(tag);
        }
        catch { return false; }
    }

    /// <summary>
    /// Reflects into the IObjectExplorerService implementation to find the TreeView control.
    /// Searches fields and properties for a TreeView or any control with a Nodes collection.
    /// </summary>
    private static object FindTreeView(IObjectExplorerService oeService)
    {
        var oeType = oeService.GetType();

        // Search for TreeView field/property — common names in SSMS internals
        string[] fieldNames = { "Tree", "tree", "_tree", "m_tree", "TreeView", "treeView",
                                "_treeView", "m_treeView", "objectTree", "_objectTree" };

        foreach (string name in fieldNames)
        {
            // Try as property
            var prop = oeType.GetProperty(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop != null)
            {
                var val = prop.GetValue(oeService);
                if (val != null && HasNodesProperty(val))
                    return val;
            }

            // Try as field
            var field = oeType.GetField(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                var val = field.GetValue(oeService);
                if (val != null && HasNodesProperty(val))
                    return val;
            }
        }

        // Brute-force: search ALL fields for anything that looks like a TreeView
        foreach (var field in oeType.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            try
            {
                if (field.FieldType.Name.Contains("Tree") || field.FieldType.Name.Contains("Explorer"))
                {
                    var val = field.GetValue(oeService);
                    if (val != null && HasNodesProperty(val))
                        return val;
                }
            }
            catch { }
        }

        return null;
    }

    private static bool HasNodesProperty(object obj)
    {
        return obj.GetType().GetProperty("Nodes",
            BindingFlags.Instance | BindingFlags.Public) != null;
    }

    /// <summary>
    /// Extracts server info from a TreeView node (TreeNode or HierarchyTreeNode).
    /// Looks at Text/Name property for the server name, and Tag for connection info.
    /// </summary>
    private static ServerInfo ExtractServerInfoFromTreeNode(object node)
    {
        try
        {
            var nodeType = node.GetType();

            string serverName = null;
            string connectionString = null;

            // Preferred: get server name from the node context (ExplorerHierarchyNode.ContainedItem.Context).
            // The display text can be an alias when the server is a registered server,
            // so the context provides the actual server name used for the connection.
            try
            {
                var containedItemProp = nodeType.GetProperty("ContainedItem",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var containedItem = containedItemProp?.GetValue(node);
                if (containedItem != null)
                {
                    var contextProp = containedItem.GetType().GetProperty("Context",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var context = contextProp?.GetValue(containedItem);
                    if (context != null)
                    {
                        connectionString = TryExtractConnectionFromObject(context);

                        // Extract the server name from the context's connection info
                        var contextType = context.GetType();
                        var serverProp = contextType.GetProperty("Name",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                            ?? contextType.GetProperty("InvariantName",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        serverName = serverProp?.GetValue(context)?.ToString();

                        // If context itself doesn't have ServerName, check nested connection info
                        if (string.IsNullOrEmpty(serverName) && !string.IsNullOrEmpty(connectionString))
                        {
                            try
                            {
                                var builder = new SqlConnectionStringBuilder(connectionString);
                                serverName = builder.DataSource;
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }

            // Fallback: use Text/Name property (may be an alias for registered servers)
            if (string.IsNullOrEmpty(serverName))
            {
                var textProp = nodeType.GetProperty("Text",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? nodeType.GetProperty("Name",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                serverName = textProp?.GetValue(node)?.ToString();
            }

            // Try to get connection string from Tag or UserData if we don't already have one
            if (string.IsNullOrEmpty(connectionString))
            {
                var tagProp = nodeType.GetProperty("Tag",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? nodeType.GetProperty("UserData",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var tagObj = tagProp?.GetValue(node);
                if (tagObj != null)
                    connectionString = TryExtractConnectionFromObject(tagObj);
            }

            if (string.IsNullOrEmpty(serverName))
                return null;

            // Clean server name — it may have format "ServerName (SQL Server xx.x - username)"
            int parenIdx = serverName.IndexOf('(');
            if (parenIdx > 0)
                serverName = serverName.Substring(0, parenIdx).Trim();

            return new ServerInfo
            {
                ServerName = serverName,
                ConnectionString = connectionString
            };
        }
        catch
        {
            return null;
        }
    }

    private static ServerInfo ExtractServerInfoFromNode(object node)
    {
        try
        {
            var nodeType = node.GetType();

            // Try to get connection info from the node
            string serverName = null;
            string connectionString = null;

            // IExplorerNode.Connection or similar
            var connProp = nodeType.GetProperty("Connection",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (connProp != null)
            {
                var conn = connProp.GetValue(node);
                if (conn != null)
                {
                    var connType = conn.GetType();

                    // Try ServerName
                    var serverProp = connType.GetProperty("ServerName",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    serverName = serverProp?.GetValue(conn)?.ToString();

                    // Try ConnectionString
                    var connStrProp = connType.GetProperty("ConnectionString",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    connectionString = connStrProp?.GetValue(conn)?.ToString();

                    // If no direct ConnectionString, try building from UIConnectionInfo
                    if (string.IsNullOrEmpty(connectionString))
                    {
                        var uiConnProp = connType.GetProperty("UIConnectionInfo",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (uiConnProp != null)
                        {
                            var uiConn = uiConnProp.GetValue(conn);
                            if (uiConn != null)
                                connectionString = BuildConnectionStringFromUIConnInfo(uiConn);
                        }
                    }
                }
            }

            // Try Name property as fallback for server name
            if (string.IsNullOrEmpty(serverName))
            {
                var nameProp = nodeType.GetProperty("Name",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                serverName = nameProp?.GetValue(node)?.ToString();
            }

            if (string.IsNullOrEmpty(serverName))
                return null;

            return new ServerInfo
            {
                ServerName = serverName,
                ConnectionString = connectionString
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts server info from an INodeInformation (the typed interface from SqlWorkbench.Interfaces).
    /// Walks up to the server-level parent node to find the server name, then tries to build a connection string.
    /// </summary>
    private static ServerInfo ExtractServerInfoFromINode(INodeInformation node)
    {
        try
        {
            // Walk up to the top-level node (server level) — parent of root is null
            var current = node;
            while (current.Parent?.Parent != null)
                current = current.Parent;

            string serverName = current.Name;
            if (string.IsNullOrEmpty(serverName))
                return null;

            // Clean server name — may have format "ServerName (SQL Server xx.x - user)"
            int parenIdx = serverName.IndexOf('(');
            if (parenIdx > 0)
                serverName = serverName.Substring(0, parenIdx).Trim();

            // Try to get connection info from the node's item indexer
            string connectionString = null;
            try
            {
                // INodeInformation has an indexer Item[string name]
                var connObj = current["ConnectionInfo"]
                    ?? current["Connection"]
                    ?? current["UIConnectionInfo"];
                if (connObj != null)
                    connectionString = TryExtractConnectionFromObject(connObj);
            }
            catch { }

            return new ServerInfo
            {
                ServerName = serverName,
                ConnectionString = connectionString
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to extract a connection string from an arbitrary object via reflection.
    /// Looks for ConnectionString, UIConnectionInfo, ServerName properties.
    /// </summary>
    private static string TryExtractConnectionFromObject(object obj)
    {
        if (obj == null) return null;

        try
        {
            var type = obj.GetType();

            // Direct ConnectionString property
            var csProp = type.GetProperty("Connection",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (csProp != null)
            {
                string cs = csProp.GetValue(obj)?.ToString();
                if (!string.IsNullOrEmpty(cs))
                {
                    var conString = ((Microsoft.SqlServer.Management.Common.SqlConnectionInfo)csProp.GetValue(obj)).ConnectionString;
                    if (!string.IsNullOrEmpty(conString)) cs = conString;
                    return cs;
                }
            }

            // UIConnectionInfo property
            var uiProp = type.GetProperty("UIConnectionInfo",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (uiProp != null)
            {
                var uiConn = uiProp.GetValue(obj);
                if (uiConn != null)
                    return BuildConnectionStringFromUIConnInfo(uiConn);
            }

            // If the object has ServerName, it might be a UIConnectionInfo — build from it
            var serverProp = type.GetProperty("ServerName",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (serverProp != null)
            {
                return ServiceCacheProxy.BuildConnectionString(obj);
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Builds a connection string for the Object Explorer node's server, always against master.
    /// The reading of the connection info - including the Entra token an Azure sign-in carries instead of a
    /// password - lives in <see cref="UIConnectionInfoReader"/>, which all three harvest strategies share.
    /// </summary>
    private static string BuildConnectionStringFromUIConnInfo(object uiConnInfo)
        => UIConnectionInfoReader.BuildConnectionString(uiConnInfo, "SQLExtended for SSMS", databaseOverride: "master");

    private static List<ServerInfo> GetServersFromCache()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var servers = new List<ServerInfo>();
        try
        {
            var cache = SchemaCache.Instance;
            // The cache stores connection strings keyed by "connKey|database"
            // We can enumerate known connection keys from cached databases
            var knownKeys = cache.GetKnownConnectionKeys();
            foreach (var (connKey, connStr) in knownKeys)
            {
                servers.Add(new ServerInfo
                {
                    ServerName = connKey,
                    ConnectionString = connStr
                });
            }
        }
        catch { }

        // Also add the currently active connection if not already present
        try
        {
            string activeConn = ConnectionHelper.GetActiveConnectionString();
            if (!string.IsNullOrEmpty(activeConn))
            {
                string activeKey = SchemaCache.Instance.GetConnectionKey(activeConn);
                if (!servers.Exists(s => string.Equals(s.ServerName, activeKey, StringComparison.OrdinalIgnoreCase)))
                {
                    servers.Insert(0, new ServerInfo
                    {
                        ServerName = activeKey,
                        ConnectionString = activeConn
                    });
                }
            }
        }
        catch { }

        return servers;
    }

    private static string GetMasterConnectionString(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                InitialCatalog = "master"
            };
            return builder.ConnectionString;
        }
        catch
        {
            return connectionString;
        }
    }

    private static Type FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = asm.GetType(fullName, throwOnError: false);
                if (type != null) return type;
            }
            catch { }
        }
        return null;
    }

    // ---------------------------------------------------------------------------------------------
    // Reveal an object in the Object Explorer tree: expand server → Databases → [db] → folders →
    // [schema.name] and select the leaf, scrolling it into view. Best-effort and version-tolerant:
    // the OE tree is an undocumented WinForms TreeView reached by reflection, and its children load
    // asynchronously, so each level is expanded and then polled (which pumps the UI message loop)
    // until the expected child appears or a timeout elapses.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Navigates Object Explorer to the given object and selects it. Returns the depth reached:
    /// false when the server or database node could not be located, true otherwise (the leaf may
    /// still be missing for object types whose folder layout isn't mapped — e.g. triggers — in which
    /// case selection stops at the deepest node found). Must be called on the UI thread.
    /// </summary>
    public static async Task<bool> RevealObjectAsync(
        string serverName, string database, string schema, string objectName, string objectType)
    {
        var target = new SchemaQueryService.ResolvedObject { Database = database, Schema = schema, Name = objectName, Type = objectType };
        return await RevealAsync(serverName, target, connectWith: null, strictServer: false, activate: false) >= RevealOutcome.Partial;
    }

    /// <summary>How far <see cref="RevealAsync"/> got. Ordered: anything from <see cref="Partial"/> up selected a node.</summary>
    internal enum RevealOutcome
    {
        NoObjectExplorer,
        ServerNotConnected,
        DatabaseNotFound,
        /// <summary>A folder or the object itself never appeared; the deepest node reached is selected.</summary>
        Partial,
        Found
    }

    /// <summary>
    /// Expands Object Explorer down to <paramref name="target"/> and selects it.
    ///
    /// <para><paramref name="connectWith"/> is a <c>UIConnectionInfo</c> to connect Object Explorer with when the server is
    /// not in the tree yet. <paramref name="strictServer"/> refuses to fall back to whichever server is first in the tree —
    /// the validation window has always done that, but for Locate (issue #7) selecting a same-named table on another
    /// server is a wrong answer that looks like a right one. <paramref name="activate"/> brings the Object Explorer window
    /// forward first, so the expansion is seen and the keyboard lands in the tree. Must be called on the UI thread.</para>
    /// </summary>
    internal static async Task<RevealOutcome> RevealAsync(
        string serverName, SchemaQueryService.ResolvedObject target, object connectWith, bool strictServer, bool activate)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (activate)
        {
            try { (Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE)?.ExecuteCommand("View.ObjectExplorer"); } catch { }
        }

        IObjectExplorerService oeService = null;
        try { oeService = ServiceCacheProxy.GetService(typeof(IObjectExplorerService)) as IObjectExplorerService; } catch { }
        if (oeService == null)
            try { oeService = Package.GetGlobalService(typeof(IObjectExplorerService)) as IObjectExplorerService; } catch { }
        if (oeService == null) return RevealOutcome.NoObjectExplorer;

        object treeView = FindTreeView(oeService);
        if (treeView == null) return RevealOutcome.NoObjectExplorer;

        // Locate the server node (present without expansion). It may sit inside a SQLExtended server-group
        // folder, so search recurses through those folders — but not into a server's own children.
        Func<object, bool> isServer = n => ServerNodeMatches(GetNodeText(n), serverName);
        object node = FindServerNode(treeView, isServer);
        if (node == null && connectWith != null && TryConnect(oeService, connectWith))
            node = await WaitForServerNodeAsync(treeView, isServer);
        if (node == null && !strictServer)
            node = FindServerNode(treeView, _ => true);
        if (node == null) return RevealOutcome.ServerNotConnected;

        node = await ExpandAndFindAsync(treeView, node, n => TextEquals(GetNodeText(n), "Databases"));
        if (node == null) return RevealOutcome.DatabaseNotFound;

        // The four system databases sit in their own folder under Databases.
        if (IsSystemDatabase(target.Database))
        {
            node = await ExpandAndFindAsync(treeView, node, n => TextEquals(GetNodeText(n), "System Databases"));
            if (node == null) return RevealOutcome.DatabaseNotFound;
        }

        node = await ExpandAndFindAsync(treeView, node, n => TextEquals(GetNodeText(n), target.Database));
        if (node == null) return RevealOutcome.DatabaseNotFound;

        var steps = PathFor(target);
        if (steps.Count == 0) { SelectNode(treeView, node); return RevealOutcome.Partial; } // unmapped type — as far as the database

        foreach (var step in steps)
        {
            var next = await ExpandAndFindAsync(treeView, node, step);
            if (next == null) { SelectNode(treeView, node); return RevealOutcome.Partial; } // stop at deepest found
            node = next;
        }

        SelectNode(treeView, node);
        return RevealOutcome.Found;
    }

    private static bool IsSystemDatabase(string database) =>
        database != null && (TextEquals(database, "master") || TextEquals(database, "model") || TextEquals(database, "msdb") || TextEquals(database, "tempdb"));

    /// <summary>
    /// The nodes under the database node that lead to <paramref name="target"/>: its folders, then the object. A trigger
    /// hangs off its table or view (<c>Tables › dbo.Orders › Triggers › trg_Orders</c>) and is listed without a schema;
    /// everything else is listed as <c>schema.name</c>.
    /// </summary>
    private static List<Func<object, bool>> PathFor(SchemaQueryService.ResolvedObject target)
    {
        var steps = new List<Func<object, bool>>();
        string type = (target.Type ?? "").Trim().ToUpperInvariant();

        if (type == "TR")
        {
            var parentFolders = FolderChainFor(target.ParentType);
            if (parentFolders.Count == 0 || string.IsNullOrEmpty(target.ParentName)) return steps;
            foreach (string folder in parentFolders) steps.Add(n => TextEquals(GetNodeText(n), folder));
            steps.Add(Leaf(target.ParentSchema, target.ParentName));
            steps.Add(n => TextEquals(GetNodeText(n), "Triggers"));
            steps.Add(Leaf(null, target.Name));
            return steps;
        }

        var folders = FolderChainFor(type);
        if (folders.Count == 0) return steps;
        foreach (string folder in folders) steps.Add(n => TextEquals(GetNodeText(n), folder));
        steps.Add(Leaf(target.Schema, target.Name));
        return steps;
    }

    private static Func<object, bool> Leaf(string schema, string name)
    {
        string qualified = (string.IsNullOrEmpty(schema) ? "" : schema + ".") + name;
        return n => LeafMatches(GetNodeText(n), qualified, name);
    }

    /// <summary>Folder names (under the database node) leading to an object of the given sys.objects type.</summary>
    private static List<string> FolderChainFor(string objectType)
    {
        switch ((objectType ?? "").Trim().ToUpperInvariant())
        {
            case "U": return new List<string> { "Tables" };
            case "V": return new List<string> { "Views" };
            case "SN": return new List<string> { "Synonyms" };
            case "P":
            case "PC": return new List<string> { "Programmability", "Stored Procedures" };
            case "FN":
            case "FS": return new List<string> { "Programmability", "Functions", "Scalar-valued Functions" };
            case "IF":
            case "TF":
            case "FT": return new List<string> { "Programmability", "Functions", "Table-valued Functions" };
            default: return new List<string>(); // unmapped — navigate as far as the database
        }
    }

    /// <summary>
    /// Asks Object Explorer to connect with a <c>UIConnectionInfo</c> — <c>IObjectExplorerService.ConnectToServer</c>, reached
    /// by reflection because the parameter's type lives in an assembly this project does not reference. Connecting uses the
    /// credentials the query window already holds, so no dialog is shown.
    /// </summary>
    private static bool TryConnect(IObjectExplorerService oeService, object uiConnectionInfo)
    {
        try
        {
            Type type = oeService.GetType();
            var method = type.GetInterfaces().Concat(new[] { type })
                .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                .FirstOrDefault(m => m.Name.EndsWith("ConnectToServer", StringComparison.Ordinal)
                                     && m.GetParameters().Length == 1
                                     && m.GetParameters()[0].ParameterType.IsInstanceOfType(uiConnectionInfo));
            if (method == null)
            {
                Diagnostics.SQLExtendedLog.Warning("ObjectExplorer", "IObjectExplorerService.ConnectToServer was not found; cannot connect Object Explorer.");
                return false;
            }
            method.Invoke(oeService, new[] { uiConnectionInfo });
            return true;
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Warning("ObjectExplorer", "Connecting Object Explorer failed", ex);
            return false;
        }
    }

    /// <summary>A connect returns before the server's node is in the tree; it appears once the connection is made.</summary>
    private static async Task<object> WaitForServerNodeAsync(object treeView, Func<object, bool> isServer)
    {
        const int timeoutMs = 20000;
        const int stepMs = 100;
        for (int waited = 0; waited < timeoutMs; waited += stepMs)
        {
            await Task.Delay(stepMs);
            var found = FindServerNode(treeView, isServer);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>
    /// Expands <paramref name="parent"/> and waits (pumping the message loop) for a child matching
    /// <paramref name="predicate"/> to appear, since OE loads children on a background thread.
    /// </summary>
    private static async Task<object> ExpandAndFindAsync(object treeView, object parent, Func<object, bool> predicate)
    {
        // A child that is already present (parent previously expanded) short-circuits the wait.
        var found = FindChild(parent, predicate);
        if (found != null) return found;

        TrySelect(treeView, parent);
        TryInvoke(parent, "Expand");

        const int timeoutMs = 8000;
        const int stepMs = 60;
        for (int waited = 0; waited < timeoutMs; waited += stepMs)
        {
            await Task.Delay(stepMs);
            found = FindChild(parent, predicate);
            if (found != null) return found;
        }
        return null;
    }

    private static object FindChild(object parentOrTree, Func<object, bool> predicate)
    {
        foreach (var child in EnumerateChildren(parentOrTree))
        {
            try { if (predicate(child)) return child; } catch { }
        }
        return null;
    }

    /// <summary>
    /// Like <see cref="FindChild"/> for locating a server node, but transparently descends through
    /// SQLExtended server-group folders so grouped servers are still found. Does not recurse into a
    /// server's own children (only into our folder nodes).
    /// </summary>
    private static object FindServerNode(object parentOrTree, Func<object, bool> predicate)
    {
        foreach (var child in EnumerateChildren(parentOrTree))
        {
            if (IsGroupFolderNode(child))
            {
                var found = FindServerNode(child, predicate);
                if (found != null) return found;
                continue;
            }

            try { if (predicate(child)) return child; } catch { }
        }
        return null;
    }

    private static IEnumerable EnumerateChildren(object nodeOrTree)
    {
        object nodes = null;
        try
        {
            nodes = nodeOrTree.GetType()
                .GetProperty("Nodes", BindingFlags.Instance | BindingFlags.Public)
                ?.GetValue(nodeOrTree);
        }
        catch { }
        return nodes as IEnumerable ?? Array.Empty<object>();
    }

    private static string GetNodeText(object node)
    {
        try { return node?.GetType().GetProperty("Text", BindingFlags.Instance | BindingFlags.Public)?.GetValue(node) as string; }
        catch { return null; }
    }

    private static bool TextEquals(string text, string expected) =>
        !string.IsNullOrEmpty(text) && string.Equals(text.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool ServerNodeMatches(string text, string serverName)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(serverName)) return false;
        // Server nodes read like "SERVER (SQL Server 16.0.x - domain\user)". Compare the leading name.
        int paren = text.IndexOf('(');
        string name = (paren > 0 ? text.Substring(0, paren) : text).Trim();
        return string.Equals(name, serverName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LeafMatches(string text, string qualified, string bareName)
    {
        if (string.IsNullOrEmpty(text)) return false;
        text = text.Trim();
        // A table can carry a suffix: "dbo.Orders (System-Versioned)", "dbo.Edges (Graph)".
        int suffix = text.IndexOf(" (", StringComparison.Ordinal);
        if (suffix > 0) text = text.Substring(0, suffix);
        return string.Equals(text, qualified, StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, bareName, StringComparison.OrdinalIgnoreCase);
    }

    private static void SelectNode(object treeView, object node)
    {
        TrySelect(treeView, node);
        TryInvoke(node, "EnsureVisible");
        TryInvoke(treeView, "Focus");
    }

    private static void TrySelect(object treeView, object node)
    {
        try
        {
            treeView.GetType().GetProperty("SelectedNode", BindingFlags.Instance | BindingFlags.Public)
                ?.SetValue(treeView, node);
        }
        catch { }
    }

    private static void TryInvoke(object target, string method)
    {
        try
        {
            target?.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null)
                ?.Invoke(target, null);
        }
        catch { }
    }
}
