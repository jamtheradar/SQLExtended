using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using SQLExtended.Settings;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace SQLExtended.Monitoring.Replication;

/// <summary>
/// The replication monitor. Pinned to the connection it was opened from, reads whatever of the topology that
/// connection can see, and judges it on the Diagnostics tab.
///
/// <para><b>The connection is captured once, when the window is opened, and kept until it closes</b>; the window
/// does not follow the active query window. That matters more here than on the other three dashboards — see the
/// first bullet below: which instance you are connected to decides what is visible at all, so a window that changed
/// connection would change which tabs have anything in them. The tool window is registered <c>MultiInstances</c>, so
/// a distributor and its publishers can each have their own window; see <see cref="MonitorWindows"/> for the
/// matching and reuse rules, shared with the other three dashboards.</para>
///
/// Threading matches the other three dashboards: collection runs entirely on a background thread and only the
/// merge into the bound collections happens on the UI thread. Polls never overlap — a slow distributor makes the
/// interval effectively longer rather than queueing work up behind itself.
///
/// Two things are specific to replication:
///  * <b>What is visible depends on which server you are on.</b> The distribution database holds nearly
///    everything, and only the distributor can read it; a publisher sees its own log and databases but no
///    subscriptions. Rather than demand a particular connection, the dashboard collects what it can and the
///    Diagnostics tab's first row says what was not visible from here.
///  * <b>Pending commands, errors and tracer tokens load on demand.</b> Each is expensive in a way that scales
///    with exactly the problem you opened the window to look at, so none of them belongs on a refresh timer.
/// </summary>
public partial class ReplMonitorControl : UserControl
{
    private readonly ObservableCollection<ReplPublicationRow> _publications = new ObservableCollection<ReplPublicationRow>();
    private readonly ObservableCollection<ReplSubscriptionRow> _subscriptions = new ObservableCollection<ReplSubscriptionRow>();
    private readonly ObservableCollection<ReplSubscriptionRow> _attention = new ObservableCollection<ReplSubscriptionRow>();
    private readonly ObservableCollection<ReplAgentRow> _agents = new ObservableCollection<ReplAgentRow>();
    private readonly ObservableCollection<ReplPublisherDatabaseRow> _publisherDatabases = new ObservableCollection<ReplPublisherDatabaseRow>();
    private readonly ObservableCollection<ReplSubscriberDatabaseRow> _subscriberDatabases = new ObservableCollection<ReplSubscriberDatabaseRow>();
    private readonly ObservableCollection<ReplIssueRow> _issues = new ObservableCollection<ReplIssueRow>();

    private readonly ReplHistory _history = new ReplHistory();
    private readonly DispatcherTimer _timer;

    private ICollectionView _subscriptionsView;
    private ICollectionView _agentsView;
    private ICollectionView _publicationsView;
    private ICollectionView _issuesView;

    private AsyncPackage _package;
    private CancellationTokenSource _inFlight;
    private bool _polling;

    // Capabilities are per-server; cached against the connection string that produced them.
    private ReplCapabilities _caps;
    private string _capsConnection;

    // The connections used by the last successful poll, reused by the on-demand loads and the tracer-token post.
    private string _masterConnection;
    private string _distributionConnection;
    private string _serverName;

    // SUSER_SNAME() from the last poll, for the header. Until one arrives the connection string's own answer stands
    // in — see MonitorPin.LoginFor. It also explains a half-populated window: which of the three databases a poll
    // could read is a question about this login's rights.
    private string _loginName;

    // The connection this window is pinned to, captured when it was opened — see the class remarks. Held as
    // harvested rather than pointed at a database, because a poll derives three connections from it.
    private readonly MonitorPin _pin = new MonitorPin();

    // A poll finished against a connection this window is no longer pinned to; its replacement runs once the
    // in-flight one has unwound (BeginRefresh refuses to overlap polls).
    private bool _restartAfterPoll;

    /// <summary>
    /// The server this window is pinned to, normalised for matching (see <see cref="MonitorWindows.ServerKey"/>).
    /// Null until pinned; <see cref="ReplMonitorCommand"/> reads it to decide whether an open window already covers
    /// the server being asked for.
    /// </summary>
    internal string PinnedServerKey => _pin.ServerKey;

    /// <summary>
    /// Set by the hosting tool window so the pane's caption can name the server. With several windows open the
    /// caption is the only thing distinguishing their tabs, so it is not optional decoration.
    /// </summary>
    internal Action<string> CaptionChanged;

    // Pending command counts survive a refresh: they are expensive to obtain, and blanking them on every poll
    // would make the button feel broken. Keyed by distribution agent id, reapplied after each merge.
    private Dictionary<int, (long Undelivered, long Delivered)> _pendingCommands;
    private DateTime? _pendingCommandsAt;

    private List<ReplErrorRow> _allErrors = new List<ReplErrorRow>();

    // An error the user has to read outlives the next poll — see ShowNotice's sticky parameter.
    private bool _noticeIsSticky;
    private string _noticeClipboardText;

    private static readonly int[] IntervalSeconds = { 5, 10, 15, 30, 60 };
    private static readonly int[] ErrorCounts = { 100, 200, 500, 2000 };

    // Tab indices. Named because both "Copy tab" and "Open as query" switch on them, and a tab inserted in the
    // middle silently repointed both when they were bare numbers.
    private const int TabOverview = 0;
    private const int TabDiagnostics = 1;
    private const int TabSubscriptions = 2;
    private const int TabPublications = 3;
    private const int TabAgents = 4;
    private const int TabPublisher = 5;
    private const int TabTracer = 6;
    private const int TabErrors = 7;

    public ReplMonitorControl()
    {
        InitializeComponent();

        PublicationGrid.ItemsSource = _publications;
        SubscriptionGrid.ItemsSource = _subscriptions;
        AttentionGrid.ItemsSource = _attention;
        AgentGrid.ItemsSource = _agents;
        PublisherGrid.ItemsSource = _publisherDatabases;
        SubscriberGrid.ItemsSource = _subscriberDatabases;
        IssueGrid.ItemsSource = _issues;

        // Filtering through the collection views rather than by rebinding keeps scroll position on every
        // keystroke, and lets the status line report how many rows the filter is holding back.
        _subscriptionsView = CollectionViewSource.GetDefaultView(_subscriptions);
        _subscriptionsView.Filter = SubscriptionFilter;
        _agentsView = CollectionViewSource.GetDefaultView(_agents);
        _agentsView.Filter = AgentFilter;
        _publicationsView = CollectionViewSource.GetDefaultView(_publications);
        _publicationsView.Filter = PublicationFilter;
        _issuesView = CollectionViewSource.GetDefaultView(_issues);
        _issuesView.Filter = IssueFilter;

        foreach (int seconds in IntervalSeconds)
            IntervalCombo.Items.Add(seconds + "s");
        IntervalCombo.SelectedIndex = NearestIntervalIndex(SQLExtendedSettings.Current.ReplRefreshSeconds);

        foreach (int count in ErrorCounts)
            ErrorCountCombo.Items.Add("Top " + count);
        ErrorCountCombo.SelectedIndex = NearestErrorCountIndex(SQLExtendedSettings.Current.ReplErrorRows);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(IntervalSeconds[IntervalCombo.SelectedIndex]) };
        _timer.Tick += (s, e) => BeginRefresh(userInitiated: false);

