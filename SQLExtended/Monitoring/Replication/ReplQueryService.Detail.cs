using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SQLExtended.Monitoring.Replication;

/// <summary>
/// The detail pane's reads: one agent's recent history, the full MSrepl_errors chain behind its error ids, and the
/// command it stopped on. All on demand — a row click — and all against the distribution database.
///
/// Why each piece is here:
///  * The grids show the <i>latest</i> history row's error, which is the agent's own wrapper ("agent … failed")
///    plus whichever layer the history row pointed at. MSrepl_errors writes several rows per failure under one id —
///    the agent's message, then the subscriber's SQL error, then the data source — and the useful line is usually
///    the second.
///  * MSrepl_errors records the transaction sequence number and command id the distribution agent stopped on;
///    <c>sp_browsereplcmds</c> decodes that command back to the procedure call with the row's values in it, and
///    MSarticles names the source and destination tables. For a type or constraint error that is the whole answer.
/// </summary>
internal static partial class ReplQueryService
{
    private const int DetailHistoryRows = 30;
    private const int DetailErrorIds = 10;

    /// <summary>The history table behind an agent type, or null when the type has none this pane can read.</summary>
    private static string HistoryTable(ReplCapabilities caps, ReplAgentType type)
    {
        switch (type)
        {
            case ReplAgentType.Distribution: return DistributionHistory;
            case ReplAgentType.LogReader: return LogReaderHistory;
            case ReplAgentType.Snapshot: return SnapshotHistory;
            case ReplAgentType.Merge: return caps.HasMergeHistory && caps.HasColumn(MergeHistory, "agent_id") ? MergeHistory : null;
            default: return null;
        }
    }

    private static string AgentsTable(ReplAgentType type)
    {
        switch (type)
        {
            case ReplAgentType.Distribution: return "MSdistribution_agents";
            case ReplAgentType.LogReader: return "MSlogreader_agents";
            case ReplAgentType.Snapshot: return "MSsnapshot_agents";
            case ReplAgentType.Merge: return "MSmerge_agents";
            default: return null;
        }
    }

