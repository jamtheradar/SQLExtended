using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SQLExtended.Monitoring.Replication;

/// <summary>
/// Minimal change-notification base, same contract as the other dashboards: the grids are merged in place by
/// key (see <see cref="RowMerge"/>) rather than rebound, so rows must raise PropertyChanged for new values to
/// reach the UI without resetting selection or scroll position.
/// </summary>
internal abstract class ReplRowBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Replication agent run status, as stored in every <c>MS*_history</c> table's <c>runstatus</c> column. The
/// numbers are the contract — there is no <c>*_desc</c> companion column anywhere in the distribution database.
/// </summary>
internal enum ReplRunStatus
{
    Unknown = 0,
    Starting = 1,
    Succeeded = 2,
    InProgress = 3,
    Idle = 4,
    Retrying = 5,
    Failed = 6
}

/// <summary>Which agent produced a row. Each type reads from its own pair of tables but is presented as one list.</summary>
internal enum ReplAgentType
{
    LogReader,
    Distribution,
    Snapshot,
    Merge,
    QueueReader
}

/// <summary>
/// Shared decoding for the numeric columns the distribution database uses instead of descriptive ones. Kept
/// separate from the rows so it can be unit tested without SqlClient or WPF, the same split the Agent Jobs
/// dashboard uses for <c>JobValueParser</c>.
/// </summary>
internal static class ReplValueParser
{
    public static ReplRunStatus ToRunStatus(int? runStatus)
    {
        switch (runStatus)
        {
            case 1: return ReplRunStatus.Starting;
            case 2: return ReplRunStatus.Succeeded;
            case 3: return ReplRunStatus.InProgress;
            case 4: return ReplRunStatus.Idle;
            case 5: return ReplRunStatus.Retrying;
            case 6: return ReplRunStatus.Failed;
            default: return ReplRunStatus.Unknown;
        }
    }

    public static string Describe(ReplRunStatus status)
    {
        switch (status)
        {
            case ReplRunStatus.Starting: return "Starting";
            case ReplRunStatus.Succeeded: return "Succeeded";
            case ReplRunStatus.InProgress: return "Running";
            case ReplRunStatus.Idle: return "Idle";
            case ReplRunStatus.Retrying: return "Retrying";
            case ReplRunStatus.Failed: return "Failed";
            default: return null;
        }
    }

    /// <summary>MSpublications.publication_type: 0 transactional, 1 snapshot, 2 merge.</summary>
    public static string DescribePublicationType(int? type)
    {
        switch (type)
        {
            case 0: return "Transactional";
            case 1: return "Snapshot";
            case 2: return "Merge";
            default: return null;
        }
    }

    /// <summary>MSsubscriptions.subscription_type: 0 push, 1 pull, 2 anonymous.</summary>
    public static string DescribeSubscriptionType(int? type)
    {
        switch (type)
        {
            case 0: return "Push";
            case 1: return "Pull";
            case 2: return "Anonymous";
            default: return null;
        }
    }

    /// <summary>
    /// MSsubscriptions.status: 0 inactive, 1 subscribed, 2 active. "Inactive" is the one that matters — the
    /// expiry job deactivates a subscription that has gone past the distribution retention period, and it then
    /// needs reinitializing rather than restarting.
    /// </summary>
    public static string DescribeSubscriptionStatus(int? status)
    {
        switch (status)
        {
            case 0: return "Inactive";
            case 1: return "Subscribed";
            case 2: return "Active";
            default: return null;
        }
    }

    /// <summary>MSsubscriptions.sync_type: 1 automatic, 2 none, 3 replication support only, …</summary>
    public static string DescribeSyncType(int? type)
    {
        switch (type)
        {
            case 1: return "Automatic";
            case 2: return "None";
            case 3: return "Support only";
            case 4: return "Initialize with backup";
            case 5: return "Initialize from LSN";
            default: return null;
        }
    }

