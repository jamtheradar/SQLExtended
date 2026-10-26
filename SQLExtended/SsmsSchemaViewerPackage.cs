using Microsoft.VisualStudio.Shell;
using SQLExtended.Cache;
using SQLExtended.History;
using SQLExtended.ScriptLibrary;
using SQLExtended.Search;
using SQLExtended.Settings;
using SQLExtended.Updates;
using SQLExtended.Validation;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace SQLExtended;

/// <summary>
/// SSMS Schema Viewer package. Auto-loads when a SQL query editor window is opened.
/// Registers Ctrl+Shift+D to show table schema in a dialog.
/// Initializes the shared schema cache on startup.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuidString)]
// Put our extension folder on the assembly probing path so our shipped dependencies
// (ICSharpCode.AvalonEdit, System.Data.SQLite, etc.) resolve via normal Fusion probing.
// Without this, another extension that ships the SAME assembly (e.g. Red Gate SQL Prompt's
// ICSharpCode.AvalonEdit) can satisfy the load via its own AssemblyResolve handler, producing
// two copies in different load contexts and an InvalidCastException when our XAML instantiates
// the control. Probing success short-circuits any such handler.
[ProvideBindingPath]
// Auto-load when a solution exists (SSMS uses this context when connected)
[ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
// Also auto-load when Object Explorer is present, so we can hook its context menu.
[ProvideAutoLoad("d114938f-591c-46cf-a785-500a82d97410", PackageAutoLoadFlags.BackgroundLoad)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(SqlSearchToolWindow), Style = VsDockStyle.Tabbed, DockedWidth = 400, DockedHeight = 600, Orientation = ToolWindowOrientation.Right)]
[ProvideToolWindow(typeof(SqlHistoryToolWindow), Style = VsDockStyle.Tabbed, DockedWidth = 400, DockedHeight = 600, Orientation = ToolWindowOrientation.Right)]
[ProvideToolWindow(typeof(ScriptLibraryToolWindow), Style = VsDockStyle.Tabbed, DockedWidth = 500, DockedHeight = 600, Orientation = ToolWindowOrientation.Right)]
[ProvideToolWindow(typeof(SchemaCacheToolWindow), Style = VsDockStyle.Tabbed, DockedWidth = 400, DockedHeight = 600, Orientation = ToolWindowOrientation.Right)]
[ProvideToolWindow(typeof(SchemaValidationToolWindow), Style = VsDockStyle.Tabbed, DockedWidth = 700, DockedHeight = 500, Orientation = ToolWindowOrientation.Bottom)]
[ProvideToolWindow(typeof(Statistics.StatisticsToolWindow), Style = VsDockStyle.Tabbed, DockedWidth = 900, DockedHeight = 400, Orientation = ToolWindowOrientation.Bottom)]
// Docked at the bottom, beside the results grid it reports on. Single-instance: it follows whichever grid holds the
// selection, so a second copy could only ever show the same figures.
[ProvideToolWindow(typeof(ResultsGrid.Aggregates.AggregatesToolWindow), Style = VsDockStyle.Tabbed, DockedWidth = 900, DockedHeight = 240, Orientation = ToolWindowOrientation.Bottom)]
[ProvideToolWindow(typeof(ResultsGrid.Find.GridFindToolWindow), Style = VsDockStyle.Tabbed, DockedWidth = 900, DockedHeight = 150, Orientation = ToolWindowOrientation.Bottom)]
// All four monitoring dashboards are MultiInstances + Transient. Each is pinned to the server it was opened from
// rather than following the active query window (see Monitoring/MonitorPinning.cs), so one window per server has to
// be able to be open at once — and Transient because a pinned connection cannot be restored at startup, so a window
// VS brought back would come up empty.
[ProvideToolWindow(typeof(Monitoring.AlwaysOn.AgMonitorToolWindow), MultiInstances = true, Transient = true, Style = VsDockStyle.Tabbed, DockedWidth = 1200, DockedHeight = 480, Orientation = ToolWindowOrientation.Bottom)]
[ProvideToolWindow(typeof(Monitoring.Performance.PerfMonitorToolWindow), MultiInstances = true, Transient = true, Style = VsDockStyle.Tabbed, DockedWidth = 1200, DockedHeight = 520, Orientation = ToolWindowOrientation.Bottom)]
[ProvideToolWindow(typeof(Monitoring.Jobs.JobsToolWindow), MultiInstances = true, Transient = true, Style = VsDockStyle.Tabbed, DockedWidth = 1200, DockedHeight = 520, Orientation = ToolWindowOrientation.Bottom)]
[ProvideToolWindow(typeof(Monitoring.Replication.ReplMonitorToolWindow), MultiInstances = true, Transient = true, Style = VsDockStyle.Tabbed, DockedWidth = 1200, DockedHeight = 520, Orientation = ToolWindowOrientation.Bottom)]
public sealed class SsmsSchemaViewerPackage : AsyncPackage
{
    public const string PackageGuidString = "f1e2d3c4-a5b6-7890-abcd-ef1234567890";

