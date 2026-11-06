using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using SQLExtended.Settings;
using Task = System.Threading.Tasks.Task;

namespace SQLExtended.ScriptCast;

/// <summary>
/// The ScriptCast pane (issue #13, stage 1): tick databases across the servers connected in Object Explorer, or load a
/// Cast List, then Cast runs the query window's script on each and merges the results. See <c>ScriptCast/CLAUDE.md</c>.
/// </summary>
public partial class ScriptCastControl : UserControl, IDisposable
{
    private readonly ObservableCollection<TargetServer> _servers = new();
    private readonly ObservableCollection<DatabaseRun> _runs = new();
    private readonly ObservableCollection<CastMessage> _messages = new();
    private readonly Dictionary<TargetServer, List<(string Name, bool IsSystem)>> _loaded = new();
    private readonly List<TabItem> _resultTabs = new();
    private readonly ScriptSource _source;
    private readonly DispatcherTimer _poll, _progress, _reloadDebounce;

    private CancellationTokenSource _cts;
    private CastList _list;
    private string _listPath;
    private FileSystemWatcher _watcher;
    private string _lastServerKey;
    private bool _selectAllUsed;
    private DateTime _castStarted;

    // Transaction mode (stage 2). _txDeadline is set while transactions wait for Commit All / Rollback All; _txBusy
    // while a verify, commit or rollback is running, so no two of them touch a held connection at once.
    private readonly DispatcherTimer _txTimer;
    private bool _castInTx, _txBusy;
    private DateTime? _txDeadline;

    public ScriptCastControl()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        InitializeComponent();

        ServerList.ItemsSource = _servers;
        SummaryGrid.ItemsSource = _runs;
        MessageGrid.ItemsSource = _messages;

        _source = new ScriptSource();
        _source.Changed += (_, _) => UpdateSourceText();
        UpdateSourceText();

        // Object Explorer raises nothing we can hook when a server connects or disconnects, so the pane looks every
        // few seconds while it is on screen. Reading the tree's root nodes is cheap; the database lists are only
        // re-read for a server that is new.
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _poll.Tick += (_, _) => PollServers();
        _progress = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _progress.Tick += (_, _) => ShowProgress();
        _reloadDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _reloadDebounce.Tick += (_, _) => { _reloadDebounce.Stop(); ReloadFromWatcher(); };
        _txTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _txTimer.Tick += (_, _) => TxTick();

        TxTimeoutBox.Text = SQLExtendedSettings.Current.ScriptCastTransactionTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        SQLExtendedSettings.Changed += OnSettingsChanged;