    /// <summary>
    /// MSpublications.retention_period_unit: 0 hours, 1 days, 2 weeks, 3 months. Converted to hours so the
    /// expiry checks have one unit to reason in.
    /// </summary>
    public static double? RetentionHours(int? retention, int? unit)
    {
        if (retention == null) return null;

        switch (unit)
        {
            case 0: return retention.Value;
            case 1: return retention.Value * 24d;
            case 2: return retention.Value * 24d * 7d;
            case 3: return retention.Value * 24d * 30d;
            default: return retention.Value * 24d;   // publication retention is in days when the unit is absent
        }
    }
}

/// <summary>What this instance's role in replication is, and how the dashboard was able to read it.</summary
internal sealed class ReplRole
{
    public bool IsDistributor { get; set; }
    public bool IsPublisher { get; set; }
    public bool IsSubscriber { get; set; }

    /// <summary>The local distribution database, when this instance is the distributor.</summary>
    public string DistributionDatabase { get; set; }

    /// <summary>From sp_helpdistributor — may name a remote server this dashboard cannot read.</summary>
    public string DistributorName { get; set; }
    public double? MinDistributionRetentionHours { get; set; }
    public double? MaxDistributionRetentionHours { get; set; }
    public double? HistoryRetentionHours { get; set; }
}

/// <summary>One publication, from MSpublications in the distribution database.</summary>
internal sealed class ReplPublicationRow : ReplRowBase
{
    public string Key => $"{Publisher}|{PublisherDb}|{Publication}";

    public string Publisher { get; set; }
    public string PublisherDb { get; set; }
    public string Publication { get; set; }

    private string _publicationType; public string PublicationType { get => _publicationType; set => Set(ref _publicationType, value); }
    private int _articleCount; public int ArticleCount { get => _articleCount; set => Set(ref _articleCount, value); }
    private int _subscriptionCount; public int SubscriptionCount { get => _subscriptionCount; set { Set(ref _subscriptionCount, value); Raise(nameof(IsWarning)); } }
    private bool? _immediateSync; public bool? ImmediateSync { get => _immediateSync; set => Set(ref _immediateSync, value); }
    private bool? _allowPush; public bool? AllowPush { get => _allowPush; set => Set(ref _allowPush, value); }
    private bool? _allowPull; public bool? AllowPull { get => _allowPull; set => Set(ref _allowPull, value); }
    private bool? _allowAnonymous; public bool? AllowAnonymous { get => _allowAnonymous; set => Set(ref _allowAnonymous, value); }
    private bool? _independentAgent; public bool? IndependentAgent { get => _independentAgent; set => Set(ref _independentAgent, value); }
    private double? _retentionHours; public double? RetentionHours { get => _retentionHours; set => Set(ref _retentionHours, value); }
    private string _description; public string Description { get => _description; set => Set(ref _description, value); }

    // Snapshot agent state, so "the snapshot never ran" is visible next to the publication it belongs to.
    private ReplRunStatus _snapshotStatus; public ReplRunStatus SnapshotStatus { get => _snapshotStatus; set { Set(ref _snapshotStatus, value); Raise(nameof(SnapshotStatusText)); Raise(nameof(IsUnhealthy)); } }
    private DateTime? _snapshotTime; public DateTime? SnapshotTime { get => _snapshotTime; set => Set(ref _snapshotTime, value); }

    public string SnapshotStatusText => ReplValueParser.Describe(SnapshotStatus);

    /// <summary>A failed snapshot means new or reinitialized subscriptions cannot start.</summary>
    public bool IsUnhealthy => SnapshotStatus == ReplRunStatus.Failed;

    /// <summary>A publication nobody subscribes to still costs the log reader work on the publisher.</summary>
    public bool IsWarning => !IsUnhealthy && SubscriptionCount == 0;
}

