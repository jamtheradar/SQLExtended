using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace SQLExtended.ScriptCast;

internal enum CastStatus { Pending, Connecting, Running, Succeeded, Failed, Cancelled }

/// <summary>Where a database's transaction stands in transaction mode (issue #13, stage 2). <c>None</c> outside it.</summary>
internal enum TxOutcome { None, Open, Committed, RolledBack, CommitFailed, CommitUnknown, Lost }

/// <summary>One database to cast to. The connection string is the server's Object Explorer one, harvested credential-free for Entra.</summary>
internal sealed class CastTarget(string server, string database, string serverConnectionString)
{
    public string Server { get; } = server;
    public string Database { get; } = database;
    public string ServerConnectionString { get; } = serverConnectionString;
}

/// <summary>A line for the Messages tab: an informational message, a row count, or an error, from one database.</summary>
internal sealed class CastMessage
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Server { get; set; }
    public string Database { get; set; }
    public string Text { get; set; }
    public bool IsError { get; set; }
    public string TimeText => Time.ToString("HH:mm:ss.fff");
    public string OneLine => (Text ?? "").Replace("\r\n", " ").Replace('\n', ' ');
}

/// <summary>Settings for one cast, read on the UI thread before it starts (settings must not be faulted in from a worker).</summary>
internal sealed class CastOptions
{
    public int MaxParallelism { get; set; } = 8;
    public int CommandTimeoutSeconds { get; set; }
    public int MaxRowsPerResultSet { get; set; } = 100_000;

    /// <summary>Each database runs inside its own transaction, left open for Commit All / Rollback All.</summary>
    public bool InTransaction { get; set; }

    /// <summary>In transaction mode: the first database to fail cancels the rest, and every transaction is rolled back.</summary>
    public bool AutoRollback { get; set; }
}

/// <summary>One database's run: a row of the Summary tab, updated live from its worker thread.</summary>
internal sealed class DatabaseRun(CastTarget target) : INotifyPropertyChanged
{
    private CastStatus _status = CastStatus.Pending;
    private TimeSpan _duration;
    private long _rowsReturned, _rowsAffected;
    private int _errorCount;
    private string _firstError = "";
    private TxOutcome _transaction;

    public CastTarget Target { get; } = target;
    public string Server => Target.Server;
    public string Database => Target.Database;
    public List<ResultSetCapture> ResultSets { get; } = new();

    public CastStatus Status { get => _status; set { _status = value; Raise(); Raise(nameof(StatusText)); Raise(nameof(IsFailed)); Raise(nameof(IsWarning)); } }

    public TxOutcome Transaction { get => _transaction; set { _transaction = value; Raise(); Raise(nameof(TransactionText)); Raise(nameof(IsFailed)); } }

    /// <summary>
    /// The connection whose transaction is still open, between the end of the cast and Commit All / Rollback All. Not
    /// pooled: closing it ends the session, and the server rolls the transaction back. Touched by one operation at a time.
    /// </summary>
    internal SqlConnection Held { get; set; }
    public TimeSpan Duration { get => _duration; set { _duration = value; Raise(); Raise(nameof(DurationText)); } }
    public long RowsReturned { get => _rowsReturned; set { _rowsReturned = value; Raise(); } }

    /// <summary>INSERT/UPDATE/DELETE/MERGE rows (the reader's <c>RecordsAffected</c>), summed over batches; SELECTs are <see cref="RowsReturned"/>.</summary>
    public long RowsAffected { get => _rowsAffected; set { _rowsAffected = value; Raise(); } }

    public int ErrorCount { get => _errorCount; set { _errorCount = value; Raise(); } }
    public string FirstError { get => _firstError; set { _firstError = value; Raise(); } }

    public string StatusText => Status switch
    {
        CastStatus.Succeeded => "Succeeded",
        CastStatus.Failed => "Failed",
        CastStatus.Cancelled => "Cancelled",
        CastStatus.Connecting => "Connecting…",
        CastStatus.Running => "Running…",
        _ => "Pending",
    };

    public string TransactionText => Transaction switch
    {
        TxOutcome.Open => "Open",
        TxOutcome.Committed => "Committed",
        TxOutcome.RolledBack => "Rolled back",
        TxOutcome.CommitFailed => "Commit failed",
        TxOutcome.CommitUnknown => "Commit unknown",
        TxOutcome.Lost => "Ended early",
        _ => "",
    };

