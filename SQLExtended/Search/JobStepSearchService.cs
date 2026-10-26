using Microsoft.Data.SqlClient;
using SQLExtended.Cache;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SQLExtended.Search;

/// <summary>
/// Searches SQL Server Agent job step commands (and step/job names) in msdb.
///
/// Three things separate this from the rest of SQL Search, all of them consequences of jobs being a
/// *server*-level object with no representation in the schema cache:
///  * It runs **once per search, not once per selected database**, against msdb on the chosen server. A job
///    step's target database is a column on the step, not the database the job lives in, so scoping it to the
///    database selection would drop every non-TSQL step and every step pointed at master.
///  * It is **read live**. Nothing here is cached: <c>sysjobsteps</c> is small (a server-side LIKE over a few
///    hundred rows), the search is already debounced, and a stale command body is the one thing worth less
///    than the round trip that avoids it — job steps are edited far more casually than modules.
///  * A login outside SQLAgentReaderRole silently sees **only the jobs it owns**, so membership is probed and
///    reported. As in the Agent jobs dashboard, a short list that looks complete is the worst outcome here.
///
/// Matching is done server-side with LIKE under an explicit CI collation, because msdb on a case-sensitive
/// instance would otherwise answer case-sensitively while every other part of this search is
/// OrdinalIgnoreCase. Which field matched is then worked out client-side, where the text is already in hand
/// — and where the snippet has to be built anyway.
/// </summary>
internal static class JobStepSearchService
{
    internal const int CommandTimeoutSeconds = 20;

    /// <summary>Characters either side of the match kept in the one-line snippet.</summary>
    private const int SnippetContext = 45;

    internal sealed class Result
    {
        public List<JobStepMatch> Matches { get; } = new();

        /// <summary>Set when the answer is incomplete or unavailable — no Agent, a restricted login, or a
        /// failed read. Shown on the status line: none of these throw, and none of them may pass as "no jobs".</summary>
        public string Warning { get; set; }

        /// <summary>
        /// <c>SERVERPROPERTY('ServerName')</c>, which is what SSMS's Job Properties dialog needs in the job's
        /// URN — behind an AG listener or a CNAME it differs from the connection string's Data Source.
        /// </summary>
        public string ServerName { get; set; }
    }

    /// <summary>
    /// Normalises a connection string harvested from SSMS for this search: msdb, a short connect timeout, and
    /// an application name that is recognisable in sys.dm_exec_sessions on the searched server.
    /// </summary>
    internal static string BuildConnectionString(string baseConnectionString)
    {
        var builder = new SqlConnectionStringBuilder(baseConnectionString)
        {
            InitialCatalog = "msdb",
            ApplicationName = "SQLExtended SQL Search",
            ConnectTimeout = 10
        };
        return builder.ConnectionString;
    }

    public static async Task<Result> SearchAsync(string connectionString, string searchTerm, int maxResults, bool wholeWord, CancellationToken ct)
    {
        var result = new Result();
        if (string.IsNullOrWhiteSpace(searchTerm) || string.IsNullOrEmpty(connectionString))
            return result;

        try
        {
            using (var conn = SqlConnectionFactory.Create(BuildConnectionString(connectionString)))
            {
                await conn.OpenAsync(ct).ConfigureAwait(false);

                if (!await ProbeAsync(conn, result, ct).ConfigureAwait(false))
                    return result;

                await ReadStepsAsync(conn, result, searchTerm, maxResults, wholeWord, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // The rest of the search is still worth showing, so this is reported rather than thrown.
            result.Warning = "Agent job steps could not be searched: " + ex.Message;
        }

        return result;
    }

    private static async Task<bool> ProbeAsync(SqlConnection conn, Result result, CancellationToken ct)
    {
        using (var cmd = new SqlCommand(ProbeSql, conn) { CommandTimeout = CommandTimeoutSeconds })
        using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                result.Warning = "Agent job steps could not be searched: the server did not respond to the Agent probe.";
                return false;
            }

            result.ServerName = reader["server_name"] as string;

            if (Convert.ToInt32(reader["has_agent_tables"], CultureInfo.InvariantCulture) == 0)
            {
                result.Warning = "Agent job steps were not searched: SQL Server Agent is not installed on this instance.";
                return false;
            }

            bool sysadmin = Convert.ToInt32(reader["is_sysadmin"], CultureInfo.InvariantCulture) == 1;
            bool agentReader = Convert.ToInt32(reader["is_agent_reader"], CultureInfo.InvariantCulture) == 1;
            bool agentOperator = Convert.ToInt32(reader["is_agent_operator"], CultureInfo.InvariantCulture) == 1;

            if (!sysadmin && !agentReader && !agentOperator)
            {
                result.Warning = "Agent job steps: this login is not sysadmin and not a member of SQLAgentReaderRole or "
                               + "SQLAgentOperatorRole in msdb, so only jobs it owns were searched.";
            }
        }

        return true;
    }

