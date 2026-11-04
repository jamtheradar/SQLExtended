# Schema Export

`Export/SchemaExportService.cs` scripts schema out of SQL Server via SMO's `Scripter` in three shapes: a
whole database to one .sql file and a single object to one .sql file (both reached from the Object Explorer
menu), and — `ExportToFolder` — a folder tree of **one file per object, in a subfolder per type**, reached
from the Schema Cache window (database context menu, server context menu, or the Export toolbar button).

The folder export exists to be pointed at a **folder-compare tool** (WinMerge), and that goal, not
runnability, decides every choice in it:

- **Nothing volatile is written.** No export timestamp, no row counts, no manifest file, and
  `IncludeHeaders` is off — SMO's object header carries a script date, which alone would make every file in
  the tree differ on every export. A file differs only when the object's definition differs.
- **The path shape differs by scope on purpose.** A single-database export puts the type folders directly
  under the chosen folder; only a whole-server export inserts a database level. Two exports of the same
  database from two servers therefore line up file-for-file — a server or database name in the path would
  misalign every file and defeat the point.
- **Statement order is left exactly as SMO produced it.** SMO's child collections (indexes, constraints,
  triggers) are keyed and enumerated by name, so the output is already stable across servers. Re-sorting to
  "canonicalise" it would risk splitting a module's leading `SET ANSI_NULLS`/`QUOTED_IDENTIFIER` batches
  from its body for no gain.
- **`NoCollation` is left off** (unlike the single-file `ExportDatabase`, which sets it). SMO then emits
  `COLLATE` only where a column departs from the database default — a real difference worth seeing, rather
  than the noise that scripting every collation would produce.
- Line endings are normalised to CRLF (a definition stored with bare LFs would otherwise read as a
  whole-file difference), and files are UTF-8 **with** a BOM so SSMS doesn't open non-ASCII identifiers as
  ANSI. Both sides of a compare get both, so neither costs diff noise.
- **A re-export offers to delete the previous scripts first.** Writing over the top leaves the scripts of
  dropped objects in place, and the next compare reports them as present on both servers — the diff would be
  wrong in the one direction the tool is trusted for. Deletion only ever touches `.sql` files inside folders
  named in `ExportFileNaming.TypeFolders`, and filters on the extension rather than trusting the `*.sql`
  wildcard (Windows matches three-character extension patterns against longer ones, so the pattern alone
  would also catch a `.sqlproj`).
- `db.PrefetchObjects` + `SetDefaultInitFields(type, true)` per scripted type: without them SMO lazy-loads
  per property and a few hundred objects becomes thousands of round trips.
- **`Scripter.PrefetchObjects = false`**, because the database-level prefetch above has already done the work.
  The scripter is handed one object per call, and with its own prefetch on (the default) it re-ran the prefetch
  queries for that single object every time. On a remote server that cost 1–2 s per table, while procedures,
  which have no child collections, went at over 100 a second. A 7,185-table database took 3 h 43 m. Each type's
  progress line reports its time and query count (`ServerConnection.StatementExecuted`). A type whose query count
  grows with its object count is going back to the server per object.
- **`CollectGroups` enumerates modules *before* prefetching them, and tables *after*.** Enumerating loads the objects
  with the default init fields (all of them), and the prefetch then fills in the scripting fields on those same
  objects. Modules need that order: the procedure (and, on LocalDB, view) prefetch leaves out `IsEncrypted`, which
  `ScriptCreate` reads first, so SMO re-initialised each one (2 queries a procedure on a real server, 2 a view on
  LocalDB, 0 after the swap). Tables need the opposite: their default fields include row counts and space used, and
  enumerating 7,185 tables first took 6.7 minutes, against 23 s when the prefetch creates them.
- **View columns are bulk-loaded with every property (`LoadViewColumns`).** On a real server the view prefetch
  loaded columns with too few properties, and scripting read one it had left out, which re-initialises that column
  alone, looked up by name: 5,779 queries for 1,434 views, 8.4 minutes. `SetDefaultInitFields(typeof(Column), true)`
  plus `db.InitChildCollection("View/Column", forScripting: false)` is one query. `forScripting: true` loads the same
  short list as the prefetch, so it doesn't help. The column defaults are put back afterwards, so table columns load as
  before. Reproduced on LocalDB by reading column properties after the prefetch: 3 queries per view, 0 after.
- **When a type is slow, read the log, not the code.** A failed prefetch is logged, not swallowed. When a type's query
  count is over its object count (and over 50), the progress log lists its three most repeated statements. That is
  how the view columns were found, after one wrong guess.
- Each type group and each object is collected/scripted in its own try/catch, and each database in a server
  export too — one type the edition or permission set disallows costs one folder and a warning, not the
  export. Cancellation is reported on the result rather than thrown, so the caller can still say how much of
  the tree was written; the files on disk are real and the user has to be told about them.

`Export/ExportFileNaming.cs` is deliberately free of SMO and I/O so the test project can link it
(`SQLExtended.Tests/Export/ExportFileNamingTests.cs`). Its job is that no two objects collapse onto one
file name: a case-sensitive collation allows both `[dbo].[Foo]` and `[dbo].[foo]`, names sanitize into each
other (`A/B` and `A\B`), and Windows silently strips trailing dots. Any of those silently drops an object,
and in a folder compare that reads as the object missing from one server — the one failure mode of this
feature nobody would notice.

In the Schema Cache window, **selection lives on the node objects** (`IsSelected`, bound TwoWay from the
item style, the same as `IsExpanded`) rather than on the tree containers. The tree's `ItemsSource` is
replaced wholesale every five seconds, so a container-held selection would not survive the next tick and the
Export button would lose its target. Selection is captured on every rebuild, including the
`capture: false` ones Expand/Collapse all use — that flag is only about expansion state, and letting it skip
selection would re-apply a stale one, aiming Export at whatever was selected two clicks ago. Status messages
that must outlive a rebuild (export outcomes, "no connection available") are written through `SetStatus(...,
sticky: true)`, since `Rebuild` otherwise overwrites the line with its counts within five seconds.