    public string DurationText => Status is CastStatus.Pending ? "" : $"{Duration.TotalSeconds:0.000} s";

    // Row style triggers in MonitoringTheme.xaml.
    public bool IsFailed => Status == CastStatus.Failed || Transaction is TxOutcome.CommitFailed or TxOutcome.CommitUnknown or TxOutcome.Lost;
    public bool IsWarning => Status == CastStatus.Cancelled;

    public event PropertyChangedEventHandler PropertyChanged;
    private void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Runs a script on many databases at once: one connection and one thread per database, at most
/// <see cref="CastOptions.MaxParallelism"/> at a time. Every connection is made through
/// <see cref="SqlConnectionFactory"/>, so an Entra sign-in's token is attached as it is everywhere else.
///
/// <para>Errors are delivered as SSMS delivers them: <c>FireInfoMessageEventOnUserErrors</c> turns errors up to
/// severity 16 into messages, so a batch that fails one statement keeps returning the result sets after it, and the
/// next batch still runs. A severity 17+ error or a dropped connection ends that database; the others carry on.</para>
/// </summary>
internal static class CastRunner
{
    /// <summary>
    /// Casts to every run. In transaction mode a database that succeeds is left with its transaction open and its
    /// connection in <see cref="DatabaseRun.Held"/>, unless the cast was cancelled or auto rollback fired, in which case
    /// every transaction is rolled back before this returns.
    /// </summary>
    public static async Task RunAsync(IReadOnlyList<DatabaseRun> runs, IReadOnlyList<ScriptBatch> batches, CastOptions options,
        Action<CastMessage> onMessage, CancellationToken ct)
    {
        // Auto rollback cancels everyone else on the first failure: a cancel the caller didn't ask for, so it has its
        // own source, linked to the caller's.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int autoRolledBack = 0;
        Action<DatabaseRun> onFailed = options.InTransaction && options.AutoRollback
            ? failed =>
            {
                if (Interlocked.Exchange(ref autoRolledBack, 1) != 0) return;
                Say(failed, onMessage, "Auto rollback: this database failed, so every database is being rolled back and the rest cancelled.", true);
                // Off this thread: a failure usually arrives in SqlClient's InfoMessage callback, mid-read, and the
                // cancel runs every command's cmd.Cancel synchronously, this one's included.
                _ = Task.Run(() => { try { stop.Cancel(); } catch (ObjectDisposedException) { } });
            }
            : null;

        using var gate = new SemaphoreSlim(Math.Max(1, options.MaxParallelism));
        var tasks = runs.Select(run => Task.Run(async () =>
        {
            try
            {
                await gate.WaitAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                run.Status = CastStatus.Cancelled;
                return;
            }

            try
            {
                await RunOneAsync(run, batches, options, onMessage, onFailed, stop.Token).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);

        // Cancel (or auto rollback) in transaction mode rolls back the databases that had already finished too.
        if (options.InTransaction && stop.IsCancellationRequested)
            await RollbackAllAsync(runs.Where(r => r.Held != null).ToList(), onMessage).ConfigureAwait(false);
    }

    private static async Task RunOneAsync(DatabaseRun run, IReadOnlyList<ScriptBatch> batches, CastOptions options, Action<CastMessage> onMessage,
        Action<DatabaseRun> onFailed, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        void Say(string text, bool isError) => onMessage(new CastMessage { Server = run.Server, Database = run.Database, Text = text, IsError = isError });
        void Fail(string text)
        {
            run.ErrorCount++;
            if (string.IsNullOrEmpty(run.FirstError)) run.FirstError = text.Replace("\r\n", " ").Replace('\n', ' ');
            Say(text, isError: true);
            onFailed?.Invoke(run);
        }

        int currentBatchStart = 1;
        SqlConnection conn = null;
        try
        {
            run.Status = CastStatus.Connecting;
            conn = SqlConnectionFactory.Create(ConnectionStringFor(run.Target, pooled: !options.InTransaction));
            conn.FireInfoMessageEventOnUserErrors = true;
            conn.InfoMessage += (_, e) =>
            {
                foreach (SqlError error in e.Errors)
                {
                    if (error.Class > 10) Fail(FormatError(error, currentBatchStart));
                    else Say(error.Message, isError: false);
                }
            };

            await conn.OpenAsync(ct).ConfigureAwait(false);
            run.Status = CastStatus.Running;

            if (options.InTransaction)
            {
                // T-SQL, not SqlConnection.BeginTransaction: SqlClient would then demand cmd.Transaction on every
                // command, and a transaction the server rolled back (XACT_ABORT, severity 20) would leave its object
                // zombied. This behaves as a query window's own BEGIN TRAN does.
                ExecuteScalar(conn, "BEGIN TRANSACTION");
                run.Transaction = TxOutcome.Open;
            }

            int ordinal = 0;
            bool stopped = false;
            foreach (var batch in batches)
            {
                currentBatchStart = batch.StartLine;
                for (int repeat = 0; repeat < batch.RepeatCount && !stopped; repeat++)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!ExecuteBatch(conn, batch, run, options, ref ordinal, Say, Fail, ct) && conn.State != System.Data.ConnectionState.Open)
                    {
                        Fail("The connection was closed; the rest of the script was not run on this database.");
                        stopped = true;
                    }
                    else if (options.InTransaction && !CheckTransaction(conn, run, batch, Fail))
                    {
                        stopped = true;
                    }
                }
                if (stopped) break;
            }
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            if (run.ErrorCount == 0) Say("Cancelled.", isError: false);
        }
        catch (SqlException ex)
        {
            foreach (SqlError error in ex.Errors) Fail(FormatError(error, currentBatchStart));
        }
        catch (Exception ex)
        {
            Fail(ex.Message);
            Diagnostics.SQLExtendedLog.Warning("ScriptCast", $"Cast to {run.Server} / {run.Database} failed", ex);
        }

        run.Duration = clock.Elapsed;
        // An error wins over the cancel: the database that set off auto rollback is Failed, the ones it stopped Cancelled.
        run.Status = run.ErrorCount > 0 ? CastStatus.Failed : ct.IsCancellationRequested ? CastStatus.Cancelled : CastStatus.Succeeded;

        // Transaction mode: only a clean finish keeps its transaction for Commit All. A failure or a cancel rolls this
        // database back now; an uncommittable transaction has nothing to review, and it holds locks.
        if (conn != null && run.Transaction == TxOutcome.Open)
        {
            run.Held = conn;
            if (run.Status == CastStatus.Succeeded && !ct.IsCancellationRequested)
                Say("Succeeded. The transaction stays open until Commit All or Rollback All.", isError: false);
            else
                await Task.Run(() => Rollback(run, onMessage, TxOutcome.RolledBack)).ConfigureAwait(false);
            return;
        }

        conn?.Dispose();
    }

    /// <summary>
    /// After each batch in transaction mode: is the cast's transaction still open and committable? <c>@@TRANCOUNT</c> 0
    /// means something ended it (a procedure's COMMIT or ROLLBACK, XACT_ABORT, a severity 20 error) and the next batch
    /// would run in autocommit, so the rest is not run. <c>XACT_STATE()</c> -1 means it can only be rolled back.
    /// </summary>
    private static bool CheckTransaction(SqlConnection conn, DatabaseRun run, ScriptBatch batch, Action<string> fail)
    {
        var (tranCount, xactState) = ReadTransactionState(conn);
        if (tranCount == 0)
        {
            run.Transaction = TxOutcome.Lost;
            fail($"The transaction was no longer open after the batch at line {batch.StartLine}: the script committed or rolled it back, or an error "
                 + "(XACT_ABORT, a severe error) rolled it back. Work from that batch may already be committed. The rest of the script was not run.");
            return false;
        }
        if (xactState == -1)
        {
            fail($"The transaction can't be committed after the batch at line {batch.StartLine} (XACT_STATE() = -1), so it is rolled back. The rest of the script was not run.");
            return false;
        }
        return true;
    }

    private static (int TranCount, int XactState) ReadTransactionState(SqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT @@TRANCOUNT, XACT_STATE()";
        cmd.CommandTimeout = 30;
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (Convert.ToInt32(reader.GetValue(0)), Convert.ToInt32(reader.GetValue(1)));
    }

    private static object ExecuteScalar(SqlConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        return cmd.ExecuteScalar();
    }

    // ============================================================ Transaction mode: verify, commit, roll back

    /// <summary>
    /// Before Commit All commits anything: is every held transaction still open and committable? One that isn't (the
    /// connection dropped, the session was killed) is marked and released, and the caller commits nothing, so a commit
    /// never starts on a set already known to be out of step.
    /// </summary>
    public static async Task<bool> VerifyAllAsync(IReadOnlyList<DatabaseRun> held, Action<CastMessage> onMessage)
    {
        var results = await Task.WhenAll(held.Select(run => Task.Run(() =>
        {
            try
            {
                var (tranCount, xactState) = ReadTransactionState(run.Held);
                if (tranCount == 1 && xactState == 1) return true;
                Say(run, onMessage, $"Not committable at Commit All (@@TRANCOUNT {tranCount}, XACT_STATE() {xactState}); rolled back.", true);
                Rollback(run, onMessage, TxOutcome.Lost);
                return false;
            }
            catch (Exception ex)
            {
                Say(run, onMessage, $"The transaction's connection is gone ({ex.Message}). The server rolls back the open transaction of a session that ends.", true);
                Release(run, TxOutcome.Lost);
                return false;
            }
        }))).ConfigureAwait(false);
        return results.All(ok => ok);
    }

    /// <summary>
    /// Commits one database. Not atomic across databases: Commit All calls this one database after another. A commit
    /// that throws while the connection is still up and the transaction still open failed outright, and is rolled back;
    /// one whose connection dropped mid-commit may or may not have committed, and says so.
    /// </summary>
    public static void Commit(DatabaseRun run, Action<CastMessage> onMessage)
    {
        var conn = run.Held;
        if (conn == null) return;
        try
        {
            int left = Convert.ToInt32(ExecuteScalar(conn, "IF @@TRANCOUNT = 1 COMMIT TRANSACTION; SELECT @@TRANCOUNT;"));
            if (left == 0)
            {
                Say(run, onMessage, "Committed.", false);
                Release(run, TxOutcome.Committed);
                return;
            }
            Say(run, onMessage, $"Not committed: {left} transactions were open where one was expected (a procedure left one open). Rolled back.", true);
            Rollback(run, onMessage, TxOutcome.CommitFailed);
        }
        catch (Exception ex)
        {
            bool stillOpen = false;
            try { stillOpen = conn.State == System.Data.ConnectionState.Open && ReadTransactionState(conn).TranCount == 1; } catch { }
            if (stillOpen)
            {
                Say(run, onMessage, $"Commit failed: {ex.Message} Rolled back.", true);
                Rollback(run, onMessage, TxOutcome.CommitFailed);
            }
            else
            {
                Say(run, onMessage, $"Commit outcome unknown: the connection failed during COMMIT ({ex.Message}). Check this database by hand.", true);
                Release(run, TxOutcome.CommitUnknown);
            }
        }
    }

    public static Task RollbackAllAsync(IReadOnlyList<DatabaseRun> held, Action<CastMessage> onMessage)
        => Task.WhenAll(held.Select(run => Task.Run(() => Rollback(run, onMessage, TxOutcome.RolledBack))));

    /// <summary>
    /// Rolls back and closes. If the ROLLBACK itself fails, closing the unpooled connection ends the session, and the
    /// server rolls back an ended session's transaction: either way none of it is committed.
    /// </summary>
    private static void Rollback(DatabaseRun run, Action<CastMessage> onMessage, TxOutcome outcome)
    {
        var conn = run.Held;
        if (conn == null) return;
        try
        {
            if (conn.State == System.Data.ConnectionState.Open) ExecuteScalar(conn, "IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION");
            if (outcome == TxOutcome.RolledBack) Say(run, onMessage, "Rolled back.", false);
        }
        catch (Exception ex)
        {
            Say(run, onMessage, $"ROLLBACK failed ({ex.Message}); the connection was closed instead, which ends the session and rolls the transaction back.", true);
        }
        Release(run, outcome);
    }

    /// <summary>Closes a held connection for good. Also used directly when the tab or SSMS closes: the server rolls back.</summary>
    public static void Release(DatabaseRun run, TxOutcome outcome)
    {
        var conn = run.Held;
        run.Held = null;
        try { conn?.Dispose(); } catch { }
        run.Transaction = outcome;
    }

    private static void Say(DatabaseRun run, Action<CastMessage> onMessage, string text, bool isError)
        => onMessage(new CastMessage { Server = run.Server, Database = run.Database, Text = text, IsError = isError });

    /// <summary>Runs one batch once, capturing its result sets. False when the server threw instead of messaging.</summary>
    private static bool ExecuteBatch(SqlConnection conn, ScriptBatch batch, DatabaseRun run, CastOptions options, ref int ordinal,
        Action<string, bool> say, Action<string> fail, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = batch.Text;
        cmd.CommandTimeout = Math.Max(0, options.CommandTimeoutSeconds);
        cmd.StatementCompleted += (_, e) => say($"({e.RecordCount} row{(e.RecordCount == 1 ? "" : "s")} affected)", false);

        using var cancel = ct.Register(() => { try { cmd.Cancel(); } catch { } });
        try
        {
            using var reader = cmd.ExecuteReader();
            do
            {
                if (reader.FieldCount == 0) continue;
                var capture = Capture(reader, ordinal++, options.MaxRowsPerResultSet, ct, out bool truncated);
                lock (run.ResultSets) run.ResultSets.Add(capture);
                run.RowsReturned += capture.Rows.Count;
                if (truncated)
                    say($"Result set {capture.Ordinal + 1} stopped at {options.MaxRowsPerResultSet:N0} rows (Settings > ScriptCast).", false);
            }
            while (reader.NextResult());

            reader.Close();
            if (reader.RecordsAffected > 0) run.RowsAffected += reader.RecordsAffected;
            return true;
        }
        catch (SqlException ex) when (!ct.IsCancellationRequested)
        {
            foreach (SqlError error in ex.Errors) fail(FormatError(error, batch.StartLine));
            return false;
        }
    }

    private static ResultSetCapture Capture(SqlDataReader reader, int ordinal, int maxRows, CancellationToken ct, out bool truncated)
    {
        var columns = new List<ResultColumn>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            string name = reader.GetName(i);
            Type type;
            string sqlType;
            try { type = reader.GetFieldType(i); } catch { type = typeof(object); }
            try { sqlType = reader.GetDataTypeName(i); } catch { sqlType = ""; }
            if (type == typeof(byte[])) type = typeof(string);   // shown as 0x… like SSMS
            columns.Add(new ResultColumn(string.IsNullOrEmpty(name) ? "(No column name)" : name, type, sqlType));
        }

        var capture = new ResultSetCapture(ordinal, columns);
        truncated = false;
        while (reader.Read())
        {
            if (maxRows > 0 && capture.Rows.Count >= maxRows) { truncated = true; break; }
            if ((capture.Rows.Count & 0xFF) == 0) ct.ThrowIfCancellationRequested();

            var values = new object[columns.Count];
            for (int i = 0; i < values.Length; i++) values[i] = ReadValue(reader, i);
            capture.Rows.Add(values);
        }
        return capture;
    }

