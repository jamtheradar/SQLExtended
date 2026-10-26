using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;

namespace SQLExtended.Settings;

/// <summary>
/// Unified settings for the SQLExtended extension, covering IntelliSense, schema cache, and search defaults.
/// Persisted to %APPDATA%\SQLExtended\SSMS\sqlextended-settings.json.
/// Formatter options remain in their own file (formatter-options.json) for backward compatibility.
/// </summary>
public sealed class SQLExtendedSettings
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SQLExtended", "SSMS");

    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "sqlextended-settings.json");

    private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
    {
        Formatting = Newtonsoft.Json.Formatting.Indented,
        Converters = { new StringEnumConverter() },
        DefaultValueHandling = DefaultValueHandling.Include
    };

    // --- IntelliSense ---

    public bool IntelliSenseEnabled { get; set; } = true;
    public bool SuppressBuiltInIntelliSense { get; set; } = false;
    public bool CamelCaseMatching { get; set; } = true;
    public bool ShowColumnTypeInfo { get; set; } = true;
    public bool ShowRowCounts { get; set; } = true;
    public bool AutoTriggerAfterKeyword { get; set; } = true;

    /// <summary>
    /// When a stored-procedure completion is accepted in an EXEC context, expand it to the proc
    /// name plus an interactive "@param = value -- TYPE" line per parameter (Tab jumps between the
    /// values), mirroring SQL Prompt. When off, only the procedure name is inserted.
    /// </summary>
    public bool ExpandProcedureParameters { get; set; } = true;

    /// <summary>
    /// A table picked from the list after FROM or JOIN is inserted with an alias built from its initials
    /// ("dbo.Company c"), and the columns of an aliased table are offered with that alias ("c.ACTIVE")
    /// even when it is the only table in the query.
    /// </summary>
    public bool AutoAliasTables { get; set; } = true;

    /// <summary>
    /// How much IntelliSense reads about the server's linked servers. Names come from the local sys.servers and cost
    /// nothing; databases, and tables with their columns, are read from the linked server itself through its
    /// provider, so they wait on that server and can be slow. Off by default past Names for that reason.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public LinkedServerCompletionLevel LinkedServerCompletion { get; set; } = LinkedServerCompletionLevel.Names;

    // --- Completion commit keys ---
    // Which keys accept (commit) the highlighted completion item.

    /// <summary>Tab commits the selected completion item.</summary>
    public bool CommitOnTab { get; set; } = true;

    /// <summary>Enter commits the selected completion item.</summary>
    public bool CommitOnEnter { get; set; } = true;

    /// <summary>Space commits the selected completion item.</summary>
    public bool CommitOnSpace { get; set; } = true;

    /// <summary>
    /// Recase SQL keywords as you type — when a keyword is completed by a boundary character
    /// (space, punctuation, newline), rewrite it to match the formatter's keyword casing.
    /// Only applies when the active formatter profile's keyword casing is Upper or Lower;
    /// never touches identifiers, strings, comments, or bracketed names.
    /// </summary>
    public bool RecaseKeywordsWhileTyping { get; set; } = true;

    /// <summary>
    /// Typing a single quote in code inserts the closing one too, with the caret between them. Not inside a
    /// string, comment or bracketed name, and not straight after a word (O'Brien) except the N of N'…'.
    /// </summary>
    public bool AutoCloseQuotes { get; set; } = true;

    /// <summary>
    /// Tab straight after a SELECT-list <c>*</c> or <c>alias.*</c> replaces it with the column list, each column
    /// prefixed with its table's alias, giving a table without one an alias (GitHub issue #4).
    /// </summary>
    public bool TabExpandsSelectStar { get; set; } = true;

    // --- Editor: rainbow parentheses ---

    /// <summary>
    /// Colour parentheses in the query editor by nesting depth. The colours themselves live in
    /// Tools → Options → Environment → Fonts and Colors ("SQLExtended Rainbow Parenthesis …"),
    /// which is why there is no palette here.
    /// </summary>
    public bool RainbowParensEnabled { get; set; } = true;

    /// <summary>
    /// How many colours to cycle through before repeating. Clamped to 1..<c>RainbowPairScanner.MaxSupportedLevels</c>
    /// when it is used, so a hand-edited settings file cannot put a nesting level outside the palette.
    /// </summary>
    public int RainbowParensLevels { get; set; } = 4;

    /// <summary>
    /// Tint a parenthesis that has no partner. Off leaves it in the editor's ordinary text colour —
    /// worth having, because a script being typed is unbalanced most of the time.
    /// </summary>
    public bool RainbowParensHighlightUnmatched { get; set; } = true;

    /// <summary>
    /// Also colour BEGIN/END, CASE/END and the TRY/CATCH forms by nesting depth, on their own depth
    /// counter. Off by default: it doubles what is coloured in procedural code, which not everyone wants.
    /// BEGIN TRAN and BEGIN DIALOG are deliberately excluded — neither is closed by END.
    /// </summary>
    public bool RainbowParensIncludeBlocks { get; set; } = false;

    // --- Editor: execution safety ---

    /// <summary>
    /// Ask before executing an UPDATE or DELETE that has no WHERE clause, and cancel the execution on No.
    /// See <c>Safety/UnsafeDmlAnalyzer</c> for what is deliberately not flagged (temp tables, a target restricted by an INNER JOIN, module bodies).
    /// </summary>
    public bool UnsafeDmlGuardEnabled { get; set; } = true;

    // --- Editor: comment tags ---

    /// <summary>
    /// Colour comments that open with <c>!</c>, <c>?</c>, <c>todo</c> or <c>*</c> apart from ordinary ones,
    /// and pick apart a banner header — dimming its rules of stars and dashes, colouring its headings.
    /// The colours live in Tools → Options → Environment → Fonts and Colors ("SQLExtended Comment …"),
    /// which is why — as with the rainbow parentheses — there is no palette here.
    /// </summary>
    public bool CommentTagsEnabled { get; set; } = true;

    /// <summary>
    /// Which colour scheme to write into Fonts and Colors. See <c>CommentThemes</c> for what each one is
    /// after; each ships a dark and a light variant, chosen from the editor background rather than from the
    /// theme's name, so a hand-edited theme still gets the right one.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public Comments.CommentScheme CommentScheme { get; set; } = Comments.CommentScheme.StructuralFade;

    /// <summary>
    /// The scheme and variant last written to Fonts and Colors, as <c>Scheme/dark</c>.
    ///
    /// <para>This is what makes hand-tuning survive. A scheme is only written when this does not match what
    /// is wanted — so once on first run, on a deliberate scheme change, and on a dark/light switch. Recolour
    /// an entry by hand and nothing rewrites it, because nothing here has changed. Clearing this field
    /// forces the scheme to be applied again, which is the way back from a hand-tuning that went wrong.</para>
    /// </summary>
    public string CommentSchemeApplied { get; set; } = "";

    // --- Schema Cache ---

    public int AutoRefreshIntervalMinutes { get; set; } = 5;
    public int MaxCacheAgeDays { get; set; } = 7;
    public bool AutoLoadOnConnect { get; set; } = true;
    public bool DetectDdlChanges { get; set; } = true;

    /// <summary>
    /// How often (in seconds) to poll the active SSMS connection for a database/connection switch.
    /// A detected switch triggers a schema-cache load and refreshes the snippet connection-info cache
    /// ($dbname$ / $server$). Clamped to a minimum of 1 second. Applies on the next SSMS start.
    /// </summary>
    public int DatabaseChangePollSeconds { get; set; } = 600;

    // --- Object Explorer ---

    /// <summary>
    /// Group connected servers in Object Explorer into folders mirroring the Registered Servers
    /// group hierarchy. Ungrouped servers stay at the tree root. Applied live by the poll timer.
    /// </summary>
    public bool ServerGroupingEnabled { get; set; } = true;

    /// <summary>
    /// How often (in seconds) to poll the Object Explorer tree and re-apply server grouping.
    /// Clamped to a minimum of 1 second. The poll interval is set on the next SSMS start.
    /// </summary>
    public int ServerGroupPollSeconds { get; set; } = 3;

    // --- Encrypted modules ---

    /// <summary>
    /// Recover the text of modules created WITH ENCRYPTION, so the schema viewer, search-in-definitions and
    /// the schema export have a body to work with instead of a blank. Not completion: nothing in the
    /// IntelliSense completion path reads a module's definition, and encryption never hid the columns and
    /// parameters that path does read.
    ///
    /// Off by default because it is not a read. There is no key to decrypt with: the only way to recover the
    /// text is to briefly ALTER the object to a throwaway definition over a dedicated administrator
    /// connection and roll that back in the same batch. Nothing is ever left changed, but the ALTER takes a
    /// schema-modification lock and recompiles the module, and it needs sysadmin — whether that is
    /// acceptable is the server owner's call, not a default.
    /// </summary>
    public bool DecryptEncryptedModules { get; set; } = false;

    // --- Search Defaults ---

    [JsonConverter(typeof(StringEnumConverter))]
    public SearchScope DefaultSearchScope { get; set; } = SearchScope.CurrentDatabase;

    public bool DefaultSearchObjectNames { get; set; } = true;
    public bool DefaultSearchColumnNames { get; set; } = true;
    public bool DefaultSearchDefinitions { get; set; } = true;
    public int DefaultMaxSearchResults { get; set; } = 200;

    // --- Schema View ---

    public bool TempTableDropIfExists { get; set; } = true;

    /// <summary>
    /// How Open Definition (F12) scripts a procedure, view, function or trigger. CREATE OR ALTER by default, so the window
    /// it opens runs as it stands — the stored CREATE would fail against the object it was scripted from.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public Definition.DefinitionScriptMode DefinitionScriptMode { get; set; } = Definition.DefinitionScriptMode.CreateOrAlter;

    /// <summary>
    /// Format With… presets, in slot order: the first five are Format Preset 1–5, which can be given shortcuts. Null until
    /// the user saves one — read through <see cref="GetFormatPresets"/>, which supplies the defaults. Not initialised to the
    /// defaults here: Json.NET adds a saved list's items to a list the constructor already filled, so every load would
    /// prepend another copy of them.
    /// </summary>
    public List<Formatting.FormatPreset> FormatPresets { get; set; }

    public List<Formatting.FormatPreset> GetFormatPresets() => FormatPresets?.ConvertAll(p => p.Clone()) ?? Formatting.FormatPreset.Defaults();

    /// <summary>Remembered size of the Schema Viewer dialog (0 = use default).</summary>
    public double SchemaDialogWidth { get; set; } = 0;
    public double SchemaDialogHeight { get; set; } = 0;

    /// <summary>
    /// Folder the Schema Cache window's "Export to folder" last wrote to, used to seed the folder picker.
    /// Comparing two servers means exporting to two sibling folders repeatedly, so starting from the last
    /// one saves re-navigating there every time.
    /// </summary>
    public string LastSchemaExportFolder { get; set; } = "";

    // --- History ---

    /// <summary>Capture tab snapshots when text changes.</summary>
    public bool HistoryEnabled { get; set; } = true;

    /// <summary>Milliseconds of typing-pause required before snapshotting a tab.</summary>
    public int HistoryDebounceMs { get; set; } = 2000;

    /// <summary>Rows older than this are purged on startup. 0 disables age purge.</summary>
    public int HistoryRetentionDays { get; set; } = 30;

    /// <summary>Maximum snapshots retained per document. 0 disables per-doc cap.</summary>
    public int HistoryMaxPerDocument { get; set; } = 50;

    /// <summary>Skip capture for tabs whose text exceeds this size (bytes).</summary>
    public int HistoryMaxTextBytes { get; set; } = 5 * 1024 * 1024;

    // --- Statistics Parser ---

    /// <summary>Hide IO columns that are zero for every row of a statement (keeps narrow output readable).</summary>
    public bool StatisticsSuppressZeroColumns { get; set; } = true;

    /// <summary>
    /// Language of the STATISTICS output to parse. <see cref="StatisticsLanguageOption.Auto"/> detects it from the
    /// captured text, which is the right answer unless detection guesses wrong on a mixed-language batch.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public StatisticsLanguageOption StatisticsLanguage { get; set; } = StatisticsLanguageOption.Auto;

    /// <summary>
    /// Collapse the underscore padding SQL Server adds to session temp-table names (<c>#t____…____0000000157</c>),
    /// so the table column stays narrow. Regular table names are never altered.
    /// </summary>
    public bool StatisticsFormatTempTableNames { get; set; } = true;

    // --- Agent Jobs dashboard ---

    /// <summary>
    /// Job categories hidden by default, comma separated. SSRS installs a dozen-plus subscription jobs under
    /// "Report Server" that swamp the list and are never what you opened the dashboard to look at; the toolbar
    /// checkbox brings them back when you do want them.
    /// </summary>
    public string JobsHiddenCategories { get; set; } = "Report Server,Report Server HTML";

    /// <summary>Auto-refresh interval for the Agent Jobs dashboard, in seconds.</summary>
    public int JobsRefreshSeconds { get; set; } = 15;

    /// <summary>How far back the History tab reads msdb.dbo.sysjobhistory for the selected job.</summary>
    public int JobsHistoryDays { get; set; } = 7;

    /// <summary>How many recent runs the average-duration column averages over.</summary>
    public int JobsAverageSampleRuns { get; set; } = 10;

    // --- Performance dashboard ---

    /// <summary>
    /// How recently <c>sys.dm_server_memory_dumps</c> must have written a dump for the Server info tab to flag it
    /// amber. Older dumps are still listed, just not flagged — an instance that crashed once two years ago is
    /// history, not news.
    ///
    /// <para>Deliberately generous by default: a dump is not a transient condition, and one written a fortnight
    /// ago on a server nobody has looked at since is exactly the case the row exists to surface. Zero or less
    /// turns the flagging off and leaves the dumps listed as plain facts.</para>
    ///
    /// <para>Read on the UI thread and passed down — <c>PerfServerInfoQuery</c> runs on a worker, and
    /// <see cref="Current"/> must not be faulted in from one.</para>
    /// </summary>
    public int PerfRecentDumpDays { get; set; } = 30;

    // --- Always On monitor ---
    //
    // Thresholds for the Diagnostics tab. Defaults are deliberately conservative: a rule that fires on a healthy
    // production system teaches people to ignore the tab, which is worse than not having it.

    /// <summary>Estimated data loss (send queue ÷ send rate), in seconds, at which a database is reported degraded.</summary>
    public double AgRpoWarningSeconds { get; set; } = 60d;

    /// <summary>Estimated data loss, in seconds, at which it is reported as critical.</summary>
    public double AgRpoCriticalSeconds { get; set; } = 300d;

    /// <summary>secondary_lag_seconds above which a secondary is reported as lagging.</summary>
    public double AgSecondaryLagWarningSeconds { get; set; } = 60d;

    /// <summary>Log send queue, in KB, above which a stalled queue is called out on its own.</summary>
    public long AgSendQueueWarningKb { get; set; } = 100_000L;

    /// <summary>Redo queue, in KB, above which recovery time is called out.</summary>
    public long AgRedoQueueWarningKb { get; set; } = 100_000L;

    /// <summary>
    /// Added commit latency per transaction, in milliseconds, before synchronous commit is reported as expensive.
    /// Derived from Transaction Delay ÷ Mirrored Write Transactions/sec.
    /// </summary>
    public double AgCommitDelayWarningMs { get; set; } = 20d;

    // --- Replication monitor ---

    /// <summary>Auto-refresh interval for the Replication monitor, in seconds.</summary>
    public int ReplRefreshSeconds { get; set; } = 15;

    /// <summary>
    /// End-to-end latency (log reader + distribution), in seconds, at which a subscription is reported degraded,
    /// then critical. Transactional replication routinely runs a few seconds behind, so the warning starts well
    /// above that.
    /// </summary>
    public double ReplLatencyWarningSeconds { get; set; } = 60d;
    public double ReplLatencyCriticalSeconds { get; set; } = 300d;

    /// <summary>
    /// Fraction of the distribution retention period a subscription may go without activity before it is
    /// reported. Past 100% the subscription is marked inactive by the expiry job and needs reinitializing, so
    /// warning at 75% leaves room to act.
    /// </summary>
    public double ReplExpiryWarningFraction { get; set; } = 0.75d;

    /// <summary>Undistributed command count above which a backlog is reported. Only read on demand.</summary>
    public long ReplPendingCommandWarning { get; set; } = 100_000L;

    /// <summary>How many rows the Errors tab reads from MSrepl_errors.</summary>
    public int ReplErrorRows { get; set; } = 200;

    // --- Environment tabs (query tab colours and captions) ---

    /// <summary>
    /// Colour and rename query tabs by which server/database they are connected to.
    ///
    /// <b>Off by default</b>, because switching it on is not a private change: it turns on the shell's own
    /// "colorize document tabs" preference, repoints it at the regex provider, and writes to the shell's
    /// <c>ColorByRegexConfig.txt</c>. Changing how SSMS itself looks is the user's call to make, not a
    /// default to inherit — the same reasoning as <see cref="DecryptEncryptedModules"/>.
    /// </summary>
    public bool EnvTabsEnabled { get; set; } = false;

    /// <summary>Tint matching tabs with the rule's colour.</summary>
    public bool EnvTabsColorTabs { get; set; } = true;

    /// <summary>Prefix matching tabs' captions with the rule's label.</summary>
    public bool EnvTabsRenameTabs { get; set; } = true;

    /// <summary>
    /// Caption template. Tokens: <c>{label}</c>, <c>{server}</c>, <c>{database}</c>, <c>{n}</c> (the tab's
    /// number within its group). The result is prefixed to the document's own name.
    /// </summary>
    public string EnvTabsCaptionTemplate { get; set; } = "{n}. {label}";

    /// <summary>What an auto-created rule keys on: the server alone, or the server and database.</summary>
    public EnvTabs.EnvTabGrouping EnvTabsGrouping { get; set; } = EnvTabs.EnvTabGrouping.Server;

    /// <summary>Offer to create a rule when connecting somewhere no rule covers.</summary>
    public bool EnvTabsAutoPrompt { get; set; } = true;

    /// <summary>
    /// How often (seconds) to re-read the active connection and refresh tabs. Clamped to at least 1.
    /// This is a cheap UI-thread poll over open frames; it is separate from
    /// <see cref="DatabaseChangePollSeconds"/> because that one triggers a schema-cache load and is
    /// deliberately slow.
    /// </summary>
    public int EnvTabsPollSeconds { get; set; } = 3;

    /// <summary>The rules, in evaluation order. First match wins.</summary>
    public List<EnvTabs.EnvTabRule> EnvTabsRules { get; set; } = new();

    /// <summary>
    /// Connections the user declined to map, as <c>server|database</c>. Kept so "don't ask again" survives
    /// a restart — without it the prompt reappears on every session for the one server someone has
    /// deliberately said they do not want coloured.
    /// </summary>
    public List<string> EnvTabsDeclined { get; set; } = new();

    // --- Results grid aggregates ---

    /// <summary>
    /// Bring the Aggregates window to the front the first time a selection is made in a results grid.
    ///
    /// <b>Off by default.</b> The window updates live whenever it is open, so this only controls whether a
    /// stray drag-select is allowed to take over screen space and pull focus off the grid mid-gesture.
    /// Opening it once (Ctrl+Alt+G) is the explicit act; after that it just keeps up.
    /// </summary>
    public bool GridAggregatesAutoShow { get; set; } = false;

    /// <summary>
    /// Largest selection that will be aggregated, in cells. A bigger selection is refused outright rather
    /// than totalled in part — see <c>GridSelectionReader</c> for why a truncated total is the worst of the
    /// available answers. Reading happens on the UI thread, so this is also what keeps SSMS responsive.
    /// </summary>
    public long GridAggregatesMaxCells { get; set; } = 250_000L;

    /// <summary>
    /// Quiet period after the selection stops changing before aggregates are recomputed. The grid raises
    /// its selection event continuously through a drag, so without this every mouse-move would re-read
    /// the whole selection.
    /// </summary>
    public int GridAggregatesDebounceMs { get; set; } = 200;

    /// <summary>
    /// How often (seconds) to look for results grids that have appeared since the last check. Grids are
    /// created fresh per result set on every execution, and the shell offers no event for it. Polling only
    /// runs while the Aggregates window is open.
    /// </summary>
    public int GridAggregatesPollSeconds { get; set; } = 2;

    /// <summary>
    /// Largest number of cells a single find will read before it gives up. Unlike the aggregates cap this is
    /// not about responsiveness — the scan is sliced across dispatcher ticks and never blocks the window —
    /// it is about a search over a result set nobody meant to search taking minutes. Reaching it is always
    /// reported, so a partial count is never presented as a total.
    /// </summary>
    public long GridFindMaxCells { get; set; } = 2_000_000L;

    /// <summary>
    /// Most matches a find will collect. Every one of them is tinted and steppable, and past a few thousand
    /// the answer to "where is it" stops being a list and starts being "everywhere" — a narrower search is
    /// the better tool. Reaching it is reported, and the count is shown as "N+".
    /// </summary>
    public int GridFindMaxMatches { get; set; } = 5_000;

    // The find window's own toggles, persisted so a way of working survives an SSMS restart.
    public bool GridFindMatchCase { get; set; } = false;
    public bool GridFindWholeCell { get; set; } = false;
    public bool GridFindUseRegex { get; set; } = false;
    public bool GridFindHighlightAll { get; set; } = true;
    public bool GridFindAllResultSets { get; set; } = false;

    // Which aggregate columns the window shows. These persist the window's own checkboxes.
    public bool GridAggregatesShowNonNull { get; set; } = true;
    public bool GridAggregatesShowNulls { get; set; } = true;
    public bool GridAggregatesShowBlanks { get; set; } = false;
    public bool GridAggregatesShowDistinct { get; set; } = true;
    public bool GridAggregatesShowSum { get; set; } = true;
    public bool GridAggregatesShowAverage { get; set; } = true;
    public bool GridAggregatesShowMin { get; set; } = true;
    public bool GridAggregatesShowMax { get; set; } = true;

    /// <summary>Show total and longest character counts of the displayed text. Off by default — it is not
    /// <c>DATALENGTH</c> and the difference is easy to miss (see the window's own column tooltips).</summary>
    public bool GridAggregatesShowChars { get; set; } = false;

    // --- Updates ---

    /// <summary>Check for new versions on SSMS startup.</summary>
    public bool UpdateCheckEnabled { get; set; } = true;

    /// <summary>
    /// URL of the version manifest JSON. Empty disables the check.
    /// <para>
    /// This is the <c>releases/latest/download/</c> form deliberately, not the GitHub API. That path
    /// redirects to the newest non-prerelease release's asset of the given name, so it needs no API call:
    /// <c>api.github.com/repos/.../releases/latest</c> is rate-limited to 60 requests an hour per IP —
    /// shared by everyone behind one corporate NAT — and is the host more likely to be blocked by a
    /// corporate proxy than the download CDN. It also keeps the manifest ours, which matters because
    /// <see cref="VersionManifest.MinRequiredVersion"/> has no equivalent field in a GitHub release.
    /// </para>
    /// </summary>
    public string UpdateFeedUrl { get; set; } = "https://github.com/JamTheRadar/SQLExtended/releases/latest/download/version.json";

    /// <summary>Version the user chose to skip (no nag until a newer one appears).</summary>
    public string UpdateSkippedVersion { get; set; } = "";

    /// <summary>UTC timestamp of the last successful check. Used to throttle to once per day.</summary>
    public DateTime UpdateLastCheckUtc { get; set; } = DateTime.MinValue;

    // --- Diagnostics ---

    /// <summary>
    /// Capture the failures the extension otherwise swallows into an in-memory session log, viewable on the
    /// Diagnostics tab of this dialog. Off by default, and memory-only: the log dies with SSMS.
    /// See <see cref="Diagnostics.SQLExtendedLog"/> for why neither Debug output nor ActivityLog.xml covers this.
    /// </summary>
    public bool DiagnosticLogEnabled { get; set; } = false;

    /// <summary>
    /// Also append the session log to <c>%APPDATA%\SQLExtended\SSMS\logs\sqlextended-yyyy-MM-dd.log</c>, for a
    /// problem that has to leave the machine. Ignored unless <see cref="DiagnosticLogEnabled"/> is on.
    /// </summary>
    public bool DiagnosticLogToFile { get; set; } = false;

    // --- Load / Save ---

    private static SQLExtendedSettings _current;

    /// <summary>
    /// Cached settings instance for hot paths (e.g. per-keystroke completion commit checks)
    /// that must not hit disk on every call. Refreshed whenever <see cref="Save"/> runs.
    /// </summary>
    public static SQLExtendedSettings Current => _current ??= Load();

    public static SQLExtendedSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                return JsonConvert.DeserializeObject<SQLExtendedSettings>(json, JsonSettings)
                    ?? new SQLExtendedSettings();
            }
        }
        catch
        {
            // Corrupted file — return defaults
        }

        return new SQLExtendedSettings();
    }

    /// <summary>
    /// Raised after <see cref="Save"/> has replaced <see cref="Current"/>, for the features that hold live
    /// state built from these settings and would otherwise keep showing the old ones until SSMS restarted
    /// (the editor taggers). Handlers run on the saving thread — the settings dialog's OK, so the UI thread —
    /// and <b>must unsubscribe</b>: this is static, so a subscriber that does not outlives its own window.
    /// </summary>
    public static event EventHandler Changed;

    public void Save()
    {
        bool saved = false;

        try
        {
            Directory.CreateDirectory(SettingsDir);
            string json = JsonConvert.SerializeObject(this, JsonSettings);
            File.WriteAllText(SettingsPath, json);

            // Refresh the hot-path cache so saved changes take effect without an SSMS restart.
            _current = this;
            saved = true;
        }
        catch
        {
            // Best effort
        }

        if (!saved) return;

        // Outside the catch above, and in its own: a subscriber that throws would otherwise be
        // indistinguishable from the write itself failing.
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A listener that cannot refresh is not a reason to lose the save.
        }
    }

    public SQLExtendedSettings Clone()
    {
        var json = JsonConvert.SerializeObject(this, JsonSettings);
        return JsonConvert.DeserializeObject<SQLExtendedSettings>(json, JsonSettings);
    }

    public static SQLExtendedSettings Defaults => new SQLExtendedSettings();
}

/// <summary>How far IntelliSense reaches into linked servers. Each level includes the ones before it.</summary>
public enum LinkedServerCompletionLevel
{
    Off,
    Names,
    Databases,
    Objects
}

public enum SearchScope
{
    CurrentDatabase,
    AllCachedDatabases
}

/// <summary>
/// Language selection for the statistics parser. Mirrors the languages the vendored parser core supports
/// (see <c>StatisticsParser.Core.Parsing.ParserLanguage</c>), plus auto-detection.
/// </summary>
public enum StatisticsLanguageOption
{
    Auto,
    English,
    Spanish,
    Italian
}