/// <summary>
/// One subscription — the row that answers "is replication working". Assembled from MSsubscriptions (grouped
/// down from its one-row-per-article shape), the distribution agent's latest history row, and the publisher
/// database's log reader latency.
/// </summary>
internal sealed class ReplSubscriptionRow : ReplRowBase
{
    public string Key => $"{Publisher}|{PublisherDb}|{Publication}|{Subscriber}|{SubscriberDb}";

    public string Publisher { get; set; }
    public string PublisherDb { get; set; }
    public string Publication { get; set; }
    public string Subscriber { get; set; }
    public string SubscriberDb { get; set; }

    /// <summary>MSsubscriptions.agent_id — how the pending-command load matches rows back to this one.</summary>
    public int? AgentId { get; set; }

    private string _publicationType; public string PublicationType { get => _publicationType; set => Set(ref _publicationType, value); }
    private string _subscriptionType; public string SubscriptionType { get => _subscriptionType; set => Set(ref _subscriptionType, value); }
    private string _syncType; public string SyncType { get => _syncType; set => Set(ref _syncType, value); }
    private int _articleCount; public int ArticleCount { get => _articleCount; set => Set(ref _articleCount, value); }
    private string _subscriptionSeqno; public string SubscriptionSeqno { get => _subscriptionSeqno; set => Set(ref _subscriptionSeqno, value); }

    private string _status; public string Status { get => _status; set { Set(ref _status, value); Raise(nameof(IsUnhealthy)); } }

    private ReplRunStatus _runStatus; public ReplRunStatus RunStatus { get => _runStatus; set { Set(ref _runStatus, value); Raise(nameof(RunStatusText)); Raise(nameof(IsUnhealthy)); Raise(nameof(IsWarning)); } }
    private string _lastComment; public string LastComment { get => _lastComment; set => Set(ref _lastComment, value); }
    private string _lastError; public string LastError { get => _lastError; set { Set(ref _lastError, value); Raise(nameof(IsUnhealthy)); } }
    private DateTime? _lastActivity; public DateTime? LastActivity { get => _lastActivity; set { Set(ref _lastActivity, value); Raise(nameof(HoursSinceActivity)); } }
    private DateTime? _lastStart; public DateTime? LastStart { get => _lastStart; set => Set(ref _lastStart, value); }

    private long? _deliveredTransactions; public long? DeliveredTransactions { get => _deliveredTransactions; set => Set(ref _deliveredTransactions, value); }
    private long? _deliveredCommands; public long? DeliveredCommands { get => _deliveredCommands; set => Set(ref _deliveredCommands, value); }
    private double? _deliveryRate; public double? DeliveryRate { get => _deliveryRate; set => Set(ref _deliveryRate, value); }

    // Latency is stored in seconds throughout, though the history tables report milliseconds — one unit in the
    // model means the thresholds and the total do not have to keep converting.
    private double? _distributionLatency; public double? DistributionLatencySeconds { get => _distributionLatency; set { Set(ref _distributionLatency, value); Raise(nameof(TotalLatencySeconds)); Raise(nameof(IsWarning)); } }
    private double? _logReaderLatency; public double? LogReaderLatencySeconds { get => _logReaderLatency; set { Set(ref _logReaderLatency, value); Raise(nameof(TotalLatencySeconds)); Raise(nameof(IsWarning)); } }

    // Filled by the on-demand pending-command load; null until then, which the grid shows as a dash rather
    // than as zero — "no backlog" and "not measured" must not look the same.
    private long? _undeliveredCommands; public long? UndeliveredCommands { get => _undeliveredCommands; set { Set(ref _undeliveredCommands, value); Raise(nameof(IsWarning)); } }
    private long? _deliveredCommandsInDistDb; public long? DeliveredCommandsInDistDb { get => _deliveredCommandsInDistDb; set => Set(ref _deliveredCommandsInDistDb, value); }

