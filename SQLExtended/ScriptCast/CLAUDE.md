# ScriptCast: one script, many databases

Issue #13. **SQLExtended > ScriptCast** opens a document-well tab: tick databases across the servers connected in Object
Explorer (or load a Cast List), and **Cast** (or F5 in the pane) runs the query window's script on each, in parallel,
merging the results. Stage 1 is run and merge; stage 2 is transaction mode (Run in transaction, Commit All / Rollback
All, auto rollback, the timeout countdown), below.

## Files

Pure, and linked into the tests (`SQLExtended.Tests/ScriptCast/ScriptCastTests.cs`):
- `BatchSplitter.cs`: `GO` / `GO n` on a line of its own (a trailing `--` comment allowed), **not** inside a block
  comment (T-SQL nests them) or a string or quoted identifier that runs across lines. The formatter's splitter is
  line-only and would cut those in two. Each batch keeps its first script line, so a server's `Line N` is reported as a
  line of the script, not of the batch.
- `CastList.cs`: the JSON model, reader, writer, and `CastServerName`.
- `ScriptCastAnalyzer.cs`: does the script change data or schema, for the Select All confirm; and
  `TransactionBlockers`, what stops it running in transaction mode.
- `ResultMerger.cs`: one grid per result set.

SSMS-only: `CastRunner.cs`, `ScriptCastControl.xaml(.cs)`, `TargetModels.cs`, `ScriptSource.cs`, the command and the
tool window. `CastRunner` was smoke-tested against LocalDB from a scratch console that compiled it with stubs
(stage 2: commit, rollback, auto rollback, no auto rollback, a script's own `EXEC('COMMIT')`, XACT_ABORT, cancel
mid-cast, release on close, a killed session caught by the pre-commit check); it is not in the test project because
it needs a server.

## Targets

- **Servers are Object Explorer's, and only Object Explorer's** (`ObjectExplorerHelper.GetObjectExplorerServers`).
  `GetConnectedServers` falls back to the schema cache's known servers, which would show a server with no live
  connection as connected. Each server's connection string is the one harvested from its OE node (pointing at master),
  re-pointed per database, and **every connection goes through `SqlConnectionFactory`**, so Entra and MFA work.
- Object Explorer raises nothing on connect/disconnect, so the pane **polls the root nodes every 3 s while visible**
  and only re-reads databases for a server that is new. Refresh re-reads them all.
- Databases are read with the pane's own query, not `ObjectExplorerHelper.GetDatabases`: that one hides system
  databases (the pane offers them, hidden by default) and merges in the schema cache's list, which can name a database
  the server no longer has. Only ONLINE databases this login can open are listed.
- A server's tick box covers its **visible** databases, so a filter narrows what it and Select All take. Ticks survive
  a filter change; the status line counts every ticked database, shown or not.

## Cast Lists

The issue's format (`name`, `description`, `servers[].server`, `servers[].databases[]`). Load and Save default to
`Settings > ScriptCast > Default folder`, else `Documents\SQLExtended\Cast Lists` (a share works).

- **A bad entry never fails the list.** It becomes an issue on the banner and the rest loads: a missing server name,
  a non-string or duplicate database, a server listed twice (merged). Only a file that isn't a JSON object with a
  `servers` array is refused. Keys are matched case-insensitively, since these are written by hand.
- **No credentials, either way.** The writer has no field for one; the reader skips any credential-looking key
  (`password`, `user id`, `connectionString`, …) with an issue saying so. Every cast uses the OE connection.
- Server names match through `CastServerName.Normalize`: case, `tcp:`, a default `,1433`, and `.` / `(local)` /
  `localhost` / `127.0.0.1` as one. A CNAME or an AG listener vs a node are different names and stay different.
- A listed server that isn't connected keeps a row with its databases (not tickable) and a **Connect…** button, which
  runs `File.ConnectObjectExplorer`: SSMS's own dialog, not pre-filled. The poll picks the server up once connected and
  ticks the list's databases on it. A listed database the server lacks is shown as "not on this server".
