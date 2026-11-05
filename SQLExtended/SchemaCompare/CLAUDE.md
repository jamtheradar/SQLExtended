# SchemaCompare

Compares two schemas side by side, like WinMerge, where each side is a folder export (the Schema Cache window or
SchemaExporter), an SSDT / SDK-style database project, any folder of .sql files, or a live SQL Server database.
`SchemaCompare/README.md` (repo root) covers use; this covers the rules.

## One compare, two hosts

The code lives here, in the extension (net48), and runs in two places:

- **Inside SSMS**: SQLExtended > Schema Compare opens `Ssms\SchemaCompareToolWindow` in the document well.
- **Standalone**: `SchemaCompare\` at the repo root is a thin net48 WinExe that **links** `Engine\*.cs` and `UI\*` (code and
  XAML, keeping the folder shape so `Source="Theme.xaml"` resolves). Its own `CLAUDE.md` covers the shell.

Everything that differs between them goes through `UI\ICompareHost` (`CompareHost.Current`, set before the view is
built): the colours, the settings object, choosing and preparing a database side, opening a file. `Ssms\SsmsCompareHost`
and the shell's `ShellCompareHost` are the two implementations. **Nothing in `Engine\` or `UI\` may reference SSMS
types** (`ThemeManager`, `ConnectionHelper`, `VsShellUtilities`, the package): the shell links those folders and would
stop building. SSMS-only code goes in `Ssms\`, which the shell doesn't link.

## Objects are matched by name, never by file

The export writes `Tables\dbo.FeeEstimate.sql`, a project `dbo\Tables\FeeEstimate.sql`, and SMO puts a table's triggers,
indexes, defaults and FKs in the table's file as separate statements where a project writes them inline or elsewhere.
So `ObjectExtractor` parses every file and gives each statement to the object it defines or changes:

- `ALTER TABLE` (ADD constraint/default, CHECK/NOCHECK CONSTRAINT, SET), `CREATE INDEX`/statistics and
  `sp_addextendedproperty` are **children** of their table (or view, or whatever level1type names). Children are
  attached after every definition is read, so an index in one file finds a table in another.
- **Triggers are objects of their own.** `ALTER TABLE … ENABLE/DISABLE TRIGGER` and `DISABLE TRIGGER` go to the trigger.
- `SET ANSI_NULLS` and similar attach to the next object in the file (ignored by default).
- A file that doesn't parse is re-parsed batch by batch; a batch that still fails is compared as text and matched by a
  regex over its `CREATE … PROCEDURE/VIEW/…` header, and listed as a warning under the path. SQLCMD (`:r`, `$(Var)`) is
  made parseable first (`PrepareSqlCmd`).
- Anything else is keyed by statement type plus the first name in it, so it still pairs up.

`SourceLoader`: a folder with exactly one `.sqlproj` is that project. A legacy project reads its `<Build Include>` items; a
legacy project with **none** (a fresh Import Database, as `MyGenea.Database` was) and an SDK-style project read every
`.sql` under the folder. `bin\`/`obj\`, `PreDeploy`/`PostDeploy`/`None` items are never schema. `*.sql` is filtered on the
extension, as in the export, because Windows matches it against `.sqlproj`.

## A live database is an export folder

A database side is written `mssql://server/database` in the source box (`UI\DatabaseSide.cs`; the server can't hold
`/`, so the first one splits it). It is **scripted with the extension's own `SchemaExportService.ExportToFolder`** (in
the shell, linked as SchemaExporter links it, with `SCHEMA_EXPORTER` defined and SchemaExporter's `DecryptionSettings`
stand-in, so decryption stays off: it ALTERs the module) into `%LOCALAPPDATA%\SQLExtended\SchemaCompare\Databases\<side>\`,
and that folder is read like any export. So "compare against the server" and "compare against an export of it" can't disagree,
and the engine never learns about SQL. SMO and SqlClient versions track `SQLExtended.csproj` for the same reason.

- **The side's cache folder is deleted before every export.** A script left from the last run reads as an object the
  database still has. One folder per side, so the same database on both sides doesn't race itself.
- **What the export couldn't write is a warning on the set** (`UnreadFolders`, `UnwrittenFiles`, a failed database
  throws). An object missing from the folder reads as "only on the other side", the error nobody would catch.
- Every F5 re-scripts; there is no cache across compares. A stale side is worse than a slow one.
- **How a side signs in is the host's.** Before a compare the view asks `PrepareDatabase` for each `mssql://` side on
  the UI thread, then takes `ConnectionStringFor` there too, so nothing on the background thread touches the host.
  - **SSMS** has no login dialog and saves nothing: a side uses a connection SSMS already has to that server — the
    active query window's, or one the schema cache has seen this session (`SsmsCompareHost.KnownServers`). A server from
    the history with no such connection is refused, with a message saying to connect to it first. The picker lists
    databases through `SqlConnectionFactory`, so an Entra token attaches.
  - **The shell** saves sign-ins per server (`ShellSettings.Servers`: Windows, SQL login, Entra MFA, Entra Integrated).
    A SQL password is kept only with "Remember password", DPAPI-encrypted for the Windows user; otherwise
    `PrepareDatabase` asks for it. Entra needs `Microsoft.Data.SqlClient.Extensions.Azure`. Plain `new SqlConnection`
    there: there's no SSMS to borrow a token from.
