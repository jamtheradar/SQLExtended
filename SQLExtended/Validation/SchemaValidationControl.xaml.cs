using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SQLExtended.Cache;
using SQLExtended.Theme;
using SQLExtended.Validation.Models;
using Microsoft.VisualStudio.Shell;

namespace SQLExtended.Validation;

/// <summary>
/// WPF control for the Schema Validation tool window.
/// Server + database multi-select, runs <see cref="SchemaValidationService"/> and shows
/// broken / external references in a grid.
/// </summary>
public partial class SchemaValidationControl : UserControl
{
    private CancellationTokenSource _validateCts;
    private List<ObjectExplorerHelper.ServerInfo> _servers = new();
    private bool _isLoadingServers;

    /// <summary>
    /// Set by the Object Explorer context menu before the window is shown, to pre-select a specific
    /// server (by connection key) and database. Consumed once on the next <see cref="LoadServers"/>.
    /// </summary>
    internal static (string ConnKey, string Database)? PendingTarget;
    private string _pendingTargetDatabase;
    private List<ValidationIssue> _allResults = new();
    private readonly ValidationIgnoreList _ignores = ValidationIgnoreList.Load();

    public SchemaValidationControl()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        InitializeComponent();

        Loaded += (s, e) => LoadServers();

        SchemaCache.Instance.CacheRefreshed += (s, e) =>
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateConnectionInfo()));
        };
    }

    // --- Server / Database population (mirrors SqlSearchControl) ---

    private void LoadServers()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _isLoadingServers = true;
        try
        {
            _servers = ObjectExplorerHelper.GetConnectedServers();
            ServerCombo.Items.Clear();
            foreach (var server in _servers)
                ServerCombo.Items.Add(new ComboBoxItem { Content = server.DisplayName, Tag = server });

            // A pending target (from the Object Explorer context menu) wins over the active connection.
            var target = PendingTarget;
            PendingTarget = null;
            string preferredKey = target?.ConnKey;
            _pendingTargetDatabase = target?.Database;

            if (string.IsNullOrEmpty(preferredKey))
            {
                try
                {
                    string activeConn = ConnectionHelper.GetActiveConnectionString();
                    if (!string.IsNullOrEmpty(activeConn))
                        preferredKey = SchemaCache.Instance.GetConnectionKey(activeConn);
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(preferredKey))
            {
                for (int i = 0; i < _servers.Count; i++)
                {
                    if (string.Equals(_servers[i].ServerName, preferredKey, StringComparison.OrdinalIgnoreCase))
                    {
                        ServerCombo.SelectedIndex = i;
                        break;
                    }
                }
            }

            if (ServerCombo.SelectedIndex < 0 && ServerCombo.Items.Count > 0)
                ServerCombo.SelectedIndex = 0;

            // SelectionChanged is suppressed while loading, so whatever we just selected has to load its
            // databases here - unconditionally. Gating this on a single server or a pending target left the
            // ordinary first open (two or more connected servers, no Object Explorer target) with a server
            // named in the combo and an empty database list, and reselecting the same item fires nothing:
            // the only way out was to pick another server and come back.
            if (ServerCombo.SelectedIndex >= 0)
                LoadDatabasesForSelectedServer();
        }
        finally
        {
            _isLoadingServers = false;
        }

        UpdateConnectionInfo();
    }

    private void ServerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_isLoadingServers) return;
        LoadDatabasesForSelectedServer();
    }

    private void LoadDatabasesForSelectedServer()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        DatabaseList.Items.Clear();

        var serverInfo = GetSelectedServer();
        if (serverInfo == null) return;

        StatusText.Text = "Loading databases...";

        string connStr = serverInfo.ConnectionString;
        if (string.IsNullOrEmpty(connStr))
        {
            try
            {
                string active = ConnectionHelper.GetActiveConnectionString();
                if (!string.IsNullOrEmpty(active))
                {
                    string activeKey = SchemaCache.Instance.GetConnectionKey(active);
                    if (string.Equals(activeKey, serverInfo.ServerName, StringComparison.OrdinalIgnoreCase))
                        connStr = active;
                }
            }
            catch { }
        }

        if (string.IsNullOrEmpty(connStr))
        {
            StatusText.Text = "No connection available for this server";
            return;
        }

        serverInfo.ConnectionString = connStr;

        _ = Task.Run(() =>
        {
            var databases = ObjectExplorerHelper.GetDatabases(connStr);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                DatabaseList.Items.Clear();
                foreach (string db in databases)
                    DatabaseList.Items.Add(db);

                // Pre-select the pending target database (from the OE menu) when present; otherwise
                // default to the active database when identifiable, else select all.
                string pendingDb = _pendingTargetDatabase;
                _pendingTargetDatabase = null;
                string current = null;
                try { current = ConnectionHelper.GetCurrentDatabaseName(); } catch { }

                if (!string.IsNullOrEmpty(pendingDb) && databases.Contains(pendingDb))
                    DatabaseList.SelectedItem = pendingDb;
                else if (!string.IsNullOrEmpty(current) && databases.Contains(current))
                    DatabaseList.SelectedItem = current;
                else
                    DatabaseList.SelectAll();

                StatusText.Text = $"{databases.Count} database(s) found";
            }));
        });
    }

    private void RefreshServers_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        LoadServers();
    }

    private void SelectAllDatabases_Click(object sender, RoutedEventArgs e) => DatabaseList.SelectAll();

    private void SelectNoDatabases_Click(object sender, RoutedEventArgs e) => DatabaseList.UnselectAll();

    private ObjectExplorerHelper.ServerInfo GetSelectedServer()
        => (ServerCombo.SelectedItem as ComboBoxItem)?.Tag as ObjectExplorerHelper.ServerInfo;

    private List<string> GetSelectedDatabases()
    {
        var result = new List<string>();
        foreach (var item in DatabaseList.SelectedItems)
            result.Add(item as string ?? item?.ToString() ?? "");
        return result;
    }

    private void UpdateConnectionInfo()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            var server = GetSelectedServer();
            string db = null;
            try { db = ConnectionHelper.GetCurrentDatabaseName(); } catch { }

            ConnectionText.Text = server != null
                ? (string.IsNullOrEmpty(db) ? server.DisplayName : $"{server.DisplayName} / {db}")
                : "";
        }
        catch
        {
            ConnectionText.Text = "";
        }
    }

    // --- Validation ---

    private void Validate_Click(object sender, RoutedEventArgs e) => ExecuteValidation();

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _validateCts?.Cancel();
        _allResults = new List<ValidationIssue>();
        ResultsGrid.ItemsSource = null;
        StatusText.Text = "Select database(s) and click Validate";
        ShowProgress(false);
    }

    private void ExecuteValidation()
    {
        var serverInfo = GetSelectedServer();
        if (serverInfo == null)
        {
            StatusText.Text = "Select a server first";
            return;
        }

        var selectedDatabases = GetSelectedDatabases();
        if (selectedDatabases.Count == 0)
        {
            StatusText.Text = "Select at least one database";
            return;
        }

        string connStr = serverInfo.ConnectionString;
        if (string.IsNullOrEmpty(connStr))
        {
            StatusText.Text = "No connection available for this server";
            return;
        }

        string connectionKey = SchemaCache.Instance.GetConnectionKey(connStr);

        _validateCts?.Cancel();
        _validateCts = new CancellationTokenSource();
        var token = _validateCts.Token;

        StatusText.Text = $"Validating {selectedDatabases.Count} database(s)...";
        ShowProgress(true);

        // Marshals progress reports from the background thread back to the UI.
        var progress = new Progress<ValidationProgress>(OnValidationProgress);

        _ = Task.Run(() =>
        {
            try
            {
                var issues = SchemaValidationService.Validate(connStr, connectionKey, selectedDatabases, token, progress);

                if (token.IsCancellationRequested) return;

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (token.IsCancellationRequested) return;
                    _allResults = issues.ToList();
                    ApplyFilter();
                    UpdateStatusCounts(selectedDatabases.Count);
                    ShowProgress(false);
                }));
            }
            catch (OperationCanceledException)
            {
                Dispatcher.BeginInvoke(new Action(() => ShowProgress(false)));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StatusText.Text = $"Validation error: {ex.Message}";
                    ShowProgress(false);
                }));
            }
        }, token);
    }

    private void ShowProgress(bool running)
    {
        ValidationProgressBar.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        StatusFooter.SetBusy(StatusText, running);
        if (running)
            ValidationProgressBar.Value = 0;
    }

    private void OnValidationProgress(ValidationProgress p)
    {
        if (p.Total > 0)
            ValidationProgressBar.Value = (double)p.Completed / p.Total;
        StatusText.Text = p.Total > 0
            ? $"{p.Message}  ({p.Completed}/{p.Total})"
            : p.Message;
    }

    private void ShowOnlyProblems_Click(object sender, RoutedEventArgs e) => ApplyFilter();

    private IEnumerable<ValidationIssue> NotIgnored()
        => _allResults.Where(i => !_ignores.IsIgnored(i.ReferencedDatabase, i.ReferencedSchema, i.ReferencedEntity));

    private void ApplyFilter()
    {
        var view = NotIgnored();
        if (ShowOnlyProblemsCheck.IsChecked == true)
            view = view.Where(i => i.Severity == IssueSeverity.Error);

        ResultsGrid.ItemsSource = view.ToList();
    }

    private void UpdateStatusCounts(int databaseCount)
    {
        int shown = NotIgnored().Count();
        int errors = NotIgnored().Count(i => i.Severity == IssueSeverity.Error);
        int ignored = _allResults.Count - shown;
        string ignoredSuffix = ignored > 0 ? $" ({ignored} ignored)" : "";

        StatusText.Text = errors == 0
            ? $"No broken references found across {databaseCount} database(s){ignoredSuffix}"
            : $"{errors} broken reference(s) found across {databaseCount} database(s){ignoredSuffix}";
    }

    // --- Ignore management ---

    private void IgnoreObject_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not ValidationIssue issue) return;
        if (string.IsNullOrEmpty(issue.ReferencedEntity))
        {
            StatusText.Text = "This row has no referenced object to ignore.";
            return;
        }

        if (_ignores.AddObject(issue.ReferencedSchema, issue.ReferencedEntity))
        {
            _ignores.Save();
            ApplyFilter();
            StatusText.Text = $"Ignoring object '{ValidationIgnoreList.ObjectKey(issue.ReferencedSchema, issue.ReferencedEntity)}'.";
        }
    }

    private void IgnoreDatabase_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not ValidationIssue issue) return;
        if (string.IsNullOrEmpty(issue.ReferencedDatabase))
        {
            StatusText.Text = "This row has no referenced database to ignore.";
            return;
        }

        if (_ignores.AddDatabase(issue.ReferencedDatabase))
        {
            _ignores.Save();
            ApplyFilter();
            StatusText.Text = $"Ignoring database '{issue.ReferencedDatabase}'.";
        }
    }

    private void ManageIgnores_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ValidationIgnoreDialog(_ignores) { Owner = Window.GetWindow(this) };
        dialog.ShowDialog();
        if (dialog.Changed)
            ApplyFilter();
    }

    // --- Result actions ---

    private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenReferencing();

    private void OpenReferencing_Click(object sender, RoutedEventArgs e) => OpenReferencing();

    private void OpenReferencing()
    {
        if (ResultsGrid.SelectedItem is not ValidationIssue issue)
            return;

        try
        {
            string objectName = $"{issue.ReferencingSchema}.{issue.ReferencingName}";
            string targetConnStr = ConnectionHelper.GetConnectionStringForDatabase(issue.ConnectionString, issue.DatabaseName);

            string connectionKey = issue.ConnectionKey;
            string referencedEntity = issue.ReferencedEntity;

            // Off the UI thread: for a module defined WITH ENCRYPTION the script is built by opening an
            // administrator connection and briefly ALTERing the object, and inline that freezes SSMS.
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                string schemaScript = null;
                Exception error = null;

                await System.Threading.Tasks.Task.Run(() =>
                {
                    try { schemaScript = SchemaQueryService.GetSchemaScript(targetConnStr, objectName, connectionKey); }
                    catch (Exception ex) { error = ex; }
                });

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (error != null)
                {
                    StatusText.Text = $"Error: {error.Message}";
                }
                else if (!string.IsNullOrEmpty(schemaScript))
                {
                    // Highlight the broken reference (the referenced entity name) inside the module body.
                    new SchemaDialog(objectName, schemaScript, targetConnStr, referencedEntity)
                    {
                        Owner = Window.GetWindow(this)
                    }.ShowDialog();
                }
                else
                {
                    StatusText.Text = $"Could not load script for '{objectName}'";
                }
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
        }
    }

    private void RevealInObjectExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not ValidationIssue issue)
            return;

        string label = $"{issue.ReferencingSchema}.{issue.ReferencingName}";
        string serverName = GetSelectedServer()?.ServerName;
        StatusText.Text = $"Revealing {label} in Object Explorer…";

        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                bool located = await ObjectExplorerHelper.RevealObjectAsync(
                    serverName, issue.DatabaseName, issue.ReferencingSchema, issue.ReferencingName, issue.ReferencingType);
                StatusText.Text = located
                    ? $"Revealed {label} in Object Explorer"
                    : $"Could not locate {label} in Object Explorer";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Reveal failed: {ex.Message}";
            }
        });
    }

    private void CopyRows_Click(object sender, RoutedEventArgs e) => CopySelectedRows();

    /// <summary>Copies the selected grid rows to the clipboard as tab-delimited text (visible columns only).</summary>
    private void CopySelectedRows()
    {
        var rows = ResultsGrid.SelectedItems.OfType<ValidationIssue>().ToList();
        if (rows.Count == 0) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Severity\tDatabase\tReferencing Object\tType\tKind\tReferenced\tIssue");
        foreach (var r in rows)
            sb.AppendLine($"{r.SeverityText}\t{r.DatabaseName}\t{r.ReferencingDisplay}\t{r.ReferencingTypeLabel}\t{r.KindLabel}\t{r.ReferencedDisplay}\t{r.Issue}");

        try { Clipboard.SetText(sb.ToString()); } catch { }
    }

    private void CopyReferencing_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is ValidationIssue issue)
        {
            try { Clipboard.SetText(issue.ReferencingDisplay); } catch { }
        }
    }

    private void CopyReferenced_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is ValidationIssue issue)
        {
            try { Clipboard.SetText(issue.ReferencedDisplay); } catch { }
        }
    }
}
