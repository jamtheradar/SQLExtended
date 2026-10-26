using Microsoft.VisualStudio.Shell;
using Microsoft.Win32;
using SQLExtended.Cache;
using SQLExtended.Diagnostics;
using SQLExtended.Formatting;
using SQLExtended.History;
using SQLExtended.Snippets;
using SQLExtended.Updates;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SQLExtended.Settings;

public partial class SQLExtendedSettingsDialog : Window
{
    private SQLExtendedSettings _settings;
    private bool _isLoading;

    /// <summary>Backs the Diagnostics tab's grid. Mirrors <see cref="SQLExtendedLog.Buffer"/>, oldest first.</summary>
    private readonly ObservableCollection<DiagnosticLogEntry> _logEntries = new ObservableCollection<DiagnosticLogEntry>();

    public SQLExtendedSettingsDialog(SQLExtendedSettings settings)
        : this(settings, initialTabHeader: null)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
    }

    public SQLExtendedSettingsDialog(SQLExtendedSettings settings, string initialTabHeader)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        InitializeComponent();
        _settings = settings.Clone();
        LoadSettingsToUI();
        LoadSnippetList();
        LoadInfoLabels();
        LoadShortcuts();
        AttachDiagnosticLog();

        if (!string.IsNullOrEmpty(initialTabHeader))
            SelectTabByHeader(initialTabHeader);
    }

    /// <summary>The Keyboard Shortcuts tab: the menu's commands and their live bindings, so neither list can go stale.</summary>
    private void LoadShortcuts()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            ShortcutsList.ItemsSource = MainMenuService.CommandShortcuts()
                .Select(c => new ShortcutItem(c.Caption.TrimEnd('.'), c.Shortcuts.Count == 0 ? "—" : string.Join("   or   ", c.Shortcuts)))
                .ToList();
        }
        catch (Exception ex)
        {
            SQLExtendedLog.Warning("Settings", "Could not read keyboard shortcuts", ex);
        }
    }

    private void SelectTabByHeader(string header)
    {
        foreach (var item in Tabs.Items)
        {
            if (item is TabItem tab && string.Equals(tab.Header as string, header, StringComparison.OrdinalIgnoreCase))
            {
                tab.IsSelected = true;
                return;
            }
        }
    }

    private void LoadSettingsToUI()
    {
        _isLoading = true;
        try
        {
            // IntelliSense
            ChkIntelliSenseEnabled.IsChecked = _settings.IntelliSenseEnabled;
            ChkSuppressBuiltIn.IsChecked = _settings.SuppressBuiltInIntelliSense;
            ChkAutoTrigger.IsChecked = _settings.AutoTriggerAfterKeyword;
            ChkExpandProcParams.IsChecked = _settings.ExpandProcedureParameters;
            ChkAutoAliasTables.IsChecked = _settings.AutoAliasTables;
            CboLinkedServers.SelectedIndex = (int)_settings.LinkedServerCompletion;
            ChkCommitOnTab.IsChecked = _settings.CommitOnTab;
            ChkCommitOnEnter.IsChecked = _settings.CommitOnEnter;
            ChkCommitOnSpace.IsChecked = _settings.CommitOnSpace;
            ChkRecaseKeywords.IsChecked = _settings.RecaseKeywordsWhileTyping;
            ChkAutoCloseQuotes.IsChecked = _settings.AutoCloseQuotes;
            ChkTabExpandsStar.IsChecked = _settings.TabExpandsSelectStar;
            ChkCamelCase.IsChecked = _settings.CamelCaseMatching;

            // Editor — rainbow parentheses. The combo starts at 2 colours, so index 0 is 2.
            ChkRainbowParens.IsChecked = _settings.RainbowParensEnabled;
            CboRainbowLevels.SelectedIndex = Math.Min(Math.Max(_settings.RainbowParensLevels, 2), 7) - 2;
            ChkRainbowUnmatched.IsChecked = _settings.RainbowParensHighlightUnmatched;
            ChkUnsafeDmlGuard.IsChecked = _settings.UnsafeDmlGuardEnabled;
            ChkRainbowBlocks.IsChecked = _settings.RainbowParensIncludeBlocks;
            ChkCommentTags.IsChecked = _settings.CommentTagsEnabled;

            // Items are the schemes themselves; the combo shows DisplayName via the ToString below, so a
            // scheme added to CommentThemes appears here without touching this file.
            CboCommentScheme.ItemsSource = Comments.CommentThemes.All.Select(s => new SchemeItem(s)).ToList();
            CboCommentScheme.SelectedItem = ((System.Collections.Generic.List<SchemeItem>)CboCommentScheme.ItemsSource)
                .FirstOrDefault(i => i.Scheme == _settings.CommentScheme);
            ChkShowColumnType.IsChecked = _settings.ShowColumnTypeInfo;
            ChkShowRowCounts.IsChecked = _settings.ShowRowCounts;

            // Cache
            TxtRefreshInterval.Text = _settings.AutoRefreshIntervalMinutes.ToString();
            TxtMaxCacheAge.Text = _settings.MaxCacheAgeDays.ToString();
            ChkAutoLoadOnConnect.IsChecked = _settings.AutoLoadOnConnect;
            ChkDetectDdl.IsChecked = _settings.DetectDdlChanges;
            ChkServerGrouping.IsChecked = _settings.ServerGroupingEnabled;
            ChkDecryptEncryptedModules.IsChecked = _settings.DecryptEncryptedModules;

            // Search
            CboSearchScope.SelectedIndex = (int)_settings.DefaultSearchScope;
            ChkSearchObjectNames.IsChecked = _settings.DefaultSearchObjectNames;
            ChkSearchColumnNames.IsChecked = _settings.DefaultSearchColumnNames;
            ChkSearchDefinitions.IsChecked = _settings.DefaultSearchDefinitions;
            TxtMaxResults.Text = _settings.DefaultMaxSearchResults.ToString();
            ChkTempTableDropIfExists.IsChecked = _settings.TempTableDropIfExists;
            CboDefinitionScriptMode.SelectedIndex = (int)_settings.DefinitionScriptMode;

            // History
            ChkHistoryEnabled.IsChecked = _settings.HistoryEnabled;
            TxtHistoryDebounce.Text = _settings.HistoryDebounceMs.ToString();
            TxtHistoryMaxBytes.Text = _settings.HistoryMaxTextBytes.ToString();
            TxtHistoryRetentionDays.Text = _settings.HistoryRetentionDays.ToString();
            TxtHistoryMaxPerDoc.Text = _settings.HistoryMaxPerDocument.ToString();

            // Statistics
            ChkStatsSuppressZeroColumns.IsChecked = _settings.StatisticsSuppressZeroColumns;
            ChkStatsFormatTempTableNames.IsChecked = _settings.StatisticsFormatTempTableNames;
            CboStatsLanguage.SelectedIndex = (int)_settings.StatisticsLanguage;

            // Grid Aggregates. The per-aggregate column toggles are not here — they live on the window's
            // own checkboxes, which is where they are switched while reading the numbers.
            ChkAggregatesAutoShow.IsChecked = _settings.GridAggregatesAutoShow;
            TxtAggregatesMaxCells.Text = _settings.GridAggregatesMaxCells.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TxtAggregatesDebounceMs.Text = _settings.GridAggregatesDebounceMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TxtAggregatesPollSeconds.Text = _settings.GridAggregatesPollSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Find in Results. Its match/case/regex toggles are likewise on the window itself.
            TxtFindMaxCells.Text = _settings.GridFindMaxCells.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TxtFindMaxMatches.Text = _settings.GridFindMaxMatches.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Agent Jobs
            TxtJobsHiddenCategories.Text = _settings.JobsHiddenCategories ?? "";
            TxtJobsRefreshSeconds.Text = _settings.JobsRefreshSeconds.ToString();
            TxtJobsHistoryDays.Text = _settings.JobsHistoryDays.ToString();
            TxtJobsAverageSampleRuns.Text = _settings.JobsAverageSampleRuns.ToString();

            // Monitoring thresholds. Invariant culture on the way out as well as in, so what is displayed is
            // what the settings file holds regardless of the machine's decimal separator.
            TxtPerfRecentDumpDays.Text = _settings.PerfRecentDumpDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TxtAgRpoWarning.Text = Num(_settings.AgRpoWarningSeconds);
            TxtAgRpoCritical.Text = Num(_settings.AgRpoCriticalSeconds);
            TxtAgLagWarning.Text = Num(_settings.AgSecondaryLagWarningSeconds);
            TxtAgSendQueueWarning.Text = _settings.AgSendQueueWarningKb.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TxtAgRedoQueueWarning.Text = _settings.AgRedoQueueWarningKb.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TxtAgCommitDelayWarning.Text = Num(_settings.AgCommitDelayWarningMs);

            TxtReplRefreshSeconds.Text = _settings.ReplRefreshSeconds.ToString();
            TxtReplLatencyWarning.Text = Num(_settings.ReplLatencyWarningSeconds);
            TxtReplLatencyCritical.Text = Num(_settings.ReplLatencyCriticalSeconds);
            TxtReplExpiryFraction.Text = Num(_settings.ReplExpiryWarningFraction);
            TxtReplPendingWarning.Text = _settings.ReplPendingCommandWarning.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TxtReplErrorRows.Text = _settings.ReplErrorRows.ToString();

            // Updates
            ChkUpdateCheckEnabled.IsChecked = _settings.UpdateCheckEnabled;
            TxtUpdateFeedUrl.Text = _settings.UpdateFeedUrl ?? "";
            UpdateUpdatesStatusLabels();

            // Diagnostics
            ChkDiagnosticLogEnabled.IsChecked = _settings.DiagnosticLogEnabled;
            ChkDiagnosticLogToFile.IsChecked = _settings.DiagnosticLogToFile;
        }
        finally
        {
            _isLoading = false;
        }

        // Outside the guard: the handler these drive returns early while loading.
        UpdateDiagnosticLogVisibility();
    }

    private void ReadSettingsFromUI()
    {
        // IntelliSense
        _settings.IntelliSenseEnabled = ChkIntelliSenseEnabled.IsChecked == true;
        _settings.SuppressBuiltInIntelliSense = ChkSuppressBuiltIn.IsChecked == true;
        _settings.AutoTriggerAfterKeyword = ChkAutoTrigger.IsChecked == true;
        _settings.ExpandProcedureParameters = ChkExpandProcParams.IsChecked == true;
        _settings.AutoAliasTables = ChkAutoAliasTables.IsChecked == true;
        _settings.LinkedServerCompletion = (LinkedServerCompletionLevel)Math.Max(0, CboLinkedServers.SelectedIndex);
        _settings.CommitOnTab = ChkCommitOnTab.IsChecked == true;
        _settings.CommitOnEnter = ChkCommitOnEnter.IsChecked == true;
        _settings.CommitOnSpace = ChkCommitOnSpace.IsChecked == true;
        _settings.RecaseKeywordsWhileTyping = ChkRecaseKeywords.IsChecked == true;
        _settings.AutoCloseQuotes = ChkAutoCloseQuotes.IsChecked == true;
        _settings.TabExpandsSelectStar = ChkTabExpandsStar.IsChecked == true;
        _settings.CamelCaseMatching = ChkCamelCase.IsChecked == true;
        _settings.ShowColumnTypeInfo = ChkShowColumnType.IsChecked == true;
        _settings.ShowRowCounts = ChkShowRowCounts.IsChecked == true;

        // Editor
        _settings.RainbowParensEnabled = ChkRainbowParens.IsChecked == true;
        _settings.RainbowParensLevels = Math.Max(0, CboRainbowLevels.SelectedIndex) + 2;
        _settings.RainbowParensHighlightUnmatched = ChkRainbowUnmatched.IsChecked == true;
        _settings.UnsafeDmlGuardEnabled = ChkUnsafeDmlGuard.IsChecked == true;
        _settings.RainbowParensIncludeBlocks = ChkRainbowBlocks.IsChecked == true;
        _settings.CommentTagsEnabled = ChkCommentTags.IsChecked == true;
        if (CboCommentScheme.SelectedItem is SchemeItem scheme)
            _settings.CommentScheme = scheme.Scheme;

        // Cache
        _settings.AutoRefreshIntervalMinutes = ParseInt(TxtRefreshInterval.Text, 5);
        _settings.MaxCacheAgeDays = ParseInt(TxtMaxCacheAge.Text, 7);
        _settings.AutoLoadOnConnect = ChkAutoLoadOnConnect.IsChecked == true;
        _settings.DetectDdlChanges = ChkDetectDdl.IsChecked == true;
        _settings.ServerGroupingEnabled = ChkServerGrouping.IsChecked == true;
        _settings.DecryptEncryptedModules = ChkDecryptEncryptedModules.IsChecked == true;

        // Search
        _settings.DefaultSearchScope = (SearchScope)CboSearchScope.SelectedIndex;
        _settings.DefaultSearchObjectNames = ChkSearchObjectNames.IsChecked == true;
        _settings.DefaultSearchColumnNames = ChkSearchColumnNames.IsChecked == true;
        _settings.DefaultSearchDefinitions = ChkSearchDefinitions.IsChecked == true;
        _settings.DefaultMaxSearchResults = ParseInt(TxtMaxResults.Text, 200);
        _settings.TempTableDropIfExists = ChkTempTableDropIfExists.IsChecked == true;
        _settings.DefinitionScriptMode = (Definition.DefinitionScriptMode)Math.Max(0, CboDefinitionScriptMode.SelectedIndex);

        // History
        _settings.HistoryEnabled = ChkHistoryEnabled.IsChecked == true;
        _settings.HistoryDebounceMs = ParseInt(TxtHistoryDebounce.Text, 2000);
        _settings.HistoryMaxTextBytes = ParseInt(TxtHistoryMaxBytes.Text, 5 * 1024 * 1024);
        _settings.HistoryRetentionDays = ParseInt(TxtHistoryRetentionDays.Text, 30);
        _settings.HistoryMaxPerDocument = ParseInt(TxtHistoryMaxPerDoc.Text, 50);

        // Statistics
        _settings.StatisticsSuppressZeroColumns = ChkStatsSuppressZeroColumns.IsChecked == true;
        _settings.StatisticsFormatTempTableNames = ChkStatsFormatTempTableNames.IsChecked == true;
        _settings.StatisticsLanguage = (StatisticsLanguageOption)Math.Max(0, CboStatsLanguage.SelectedIndex);

        // Grid Aggregates. Each falls back to its default rather than to zero: a cap of 0 would refuse every
        // selection and a poll of 0 would spin, both of which read as the feature being broken.
        _settings.GridAggregatesAutoShow = ChkAggregatesAutoShow.IsChecked == true;
        _settings.GridAggregatesMaxCells = Math.Max(1L, ParseLong(TxtAggregatesMaxCells.Text, 250_000L));
        _settings.GridAggregatesDebounceMs = ParseInt(TxtAggregatesDebounceMs.Text, 200);
        _settings.GridAggregatesPollSeconds = Math.Max(1, ParseInt(TxtAggregatesPollSeconds.Text, 2));

        // Find in Results. Same reasoning: a cap of 0 would find nothing at all and read as a broken search.
        _settings.GridFindMaxCells = Math.Max(1L, ParseLong(TxtFindMaxCells.Text, 2_000_000L));
        _settings.GridFindMaxMatches = Math.Max(1, ParseInt(TxtFindMaxMatches.Text, 5_000));

        // Agent Jobs
        _settings.JobsHiddenCategories = TxtJobsHiddenCategories.Text?.Trim() ?? "";
        _settings.JobsRefreshSeconds = ParseInt(TxtJobsRefreshSeconds.Text, 15);
        _settings.JobsHistoryDays = ParseInt(TxtJobsHistoryDays.Text, 7);
        _settings.JobsAverageSampleRuns = ParseInt(TxtJobsAverageSampleRuns.Text, 10);

        // Monitoring thresholds
        _settings.PerfRecentDumpDays = ParseInt(TxtPerfRecentDumpDays.Text, 30);
        _settings.AgRpoWarningSeconds = ParseDouble(TxtAgRpoWarning.Text, 60d);
        _settings.AgRpoCriticalSeconds = ParseDouble(TxtAgRpoCritical.Text, 300d);
        _settings.AgSecondaryLagWarningSeconds = ParseDouble(TxtAgLagWarning.Text, 60d);
        _settings.AgSendQueueWarningKb = ParseLong(TxtAgSendQueueWarning.Text, 100_000L);
        _settings.AgRedoQueueWarningKb = ParseLong(TxtAgRedoQueueWarning.Text, 100_000L);
        _settings.AgCommitDelayWarningMs = ParseDouble(TxtAgCommitDelayWarning.Text, 20d);

        _settings.ReplRefreshSeconds = ParseInt(TxtReplRefreshSeconds.Text, 15);
        _settings.ReplLatencyWarningSeconds = ParseDouble(TxtReplLatencyWarning.Text, 60d);
        _settings.ReplLatencyCriticalSeconds = ParseDouble(TxtReplLatencyCritical.Text, 300d);
        _settings.ReplExpiryWarningFraction = ParseDouble(TxtReplExpiryFraction.Text, 0.75d);
        _settings.ReplPendingCommandWarning = ParseLong(TxtReplPendingWarning.Text, 100_000L);
        _settings.ReplErrorRows = ParseInt(TxtReplErrorRows.Text, 200);

        // Updates
        _settings.UpdateCheckEnabled = ChkUpdateCheckEnabled.IsChecked == true;
        _settings.UpdateFeedUrl = TxtUpdateFeedUrl.Text?.Trim() ?? "";

        // Diagnostics
        _settings.DiagnosticLogEnabled = ChkDiagnosticLogEnabled.IsChecked == true;
        _settings.DiagnosticLogToFile = ChkDiagnosticLogToFile.IsChecked == true;
    }

    /// <summary>Invariant-culture number text for the threshold boxes, matching how <see cref="ParseDouble"/> reads them back.</summary>
    private static string Num(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void UpdateUpdatesStatusLabels()
    {
        TxtUpdateCurrentVersion.Text = $"Current version: {UpdateCheckService.GetCurrentVersion()}";

        TxtUpdateLastCheck.Text =
            _settings.UpdateLastCheckUtc == DateTime.MinValue
                ? "Last checked: never"
                : $"Last checked: {_settings.UpdateLastCheckUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

        TxtUpdateSkipped.Text = string.IsNullOrEmpty(_settings.UpdateSkippedVersion)
            ? "Skipped version: (none)"
            : $"Skipped version: {_settings.UpdateSkippedVersion}";
    }

    private void CheckUpdatesNow_Click(object sender, RoutedEventArgs e)
    {
        // Persist current edits before kicking off the check so the service sees the user's URL/enabled state.
        ReadSettingsFromUI();
        _settings.UpdateLastCheckUtc = DateTime.MinValue; // bypass cooldown
        _settings.Save();

        UpdateCheckService.RunManualCheck();
        MessageBox.Show(
            "Checking for updates… you'll see an InfoBar at the top of SSMS if a new version is available.",
            "Check for Updates",
            MessageBoxButton.OK,
            MessageBoxImage.Information
        );

        UpdateUpdatesStatusLabels();
    }

    private void ClearSkippedVersion_Click(object sender, RoutedEventArgs e)
    {
        _settings.UpdateSkippedVersion = "";
        _settings.Save();
        UpdateUpdatesStatusLabels();
    }

    private void LoadInfoLabels()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SQLExtended", "SSMS");

        RunFormatterPath.Text = Path.Combine(appData, "formatter-options.json");
        TxtActiveProfile.Text = FormatterProfileManager.Instance.ActiveProfileName;
        RunCachePath.Text = Path.Combine(appData, "schema-cache.db");
        RunHistoryPath.Text = Path.Combine(appData, "history.db");

        UpdateCacheStatus();
        UpdateHistoryStatus();
    }

    private void UpdateHistoryStatus()
    {
        try
        {
            long rows = HistoryService.Instance.IsInitialized ? HistoryService.Instance.RowCount : 0;
            TxtHistoryStatus.Text = HistoryService.Instance.IsInitialized ? $"{rows:N0} snapshot(s) stored." : "History store not initialized.";
        }
        catch
        {
            TxtHistoryStatus.Text = "Unable to read history status.";
        }
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Delete all captured history snapshots? This cannot be undone.",
            "Clear History",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question
        );

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            HistoryService.Instance.ClearAll();
            UpdateHistoryStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Clear History", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenHistoryFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SQLExtended", "SSMS");
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Open Folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateCacheStatus()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            string connStr = ConnectionHelper.GetActiveConnectionString();
            string db = ConnectionHelper.GetCurrentDatabaseName();
            if (!string.IsNullOrEmpty(connStr) && !string.IsNullOrEmpty(db))
            {
                var cache = SchemaCache.Instance;
                string connKey = cache.GetConnectionKey(connStr);
                var state = cache.GetState(connKey, db);
                int count = cache.GetObjectCount(connKey, db);
                TxtCacheStatus.Text = $"Current: {db} \u2014 {state} ({count:N0} objects)";
            }
            else
            {
                TxtCacheStatus.Text = "No active database connection.";
            }
        }
        catch
        {
            TxtCacheStatus.Text = "Unable to determine cache status.";
        }
    }

    // --- Snippet management ---

    private void LoadSnippetList()
    {
        SnippetList.ItemsSource = null;
        SnippetList.ItemsSource = SnippetManager.Instance.Snippets;
    }

    private readonly ObservableCollection<PlaceholderDefaultItem> _placeholderDefaults = new ObservableCollection<PlaceholderDefaultItem>();

    private void SnippetList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SnippetList.SelectedItem is SqlSnippet snippet)
        {
            _isLoading = true;
            try
            {
                TxtSnippetCode.Text = snippet.Code;
                TxtSnippetTitle.Text = snippet.Title;
                TxtSnippetDescription.Text = snippet.Description;
                TxtSnippetBody.Text = snippet.Body?.Replace("\n", "\r\n") ?? "";
                UpdatePlaceholderDefaults(snippet.Body, snippet.Defaults);
            }
            finally
            {
                _isLoading = false;
            }
        }
    }

    private void NewSnippet_Click(object sender, RoutedEventArgs e)
    {
        SnippetList.SelectedItem = null;
        TxtSnippetCode.Text = "";
        TxtSnippetTitle.Text = "";
        TxtSnippetDescription.Text = "";
        TxtSnippetBody.Text = "";
        _placeholderDefaults.Clear();
        PlaceholderDefaultsBorder.Visibility = Visibility.Collapsed;
        TxtSnippetCode.Focus();
    }

    private void TxtSnippetBody_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading)
            return;

        string body = TxtSnippetBody.Text?.Replace("\r\n", "\n") ?? "";
        UpdatePlaceholderDefaults(body, null);
    }

    private void UpdatePlaceholderDefaults(string body, Dictionary<string, string> existingDefaults)
    {
        var customNames = SnippetPlaceholderResolver.GetCustomPlaceholderNames(body ?? "");

        if (customNames.Count == 0)
        {
            _placeholderDefaults.Clear();
            PlaceholderDefaultsBorder.Visibility = Visibility.Collapsed;
            return;
        }

        // Preserve existing values in the grid for placeholders that still exist
        var currentValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _placeholderDefaults)
            currentValues[item.Name] = item.DefaultValue;

        _placeholderDefaults.Clear();
        foreach (var name in customNames)
        {
            string defaultValue = "";
            // Priority: current grid value > snippet.Defaults > placeholder name
            if (currentValues.TryGetValue(name, out string gridVal))
                defaultValue = gridVal;
            else if (existingDefaults != null && existingDefaults.TryGetValue(name, out string defVal))
                defaultValue = defVal;
            else
                defaultValue = name;

            _placeholderDefaults.Add(new PlaceholderDefaultItem { Name = name, DefaultValue = defaultValue });
        }

        PlaceholderDefaultsGrid.ItemsSource = _placeholderDefaults;
        PlaceholderDefaultsBorder.Visibility = Visibility.Visible;
    }

    private void SaveSnippet_Click(object sender, RoutedEventArgs e)
    {
        string code = TxtSnippetCode.Text?.Trim();
        if (string.IsNullOrEmpty(code))
        {
            MessageBox.Show("Code is required.", "Save Snippet", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Collect placeholder defaults from the grid
        Dictionary<string, string> defaults = null;
        if (_placeholderDefaults.Count > 0)
        {
            defaults = new Dictionary<string, string>();
            foreach (var item in _placeholderDefaults)
            {
                if (!string.IsNullOrEmpty(item.DefaultValue))
                    defaults[item.Name] = item.DefaultValue;
            }
            if (defaults.Count == 0)
                defaults = null;
        }

        var snippet = new SqlSnippet
        {
            Code = code,
            Title = TxtSnippetTitle.Text?.Trim() ?? code,
            Description = TxtSnippetDescription.Text?.Trim() ?? "",
            Body = TxtSnippetBody.Text?.Replace("\r\n", "\n") ?? "",
            Defaults = defaults,
        };

        SnippetManager.Instance.SaveSnippet(snippet);
        LoadSnippetList();

        // Re-select the saved snippet
        SnippetList.SelectedItem = SnippetManager.Instance.Snippets.FirstOrDefault(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase));
    }

    private void DeleteSnippet_Click(object sender, RoutedEventArgs e)
    {
        if (SnippetList.SelectedItem is not SqlSnippet snippet)
            return;

        var result = MessageBox.Show($"Delete snippet '{snippet.Code}'?", "Delete Snippet", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            SnippetManager.Instance.RemoveSnippet(snippet.Code);
            LoadSnippetList();
            NewSnippet_Click(sender, e);
        }
    }

    private void ImportSnippets_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "JSON Files (*.json)|*.json|All Files (*.*)|*.*", Title = "Import Snippets" };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                string json = File.ReadAllText(dlg.FileName);
                var imported = Newtonsoft.Json.JsonConvert.DeserializeObject<List<SqlSnippet>>(json);
                if (imported == null || imported.Count == 0)
                {
                    MessageBox.Show("No snippets found in file.", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                foreach (var snippet in imported)
                    SnippetManager.Instance.SaveSnippet(snippet);

                LoadSnippetList();
                MessageBox.Show($"Imported {imported.Count} snippet(s).", "Import", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to import: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void ImportSqlPromptSnippets_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "SQL Prompt Snippets (*.json)|*.json|All Files (*.*)|*.*",
            Title = "Import SQL Prompt Snippets",
            Multiselect = true,
        };

        if (dlg.ShowDialog() != true)
            return;

        int imported = 0;
        var errors = new List<string>();

        foreach (var file in dlg.FileNames)
        {
            try
            {
                var snippets = SqlPromptSnippetImporter.Convert(File.ReadAllText(file));
                foreach (var snippet in snippets)
                {
                    SnippetManager.Instance.SaveSnippet(snippet);
                    imported++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        LoadSnippetList();

        if (errors.Count > 0)
        {
            MessageBox.Show(
                $"Imported {imported} snippet(s).\n\nSkipped {errors.Count} file(s):\n{string.Join("\n", errors)}",
                "Import SQL Prompt",
                MessageBoxButton.OK,
                MessageBoxImage.Warning
            );
        }
        else if (imported == 0)
        {
            MessageBox.Show("No snippets found in the selected file(s).", "Import SQL Prompt", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            MessageBox.Show($"Imported {imported} snippet(s).", "Import SQL Prompt", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ExportSnippets_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "JSON Files (*.json)|*.json",
            Title = "Export Snippets",
            FileName = "sqlextended-snippets.json",
        };

        if (dlg.ShowDialog() == true)
        {
            try
            {
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(SnippetManager.Instance.Snippets, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(dlg.FileName, json);
                MessageBox.Show("Snippets exported successfully.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to export: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void InsertPlaceholder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string placeholder)
        {
            int caretIndex = TxtSnippetBody.CaretIndex;
            TxtSnippetBody.Text = TxtSnippetBody.Text.Insert(caretIndex, placeholder);
            TxtSnippetBody.CaretIndex = caretIndex + placeholder.Length;
            TxtSnippetBody.Focus();
        }
    }

    // --- Cache actions ---

    private void RefreshCurrentCache_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            string connStr = ConnectionHelper.GetActiveConnectionString();
            string db = ConnectionHelper.GetCurrentDatabaseName();
            if (string.IsNullOrEmpty(connStr) || string.IsNullOrEmpty(db))
            {
                MessageBox.Show("No active database connection.", "Refresh Cache", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            CacheStatusBar.SetText($"Schema: Refreshing {db}...");
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                await SchemaCache.Instance.LoadDatabaseAsync(connStr, db, forceFullRefresh: true);
            });
            TxtCacheStatus.Text = $"Refreshing {db}...";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Refresh Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshAllCache_Click(object sender, RoutedEventArgs e)
    {
        CacheStatusBar.SetText("Schema: Refreshing all databases...");
        SchemaCache.Instance.RefreshAllAsync();
        TxtCacheStatus.Text = "Refreshing all databases...";
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Clear all cached schema data? This will remove all in-memory and persisted cache.",
            "Clear Cache",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question
        );

        if (result == MessageBoxResult.Yes)
        {
            SchemaCache.Instance.ClearAll();
            CacheStatusBar.SetText("Schema: Cache cleared");
            TxtCacheStatus.Text = "Cache cleared.";
        }
    }

    // --- Formatter ---

    private void OpenFormatterOptions_Click(object sender, RoutedEventArgs e)
    {
        var options = FormatterOptions.Load();
        var dialog = new FormatterOptionsDialog(options);
        dialog.ShowDialog();

        // Refresh active profile display in case it changed
        TxtActiveProfile.Text = FormatterProfileManager.Instance.ActiveProfileName;
    }

    // --- Diagnostics tab ---

    /// <summary>
    /// Binds the live log. The collection is fed from <see cref="SQLExtendedLog.Buffer"/>'s change event, which
    /// fires on whatever thread logged — a cache poll, a completion, the shell — so every touch of the
    /// collection is marshalled. WPF forwards a single property change to the dispatcher itself, which is
    /// what lets a repeat count update in place, but a collection change from off-thread throws.
    /// </summary>
    private void AttachDiagnosticLog()
    {
        foreach (var entry in SQLExtendedLog.Buffer.Snapshot())
            _logEntries.Add(entry);

        DiagnosticLogGrid.ItemsSource = _logEntries;
        SQLExtendedLog.Buffer.Changed += OnDiagnosticLogChanged;
        Closed += (_, _) => SQLExtendedLog.Buffer.Changed -= OnDiagnosticLogChanged;

        ScrollDiagnosticLogToEnd();
        UpdateDiagnosticLogStatus();
    }

    private void OnDiagnosticLogChanged(object sender, DiagnosticLogEventArgs e)
    {
        // BeginInvoke rather than Invoke: the logging thread is usually mid-failure and must not be made to
        // wait on the UI thread, which may itself be blocked on the modal dialog.
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                try
                {
                    if (!e.IsNew)
                    {
                        // The entry updates itself through PropertyChanged; only the count moved.
                        UpdateDiagnosticLogStatus();
                        return;
                    }

                    if (e.Evicted != null)
                        _logEntries.Remove(e.Evicted);

                    _logEntries.Add(e.Entry);
                    ScrollDiagnosticLogToEnd();
                    UpdateDiagnosticLogStatus();
                }
                catch
                {
                    // A dialog closing mid-notification is not worth reporting.
                }
            })
        );
    }

    /// <summary>Keeps the newest line in view — a log that has to be scrolled to be read is one nobody reads.</summary>
    private void ScrollDiagnosticLogToEnd()
    {
        try
        {
            if (_logEntries.Count > 0)
                DiagnosticLogGrid.ScrollIntoView(_logEntries[_logEntries.Count - 1]);
        }
        catch
        {
            // Virtualization can refuse mid-layout; the row is still there.
        }
    }

    private void DiagnosticLogEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
            return;
        UpdateDiagnosticLogVisibility();
    }

    /// <summary>
    /// The log view is hidden until logging is enabled, but the switches are not — otherwise there is
    /// nowhere to turn it on from. The "off" note takes the grid's place so the tab never reads as empty.
    /// </summary>
    private void UpdateDiagnosticLogVisibility()
    {
        bool on = ChkDiagnosticLogEnabled.IsChecked == true;

        DiagnosticLogGrid.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        TxtDiagnosticLogDetail.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        TxtDiagnosticLogOff.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        ChkDiagnosticLogToFile.IsEnabled = on;
        BtnDiagnosticCopy.IsEnabled = on;
        BtnDiagnosticClear.IsEnabled = on;

        TxtDiagnosticLogPath.Text =
            ChkDiagnosticLogToFile.IsChecked == true
                ? "Writing to " + SQLExtendedLog.CurrentFilePath + " — one file per day, kept for a week."
                : "Would write to " + SQLExtendedLog.LogDirectory + " — one file per day, kept for a week.";

        UpdateDiagnosticLogStatus();
    }

    private void UpdateDiagnosticLogStatus()
    {
        if (ChkDiagnosticLogEnabled.IsChecked != true)
        {
            TxtDiagnosticLogStatus.Text = "";
            return;
        }

        // Says "capturing" only when it actually is: ticking the box takes effect on OK, and until then the
        // grid is showing whatever was already captured.
        string state = SQLExtendedLog.Enabled ? "capturing" : "starts when you press OK";
        TxtDiagnosticLogStatus.Text = $"{_logEntries.Count:N0} of {SQLExtendedLog.Buffer.Capacity:N0} entries — {state}.";
    }

    private void DiagnosticLogGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var entry = DiagnosticLogGrid.SelectedItem as DiagnosticLogEntry;
        TxtDiagnosticLogDetail.Text =
            entry == null ? ""
            : entry.HasDetail ? entry.Message + Environment.NewLine + Environment.NewLine + entry.Detail
            : entry.Message;
    }

    private void CopyDiagnosticLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string text = SQLExtendedLog.Buffer.ToText();
            if (string.IsNullOrEmpty(text))
            {
                TxtDiagnosticLogStatus.Text = "Nothing to copy.";
                return;
            }

            Clipboard.SetText(text);
            TxtDiagnosticLogStatus.Text = "Copied the whole log to the clipboard.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Copy Log", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearDiagnosticLog_Click(object sender, RoutedEventArgs e)
    {
        SQLExtendedLog.Buffer.Clear();
        _logEntries.Clear();
        TxtDiagnosticLogDetail.Text = "";
        UpdateDiagnosticLogStatus();
    }

    private void OpenDiagnosticLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(SQLExtendedLog.LogDirectory);
            System.Diagnostics.Process.Start("explorer.exe", SQLExtendedLog.LogDirectory);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Open Folder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- Dialog buttons ---

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        ReadSettingsFromUI();
        _settings.Save();

        // Pushed in rather than read by the log itself: most of what logs is on a worker thread, and
        // SQLExtendedSettings.Current must not be faulted in from one. This is the UI thread, and it is the
        // only place the two switches change.
        SQLExtendedLog.Configure(_settings.DiagnosticLogEnabled, _settings.DiagnosticLogToFile);

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        _settings = SQLExtendedSettings.Defaults;
        LoadSettingsToUI();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }

    private static int ParseInt(string text, int fallback)
    {
        return int.TryParse(text, out int value) && value >= 0 ? value : fallback;
    }

    private static long ParseLong(string text, long fallback)
    {
        return long.TryParse(text, out long value) && value >= 0 ? value : fallback;
    }

    /// <summary>
    /// Parses a monitoring threshold. Invariant culture rather than the current one: these values round-trip
    /// through the settings JSON, so a machine using ',' as its decimal separator must not write a number the
    /// next load cannot read back.
    /// </summary>
    private static double ParseDouble(string text, double fallback)
    {
        return double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) && value >= 0
            ? value
            : fallback;
    }
}

/// <summary>
/// Row model for the placeholder defaults DataGrid.
/// </summary>
internal sealed class PlaceholderDefaultItem : INotifyPropertyChanged
{
    private string _defaultValue;

    public string Name { get; set; }

    public string DefaultValue
    {
        get => _defaultValue;
        set
        {
            if (_defaultValue != value)
            {
                _defaultValue = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DefaultValue)));
            }
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
}

/// <summary>
/// One entry in the comment colour-scheme combo. A wrapper rather than binding the enum directly so the
/// list shows the scheme's display name — the combo has no DisplayMemberPath, and an enum would show as
/// "MonochromeRamp".
/// </summary>
/// <summary>One row of the Keyboard Shortcuts tab.</summary>
internal sealed class ShortcutItem(string command, string shortcut)
{
    public string Command { get; } = command;
    public string Shortcut { get; } = shortcut;
}

internal sealed class SchemeItem(Comments.CommentScheme scheme)
{
    public Comments.CommentScheme Scheme { get; } = scheme;

    public override string ToString() => Comments.CommentThemes.DisplayName(Scheme);
}