    // The agent's own SQL Server Agent job, when msdb was readable. A disabled job explains a subscription that
    // never moves far better than any latency number does.
    private bool? _jobEnabled; public bool? JobEnabled { get => _jobEnabled; set { Set(ref _jobEnabled, value); Raise(nameof(IsWarning)); } }
    private bool? _jobRunning; public bool? JobRunning { get => _jobRunning; set => Set(ref _jobRunning, value); }
    private string _jobName; public string JobName { get => _jobName; set => Set(ref _jobName, value); }

    /// <summary>Distribution retention for this publication, used to judge how close the subscription is to expiring.</summary>
    public double? RetentionHours { get; set; }

    internal ReplThresholds Thresholds { get; set; }

    public string RunStatusText => ReplValueParser.Describe(RunStatus);

    /// <summary>Publisher → subscriber, the number an application actually experiences.</summary>
    public double? TotalLatencySeconds =>
        DistributionLatencySeconds == null && LogReaderLatencySeconds == null
            ? null
            : DistributionLatencySeconds.GetValueOrDefault() + LogReaderLatencySeconds.GetValueOrDefault();

    public double? HoursSinceActivity => LastActivity == null ? null : (DateTime.Now - LastActivity.Value).TotalHours;

    /// <summary>
    /// How much of the distribution retention window has been used up since the last activity. Past 1.0 the
    /// expiry job deactivates the subscription and it has to be reinitialized, so this is the number to watch on
    /// a subscription that has been down for a while.
    /// </summary>
    public double? RetentionUsedFraction =>
        RetentionHours.GetValueOrDefault() > 0 && HoursSinceActivity != null ? HoursSinceActivity / RetentionHours : null;

    public bool IsUnhealthy => RunStatus == ReplRunStatus.Failed
                            || string.Equals(Status, "Inactive", StringComparison.OrdinalIgnoreCase)
                            || TotalLatencySeconds.GetValueOrDefault() > (Thresholds?.LatencyCriticalSeconds ?? double.MaxValue)
                            || RetentionUsedFraction.GetValueOrDefault() >= 1d;

    public bool IsWarning
    {
        get
        {
            if (IsUnhealthy) return false;

            var thresholds = Thresholds ?? new ReplThresholds();
            if (RunStatus == ReplRunStatus.Retrying) return true;
            if (JobEnabled == false) return true;
            if (TotalLatencySeconds.GetValueOrDefault() > thresholds.LatencyWarningSeconds) return true;
            if (RetentionUsedFraction.GetValueOrDefault() > thresholds.ExpiryWarningFraction) return true;
            if (UndeliveredCommands.GetValueOrDefault() > thresholds.PendingCommandWarning) return true;

            return false;
        }
    }

    /// <summary>Rolling latency samples backing the trend sparkline. Owned by <see cref="ReplHistory"/>.</summary>
    public IReadOnlyList<double> LatencyHistory { get; internal set; }

    internal void RaiseHistoryChanged() => Raise(nameof(LatencyHistory));
    internal void RaiseThresholdChanged() { Raise(nameof(IsUnhealthy)); Raise(nameof(IsWarning)); }
}

/// <summary>
/// One replication agent of any type, from its <c>MS*_agents</c> table joined to its latest history row.
/// The merge counters are only populated for merge agents and show as dashes elsewhere.
/// </summary>
internal sealed class ReplAgentRow : ReplRowBase
{
    public string Key => $"{AgentType}|{AgentId}";

    public ReplAgentType AgentType { get; set; }
    public int AgentId { get; set; }
    public string Name { get; set; }
    public string Publisher { get; set; }
    public string PublisherDb { get; set; }
    public string Publication { get; set; }
    public string Subscriber { get; set; }
    public string SubscriberDb { get; set; }
    public Guid? JobId { get; set; }

    public string AgentTypeText
    {
        get
        {
            switch (AgentType)
            {
                case ReplAgentType.LogReader: return "Log Reader";
                case ReplAgentType.Distribution: return "Distribution";
                case ReplAgentType.Snapshot: return "Snapshot";
                case ReplAgentType.Merge: return "Merge";
                default: return "Queue Reader";
            }
        }
    }

