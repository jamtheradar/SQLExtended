using Microsoft.Data.SqlClient;
using SQLExtended.Settings;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Threading;

namespace SQLExtended.Decryption;

/// <summary>
/// Reads the text of modules created <c>WITH ENCRYPTION</c> back out, so the schema cache (and through it
/// IntelliSense, search and the schema viewer) and the schema export have a definition to work with instead
/// of a blank.
///
/// How it works is <see cref="EncryptedModuleCrypto"/>'s business; what matters here is the one consequence
/// of it. Recovering the text needs a second ciphertext for the same object, and the only way to get one is
/// to briefly <b>ALTER the object</b> to a throwaway definition. So this code writes to the database, and
/// every rule below exists because of that:
///
/// <list type="bullet">
/// <item><b>The ALTER and its rollback are one server-side batch.</b> The ROLLBACK is not a separate round
/// trip that a dropped connection could strand — and if the connection does drop mid-batch, the engine
/// rolls the transaction back on its own. There is no sequence of failures that leaves the throwaway
/// definition in place.</item>
/// <item><b>It is off by default</b> (<see cref="SQLExtendedSettings.DecryptEncryptedModules"/>). An ALTER
/// takes a schema-modification lock, so it briefly blocks callers of the procedure and recompiles it. That
/// is a decision for whoever owns the server, not a default.</item>
/// <item><b>Results are memoised per object version.</b> The schema cache reloads on a timer; without this
/// a five-minute refresh would take a Sch-M lock on every encrypted procedure in the database, forever. The
/// key includes <c>modify_date</c>, so a module that changes is decrypted again and one that does not is
/// never touched twice.</item>
/// <item><b>One DAC at a time, process-wide.</b> An instance permits a single dedicated administrator
/// connection, so a cache load and an export running together would otherwise fail each other. They queue
/// instead.</item>
/// <item><b>Nothing that fails validation is returned.</b> A wrong assumption anywhere upstream still
/// yields a string; <see cref="EncryptedModuleCrypto.LooksLikeModuleDefinition"/> is what stops noise
/// reaching a cache or a comparison export, where nobody would recognise it.</item>
/// </list>
/// </summary>
internal static class ModuleDecryptionService
{
    /// <summary>
    /// Serialises DAC use across the whole process. The server allows one, and the two callers (schema
    /// cache load, schema export) run on independent background threads.
    /// </summary>
    private static readonly SemaphoreSlim DacGate = new(1, 1);

    /// <summary>
    /// How long to wait for the gate before giving up. Never infinite: a caller that queues behind a slow
    /// run for an unbounded time is indistinguishable from a hang, and one of these callers is a schema
    /// viewer someone is waiting on.
    /// </summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Decrypted text by server|database|schema.name|modify_date. Keyed on the object's version so a
    /// changed module is re-read and an unchanged one is never ALTERed a second time.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> Memo = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Failures, keyed exactly like <see cref="Memo"/>. Remembering these matters as much as remembering
    /// the successes: without it, a module that cannot be read is ALTERed again on every cache refresh and
    /// every hover, each attempt paying the full timeout, for an answer that will not change until the
    /// module does.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> Failures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Servers whose DAC could not be opened, by data source. A login without sysadmin, an instance with
    /// remote admin connections off, or Azure SQL Database will never succeed, and retrying costs the
    /// connect timeout every time — for every object, on every refresh. Recorded once, reported instantly
    /// thereafter, and cleared by <see cref="ClearCache"/>.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> UnavailableServers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the user has turned decryption on. Every automatic caller checks this first.</summary>
    public static bool Enabled => SQLExtendedSettings.Current.DecryptEncryptedModules;

    /// <summary>
    /// Drops everything remembered — successes, failures and unreachable servers — so the next run reads
    /// every module again. Wired to "Clear All Cache"; it is also the only way to retry a server whose DAC
    /// was unavailable earlier in the session.
    /// </summary>
    public static void ClearCache()
    {
        Memo.Clear();
        Failures.Clear();
        UnavailableServers.Clear();
    }