        Unloaded += OnUnloaded;
    }

    internal void SetPackage(AsyncPackage package) => _package = package;

    private static int NearestIntervalIndex(int seconds) => Nearest(IntervalSeconds, seconds);

    private static int NearestErrorCountIndex(int rows) => Nearest(ErrorCounts, rows);

    /// <summary>
    /// The index of the closest offered value to a configured one. The combos offer a fixed set so the toolbar
    /// stays a two-click affair; a setting that is not one of them picks the nearest rather than being ignored.
    /// </summary>
    private static int Nearest(int[] options, int value)
    {
        int best = 0;
        for (int i = 1; i < options.Length; i++)
            if (Math.Abs(options[i] - value) < Math.Abs(options[best] - value)) best = i;
        return best;
    }

    // -------------------------------------------------------------------------------------------------
    // Toolbar
    // -------------------------------------------------------------------------------------------------

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        // Pressing Refresh is the user acknowledging whatever the banner said.
        _noticeIsSticky = false;
        BeginRefresh(userInitiated: true);
    }

    private void AutoRefresh_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoRefreshCheck.IsChecked == true)
        {
            _timer.Start();
            BeginRefresh(userInitiated: true);
        }
        else
        {
            _timer.Stop();
        }
    }

    private void Interval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_timer == null) return;
        int index = Math.Max(0, Math.Min(IntervalSeconds.Length - 1, IntervalCombo.SelectedIndex));
        _timer.Interval = TimeSpan.FromSeconds(IntervalSeconds[index]);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        try { _inFlight?.Cancel(); } catch { }
        _history.Clear();
    }

    private void GoToDiagnostics_Click(object sender, RoutedEventArgs e) => Tabs.SelectedIndex = TabDiagnostics;

    // -------------------------------------------------------------------------------------------------
    // Filtering
    // -------------------------------------------------------------------------------------------------

    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        _subscriptionsView?.Refresh();
        _agentsView?.Refresh();
        _publicationsView?.Refresh();
        UpdateStatus();
    }

    private void IssuesOnly_Changed(object sender, RoutedEventArgs e)
    {
        _issuesView?.Refresh();
        UpdateIssuesHint();
    }

    private bool SubscriptionFilter(object item)
    {
        if (!(item is ReplSubscriptionRow row)) return false;

        string text = FilterBox?.Text;
        if (string.IsNullOrWhiteSpace(text)) return true;

        text = text.Trim();
        return Contains(row.Publication, text) || Contains(row.Publisher, text) || Contains(row.PublisherDb, text)
            || Contains(row.Subscriber, text) || Contains(row.SubscriberDb, text);
    }

    private bool AgentFilter(object item)
    {
        if (!(item is ReplAgentRow row)) return false;

        string text = FilterBox?.Text;
        if (string.IsNullOrWhiteSpace(text)) return true;

        text = text.Trim();
        return Contains(row.Publication, text) || Contains(row.Publisher, text) || Contains(row.PublisherDb, text)
            || Contains(row.Subscriber, text) || Contains(row.SubscriberDb, text) || Contains(row.Name, text)
            || Contains(row.AgentTypeText, text);
    }

    private bool PublicationFilter(object item)
    {
        if (!(item is ReplPublicationRow row)) return false;

        string text = FilterBox?.Text;
        if (string.IsNullOrWhiteSpace(text)) return true;

        text = text.Trim();
        return Contains(row.Publication, text) || Contains(row.Publisher, text) || Contains(row.PublisherDb, text);
    }

    /// <summary>Hides informational rows when "Problems only" is ticked, the all-clear row included.</summary>
    private bool IssueFilter(object item)
    {
        if (!(item is ReplIssueRow issue)) return false;
        return IssuesOnlyCheck?.IsChecked != true || issue.Severity != ReplIssueSeverity.Information;
    }

    private static bool Contains(string haystack, string needle) =>
        haystack != null && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Reads the diagnostic thresholds out of settings. Loaded per poll so an edit takes effect at once.</summary>
    private static ReplThresholds CurrentThresholds()
    {
        var settings = SQLExtendedSettings.Current;
        return new ReplThresholds
        {
            LatencyWarningSeconds = settings.ReplLatencyWarningSeconds,
            LatencyCriticalSeconds = settings.ReplLatencyCriticalSeconds,
            ExpiryWarningFraction = settings.ReplExpiryWarningFraction,
            PendingCommandWarning = settings.ReplPendingCommandWarning
        };
    }

    // -------------------------------------------------------------------------------------------------
    // Pinning
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Binds this window to one instance for the rest of its life and starts a collection. UI thread only.
    ///
    /// Pinning matters more here than on the other three dashboards: what replication state is visible depends on
    /// which instance you are connected to (distributor, publisher, subscriber, or some combination), so a window
    /// that changed connection would not just show different numbers — it would show a different set of tabs
    /// populated, and the Diagnostics tab's "not visible from here" row would be describing somewhere else.
    /// </summary>
    internal void PinTo(string connectionString, string serverLabel)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // Nothing to pin to. A window already watching an instance keeps it — losing a working pin because the
        // caller could not resolve a connection this time would be a worse outcome than doing nothing.
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (!_pin.IsPinned) ShowNoConnection(serverLabel);
            return;
        }

        // Re-pinning to a different instance: the role probe, latency history and pending counts all describe the
        // old topology. Only reachable when the window cap is hit, never silently.
        if (_pin.Set(connectionString))
        {
            _publications.Clear(); _subscriptions.Clear(); _attention.Clear(); _agents.Clear();
            _publisherDatabases.Clear(); _subscriberDatabases.Clear(); _issues.Clear();
            _allErrors = new List<ReplErrorRow>();
            CloseDetail();   // the agent and error ids it shows mean nothing on the new server
            _history.Clear();
            _pendingCommands = null;
            _pendingCommandsAt = null;
            _caps = null;
            _capsConnection = null;
            _masterConnection = null;
            _distributionConnection = null;
            _serverName = null;
            _loginName = null;
            TimingText.Text = "";
        }

        ApplyPinnedChrome(serverLabel);

        // A stale "no connection" or previous server's error must not outlive the new pin.
        _noticeIsSticky = false;
        ShowNotice(null);

        BeginRefresh(userInitiated: true);
    }

    /// <summary>Names the pinned instance in the caption, the header and the header's tooltip.</summary>
    private void ApplyPinnedChrome(string serverLabel)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        string display = FirstNonEmpty(_serverName, serverLabel, _pin.Target) ?? "(not connected)";
        string login = _pin.LoginFor(_loginName);

        ServerText.Text = display;
        LoginText.Text = string.IsNullOrEmpty(login) ? "" : "as " + login;
        ServerText.ToolTip = LoginText.ToolTip = _pin.Describe(display, login);

        // The login is deliberately not in the caption: with several windows docked the tab strip has room for the
        // server and nothing else, and the server is what distinguishes them.
        CaptionChanged?.Invoke(display);
    }

    /// <summary>
    /// Nothing to pin to. Left unpinned deliberately: the next Refresh falls back to the active query window and
    /// pins to that, which is what the message tells the user to do.
    /// </summary>
    internal void ShowNoConnection(string requestedServer)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        ShowNotice(string.IsNullOrEmpty(requestedServer)
            ? "No SQL connection to pin this window to. Open or focus a query window connected to the distributor (or a publisher), then press Refresh."
            : $"Could not get a connection for {requestedServer}. Open a query window against it, then press Refresh.", sticky: true);

        StatusText.Text = "Not connected.";
        CaptionChanged?.Invoke(null);
    }

    /// <summary>
    /// Says that this window was re-pointed at a different instance because the window cap was reached, rather than
    /// a new one being opened — the grids alone give no hint that anything was displaced.
    /// </summary>
    internal void ShowRepinnedNotice(int maxWindows)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        ShowNotice($"This window was re-pinned to {_pin.Target} — {maxWindows} Replication windows were already open, "
                 + "which is the maximum. Close one to open a server in its own window.", sticky: true);
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
            if (!string.IsNullOrWhiteSpace(value)) return value;
        return null;
    }

    // -------------------------------------------------------------------------------------------------
    // Refresh pipeline
    // -------------------------------------------------------------------------------------------------

    /// <summary>Fire-and-forget refresh. Silently no-ops if a poll is already running.</summary>
    internal void BeginRefresh(bool userInitiated)
    {
        if (_polling)
        {
            if (userInitiated) StatusText.Text = "A refresh is already running.";
            return;
        }

        var package = _package;
        if (package == null) { StatusText.Text = "Package not initialised."; return; }

        _ = package.JoinableTaskFactory.RunAsync(async () => await RefreshAsync(package, userInitiated));
    }

    private async Task RefreshAsync(AsyncPackage package, bool userInitiated)
    {
        _polling = true;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(package.DisposalToken);
        _inFlight = cts;

        try
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);
            RefreshButton.IsEnabled = false;

            // Progress and the early paint are for the polls a person is waiting on — the first one and an explicit
            // Refresh. On the timer they would replace a settled summary with a flicker of step text and re-merge
            // the Overview twice per tick, which is cost and noise for a window already populated.
            var progress = userInitiated
                ? new MonitorStatusReporter(package.JoinableTaskFactory, cts.Token, text => StatusText.Text = text)
                : null;

            if (userInitiated) StatusText.Text = "Connecting…";

            if (!_pin.IsPinned)
            {
                // Unpinned: the window was opened with nothing to pin to, so Refresh means "use the active query
                // window now, and keep it" — which is what ShowNoConnection told the user to do. Connection
                // discovery must happen on the UI thread; it reflects into SSMS's editor internals.
                string discovered = ConnectionHelper.GetActiveConnectionString();
                if (string.IsNullOrEmpty(discovered))
                {
                    ShowNoConnection(null);
                    return;
                }

                _pin.Set(discovered);
                _noticeIsSticky = false;
                ShowNotice(null);
                ApplyPinnedChrome(null);
            }

            // The pinned connection, not whatever the active editor happens to be on. Captured here on the UI
            // thread so the poll can be compared against it afterwards and discarded if the pin moved underneath.
            string baseConnection = _pin.Connection;
            string masterConnection = ReplQueryService.BuildMonitorConnectionString(baseConnection, "master");

            // Read on the UI thread — SQLExtendedSettings.Current is not thread-safe to fault in from a worker.
            var thresholds = CurrentThresholds();

            // Everything from here is off the UI thread.
            await TaskScheduler.Default;

            if (_caps == null || !string.Equals(_capsConnection, masterConnection, StringComparison.Ordinal))
            {
                _caps = await ReplCapabilities.ProbeAsync(masterConnection, cts.Token).ConfigureAwait(false);
                _capsConnection = masterConnection;

                // A different server means the previous window's samples and pending counts describe something
                // else entirely.
                _history.Clear();
                _pendingCommands = null;
                _pendingCommandsAt = null;
            }

            // Shows the Overview as soon as the distributor-side sections behind it are in, rather than after the
            // publisher and subscriber database reads it does not display — each of which opens its own connection.
            // The collection awaits this, so nothing is writing to the snapshot while the grids read it.
            Func<ReplSnapshot, Task> overviewReady = !userInitiated ? (Func<ReplSnapshot, Task>)null : async partial =>
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);

                if (string.Equals(_pin.Connection, baseConnection, StringComparison.Ordinal))
                    ApplyOverview(partial, stillCollecting: true);

                await TaskScheduler.Default;
            };

            var snapshot = await ReplQueryService.CollectAsync(masterConnection, _caps, thresholds, progress, overviewReady, cts.Token).ConfigureAwait(false);
            _history.Record(snapshot.Subscriptions);
            _history.Prune(new HashSet<string>(snapshot.Subscriptions.Select(s => s.Key), StringComparer.OrdinalIgnoreCase));

            await package.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);

            // The window was re-pinned while this poll was in flight, so the snapshot describes an instance this
            // window no longer shows. The re-pin's own BeginRefresh was refused as "already running", so the
            // replacement is queued here — otherwise the new server's grids stay empty until the next tick.
            if (!string.Equals(_pin.Connection, baseConnection, StringComparison.Ordinal))
            {
                _restartAfterPoll = true;
                return;
            }

            _masterConnection = masterConnection;
            _distributionConnection = _caps.IsDistributor
                ? ReplQueryService.BuildMonitorConnectionString(baseConnection, _caps.DistributionDatabase)
                : null;

            Apply(snapshot);
        }
        catch (OperationCanceledException)
        {
            // Tool window closed or SSMS shutting down — nothing to report.
        }
        catch (Exception ex)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            StatusText.Text = "Refresh failed: " + ex.Message;
            ShowNotice(ex.Message, tooltip: ex.ToString(), sticky: true);

            // A failing server on a timer would otherwise log and retry forever.
            if (AutoRefreshCheck.IsChecked == true)
            {
                AutoRefreshCheck.IsChecked = false;
                StatusText.Text = "Refresh failed, auto-refresh stopped: " + ex.Message;
            }

            ActivityLogHelper.LogError(package, "SQLExtended Replication Monitor", "Refresh failed: " + ex);
        }
        finally
        {
            _polling = false;
            if (_inFlight == cts) _inFlight = null;
            try { cts.Dispose(); } catch { }

            try
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                RefreshButton.IsEnabled = true;

                if (_restartAfterPoll)
                {
                    _restartAfterPoll = false;
                    BeginRefresh(userInitiated: false);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Merges a completed snapshot into every bound collection and updates the chrome. UI thread only.
    ///
    /// Split in two so the Overview can be drawn part-way through a collection — see <see cref="ApplyOverview"/>.
    /// Both halves are called here in order, so a poll that had no early paint (every timer tick) ends up in
    /// exactly the state it did before the split.
    /// </summary>
    private void Apply(ReplSnapshot snapshot)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        ApplyOverview(snapshot, stillCollecting: false);
        if (!snapshot.IsAvailable) return;

        ApplyRemainingTabs(snapshot);
    }

    /// <summary>
    /// The Overview tab: the role cards, the attention list, and the publication, subscription and agent grids it
    /// projects from and counts in the status line.
    ///
    /// <para>Called twice on a user-initiated poll — once the moment the distributor-side sections are in (with
    /// <paramref name="stillCollecting"/> set, from <c>ReplQueryService</c>'s hook) and once at the end. It is
    /// therefore written to be idempotent: merges, projections and text, nothing that accumulates.</para>
    /// </summary>
    private void ApplyOverview(ReplSnapshot snapshot, bool stillCollecting)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // The instance's own name for itself, which the caption and header prefer over the connect target — behind
        // a listener or a CNAME they differ, and with several windows open the real name is what tells them apart.
        _serverName = snapshot.ServerName;
        _loginName = snapshot.LoginName;
        ApplyPinnedChrome(null);

        if (!snapshot.IsAvailable)
        {
            _publications.Clear(); _subscriptions.Clear(); _attention.Clear(); _agents.Clear();
            _publisherDatabases.Clear(); _subscriberDatabases.Clear(); _issues.Clear();
            TracerPublicationCombo.Items.Clear();
            CloseDetail();

            ApplyRoleCards(snapshot);
            SetVerdict("Nothing to monitor", snapshot.UnavailableReason, ReplIssueSeverity.Information);

            // Sticky: "replication is not configured here" is a standing condition, not a transient warning, and
            // it must not be displaced by the next poll finding nothing to report.
            ShowNotice(snapshot.UnavailableReason, sticky: true);
            StatusText.Text = "Nothing to monitor.";
            TimingText.Text = "";
            AttentionGrid.Visibility = Visibility.Collapsed;
            AttentionEmpty.Visibility = Visibility.Visible;
            AttentionEmpty.Text = snapshot.UnavailableReason;

            if (AutoRefreshCheck.IsChecked == true)
                AutoRefreshCheck.IsChecked = false;
            return;
        }

        RowMerge.Apply(_publications, snapshot.Publications, r => r.Key, CopyPublication);
        RowMerge.Apply(_subscriptions, snapshot.Subscriptions, r => r.Key, CopySubscription);
        RowMerge.Apply(_agents, snapshot.Agents, r => r.Key, CopyAgent);

        // Pending counts were obtained separately and at cost; put them back on the freshly merged rows.
        ReapplyPendingCommands();

        _subscriptionsView.Refresh();
        _agentsView.Refresh();
        _publicationsView.Refresh();

        // The attention list is a projection of the same rows, so the merged instances are reused — both grids
        // then show identical values and share one set of history buffers. Unhealthy sorts above degraded.
        var attention = _subscriptions.Where(s => s.IsUnhealthy).Concat(_subscriptions.Where(s => s.IsWarning)).ToList();
        RowMerge.Apply(_attention, attention, r => r.Key, (existing, updated) => { });
        AttentionGrid.Visibility = attention.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AttentionEmpty.Visibility = attention.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        AttentionEmpty.Text = _subscriptions.Count == 0
            ? (snapshot.Role.IsDistributor ? "No subscriptions on this distributor." : "Subscriptions are only visible from the distributor.")
            : $"All {_subscriptions.Count} subscription(s) are healthy.";

        ApplyRoleCards(snapshot);
        UpdateTracerPublications();
        UpdateStatus(snapshot, stillCollecting);
    }

    /// <summary>
    /// Everything behind the Overview: the publisher and subscriber database tabs, the diagnostics findings, the
    /// verdict strip and the final status and timing. Runs only once a collection has finished. UI thread only.
    /// </summary>
    private void ApplyRemainingTabs(ReplSnapshot snapshot)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        RowMerge.Apply(_publisherDatabases, snapshot.PublisherDatabases, r => r.Key, CopyPublisherDatabase);
        RowMerge.Apply(_subscriberDatabases, snapshot.SubscriberDatabases, r => r.Key, CopySubscriberDatabase);

        // Findings are immutable value rows, so the merge only needs to add and remove; keying on the whole
        // finding means an unchanged one keeps its place (and the user's scroll position) across polls.
        RowMerge.Apply(_issues, snapshot.Issues, IssueKey, (existing, updated) => { });
        _issuesView.Refresh();

        ApplyVerdict(snapshot);
        UpdateIssuesHint();

        ShowNotice(snapshot.Warnings.Count == 0 ? null : "Some sections could not be collected — " + string.Join("; ", snapshot.Warnings) + ".");
        UpdateStatus(snapshot);

        TimingText.Text = BuildTiming(snapshot);
    }

    /// <summary>
    /// When the snapshot was taken, how many sections it read and how long it took. The section count is here
    /// because this dashboard reads three different databases and needs different rights for each: "8 sections"
    /// beside an empty tab says something a duration on its own does not.
    /// </summary>
    private static string BuildTiming(ReplSnapshot snapshot)
    {
        string sections = snapshot.SectionsFailed > 0
            ? $"{snapshot.SectionsRead - snapshot.SectionsFailed} of {snapshot.SectionsRead} sections"
            : $"{snapshot.SectionsRead} sections";

        return $"{snapshot.CollectedAtLocal:HH:mm:ss} · {sections} · {snapshot.Duration.TotalMilliseconds:N0} ms";
    }

    // -------------------------------------------------------------------------------------------------
    // Chrome: role cards, verdict strip, status line
    // -------------------------------------------------------------------------------------------------

    private static string IssueKey(ReplIssueRow issue) => $"{issue.Severity}|{issue.Area}|{issue.Subject}|{issue.Detail}";

    private void ApplyRoleCards(ReplSnapshot snapshot)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var role = snapshot.Role;

        DistributorText.Text = role.IsDistributor
            ? "This instance"
            : string.IsNullOrWhiteSpace(role.DistributorName) ? "Not a distributor" : role.DistributorName;

        DistributionDbText.Text = string.IsNullOrWhiteSpace(role.DistributionDatabase) ? "" : role.DistributionDatabase;

        RetentionText.Text = role.MaxDistributionRetentionHours == null
            ? ""
            : $"retention {Hours(role.MinDistributionRetentionHours)}–{Hours(role.MaxDistributionRetentionHours)}, history {Hours(role.HistoryRetentionHours)}";

        PublicationCountText.Text = role.IsDistributor ? snapshot.Publications.Count.ToString("N0") : "—";
        int transactional = snapshot.Publications.Count(p => string.Equals(p.PublicationType, "Transactional", StringComparison.OrdinalIgnoreCase));
        int merge = snapshot.Publications.Count(p => string.Equals(p.PublicationType, "Merge", StringComparison.OrdinalIgnoreCase));
        int snapshotType = snapshot.Publications.Count - transactional - merge;
        PublicationDetailText.Text = role.IsDistributor
            ? $"{transactional} transactional, {snapshotType} snapshot, {merge} merge"
            : "visible only from the distributor";

        SubscriptionCountText.Text = role.IsDistributor ? snapshot.Subscriptions.Count.ToString("N0") : "—";
        int failed = snapshot.Subscriptions.Count(s => s.IsUnhealthy);
        int degraded = snapshot.Subscriptions.Count(s => s.IsWarning);
        SubscriptionDetailText.Text = role.IsDistributor
            ? (failed == 0 && degraded == 0 ? "all healthy" : $"{failed} failing, {degraded} degraded")
            : "visible only from the distributor";

        // Worst latency answers the "is it keeping up" question without reading the grid.
        var worst = snapshot.Subscriptions
            .Where(s => s.TotalLatencySeconds != null)
            .OrderByDescending(s => s.TotalLatencySeconds)
            .FirstOrDefault();

        if (worst == null)
        {
            WorstLatencyText.Text = "—";
            WorstLatencyDetailText.Text = role.IsDistributor ? "no latency reported yet" : "visible only from the distributor";
        }
        else
        {
            WorstLatencyText.Text = FormatSeconds(worst.TotalLatencySeconds);
            WorstLatencyDetailText.Text = $"{worst.Publication} → {worst.Subscriber}";
        }

        RoleSummaryText.Text = _caps == null
            ? ""
            : $"This instance is {_caps.DescribeRoles()}"
              + (string.IsNullOrEmpty(_caps.Edition) ? "" : $" · {_caps.Edition}")
              + (_pendingCommandsAt == null ? "" : $" · pending commands as at {_pendingCommandsAt:HH:mm:ss}");
    }

    /// <summary>The Overview's one-line answer. Worst finding wins.</summary>
    private void ApplyVerdict(ReplSnapshot snapshot)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        int critical = snapshot.Issues.Count(i => i.Severity == ReplIssueSeverity.Critical);
        int warning = snapshot.Issues.Count(i => i.Severity == ReplIssueSeverity.Warning);

        if (critical > 0)
        {
            var worst = snapshot.Issues.First(i => i.Severity == ReplIssueSeverity.Critical);
            SetVerdict(critical == 1 ? "1 critical finding" : $"{critical} critical findings",
                       $"{worst.Subject} — {worst.Detail}", ReplIssueSeverity.Critical);
        }
        else if (warning > 0)
        {
            var worst = snapshot.Issues.First(i => i.Severity == ReplIssueSeverity.Warning);
            SetVerdict(warning == 1 ? "1 warning" : $"{warning} warnings",
                       $"{worst.Subject} — {worst.Detail}", ReplIssueSeverity.Warning);
        }
        else
        {
            SetVerdict("Healthy",
                       $"{snapshot.Publications.Count} publication(s), {snapshot.Subscriptions.Count} subscription(s) and {snapshot.Agents.Count} agent(s) checked with no problems found.",
                       ReplIssueSeverity.Information);
        }
    }

    private readonly ReplSeverityBrushConverter _severityBrush = new ReplSeverityBrushConverter();

    private void SetVerdict(string headline, string detail, ReplIssueSeverity severity)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        VerdictText.Text = headline;
        VerdictText.Foreground = (System.Windows.Media.Brush)_severityBrush.Convert(severity, typeof(System.Windows.Media.Brush), null, System.Globalization.CultureInfo.CurrentUICulture);
        VerdictDetailText.Text = detail ?? "";
    }

    private void UpdateIssuesHint()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (IssuesHint == null) return;

        int critical = _issues.Count(i => i.Severity == ReplIssueSeverity.Critical);
        int warning = _issues.Count(i => i.Severity == ReplIssueSeverity.Warning);
        int info = _issues.Count(i => i.Severity == ReplIssueSeverity.Information);

        if (critical == 0 && warning == 0)
        {
            IssuesHint.Text = IssuesOnlyCheck?.IsChecked == true
                ? "No warnings or critical findings — the grid is empty because \"Problems only\" is hiding the informational rows."
                : $"No problems found. {info} informational note(s) below say what was checked and what this connection could not see.";
            return;
        }

        var parts = new List<string>();
        if (critical > 0) parts.Add($"{critical} critical");
        if (warning > 0) parts.Add($"{warning} warning");
        if (info > 0 && IssuesOnlyCheck?.IsChecked != true) parts.Add($"{info} informational");

        IssuesHint.Text = string.Join(", ", parts) + ". Hover the last column for the full explanation.";
    }

    /// <param name="stillCollecting">
    /// Set while only the distributor-side sections are in. Says so, because the Overview looks complete at that
    /// point while the Publisher and Subscriber tabs behind it are still empty.
    /// </param>
    private void UpdateStatus(ReplSnapshot snapshot = null, bool stillCollecting = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_subscriptions.Count == 0 && snapshot == null) return;

        int shown = _subscriptionsView.Cast<object>().Count();
        int failing = _subscriptions.Count(s => s.IsUnhealthy);
        int degraded = _subscriptions.Count(s => s.IsWarning);

        var parts = new List<string>
        {
            $"{_publications.Count} publication(s)",
            shown == _subscriptions.Count ? $"{_subscriptions.Count} subscription(s)" : $"{shown} of {_subscriptions.Count} subscription(s)",
            $"{_agents.Count} agent(s)"
        };

        if (failing > 0) parts.Add($"{failing} failing");
        if (degraded > 0) parts.Add($"{degraded} degraded");
        if (failing == 0 && degraded == 0 && _subscriptions.Count > 0) parts.Add("all healthy");

        string status = string.Join(" · ", parts);

        // Worth saying out loud: a publisher cannot read the distribution database, so the empty grids are the
        // expected result rather than a fault.
        if (_caps != null && !_caps.IsDistributor)
            status += "  Not the distributor — publications, subscriptions and agents are only visible there.";

        if (stillCollecting) status += "   Still reading the publisher and subscriber databases…";

        StatusText.Text = status;
    }

    private static string Hours(double? hours)
    {
        if (hours == null) return "—";
        if (hours < 24) return $"{hours.Value:N0}h";
        return $"{hours.Value / 24d:N0}d";
    }

    private static string FormatSeconds(double? seconds)
    {
        if (seconds == null) return "—";
        if (seconds < 1) return "<1s";
        if (seconds < 60) return $"{seconds.Value:N0}s";
        if (seconds < 3600) return TimeSpan.FromSeconds(seconds.Value).ToString(@"m\m\ ss\s");
        if (seconds < 86400) return TimeSpan.FromSeconds(seconds.Value).ToString(@"h\h\ mm\m");
        return TimeSpan.FromSeconds(seconds.Value).ToString(@"d\d\ h\h");
    }

    // -------------------------------------------------------------------------------------------------
    // Notice banner
    // -------------------------------------------------------------------------------------------------

    /// <param name="tooltip">Optional long-form detail (a full stack trace), hung off the banner because VS only
    /// writes ActivityLog.xml when launched with /log — so for a normal SSMS session it is not there.</param>
    /// <param name="sticky">Keep the message until the user does something about it. Without this the next poll
    /// replaces it with that snapshot's (usually empty) warning text, and an error flashes up and vanishes.</param>
    private void ShowNotice(string text, string tooltip = null, bool sticky = false)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        // A non-sticky message must not paint over a sticky one that is still standing.
        if (_noticeIsSticky && !sticky) return;

        _noticeIsSticky = sticky && !string.IsNullOrWhiteSpace(text);

        if (string.IsNullOrWhiteSpace(text))
        {
            NoticeBorder.Visibility = Visibility.Collapsed;
            NoticeText.Text = "";
            NoticeText.ToolTip = null;
            _noticeClipboardText = null;
            return;
        }

        NoticeText.Text = text;

        // A raw string tooltip renders as one unwrapped line and a stack trace runs off the screen.
        NoticeText.ToolTip = string.IsNullOrWhiteSpace(tooltip)
            ? null
            : new TextBlock { Text = tooltip, TextWrapping = TextWrapping.Wrap, MaxWidth = 700, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 11 };

        _noticeClipboardText = BuildNoticeClipboardText(text, tooltip);
        NoticeBorder.Visibility = Visibility.Visible;
    }

    private string BuildNoticeClipboardText(string text, string detail)
    {
        var builder = new StringBuilder();
        builder.AppendLine("SQLExtended Replication Monitor");
        builder.AppendLine("Server: " + (string.IsNullOrEmpty(_serverName) ? "(unknown)" : _serverName));
        builder.AppendLine("When:   " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine();
        builder.AppendLine(text);

        if (!string.IsNullOrWhiteSpace(detail))
        {
            builder.AppendLine();
            builder.AppendLine("--- detail ---");
            builder.AppendLine(detail);
        }

        return builder.ToString();
    }

    private void NoticeCopy_Click(object sender, RoutedEventArgs e)
    {
        string payload = _noticeClipboardText;
        if (string.IsNullOrEmpty(payload)) return;

        try
        {
            Clipboard.SetText(payload);
            StatusText.Text = "Copied the message and its detail to the clipboard.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Copy failed: " + ex.Message;
        }
    }

    private void NoticeClose_Click(object sender, RoutedEventArgs e)
    {
        // Dismissing is the acknowledgement, so the sticky flag goes with it.
        _noticeIsSticky = false;
        ShowNotice(null);
    }

    // -------------------------------------------------------------------------------------------------
    // Row copiers for the in-place merge. Keys are excluded — they are what matched the rows.
    // -------------------------------------------------------------------------------------------------

    private static void CopyPublication(ReplPublicationRow into, ReplPublicationRow from)
    {
        into.PublicationType = from.PublicationType;
        into.ArticleCount = from.ArticleCount;
        into.SubscriptionCount = from.SubscriptionCount;
        into.ImmediateSync = from.ImmediateSync;
        into.AllowPush = from.AllowPush;
        into.AllowPull = from.AllowPull;
        into.AllowAnonymous = from.AllowAnonymous;
        into.IndependentAgent = from.IndependentAgent;
        into.RetentionHours = from.RetentionHours;
        into.Description = from.Description;
        into.SnapshotStatus = from.SnapshotStatus;
        into.SnapshotTime = from.SnapshotTime;
    }

    private static void CopySubscription(ReplSubscriptionRow into, ReplSubscriptionRow from)
    {
        into.AgentId = from.AgentId;
        into.PublicationType = from.PublicationType;
        into.SubscriptionType = from.SubscriptionType;
        into.SyncType = from.SyncType;
        into.ArticleCount = from.ArticleCount;
        into.SubscriptionSeqno = from.SubscriptionSeqno;
        into.Status = from.Status;
        into.RunStatus = from.RunStatus;
        into.LastComment = from.LastComment;
        into.LastError = from.LastError;
        into.LastActivity = from.LastActivity;
        into.LastStart = from.LastStart;
        into.DeliveredTransactions = from.DeliveredTransactions;
        into.DeliveredCommands = from.DeliveredCommands;
        into.DeliveryRate = from.DeliveryRate;
        into.DistributionLatencySeconds = from.DistributionLatencySeconds;
        into.LogReaderLatencySeconds = from.LogReaderLatencySeconds;
        into.JobEnabled = from.JobEnabled;
        into.JobRunning = from.JobRunning;
        into.JobName = from.JobName;
        into.RetentionHours = from.RetentionHours;

        // The thresholds can change under the row (a settings edit), and they decide the tint.
        into.Thresholds = from.Thresholds;
        into.RaiseThresholdChanged();

        // The history buffer was assigned to the freshly collected row; move it across and notify.
        into.LatencyHistory = from.LatencyHistory;
        into.RaiseHistoryChanged();
    }

    private static void CopyAgent(ReplAgentRow into, ReplAgentRow from)
    {
        into.Name = from.Name;
        into.Publisher = from.Publisher;
        into.PublisherDb = from.PublisherDb;
        into.Publication = from.Publication;
        into.Subscriber = from.Subscriber;
        into.SubscriberDb = from.SubscriberDb;
        into.JobId = from.JobId;
        into.RunStatus = from.RunStatus;
        into.StartTime = from.StartTime;
        into.LastActivity = from.LastActivity;
        into.DurationSeconds = from.DurationSeconds;
        into.Comments = from.Comments;
        into.LastError = from.LastError;
        into.LatencySeconds = from.LatencySeconds;
        into.DeliveryRate = from.DeliveryRate;
        into.DeliveredTransactions = from.DeliveredTransactions;
        into.DeliveredCommands = from.DeliveredCommands;
        into.UploadedChanges = from.UploadedChanges;
        into.DownloadedChanges = from.DownloadedChanges;
        into.Conflicts = from.Conflicts;
        into.JobEnabled = from.JobEnabled;
        into.JobRunning = from.JobRunning;
        into.JobName = from.JobName;
    }

    private static void CopyPublisherDatabase(ReplPublisherDatabaseRow into, ReplPublisherDatabaseRow from)
    {
        into.IsPublished = from.IsPublished;
        into.IsMergePublished = from.IsMergePublished;
        into.IsSubscribed = from.IsSubscribed;
        into.IsSyncWithBackup = from.IsSyncWithBackup;
        into.RecoveryModel = from.RecoveryModel;
        into.LogReuseWait = from.LogReuseWait;
        into.LogPercentUsed = from.LogPercentUsed;
        into.LogSizeKb = from.LogSizeKb;
        into.ReplicatedTransactions = from.ReplicatedTransactions;
        into.ReplicationRate = from.ReplicationRate;
        into.ReplicationLatencySeconds = from.ReplicationLatencySeconds;
        into.Thresholds = from.Thresholds;
        into.RaiseThresholdChanged();
    }

    private static void CopySubscriberDatabase(ReplSubscriberDatabaseRow into, ReplSubscriberDatabaseRow from)
    {
        into.SubscriptionType = from.SubscriptionType;
        into.LastApplied = from.LastApplied;
        into.TransactionTimestamp = from.TransactionTimestamp;
        into.Description = from.Description;
    }

    // -------------------------------------------------------------------------------------------------
    // Pending commands — on demand
    // -------------------------------------------------------------------------------------------------

    private void LoadPending_Click(object sender, RoutedEventArgs e)
    {
        var package = _package;
        if (package == null) return;

        string connection = _distributionConnection;
        if (string.IsNullOrEmpty(connection))
        {
            PendingHint.Text = _caps != null && !_caps.IsDistributor
                ? "Undelivered commands live in the distribution database — connect to the distributor to count them."
                : "Refresh first so the monitor knows which distribution database to read.";
            return;
        }

        if (_caps != null && !_caps.HasDistributionStatusView)
        {
            PendingHint.Text = "This distribution database does not expose MSdistribution_status, so undelivered commands cannot be counted.";
            return;
        }

        _ = package.JoinableTaskFactory.RunAsync(async () =>
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            PendingButton.IsEnabled = false;
            PendingHint.Text = "Counting undelivered commands… this scans the command table and can take a while on a backlogged distributor.";

            try
            {
                await TaskScheduler.Default;
                var pending = await ReplQueryService.ReadPendingCommandsAsync(connection, package.DisposalToken).ConfigureAwait(false);

                await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                _pendingCommands = pending;
                _pendingCommandsAt = DateTime.Now;
                ReapplyPendingCommands();
                _subscriptionsView.Refresh();

                long total = pending.Values.Sum(v => v.Undelivered);
                PendingHint.Text = $"{total:N0} undelivered command(s) across {pending.Count} agent(s), as at {_pendingCommandsAt:HH:mm:ss}. "
                                 + "Load again in a minute — falling means it is draining, level or rising means the agent cannot keep up.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                PendingHint.Text = ex.Message;
            }
            finally
            {
                try
                {
                    await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                    PendingButton.IsEnabled = true;
                }
                catch { }
            }
        });
    }

    /// <summary>
    /// Puts the last pending-command counts back onto the subscription rows after a merge. They are kept across
    /// refreshes rather than recollected because the count is expensive; the Overview says when they were taken
    /// so an old number is never mistaken for a current one.
    /// </summary>
    private void ReapplyPendingCommands()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_pendingCommands == null) return;

        foreach (var row in _subscriptions)
        {
            if (row.AgentId == null || !_pendingCommands.TryGetValue(row.AgentId.Value, out var counts))
            {
                row.UndeliveredCommands = null;
                row.DeliveredCommandsInDistDb = null;
                continue;
            }

            row.UndeliveredCommands = counts.Undelivered;
            row.DeliveredCommandsInDistDb = counts.Delivered;
        }
    }

    // -------------------------------------------------------------------------------------------------
    // Tracer tokens — on demand, plus the one write in this window
    // -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Fills the publication picker with the publications a token can actually be posted into from here, which
    /// means the ones published by this instance. Offering the rest and failing at the procedure call would be
    /// worse than not offering them.
    /// </summary>
    private void UpdateTracerPublications()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var postable = _publications
            .Where(p => ReplActionService.CanPostFrom(_serverName, p.Publisher))
            .Where(p => !string.Equals(p.PublicationType, "Merge", StringComparison.OrdinalIgnoreCase))
            .Select(p => $"{p.PublisherDb} · {p.Publication}")
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Preserve the selection across refreshes — the combo is repopulated every poll.
        string selected = TracerPublicationCombo.SelectedItem as string;

        TracerPublicationCombo.Items.Clear();
        foreach (string item in postable)
            TracerPublicationCombo.Items.Add(item);

        if (selected != null && postable.Contains(selected))
            TracerPublicationCombo.SelectedItem = selected;
        else if (postable.Count > 0)
            TracerPublicationCombo.SelectedIndex = 0;

        bool canPost = postable.Count > 0;
        PostTracerButton.IsEnabled = canPost;
        TracerPublicationCombo.IsEnabled = canPost;

        if (!canPost)
        {
            // Merge publications are excluded deliberately: tracer tokens are a transactional-replication feature.
            TracerHint.Text = _publications.Count == 0
                ? "No publications visible from here. Tracer tokens are posted in the publication database on the publisher."
                : "None of these publications is published by this instance, so a token cannot be posted from here. Connect to the publisher. (Merge publications do not support tracer tokens at all.)";
        }
    }

    private void LoadTracer_Click(object sender, RoutedEventArgs e)
    {
        var package = _package;
        if (package == null) return;

        string connection = _distributionConnection;
        if (string.IsNullOrEmpty(connection))
        {
            TracerHint.Text = "Tracer token history lives in the distribution database — connect to the distributor to read it.";
            return;
        }

        if (_caps != null && !_caps.HasTracerTokens)
        {
            TracerHint.Text = "This distribution database has no tracer token tables.";
            return;
        }

        _ = package.JoinableTaskFactory.RunAsync(async () =>
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            LoadTracerButton.IsEnabled = false;
            TracerHint.Text = "Reading tracer tokens…";

            try
            {
                await TaskScheduler.Default;
                var tokens = await ReplQueryService.ReadTracerTokensAsync(connection, package.DisposalToken).ConfigureAwait(false);

                await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                TracerGrid.ItemsSource = tokens;

                int pending = tokens.Count(t => t.IsWarning);
                TracerHint.Text = tokens.Count == 0
                    ? "No tracer tokens recorded. Post one to measure end-to-end latency for real rather than by estimate."
                    : $"{tokens.Count} token record(s)"
                      + (pending > 0 ? $", {pending} of which never reached a subscriber." : ", all delivered.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                TracerHint.Text = ex.Message;
            }
            finally
            {
                try
                {
                    await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                    LoadTracerButton.IsEnabled = true;
                }
                catch { }
            }
        });
    }

    /// <summary>
    /// Posts a tracer token, after confirming.
    ///
    /// The confirmation is not ceremony: this window follows whatever query window has focus, so the instance it
    /// points at is not always the one the user has in mind, and this writes into a production publication. It is
    /// a harmless write — one command that every subscriber applies as a no-op — but it is still a write, so the
    /// prompt names both the publication and the server, and defaults to No.
    /// </summary>
    private void PostTracer_Click(object sender, RoutedEventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var package = _package;
        if (package == null) return;

        string selected = TracerPublicationCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(selected)) { TracerHint.Text = "Select a publication first."; return; }

        // The combo shows "database · publication"; both halves are needed and the separator cannot appear in
        // either (SQL Server does not allow it in a publication name).
        int separator = selected.IndexOf(" · ", StringComparison.Ordinal);
        if (separator < 0) { TracerHint.Text = "Could not parse the selected publication."; return; }

        string publisherDb = selected.Substring(0, separator);
        string publication = selected.Substring(separator + 3);

        string baseConnection = _masterConnection;
        if (string.IsNullOrEmpty(baseConnection)) { TracerHint.Text = "Refresh first so the monitor knows which server to post to."; return; }

        string server = string.IsNullOrEmpty(_serverName) ? "the current server" : _serverName;
        int answer = VsShellUtilities.ShowMessageBox(package,
            $"Post a tracer token into the publication \"{publication}\" in {publisherDb} on {server}?\r\n\r\n"
            + "This writes one tracer command into the replication stream so its end-to-end latency can be measured. "
            + "Subscribers apply it as a no-op — no user data changes.",
            "SQLExtended — Replication Monitor",
            OLEMSGICON.OLEMSGICON_QUERY, OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_SECOND);

        if (answer != (int)Microsoft.VisualStudio.VSConstants.MessageBoxResult.IDYES) return;

        // Posting supersedes whatever the banner was complaining about.
        _noticeIsSticky = false;
        ShowNotice(null);

        TracerHint.Text = $"Posting a tracer token into {publication}…";

        _ = package.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await TaskScheduler.Default;
                await ReplActionService.PostTracerTokenAsync(baseConnection, publisherDb, publication, package.DisposalToken).ConfigureAwait(false);

                await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                TracerHint.Text = $"Posted a tracer token into {publication}. Give it a few seconds, then press Load tokens to see where it got to.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

                // Typically a permissions refusal or "the publication does not exist" — the server's own wording
                // is more accurate than anything this code could pre-check for.
                TracerHint.Text = "Posting the token failed: " + ex.Message;
                ShowNotice($"Could not post a tracer token into {publication} — {ex.Message}", tooltip: ex.ToString(), sticky: true);
                ActivityLogHelper.LogError(package, "SQLExtended Replication Monitor", $"Post tracer token failed on {publication}: {ex}");
            }
        });
    }

    // -------------------------------------------------------------------------------------------------
    // Errors — on demand
    // -------------------------------------------------------------------------------------------------

    private void LoadErrors_Click(object sender, RoutedEventArgs e)
    {
        var package = _package;
        if (package == null) return;

        string connection = _distributionConnection;
        if (string.IsNullOrEmpty(connection))
        {
            ErrorsHint.Text = "MSrepl_errors lives in the distribution database — connect to the distributor to read it.";
            return;
        }

        int top = ErrorCounts[Math.Max(0, Math.Min(ErrorCounts.Length - 1, ErrorCountCombo.SelectedIndex))];

        _ = package.JoinableTaskFactory.RunAsync(async () =>
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            LoadErrorsButton.IsEnabled = false;
            ErrorsHint.Text = "Reading MSrepl_errors…";

            try
            {
                await TaskScheduler.Default;
                var errors = await ReplQueryService.ReadErrorsAsync(connection, top, package.DisposalToken).ConfigureAwait(false);

                await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                _allErrors = errors;
                ErrorGrid.ItemsSource = _allErrors;
                ErrorsHint.Text = errors.Count == 0
                    ? "No errors recorded — MSrepl_errors is empty, or the history retention window has already cleaned them up."
                    : $"{errors.Count} error(s), newest first. Hover the last column for the full text.";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                ErrorsHint.Text = ex.Message;
            }
            finally
            {
                try
                {
                    await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                    LoadErrorsButton.IsEnabled = true;
                }
                catch { }
            }
        });
    }

    // -------------------------------------------------------------------------------------------------
    // Copy / open as query
    // -------------------------------------------------------------------------------------------------

    private void CopyTab_Click(object sender, RoutedEventArgs e)
    {
        var grid = ActiveGrid();
        if (grid == null || grid.Items.Count == 0) { StatusText.Text = "Nothing to copy on this tab."; return; }

        var text = new StringBuilder();
        var columns = grid.Columns.Where(c => c.Visibility == Visibility.Visible).ToList();

        text.AppendLine(string.Join("\t", columns.Select(c => c.Header?.ToString() ?? "")));

        foreach (var item in grid.Items)
        {
            var cells = columns.Select(c => CellText(c, item)?.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ') ?? "");
            text.AppendLine(string.Join("\t", cells));
        }

        try
        {
            Clipboard.SetText(text.ToString());
            StatusText.Text = $"Copied {grid.Items.Count} row(s) as tab-separated text.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Copy failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Resolves a cell's displayed text for the clipboard. Template columns (the coloured status cells and the
    /// sparkline) have no binding to read, so their underlying property is looked up by the column's
    /// SortMemberPath — which is set on exactly those columns for this reason.
    /// </summary>
    private static string CellText(DataGridColumn column, object item)
    {
        if (column is DataGridBoundColumn bound && bound.Binding is Binding binding)
            return ValueOf(item, binding.Path?.Path);

        return ValueOf(item, column.SortMemberPath);
    }

    private static string ValueOf(object item, string path)
    {
        if (item == null || string.IsNullOrEmpty(path)) return "";
        var property = item.GetType().GetProperty(path);
        return property == null ? "" : Convert.ToString(property.GetValue(item));
    }

    /// <summary>
    /// The grid "Copy tab" acts on. Tabs with more than one grid nominate their primary one — the one the tab is
    /// named after.
    /// </summary>
    private DataGrid ActiveGrid()
    {
        switch (Tabs.SelectedIndex)
        {
            case TabOverview: return AttentionGrid;
            case TabDiagnostics: return IssueGrid;
            case TabSubscriptions: return SubscriptionGrid;
            case TabPublications: return PublicationGrid;
            case TabAgents: return AgentGrid;
            case TabPublisher: return PublisherGrid;
            case TabTracer: return TracerGrid;
            case TabErrors: return ErrorGrid;
            default: return null;
        }
    }

    private void OpenAsQuery_Click(object sender, RoutedEventArgs e)
    {
        var caps = _caps;
        if (caps == null) { StatusText.Text = "Refresh first so the monitor knows what this server supports."; return; }

        string sql = SqlForActiveTab(caps);
        if (sql == null) { StatusText.Text = "No query backs this tab."; return; }

        // Nearly all of these run in the distribution database, and pasting them into a master-connected window
        // fails with a confusing "invalid object name". The USE goes in rather than making the user work it out.
        if (RequiresDistributionDatabase(Tabs.SelectedIndex) && !string.IsNullOrEmpty(caps.DistributionDatabase))
            sql = $"USE {QuoteName(caps.DistributionDatabase)};{Environment.NewLine}GO{Environment.NewLine}{Environment.NewLine}{sql}";

        OpenTextInNewQueryWindow(sql);
    }

    private static bool RequiresDistributionDatabase(int tabIndex) =>
        tabIndex == TabDiagnostics || tabIndex == TabSubscriptions || tabIndex == TabPublications
        || tabIndex == TabAgents || tabIndex == TabTracer || tabIndex == TabErrors || tabIndex == TabOverview;

    /// <summary>Brackets an identifier and doubles any closing bracket, the same rule QUOTENAME follows.</summary>
    private static string QuoteName(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    /// <summary>
    /// The exact T-SQL behind the active tab, capability substitutions included, so what the user gets back runs
    /// on the server they are looking at rather than on the newest one.
    /// </summary>
    private string SqlForActiveTab(ReplCapabilities caps)
    {
        switch (Tabs.SelectedIndex)
        {
            case TabOverview: return ReplQueryService.PublicationsSql(caps) + Environment.NewLine + ReplQueryService.SubscriptionsSql(caps);

            // The findings are derived in this process, so what the user gets is the state they were derived
            // from — with a header saying so, otherwise the returned batch looks like it should reproduce them.
            case TabDiagnostics:
                return "-- The Diagnostics tab evaluates its rules in the extension, against the result of these"
                     + Environment.NewLine
                     + "-- queries plus the publisher-side ones on the Publisher tab. There is no server-side"
                     + Environment.NewLine
                     + "-- query that produces the findings themselves."
                     + Environment.NewLine + Environment.NewLine
                     + ReplQueryService.SubscriptionsSql(caps) + Environment.NewLine
                     + ReplQueryService.AgentsSql(caps);

            case TabSubscriptions:
                return ReplQueryService.SubscriptionsSql(caps)
                     + Environment.NewLine
                     + "-- Undelivered commands, loaded on demand by the toolbar button:"
                     + Environment.NewLine
                     + ReplQueryService.PendingCommandsSql;

            case TabPublications: return ReplQueryService.PublicationsSql(caps);
            case TabAgents: return ReplQueryService.AgentsSql(caps) + Environment.NewLine + ReplQueryService.AgentJobsSql(caps);

            // The one tab that reads master and the subscriber databases rather than the distributor.
            case TabPublisher:
                return ReplQueryService.PublisherDatabasesSql + Environment.NewLine
                     + ReplQueryService.ReplCountersSql + Environment.NewLine
                     + ReplQueryService.SubscriberDatabasesSql;

            case TabTracer: return ReplQueryService.TracerTokensSql;
            case TabErrors: return ReplQueryService.ErrorsSql(ErrorCounts[Math.Max(0, Math.Min(ErrorCounts.Length - 1, ErrorCountCombo.SelectedIndex))]);
            default: return null;
        }
    }

    /// <summary>
    /// Writes the SQL to a temp .sql file and opens it as a query window, so it gets T-SQL highlighting and F5.
    /// Mirrors the approach in the Always On monitor and the Script Library.
    /// </summary>
    private void OpenTextInNewQueryWindow(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var dte = (DTE2)Package.GetGlobalService(typeof(DTE));
        if (dte == null) { StatusText.Text = "No DTE available."; return; }

        try
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SQLExtended", "Replication");
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, $"Replication_{DateTime.Now:yyyyMMdd_HHmmss_fff}.sql");
            System.IO.File.WriteAllText(path, text, new UTF8Encoding(false));

            dte.ItemOperations.OpenFile(path, EnvDTE.Constants.vsViewKindTextView);
            StatusText.Text = "Opened the tab's query in a new window.";
        }
        catch (Exception ex)
        {
            try { Clipboard.SetText(text); } catch { }
            StatusText.Text = $"Could not open a query window ({ex.Message}). Copied the SQL to the clipboard instead.";
        }
    }
}