    private DatabaseChangeMonitor _dbMonitor;
    private TabHistoryTracker _historyTracker;
    private UpdateCheckService _updateCheckService;

    protected override async Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        // 1. Crucial for SSMS 22: Yield control back briefly if the shell is in the middle of a cold-start background loop
        await Task.Yield();

        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        // Before anything else that can fail. Every step below reports into the session log, and a command
        // that failed to register is exactly the kind of thing this exists to make visible — so the switch
        // has to be read first. This is the UI thread, which is where the settings may be read from.
        try
        {
            var diagnosticSettings = Settings.SQLExtendedSettings.Current;
            Diagnostics.SQLExtendedLog.Configure(diagnosticSettings.DiagnosticLogEnabled, diagnosticSettings.DiagnosticLogToFile);
        }
        catch { /* the logger is not allowed to break the package it reports on */ }

        // Installs the dark or light palette every WPF window of the extension paints with, and follows
        // theme switches. Before the commands, so nothing can open a window that resolves no colours.
        try { Theme.ThemeManager.Initialize(); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "Theme init failed", ex); }

        // Writes the chosen comment colour scheme into Fonts and Colors, but only when it is not already
        // the one in force — so hand-tuned entries are not overwritten on every start. Also starts
        // listening for a dark/light theme switch, which needs the other variant of the same scheme.
        try { Comments.CommentThemeApplier.Initialize(); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "Comment theme init failed", ex); }

        try { await SchemaViewerCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "SchemaViewerCommand init failed", ex); }

        try { await Definition.OpenDefinitionCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "OpenDefinitionCommand init failed", ex); }

        try { await ObjectExplorer.LocateInObjectExplorerCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "LocateInObjectExplorerCommand init failed", ex); }

        try { await FormatCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "FormatCommand init failed", ex); }

        try { await FormatSelectionCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "FormatSelectionCommand init failed", ex); }

        try { await Qualify.QualifyColumnsCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "QualifyColumnsCommand init failed", ex); }

        try { await Align.AlignOnCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "AlignOnCommand init failed", ex); }

        try { await RefreshCacheCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "RefreshCacheCommand init failed", ex); }

        try { await SchemaCacheCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "SchemaCacheCommand init failed", ex); }

        try { await SqlSearchCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "SqlSearchCommand init failed", ex); }

        try { await SchemaValidationCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "SchemaValidationCommand init failed", ex); }

        try { await SettingsCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "SettingsCommand init failed", ex); }

        try { await SqlHistoryCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "SqlHistoryCommand init failed", ex); }

        try { await CheckForUpdatesCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "CheckForUpdatesCommand init failed", ex); }

        try { await ScriptLibraryCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "ScriptLibraryCommand init failed", ex); }

        try { await ResultsGrid.ScriptResultsAsInsertCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "ScriptResultsAsInsertCommand init failed", ex); }

        try { await ResultsGrid.Aggregates.AggregatesCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "AggregatesCommand init failed", ex); }

        try { await ResultsGrid.Find.GridFindCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "GridFindCommand init failed", ex); }

        try { await ResultsGrid.Columns.GoToColumnCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "GoToColumnCommand init failed", ex); }

        try { await ResultsGrid.CopyAsInFilterCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "CopyAsInFilterCommand init failed", ex); }

        // Only does anything when the (off by default) "bring the window to the front when cells are selected"
        // setting is on — that is the one case needing a grid watcher with the window closed.
        try { ResultsGrid.Aggregates.GridAggregatesWatcher.ArmAutoShow(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "Aggregates auto-show arm failed", ex); }

        try { await Statistics.StatisticsCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "StatisticsCommand init failed", ex); }

        try { await Monitoring.AlwaysOn.AgMonitorCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "AgMonitorCommand init failed", ex); }

        try { await Monitoring.Performance.PerfMonitorCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "PerfMonitorCommand init failed", ex); }

        try { await Monitoring.Jobs.JobsCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "JobsCommand init failed", ex); }

        try { await Monitoring.Replication.ReplMonitorCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "ReplMonitorCommand init failed", ex); }

        try { await EnvTabs.EnvTabsCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "EnvTabsCommand init failed", ex); }

        try { await EnvTabs.TabHighlightCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "TabHighlightCommand init failed", ex); }

        // Hook the Object Explorer context menu (reflection-based; tolerant of OE not being ready yet).
        try { _ = ObjectExplorer.ObjectExplorerMenuService.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "ObjectExplorerMenuService init failed", ex); }

        // Group Object Explorer server nodes into folders mirroring the Registered Servers groups.
        try { _ = ObjectExplorer.ServerGroupFolderService.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "ServerGroupFolderService init failed", ex); }

        try { await ObjectExplorer.RegroupServersCommand.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "RegroupServersCommand init failed", ex); }

        // Inject the top-level "SQLExtended" menu at runtime — SSMS 22's shell doesn't reliably merge the
        // VSCT-declared top-level menu, so we build it against the live menu bar instead.
        try { _ = MainMenuService.InitializeAsync(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "MainMenuService init failed", ex); }

        // Initialize status bar indicator
        try { CacheStatusBar.Initialize(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "CacheStatusBar init failed", ex); }

        // Initialize the shared schema cache (loads persisted data from SQLite)
        await Task.Run(() =>
        {
            try
            {
                SchemaCache.Instance.Initialize();
            }
            catch (Exception ex)
            {
                Diagnostics.SQLExtendedLog.Error("Package", "SchemaCache init failed", ex);
            }
        });

        // Confirm before executing UPDATE/DELETE without WHERE. Hooks the shell's Query.Execute; the setting is read per execution.
        try { Safety.UnsafeDmlGuard.Initialize(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "UnsafeDmlGuard init failed", ex); }

        // Colour and rename query tabs by connection. Off unless the user has enabled it — Start()
        // checks the setting itself and does nothing when it's off.
        try { EnvTabs.EnvTabsService.Start(this); }
        catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "EnvTabsService init failed", ex); }

        // Start monitoring for database switches
        try
        {
            _dbMonitor = new DatabaseChangeMonitor();
            _dbMonitor.Start();
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Error("Package", "DatabaseChangeMonitor init failed", ex);
        }

        // Initialize history store + start tab history tracker on a background thread.
        await Task.Run(() =>
        {
            try { HistoryService.Instance.Initialize(); }
            catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "HistoryService init failed", ex); }
        });

        // Pre-load the script library (curated manifest + user file) off the UI thread.
        await Task.Run(() =>
        {
            try { ScriptLibraryService.Instance.Initialize(); }
            catch (Exception ex) { Diagnostics.SQLExtendedLog.Error("Package", "ScriptLibraryService init failed", ex); }
        });

        try
        {
            _historyTracker = new TabHistoryTracker();
            _historyTracker.Start();
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Error("Package", "TabHistoryTracker init failed", ex);
        }

        // Fire-and-forget update check. Runs on a background task; cheap when feed URL is unset or cooldown is active.
        try
        {
            _updateCheckService = new UpdateCheckService(this);
            _updateCheckService.StartBackgroundCheck();
        }
        catch (Exception ex)
        {
            Diagnostics.SQLExtendedLog.Error("Package", "UpdateCheckService init failed", ex);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { ObjectExplorer.ObjectExplorerMenuService.Dispose(); }
            catch { }
            try { ObjectExplorer.ServerGroupFolderService.Dispose(); }
            catch { }
            try { _historyTracker?.Dispose(); }
            catch { }
            try { HistoryService.Instance.Dispose(); }
            catch { }
            try { _dbMonitor?.Dispose(); }
            catch { }
            try { EnvTabs.EnvTabsService.Instance?.Dispose(); }
            catch { }
            try { CacheStatusBar.Dispose(); }
            catch { }
            try { SettingsCommand.Dispose(); }
            catch { }
            try { SchemaCache.Instance.Dispose(); }
            catch { }
        }
        base.Dispose(disposing);
    }
}