    /// <summary>
    /// Decrypts every module in <paramref name="modules"/>, which must all live in
    /// <paramref name="database"/>. One DAC is opened for the batch — opening one per object would be an
    /// order of magnitude slower and would fight anything else wanting the connection in between.
    /// </summary>
    public static ModuleDecryptionResult Decrypt(
        string connectionString,
        string database,
        IReadOnlyList<EncryptedModule> modules,
        Action<string> progress = null,
        CancellationToken ct = default)
    {
        var result = DecryptCore(connectionString, database, modules, progress, ct);

        // Recorded on every run, including the ones that did nothing. The automatic caller — the schema
        // cache load — has nowhere to put warnings, and for a while it simply dropped them: decryption
        // would fail and there was no way at all to find out why. This is where the answer lives now, and
        // the Schema Cache window reads it.
        if (modules != null && modules.Count > 0)
            LastRun = new ModuleDecryptionDiagnostics(ServerOf(connectionString), database, modules.Count, result);

        return result;
    }

    /// <summary>
    /// What the most recent run did, whether or not anyone was listening. Null until decryption first runs.
    /// </summary>
    public static ModuleDecryptionDiagnostics LastRun { get; private set; }

    private static ModuleDecryptionResult DecryptCore(
        string connectionString,
        string database,
        IReadOnlyList<EncryptedModule> modules,
        Action<string> progress,
        CancellationToken ct)
    {
        var result = new ModuleDecryptionResult();
        if (modules == null || modules.Count == 0) return result;

        if (!Enabled)
        {
            result.DacError = "Decryption of encrypted modules is switched off (SQLExtended Settings ▸ Schema Cache).";
            return result;
        }

        string server = ServerOf(connectionString);

        // Anything already known — decrypted or known-unreadable — is answered without a connection at all.
        // For a repeat cache load of an unchanged database that means no DAC is opened and no object is
        // touched at all.
        var outstanding = new List<EncryptedModule>();
        foreach (var module in modules)
        {
            string key = MemoKey(server, database, module);

            if (Memo.TryGetValue(key, out string cached))
                result.Definitions[module.Key] = cached;
            else if (Failures.TryGetValue(key, out string failure))
                result.Warnings.Add($"{module.Key}: {failure}");
            else
                outstanding.Add(module);
        }

        if (outstanding.Count == 0) return result;

        // Nothing here may run on the UI thread. Opening a DAC is an unpooled connect that goes via SQL
        // Browser, and the ALTER can sit waiting on a schema-modification lock — both are seconds at best,
        // and on the UI thread that is a frozen SSMS with no window to explain itself. Callers do this work
        // on a background thread; this is the backstop that makes a caller which forgets merely wrong
        // rather than fatal.
        // SchemaExporter links this file into a console app, which has no shell and no UI thread to protect.
#if !SCHEMA_EXPORTER
        if (Microsoft.VisualStudio.Shell.ThreadHelper.CheckAccess())
        {
            result.DacError = "Encrypted module text is not decrypted on the UI thread, because opening an administrator "
                            + "connection would freeze SSMS while it waits. Try the action again from a window that loads in "
                            + "the background (the schema cache, or the schema viewer).";
            return result;
        }
#endif

        if (UnavailableServers.TryGetValue(server, out string knownFailure))
        {
            result.DacError = knownFailure;
            return result;
        }

        // Never an unbounded wait: queueing behind another run for an arbitrary time is indistinguishable
        // from a hang to whoever is waiting on the answer.
        if (!DacGate.Wait(GateTimeout, ct))
        {
            result.DacError = "Timed out waiting for the server's single administrator connection, which another "
                            + "decryption run is using. Try again in a moment.";
            return result;
        }

        try
        {
            using var dac = DacConnectionFactory.Open(connectionString, database);

            foreach (var module in outstanding)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke($"Decrypting {module.Key}…");

                try
                {
                    string definition = DecryptOne(dac, module);
                    if (definition == null)
                    {
                        Fail(server, database, module, result, "the recovered text was not a module definition, so it was discarded.");
                        continue;
                    }

                    Memo[MemoKey(server, database, module)] = definition;
                    result.Definitions[module.Key] = definition;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Fail(server, database, module, result, RootMessage(ex));
                }
            }
        }
        catch (DacUnavailableException ex)
        {
            // One failure for the whole batch, reported once, and remembered — a server that cannot give us
            // a DAC will not start doing so, and retrying costs the connect timeout per object per refresh.
            result.DacError = ex.Message;
            UnavailableServers[server] = ex.Message;
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
        }
        finally
        {
            DacGate.Release();
        }

