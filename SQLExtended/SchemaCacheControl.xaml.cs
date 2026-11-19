using Microsoft.VisualStudio.Shell;
using SQLExtended.Cache;
using SQLExtended.Theme;
using SQLExtended.Cache.Models;
using SQLExtended.Decryption;
using SQLExtended.Export;
using SQLExtended.Settings;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Task = System.Threading.Tasks.Task;

namespace SQLExtended;

/// <summary>
/// WPF control for the Schema Cache tool window. Shows a live Server → Database tree of
/// everything the shared <see cref="SchemaCache"/> currently holds, with per-node refresh
/// and clear actions. Rebuilds on <see cref="ISchemaCache.CacheRefreshed"/> and on a short
/// timer (so mid-load states and relative timestamps stay current).
/// </summary>
public partial class SchemaCacheControl : UserControl
{
    // State colors keyed by CacheState (frozen so they can cross threads / be shared).
    private static Brush ReadyBrush => Theme.ThemeManager.Get("SqlxGood");
    private static Brush StaleBrush => Theme.ThemeManager.Get("SqlxWarn");
    private static Brush LoadingBrush => Theme.ThemeManager.Get("SqlxHeading");
    private static Brush ErrorBrush => Theme.ThemeManager.Get("SqlxError");
    private static Brush NotLoadedBrush => Theme.ThemeManager.Get("SqlxTextMuted");

    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _collapsedServers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expandedDatabases = new(StringComparer.OrdinalIgnoreCase);

    // The linked-server branch starts collapsed, at every level: it is reference, not the cache's main content.
    private readonly HashSet<string> _expandedLinked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which node is selected, carried across rebuilds. The tree's ItemsSource is replaced wholesale every
    /// five seconds, so without this the toolbar's Export button would lose its target on the next tick.
    /// </summary>
    private string _selectedKey;

    private CancellationTokenSource _exportCts;
    private bool _exporting;

    /// <summary>
    /// Set for a message the user still needs to see a moment later — an export summary, or a "no
    /// connection available" refusal. Rebuild's own counts line would otherwise overwrite it within
    /// five seconds, which reads as the action having done nothing at all.
    /// </summary>
    private bool _statusSticky;

    private static string DbKey(string connectionKey, string database) => $"{connectionKey}|{database}";

    private static string SelectionKeyForServer(string connectionKey) => $"s|{connectionKey}";
    private static string SelectionKeyForDatabase(string connectionKey, string database) => $"d|{connectionKey}|{database}";

    public SchemaCacheControl()
    {
        InitializeComponent();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (s, e) => Rebuild();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SchemaCache.Instance.CacheRefreshed += OnCacheRefreshed;
        LinkedServerCache.Instance.Changed += OnLinkedServersChanged;
        Rebuild();
        _timer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        SchemaCache.Instance.CacheRefreshed -= OnCacheRefreshed;
        LinkedServerCache.Instance.Changed -= OnLinkedServersChanged;
    }

    private void OnCacheRefreshed(object sender, CacheRefreshEventArgs e)
        => Dispatcher.BeginInvoke(new Action(() => Rebuild()));

    private void OnLinkedServersChanged(object sender, EventArgs e)
        => Dispatcher.BeginInvoke(new Action(() => Rebuild()));

    // --- Tree construction ---