    private ReplRunStatus _runStatus; public ReplRunStatus RunStatus { get => _runStatus; set { Set(ref _runStatus, value); Raise(nameof(RunStatusText)); Raise(nameof(IsUnhealthy)); Raise(nameof(IsWarning)); } }
    private DateTime? _startTime; public DateTime? StartTime { get => _startTime; set => Set(ref _startTime, value); }
    private DateTime? _lastActivity; public DateTime? LastActivity { get => _lastActivity; set => Set(ref _lastActivity, value); }
    private long? _durationSeconds; public long? DurationSeconds { get => _durationSeconds; set => Set(ref _durationSeconds, value); }
    private string _comments; public string Comments { get => _comments; set => Set(ref _comments, value); }
    private string _lastError; public string LastError { get => _lastError; set { Set(ref _lastError, value); Raise(nameof(IsUnhealthy)); } }
    private double? _latencySeconds; public double? LatencySeconds { get => _latencySeconds; set => Set(ref _latencySeconds, value); }
    private double? _deliveryRate; public double? DeliveryRate { get => _deliveryRate; set => Set(ref _deliveryRate, value); }
    private long? _deliveredTransactions; public long? DeliveredTransactions { get => _deliveredTransactions; set => Set(ref _deliveredTransactions, value); }
    private long? _deliveredCommands; public long? DeliveredCommands { get => _deliveredCommands; set => Set(ref _deliveredCommands, value); }

    // Merge only.
    private long? _uploadedChanges; public long? UploadedChanges { get => _uploadedChanges; set => Set(ref _uploadedChanges, value); }
    private long? _downloadedChanges; public long? DownloadedChanges { get => _downloadedChanges; set => Set(ref _downloadedChanges, value); }
    private long? _conflicts; public long? Conflicts { get => _conflicts; set { Set(ref _conflicts, value); Raise(nameof(IsWarning)); } }

    private bool? _jobEnabled; public bool? JobEnabled { get => _jobEnabled; set { Set(ref _jobEnabled, value); Raise(nameof(IsWarning)); } }
    private bool? _jobRunning; public bool? JobRunning { get => _jobRunning; set => Set(ref _jobRunning, value); }
    private string _jobName; public string JobName { get => _jobName; set => Set(ref _jobName, value); }

    public string RunStatusText => ReplValueParser.Describe(RunStatus);

    public bool IsUnhealthy => RunStatus == ReplRunStatus.Failed;
    public bool IsWarning => !IsUnhealthy && (RunStatus == ReplRunStatus.Retrying || JobEnabled == false || Conflicts.GetValueOrDefault() > 0);
}

/// <summary>
/// A published database as seen from the publisher itself: whether the log can be truncated, how full it is,
/// and what sp_replcounters says about the backlog.
///
/// log_reuse_wait_desc = REPLICATION is the reason this tab exists. It is the symptom of stalled replication
/// that costs you the server — the log grows until the disk fills — and nothing in the distribution database
/// mentions it.
/// </summary>
internal sealed class ReplPublisherDatabaseRow : ReplRowBase
{
    public string Key => DatabaseName ?? "";
    public string DatabaseName { get; set; }

    private bool _isPublished; public bool IsPublished { get => _isPublished; set => Set(ref _isPublished, value); }
    private bool _isMergePublished; public bool IsMergePublished { get => _isMergePublished; set => Set(ref _isMergePublished, value); }
    private bool _isSubscribed; public bool IsSubscribed { get => _isSubscribed; set => Set(ref _isSubscribed, value); }
    private bool _isSyncWithBackup; public bool IsSyncWithBackup { get => _isSyncWithBackup; set => Set(ref _isSyncWithBackup, value); }
    private string _recoveryModel; public string RecoveryModel { get => _recoveryModel; set => Set(ref _recoveryModel, value); }