        return result;
    }

    /// <summary>
    /// Convenience for the single-object paths (the schema viewer). Returns null when decryption is off,
    /// unavailable, or the object could not be read; <paramref name="error"/> then says which.
    /// </summary>
    public static string DecryptSingle(string connectionString, string database, EncryptedModule module, out string error)
    {
        error = null;
        if (module == null) return null;

        var result = Decrypt(connectionString, database, new[] { module });
        if (result.Definitions.TryGetValue(module.Key, out string definition)) return definition;

        error = result.DacError ?? (result.Warnings.Count > 0 ? result.Warnings[0] : "The module could not be decrypted.");
        return null;
    }

    /// <summary>
    /// Lists the encrypted modules of the database <paramref name="connection"/> is pointed at. In
    /// <c>sys.sql_modules</c> a NULL definition means exactly one thing — the module is encrypted. A caller
    /// without VIEW DEFINITION does not get a NULL, it gets no row.
    /// </summary>
    public static List<EncryptedModule> ListEncryptedModules(SqlConnection connection, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT s.name AS schema_name, o.name AS object_name, o.type, o.modify_date,
                   ps.name AS parent_schema, po.name AS parent_name
            FROM sys.sql_modules m
            JOIN sys.objects o ON m.object_id = o.object_id
            JOIN sys.schemas s ON o.schema_id = s.schema_id
            LEFT JOIN sys.objects po ON o.parent_object_id = po.object_id
            LEFT JOIN sys.schemas ps ON po.schema_id = ps.schema_id
            WHERE m.definition IS NULL AND o.is_ms_shipped = 0";

        var modules = new List<EncryptedModule>();
        using var cmd = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            modules.Add(new EncryptedModule
            {
                Schema = reader.GetString(0),
                Name = reader.GetString(1),
                ObjectType = reader.GetString(2)?.Trim(),
                ModifyDate = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3),
                ParentSchema = reader.IsDBNull(4) ? null : reader.GetString(4),
                ParentName = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }

        return modules;
    }

    /// <summary>
    /// Reads one module's ciphertext, overwrites it with a dummy inside a transaction to get a second
    /// ciphertext for the same keystream, rolls back, and XORs the two together. Returns null if what comes
    /// out is not a module definition.
    /// </summary>
    private static string DecryptOne(SqlConnection dac, EncryptedModule module)
    {
        if (!string.Equals(dac.Database, module.Database ?? dac.Database, StringComparison.OrdinalIgnoreCase))
            dac.ChangeDatabase(module.Database);

        byte[] original = ReadCipher(dac, module, dummyDefinition: null, out _);
        if (original == null || original.Length < 2)
            throw new InvalidOperationException("No encrypted text was found for the object.");

        string body = EncryptedModuleCrypto.BuildDummyBody(
            module.ObjectType, module.Schema, module.Name, module.ParentSchema, module.ParentName, original.Length / 2);

        if (body == null)
            throw new InvalidOperationException($"Objects of type '{module.ObjectType}' are not supported.");

        // Executed with ALTER (the object exists), but XORed against the CREATE form — that is what the
        // engine stores and therefore what was encrypted. Using the ALTER text here decrypts the first five
        // characters and then runs one character out of step for the whole module.
        byte[] dummyCipher = ReadCipher(dac, module, EncryptedModuleCrypto.AlterStatement(body), out byte[] recheckedOriginal);

        // The re-read of the original comes from the same batch as the ALTER, so a module someone changed
        // between the two round trips is caught rather than decrypted against a stale ciphertext.
        string definition = EncryptedModuleCrypto.Xor(
            recheckedOriginal ?? original, dummyCipher, EncryptedModuleCrypto.StoredPlaintext(body));

        return EncryptedModuleCrypto.LooksLikeModuleDefinition(definition, module.Name) ? definition : null;
    }

    /// <summary>
    /// One round trip. With <paramref name="dummyDefinition"/> null it just reads the current ciphertext;
    /// with one supplied it reads the current ciphertext, applies the dummy, reads the ciphertext again and
    /// rolls back — all inside a single batch, so the object is never left altered.
    ///
    /// The table variable is what makes that possible: table variables are not transactional, so the rows
    /// read inside the transaction survive the ROLLBACK that discards the ALTER.
    /// </summary>
    private static byte[] ReadCipher(SqlConnection dac, EncryptedModule module, string dummyDefinition, out byte[] originalAgain)
    {
        const string readOnly = @"
SET NOCOUNT ON;
DECLARE @objid int = OBJECT_ID(@qualified);
IF @objid IS NULL
BEGIN
    RAISERROR('The object no longer exists.', 16, 1);
    RETURN;
END;
-- CAST, not a bare 0: an integer literal is an int, and the reader addresses this column as a byte to
-- match the table-variable column the other batch returns.
SELECT CAST(0 AS tinyint) AS which, valnum, CAST(imageval AS varbinary(max)) AS val
FROM sys.sysobjvalues
WHERE valclass = 1 AND objid = @objid AND subobjid = 1
ORDER BY valnum;";

        // LOCK_TIMEOUT is not a nicety. The ALTER needs a schema-modification lock, so it waits behind any
        // session currently running the module — and while it waits it blocks every new caller of that
        // module behind itself. Failing the object after five seconds is the only acceptable behaviour on a
        // server anyone is using; without it, reading a procedure's text could stall the procedure.
        const string alterAndRead = @"
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET LOCK_TIMEOUT 5000;
DECLARE @objid int = OBJECT_ID(@qualified);
IF @objid IS NULL
BEGIN
    RAISERROR('The object no longer exists.', 16, 1);
    RETURN;
END;

DECLARE @blobs TABLE (which tinyint NOT NULL, valnum int NOT NULL, val varbinary(max) NULL);

INSERT @blobs (which, valnum, val)
SELECT 0, valnum, CAST(imageval AS varbinary(max))
FROM sys.sysobjvalues
WHERE valclass = 1 AND objid = @objid AND subobjid = 1;

BEGIN TRY
    BEGIN TRAN;

    EXEC sp_executesql @dummy;

    INSERT @blobs (which, valnum, val)
    SELECT 1, valnum, CAST(imageval AS varbinary(max))
    FROM sys.sysobjvalues
    WHERE valclass = 1 AND objid = @objid AND subobjid = 1;

    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    THROW;
END CATCH;

SELECT which, valnum, val FROM @blobs ORDER BY which, valnum;";

        bool altering = dummyDefinition != null;

        using var cmd = new SqlCommand(altering ? alterAndRead : readOnly, dac) { CommandTimeout = 30 };
        cmd.Parameters.Add("@qualified", SqlDbType.NVarChar, 776).Value = EncryptedModuleCrypto.Quote(module.Schema, module.Name);
        if (altering)
            cmd.Parameters.Add("@dummy", SqlDbType.NVarChar, -1).Value = dummyDefinition;

        var originalChunks = new List<byte[]>();
        var dummyChunks = new List<byte[]>();

        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.IsDBNull(2)) continue;
                var bytes = (byte[])reader.GetValue(2);
                (reader.GetByte(0) == 0 ? originalChunks : dummyChunks).Add(bytes);
            }
        }

        originalAgain = altering ? EncryptedModuleCrypto.Concat(originalChunks) : null;
        return altering ? EncryptedModuleCrypto.Concat(dummyChunks) : EncryptedModuleCrypto.Concat(originalChunks);
    }

    /// <summary>
    /// Records a per-object failure and remembers it against that object's version, so the next refresh
    /// reports it from memory instead of ALTERing the module again for the same answer.
    /// </summary>
    private static void Fail(string server, string database, EncryptedModule module, ModuleDecryptionResult result, string reason)
    {
        Failures[MemoKey(server, database, module)] = reason;
        result.Warnings.Add($"{module.Key}: {reason}");
    }

    private static string MemoKey(string server, string database, EncryptedModule module)
        => $"{server}|{database}|{module.Key}|{module.ModifyDate?.Ticks ?? 0}";

    private static string ServerOf(string connectionString)
    {
        try { return new SqlConnectionStringBuilder(connectionString).DataSource; }
        catch { return connectionString ?? ""; }
    }

    private static string RootMessage(Exception ex)
    {
        while (ex.InnerException != null) ex = ex.InnerException;
        return ex.Message;
    }
}