    private void Rebuild(bool capture = true)
    {
        // Capture the user's current expand/collapse and selection choices before we replace the nodes.
        // Selection is captured even when `capture` is false: that flag exists so Expand/Collapse all can
        // override the recorded expansion state, and letting it skip the selection too would re-apply a
        // stale one — the Export button would then be aimed at whatever was selected two clicks ago.
        if (CacheTree.ItemsSource is IEnumerable<ServerNode> existing)
        {
            _selectedKey = null;

            foreach (var s in existing)
            {
                if (capture)
                {
                    if (s.IsExpanded) _collapsedServers.Remove(s.ConnectionKey);
                    else _collapsedServers.Add(s.ConnectionKey);
                }

                if (s.IsSelected) _selectedKey = SelectionKeyForServer(s.ConnectionKey);

                foreach (var node in s.Linked?.AllNodes() ?? Enumerable.Empty<LinkedNode>())
                {
                    if (capture)
                    {
                        if (node.IsExpanded) _expandedLinked.Add(node.Key);
                        else _expandedLinked.Remove(node.Key);
                    }
                    if (node.IsSelected) _selectedKey = "l|" + node.Key;
                }

                if (s.Databases != null)
                {
                    foreach (var d in s.Databases)
                    {
                        if (capture)
                        {
                            string dk = DbKey(d.ConnectionKey, d.Name);
                            if (d.IsExpanded) _expandedDatabases.Add(dk);
                            else _expandedDatabases.Remove(dk);
                        }

                        // A selected type-count row keeps its database selected: it is the database the
                        // user is looking at, and it is what the toolbar's Export button should act on.
                        if (d.IsSelected || d.TypeCounts?.Any(t => t.IsSelected) == true)
                            _selectedKey = SelectionKeyForDatabase(d.ConnectionKey, d.Name);
                    }
                }
            }
        }

        var snapshot = SchemaCache.Instance.GetCacheSnapshot();

        // A server whose linked servers were read but none of whose databases is cached still gets a node.
        var serverKeys = snapshot.Select(e => e.ConnectionKey)
            .Concat(LinkedServerCache.Instance.ConnectionKeys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        bool linkedHidden = SQLExtendedSettings.Current.LinkedServerCompletion == LinkedServerCompletionLevel.Off;

        var servers = serverKeys
            .GroupJoin(snapshot, k => k, e => e.ConnectionKey, (k, entries) => new { Key = k, Entries = entries.ToList() }, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var dbs = g.Entries.OrderBy(e => e.Database, StringComparer.OrdinalIgnoreCase)
                           .Select(e =>
                           {
                               var node = DatabaseNode.From(e);
                               node.IsExpanded = _expandedDatabases.Contains(DbKey(e.ConnectionKey, e.Database));
                               node.IsSelected = _selectedKey == SelectionKeyForDatabase(e.ConnectionKey, e.Database);
                               return node;
                           })
                           .ToList();
                int objects = g.Entries.Sum(e => e.ObjectCount);
                var linked = BuildLinkedGroup(g.Key);
                return new ServerNode
                {
                    ConnectionKey = g.Key,
                    Name = g.Key,
                    Databases = dbs,
                    Linked = linked,
                    // With linked servers turned off and nothing read, the branch would be an empty row under every server.
                    Children = linkedHidden && LinkedServerCache.Instance.GetServersState(g.Key, out _) == LinkedLoadState.NotLoaded
                        ? dbs.Cast<object>().ToList()
                        : dbs.Cast<object>().Append(linked).ToList(),
                    Summary = $"{dbs.Count} database{(dbs.Count == 1 ? "" : "s")} · {objects:N0} object{(objects == 1 ? "" : "s")}",
                    IsExpanded = !_collapsedServers.Contains(g.Key),
                    IsSelected = _selectedKey == SelectionKeyForServer(g.Key)
                };
            })
            .ToList();

        CacheTree.ItemsSource = servers;

        bool empty = servers.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        // An export in progress owns the status line, and a sticky message is one the user still needs.
        if (_exporting || _statusSticky)
            return;

        // A cache load that could not decrypt an encrypted module has no other way to say so — it runs on a
        // timer with no UI. Sticky, so the five-second rebuild does not wipe it before it is read, and
        // reported once so it does not re-stick after Refresh acknowledges it.
        var decryption = ModuleDecryptionService.LastRun;
        if (decryption != null && decryption.HasProblem && !decryption.Reported)
        {
            decryption.Reported = true;
            SetStatus(decryption.Summary, sticky: true);
            return;
        }

        if (empty)
        {
            StatusText.Text = "";
        }
        else
        {
            int dbCount = servers.Sum(s => s.Databases.Count);
            int objCount = snapshot.Sum(e => e.ObjectCount);
            StatusText.Text = $"{servers.Count} server{(servers.Count == 1 ? "" : "s")} · " +
                              $"{dbCount} database{(dbCount == 1 ? "" : "s")} · {objCount:N0} objects cached";
        }
    }

