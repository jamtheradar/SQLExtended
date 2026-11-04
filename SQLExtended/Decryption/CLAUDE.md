# Encrypted Modules

`Decryption/` recovers the text of modules created `WITH ENCRYPTION`, so `CachedObject.Definition` holds a
body instead of a blank. Its consumers are the schema viewer, the SQLite FTS index behind SQL Search's
"search in definitions", and the schema export. **Not completion** — `SqlCompletionSource` never reads
`Definition`; it works off `sys.columns`/`sys.parameters`, which encryption does not hide. Wiring this to
"IntelliSense" is a natural assumption and a wrong one.

**There is no key.** SQL Server XORs module text against a keystream derived from (database family GUID,
object id, subobject id). Nothing in that derivation depends on the text or its length, so two different
definitions of the *same object* are masked by the same keystream and
`plain = cipher_original XOR cipher_dummy XOR plain_dummy` recovers the original a character at a time. The
second ciphertext is obtained by briefly **ALTERing the object** to a throwaway definition. That single fact
drives everything else:

- **The ALTER and its ROLLBACK are one server-side batch**, with the blobs read into a table variable
  (table variables are not transactional, so the rows survive the rollback that discards the ALTER). There
  is no sequence of failures that leaves the dummy in place — a dropped connection mid-batch is rolled back
  by the engine.
- **It is off by default** (`SQLExtendedSettings.DecryptEncryptedModules`). The ALTER takes a Sch-M lock and
  recompiles the module, and the DAC needs sysadmin. That is the server owner's call, not a default.
- **Results are memoised per object version** (`server|db|schema.name|modify_date`), and so are **failures**
  — per object, and per server for a DAC that could not be opened at all. The cache reloads on a timer;
  without the positive memo a five-minute refresh would take a Sch-M lock on every encrypted procedure
  forever, and without the negative ones a login that is not sysadmin would pay the full connect timeout per
  object per refresh for an answer that cannot change. `SchemaCache.ClearAll` clears all three explicitly
  (they are not keyed by database), and is the only way to retry a server that failed earlier in the session.
- **One DAC at a time, process-wide** (`ModuleDecryptionService.DacGate`). The instance permits exactly one,
  and a cache load and an export on separate threads would otherwise fail each other. `DacConnectionFactory`
  also forces `Pooling=false` — a pooled DAC stays open after `Dispose` and refuses the next attempt,
  including SSMS's own `ADMIN:` query windows — and strips any explicit `,port`, because the DAC listens on
  its own port resolved by SQL Browser from the instance name.
- **Nothing that fails validation is returned.** If any assumption is wrong the XOR still yields a string,
  and noise reaching the cache or a comparison export is the one failure nobody would catch by reading it.
  `LooksLikeModuleDefinition` requires the text to open with CREATE/ALTER (past any leading comment) and to
  mention the object it claims to be.

**Nothing here may touch the UI thread.** This froze SSMS once and the two causes are both structural:

- Opening a DAC is an unpooled connect via SQL Browser, and the ALTER can queue behind a session running
  the module — seconds each, and on the UI thread that is a hung SSMS with no window to explain itself.
  `Decrypt` therefore **refuses to run when `ThreadHelper.CheckAccess()` is true** and says so, so a caller
  that forgets is merely wrong rather than fatal. Every path that builds a schema script
  (`SchemaViewerCommand`, `SchemaQuickInfoSource.OnTooltipClicked`, `SchemaDialog.Refresh_Click`,
  `SqlSearchControl.ViewSchemaDialogForSelectedResult`, `SchemaValidationControl.OpenReferencing`) now does
  it in `Task.Run` and switches back to show the dialog. They were all synchronous before; that was already
  wrong and this made it fatal.
- **`SET LOCK_TIMEOUT 5000` in the ALTER batch is not a nicety.** The ALTER needs a Sch-M lock, so it waits
  behind anyone executing the module — and *while it waits it blocks every new caller of that module behind
  itself*. Reading a procedure's text must never be able to stall the procedure. Same reason `DacGate` is
  waited on with a timeout rather than indefinitely.

Related, and the other half of the same bug report: **`SchemaDialog` gives itself the shell's window as
owner in `OnSourceInitialized`** when the caller supplied none. An unowned modal WPF window shown from the
shell can be placed *behind* the main window — still modal, so SSMS stops responding with nothing visible to
explain it, which is reported as a hang. Most callers cannot supply an owner: `Window.GetWindow` returns
null for a WPF control hosted in a VS tool window, and a command handler has no WPF window at all.