    /// <summary>
    /// For an Errors-tab row: which agent's history points at this error id. MSrepl_errors does not say — its
    /// <c>source_name</c> is the layer that raised the message, not the agent. There is no index on
    /// <c>error_id</c>, so this scans the history tables; it runs on a click, never on the timer.
    /// </summary>
    public static async Task<(ReplAgentType Type, int AgentId)?> FindAgentForErrorAsync(string distributionConnectionString, ReplCapabilities caps, int errorId,
                                                                                         CancellationToken ct)
    {
        var parts = new List<string>();
        void Add(bool present, string table, ReplAgentType type)
        {
            if (present && caps.HasColumn(table, "error_id") && caps.HasColumn(table, "agent_id"))
                parts.Add($"SELECT {(int)type} AS agent_type, h.agent_id, h.[time] FROM dbo.{table} AS h WHERE h.error_id = @error_id");
        }

        Add(caps.HasDistributionAgents, DistributionHistory, ReplAgentType.Distribution);
        Add(caps.HasLogReaderAgents, LogReaderHistory, ReplAgentType.LogReader);
        Add(caps.HasSnapshotAgents, SnapshotHistory, ReplAgentType.Snapshot);
        Add(caps.HasMergeHistory, MergeHistory, ReplAgentType.Merge);
        if (parts.Count == 0) return null;

        string sql = $"SELECT TOP (1) x.agent_type, x.agent_id FROM ({string.Join(" UNION ALL ", parts)}) AS x ORDER BY x.[time] DESC;";

        using (var conn = SqlConnectionFactory.Create(distributionConnectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 })
            {
                cmd.Parameters.Add("@error_id", SqlDbType.Int).Value = errorId;
                using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
                    int? type = Int(reader, "agent_type");
                    int? agentId = Int(reader, "agent_id");
                    return type == null || agentId == null ? null : ((ReplAgentType)type.Value, agentId.Value);
                }
            }
        }
    }

    /// <summary>
    /// The agent's identity, recent history and error chain. Each part is its own command and its own try/catch, so a
    /// history table missing a column costs the history grid rather than the error text. <paramref name="extraErrorId"/>
    /// is the Errors-tab row's id, included even when it is older than the history window.
    /// </summary>
    public static async Task<ReplAgentDetail> ReadAgentDetailAsync(string distributionConnectionString, ReplCapabilities caps, ReplAgentType type, int? agentId,
                                                                   int? extraErrorId, CancellationToken ct)
    {
        var detail = new ReplAgentDetail { AgentId = agentId };
        string historyTable = agentId == null ? null : HistoryTable(caps, type);

        using (var conn = SqlConnectionFactory.Create(distributionConnectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);

            if (agentId != null)
            {
                try { await ReadAgentIdentityAsync(conn, type, agentId.Value, detail, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { detail.Warnings.Add("agent: " + ex.Message); }
            }

            if (historyTable != null)
            {
                try { await ReadAgentHistoryAsync(conn, caps, historyTable, agentId.Value, detail, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { detail.Warnings.Add("history: " + ex.Message); }
            }

            if (caps.HasReplErrors)
            {
                try { await ReadErrorChainAsync(conn, caps, historyTable, agentId, extraErrorId, detail, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { detail.Warnings.Add("errors: " + ex.Message); }
            }
        }

        return detail;
    }

    private static async Task ReadAgentIdentityAsync(SqlConnection conn, ReplAgentType type, int agentId, ReplAgentDetail detail, CancellationToken ct)
    {
        string table = AgentsTable(type);
        if (table == null) return;

        // Only the distribution agent has a subscriber here; the publisher database id is what sp_browsereplcmds
        // needs to seek MSrepl_commands rather than scan it.
        bool distribution = type == ReplAgentType.Distribution;
        string sql = $@"
SELECT
    a.name,
    a.publisher_id,
    ISNULL(psrv.name, CONVERT(nvarchar(128), a.publisher_id)) AS publisher,
    a.publisher_db,
    a.publication,
    {(distribution ? "ISNULL(ssrv.name, CONVERT(nvarchar(128), a.subscriber_id))" : "CONVERT(nvarchar(128), NULL)")} AS subscriber,
    {(distribution ? "a.subscriber_db" : "CONVERT(sysname, NULL)")} AS subscriber_db,
    pd.id AS publisher_database_id
FROM dbo.{table} AS a
LEFT JOIN sys.servers AS psrv ON psrv.server_id = a.publisher_id
{(distribution ? "LEFT JOIN sys.servers AS ssrv ON ssrv.server_id = a.subscriber_id" : "")}
OUTER APPLY (SELECT TOP (1) d.id FROM dbo.MSpublisher_databases AS d WHERE d.publisher_id = a.publisher_id AND d.publisher_db = a.publisher_db) AS pd
WHERE a.id = @agent_id;";

        using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds })
        {
            cmd.Parameters.Add("@agent_id", SqlDbType.Int).Value = agentId;
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return;
                detail.AgentName = Str(reader, "name");
                detail.PublisherId = Int(reader, "publisher_id");
                detail.Publisher = Str(reader, "publisher");
                detail.PublisherDb = Str(reader, "publisher_db");
                detail.Publication = Str(reader, "publication");
                detail.Subscriber = Str(reader, "subscriber");
                detail.SubscriberDb = Str(reader, "subscriber_db");
                detail.PublisherDatabaseId = Int(reader, "publisher_database_id");
            }
        }
    }

    internal static string AgentHistorySql(ReplCapabilities caps, string historyTable) => $@"
SELECT TOP ({DetailHistoryRows}) {HistorySelect(caps, historyTable, "h", "time", "runstatus", "comments", "error_id", "delivered_transactions", "delivered_commands")}
FROM dbo.{historyTable} AS h
WHERE h.agent_id = @agent_id
ORDER BY h.[time] DESC;";

    private static async Task ReadAgentHistoryAsync(SqlConnection conn, ReplCapabilities caps, string historyTable, int agentId, ReplAgentDetail detail,
                                                    CancellationToken ct)
    {
        using (var cmd = new SqlCommand(AgentHistorySql(caps, historyTable), conn) { CommandTimeout = CommandTimeoutSeconds })
        {
            cmd.Parameters.Add("@agent_id", SqlDbType.Int).Value = agentId;
            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    detail.History.Add(new ReplHistoryRow
                    {
                        Time = Date(reader, "time"),
                        RunStatus = ReplValueParser.ToRunStatus(Int(reader, "runstatus")),
                        Comments = Str(reader, "comments"),
                        ErrorId = Int(reader, "error_id"),
                        DeliveredTransactions = Long(reader, "delivered_transactions"),
                        DeliveredCommands = Long(reader, "delivered_commands")
                    });
                }
            }
        }
    }

    private static async Task ReadErrorChainAsync(SqlConnection conn, ReplCapabilities caps, string historyTable, int? agentId, int? extraErrorId,
                                                  ReplAgentDetail detail, CancellationToken ct)
    {
        // The ids come from the history table rather than from the history rows already read, so an agent that
        // logged thirty retries since its last error still gets that error — the latest ids with an error, not the
        // latest rows.
        bool byHistory = historyTable != null && agentId != null && caps.HasColumn(historyTable, "error_id");
        if (!byHistory && extraErrorId == null) return;

        var filters = new List<string>();
        if (byHistory)
            filters.Add($"e.id IN (SELECT TOP ({DetailErrorIds}) h.error_id FROM dbo.{historyTable} AS h WHERE h.agent_id = @agent_id AND h.error_id > 0 ORDER BY h.[time] DESC)");
        if (extraErrorId != null)
            filters.Add("e.id = @error_id");

        string sql = $@"
SELECT
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
WHERE {string.Join(" OR ", filters)}
ORDER BY e.id DESC, e.time;";

        using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = CommandTimeoutSeconds })
        {
            if (byHistory) cmd.Parameters.Add("@agent_id", SqlDbType.Int).Value = agentId.Value;
            if (extraErrorId != null) cmd.Parameters.Add("@error_id", SqlDbType.Int).Value = extraErrorId.Value;

            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    detail.Errors.Add(new ReplErrorRow
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

    /// <summary>
    /// A sequence number worth decoding: present and not all zeros. Agents that fail before reading a transaction
    /// (a login failure, a missing snapshot) record <c>0x00…</c>.
    /// </summary>
    internal static bool IsMeaningfulSeqno(string seqno)
    {
        if (string.IsNullOrWhiteSpace(seqno)) return false;
        string hex = seqno.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? seqno.Substring(2) : seqno;
        return hex.Length > 0 && hex.Any(c => c != '0');
    }

    /// <summary>
    /// The batch that reproduces the failing-command lookup, for "Open as query". Runs in the distribution database.
    /// </summary>
    internal static string FailingCommandSql(string seqno, int? publisherDatabaseId, int? commandId) =>
        $@"-- The command the agent stopped on. Run in the distribution database.
EXEC sys.sp_browsereplcmds
    @xact_seqno_start = N'{seqno}',
    @xact_seqno_end   = N'{seqno}'{(publisherDatabaseId == null ? "" : $@",
    @publisher_database_id = {publisherDatabaseId}")};{(commandId == null ? "" : $"   -- look for command_id = {commandId}")}";

    /// <summary>
    /// Decodes the command a distribution agent stopped on. <c>sp_browsereplcmds</c> is given the transaction's
    /// sequence number as both bounds and the publisher database id, which makes it a seek on MSrepl_commands'
    /// clustered key; the matching command id is picked out here rather than passed in, because the procedure
    /// insists that every other filter be supplied alongside <c>@command_id</c>.
    /// </summary>
    public static async Task<ReplFailingCommand> ReadFailingCommandAsync(string distributionConnectionString, ReplAgentDetail detail, string seqno, int? commandId,
                                                                         CancellationToken ct)
    {
        var result = new ReplFailingCommand { XactSeqno = seqno, CommandId = commandId };

        if (!IsMeaningfulSeqno(seqno))
        {
            result.Problem = "The error has no transaction sequence number — the agent failed before it reached a command (a login, path or snapshot problem rather than a data one).";
            return result;
        }

        if (detail.PublisherDatabaseId == null)
        {
            result.Problem = "Could not work out the publisher database id, which sp_browsereplcmds needs to find the command without scanning MSrepl_commands.";
            return result;
        }

        // nchar(22) parameters: "0x" and twenty hex digits for the ten-byte sequence number.
        string bound = seqno.Length > 22 ? seqno.Substring(0, 22) : seqno;
        var text = new StringBuilder();

        using (var conn = SqlConnectionFactory.Create(distributionConnectionString))
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);

            using (var cmd = new SqlCommand("sys.sp_browsereplcmds", conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 120 })
            {
                cmd.Parameters.Add("@xact_seqno_start", SqlDbType.NChar, 22).Value = bound;
                cmd.Parameters.Add("@xact_seqno_end", SqlDbType.NChar, 22).Value = bound;
                cmd.Parameters.Add("@publisher_database_id", SqlDbType.Int).Value = detail.PublisherDatabaseId.Value;

                using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    int ordCommandId = Ordinal(reader, "command_id");
                    int ordArticle = Ordinal(reader, "article_id");
                    int ordCommand = Ordinal(reader, "command");
                    var seen = new HashSet<int>();
                    int? firstCommandId = null;

                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        int? rowCommandId = ordCommandId < 0 || reader.IsDBNull(ordCommandId) ? (int?)null : Convert.ToInt32(reader.GetValue(ordCommandId));
                        if (rowCommandId != null) seen.Add(rowCommandId.Value);
                        firstCommandId ??= rowCommandId;

                        // A long command arrives as several partial rows under one command id; they are joined in order.
                        bool match = commandId == null ? rowCommandId == firstCommandId : rowCommandId == commandId;
                        if (!match) continue;

                        if (result.ArticleId == null && ordArticle >= 0 && !reader.IsDBNull(ordArticle))
                            result.ArticleId = Convert.ToInt32(reader.GetValue(ordArticle));
                        if (ordCommand >= 0 && !reader.IsDBNull(ordCommand))
                            text.Append(Convert.ToString(reader.GetValue(ordCommand)));
                    }

                    result.CommandsInTransaction = seen.Count;
                }
            }

            if (result.ArticleId != null && detail.PublisherDb != null)
            {
                try
                {
                    const string articleSql = @"
SELECT TOP (1) a.article, a.source_owner, a.source_object, a.destination_owner, a.destination_object
FROM dbo.MSarticles AS a
WHERE a.article_id = @article_id AND a.publisher_db = @publisher_db AND (@publisher_id IS NULL OR a.publisher_id = @publisher_id);";

                    using (var cmd = new SqlCommand(articleSql, conn) { CommandTimeout = CommandTimeoutSeconds })
                    {
                        cmd.Parameters.Add("@article_id", SqlDbType.Int).Value = result.ArticleId.Value;
                        cmd.Parameters.Add("@publisher_db", SqlDbType.NVarChar, 128).Value = detail.PublisherDb;
                        cmd.Parameters.Add("@publisher_id", SqlDbType.SmallInt).Value = (object)detail.PublisherId ?? DBNull.Value;

                        using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                        {
                            if (await reader.ReadAsync(ct).ConfigureAwait(false))
                            {
                                result.Article = Str(reader, "article");
                                result.SourceObject = TwoPart(Str(reader, "source_owner"), Str(reader, "source_object"));
                                result.DestinationObject = TwoPart(Str(reader, "destination_owner"), Str(reader, "destination_object"));
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The command text is the important half; an unreadable article name is not worth losing it over.
                }
            }
        }

        result.CommandText = text.Length == 0 ? null : text.ToString();
        if (result.CommandText == null)
            result.Problem = result.CommandsInTransaction == 0
                ? "The transaction is no longer in MSrepl_commands — distribution cleanup has removed it, or the subscription was reinitialized since."
                : $"Command {commandId} was not among the {result.CommandsInTransaction} command(s) of that transaction.";

        return result;
    }

    private static string TwoPart(string schema, string name) =>
        string.IsNullOrEmpty(name) ? null : string.IsNullOrEmpty(schema) ? $"[{name}]" : $"[{schema}].[{name}]";
}