    /// <summary>Writes the status line. <paramref name="sticky"/> keeps it there past the next rebuild.</summary>
    private void SetStatus(string text, bool sticky = false)
    {
        StatusText.Text = text;
        _statusSticky = sticky;
    }

    // --- Toolbar ---

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        // Pressing Refresh is an acknowledgement of whatever the status line was holding.
        _statusSticky = false;
        Rebuild();
    }

    /// <summary>
    /// Loads schema for every database on every connected server into the cache. Server enumeration
    /// and the active-connection fallback must run on the UI thread; the per-database loads are
    /// kicked off on a background thread (and self-throttle inside <see cref="SchemaCache"/>).
    /// </summary>
    private void CacheAllDatabases_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var servers = ObjectExplorerHelper.GetConnectedServers();

        string active = null;
        try { active = ConnectionHelper.GetActiveConnectionString(); } catch { }

        // Resolve a usable connection string per server now, while on the UI thread.
        var targets = new List<string>();
        foreach (var s in servers)
        {
            string connStr = s.ConnectionString;
            if (string.IsNullOrEmpty(connStr) && !string.IsNullOrEmpty(active) &&
                string.Equals(SchemaCache.Instance.GetConnectionKey(active), s.ServerName, StringComparison.OrdinalIgnoreCase))
                connStr = active;

            if (!string.IsNullOrEmpty(connStr) && !targets.Contains(connStr))
                targets.Add(connStr);
        }

        if (targets.Count == 0)
        {
            StatusText.Text = "No connected server available — open a query window first.";
            return;
        }

        StatusText.Text = "Enumerating databases…";

        _ = Task.Run(() =>
        {
            int dbTotal = 0;
            foreach (string connStr in targets)
            {
                foreach (string db in ObjectExplorerHelper.GetDatabases(connStr))
                {
                    _ = SchemaCache.Instance.LoadDatabaseAsync(connStr, db);
                    dbTotal++;
                }
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusText.Text = dbTotal == 0
                    ? "No databases found to cache."
                    : $"Loading {dbTotal} database{(dbTotal == 1 ? "" : "s")} into the cache…";
                Rebuild();
            }));
        });
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        _collapsedServers.Clear();
        foreach (var entry in SchemaCache.Instance.GetCacheSnapshot())
            _expandedDatabases.Add(DbKey(entry.ConnectionKey, entry.Database));
        foreach (var server in CacheTree.ItemsSource as IEnumerable<ServerNode> ?? Enumerable.Empty<ServerNode>())
            foreach (var node in server.Linked?.AllNodes() ?? Enumerable.Empty<LinkedNode>())
                _expandedLinked.Add(node.Key);
        Rebuild(capture: false);
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var entry in SchemaCache.Instance.GetCacheSnapshot())
            _collapsedServers.Add(entry.ConnectionKey);
        _expandedDatabases.Clear();
        _expandedLinked.Clear();
        Rebuild(capture: false);
    }

    // --- Context menu: database ---

    private void RefreshDatabase_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if ((sender as MenuItem)?.DataContext is DatabaseNode node)
            RefreshDatabase(node);
    }

    private void ClearDatabase_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is DatabaseNode node)
        {
            SchemaCache.Instance.ClearDatabase(node.ConnectionKey, node.Name);
            StatusText.Text = $"Removed {node.ConnectionKey} / {node.Name} from cache";
            Rebuild();
        }
    }

    private void CopyDatabaseName_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is DatabaseNode node)
            TrySetClipboard(node.Name);
    }

    // --- Context menu: server ---

    private void RefreshServer_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if ((sender as MenuItem)?.DataContext is ServerNode node)
        {
            int refreshed = 0;
            foreach (var db in node.Databases)
                if (RefreshDatabase(db, silent: true)) refreshed++;
            StatusText.Text = refreshed > 0
                ? $"Refreshing {refreshed} database{(refreshed == 1 ? "" : "s")} on {node.Name}…"
                : $"No connection available to refresh {node.Name} — open a query window on it first.";
        }
    }

    private void ClearServer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is ServerNode node)
        {
            foreach (var db in node.Databases.ToList())
                SchemaCache.Instance.ClearDatabase(node.ConnectionKey, db.Name);
            StatusText.Text = $"Removed {node.Name} from cache";
            Rebuild();
        }
    }

    private void CopyServerName_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.DataContext is ServerNode node)
            TrySetClipboard(node.Name);
    }

    // --- Linked servers ---

    /// <summary>
    /// The "Linked servers" branch under one server: what <see cref="LinkedServerCache"/> holds for it, which is only
    /// what IntelliSense has asked for so far (or what was loaded from here), at the level the setting allows.
    /// </summary>
    private LinkedGroupNode BuildLinkedGroup(string connectionKey)
    {
        var cache = LinkedServerCache.Instance;
        var state = cache.GetServersState(connectionKey, out string error);
        var group = new LinkedGroupNode { ConnectionKey = connectionKey, Key = $"{connectionKey}|" };

        var servers = cache.GetSnapshot(connectionKey).Select(ls =>
        {
            var server = new LinkedServerNode
            {
                ConnectionKey = connectionKey,
                Name = ls.Server.Name,
                Key = $"{connectionKey}|{ls.Server.Name}",
                Product = string.Join(" · ", new[] { ls.Server.Description, ls.Server.DataSource }.Where(x => !string.IsNullOrWhiteSpace(x))),
                StateBrush = BrushFor(ls.DatabasesState),
                StateText = ls.DatabasesState switch
                {
                    LinkedLoadState.Ready => $"Databases read {(ls.DatabasesLoadedUtc.HasValue ? RelativeTime(ls.DatabasesLoadedUtc.Value) : "")}",
                    LinkedLoadState.Loading => "Reading databases…",
                    LinkedLoadState.Failed => "Could not read databases: " + ls.DatabasesError,
                    _ => "Databases not read — right-click to load"
                },
                Detail = ls.DatabasesState switch
                {
                    LinkedLoadState.Ready => $"{ls.Databases.Count:N0} database{(ls.Databases.Count == 1 ? "" : "s")}",
                    LinkedLoadState.Loading => "loading…",
                    LinkedLoadState.Failed => "failed",
                    _ => "name only"
                },
                Databases = ls.Databases.Select(d => new LinkedDatabaseNode
                {
                    ConnectionKey = connectionKey,
                    ServerName = ls.Server.Name,
                    Name = d.Name,
                    Key = $"{connectionKey}|{ls.Server.Name}|{d.Name}",
                    StateBrush = BrushFor(d.ObjectsState),
                    StateText = d.ObjectsState switch
                    {
                        LinkedLoadState.Ready => "Tables and views read",
                        LinkedLoadState.Loading => "Reading tables and views…",
                        LinkedLoadState.Failed => "Could not read tables and views: " + d.ObjectsError,
                        _ => "Tables not read — right-click to load"
                    },
                    Detail = d.ObjectsState switch
                    {
                        LinkedLoadState.Ready => LinkedObjectSummary(d),
                        LinkedLoadState.Loading => "loading…",
                        LinkedLoadState.Failed => "failed",
                        _ => ""
                    }
                }).ToList()
            };
            return server;
        }).ToList();

        group.Servers = servers;
        group.Summary = state switch
        {
            LinkedLoadState.Ready => servers.Count == 0 ? "none" : $"{servers.Count}",
            LinkedLoadState.Loading => "reading…",
            LinkedLoadState.Failed => "could not be read",
            _ => "not read yet"
        };
        group.StateText = state == LinkedLoadState.Failed
            ? "Could not read sys.servers: " + error
            : state == LinkedLoadState.NotLoaded ? "Right-click to read this server's linked servers" : null;

        foreach (var node in group.AllNodes())
        {
            node.IsExpanded = _expandedLinked.Contains(node.Key);
            node.IsSelected = _selectedKey == "l|" + node.Key;
        }

        return group;
    }

    private static string LinkedObjectSummary(LinkedDatabaseSnapshot d)
    {
        var parts = new List<string> { $"{d.TableCount:N0} table{(d.TableCount == 1 ? "" : "s")}", $"{d.ViewCount:N0} view{(d.ViewCount == 1 ? "" : "s")}" };
        if (d.OtherCount > 0) parts.Add($"{d.OtherCount:N0} synonym{(d.OtherCount == 1 ? "" : "s")}");
        if (d.TablesWithColumns > 0) parts.Add($"columns for {d.TablesWithColumns:N0}");
        return string.Join(" · ", parts);
    }

    private static Brush BrushFor(LinkedLoadState state) => state switch
    {
        LinkedLoadState.Ready => ReadyBrush,
        LinkedLoadState.Loading => LoadingBrush,
        LinkedLoadState.Failed => ErrorBrush,
        _ => NotLoadedBrush
    };

    /// <summary>
    /// A connection to the local server, for a load started from here: the one IntelliSense last used, any cached
    /// database's, or the active editor's. Null with the status line saying why.
    /// </summary>
    private string LinkedConnectionFor(string connectionKey)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string cs = LinkedServerCache.Instance.GetConnectionString(connectionKey)
                    ?? SchemaCache.Instance.GetCacheSnapshot().Where(e => string.Equals(e.ConnectionKey, connectionKey, StringComparison.OrdinalIgnoreCase))
                                                             .Select(e => e.ConnectionString).FirstOrDefault(c => !string.IsNullOrEmpty(c))
                    ?? TryActiveConnectionFor(connectionKey);
        if (string.IsNullOrEmpty(cs))
            SetStatus($"No connection available to {connectionKey} — open a query window on it first.", sticky: true);
        return cs;
    }

    private void RefreshLinkedList_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if ((sender as MenuItem)?.DataContext is not LinkedGroupNode node || LinkedConnectionFor(node.ConnectionKey) is not { } cs)
            return;
        LinkedServerCache.Instance.Refresh(node.ConnectionKey, server: null, reloadDatabases: false, connectionString: cs);
        SetStatus($"Reading the linked servers of {node.ConnectionKey}…");
    }

    private void RefreshLinkedServer_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if ((sender as MenuItem)?.DataContext is not LinkedServerNode node || LinkedConnectionFor(node.ConnectionKey) is not { } cs)
            return;
        _expandedLinked.Add(node.Key);
        LinkedServerCache.Instance.Refresh(node.ConnectionKey, node.Name, reloadDatabases: true, connectionString: cs);
        SetStatus($"Reading the databases on linked server {node.Name}…");
    }

    private void RefreshLinkedDatabase_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if ((sender as MenuItem)?.DataContext is not LinkedDatabaseNode node || LinkedConnectionFor(node.ConnectionKey) is not { } cs)
            return;
        LinkedServerCache.Instance.RefreshDatabase(node.ConnectionKey, node.ServerName, node.Name, connectionString: cs);
        SetStatus($"Reading the tables and views in {node.ServerName}.{node.Name}…");
    }

    private void CopyLinkedName_Click(object sender, RoutedEventArgs e)
    {
        switch ((sender as MenuItem)?.DataContext)
        {
            case LinkedServerNode server: TrySetClipboard(server.Name); break;
            case LinkedDatabaseNode db: TrySetClipboard($"{db.ServerName}.{db.Name}"); break;
        }
    }

    // --- Export to folder ---

    /// <summary>
    /// Toolbar button. Doubles as the cancel button while an export is running — an export of a whole
    /// server is minutes of work, so there has to be a way to stop it that isn't closing the window.
    /// </summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_exporting)
        {
            _exportCts?.Cancel();
            StatusText.Text = "Cancelling export…";
            return;
        }

        switch (CacheTree.SelectedItem)
        {
            case DatabaseNode db:
                StartExport(db.ConnectionKey, new List<DatabaseNode> { db }, folderPerDatabase: false);
                break;
            case TypeCountNode type when type.Owner != null:
                StartExport(type.Owner.ConnectionKey, new List<DatabaseNode> { type.Owner }, folderPerDatabase: false);
                break;
            case ServerNode server:
                StartExport(server.ConnectionKey, server.Databases, folderPerDatabase: true);
                break;
            default:
                SetStatus("Select a server or a database in the tree first, then click Export.", sticky: true);
                break;
        }
    }

    private void ExportDatabase_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if ((sender as MenuItem)?.DataContext is DatabaseNode node)
            StartExport(node.ConnectionKey, new List<DatabaseNode> { node }, folderPerDatabase: false);
    }

    private void ExportServer_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if ((sender as MenuItem)?.DataContext is ServerNode node)
            StartExport(node.ConnectionKey, node.Databases, folderPerDatabase: true);
    }

    /// <summary>
    /// Prompts for a target folder and starts the export on a background thread.
    ///
    /// <paramref name="folderPerDatabase"/> decides the shape of the tree, and the two cases are not
    /// interchangeable: exporting one database puts the type folders straight under the chosen folder, so
    /// two exports of the same database from two servers line up file-for-file in a folder compare. Only
    /// a whole-server export, where several databases have to coexist, inserts a database level.
    /// </summary>
    private void StartExport(string connectionKey, IReadOnlyList<DatabaseNode> nodes, bool folderPerDatabase)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // One at a time: a second export would fight the first over the cancellation source and the
        // button caption, and both would be writing into the same tree.
        if (_exporting)
        {
            SetStatus("An export is already running — wait for it to finish, or press Cancel export.", sticky: true);
            return;
        }

        var databases = (nodes ?? Array.Empty<DatabaseNode>())
            .Select(n => n.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (databases.Count == 0)
        {
            SetStatus($"Nothing is cached for {connectionKey} to export.", sticky: true);
            return;
        }

        // The export reads live schema out of SQL Server, so it needs a real connection — a database the
        // cache only knows from disk has none until something reconnects to that server.
        string connStr = nodes.Select(n => n.ConnectionString).FirstOrDefault(s => !string.IsNullOrEmpty(s))
                         ?? TryActiveConnectionFor(connectionKey);

        if (string.IsNullOrEmpty(connStr))
        {
            SetStatus($"No connection available to export {connectionKey} — open a query window on it first.", sticky: true);
            return;
        }

        var settings = SQLExtendedSettings.Load();

        string folder = PromptForFolder(
            folderPerDatabase
                ? $"Export {databases.Count} cached database{(databases.Count == 1 ? "" : "s")} from {connectionKey} — one .sql file per object, in a folder per database."
                : $"Export {connectionKey} / {databases[0]} — one .sql file per object, in a folder per object type.",
            settings.LastSchemaExportFolder);

        if (string.IsNullOrEmpty(folder)) return;

        bool clean = false;
        int existing;
        try { existing = SchemaExportService.CountExistingScripts(folder); }
        catch { existing = 0; }

        if (existing > 0)
        {
            // Re-exporting over the top would leave the scripts of dropped objects in place, and the next
            // folder compare would report them as present on both servers.
            var answer = MessageBox.Show(
                $"{folder}\n\nalready holds {existing:N0} exported .sql file{(existing == 1 ? "" : "s")}.\n\n" +
                "Delete them first so the export reflects the server exactly? Only .sql files inside SQLExtended's own " +
                "type folders are removed — anything else in the folder is left alone.\n\n" +
                "Choosing No cancels the export.",
                "Export schema to folder", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes) return;
            clean = true;
        }

        settings.LastSchemaExportFolder = folder;
        settings.Save();

        _exportCts = new CancellationTokenSource();
        var ct = _exportCts.Token;
        _exporting = true;
        StatusFooter.SetBusy(StatusText, true);
        _statusSticky = false;
        ExportButton.Content = "Cancel export";
        StatusText.Text = $"Exporting to {folder}…";

        _ = Task.Run(() =>
        {
            SchemaFolderExportResult result = null;
            Exception failure = null;

            try
            {
                if (clean) SchemaExportService.DeleteExistingScripts(folder);

                result = SchemaExportService.ExportToFolder(
                    connStr, databases, folder, folderPerDatabase,
                    message => Dispatcher.BeginInvoke(new Action(() => { if (_exporting) StatusText.Text = message; })),
                    ct);
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            Dispatcher.BeginInvoke(new Action(() => FinishExport(folder, result, failure)));
        });
    }

    private void FinishExport(string folder, SchemaFolderExportResult result, Exception failure)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _exporting = false;
        StatusFooter.SetBusy(StatusText, false);
        ExportButton.Content = "Export…";
        _exportCts?.Dispose();
        _exportCts = null;

        if (failure != null)
        {
            SetStatus($"Export failed: {failure.Message}", sticky: true);
            MessageBox.Show($"The export failed:\n\n{failure.Message}", "Export schema to folder",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (result == null)
        {
            SetStatus("Export produced no result.", sticky: true);
            return;
        }

        string summary = $"{result.FilesWritten:N0} object script{(result.FilesWritten == 1 ? "" : "s")} written to {folder}";
        if (result.Failed > 0) summary += $" · {result.Failed:N0} could not be scripted";
        SetStatus(result.Cancelled ? "Export cancelled — " + summary : summary, sticky: true);

        var message = new StringBuilder();
        message.AppendLine(result.Cancelled ? "Export cancelled part-way through." : "Export complete.");
        message.AppendLine();
        message.AppendLine($"{result.FilesWritten:N0} file{(result.FilesWritten == 1 ? "" : "s")} written to:");
        message.AppendLine(folder);

        if (result.Warnings.Count > 0)
        {
            message.AppendLine();
            message.AppendLine($"{result.Warnings.Count:N0} item{(result.Warnings.Count == 1 ? "" : "s")} could not be scripted:");
            foreach (string warning in result.Warnings.Take(12))
                message.AppendLine("  • " + warning);
            if (result.Warnings.Count > 12)
                message.AppendLine($"  … and {result.Warnings.Count - 12:N0} more.");
        }

        message.AppendLine();
        message.Append("Open the folder now?");

        var icon = result.Warnings.Count > 0 || result.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information;
        if (MessageBox.Show(message.ToString(), "Export schema to folder", MessageBoxButton.YesNo, icon) == MessageBoxResult.Yes)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus($"Could not open the folder: {ex.Message}", sticky: true); }
        }
    }

    /// <summary>Shows a folder picker. Returns the chosen path, or null if cancelled. UI thread only.</summary>
    private static string PromptForFolder(string description, string initialPath)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = description,
            ShowNewFolderButton = true
        };

        try
        {
            if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
                dialog.SelectedPath = initialPath;
        }
        catch { /* a remembered path on a disconnected drive just means no starting point */ }

        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.SelectedPath : null;
    }

    // --- Helpers ---

    /// <summary>
    /// Kicks off a forced refresh for a database. Uses the connection string the cache last saw;
    /// falls back to the active editor connection when it targets the same server. Returns false
    /// when no usable connection is available.
    /// </summary>
    private bool RefreshDatabase(DatabaseNode node, bool silent = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string connStr = node.ConnectionString;
        if (string.IsNullOrEmpty(connStr))
            connStr = TryActiveConnectionFor(node.ConnectionKey);

        if (string.IsNullOrEmpty(connStr))
        {
            if (!silent)
                StatusText.Text = $"No connection available to refresh {node.Name} — open a query window on {node.ConnectionKey} first.";
            return false;
        }

        if (!silent)
            StatusText.Text = $"Refreshing {node.Name}…";

        string database = node.Name;
        _ = Task.Run(() => SchemaCache.Instance.LoadDatabaseAsync(connStr, database, forceFullRefresh: true));
        return true;
    }

    private static string TryActiveConnectionFor(string connectionKey)
    {
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string active = ConnectionHelper.GetActiveConnectionString();
            if (!string.IsNullOrEmpty(active) &&
                string.Equals(SchemaCache.Instance.GetConnectionKey(active), connectionKey, StringComparison.OrdinalIgnoreCase))
                return active;
        }
        catch { }
        return null;
    }

    private static void TrySetClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { }
    }

    private void CacheTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = VisualUpwardSearch<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item != null)
            item.IsSelected = true;
    }

    private static T VisualUpwardSearch<T>(DependencyObject source) where T : DependencyObject
    {
        while (source != null && source is not T)
            source = VisualTreeHelper.GetParent(source);
        return source as T;
    }

    private static string RelativeTime(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalSeconds < 45) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }

    // --- View-model node types (referenced by the XAML DataTemplates) ---

    public sealed class ServerNode
    {
        public string Name { get; set; }
        public string ConnectionKey { get; set; }
        public string Summary { get; set; }
        public List<DatabaseNode> Databases { get; set; }
        public LinkedGroupNode Linked { get; set; }

        /// <summary>What the tree shows beneath the server: its databases, then the linked-server branch.</summary>
        public List<object> Children { get; set; }
        public bool IsExpanded { get; set; } = true;
        public bool IsSelected { get; set; }
    }

    public sealed class DatabaseNode
    {
        public string Name { get; set; }
        public string ConnectionKey { get; set; }
        public string ConnectionString { get; set; }
        public Brush StateBrush { get; set; }
        public string StateText { get; set; }
        public string Detail { get; set; }
        public string LastRefreshText { get; set; }
        public List<TypeCountNode> TypeCounts { get; set; }
        public bool IsExpanded { get; set; } // collapsed by default; expands to show the type breakdown
        public bool IsSelected { get; set; }

        internal static DatabaseNode From(CacheSnapshotEntry e)
        {
            var node = new DatabaseNode
            {
                Name = e.Database,
                ConnectionKey = e.ConnectionKey,
                ConnectionString = e.ConnectionString,
                StateBrush = BrushFor(e.State),
                StateText = StateTextFor(e),
                Detail = DetailFor(e),
                LastRefreshText = e.FromDiskOnly
                    ? "from disk"
                    : e.LastRefreshUtc.HasValue ? RelativeTime(e.LastRefreshUtc.Value) : "",
                TypeCounts = e.ObjectTypeCounts
                    .Select(t => new TypeCountNode { Label = t.Label, Count = t.Count })
                    .ToList()
            };

            // Back-reference so a selected type row still identifies the database it belongs to.
            foreach (var t in node.TypeCounts) t.Owner = node;

            return node;
        }

        private static Brush BrushFor(CacheState state) => state switch
        {
            CacheState.Ready => ReadyBrush,
            CacheState.Stale => StaleBrush,
            CacheState.Loading => LoadingBrush,
            CacheState.Error => ErrorBrush,
            _ => NotLoadedBrush
        };

        private static string StateTextFor(CacheSnapshotEntry e) => e.State switch
        {
            CacheState.Ready => "Ready",
            CacheState.Stale => e.FromDiskOnly ? "Stale — loaded from disk" : "Stale",
            CacheState.Loading => "Loading…",
            CacheState.Error => "Error loading schema",
            _ => "Not loaded"
        };

        private static string DetailFor(CacheSnapshotEntry e) => e.State switch
        {
            CacheState.Loading => "loading…",
            CacheState.Error => "error",
            CacheState.NotLoaded => "not loaded",
            _ => $"{e.ObjectCount:N0} object{(e.ObjectCount == 1 ? "" : "s")}"
        };
    }

    /// <summary>Common to the three linked-server node types, so expansion and selection survive the rebuild.</summary>
    public abstract class LinkedNode
    {
        public string ConnectionKey { get; set; }
        public string Name { get; set; }
        internal string Key { get; set; }
        public string Detail { get; set; }
        public Brush StateBrush { get; set; }
        public string StateText { get; set; }
        public bool IsExpanded { get; set; }
        public bool IsSelected { get; set; }
    }

    public sealed class LinkedGroupNode : LinkedNode
    {
        public string Summary { get; set; }
        public List<LinkedServerNode> Servers { get; set; } = new();

        internal IEnumerable<LinkedNode> AllNodes()
        {
            yield return this;
            foreach (var server in Servers)
            {
                yield return server;
                foreach (var db in server.Databases)
                    yield return db;
            }
        }
    }

    public sealed class LinkedServerNode : LinkedNode
    {
        public string Product { get; set; }
        public List<LinkedDatabaseNode> Databases { get; set; } = new();
    }

    public sealed class LinkedDatabaseNode : LinkedNode
    {
        public string ServerName { get; set; }
    }

    public sealed class TypeCountNode
    {
        public string Label { get; set; }
        public int Count { get; set; }
        public string CountText => $"{Count:N0}";
        public bool IsExpanded { get; set; } // leaf; present only so the shared item style's binding resolves
        public bool IsSelected { get; set; }

        /// <summary>The database this count belongs to, so selecting a type row still names a database.</summary>
        internal DatabaseNode Owner { get; set; }
    }
}
