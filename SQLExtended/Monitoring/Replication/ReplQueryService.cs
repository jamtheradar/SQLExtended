using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace SQLExtended.Monitoring.Replication;

/// <summary>
/// Reads replication state. Everything here runs on a background thread against connections derived from the
/// window's pinned connection.
///
/// Where the data lives decides the shape of this class. Unlike the Always On and Agent Jobs dashboards, which
/// each read one database, replication is spread across three:
///  * the <b>distribution database</b> holds publications, subscriptions, every agent and its history — the bulk
///    of the dashboard, and only readable when the connected instance is the distributor;
///  * <b>master</b> on the publisher holds the one thing the distributor cannot tell you, which is whether the
///    log can be truncated (log_reuse_wait_desc = REPLICATION) and how full it is;
///  * each <b>subscriber database</b> holds its own MSreplication_subscriptions, the only record of a pull
///    subscription's progress that does not depend on the distributor being reachable.
///
/// So a poll opens one connection per database it needs, and each section is collected in its own try/catch that
/// records a warning rather than throwing — a login with rights on master but not on the distribution database
/// should still get the publisher tab, not an error.
///
/// Deliberate choices worth keeping:
///  * The agents' latest history row is fetched with <c>OUTER APPLY … TOP (1) … ORDER BY time DESC</c> per agent
///    rather than a window function over the whole history table. MSdistribution_history is the largest table in
///    a busy distribution database; a ranked scan of all of it on a five-second timer is not acceptable, and
///    agent counts are small enough that a seek per agent is cheap.
///  * Latency is converted to seconds on the way in. The history tables report milliseconds, the tracer tokens
///    report datetimes, and sp_replcounters reports seconds — normalising once here means nothing downstream has
///    to remember which.
///  * The undelivered-command count and the error list load <b>on demand</b>. MSdistribution_status counts rows
///    in MSrepl_commands, which on a backlogged distributor is exactly when it is most expensive and most
///    wanted; putting it on the refresh timer would make the dashboard part of the problem.
/// </summary>
internal static partial class ReplQueryService
{
    internal const int CommandTimeoutSeconds = 30;