**The statement is executed with `ALTER` but XORed against `CREATE`.** This is the one detail that decides
whether any of it works, and it shipped wrong once. The object exists, so the throwaway definition has to be
applied with ALTER — but what the engine stores, encrypts, and hands back is the **`CREATE` form of the same
statement**. `ALTER` is one character shorter, so reconstructing the plaintext from the executed text
decrypts the leading keyword and then runs one character out of step for the entire module. It still returns
a string. `EncryptedModuleCrypto` therefore builds a keyword-less *body* and exposes
`AlterStatement(body)` / `StoredPlaintext(body)`; never assemble either as one string. This is empirical,
confirmed against a live instance, and it is why every published version of the technique
([jongurgul.com/blog/sql-object-decryption](http://jongurgul.com/blog/sql-object-decryption/)) is structured
the same way. `EncryptedModuleCryptoTests` pins it with a test that asserts the ALTER-form mask produces
garbage.

Two more things that are easy to get wrong:

- **The dummy's shape is per object type, not generic.** `ALTER FUNCTION` cannot move a function between its
  scalar, inline and multi-statement forms, and `ALTER TRIGGER` must name the table it is already on and
  cannot change it — hence `ParentSchema`/`ParentName` on `EncryptedModule` and a null dummy (reported, not
  guessed) for a trigger whose table is unknown. The dummy is run through `sp_executesql`, so names go
  through `EncryptedModuleCrypto.Quote`, which doubles `]`.
- **Matching the original's length is not actually required** — the keystream is position-based — but the
  dummy is padded to it anyway: it costs nothing, it is what every published version of this technique does,
  and it keeps the two ciphertexts the same size so a length mismatch stays a real signal. A dummy that is
  *longer* than the original (a short view, a trigger on a long table name) is fine; a shorter one returns
  null rather than a partial decryption, which would be indistinguishable from a truncated definition.

Deriving the keystream from the family GUID directly would avoid the ALTER entirely, but the byte layout
that feeds it is undocumented and could not be verified here — a wrong guess produces plausible garbage. The
ALTER route is verifiable against any instance, so it is the one implemented. Don't swap it for the other
without a live instance to check against.

`SoluitionDocs/Queries/decrypt-module-probe.sql` does the whole thing in pure T-SQL, printing a verdict at
each of four stages. Reach for it before debugging the C#: it separates "does the technique work on this
instance" from "is the plumbing right", and it is how the CREATE/ALTER asymmetry above was pinned down.

`Decryption/EncryptedModuleCrypto.cs` and `DacConnectionFactory.cs` are free of SMO and WPF so the test
project can link them (`SQLExtended.Tests/Decryption/EncryptedModuleCryptoTests.cs`); the tests use a
stand-in keystream, which is exactly what the technique assumes about the real one.

In the **export** (`Export/SchemaExportService.cs`), encrypted objects are pulled out before the scripter
sees them. SMO does not throw on one — it scripts a comment saying the object is not transferable — and that
comment is byte-identical for every encrypted object, so an export that leaves them in writes files that
make two servers' differing procedures **compare equal**. When the text cannot be recovered the folder export
writes **no file** and records a warning; the single-file export names the object in a comment. Decrypted
text has its leading `ALTER` rewritten to `CREATE` (`NormalizeToCreate`) because what comes back is whatever
was last submitted, and SMO normalises this for everything it scripts itself. A script that still contains
SMO's marker is warned about after the fact.

**Encrypted DML triggers don't produce the marker; they throw.** SMO reads the trigger's `TextHeader` while
scripting its table, gets "The text is encrypted", and the exception takes the whole table with it. The folder
export lost that table's file (it hit 3 tables in every SYTELINE database), and the single-file export lost the
whole batch. `EncryptedTriggersByTable` therefore finds them up front with **one** catalog query
(`OBJECTPROPERTY(…, 'IsEncrypted')`), not by walking each table's `Triggers`, which would be a round trip per
table. Those tables are scripted with `Triggers = false`. Their triggers are then appended: plain ones by SMO,
encrypted ones decrypted (as `"TR"` modules carrying `ParentSchema`/`ParentName`) and followed by SMO's
`ENABLE`/`DISABLE TRIGGER` line. One that could not be decrypted becomes a fixed comment plus a warning. That
departs from "no file at all" for modules on purpose: the file stands for the table, and losing all of it over
one trigger is the larger gap.