        Loaded += (_, _) => { RefreshServers(reloadDatabases: false); _poll.Start(); };
        Unloaded += (_, _) => _poll.Stop();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5 && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; OnCast(this, null); }
        };
    }

    // ============================================================ Targets

    private void PollServers()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!IsVisible) return;
        try
        {
            if (ServerKey(ObjectExplorerHelper.GetObjectExplorerServers()) != _lastServerKey) RefreshServers(reloadDatabases: false);
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Warning("ScriptCast", "Reading Object Explorer's servers failed", ex);
        }
    }

    private static string ServerKey(List<ObjectExplorerHelper.ServerInfo> servers)
        => string.Join("|", servers.Select(s => CastServerName.Normalize(s.ServerName)).OrderBy(n => n, StringComparer.Ordinal));

    /// <summary>Brings the list in line with Object Explorer: new servers added, disconnected ones dropped (or kept as unavailable if the Cast List names them).</summary>
    private void RefreshServers(bool reloadDatabases)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var connected = ObjectExplorerHelper.GetObjectExplorerServers();
        _lastServerKey = ServerKey(connected);

        foreach (var info in connected)
        {
            var server = Find(info.ServerName);
            if (server == null)
            {
                server = new TargetServer(info.ServerName, info.ConnectionString);
                AddServer(server);
                if (server.IsConnected) LoadDatabases(server);
                else server.Error = "Object Explorer gave no connection details for this server.";
            }
            else if (!server.IsConnected && info.ConnectionString != null)
            {
                server.MarkConnected(info.ConnectionString);
                LoadDatabases(server);
            }
            else if (reloadDatabases && server.IsConnected)
            {
                LoadDatabases(server);
            }
        }

        // Gone from Object Explorer: kept as unavailable if the Cast List names it, otherwise dropped. A list placeholder
        // that was never connected is left as it is.
        foreach (var server in _servers.Where(s => !connected.Any(c => CastServerName.Same(c.ServerName, s.Name))).ToList())
        {
            var entry = ListEntry(server.Name);
            if (entry == null)
            {
                _loaded.Remove(server);
                _servers.Remove(server);
            }
            else if (server.IsConnected)
            {
                _loaded.Remove(server);
                server.MarkDisconnected();
                server.SetDatabases(Array.Empty<(string, bool)>(), entry.Databases);
            }
        }

        AddListPlaceholders();
        ApplyFilter();
    }

    private TargetServer Find(string name) => _servers.FirstOrDefault(s => CastServerName.Same(s.Name, name));

    private void AddServer(TargetServer server)
    {
        server.CheckChanged += (_, _) => UpdateTargetCount();
        // Connected first, then by name: the unavailable ones are the list's leftovers.
        int at = 0;
        while (at < _servers.Count && (_servers[at].IsConnected && !server.IsConnected
               || _servers[at].IsConnected == server.IsConnected && string.Compare(_servers[at].Name, server.Name, StringComparison.OrdinalIgnoreCase) < 0))
            at++;
        _servers.Insert(at, server);
    }

    private void LoadDatabases(TargetServer server)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        server.IsLoading = true;
        server.Error = "";
        string connectionString = server.ConnectionString;

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            List<(string, bool)> databases = null;
            string error = null;
            try
            {
                databases = await Task.Run(() => ReadDatabases(connectionString));
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Diagnostics.SQLExtendedLog.Warning("ScriptCast", $"Could not list the databases on {server.Name}", ex);
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            server.IsLoading = false;
            if (!_servers.Contains(server) || !server.IsConnected) return;
            if (error != null)
            {
                server.Error = error;
                return;
            }

            _loaded[server] = databases;
            ApplyListTo(server, tick: _list != null && !server.Databases.Any(d => d.IsChecked));
            ApplyFilter();
        });
    }

    /// <summary>
    /// Every database this login can open, system ones included (flagged, hidden by default). Unlike
    /// <see cref="ObjectExplorerHelper.GetDatabases"/>, nothing is merged in from the schema cache: a database from the
    /// cache that the server no longer has would be offered for a cast that can only fail.
    /// </summary>
    private static List<(string, bool)> ReadDatabases(string connectionString)
    {
        var list = new List<(string, bool)>();
        using var conn = SqlConnectionFactory.Create(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT name, CAST(CASE WHEN database_id <= 4 OR is_distributor = 1 THEN 1 ELSE 0 END AS bit)
            FROM sys.databases
            WHERE state_desc = 'ONLINE' AND ISNULL(HAS_DBACCESS(name), 0) = 1
            ORDER BY name";
        cmd.CommandTimeout = 15;
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) list.Add((reader.GetString(0), reader.GetBoolean(1)));
        return list;
    }

    private CastListServer ListEntry(string server) => _list?.Servers.FirstOrDefault(s => CastServerName.Same(s.Server, server));

    /// <summary>Rebuilds a loaded server's rows with the list's databases it lacks shown as missing, and (when asked) ticks the list's databases.</summary>
    private void ApplyListTo(TargetServer server, bool tick)
    {
        if (!_loaded.TryGetValue(server, out var databases)) return;
        var entry = ListEntry(server.Name);
        var missing = entry?.Databases.Where(d => !databases.Any(x => string.Equals(x.Name, d, StringComparison.OrdinalIgnoreCase))).ToList() ?? new List<string>();
        server.SetDatabases(databases, missing);

        if (tick && entry != null)
            foreach (var db in server.Databases)
                if (entry.Databases.Contains(db.Name, StringComparer.OrdinalIgnoreCase)) db.IsChecked = true;
    }

    /// <summary>A Cast List server that isn't connected still gets a row, with its databases shown and a Connect… button.</summary>
    private void AddListPlaceholders()
    {
        if (_list == null) return;
        foreach (var entry in _list.Servers)
        {
            if (Find(entry.Server) != null) continue;
            var server = new TargetServer(entry.Server, null);
            server.SetDatabases(Array.Empty<(string, bool)>(), entry.Databases);
            AddServer(server);
        }
    }

    private void ApplyFilter()
    {
        string text = FilterBox.Text?.Trim() ?? "";
        bool hideSystem = HideSystem.IsChecked == true;

        foreach (var server in _servers)
        {
            bool serverMatches = text.Length > 0 && server.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
            bool any = false;
            foreach (var db in server.Databases)
            {
                db.IsVisible = !(hideSystem && db.IsSystem)
                               && (text.Length == 0 || serverMatches || db.Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
                any |= db.IsVisible;
            }
            server.IsVisible = text.Length == 0 || serverMatches || any;
            server.OnDatabaseCheckChanged();
        }

        TargetsEmpty.Visibility = _servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateTargetCount();
    }

    private List<TargetDatabase> CheckedTargets()
        => _servers.Where(s => s.IsConnected).SelectMany(s => s.Checked)
                   .OrderBy(d => d.Server.Name, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private void UpdateTargetCount()
    {
        if (_cts != null) return;   // the status line is showing progress
        var targets = CheckedTargets();
        int servers = targets.Select(t => t.Server).Distinct().Count();
        StatusText.Text = targets.Count == 0
            ? "Tick databases, then Cast to run the query window's script on each of them."
            : $"{targets.Count} database{Plural(targets.Count)} on {servers} server{Plural(servers)} ticked.";
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) { if (IsLoaded) ApplyFilter(); }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var server in _servers.Where(s => s.IsConnected && s.IsVisible)) server.SetVisible(true);
        _selectAllUsed = true;
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        foreach (var server in _servers) server.SetVisible(false);
        foreach (var db in _servers.SelectMany(s => s.Databases)) db.IsChecked = false;
        _selectAllUsed = false;
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        RefreshServers(reloadDatabases: true);
    }

    private void OnToggleServer(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TargetServer server) server.IsExpanded = !server.IsExpanded;
    }

    private void OnConnectServer(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if ((sender as FrameworkElement)?.DataContext is not TargetServer server) return;
        try
        {
            // SSMS's own connect dialog. It can't be pre-filled from here; the server is picked up by the poll once
            // Object Explorer has it, whatever name it was typed under, as long as it normalises to the same one.
            (Package.GetGlobalService(typeof(EnvDTE.DTE)) as DTE2)?.ExecuteCommand("File.ConnectObjectExplorer");
            StatusText.Text = $"Connect to {server.Name} in the dialog. It is picked up here once Object Explorer has it.";
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Warning("ScriptCast", "File.ConnectObjectExplorer failed", ex);
            StatusText.Text = $"Could not open the connect dialog. Connect to {server.Name} in Object Explorer; it is picked up here once connected.";
        }
    }

    // ============================================================ Cast Lists

    private static string DefaultListFolder()
    {
        string configured = SQLExtendedSettings.Current.ScriptCastListFolder;
        if (!string.IsNullOrWhiteSpace(configured)) return Environment.ExpandEnvironmentVariables(configured.Trim());
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "SQLExtended", "Cast Lists");
    }

    private static string FolderForDialog()
    {
        string folder = DefaultListFolder();
        try { Directory.CreateDirectory(folder); } catch { }
        return folder;
    }

    private void OnLoadList(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load Cast List",
            Filter = "Cast List (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = _listPath != null ? Path.GetDirectoryName(_listPath) : FolderForDialog(),
        };
        if (dialog.ShowDialog() != true) return;
        LoadList(dialog.FileName, fromWatcher: false);
    }

    private void LoadList(string path, bool fromWatcher)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        CastList list;
        try
        {
            list = CastListSerializer.Parse(ReadShared(path), Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception ex) when (ex is CastListFormatException or IOException or UnauthorizedAccessException)
        {
            if (fromWatcher)
            {
                // Mid-edit saves are normal; keep what is on screen and say why it didn't change.
                ShowIssues(new[] { $"{Path.GetFileName(path)} was changed but couldn't be read, so the list here is unchanged: {ex.Message}" });
                return;
            }
            MessageBox.Show($"Could not load {path}.\n\n{ex.Message}", "ScriptCast", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _list = list;
        if (!string.Equals(_listPath, path, StringComparison.OrdinalIgnoreCase)) Watch(path);
        _listPath = path;
        _selectAllUsed = false;

        ListName.Text = string.IsNullOrWhiteSpace(list.Name) ? Path.GetFileNameWithoutExtension(path) : list.Name;
        ListName.ToolTip = string.IsNullOrWhiteSpace(list.Description) ? path : list.Description + "\n" + path;
        OpenJsonButton.IsEnabled = true;

        // The list replaces the selection: everything cleared, unavailable rows from an earlier list dropped.
        foreach (var server in _servers.Where(s => !s.IsConnected).ToList()) _servers.Remove(server);
        foreach (var db in _servers.SelectMany(s => s.Databases)) db.IsChecked = false;
        foreach (var server in _servers.ToList()) ApplyListTo(server, tick: true);
        AddListPlaceholders();
        ApplyFilter();

        if (list.Issues.Count > 0) ShowIssues(list.Issues);
        else IssueBanner.Visibility = Visibility.Collapsed;

        int unavailable = list.Servers.Count(s => Find(s.Server) is not { IsConnected: true });
        StatusText.Text = $"{(fromWatcher ? "Reloaded" : "Loaded")} {ListName.Text}: {list.DatabaseCount} database{Plural(list.DatabaseCount)} on {list.Servers.Count} server{Plural(list.Servers.Count)}"
                          + (unavailable > 0 ? $", {unavailable} not connected." : ".");
    }

    /// <summary>Reads a file another program (the SSMS editor, mid-save) may still have open.</summary>
    private static string ReadShared(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>
    /// Reload when the file changes: covers a save from the SSMS tab Open JSON gave it and an edit anywhere else, which
    /// is why there is no hook on the editor's save. Editors save in bursts (truncate, write, rename), hence the debounce.
    /// </summary>
    private void Watch(string path)
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            _watcher = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(new Action(() => { _reloadDebounce.Stop(); _reloadDebounce.Start(); }));
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(new Action(() => { _reloadDebounce.Stop(); _reloadDebounce.Start(); }));
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Warning("ScriptCast", $"Cannot watch {path} for changes; edits won't reload until it is loaded again", ex);
        }
    }

    private void ReloadFromWatcher()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_listPath != null && File.Exists(_listPath)) LoadList(_listPath, fromWatcher: true);
    }

    private void OnSaveList(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var targets = CheckedTargets();
        if (targets.Count == 0)
        {
            StatusText.Text = "Tick the databases to save first.";
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Cast List",
            Filter = "Cast List (*.json)|*.json",
            DefaultExt = ".json",
            InitialDirectory = _listPath != null ? Path.GetDirectoryName(_listPath) : FolderForDialog(),
            FileName = _listPath != null ? Path.GetFileName(_listPath) : "Cast List.json",
        };
        if (dialog.ShowDialog() != true) return;

        bool sameFile = string.Equals(dialog.FileName, _listPath, StringComparison.OrdinalIgnoreCase);
        var list = new CastList
        {
            Name = sameFile && _list != null && !string.IsNullOrWhiteSpace(_list.Name) ? _list.Name : Path.GetFileNameWithoutExtension(dialog.FileName),
            Description = sameFile ? _list?.Description ?? "" : "",
        };
        foreach (var group in targets.GroupBy(t => t.Server))
            list.Servers.Add(new CastListServer(group.Key.Name, group.Select(d => d.Name).ToList()));

        try
        {
            File.WriteAllText(dialog.FileName, CastListSerializer.Serialize(list));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save {dialog.FileName}.\n\n{ex.Message}", "ScriptCast", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // The saved file becomes the loaded list (the watcher's echo of our own write reloads the same ticks).
        LoadList(dialog.FileName, fromWatcher: false);
        StatusText.Text = $"Saved {list.DatabaseCount} database{Plural(list.DatabaseCount)} to {dialog.FileName}.";
    }

    private void OnOpenJson(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_listPath == null) return;
        try
        {
            VsShellUtilities.OpenDocument(ServiceProvider.GlobalProvider, _listPath);
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Warning("ScriptCast", $"Opening {_listPath} in an editor tab failed", ex);
            StatusText.Text = $"Could not open {_listPath}: {ex.Message}";
        }
    }

    private void ShowIssues(IEnumerable<string> issues)
    {
        var lines = issues.ToList();
        IssueText.Text = (lines.Count == 1 ? "" : $"{lines.Count} problems in the Cast List; the rest of it loaded:\n") + string.Join("\n", lines.Take(12))
                         + (lines.Count > 12 ? $"\n… and {lines.Count - 12} more." : "");
        IssueBanner.Visibility = Visibility.Visible;
    }

    private void OnDismissIssues(object sender, RoutedEventArgs e) => IssueBanner.Visibility = Visibility.Collapsed;

    // ============================================================ Cast

    private void UpdateSourceText()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string label = _source.Label;
        SourceText.Text = label == null ? "No query window yet: click into one, then Cast." : $"Script: {label}";
    }

    private void OnCast(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_cts != null || _txBusy) return;
        if (HeldRuns().Count > 0)
        {
            StatusText.Text = "Transactions from the last cast are still open: Commit All or Rollback All first.";
            return;
        }

        string script = _source.GetScript(out bool isSelection);
        UpdateSourceText();
        if (string.IsNullOrWhiteSpace(script))
        {
            StatusText.Text = _source.Label == null
                ? "No query window to cast from. Click into the query window with the script, then Cast."
                : $"{_source.Label} is empty.";
            return;
        }

        var targets = CheckedTargets();
        if (targets.Count == 0)
        {
            StatusText.Text = "No databases ticked.";
            return;
        }

        bool inTx = InTransaction.IsChecked == true;
        if (inTx)
        {
            if (!int.TryParse(TxTimeoutBox.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || seconds < 10 || seconds > 86_400)
            {
                StatusText.Text = "Roll back after: give a number of seconds from 10 to 86,400.";
                TxTimeoutBox.Focus();
                return;
            }

            var blockers = ScriptCastAnalyzer.TransactionBlockers(script);
            if (blockers.Count > 0)
            {
                MessageBox.Show("This script can't run in transaction mode:\n\n" + string.Join("\n", blockers.Take(8).Select(b => "  • " + b))
                                + (blockers.Count > 8 ? $"\n  … and {blockers.Count - 8} more" : "")
                                + "\n\nRemove those statements, or untick Run in transaction.",
                    "ScriptCast", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        if (!ConfirmCast(script, targets)) return;

        var batches = BatchSplitter.Split(script);
        if (batches.Count == 0)
        {
            StatusText.Text = "The script has nothing to run but GO.";
            return;
        }

        var settings = SQLExtendedSettings.Current;
        var options = new CastOptions
        {
            MaxParallelism = Math.Max(1, settings.ScriptCastMaxParallelism),
            CommandTimeoutSeconds = Math.Max(0, settings.ScriptCastCommandTimeoutSeconds),
            MaxRowsPerResultSet = Math.Max(0, settings.ScriptCastMaxRowsPerResultSet),
            InTransaction = inTx,
            AutoRollback = inTx && AutoRollback.IsChecked == true,
        };

        ClearResults();
        foreach (var t in targets) _runs.Add(new DatabaseRun(new CastTarget(t.Server.Name, t.Name, t.Server.ConnectionString)));
        ResultTabs.SelectedItem = SummaryTab;
        TransactionColumn.Visibility = inTx ? Visibility.Visible : Visibility.Collapsed;

        _cts = new CancellationTokenSource();
        _castStarted = DateTime.Now;
        _castInTx = inTx;
        TxText.Text = inTx ? "Transactions open while casting" + (options.AutoRollback ? " · auto rollback on" : "") : "";
        CancelButton.IsEnabled = true;
        UpdateTxButtons();
        _progress.Start();
        ShowProgress();

        Diagnostics.SQLExtendedLog.Info("ScriptCast",
            $"Casting {batches.Count} batch(es){(isSelection ? " (selection)" : "")} from {_source.Label} to {targets.Count} database(s), {options.MaxParallelism} at a time"
            + (inTx ? $", in transaction{(options.AutoRollback ? " with auto rollback" : "")}" : ""));

        var runs = _runs.ToList();
        var token = _cts.Token;
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await Task.Run(() => CastRunner.RunAsync(runs, batches, options, OnMessage, token));
            }
            catch (Exception ex)
            {
                Diagnostics.SQLExtendedLog.Error("ScriptCast", "The cast failed", ex);
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            FinishCast(runs);
        });
    }

    private bool ConfirmCast(string script, List<TargetDatabase> targets)
    {
        string where = $"{targets.Count} database{Plural(targets.Count)} on {targets.Select(t => t.Server).Distinct().Count()} server(s)";

        // The query window's own guard, applied to the cast too: a missing WHERE is no less wrong times fifty.
        if (SQLExtendedSettings.Current.UnsafeDmlGuardEnabled)
        {
            var unsafeDml = Safety.UnsafeDmlAnalyzer.Analyze(script).Findings;
            if (unsafeDml.Count > 0)
            {
                string list = string.Join("\n", unsafeDml.Take(5).Select(f => $"  • {f.Kind} {f.Target} (line {f.Line})"));
                if (MessageBox.Show($"This script has {unsafeDml.Count} UPDATE/DELETE statement(s) with no WHERE clause:\n\n{list}\n\nRun it on {where} anyway?",
                        "ScriptCast", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return false;
            }
        }

        if (_selectAllUsed)
        {
            var report = ScriptCastAnalyzer.Analyze(script);
            if (report.MayChange)
            {
                string what = report.Kinds.Count > 0
                    ? "It contains " + string.Join(", ", report.Kinds.Take(6)) + (report.Kinds.Count > 6 ? ", …" : "") + "."
                    : "It couldn't be parsed, so it may change data or schema.";
                if (MessageBox.Show($"Select All was used, and this script may change data or schema. {what}\n\nRun it on {where}?",
                        "ScriptCast", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return false;
            }
        }

        return true;
    }

    private void OnMessage(CastMessage message)
        => Dispatcher.BeginInvoke(new Action(() => _messages.Add(message)), DispatcherPriority.Background);

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = _castInTx ? "Cancelling, and rolling back every database…" : "Cancelling…";
        UpdateTxButtons();
    }

    private void ShowProgress()
    {
        if (_cts == null) return;
        int done = _runs.Count(r => r.Status is CastStatus.Succeeded or CastStatus.Failed or CastStatus.Cancelled);
        int failed = _runs.Count(r => r.Status == CastStatus.Failed);
        int running = _runs.Count(r => r.Status is CastStatus.Connecting or CastStatus.Running);
        StatusText.Text = $"{(_cts.IsCancellationRequested ? "Cancelling" : "Casting")} · {done} of {_runs.Count} done, {running} running"
                          + (failed > 0 ? $", {failed} failed" : "") + $" · {(DateTime.Now - _castStarted).TotalSeconds:0.0} s";
    }

    private void FinishCast(List<DatabaseRun> runs)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _progress.Stop();
        bool cancelled = _cts?.IsCancellationRequested == true;
        _cts?.Dispose();
        _cts = null;
        CancelButton.IsEnabled = false;

        // A database the runner never reached (cancelled while waiting for a slot) still says Pending.
        foreach (var run in runs.Where(r => r.Status is CastStatus.Pending or CastStatus.Connecting or CastStatus.Running))
            run.Status = CastStatus.Cancelled;

        List<MergedResultSet> merged;
        try
        {
            merged = ResultMerger.Merge(runs.Select(r => new DatabaseResults(r.Server, r.Database, SnapshotResults(r))).ToList());
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Error("ScriptCast", "Merging the results failed", ex);
            merged = new List<MergedResultSet>();
        }

        int resultNumber = 0, lastOrdinal = -1;
        foreach (var set in merged)
        {
            if (set.Ordinal != lastOrdinal) { resultNumber++; lastOrdinal = set.Ordinal; }
            AddResultTab(set, resultNumber);
        }

        int succeeded = runs.Count(r => r.Status == CastStatus.Succeeded);
        int failed = runs.Count(r => r.Status == CastStatus.Failed);
        int cancelledCount = runs.Count(r => r.Status == CastStatus.Cancelled);
        long rows = runs.Sum(r => r.RowsReturned), affected = runs.Sum(r => r.RowsAffected);
        int mismatched = merged.Count(m => m.Mismatched);

        StatusText.Text = $"{(cancelled ? "Cancelled" : "Done")} · {runs.Count} database{Plural(runs.Count)}: {succeeded} succeeded"
                          + (failed > 0 ? $", {failed} failed" : "") + (cancelledCount > 0 ? $", {cancelledCount} cancelled" : "")
                          + $" · {rows:N0} row{Plural(rows)} returned" + (affected > 0 ? $", {affected:N0} affected" : "")
                          + (mismatched > 0 ? $" · {mismatched} result set{Plural(mismatched)} with different columns" : "")
                          + $" · {(DateTime.Now - _castStarted).TotalSeconds:0.0} s";

        // Results first when there are any and nothing failed; otherwise the Summary says what went wrong.
        if (failed == 0 && _resultTabs.Count > 0) ResultTabs.SelectedItem = _resultTabs[0];
        else ResultTabs.SelectedItem = SummaryTab;

        Diagnostics.SQLExtendedLog.Info("ScriptCast", $"Cast finished: {succeeded} succeeded, {failed} failed, {cancelledCount} cancelled, {rows} rows");

        if (_castInTx)
        {
            int held = HeldRuns().Count;
            if (held > 0)
            {
                // The clock starts now, not at Cast: it bounds the time locks are held waiting for a person, and a long
                // script shouldn't use up the time meant for reviewing what it did.
                int seconds = int.Parse(TxTimeoutBox.Text.Trim(), CultureInfo.InvariantCulture);
                _txDeadline = DateTime.Now.AddSeconds(seconds);
                _txTimer.Start();
                ResultTabs.SelectedItem = SummaryTab;   // rows affected per database, before the commit
            }
            else
            {
                TxText.Text = cancelled || failed > 0 ? "Rolled back on every database." : "";
            }
            TxTick();
        }
        UpdateTxButtons();
    }

    // ============================================================ Transaction mode

    private List<DatabaseRun> HeldRuns() => _runs.Where(r => r.Held != null).ToList();

    /// <summary>
    /// Commit All only when every database of the cast succeeded and still has its transaction open (the issue's rule):
    /// a failure without auto rollback leaves the others open for review, and the only way on is Rollback All.
    /// </summary>
    private bool CanCommit()
        => !_txBusy && _cts == null && _runs.Count > 0 && _runs.All(r => r.Status == CastStatus.Succeeded && r.Transaction == TxOutcome.Open && r.Held != null);

    private void UpdateTxButtons()
    {
        bool casting = _cts != null, open = HeldRuns().Count > 0;
        CommitButton.IsEnabled = CanCommit();
        RollbackButton.IsEnabled = !_txBusy && (open || casting && _castInTx && !_cts.IsCancellationRequested);
        CastButton.IsEnabled = !casting && !open && !_txBusy;
        InTransaction.IsEnabled = !casting && !open && !_txBusy;
        AutoRollback.IsEnabled = TxTimeoutBox.IsEnabled = InTransaction.IsEnabled && InTransaction.IsChecked == true;
    }

    private void OnTxOptionChanged(object sender, RoutedEventArgs e) { if (IsLoaded || CommitButton != null) UpdateTxButtons(); }

    private void OnSettingsChanged(object sender, EventArgs e)
    {
        // A new default for the next cast, unless one is waiting on the current timeout (that cast keeps its own).
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_txDeadline == null && _cts == null)
                TxTimeoutBox.Text = SQLExtendedSettings.Current.ScriptCastTransactionTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        }));
    }

    private void TxTick()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var held = HeldRuns();
        if (held.Count == 0 || _txDeadline == null)
        {
            _txTimer.Stop();
            return;
        }

        var left = _txDeadline.Value - DateTime.Now;
        if (left <= TimeSpan.Zero)
        {
            if (!_txBusy) RollbackHeld($"The {TxTimeoutBox.Text.Trim()} s transaction timeout ran out");
            return;
        }

        int failed = _runs.Count(r => r.IsFailed);
        TxText.Text = $"{held.Count} transaction{Plural(held.Count)} open · rolls back in {(int)left.TotalMinutes}:{left.Seconds:00}"
                      + (failed > 0 ? $" · {failed} database{Plural(failed)} failed: Commit All is off, review the errors then Rollback All" : "");
    }

    private void OnCommitAll(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!CanCommit()) return;
        var held = HeldRuns();
        long affected = _runs.Sum(r => r.RowsAffected);
        int servers = held.Select(r => r.Server).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        if (MessageBox.Show($"Commit on {held.Count} database{Plural(held.Count)} on {servers} server{Plural(servers)} ({affected:N0} row{Plural(affected)} affected)?\n\n"
                            + "The commits run one database after another. If a connection fails partway, some databases will be committed and others not; "
                            + "the Summary will say which.",
                "ScriptCast", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        // The countdown kept running behind the dialog; it may have rolled everything back while it was open.
        if (!CanCommit())
        {
            StatusText.Text = "Nothing committed: the transactions were rolled back while the dialog was open.";
            return;
        }

        _txBusy = true;
        _txTimer.Stop();
        UpdateTxButtons();
        TxText.Text = "Checking every transaction is still open…";

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            bool allOpen = false;
            try
            {
                allOpen = await Task.Run(() => CastRunner.VerifyAllAsync(held, OnMessage));
                if (allOpen)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    TxText.Text = "Committing…";
                    await Task.Run(() => { foreach (var run in held) CastRunner.Commit(run, OnMessage); });
                }
            }
            catch (Exception ex)
            {
                Diagnostics.SQLExtendedLog.Error("ScriptCast", "Commit All failed", ex);
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _txBusy = false;

            if (!allOpen)
            {
                int lost = held.Count(r => r.Transaction == TxOutcome.Lost);
                StatusText.Text = $"Nothing committed: {lost} transaction{Plural(lost)} had already ended (see Messages). Roll back the rest.";
                TxTick();
                if (HeldRuns().Count > 0) _txTimer.Start();
                UpdateTxButtons();
                return;
            }

            _txDeadline = null;
            int committed = held.Count(r => r.Transaction == TxOutcome.Committed);
            int commitFailed = held.Count(r => r.Transaction == TxOutcome.CommitFailed);
            int unknown = held.Count(r => r.Transaction == TxOutcome.CommitUnknown);
            TxText.Text = committed == held.Count ? "" : "Not every database committed: see the Transaction column.";
            StatusText.Text = $"Committed {committed} of {held.Count} database{Plural(held.Count)}"
                              + (commitFailed > 0 ? $", {commitFailed} failed and rolled back" : "")
                              + (unknown > 0 ? $", {unknown} unknown (connection lost during COMMIT: check by hand)" : "") + ".";
            if (committed != held.Count) ResultTabs.SelectedItem = SummaryTab;
            Diagnostics.SQLExtendedLog.Info("ScriptCast", $"Commit All: {committed} committed, {commitFailed} failed, {unknown} unknown");
            UpdateTxButtons();
        });
    }

    private void OnRollbackAll(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_cts != null)
        {
            OnCancel(sender, e);   // still casting: the runner rolls back everything once the databases have stopped
            return;
        }
        RollbackHeld("Rollback All");
    }

    private void RollbackHeld(string why)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var held = HeldRuns();
        if (held.Count == 0 || _txBusy) return;

        _txBusy = true;
        _txTimer.Stop();
        UpdateTxButtons();
        TxText.Text = "Rolling back…";

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await Task.Run(() => CastRunner.RollbackAllAsync(held, OnMessage));
            }
            catch (Exception ex)
            {
                Diagnostics.SQLExtendedLog.Error("ScriptCast", "Rollback All failed", ex);
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _txBusy = false;
            _txDeadline = null;
            TxText.Text = "Rolled back on every database.";
            StatusText.Text = $"{why}: rolled back {held.Count} database{Plural(held.Count)}.";
            Diagnostics.SQLExtendedLog.Info("ScriptCast", $"{why}: rolled back {held.Count} database(s)");
            UpdateTxButtons();
        });
    }

    private static List<ResultSetCapture> SnapshotResults(DatabaseRun run)
    {
        lock (run.ResultSets) return run.ResultSets.OrderBy(r => r.Ordinal).ToList();
    }

    private void ClearResults()
    {
        foreach (var tab in _resultTabs) ResultTabs.Items.Remove(tab);
        _resultTabs.Clear();
        _runs.Clear();
        _messages.Clear();
    }

    // ============================================================ Result grids

    private void AddResultTab(MergedResultSet set, int number)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        if (set.Mismatched)
        {
            var mark = new TextBlock { Text = "⚠ ", FontWeight = FontWeights.Normal };
            mark.SetResourceReference(TextBlock.ForegroundProperty, "SqlxWarn");
            header.Children.Add(mark);
        }
        header.Children.Add(new TextBlock { Text = $"Result {number}" + (set.Mismatched ? " (different columns)" : "") });
        var count = new TextBlock { Text = $"  {set.Rows.Count:N0}", FontWeight = FontWeights.Normal };
        count.SetResourceReference(TextBlock.ForegroundProperty, "SqlxTextMuted");
        header.Children.Add(count);

        var tab = new TabItem
        {
            Header = header,
            Style = (Style)FindResource("TabHeader"),
            ToolTip = $"{set.Sources.Count} database{Plural(set.Sources.Count)}:\n" + string.Join("\n", set.Sources.Take(20)) + (set.Sources.Count > 20 ? "\n…" : ""),
        };

        var grid = BuildGrid(set);
        if (set.Mismatched)
        {
            var panel = new DockPanel();
            var banner = new Border { Padding = new Thickness(10, 6, 10, 6), BorderThickness = new Thickness(0, 0, 0, 1) };
            banner.SetResourceReference(Border.BackgroundProperty, "SqlxWarnSurface");
            banner.SetResourceReference(Border.BorderBrushProperty, "SqlxWarnBorder");
            var text = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = $"Result set {number} has different columns on {set.Sources.Count} database{Plural(set.Sources.Count)} than on the rest, so they are kept apart: "
                       + string.Join(", ", set.Sources.Take(8)) + (set.Sources.Count > 8 ? ", …" : "") + ".",
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "SqlxText");
            banner.Child = text;
            DockPanel.SetDock(banner, Dock.Top);
            panel.Children.Add(banner);
            panel.Children.Add(grid);
            tab.Content = panel;
        }
        else
        {
            tab.Content = grid;
        }

        ResultTabs.Items.Add(tab);
        _resultTabs.Add(tab);
    }

    /// <summary>
    /// A DataTable under a DataGrid. Columns get positional names (<c>c0</c>, <c>c1</c>…) with the real name as the header:
    /// a result's column names can repeat, be empty, or hold dots and brackets, each of which breaks a binding path.
    /// </summary>
    private DataGrid BuildGrid(MergedResultSet set)
    {
        var table = new DataTable();
        table.Columns.Add("ServerName", typeof(string));
        table.Columns.Add("DatabaseName", typeof(string));
        for (int i = 0; i < set.Columns.Count; i++)
        {
            // A value the reader couldn't type (an unloadable UDT read back as text) would break a typed column.
            var type = set.Columns[i].Type;
            int index = i;
            if (type != typeof(object) && set.Rows.Any(r => r.Values[index] is not DBNull && r.Values[index] != null && !type.IsInstanceOfType(r.Values[index])))
                type = typeof(object);
            table.Columns.Add("c" + i, type);
        }

        table.BeginLoadData();
        foreach (var row in set.Rows)
        {
            var values = new object[row.Values.Length + 2];
            values[0] = row.Server;
            values[1] = row.Database;
            Array.Copy(row.Values, 0, values, 2, row.Values.Length);
            table.Rows.Add(values);
        }
        table.EndLoadData();

        var grid = new DataGrid
        {
            Style = (Style)FindResource("MonitorGrid"),
            AutoGenerateColumns = false,
            EnableColumnVirtualization = true,
            FrozenColumnCount = 2,
        };
        grid.Columns.Add(TextColumn("ServerName", "ServerName"));
        grid.Columns.Add(TextColumn("DatabaseName", "DatabaseName"));
        for (int i = 0; i < set.Columns.Count; i++) grid.Columns.Add(TextColumn(set.Columns[i].Name, "c" + i));
        grid.ItemsSource = table.DefaultView;
        return grid;
    }

    private static DataGridTextColumn TextColumn(string header, string path) => new()
    {
        Header = header,
        Binding = new Binding(path) { Converter = CellText.Instance, Mode = BindingMode.OneWay },
        ClipboardContentBinding = new Binding(path) { Converter = CellText.Instance, ConverterParameter = "copy", Mode = BindingMode.OneWay },
        SortMemberPath = path,
        MaxWidth = 600,
    };

    /// <summary>Cell text as SSMS shows it: NULL, bit as 1/0, datetimes to the millisecond, one line, capped.</summary>
    private sealed class CellText : IValueConverter
    {
        public static readonly CellText Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool copy = parameter as string == "copy";
            string text = value switch
            {
                null or DBNull => "NULL",
                bool b => b ? "1" : "0",
                DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                DateTimeOffset o => o.ToString("yyyy-MM-dd HH:mm:ss.fffffff zzz", CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString(),
            };
            if (copy) return text;
            text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            return text.Length > 1000 ? text.Substring(0, 1000) + "…" : text;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    private static string Plural(long n) => n == 1 ? "" : "s";

    public void Dispose()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _cts?.Cancel();
        _poll.Stop();
        _progress.Stop();
        _reloadDebounce.Stop();
        _txTimer.Stop();
        SQLExtendedSettings.Changed -= OnSettingsChanged;

        // Tab or SSMS closing with transactions open: close their connections. They are unpooled, so each session
        // ends and the server rolls its transaction back; no round trip, so closing isn't held up. Not mid-commit:
        // pulling the connection from under a COMMIT is what makes an outcome unknown.
        if (!_txBusy)
            foreach (var run in HeldRuns())
            {
                CastRunner.Release(run, TxOutcome.RolledBack);
                Diagnostics.SQLExtendedLog.Info("ScriptCast", $"Closed with a transaction open on {run.Server} / {run.Database}: connection closed, rolled back");
            }
        _watcher?.Dispose();
        _source.Dispose();
    }
}