- **The export opens its own SMO connection from the string** (`new ServerConnection { ConnectionString }`), so inside
  SSMS an Entra side whose only credential is a token fails to script. The Schema Cache window's export has the same
  limit. Not fixed yet.
- `DatabaseSide` is UI-side on purpose: it needs SMO, and `Engine\*.cs` is linked into the net48 tests. The engine's
  only part in it is `SourceKind.Database`.

## Two ways to normalise, on purpose

- **Tables and other declarative objects are rebuilt from the parse tree** (`Normalizer.TableModel`) into one canonical
  shape: defaults back on their column, every other constraint inside the CREATE TABLE sorted by kind then name, indexes
  after it sorted by name, then disabled constraints, then extended properties. Text comes from ScriptDom's generator,
  so layout, keyword case and bracketing can't differ. Comments inside a CREATE TABLE are lost; the Original view has them.
- **Modules keep the author's text**, filtered on the token stream (comments, whitespace, brackets, semicolons,
  CREATE OR ALTER). Regenerating them would hide real edits behind the generator's layout. Token-level, so `--` in a
  string or `[` in a comment is never touched.

Each statement is **parsed again** at normalise time rather than reusing the extractor's tree: the normaliser mutates the
tree, and an options change must start from the original. Parsing is the cost of an options change; it runs in parallel.

Things that are always treated as equal, because they are the same object, not an option: `WITH CHECK ADD` + `CHECK
CONSTRAINT` (enabled is the default), FK `NO ACTION`, a missing sort order (ASC), PK clustered / UNIQUE nonclustered when
unstated, a missing NULL/NOT NULL (NULL, or NOT NULL for a key column), `IDENTITY` without seed, and default sizes
(`FLOAT(53)`, `DATETIME2(7)`, `DECIMAL(18,0)`, length 1). Each was a difference on the MyGenea Prod export vs project
until it was added; HangFire's `FLOAT` vs `FLOAT (53)` was the last.

## Options

`CompareOptions` is the one list; the options panel binds to it by property name and `CompareSettings` stores it as JSON
(Newtonsoft) in `%APPDATA%\SQLExtended\SchemaCompare\settings.json`. **Both hosts share that file**, so options and
history carry between SSMS and the shell; the shell's own entries (theme, sign-ins) ride through an SSMS save in
`CompareSettings.Extra` (`[JsonExtensionData]`). A new option needs: the property, its handling in `Normalizer`, a
checkbox in `UI\SchemaCompareView.xaml`, and a test that it lets its difference through when off
(`SchemaCompareTests.EachTableOption_WhenOff_LetsItsDifferenceThrough` is the pattern). Ignore case and ignore whitespace
are applied by `LineDiff` as line equality, so the text shown keeps its own case.

**Every default must still let a real difference through.** The point of the tool is the differences it shows; an
option that also hides a real change is worse than the noise. The non-default index option (FILLFACTOR = 80), a disabled
constraint and a disabled trigger are tested to stay different under the defaults.

`EquivalentNames` (`left = right`) rewrites right-side names before comparing: cross-database references that differ by
environment (`[GENGER.Patient]` vs `[GENGER-uat.Patient]`, the two remaining view differences on MyGenea) or a project's
`$(SqlCmdVar)`.

## Change scripts (`ChangeScripter`)

- **The compare's options decide what is scripted.** Each table is read twice through `Normalizer.BuildTable`: with
  the compare's options (to decide whether an element differs) and with every ignore off (the text that is emitted).
  The two models are built from the same statements, so their columns, constraints and indexes line up by position;
  indexes and extended properties are only diffed when the compare includes them. Don't diff on the faithful model:
  the script would "fix" noise the list doesn't show.
- **Order is by phase, not by object** (the constants at the top): FK drops, module drops, table drops, table-part
  drops, other drops, schemas/types/sequences/synonyms, tables, modules (dependency-sorted by name), FK adds, the rest.
  Table drops precede table-part drops so a dropped table's FKs (often unnamed) are gone before a key they reference is
  rebuilt.
