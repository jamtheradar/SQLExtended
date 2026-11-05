# SchemaExporter

A .NET 10 console app (not part of the VSIX) that runs the Schema Cache window's folder export unattended for a list
of servers and commits the result to git. `README.md` covers setup and scheduling; this covers the rules.

## The exporter is linked, never copied

`SchemaExportService.cs`, `ExportFileNaming.cs` and the three `Decryption/` files are `<Compile Include Link>`ed
from `SQLExtended/`. The history only has value if it can be folder-compared against an export someone takes by
hand in SSMS. A copy would drift the first time either side changed. Read `SQLExtended/Export/CLAUDE.md`
before changing what gets written. Every rule there (no volatile content, CRLF with a BOM, SMO's statement
order, `NoCollation` off) applies here too.

What the linked files need from the shell is handled in two ways:

- **`SCHEMA_EXPORTER` is defined** and compiles out the code that can't build here: the
  `ThreadHelper.CheckAccess()` guard in `ModuleDecryptionService` (there is no UI thread), and
  `SchemaExportService.ExportObject` (it depends on the schema viewer's `SchemaQueryService`).
- **`DecryptionSettings.cs` is a stand-in `SQLExtended.Settings.SQLExtendedSettings`** (SchemaCompare links it too,
  for its live-database sources, along with the same exporter files and the same `SCHEMA_EXPORTER` define). The decryption service
  only reads `Current.DecryptEncryptedModules`. The real settings file is in the *user's* %APPDATA%, which is the
  wrong place for a service account, so `ServerExporter` sets this value per server from the config.

The package versions (SMO, SqlClient) track `SQLExtended.csproj`. If they differ, the two exports can script
differently. The extension is net48 and this is net10.0, using SMO's own net10.0 build of the same version, so
**every linked file must compile on both frameworks**. A newer API in a linked file builds here and breaks the
VSIX, or the other way round. Build the whole solution after touching one. `SchemaMirror.cs` has the same
constraint, because the net48 test project links it.

## Nothing that fails may look like a drop

The target is a git history, so a file missing from tonight's tree gets committed as the object being dropped,
and the next good night adds it back as new. That is the one failure here nobody would notice until they needed
the history. So `ServerExporter` exports into a temporary **staging** folder, and `SchemaMirror` copies into the
repository only what the export can vouch for:

- **An unreachable server** throws before anything is mirrored.
- **Inaccessible databases** (offline, restoring, a non-readable AG secondary, `HAS_DBACCESS = 0`) are never
  passed to the export. Databases in `SchemaFolderExportResult.FailedDatabases` are left out of
  `ExportedDatabases`. Both keep their folders as they were.
- **`UnreadFolders`**: a type whose collection threw keeps its folder.
- **`UnwrittenFiles`**: an object that threw, scripted empty, or was encrypted and couldn't be decrypted keeps
  its previous file. An encrypted module whose DAC fails one night therefore keeps the last decrypted text,
  rather than losing it.
- **Dropped databases** are recognised only in "every database" mode, and only when `sys.databases` has no row
  for them at all, in any state. That is why `ListDatabases` returns inaccessible databases instead of filtering
  them out in SQL.
- **A cancelled export** mirrors nothing for the database in progress.

**Each database is exported and mirrored on its own**, one `ExportToFolder` call per database. The first version
exported the whole server to staging and mirrored once at the end. On a big server the repository stayed empty
for the whole run and the console printed nothing, which looked exactly like a hang. Mirroring per database
fills the repository as the run goes and logs a time per database. Progress lines (`RunLog.Progress`) always go to
the console, but go to the log file only with `--verbose`. Dropped-database detection still runs once, after
every database is done.

Those three lists on `SchemaFolderExportResult` exist for this tool. The Schema Cache window ignores them.
`SchemaMirror` is pure file I/O and is linked into the test project (`SchemaMirrorTests`). Keep it that way.
Like the window's re-export cleanup, it only touches `.sql` files inside `ExportFileNaming.TypeFolders`.

Two servers must not share a folder. Each would see the other's databases as dropped. `ExporterConfig.Validate`
refuses that config.

## Skipping unchanged databases

`SchemaFingerprint` hashes one catalog query per database. For each source it takes a row count and
`CHECKSUM_AGG(BINARY_CHECKSUM(...))`, plus the database collation. It then adds the exporter's build ID (MVID), SMO's
version and the decryption flag, so a new build or setting re-exports everything once. `ChangeDetection` (pure, and
linked into the tests) decides whether to skip, and the state lives in `stateDirectory`, never in the repository. The
same rule as the mirror applies in the other direction: **a wrong skip loses a change silently, a wrong export only
costs time**, so every doubtful case exports.

- **`modify_date` is not enough on its own.** Extended properties, schemas and their owners, and user-defined types
  have none, and the enabled/disabled and NOCHECK flags on indexes, triggers and constraints may not touch it. Each
  has its own checksummed source. Any new thing the export starts writing needs its source added here too, or a
  change to it waits for the full scan.
- **Nothing that changes with data or use:** no row counts and no sequence `current_value`. Either would make
  every database look changed every night.
- **The fingerprint is taken before the export**, so a change made during the export still differs tomorrow.
- **It is recorded only after a clean export** (`IsClean`). Otherwise a one-night failure is frozen into the history
  until the schema next changes. The one exception is encrypted objects that weren't decrypted while decryption is
  off: they are the same every night. With decryption on, a failed DAC may work tomorrow, so it counts as unclean.
  That is what `SchemaFolderExportResult.NotDecryptedModules`/`NotDecryptedTriggers` exist for.
- **A skip also needs the database's folder to exist in the repository**, and the state file records the output
  folder it was written for. A re-cloned repository, or a moved `outputPath`, must not inherit "unchanged".
- `fullScanEveryDays` (default 7) is the backstop for anything the fingerprint misses, and `--full` forces one.

Verified on LocalDB: an extended property added on its own, and a trigger disabled, each caused an export; an
unchanged database was skipped in 0.1 s.

## Git

- Uses the git CLI and nothing provider-specific. Azure DevOps and GitHub differ only in the credential, and a
  PAT sent as `Authorization: Basic base64("x-access-token:<pat>")` works for both. The header is passed
  through `GIT_CONFIG_COUNT`/`KEY_0`/`VALUE_0` (git 2.31+), never `-c` on the command line, where other
  processes could read it.
- `GIT_TERMINAL_PROMPT=0` and `GCM_INTERACTIVE=never`: a credential prompt in a scheduled task would hang forever
  and never fail.
- `git.executable` runs a git that isn't on PATH, typically Portable Git. Point it at `<folder>\cmd\git.exe`: that
  wrapper sets up the PATH its HTTPS and credential helpers need, and `bin\` or `mingw64\bin\git.exe` don't. A git
  that can't be started throws `Win32Exception`, which `Run` turns into a `GitException`, so it is a logged error
  (exit 1) and not an unhandled crash.
- **Every slow git step reports progress.** The first real run's `git add` (tens of thousands of new scripts, each
  scanned by antivirus) went many minutes with no output and was killed as a hang. Killing it left a stale
  `.git/index.lock`, and the second server's folder was never committed. So `add` runs `--verbose` and its per-file
  lines are counted into a progress line every 5 s, and `push` runs `--progress` (it reports nothing to a pipe
  otherwise), thinned to one line per phase or every 5 s. Every step given an `activity` also prints "still running"
  every 30 s. `Run` reads both streams with the async line reader, which splits on the bare `\r` git redraws with.
  Lines `onLine` consumes stay out of the output that callers parse and errors quote.
- `pull --ff-only` only. This tool is meant to be the repository's only writer, so it never merges or rebases
  unattended.
- `.gitattributes` gets `*.sql -text`. Without it, `core.autocrlf` on the service account would rewrite the CRLF
  files on checkout, and every file would differ from an SSMS export.
- Add and commit use a pathspec, and a pathspec that matches nothing is fatal to both. A folder can be missing
  from disk yet still tracked (every database dropped), so `CommitServer` checks both before naming it.
- With `core.ignorecase` (the Windows default), a rename that only changes case (`Foo` to `foo`) is written to
  disk under the new name but git may keep the old one. This is known and left as is.
- Connections use plain `new SqlConnection` / SMO `ServerConnection`. The root rule about
  `SqlConnectionFactory.Create` is about borrowing SSMS's Entra token, and there is no SSMS here. For Entra, use
  the connection string's `Authentication=` keyword.

## SSRS report servers

`ReportServerExporter` exports `reportServers` entries from the catalog database (`dbo.Catalog`, one query), not the
web service. It needs only a SQL login, works on any SSRS version, and the `Content` bytes are exactly what the
portal's Download returns. It shares the config, log and `GitRepository` (`CommitReportServer`) with the schema export,
but none of the staging or fingerprint machinery, because none of it applies here:

- **The whole result is read into memory before anything is written**, so a failure throws with the folder
  untouched. There's no partial result to guard, so `ReportMirror` owns the folder outright: anything in it that the
  catalog didn't produce is deleted, except the folder's own `.gitattributes` (`* -text`; RDL keeps whatever line
  endings its author's tool saved, and resources are binary). That is why a report server's folder must not
  share a folder with a schema server, which `Validate` checks across both lists.
- **No skip-unchanged.** One query over a catalog is cheap. Unchanged files aren't rewritten, so git sees no change.
- **Shared data sources (Type 5) are written from `dbo.DataSource`, not `Content`**: extension, credential mode,
  prompt and flags. The connection string is encrypted with the report server's key, so it is only included when
  `reportServerUrl` is set. Then `GetDataSourceContents` on `ReportService2010.asmx` (SOAP, which goes back to 2008 R2,
  unlike the REST API) returns it decrypted, called with the task account's Windows credentials. A failed call
  keeps the previous `.rds` byte for byte. Writing the no-connection-string version instead would commit the
  string as removed, and the next good night would add it back. `RedactPassword` masks `Password`/`Pwd` keys,
  because the history is pushed to a shared repository.
- Left out: folders (1), linked reports (4, no content of their own) and Power BI reports (13, a `.pbix` in
  `CatalogItemExtendedContent` that doesn't diff).
- `ReportMirror` (with `FilePathFor`, the type-to-extension map) is pure and linked into the net48 tests
  (`ReportMirrorTests`).
- Not yet verified against a live report server: the column types and Type codes come from the documented catalog
  schema, and the mirror and naming are unit-tested.