    private static async Task ReadStepsAsync(SqlConnection conn, Result result, string searchTerm, int maxResults, bool wholeWord, CancellationToken ct)
    {
        // Whole word has no LIKE form: LIKE narrows to the substring matches and Classify makes the word check.
        // TOP then has to stop limiting, or substring-only rows would fill it ahead of the real hits — the cap
        // is applied here instead, after the check. sysjobsteps is small enough that this costs nothing.
        using (var cmd = new SqlCommand(StepsSql, conn) { CommandTimeout = CommandTimeoutSeconds })
        {
            cmd.Parameters.Add("@pattern", System.Data.SqlDbType.NVarChar, 4000).Value = BuildLikePattern(searchTerm);
            cmd.Parameters.Add("@max", System.Data.SqlDbType.Int).Value = wholeWord ? int.MaxValue : Math.Max(1, maxResults);

            using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (result.Matches.Count < Math.Max(1, maxResults) && await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var match = new JobStepMatch
                    {
                        JobId = reader.GetGuid(reader.GetOrdinal("job_id")),
                        JobName = reader["job_name"] as string,
                        JobEnabled = Convert.ToInt32(reader["enabled"], CultureInfo.InvariantCulture) == 1,
                        StepId = Convert.ToInt32(reader["step_id"], CultureInfo.InvariantCulture),
                        StepName = reader["step_name"] as string,
                        Subsystem = reader["subsystem"] as string,
                        StepDatabase = reader["database_name"] as string,
                        Command = reader["command"] as string
                    };

                    if (Classify(match, searchTerm, wholeWord))
                        result.Matches.Add(match);
                }
            }
        }
    }

    /// <summary>
    /// Decides which field the term was found in and builds the snippet. The server matched under a CI
    /// collation, which is not exactly <see cref="StringComparison.OrdinalIgnoreCase"/> (width, kana and a
    /// handful of accent-insensitive pairs can differ), so a row the client scan cannot place is kept and
    /// labelled generically rather than dropped — the server is the authority on what matched.
    ///
    /// <para>Except under whole word, where the server only matched substrings and this <i>is</i> the match:
    /// a row with no whole-word hit in any field is rejected (returns false). The few rows CI and
    /// OrdinalIgnoreCase disagree on go with it — whole word is a narrowing the user asked for.</para>
    /// </summary>
    internal static bool Classify(JobStepMatch match, string searchTerm, bool wholeWord = false)
    {
        int inCommand = SearchTextMatcher.IndexOf(match.Command, searchTerm, 0, wholeWord);
        if (inCommand >= 0)
        {
            match.MatchedIn = "Command";
            match.Snippet = BuildSnippet(match.Command, inCommand, searchTerm.Length);
            return true;
        }

        if (SearchTextMatcher.Contains(match.StepName, searchTerm, wholeWord)) { match.MatchedIn = "StepName"; return true; }
        if (SearchTextMatcher.Contains(match.JobName, searchTerm, wholeWord)) { match.MatchedIn = "JobName"; return true; }

        if (wholeWord) return false;
        match.MatchedIn = "JobStep";
        return true;
    }

    /// <summary>
    /// One line of the command around the match. Job step commands are frequently a whole batch, so every run
    /// of whitespace is collapsed — a raw excerpt of a formatted T-SQL step is mostly indentation.
    /// </summary>
    internal static string BuildSnippet(string text, int matchIndex, int matchLength)
    {
        if (string.IsNullOrEmpty(text)) return null;

        int start = Math.Max(0, matchIndex - SnippetContext);
        int end = Math.Min(text.Length, matchIndex + matchLength + SnippetContext);

        var sb = new StringBuilder(end - start + 8);
        if (start > 0) sb.Append('…');

        bool lastWasSpace = false;
        for (int i = start; i < end; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        if (end < text.Length) sb.Append('…');
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Wraps the term in wildcards, escaping the ones LIKE would otherwise interpret. <c>[</c> has to be
    /// escaped as well as <c>%</c> and <c>_</c> — a search for a bracketed identifier is a normal thing to
    /// type here, and unescaped it opens a character class that swallows the rest of the pattern.
    /// </summary>
    internal static string BuildLikePattern(string searchTerm)
    {
        var sb = new StringBuilder(searchTerm.Length + 8);
        sb.Append('%');
        foreach (char c in searchTerm)
        {
            if (c == '%' || c == '_' || c == '[' || c == LikeEscape) sb.Append(LikeEscape);
            sb.Append(c);
        }
        sb.Append('%');
        return sb.ToString();
    }

    private const char LikeEscape = '\\';

    // -------------------------------------------------------------------------------------------------
    // SQL
    // -------------------------------------------------------------------------------------------------

    internal const string ProbeSql = @"
SET NOCOUNT ON;
SELECT
    CONVERT(nvarchar(128), SERVERPROPERTY('ServerName'))                    AS server_name,
    CASE WHEN OBJECT_ID('msdb.dbo.sysjobsteps') IS NULL THEN 0 ELSE 1 END   AS has_agent_tables,
    CONVERT(int, ISNULL(IS_SRVROLEMEMBER('sysadmin'), 0))                   AS is_sysadmin,
    CONVERT(int, ISNULL(IS_ROLEMEMBER('SQLAgentReaderRole'), 0))            AS is_agent_reader,
    CONVERT(int, ISNULL(IS_ROLEMEMBER('SQLAgentOperatorRole'), 0))          AS is_agent_operator;";

    /// <summary>
    /// The step search. The explicit CI collation is what keeps this consistent with the in-memory search on a
    /// case-sensitive instance; it is applied to the column rather than the pattern so the comparison, not the
    /// parameter, is what is re-collated.
    ///
    /// A job whose *name* matches returns all of its steps. That is deliberate — the step is the addressable
    /// thing here, each row says which field matched, and a job named for what you searched for is exactly the
    /// one whose steps you wanted to see.
    /// </summary>
    internal const string StepsSql = @"
SET NOCOUNT ON;
SELECT TOP (@max)
    j.job_id,
    j.name          AS job_name,
    j.enabled,
    s.step_id,
    s.step_name,
    s.subsystem,
    s.database_name,
    s.command
FROM msdb.dbo.sysjobs AS j
INNER JOIN msdb.dbo.sysjobsteps AS s ON s.job_id = j.job_id
WHERE s.command   COLLATE Latin1_General_CI_AS LIKE @pattern ESCAPE '\'
   OR s.step_name COLLATE Latin1_General_CI_AS LIKE @pattern ESCAPE '\'
   OR j.name      COLLATE Latin1_General_CI_AS LIKE @pattern ESCAPE '\'
ORDER BY j.name, s.step_id;";
}