- **Anything leaning on a changed column is dropped and put back** (indexes, constraints, defaults), and a rebuilt PK
  or unique key takes every target FK that references it, on any table, with it (`ReferencingForeignKeys`).
- **Unnamed constraints are dropped by looking up the server's name** (defaults by column, PK by type, FKs by table,
  referenced table and exact column set). `EXEC()` can't take `QUOTENAME(...)` in its argument; build the statement in
  a variable and `sp_executesql` it. That shipped broken until it was run on LocalDB.
- **Nothing unalterable is guessed**: changed types/sequences/schemas, IDENTITY, column order and table options become
  `-- REVIEW` comments with the source's definition. Data loss is a `-- WARNING`. Both go in `ChangeScript.Warnings`
  and the header.
- The transaction wrapper is `XACT_ABORT` plus `IF @@ERROR <> 0 SET NOEXEC ON` after every batch, and a final batch
  that reads whether `SET @Succeeded = 1` ran. Under NOEXEC the later batches still compile, so a failure is followed
  by compile errors against the unchanged schema; the first error is the real one.
- `EquivalentNames` run both ways: `MapRightNames` for comparing, and for emitting `MapLeftNames` when the left is the
  source (its text goes to the right's environment).
- Tests: `ChangeScriptTests`. Text only; a LocalDB round trip (build the target, run the script with rows in the tables,
  then make one step fail and check the rollback) is the check that the T-SQL is valid. Do it again after any change
  to the emitted statements.

## The engine is linked into net48 tests

`Engine\*.cs` is pure ScriptDom and file I/O and is linked into `SQLExtended.Tests` (net48). Everything in it must
compile on net48. Everything here is net48 now (the extension, the shell and the tests): no records or `init`, no ranges,
no newer BCL overloads (`string.Contains(char)`, `GetValueOrDefault`), no `Dispatcher.BeginInvoke(() => …)` (wrap it in
`new Action`), and no WPF `OpenFolderDialog` (`System.Windows.Forms.FolderBrowserDialog` instead). `CompareOptions` caches compiled regexes and is read from
`Parallel.For`, hence its lock; clone it before handing it to a background compare.

## UI

- `DiffView` puts filler lines where a side has nothing, so row N is line N on both sides and scroll sync is a plain
  offset copy. `RowNumberMargin` shows each side's own line numbers (none on filler). The overview bar between the
  panes marks every difference across the whole text.
- Highlighting is the extension's `Search\TsqlDarkHighlighting.xshd`, as is for dark and light-swapped for light
  (`UI\SqlHighlighting.cs`, the same map as `SQLExtended\Theme\TsqlHighlighting.cs`; keep the two in step). It is looked
  up by the extension's resource name, which the shell gives its link as `LogicalName`.
- The view is a `UserControl` (`UI\SchemaCompareView`); the tool window and the shell's `MainWindow` are only frames.
  Its state is saved on `Unloaded` and by the shell on close (`SaveState`).
- **Its shortcuts are WPF `KeyBinding`s on the view.** Inside SSMS a chord the shell binds globally may be taken by
  the shell before WPF sees it (F5 is the one to watch). Not yet observed in a running SSMS.

## Theme

- **The palette is the extension's**, diff colours included (`SqlxDiff…` in `ThemePalette`). Inside SSMS `ThemeManager`
  installs it; the shell's `AppTheme` installs the same roles. `SQLExtended\Theme\CLAUDE.md`'s rules apply, and
  `ThemePaletteTests` now scans this folder: every colour in XAML is `{DynamicResource Sqlx…}`, and a key the palette
  doesn't define fails the build.
- **`UI\Theme.xaml` (full templates for every control used) is merged into the view's and each window's own
  `Resources`, never Application resources inside SSMS**: there it would restyle every WPF control in the shell. The
  shell's App also merges it application-wide, which its `ConnectWindow` relies on. The stock templates paint popups,
  scroll bars and check marks with system colours, which stay white in dark; a new control type needs a template there.
- **Brushes handed out as values don't follow a swap.** `DiffView` reads `CompareHost.Current.GetBrush` at paint time
  and redraws on `ThemeChanged`, and swaps the highlighting definition. The list's status brushes are rebuilt by
  `RefreshList`. Subscriptions are held only while loaded: the host outlives a closed tool window.
- The title bar follows through `UI\WindowTitleBar` (`DwmSetWindowAttribute(20)`, Windows 10 20H1+).
- Only the shell has a theme switch (`ICompareHost.HasThemeSwitch`); `SchemaCompare\CLAUDE.md` covers "Match SSMS".
- Shortcuts: F5 compare, Alt+Down/Up (or F8/Shift+F8) next/previous difference (moving on to the next object after the
  last), Ctrl+Alt+Down/Up next/previous object.