- **Reload is by file watcher, not by hooking the editor's save.** Open JSON opens the file in an SSMS tab; saving it
  there and editing it anywhere else both fire the watcher (debounced 400 ms: editors save in bursts). A save that
  doesn't parse keeps the list on screen and says why in the banner. Loading a list replaces the selection.

## Running

- **The script is the last `.sql` document that had focus** (`ScriptSource`), its selection if there is one. Not
  `DTE.ActiveDocument` at Cast time: the pane is itself in the document well, so clicking Cast changes what is
  active. The toolbar names the document, so which script will run is never a guess.
- One connection and one thread per database, at most `ScriptCastMaxParallelism` (8) at once. The script is sent
  unchanged, batch by batch, with `GO n` repeated.
- **`FireInfoMessageEventOnUserErrors = true`** makes errors up to severity 16 arrive as messages, as SSMS shows them:
  a failed statement doesn't stop the batch's later result sets, and later batches still run. Severity 17+ throws,
  and that ends the batch; the database stops only if the connection closed. A database with any error is **Failed**;
  the others are unaffected (the issue's "always continue").
- `(n rows affected)` comes from `StatementCompleted`; the Summary's Rows affected is the reader's `RecordsAffected`
  (DML only) summed over batches, Rows returned is what was read.
- Cancel cancels every running command (`cmd.Cancel` via the token) and marks databases still waiting for a slot
  Cancelled. Closing the tab cancels too (`ScriptCastToolWindow.Dispose`).
- `ScriptCastMaxRowsPerResultSet` (100,000; 0 = all) caps rows **per database per result set**: everything is held in
  SSMS's memory until the next cast. A database that hits it says so in Messages.

## Confirms

- **Select All + a script that may change data or schema** asks first (default No), per the issue.
  `ScriptCastAnalyzer` is an allow-list: a statement is read-only only if it is a known reading/variable/flow/session
  kind, so a statement type nobody thought of counts as a change. `EXEC` counts (a procedure can do anything). Scratch
  work doesn't: DML into `#temp` or `@table`, `SELECT … INTO #t`, creating or dropping `#temp`. A script that doesn't
  parse (SQLCMD mode included) counts as a change.
- **The query window's UPDATE/DELETE-without-WHERE guard applies to a cast too**, whatever the targets, when
  `UnsafeDmlGuardEnabled` is on. It is `Safety/UnsafeDmlAnalyzer`, reused.

## Results

- **One grid per result set, by position**: result set N of every database merged, servers then databases in name
  order (the default proposed on #13). Shape is column names (case-insensitive) plus SQL types. The shape most
  databases returned is the main grid (a tie goes to the first listed); each other shape is its own tab, flagged, with a
  banner naming its databases. Never padded or coerced into the main grid: a column lined up by position that means
  something else is a wrong answer that looks right.
- Grid columns are bound by position (`c0`, `c1`, …) with the real name as the header: result column names repeat, can
  be empty, and can hold `.` or `[`, each of which breaks a binding path. `ServerName` and `DatabaseName` come first
  and are frozen.
- Cells read like SSMS: `NULL`, bit as 1/0, datetimes to the millisecond, binary as `0x…` (first 4 KB). A value the
  reader can't materialise (a CLR UDT whose assembly isn't loaded) becomes its text, and that column falls back to
  `object` so the DataTable accepts it. Copy takes the full text; the cell shows one line, capped at 1,000 characters.
- Summary (status, duration, rows, errors, first error per database) and Messages (time, server, database, text) fill
  live; result grids are built when the cast finishes, or is cancelled.

## Transaction mode

**Run in transaction** (toolbar, second row) runs each database inside its own transaction and leaves it open: Commit
All / Rollback All decide. Rows affected per database are on the Summary before anyone commits.

- **`BEGIN TRANSACTION` as T-SQL, not `SqlConnection.BeginTransaction`.** With an API transaction SqlClient demands
  `cmd.Transaction` on every command, and one the server rolled back (XACT_ABORT) leaves the object zombied. T-SQL
  behaves as a query window's own BEGIN TRAN does.
- **Pooling is off for transaction-mode connections.** A pooled `Close` only parks the connection in the pool, where
  the session, its open transaction and its locks live on until reuse. Unpooled, closing ends the session and the
  server rolls back. That is what makes closing the tab (or SSMS) safe without a round trip: `Dispose` just closes the
  held connections (`CastRunner.Release`). Process exit does the same.
- **Blocked up front** (`TransactionBlockers`): the script's own `BEGIN TRAN` / `COMMIT` / `ROLLBACK` (`SAVE TRAN` is
  fine), statements SQL Server refuses in a transaction (CREATE/ALTER/DROP DATABASE, BACKUP, RESTORE, full-text catalog
  and index DDL, RECONFIGURE), and a script that doesn't parse.