    /// <summary>A value as the grid shows it. A UDT the extension can't load (geography without its assembly) becomes its text, not a dead cast.</summary>
    private static object ReadValue(SqlDataReader reader, int i)
    {
        try
        {
            if (reader.IsDBNull(i)) return DBNull.Value;
            object value = reader.GetValue(i);
            return value is byte[] bytes ? Hex(bytes) : value;
        }
        catch (Exception ex)
        {
            try { return reader.GetSqlValue(i)?.ToString() ?? ""; }
            catch { return "(unreadable: " + ex.Message + ")"; }
        }
    }

    private static string Hex(byte[] bytes)
    {
        const int MaxBytes = 4096;
        var sb = new StringBuilder("0x", 2 + Math.Min(bytes.Length, MaxBytes) * 2 + 1);
        for (int i = 0; i < bytes.Length && i < MaxBytes; i++) sb.Append(bytes[i].ToString("X2"));
        if (bytes.Length > MaxBytes) sb.Append('…');
        return sb.ToString();
    }

    /// <summary>SSMS's own layout. The line is the script's, not the batch's, so it can be found in the editor.</summary>
    private static string FormatError(SqlError error, int batchStartLine)
    {
        int line = error.LineNumber > 0 ? batchStartLine + error.LineNumber - 1 : 0;
        string header = error.Number == 0 && error.Class == 0
            ? ""
            : $"Msg {error.Number}, Level {error.Class}, State {error.State}{(line > 0 ? $", Line {line}" : "")}\r\n";
        return header + error.Message;
    }

    /// <summary>
    /// Re-pointed at the database. Transaction mode turns pooling off: a pooled connection's Close only returns it to the
    /// pool, where the session (and an open transaction's locks) lives on until it is reused; an unpooled one ends.
    /// </summary>
    private static string ConnectionStringFor(CastTarget target, bool pooled)
    {
        var builder = new SqlConnectionStringBuilder(target.ServerConnectionString)
        {
            InitialCatalog = target.Database,
            ApplicationName = "SQLExtended ScriptCast",
        };
        if (!pooled) builder.Pooling = false;
        return builder.ConnectionString;
    }
}