    private string _logReuseWait; public string LogReuseWait { get => _logReuseWait; set { Set(ref _logReuseWait, value); Raise(nameof(IsUnhealthy)); Raise(nameof(IsWarning)); } }
    private double? _logPercentUsed; public double? LogPercentUsed { get => _logPercentUsed; set { Set(ref _logPercentUsed, value); Raise(nameof(IsUnhealthy)); Raise(nameof(IsWarning)); } }
    private long? _logSizeKb; public long? LogSizeKb { get => _logSizeKb; set => Set(ref _logSizeKb, value); }

    // From sp_replcounters, which is publisher-side and needs elevated rights; null when it could not be read.
    private long? _replicatedTransactions; public long? ReplicatedTransactions { get => _replicatedTransactions; set => Set(ref _replicatedTransactions, value); }
    private double? _replicationRate; public double? ReplicationRate { get => _replicationRate; set => Set(ref _replicationRate, value); }
    private double? _replicationLatencySeconds; public double? ReplicationLatencySeconds { get => _replicationLatencySeconds; set { Set(ref _replicationLatencySeconds, value); Raise(nameof(IsWarning)); } }

    internal ReplThresholds Thresholds { get; set; }

    /// <summary>Log pinned by replication and nearly full: this is how a stalled log reader takes an instance down.</summary>
    public bool IsUnhealthy => IsLogHeldByReplication && LogPercentUsed.GetValueOrDefault() >= 80d;

    public bool IsWarning => !IsUnhealthy
                          && (IsLogHeldByReplication
                           || ReplicationLatencySeconds.GetValueOrDefault() > (Thresholds?.LatencyWarningSeconds ?? double.MaxValue));

    public bool IsLogHeldByReplication => string.Equals(LogReuseWait, "REPLICATION", StringComparison.OrdinalIgnoreCase);

    internal void RaiseThresholdChanged() { Raise(nameof(IsUnhealthy)); Raise(nameof(IsWarning)); }
}

/// <summary>
/// A subscription as the *subscriber* records it, from that database's own MSreplication_subscriptions table.
/// Worth reading separately because a pull subscription's subscriber may be the only place its progress is
/// visible, and because it is what the subscriber believes regardless of what the distributor thinks.
/// </summary>
internal sealed class ReplSubscriberDatabaseRow : ReplRowBase
{
    public string Key => $"{SubscriberDb}|{Publisher}|{PublisherDb}|{Publication}";

    public string SubscriberDb { get; set; }
    public string Publisher { get; set; }
    public string PublisherDb { get; set; }
    public string Publication { get; set; }

    private string _subscriptionType; public string SubscriptionType { get => _subscriptionType; set => Set(ref _subscriptionType, value); }
    private DateTime? _lastApplied; public DateTime? LastApplied { get => _lastApplied; set { Set(ref _lastApplied, value); Raise(nameof(HoursSinceApplied)); } }
    private string _transactionTimestamp; public string TransactionTimestamp { get => _transactionTimestamp; set => Set(ref _transactionTimestamp, value); }
    private string _description; public string Description { get => _description; set => Set(ref _description, value); }

    public double? HoursSinceApplied => LastApplied == null ? null : (DateTime.Now - LastApplied.Value).TotalHours;
}

/// <summary>One row of MSrepl_errors — the error text the history tables only reference by id.</summary>
internal sealed class ReplErrorRow
{
    /// <summary>
    /// MSrepl_errors.id — not unique. One failure writes several rows under the same id: the agent's own message
    /// and then each layer beneath it (the subscriber's SQL error, the data source, …). The history tables point
    /// at this id, so it is how the detail pane gathers the whole chain.
    /// </summary>
    public int? Id { get; set; }
    public DateTime? Time { get; set; }
    /// <summary>MSrepl_errors.error_code is sysname, not int: "MSSQL_REPL20011", "08001", or a bare number.</summary>
    public string ErrorCode { get; set; }
    public string ErrorText { get; set; }
    public string SourceName { get; set; }
    public int? SourceTypeId { get; set; }
    public int? ErrorTypeId { get; set; }
    public string XactSeqno { get; set; }
    public int? CommandId { get; set; }
    public int? SessionId { get; set; }