/// <summary>One module defined WITH ENCRYPTION, and what is needed to write a valid dummy for it.</summary>
internal sealed class EncryptedModule
{
    public string Schema { get; set; }
    public string Name { get; set; }

    /// <summary>Type code from sys.objects — P, V, FN, IF, TF, TR. Decides the shape of the dummy.</summary>
    public string ObjectType { get; set; }

    /// <summary>Owning table of a DML trigger. ALTER TRIGGER has to name it and cannot change it.</summary>
    public string ParentSchema { get; set; }
    public string ParentName { get; set; }

    /// <summary>Version stamp for the memo — a module that changes is decrypted again.</summary>
    public DateTime? ModifyDate { get; set; }

    /// <summary>Set only when a batch spans databases; otherwise the connection's database is used.</summary>
    public string Database { get; set; }

    public string Key => $"{Schema}.{Name}";
}

/// <summary>
/// A plain-English account of the last decryption run, for the callers that have no other way to report
/// one — chiefly the schema cache load, which runs on a timer with no UI attached. Without this, a run that
/// decrypted nothing and a run that was never attempted look identical from the outside, which is exactly
/// the state "the decrypt isn't working" leaves you in.
/// </summary>
internal sealed class ModuleDecryptionDiagnostics
{
    public ModuleDecryptionDiagnostics(string server, string database, int requested, ModuleDecryptionResult result)
    {
        Server = server;
        Database = database;
        Requested = requested;
        Succeeded = result.Definitions.Count;
        DacError = result.DacError;
        Warnings = new List<string>(result.Warnings);
        WhenUtc = DateTime.UtcNow;
    }