- **Checked after every batch**: `@@TRANCOUNT` 0 means something ended the transaction (a procedure's COMMIT, XACT_ABORT,
  a severe error) and the next batch would run in autocommit, so the rest isn't run and the database is Failed,
  Transaction "Ended early" (what ran may be committed: it says so). `XACT_STATE()` -1 is Failed and rolled back.
  This is what catches the procedure that commits, which no static check can see.
- **A failed database rolls back at once**; it can never be committed, and it holds locks. The succeeded ones stay
  open. Without auto rollback Commit All is off and the user reviews, then Rollback All (the issue's rule). With
  **Auto rollback** (default on) the first failure cancels the rest and rolls back everything, including databases
  that had already finished. The cancel is raised off-thread: failures arrive in SqlClient's `InfoMessage` callback
  mid-read, and cancelling runs every command's `cmd.Cancel` synchronously. The database that failed reads Failed,
  the ones it stopped Cancelled.
- **Cancel (and Rollback All while casting) rolls back every database**, finished ones too.
- **Commit All is not atomic** (no MSDTC; Azure SQL Database has none). To keep the window small: it first checks every
  held transaction is still open and committable (`VerifyAllAsync`), and commits **nothing** if one isn't. Then it
  commits one database after another, carrying on past a failure: the user asked for all, and the fewer databases left
  to fix by hand the better. Each gets an outcome: Committed; Commit failed (threw with the transaction still open, so
  rolled back); Commit unknown (the connection died during COMMIT: check by hand). A confirm comes first, default No.
- **The timeout counts from the end of the cast, not from Cast.** It bounds locks held while waiting for a person; a
  long script shouldn't eat the time meant for reviewing it. Default `ScriptCastTransactionTimeoutSeconds` 300
  (10 – 86,400), shown in the toolbar box, which overrides it for one cast; a settings change updates the box unless
  transactions are waiting. Expiry rolls back everything. The countdown is stopped during a verify / commit / rollback
  (`_txBusy`) so it never pulls the rug from under a COMMIT; it keeps running behind the Commit All confirm, which
  re-checks afterwards.
- A new cast is refused while transactions are open.

## Theme

The control merges Schema Compare's `UI/Theme.xaml` (implicit templates for buttons, check boxes, text boxes, scroll
bars) and `Monitoring/MonitoringTheme.xaml` (grid, row and tab styles) into its own resources, never Application
resources. Code-built UI (result tabs, banners) uses `SetResourceReference`. `ThemePaletteTests` scans this XAML.

## Not yet observed in a running SSMS

- `File.ConnectObjectExplorer` as the command name for the connect dialog.
- F5 inside the pane: SSMS may take it before WPF sees it (the same open question as Schema Compare's F5).
- Whether `WindowEvents.WindowActivated` hands over the query window's `Document` for every way of switching tabs.
- Transaction mode end to end in the pane (the runner was only exercised from the scratch console), and whether the
  tool window's `Dispose` runs on SSMS exit (if not, process exit closes the connections and the server rolls back).