    /// <summary>Everything in MSrepl_errors is an error; the tint is unconditional.</summary>
    public bool IsError => true;
}

/// <summary>
/// What the detail pane was opened on: an agent (from the Overview, Subscriptions or Agents grids) or a single
/// error id (from the Errors tab, whose rows do not name their agent — the agent is looked up from history).
/// </summary>
internal sealed class ReplDetailTarget
{
    public ReplAgentType AgentType { get; set; } = ReplAgentType.Distribution;
    public int? AgentId { get; set; }
    public int? ErrorId { get; set; }

    public string Title { get; set; }
    public string JobName { get; set; }

    /// <summary>The error the grid row already showed, used until the full chain arrives (and if it never does).</summary>
    public string KnownError { get; set; }

    public string Key => $"{AgentType}|{AgentId}|{ErrorId}";
}

/// <summary>One row of an agent's history table, newest first in the detail pane.</summary>
internal sealed class ReplHistoryRow
{
    public DateTime? Time { get; set; }
    public ReplRunStatus RunStatus { get; set; }
    public string Comments { get; set; }
    public int? ErrorId { get; set; }
    public long? DeliveredTransactions { get; set; }
    public long? DeliveredCommands { get; set; }

    public string RunStatusText => ReplValueParser.Describe(RunStatus);
    public bool HasError => ErrorId.GetValueOrDefault() > 0;

    public bool IsUnhealthy => RunStatus == ReplRunStatus.Failed || HasError;
    public bool IsWarning => !IsUnhealthy && RunStatus == ReplRunStatus.Retrying;
}

/// <summary>
/// The command a distribution agent stopped on, decoded by <c>sp_browsereplcmds</c> from the transaction sequence
/// number and command id that MSrepl_errors recorded. The command text carries the row's values, which is usually
/// what turns "conversion … out-of-range" into a fixable fact.
/// </summary>
internal sealed class ReplFailingCommand
{
    public string XactSeqno { get; set; }
    public int? CommandId { get; set; }
    public int? ArticleId { get; set; }
    public string Article { get; set; }
    public string SourceObject { get; set; }
    public string DestinationObject { get; set; }
    public string CommandText { get; set; }

    /// <summary>How many commands the failing transaction holds — one bad row can sit inside a large batch.</summary>
    public int CommandsInTransaction { get; set; }

    /// <summary>Set when the command could not be found or read, in place of the text.</summary>
    public string Problem { get; set; }
}

/// <summary>Everything the detail pane shows for one agent. Assembled off the UI thread.</summary>
internal sealed class ReplAgentDetail
{
    public int? AgentId { get; set; }
    public string AgentName { get; set; }
    public int? PublisherId { get; set; }
    public int? PublisherDatabaseId { get; set; }
    public string Publisher { get; set; }
    public string PublisherDb { get; set; }
    public string Publication { get; set; }
    public string Subscriber { get; set; }
    public string SubscriberDb { get; set; }

    public List<ReplHistoryRow> History { get; } = new List<ReplHistoryRow>();

    /// <summary>MSrepl_errors rows for the error ids in <see cref="History"/> (or the one id asked for), newest id first.</summary>
    public List<ReplErrorRow> Errors { get; } = new List<ReplErrorRow>();

    public List<string> Warnings { get; } = new List<string>();
}

/// <summary>
/// A tracer token and its two measured hops. This is the only end-to-end measurement in replication that is not
/// an estimate: the token is a real transaction, written at the publisher and timed as it lands.
/// </summary>
internal sealed class ReplTracerRow
{
    public int TracerId { get; set; }
    public string Publisher { get; set; }
    public string PublisherDb { get; set; }
    public string Publication { get; set; }
    public string Subscriber { get; set; }
    public string SubscriberDb { get; set; }