    public string Server { get; }
    public string Database { get; }
    public int Requested { get; }
    public int Succeeded { get; }
    public string DacError { get; }
    public IReadOnlyList<string> Warnings { get; }
    public DateTime WhenUtc { get; }

    /// <summary>Set once the message has been put in front of the user, so it is not re-stuck every rebuild.</summary>
    public bool Reported { get; set; }

    public bool HasProblem => Succeeded < Requested;

    /// <summary>
    /// One line, leading with the count so a partial success is not mistaken for a total one, and carrying
    /// the first actual reason — a count with no cause is not diagnosable.
    /// </summary>
    public string Summary
    {
        get
        {
            string scope = string.IsNullOrEmpty(Database) ? Server : $"{Server}/{Database}";
            string head = $"Encrypted modules on {scope}: {Succeeded} of {Requested} decrypted";

            if (!string.IsNullOrEmpty(DacError)) return $"{head} — {DacError}";
            if (Warnings.Count > 0) return $"{head} — {Warnings[0]}" + (Warnings.Count > 1 ? $" (+{Warnings.Count - 1} more)" : "");
            return head + ".";
        }
    }
}

/// <summary>What a decryption run produced, and what it could not.</summary>
internal sealed class ModuleDecryptionResult
{
    /// <summary>Decrypted text by "schema.name". Only entries that passed validation appear.</summary>
    public Dictionary<string, string> Definitions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-object failures. One unreadable module costs that module, not the run.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Set when no DAC could be opened, in which case nothing was decrypted at all.</summary>
    public string DacError { get; set; }

    public bool Cancelled { get; set; }
}