    /// <summary>
    /// Normalises a harvested connection string for monitoring use, pointed at a named database. Replication
    /// forces a database per query rather than always using master, so the catalog is a parameter here.
    /// </summary>
    public static string BuildMonitorConnectionString(string baseConnectionString, string database)
    {
        var builder = new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = string.IsNullOrWhiteSpace(database) ? "master" : database,
            ApplicationName = "SQLExtended Replication Monitor",
            ConnectTimeout = 10
        };
        return builder.ConnectionString;
    }

    /// <param name="progress">Reports each section as it starts, for the status line. Null on the timer polls.</param>
    /// <param name="onOverviewReady">
    /// Awaited once the distributor-side sections the Overview is built from have been read, before the publisher
    /// and subscriber databases are visited — see <see cref="MonitorPlan"/>. Those later sections each open a
    /// connection of their own, and the subscriber read opens one per subscribed database, so on a wide topology
    /// they are much the slowest part of a poll and none of them appears on the tab the window opens on.
    /// </param>
    public static async Task<ReplSnapshot> CollectAsync(string masterConnectionString, ReplCapabilities caps, ReplThresholds thresholds,
                                                        IProgress<MonitorStep> progress, Func<ReplSnapshot, Task> onOverviewReady, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var snapshot = new ReplSnapshot { ServerName = caps.ServerName, LoginName = caps.LoginName, CollectedAtLocal = DateTime.Now };

        // The rows hold a reference to these for their own tinting, so a null would surface as a crash in the
        // grid rather than here.
        thresholds = thresholds ?? new ReplThresholds();

        snapshot.Role.IsDistributor = caps.IsDistributor;
        snapshot.Role.IsPublisher = caps.IsPublisher;
        snapshot.Role.IsSubscriber = caps.IsSubscriber;
        snapshot.Role.DistributionDatabase = caps.DistributionDatabase;

        if (!caps.HasAnyRole)
        {
            snapshot.UnavailableReason =
                "Replication is not configured on this instance — no database is published, subscribed or acting as a distributor. "
                + "If replication runs elsewhere in this topology, connect a query window to the distributor (or the publisher) and refresh.";
            return snapshot;
        }

        string distConnection = caps.IsDistributor ? BuildMonitorConnectionString(masterConnectionString, caps.DistributionDatabase) : null;

        // Marked primary in the order the Overview needs them: who the distributor is, then the three
        // distribution-database reads the tab's cards, attention list and counts are drawn from.
        var plan = new MonitorPlan(progress, snapshot.Warnings.Add)
            .Add("sp_helpdistributor", () => ReadDistributorInfoAsync(masterConnectionString, snapshot, ct), primary: true)
            .AddIf(caps.IsDistributor && caps.HasPublications, "publications", () => ReadPublicationsAsync(distConnection, caps, snapshot, ct), primary: true)
            .AddIf(caps.IsDistributor, "agents", () => ReadAgentsAsync(distConnection, caps, snapshot, ct), primary: true)
            .AddIf(caps.IsDistributor && caps.HasSubscriptions, "subscriptions", () => ReadSubscriptionsAsync(distConnection, caps, thresholds, snapshot, ct), primary: true)

            // Publisher- and subscriber-side sections read master and the subscriber databases; they work whether
            // or not this instance is the distributor, which is the whole point of keeping them separate. Order
            // among themselves is unchanged — sp_replcounters enriches the publisher database rows read above it.
            .Add("publisher databases", () => ReadPublisherDatabasesAsync(masterConnectionString, thresholds, snapshot, ct))
            .AddIf(caps.IsPublisher, "sp_replcounters", () => ReadReplCountersAsync(masterConnectionString, snapshot, ct))
            .AddIf(caps.IsSubscriber, "subscriber databases", () => ReadSubscriberDatabasesAsync(masterConnectionString, snapshot, ct))
            .AddIf(caps.IsDistributor && caps.CanReadJobs, "agent jobs", () => ReadAgentJobsAsync(distConnection, caps, snapshot, ct));

        await plan.RunAsync(async () =>
        {
            LinkLogReaderLatency(snapshot);
            if (onOverviewReady != null) await onOverviewReady(snapshot).ConfigureAwait(false);
        }).ConfigureAwait(false);

        snapshot.SectionsRead = plan.Ran;
        snapshot.SectionsFailed = plan.Failed;

        // The log reader's hop and the distribution agent's hop are collected separately; the total only exists
        // once they are matched up by publisher database. Idempotent, and done again here for the path that had
        // no early paint to do it for.
        LinkLogReaderLatency(snapshot);

        ReplDiagnostics.Evaluate(snapshot, caps, thresholds);

        snapshot.Duration = DateTime.UtcNow - started;
        return snapshot;
    }

    /// <summary>
    /// Copies each publisher database's log reader latency onto the subscriptions that depend on it, so the
    /// Subscriptions tab can show a publisher-to-subscriber total. Matched on publisher + publisher database
    /// because that is the granularity the log reader works at — one agent per published database, not per
    /// publication or per subscription.
    /// </summary>
    private static void LinkLogReaderLatency(ReplSnapshot snapshot)
    {
        var byDatabase = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);

        foreach (var agent in snapshot.Agents)
        {
            if (agent.AgentType != ReplAgentType.LogReader) continue;
            byDatabase[$"{agent.Publisher}|{agent.PublisherDb}"] = agent.LatencySeconds;
        }

        foreach (var subscription in snapshot.Subscriptions)
        {
            if (byDatabase.TryGetValue($"{subscription.Publisher}|{subscription.PublisherDb}", out var latency))
                subscription.LogReaderLatencySeconds = latency;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Distributor properties
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// sp_helpdistributor names the distributor and its retention settings. It is used rather than reading
    /// MSdistributiondbs directly because on a *publisher* with a remote distributor this is the only thing that
    /// answers "who is my distributor" — and knowing that the distributor is another server is what tells the
    /// user why the subscription grid is empty.
    /// </summary>
    internal const string DistributorInfoSql = "EXEC master.sys.sp_helpdistributor;";

    private static async Task ReadDistributorInfoAsync(string masterConnectionString, ReplSnapshot snapshot, CancellationToken ct)
    {
        using (var conn = SqlConnectionFactory.Create(masterConnectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using (var cmd = new SqlCommand(DistributorInfoSql, conn) { CommandTimeout = CommandTimeoutSeconds })
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return;

                // The column set has varied across releases, so every column is looked up by name and tolerated
                // as missing rather than read positionally.
                snapshot.Role.DistributorName = StrIfPresent(reader, "distributor");
                if (string.IsNullOrEmpty(snapshot.Role.DistributionDatabase))
                    snapshot.Role.DistributionDatabase = StrIfPresent(reader, "distribution database");

                // Retention values are in hours for distribution and hours for history.
                snapshot.Role.MinDistributionRetentionHours = DoubleIfPresent(reader, "min distrib retention");
                snapshot.Role.MaxDistributionRetentionHours = DoubleIfPresent(reader, "max distrib retention");
                snapshot.Role.HistoryRetentionHours = DoubleIfPresent(reader, "history retention");
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Publications
    // ---------------------------------------------------------------------------------------------

    // publisher_id is a linked-server id, so the readable name comes from sys.servers; the id is kept as the
    // fallback because a publisher that has been dropped from sys.servers still has rows here.
    internal static string PublicationsSql(ReplCapabilities caps) => $@"
SELECT
    ISNULL(srv.name, CONVERT(nvarchar(128), pub.publisher_id)) AS publisher,
    pub.publisher_db,
    pub.publication,
    pub.publication_type,
    pub.immediate_sync,
    pub.allow_push,
    pub.allow_pull,
    pub.allow_anonymous,
    pub.independent_agent,
    pub.retention,
    {ReplCapabilities.Column(caps.HasRetentionPeriodUnit, "pub.retention_period_unit", "NULL", "retention_period_unit")},
    pub.description,
    (SELECT COUNT(*) FROM dbo.MSarticles AS a WHERE a.publication_id = pub.publication_id) AS article_count,
    (SELECT COUNT(DISTINCT CONVERT(nvarchar(30), s.subscriber_id) + N'|' + ISNULL(s.subscriber_db, N''))
       FROM dbo.MSsubscriptions AS s WHERE s.publication_id = pub.publication_id) AS subscription_count,
    snap.runstatus AS snapshot_runstatus,
    snap.time      AS snapshot_time
FROM dbo.MSpublications AS pub
LEFT JOIN sys.servers AS srv ON srv.server_id = pub.publisher_id
{(caps.HasSnapshotAgents ? @"OUTER APPLY (
    -- The publication's last snapshot run, so ""the snapshot never succeeded"" is visible next to the
    -- publication it belongs to rather than only on the Agents tab.
    SELECT TOP (1) h.runstatus, h.time
    FROM dbo.MSsnapshot_agents AS sa
    JOIN dbo.MSsnapshot_history AS h ON h.agent_id = sa.id
    WHERE sa.publisher_id = pub.publisher_id AND sa.publisher_db = pub.publisher_db AND sa.publication = pub.publication
    ORDER BY h.time DESC
) AS snap" : @"CROSS APPLY (SELECT CONVERT(int, NULL) AS runstatus, CONVERT(datetime, NULL) AS time) AS snap")}
ORDER BY publisher, pub.publisher_db, pub.publication;";

    private static async Task ReadPublicationsAsync(string connectionString, ReplCapabilities caps, ReplSnapshot snapshot, CancellationToken ct)
    {
        using (var conn = SqlConnectionFactory.Create(connectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using (var cmd = new SqlCommand(PublicationsSql(caps), conn) { CommandTimeout = CommandTimeoutSeconds })
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    snapshot.Publications.Add(new ReplPublicationRow
                    {
                        Publisher = Str(reader, "publisher"),
                        PublisherDb = Str(reader, "publisher_db"),
                        Publication = Str(reader, "publication"),
                        PublicationType = ReplValueParser.DescribePublicationType(Int(reader, "publication_type")),
                        ImmediateSync = Bool(reader, "immediate_sync"),
                        AllowPush = Bool(reader, "allow_push"),
                        AllowPull = Bool(reader, "allow_pull"),
                        AllowAnonymous = Bool(reader, "allow_anonymous"),
                        IndependentAgent = Bool(reader, "independent_agent"),
                        RetentionHours = ReplValueParser.RetentionHours(Int(reader, "retention"), Int(reader, "retention_period_unit")),
                        Description = Str(reader, "description"),
                        ArticleCount = Int(reader, "article_count") ?? 0,
                        SubscriptionCount = Int(reader, "subscription_count") ?? 0,
                        SnapshotStatus = ReplValueParser.ToRunStatus(Int(reader, "snapshot_runstatus")),
                        SnapshotTime = Date(reader, "snapshot_time")
                    });
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Subscriptions
    // ---------------------------------------------------------------------------------------------

    // MSsubscriptions holds one row per article per subscription, so it is grouped down to one row per
    // (publication, subscriber, subscriber database) first — the grain everyone actually thinks in — and the
    // agent's latest history row is applied to that.
    //
    // The aggregates over columns that are constant within a group (status, subscription_type, agent_id) are
    // there to satisfy GROUP BY, not to combine anything: MAX of one distinct value is that value.
    internal static string SubscriptionsSql(ReplCapabilities caps)
    {
        var history = new List<string> { "runstatus", "start_time", "time", "comments", "error_id", "delivered_transactions", "delivered_commands" };
        if (caps.HasCurrentDeliveryLatency) history.Add("current_delivery_latency");
        history.Add("delivery_latency");
        if (caps.HasCurrentDeliveryRate) history.Add("current_delivery_rate");
        history.Add("delivery_rate");

        return $@"
WITH subs AS (
    SELECT
        p.publisher_id,
        p.publisher_db,
        p.publication,
        p.publication_type,
        p.retention,
        {ReplCapabilities.Column(caps.HasRetentionPeriodUnit, "p.retention_period_unit", "NULL", "retention_period_unit")},
        s.subscriber_id,
        s.subscriber_db,
        MAX(s.subscription_type) AS subscription_type,
        MAX(s.status)            AS status,
        MAX(s.sync_type)         AS sync_type,
        MAX(s.agent_id)          AS agent_id,
        MAX(CONVERT(varchar(42), s.subscription_seqno, 1)) AS subscription_seqno,
        COUNT(*) AS article_count
    FROM dbo.MSsubscriptions AS s
    JOIN dbo.MSpublications AS p ON p.publication_id = s.publication_id
    GROUP BY p.publisher_id, p.publisher_db, p.publication, p.publication_type, p.retention,
             {(caps.HasRetentionPeriodUnit ? "p.retention_period_unit," : "")} s.subscriber_id, s.subscriber_db
)
SELECT
    ISNULL(psrv.name, CONVERT(nvarchar(128), subs.publisher_id)) AS publisher,
    subs.publisher_db,
    subs.publication,
    subs.publication_type,
    ISNULL(ssrv.name, CONVERT(nvarchar(128), subs.subscriber_id)) AS subscriber,
    subs.subscriber_db,
    subs.subscription_type,
    subs.status,
    subs.sync_type,
    subs.agent_id,
    subs.subscription_seqno,
    subs.article_count,
    subs.retention,
    subs.retention_period_unit,
    da.job_id,
    dh.runstatus,
    dh.start_time,
    dh.time              AS last_activity,
    dh.comments,
    dh.delivered_transactions,
    dh.delivered_commands,
    {ReplCapabilities.Column(caps.HasCurrentDeliveryLatency, "dh.current_delivery_latency", "dh.delivery_latency", "delivery_latency_ms")},
    {ReplCapabilities.Column(caps.HasCurrentDeliveryRate, "dh.current_delivery_rate", "dh.delivery_rate", "delivery_rate")},
    err.error_text
FROM subs
LEFT JOIN sys.servers AS psrv ON psrv.server_id = subs.publisher_id
LEFT JOIN sys.servers AS ssrv ON ssrv.server_id = subs.subscriber_id
LEFT JOIN dbo.MSdistribution_agents AS da ON da.id = subs.agent_id
OUTER APPLY (
    -- Latest history row for this agent. TOP (1) per agent rather than a ranked scan of the whole table: on a
    -- busy distributor MSdistribution_history is the largest table there is.
    SELECT TOP (1) {HistorySelect(caps, DistributionHistory, "h", history.ToArray())}
    FROM dbo.MSdistribution_history AS h
    WHERE h.agent_id = subs.agent_id
    ORDER BY h.time DESC
) AS dh
OUTER APPLY (
    -- Only an error id is stored on the history row; the text lives in MSrepl_errors. A server whose history
    -- table has no error_id at all lands here with a NULL to compare against, so no row and no error text.
    SELECT TOP (1) e.error_text FROM dbo.MSrepl_errors AS e WHERE e.id = dh.error_id
) AS err
ORDER BY publisher, subs.publisher_db, subs.publication, subscriber, subs.subscriber_db;";
    }

    private static async Task ReadSubscriptionsAsync(string connectionString, ReplCapabilities caps, ReplThresholds thresholds, ReplSnapshot snapshot, CancellationToken ct)
    {
        using (var conn = SqlConnectionFactory.Create(connectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using (var cmd = new SqlCommand(SubscriptionsSql(caps), conn) { CommandTimeout = CommandTimeoutSeconds })
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    snapshot.Subscriptions.Add(new ReplSubscriptionRow
                    {
                        Publisher = Str(reader, "publisher"),
                        PublisherDb = Str(reader, "publisher_db"),
                        Publication = Str(reader, "publication"),
                        PublicationType = ReplValueParser.DescribePublicationType(Int(reader, "publication_type")),
                        Subscriber = Str(reader, "subscriber"),
                        SubscriberDb = Str(reader, "subscriber_db"),
                        SubscriptionType = ReplValueParser.DescribeSubscriptionType(Int(reader, "subscription_type")),
                        Status = ReplValueParser.DescribeSubscriptionStatus(Int(reader, "status")),
                        SyncType = ReplValueParser.DescribeSyncType(Int(reader, "sync_type")),
                        AgentId = Int(reader, "agent_id"),
                        SubscriptionSeqno = Str(reader, "subscription_seqno"),
                        ArticleCount = Int(reader, "article_count") ?? 0,
                        RetentionHours = ReplValueParser.RetentionHours(Int(reader, "retention"), Int(reader, "retention_period_unit")),
                        RunStatus = ReplValueParser.ToRunStatus(Int(reader, "runstatus")),
                        LastStart = Date(reader, "start_time"),
                        LastActivity = Date(reader, "last_activity"),
                        LastComment = Str(reader, "comments"),
                        LastError = Str(reader, "error_text"),
                        DeliveredTransactions = Long(reader, "delivered_transactions"),
                        DeliveredCommands = Long(reader, "delivered_commands"),
                        DeliveryRate = Double(reader, "delivery_rate"),
                        DistributionLatencySeconds = MsToSeconds(Long(reader, "delivery_latency_ms")),
                        Thresholds = thresholds
                    });
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Agents — four tables, one list
    // ---------------------------------------------------------------------------------------------

    private const string LogReaderHistory = "MSlogreader_history";
    private const string DistributionHistory = "MSdistribution_history";
    private const string SnapshotHistory = "MSsnapshot_history";
    private const string MergeSessions = "MSmerge_sessions";
    private const string MergeHistory = "MSmerge_history";

    /// <summary>
    /// One query per agent type present, each run as its own command by <see cref="ReadAgentsAsync"/>.
    ///
    /// They were one batch once. A batch binds as a whole, so a single column a release has dropped takes every
    /// agent type down with it rather than one — which is exactly what SQL Server 2025 did, emptying the tab with
    /// "Invalid column name 'comments'. Invalid column name 'error_id'.". Four small commands on one connection
    /// cost three extra round trips and turn that into one type's worth of warning.
    /// </summary>
    internal static List<(ReplAgentType Type, string Label, string Sql)> AgentQueries(ReplCapabilities caps)
    {
        var queries = new List<(ReplAgentType, string, string)>();
        if (caps.HasLogReaderAgents) queries.Add((ReplAgentType.LogReader, "log reader", LogReaderAgentsSql(caps)));
        if (caps.HasDistributionAgents) queries.Add((ReplAgentType.Distribution, "distribution", DistributionAgentsSql(caps)));
        if (caps.HasSnapshotAgents) queries.Add((ReplAgentType.Snapshot, "snapshot", SnapshotAgentsSql(caps)));
        if (caps.HasMergeAgents) queries.Add((ReplAgentType.Merge, "merge", MergeAgentsSql(caps)));
        return queries;
    }

    /// <summary>The same queries as one batch, for "Open as query" — which wants a script, not a poll.</summary>
    internal static string AgentsSql(ReplCapabilities caps)
    {
        var parts = new List<string>();
        foreach (var query in AgentQueries(caps)) parts.Add(query.Sql);
        return string.Join(Environment.NewLine, parts);
    }

    // Typed NULLs for the history columns a given release does not have, so the derived table always exposes the
    // full alias set and neither the outer SELECT nor the error OUTER APPLY has to know what was missing.
    private static readonly Dictionary<string, string> HistoryColumnTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["runstatus"] = "int",
        ["session_id"] = "int",
        ["start_time"] = "datetime",
        ["end_time"] = "datetime",
        ["time"] = "datetime",
        ["duration"] = "int",
        ["comments"] = "nvarchar(4000)",
        ["error_id"] = "int",
        ["delivery_latency"] = "int",
        ["current_delivery_latency"] = "int",
        ["delivery_rate"] = "float",
        ["current_delivery_rate"] = "float",
        ["delivered_transactions"] = "int",
        ["delivered_commands"] = "int",
        ["upload_inserts"] = "int",
        ["upload_updates"] = "int",
        ["upload_deletes"] = "int",
        ["upload_conflicts"] = "int",
        ["download_inserts"] = "int",
        ["download_updates"] = "int",
        ["download_deletes"] = "int",
        ["download_conflicts"] = "int"
    };

    /// <summary>
    /// A select list over one agent-history table, substituting <c>CONVERT(&lt;type&gt;, NULL) AS name</c> for
    /// any column the probe found to be absent. Aliases are bracketed because <c>time</c> reads as a type name.
    /// </summary>
    private static string HistorySelect(ReplCapabilities caps, string table, string alias, params string[] columns)
    {
        var parts = new List<string>(columns.Length);

        foreach (string column in columns)
            parts.Add(caps.HasColumn(table, column) ? $"{alias}.[{column}]" : $"CONVERT({HistoryColumnTypes[column]}, NULL) AS [{column}]");

        return string.Join(", ", parts);
    }

    // delivery_latency here is the publisher-to-distributor hop: how long a committed transaction took to reach
    // the distribution database. It is the first half of end-to-end latency.
    private static string LogReaderAgentsSql(ReplCapabilities caps) => $@"
SELECT
    la.id,
    la.name,
    ISNULL(srv.name, CONVERT(nvarchar(128), la.publisher_id)) AS publisher,
    la.publisher_db,
    la.publication,
    la.job_id,
    h.runstatus,
    h.start_time,
    h.time AS last_activity,
    h.duration,
    h.comments,
    h.delivery_latency,
    h.delivery_rate,
    h.delivered_transactions,
    h.delivered_commands,
    err.error_text
FROM dbo.MSlogreader_agents AS la
LEFT JOIN sys.servers AS srv ON srv.server_id = la.publisher_id
OUTER APPLY (
    SELECT TOP (1) {HistorySelect(caps, LogReaderHistory, "x", "runstatus", "start_time", "time", "duration", "comments", "error_id", "delivery_latency", "delivery_rate", "delivered_transactions", "delivered_commands")}
    FROM dbo.MSlogreader_history AS x WHERE x.agent_id = la.id ORDER BY x.time DESC
) AS h
OUTER APPLY (SELECT TOP (1) e.error_text FROM dbo.MSrepl_errors AS e WHERE e.id = h.error_id) AS err
ORDER BY publisher, la.publisher_db;";

    private static string DistributionAgentsSql(ReplCapabilities caps)
    {
        // current_delivery_* is the newer pair; the older delivery_* stays in the list as the fallback the outer
        // SELECT reaches for, and HistorySelect nulls out whichever of them this server turns out not to have.
        var history = new List<string> { "runstatus", "start_time", "time", "duration", "comments", "error_id" };
        if (caps.HasCurrentDeliveryLatency) history.Add("current_delivery_latency");
        history.Add("delivery_latency");
        if (caps.HasCurrentDeliveryRate) history.Add("current_delivery_rate");
        history.Add("delivery_rate");
        history.Add("delivered_transactions");
        history.Add("delivered_commands");

        return $@"
SELECT
    da.id,
    da.name,
    ISNULL(psrv.name, CONVERT(nvarchar(128), da.publisher_id)) AS publisher,
    da.publisher_db,
    da.publication,
    ISNULL(ssrv.name, CONVERT(nvarchar(128), da.subscriber_id)) AS subscriber,
    da.subscriber_db,
    da.job_id,
    h.runstatus,
    h.start_time,
    h.time AS last_activity,
    h.duration,
    h.comments,
    {ReplCapabilities.Column(caps.HasCurrentDeliveryLatency, "h.current_delivery_latency", "h.delivery_latency", "delivery_latency")},
    {ReplCapabilities.Column(caps.HasCurrentDeliveryRate, "h.current_delivery_rate", "h.delivery_rate", "delivery_rate")},
    h.delivered_transactions,
    h.delivered_commands,
    err.error_text
FROM dbo.MSdistribution_agents AS da
LEFT JOIN sys.servers AS psrv ON psrv.server_id = da.publisher_id
LEFT JOIN sys.servers AS ssrv ON ssrv.server_id = da.subscriber_id
OUTER APPLY (
    SELECT TOP (1) {HistorySelect(caps, DistributionHistory, "x", history.ToArray())}
    FROM dbo.MSdistribution_history AS x WHERE x.agent_id = da.id ORDER BY x.time DESC
) AS h
OUTER APPLY (SELECT TOP (1) e.error_text FROM dbo.MSrepl_errors AS e WHERE e.id = h.error_id) AS err
ORDER BY publisher, da.publisher_db, da.publication, subscriber;";
    }

    private static string SnapshotAgentsSql(ReplCapabilities caps) => $@"
SELECT
    sa.id,
    sa.name,
    ISNULL(srv.name, CONVERT(nvarchar(128), sa.publisher_id)) AS publisher,
    sa.publisher_db,
    sa.publication,
    sa.job_id,
    h.runstatus,
    h.start_time,
    h.time AS last_activity,
    h.duration,
    h.comments,
    h.delivery_rate,
    h.delivered_transactions,
    h.delivered_commands,
    err.error_text
FROM dbo.MSsnapshot_agents AS sa
LEFT JOIN sys.servers AS srv ON srv.server_id = sa.publisher_id
OUTER APPLY (
    SELECT TOP (1) {HistorySelect(caps, SnapshotHistory, "x", "runstatus", "start_time", "time", "duration", "comments", "error_id", "delivery_rate", "delivered_transactions", "delivered_commands")}
    FROM dbo.MSsnapshot_history AS x WHERE x.agent_id = sa.id ORDER BY x.time DESC
) AS h
OUTER APPLY (SELECT TOP (1) e.error_text FROM dbo.MSrepl_errors AS e WHERE e.id = h.error_id) AS err
ORDER BY publisher, sa.publisher_db, sa.publication;";

    // Merge agents report per-session totals rather than latency: what went up, what came down, and how many
    // rows collided. The conflict count is the finding — merge replication resolves conflicts silently.
    //
    // This is the one agent type whose history is two tables. MSmerge_sessions is a summary row per session, with
    // upload_*/download_* counts and neither a comment nor an error id; MSmerge_history is one row per message
    // for a session, and is where both of those live. So the session supplies the timings and totals, and two
    // further applies over its messages supply the text — the latest message for the comment, and the latest
    // message that actually carries an error id for the error, since a session that failed and then retried ends
    // on "Retrying..." and would otherwise report no error at all.
    private static string MergeAgentsSql(ReplCapabilities caps)
    {
        var history = new List<string> { "session_id", "runstatus", "start_time", "end_time", "duration", "delivery_rate" };
        if (caps.HasMergeSessionConflictCounts)
            history.AddRange(new[]
            {
                "upload_inserts", "upload_updates", "upload_deletes", "upload_conflicts",
                "download_inserts", "download_updates", "download_deletes", "download_conflicts"
            });

        // These two are gated on the columns rather than NULL-substituted: both applies key and order on
        // MSmerge_history's own columns, and a WHERE or ORDER BY still binds against the base table.
        bool canJoinHistory = caps.HasMergeHistory && caps.HasColumn(MergeHistory, "session_id") && caps.HasColumn(MergeHistory, "time");
        bool hasComments = canJoinHistory && caps.HasColumn(MergeHistory, "comments");
        bool hasErrorId = canJoinHistory && caps.HasColumn(MergeHistory, "error_id");

        return $@"
SELECT
    ma.id,
    ma.name,
    ISNULL(psrv.name, CONVERT(nvarchar(128), ma.publisher_id)) AS publisher,
    ma.publisher_db,
    ma.publication,
    ISNULL(ssrv.name, CONVERT(nvarchar(128), ma.subscriber_id)) AS subscriber,
    ma.subscriber_db,
    ma.job_id,
    h.runstatus,
    h.start_time,
    h.end_time AS last_activity,
    h.duration,
    mh.comments,
    h.delivery_rate,
    {(caps.HasMergeSessionConflictCounts ? @"CONVERT(bigint, h.upload_inserts) + h.upload_updates + h.upload_deletes AS uploaded_changes,
    CONVERT(bigint, h.download_inserts) + h.download_updates + h.download_deletes AS downloaded_changes,
    CONVERT(bigint, h.upload_conflicts) + h.download_conflicts AS conflicts" : @"CONVERT(bigint, NULL) AS uploaded_changes,
    CONVERT(bigint, NULL) AS downloaded_changes,
    CONVERT(bigint, NULL) AS conflicts")},
    err.error_text
FROM dbo.MSmerge_agents AS ma
LEFT JOIN sys.servers AS psrv ON psrv.server_id = ma.publisher_id
LEFT JOIN sys.servers AS ssrv ON ssrv.server_id = ma.subscriber_id
OUTER APPLY (
    SELECT TOP (1) {HistorySelect(caps, MergeSessions, "x", history.ToArray())}
    FROM dbo.MSmerge_sessions AS x WHERE x.agent_id = ma.id ORDER BY x.start_time DESC
) AS h
{(hasComments ? @"OUTER APPLY (
    SELECT TOP (1) y.comments
    FROM dbo.MSmerge_history AS y WHERE y.session_id = h.session_id ORDER BY y.time DESC
) AS mh" : @"CROSS APPLY (SELECT CONVERT(nvarchar(4000), NULL) AS comments) AS mh")}
{(hasErrorId ? @"OUTER APPLY (
    -- The last message carrying an error, not the last message: a session that failed and retried ends on a
    -- retry line, and reading the error off that one would report the failure as no error at all.
    SELECT TOP (1) y.error_id
    FROM dbo.MSmerge_history AS y WHERE y.session_id = h.session_id AND y.error_id > 0 ORDER BY y.time DESC
) AS mherr" : @"CROSS APPLY (SELECT CONVERT(int, NULL) AS error_id) AS mherr")}
OUTER APPLY (SELECT TOP (1) e.error_text FROM dbo.MSrepl_errors AS e WHERE e.id = mherr.error_id) AS err
ORDER BY publisher, ma.publisher_db, ma.publication, subscriber;";
    }

    private static async Task ReadAgentsAsync(string connectionString, ReplCapabilities caps, ReplSnapshot snapshot, CancellationToken ct)
    {
        var queries = AgentQueries(caps);
        if (queries.Count == 0) return;

        using (var conn = SqlConnectionFactory.Create(connectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);

            foreach (var query in queries)
            {
                // Per type rather than per section: one agent type the server's schema or permissions will not
                // give up should cost that type's rows and a named warning, not the whole tab.
                try
                {
                    using (var cmd = new SqlCommand(query.Sql, conn) { CommandTimeout = CommandTimeoutSeconds })
                    using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                        await ReadAgentSetAsync(reader, snapshot, query.Type, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { snapshot.Warnings.Add($"{query.Label} agents: {ex.Message}"); }
            }
        }
    }

    /// <summary>
    /// Reads one agent type's rows. The four queries deliberately share column aliases so one reader covers them
    /// all; columns a given type does not have are simply absent and come back null.
    /// </summary>
    private static async Task ReadAgentSetAsync(SqlDataReader reader, ReplSnapshot snapshot, ReplAgentType type, CancellationToken ct)
    {
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            snapshot.Agents.Add(new ReplAgentRow
            {
                AgentType = type,
                AgentId = Int(reader, "id") ?? 0,
                Name = Str(reader, "name"),
                Publisher = Str(reader, "publisher"),
                PublisherDb = Str(reader, "publisher_db"),
                Publication = Str(reader, "publication"),
                Subscriber = StrIfPresent(reader, "subscriber"),
                SubscriberDb = StrIfPresent(reader, "subscriber_db"),
                JobId = GuidIfPresent(reader, "job_id"),
                RunStatus = ReplValueParser.ToRunStatus(Int(reader, "runstatus")),
                StartTime = Date(reader, "start_time"),
                LastActivity = Date(reader, "last_activity"),
                DurationSeconds = Long(reader, "duration"),
                Comments = Str(reader, "comments"),
                LastError = Str(reader, "error_text"),
                LatencySeconds = MsToSeconds(LongIfPresent(reader, "delivery_latency")),
                DeliveryRate = DoubleIfPresent(reader, "delivery_rate"),
                DeliveredTransactions = LongIfPresent(reader, "delivered_transactions"),
                DeliveredCommands = LongIfPresent(reader, "delivered_commands"),
                UploadedChanges = LongIfPresent(reader, "uploaded_changes"),
                DownloadedChanges = LongIfPresent(reader, "downloaded_changes"),
                Conflicts = LongIfPresent(reader, "conflicts")
            });
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Agent jobs — the state of the SQL Server Agent jobs the agents actually run under
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Replication agents are SQL Server Agent jobs, and a disabled or dead job explains a stalled subscription
    /// more directly than any latency figure. Collected as its own section because it needs msdb rights the
    /// distribution database's do not imply — a login that cannot read msdb should lose these two columns, not
    /// the whole agents list.
    ///
    /// The agent tables are joined to msdb <i>here</i> rather than matching job ids in C#, and that is deliberate:
    /// the <c>MS*_agents</c> tables store <c>job_id</c> as <c>binary(16)</c> while <c>sysjobs.job_id</c> is a
    /// <c>uniqueidentifier</c>, and the two byte orders only agree under SQL Server's own conversion rule.
    /// Letting the server compare them makes the match right by construction; doing it in C# makes it a
    /// coin flip that fails silently as a lookup miss.
    ///
    /// The result is keyed by agent type and agent id, which is what the rows are keyed by anyway.
    /// </summary>
    internal static string AgentJobsSql(ReplCapabilities caps)
    {
        var parts = new List<string>();

        if (caps.HasLogReaderAgents) parts.Add(AgentJobSelect("LogReader", "dbo.MSlogreader_agents"));
        if (caps.HasDistributionAgents) parts.Add(AgentJobSelect("Distribution", "dbo.MSdistribution_agents"));
        if (caps.HasSnapshotAgents) parts.Add(AgentJobSelect("Snapshot", "dbo.MSsnapshot_agents"));
        if (caps.HasMergeAgents) parts.Add(AgentJobSelect("Merge", "dbo.MSmerge_agents"));

        return parts.Count == 0 ? null : string.Join(Environment.NewLine + "UNION ALL" + Environment.NewLine, parts) + ";";
    }

    // A job with a start but no stop on its most recent session is running. session_id ordering rather than date
    // ordering because start_execution_date is null for a session that was queued and never ran.
    private static string AgentJobSelect(string agentType, string agentTable) => $@"
SELECT N'{agentType}' AS agent_type, a.id AS agent_id, j.name AS job_name, j.enabled,
       CASE WHEN act.start_execution_date IS NOT NULL AND act.stop_execution_date IS NULL THEN 1 ELSE 0 END AS is_running
FROM {agentTable} AS a
JOIN msdb.dbo.sysjobs AS j ON j.job_id = a.job_id
OUTER APPLY (
    SELECT TOP (1) s.start_execution_date, s.stop_execution_date
    FROM msdb.dbo.sysjobactivity AS s
    WHERE s.job_id = j.job_id
    ORDER BY s.session_id DESC
) AS act";

    private static async Task ReadAgentJobsAsync(string connectionString, ReplCapabilities caps, ReplSnapshot snapshot, CancellationToken ct)
    {
        string sql = AgentJobsSql(caps);
        if (string.IsNullOrWhiteSpace(sql)) return;

        var byAgent = new Dictionary<string, (string Name, bool Enabled, bool Running)>(StringComparer.OrdinalIgnoreCase);

        using (var conn = SqlConnectionFactory.Create(connectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds })
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    string key = $"{Str(reader, "agent_type")}|{Int(reader, "agent_id")}";
                    byAgent[key] = (Str(reader, "job_name"), Bool(reader, "enabled") == true, Int(reader, "is_running") == 1);
                }
            }
        }

        foreach (var agent in snapshot.Agents)
        {
            if (!byAgent.TryGetValue($"{agent.AgentType}|{agent.AgentId}", out var job)) continue;
            agent.JobName = job.Name;
            agent.JobEnabled = job.Enabled;
            agent.JobRunning = job.Running;
        }

        // Subscriptions carry the same columns so the main grid can show them without cross-referencing tabs.
        var distributionByAgentId = new Dictionary<int, ReplAgentRow>();
        foreach (var agent in snapshot.Agents)
            if (agent.AgentType == ReplAgentType.Distribution) distributionByAgentId[agent.AgentId] = agent;

        foreach (var subscription in snapshot.Subscriptions)
        {
            if (subscription.AgentId == null || !distributionByAgentId.TryGetValue(subscription.AgentId.Value, out var agent)) continue;
            subscription.JobName = agent.JobName;
            subscription.JobEnabled = agent.JobEnabled;
            subscription.JobRunning = agent.JobRunning;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Publisher side — read from master, not the distributor
    // ---------------------------------------------------------------------------------------------

    // log_reuse_wait_desc is why this section exists. 'REPLICATION' means the log cannot be truncated until the
    // log reader drains it, so a stalled log reader grows the log until the disk fills — the failure that takes
    // an instance down, and one the distribution database says nothing about.
    //
    // Percent log used comes from the Databases counter object rather than DBCC SQLPERF(LOGSPACE) so it can be
    // read for every database in one ordinary result set.
    internal const string PublisherDatabasesSql = @"
SELECT
    d.name,
    d.is_published,
    d.is_merge_published,
    d.is_subscribed,
    d.is_sync_with_backup,
    d.recovery_model_desc,
    d.log_reuse_wait_desc,
    logsize.log_size_kb,
    pct.cntr_value AS percent_log_used
FROM sys.databases AS d
OUTER APPLY (
    SELECT SUM(CONVERT(bigint, mf.size)) * 8 AS log_size_kb
    FROM sys.master_files AS mf
    WHERE mf.database_id = d.database_id AND mf.type = 1
) AS logsize
OUTER APPLY (
    SELECT TOP (1) c.cntr_value
    FROM sys.dm_os_performance_counters AS c
    WHERE c.object_name LIKE N'%Databases%'
      AND c.counter_name LIKE N'Percent Log Used%'
      AND RTRIM(c.instance_name) = d.name
) AS pct
WHERE d.is_published = 1 OR d.is_merge_published = 1 OR d.is_subscribed = 1 OR d.is_distributor = 1
ORDER BY d.name;";

    private static async Task ReadPublisherDatabasesAsync(string masterConnectionString, ReplThresholds thresholds, ReplSnapshot snapshot, CancellationToken ct)
    {
        using (var conn = SqlConnectionFactory.Create(masterConnectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using (var cmd = new SqlCommand(PublisherDatabasesSql, conn) { CommandTimeout = CommandTimeoutSeconds })
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    snapshot.PublisherDatabases.Add(new ReplPublisherDatabaseRow
                    {
                        DatabaseName = Str(reader, "name"),
                        IsPublished = Bool(reader, "is_published") == true,
                        IsMergePublished = Bool(reader, "is_merge_published") == true,
                        IsSubscribed = Bool(reader, "is_subscribed") == true,
                        IsSyncWithBackup = Bool(reader, "is_sync_with_backup") == true,
                        RecoveryModel = Str(reader, "recovery_model_desc"),
                        LogReuseWait = Str(reader, "log_reuse_wait_desc"),
                        LogSizeKb = Long(reader, "log_size_kb"),
                        LogPercentUsed = Double(reader, "percent_log_used"),
                        Thresholds = thresholds
                    });
                }
            }
        }
    }

    /// <summary>
    /// sp_replcounters, the publisher's own view: how many transactions are waiting to be read from the log, the
    /// rate they are draining at, and the latency of the last one. Its own section because it needs sysadmin or
    /// db_owner and reads DBCC internals — a monitoring login often has neither, and losing three columns is a
    /// far better outcome than losing the tab.
    /// </summary>
    internal const string ReplCountersSql = "EXEC master.dbo.sp_replcounters;";

    private static async Task ReadReplCountersAsync(string masterConnectionString, ReplSnapshot snapshot, CancellationToken ct)
    {
        var byDatabase = new Dictionary<string, ReplPublisherDatabaseRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in snapshot.PublisherDatabases)
            byDatabase[row.DatabaseName ?? ""] = row;

        using (var conn = SqlConnectionFactory.Create(masterConnectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using (var cmd = new SqlCommand(ReplCountersSql, conn) { CommandTimeout = CommandTimeoutSeconds })
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    string database = StrIfPresent(reader, "database");
                    if (database == null || !byDatabase.TryGetValue(database, out var row)) continue;

                    row.ReplicatedTransactions = LongIfPresent(reader, "replicated transactions");
                    row.ReplicationRate = DoubleIfPresent(reader, "replication rate trans/sec");
                    row.ReplicationLatencySeconds = DoubleIfPresent(reader, "replication latency (sec)");
                    row.RaiseThresholdChanged();
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Subscriber side — each subscriber database's own record of its progress
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Every subscriber database keeps its own MSreplication_subscriptions table, so reading them means one
    /// statement per database — built dynamically because the set of databases is not known until run time.
    ///
    /// Three guards matter here: <c>state = 0</c> skips databases that are not online, <c>HAS_DBACCESS</c> skips
    /// ones this login cannot enter (either would fail the whole batch), and the OBJECT_ID check skips a database
    /// flagged as subscribed whose subscription tables have already been removed. The name is emitted through
    /// QUOTENAME and doubled quotes, so a database called <c>O'Brien's [db]</c> cannot break the batch.
    /// </summary>
    internal const string SubscriberDatabasesSql = @"
DECLARE @sql nvarchar(max) = N'';

SELECT @sql = @sql + N'SELECT ' + QUOTENAME(d.name, '''') + N' AS subscriber_db, publisher, publisher_db, publication,'
                   + N' subscription_type, [time] AS last_applied,'
                   + N' CONVERT(varchar(42), transaction_timestamp, 1) AS transaction_timestamp, description'
                   + N' FROM ' + QUOTENAME(d.name) + N'.dbo.MSreplication_subscriptions UNION ALL '
FROM sys.databases AS d
WHERE d.is_subscribed = 1
  AND d.state = 0
  AND HAS_DBACCESS(d.name) = 1
  AND OBJECT_ID(QUOTENAME(d.name) + N'.dbo.MSreplication_subscriptions') IS NOT NULL;

IF @sql <> N''
BEGIN
    -- DATALENGTH, not LEN: LEN ignores the trailing space of ' UNION ALL ' and would cut a character short.
    SET @sql = LEFT(@sql, DATALENGTH(@sql) / 2 - 11);
    EXEC sp_executesql @sql;
END
ELSE
BEGIN
    -- An empty result set of the right shape, so the reader does not have to special-case ""nothing to read"".
    SELECT CONVERT(nvarchar(128), NULL) AS subscriber_db, CONVERT(nvarchar(128), NULL) AS publisher,
           CONVERT(nvarchar(128), NULL) AS publisher_db, CONVERT(nvarchar(128), NULL) AS publication,
           CONVERT(int, NULL) AS subscription_type, CONVERT(datetime, NULL) AS last_applied,
           CONVERT(varchar(42), NULL) AS transaction_timestamp, CONVERT(nvarchar(255), NULL) AS description
    WHERE 1 = 0;
END";

    private static async Task ReadSubscriberDatabasesAsync(string masterConnectionString, ReplSnapshot snapshot, CancellationToken ct)
    {
        using (var conn = SqlConnectionFactory.Create(masterConnectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using (var cmd = new SqlCommand(SubscriberDatabasesSql, conn) { CommandTimeout = CommandTimeoutSeconds })
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    snapshot.SubscriberDatabases.Add(new ReplSubscriberDatabaseRow
                    {
                        SubscriberDb = Str(reader, "subscriber_db"),
                        Publisher = Str(reader, "publisher"),
                        PublisherDb = Str(reader, "publisher_db"),
                        Publication = Str(reader, "publication"),
                        SubscriptionType = ReplValueParser.DescribeSubscriptionType(Int(reader, "subscription_type")),
                        LastApplied = Date(reader, "last_applied"),
                        TransactionTimestamp = Str(reader, "transaction_timestamp"),
                        Description = Str(reader, "description")
                    });
                }
            }
        }

        snapshot.SubscriberDatabases.Sort((a, b) =>
        {
            int byDb = string.Compare(a.SubscriberDb, b.SubscriberDb, StringComparison.OrdinalIgnoreCase);
            return byDb != 0 ? byDb : string.Compare(a.Publication, b.Publication, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ---------------------------------------------------------------------------------------------
    // On-demand reads: pending commands, errors, tracer tokens
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Undelivered commands per distribution agent, from the MSdistribution_status view.
    ///
    /// On demand, never on the timer. The view counts rows in MSrepl_commands, which is cheap on an idle
    /// distributor and expensive on a backlogged one — precisely when someone is watching this dashboard. Its
    /// grain is per article per agent, so the counts are summed to the agent the subscriptions are keyed by.
    /// </summary>
    internal const string PendingCommandsSql = @"
SELECT
    ds.agent_id,
    SUM(CONVERT(bigint, ds.UndelivCmdsInDistDB)) AS undelivered_commands,
    SUM(CONVERT(bigint, ds.DelivCmdsInDistDB))   AS delivered_commands
FROM dbo.MSdistribution_status AS ds
GROUP BY ds.agent_id;";

    /// <summary>Returns undelivered and delivered command counts keyed by distribution agent id.</summary>
    public static async Task<Dictionary<int, (long Undelivered, long Delivered)>> ReadPendingCommandsAsync(string distributionConnectionString, CancellationToken ct)
    {
        var results = new Dictionary<int, (long, long)>();

        using (var conn = SqlConnectionFactory.Create(distributionConnectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);

            // Longer than the standard timeout on purpose: this is the one query here that can legitimately take
            // a while, and timing it out tells the user nothing they can act on.
            using (var cmd = new SqlCommand(PendingCommandsSql, conn) { CommandTimeout = 120 })
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    int? agentId = Int(reader, "agent_id");
                    if (agentId == null) continue;
                    results[agentId.Value] = (Long(reader, "undelivered_commands") ?? 0, Long(reader, "delivered_commands") ?? 0);
                }
            }
        }

        return results;
    }

    private const string ErrorsBodySql = @"
SELECT TOP (@top)
    e.id,
    e.time,
    e.error_type_id,
    e.source_type_id,
    e.source_name,
    e.error_code,
    e.error_text,
    CONVERT(varchar(42), e.xact_seqno, 1) AS xact_seqno,
    e.command_id,
    e.session_id
FROM dbo.MSrepl_errors AS e
ORDER BY e.time DESC;";

    /// <summary>The errors query with @top declared inline, so "Open as query" produces a runnable batch.</summary>
    internal static string ErrorsSql(int topRows) => $"DECLARE @top int = {topRows};{ErrorsBodySql}";

    public static async Task<List<ReplErrorRow>> ReadErrorsAsync(string distributionConnectionString, int topRows, CancellationToken ct)
    {
        var rows = new List<ReplErrorRow>();

        using (var conn = SqlConnectionFactory.Create(distributionConnectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using (var cmd = new SqlCommand(ErrorsBodySql, conn) { CommandTimeout = 60 })
            {
                cmd.Parameters.Add("@top", SqlDbType.Int).Value = topRows;
                using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        rows.Add(new ReplErrorRow
                        {
                            Id = Int(reader, "id"),
                            Time = Date(reader, "time"),
                            ErrorTypeId = Int(reader, "error_type_id"),
                            SourceTypeId = Int(reader, "source_type_id"),
                            SourceName = Str(reader, "source_name"),
                            ErrorCode = Str(reader, "error_code"),
                            ErrorText = Str(reader, "error_text"),
                            XactSeqno = Str(reader, "xact_seqno"),
                            CommandId = Int(reader, "command_id"),
                            SessionId = Int(reader, "session_id")
                        });
                    }
                }
            }
        }

        return rows;
    }

    // A tracer token has one history row per subscription it reached, so a token that never arrived at one
    // subscriber shows as a row with no subscriber_commit — which is the finding, and why this is an OUTER JOIN.
    internal const string TracerTokensSql = @"
SELECT
    t.tracer_id,
    ISNULL(psrv.name, CONVERT(nvarchar(128), p.publisher_id)) AS publisher,
    p.publisher_db,
    p.publication,
    ISNULL(ssrv.name, CONVERT(nvarchar(128), da.subscriber_id)) AS subscriber,
    da.subscriber_db,
    t.publisher_commit,
    h.distributor_commit,
    h.subscriber_commit
FROM dbo.MStracer_tokens AS t
JOIN dbo.MSpublications AS p ON p.publication_id = t.publication_id
LEFT JOIN sys.servers AS psrv ON psrv.server_id = p.publisher_id
LEFT JOIN dbo.MStracer_history AS h ON h.parent_tracer_id = t.tracer_id
LEFT JOIN dbo.MSdistribution_agents AS da ON da.id = h.agent_id
LEFT JOIN sys.servers AS ssrv ON ssrv.server_id = da.subscriber_id
ORDER BY t.publisher_commit DESC, subscriber;";

    public static async Task<List<ReplTracerRow>> ReadTracerTokensAsync(string distributionConnectionString, CancellationToken ct)
    {
        var rows = new List<ReplTracerRow>();

        using (var conn = SqlConnectionFactory.Create(distributionConnectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using (var cmd = new SqlCommand(TracerTokensSql, conn) { CommandTimeout = 60 })
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    rows.Add(new ReplTracerRow
                    {
                        TracerId = Int(reader, "tracer_id") ?? 0,
                        Publisher = Str(reader, "publisher"),
                        PublisherDb = Str(reader, "publisher_db"),
                        Publication = Str(reader, "publication"),
                        Subscriber = Str(reader, "subscriber"),
                        SubscriberDb = Str(reader, "subscriber_db"),
                        PublisherCommit = Date(reader, "publisher_commit"),
                        DistributorCommit = Date(reader, "distributor_commit"),
                        SubscriberCommit = Date(reader, "subscriber_commit")
                    });
                }
            }
        }

        return rows;
    }

    // ---------------------------------------------------------------------------------------------
    // Reader helpers
    //
    // Two families on purpose: Str/Int/Long/… require the column and throw if it is missing, which is what you
    // want for a query this file wrote. The *IfPresent variants tolerate absence, for the shared agent reader
    // (four queries, overlapping column sets) and for the stored procedures whose result shape is not ours.
    // ---------------------------------------------------------------------------------------------

    private static string Str(SqlDataReader reader, string name)
    {
        int i = reader.GetOrdinal(name);
        return reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i));
    }

    private static int? Int(SqlDataReader reader, string name)
    {
        int i = reader.GetOrdinal(name);
        return reader.IsDBNull(i) ? (int?)null : Convert.ToInt32(reader.GetValue(i));
    }

    private static long? Long(SqlDataReader reader, string name)
    {
        int i = reader.GetOrdinal(name);
        return reader.IsDBNull(i) ? (long?)null : Convert.ToInt64(reader.GetValue(i));
    }

    private static double? Double(SqlDataReader reader, string name)
    {
        int i = reader.GetOrdinal(name);
        return reader.IsDBNull(i) ? (double?)null : Convert.ToDouble(reader.GetValue(i));
    }

    private static bool? Bool(SqlDataReader reader, string name)
    {
        int i = reader.GetOrdinal(name);
        return reader.IsDBNull(i) ? (bool?)null : Convert.ToBoolean(reader.GetValue(i));
    }

    private static DateTime? Date(SqlDataReader reader, string name)
    {
        int i = reader.GetOrdinal(name);
        return reader.IsDBNull(i) ? (DateTime?)null : Convert.ToDateTime(reader.GetValue(i));
    }

    /// <summary>Column ordinal, or -1 when the result set does not have that column. Case-insensitive.</summary>
    private static int Ordinal(SqlDataReader reader, string name)
    {
        for (int i = 0; i < reader.FieldCount; i++)
            if (string.Equals(reader.GetName(i), name, StringComparison.OrdinalIgnoreCase)) return i;

        return -1;
    }

    private static string StrIfPresent(SqlDataReader reader, string name)
    {
        int i = Ordinal(reader, name);
        return i < 0 || reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i));
    }

    private static long? LongIfPresent(SqlDataReader reader, string name)
    {
        int i = Ordinal(reader, name);
        return i < 0 || reader.IsDBNull(i) ? (long?)null : Convert.ToInt64(reader.GetValue(i));
    }

    private static double? DoubleIfPresent(SqlDataReader reader, string name)
    {
        int i = Ordinal(reader, name);
        return i < 0 || reader.IsDBNull(i) ? (double?)null : Convert.ToDouble(reader.GetValue(i));
    }

    private static Guid? GuidIfPresent(SqlDataReader reader, string name)
    {
        int i = Ordinal(reader, name);
        if (i < 0 || reader.IsDBNull(i)) return null;

        object value = reader.GetValue(i);
        if (value is Guid guid) return guid;

        // job_id is binary(16) in some of these tables rather than uniqueidentifier.
        if (value is byte[] bytes && bytes.Length == 16) return new Guid(bytes);
        return Guid.TryParse(Convert.ToString(value), out var parsed) ? parsed : (Guid?)null;
    }

    /// <summary>
    /// The history tables report latency in milliseconds. Everything downstream works in seconds, so the
    /// conversion happens once, here, rather than being remembered at every use.
    /// </summary>
    private static double? MsToSeconds(long? milliseconds) => milliseconds == null ? (double?)null : milliseconds.Value / 1000d;
}