    public DateTime? PublisherCommit { get; set; }
    public DateTime? DistributorCommit { get; set; }
    public DateTime? SubscriberCommit { get; set; }

    /// <summary>Publisher → distributor: the log reader's hop.</summary>
    public double? PublisherToDistributorSeconds => Span(PublisherCommit, DistributorCommit);

    /// <summary>Distributor → subscriber: the distribution agent's hop.</summary>
    public double? DistributorToSubscriberSeconds => Span(DistributorCommit, SubscriberCommit);

    public double? TotalSeconds => Span(PublisherCommit, SubscriberCommit);

    /// <summary>A token with no subscriber commit has not arrived — either still in flight, or it never will.</summary>
    public bool IsWarning => PublisherCommit != null && SubscriberCommit == null;

    private static double? Span(DateTime? from, DateTime? to)
    {
        if (from == null || to == null) return null;
        double seconds = (to.Value - from.Value).TotalSeconds;
        return seconds < 0 ? (double?)null : seconds;
    }
}

/// <summary>How badly a diagnostic finding wants attention. Ordered so a plain sort puts the worst first.</summary>
internal enum ReplIssueSeverity
{
    Critical = 0,
    Warning = 1,
    Information = 2
}

/// <summary>One finding from <see cref="ReplDiagnostics"/>. Same shape and purpose as the Always On equivalent.</summary>
internal sealed class ReplIssueRow
{
    public ReplIssueSeverity Severity { get; set; }
    public string Area { get; set; }
    public string Subject { get; set; }
    public string Detail { get; set; }
    public string Recommendation { get; set; }

    public string SeverityText => Severity == ReplIssueSeverity.Critical ? "CRITICAL" : Severity == ReplIssueSeverity.Warning ? "WARNING" : "INFO";

    public bool IsUnhealthy => Severity == ReplIssueSeverity.Critical;
    public bool IsWarning => Severity == ReplIssueSeverity.Warning;
}

/// <summary>Everything one poll cycle collected. Assembled off the UI thread, then merged into the grids.</summary>
internal sealed class ReplSnapshot
{
    public List<ReplPublicationRow> Publications { get; } = new List<ReplPublicationRow>();
    public List<ReplSubscriptionRow> Subscriptions { get; } = new List<ReplSubscriptionRow>();
    public List<ReplAgentRow> Agents { get; } = new List<ReplAgentRow>();
    public List<ReplPublisherDatabaseRow> PublisherDatabases { get; } = new List<ReplPublisherDatabaseRow>();
    public List<ReplSubscriberDatabaseRow> SubscriberDatabases { get; } = new List<ReplSubscriberDatabaseRow>();
    public List<ReplIssueRow> Issues { get; } = new List<ReplIssueRow>();

    public ReplRole Role { get; set; } = new ReplRole();

    public string ServerName { get; set; }

    /// <summary>The login this poll ran as, per <c>SUSER_SNAME()</c>. Shown beside the server in the header.</summary>
    public string LoginName { get; set; }
    public DateTime CollectedAtLocal { get; set; }
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// How many sections this poll read and how many of those failed, reported next to the timing. This dashboard
    /// reads three different databases with different rights needed for each, so "8 sections in 212 ms" says
    /// considerably more about an empty tab than the duration alone.
    /// </summary>
    public int SectionsRead { get; set; }

    public int SectionsFailed { get; set; }

    /// <summary>Per-section failures, so an unavailable table costs one tab rather than the dashboard.</summary>
    public List<string> Warnings { get; } = new List<string>();

    /// <summary>Set when replication is not configured here — the UI shows this instead of empty grids.</summary>
    public string UnavailableReason { get; set; }
    public bool IsAvailable => UnavailableReason == null;
}
