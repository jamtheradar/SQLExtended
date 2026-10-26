<!-- Absolute raw URL, not a repo-relative path: publish-release.ps1 hands this file to
     www.vsixgallery.com as ?readmeUrl=, and the gallery renders it on its own domain, where a relative
     path resolves to nothing. Same image as the .vsix icon — SQLExtended\Resources\preview.png. -->
<img src="https://raw.githubusercontent.com/JamTheRadar/SQLExtended/main/SQLExtended/Resources/preview.png"
     alt="SQLExtended" width="92" align="right">

# SQLExtended for SSMS

**SSMS, extended.** A free, open-source suite of tools for SQL Server Management Studio 22 —
schema browsing and search, T-SQL formatting, IntelliSense, four live monitoring dashboards,
schema export built for folder diffing, and results-grid tooling, in one extension.

Every multi-tool suite for SSMS is commercial. This is the open-source one.

> **Status:** works, used daily, but young. Expect rough edges, and see *Known limitations* below.

## Requirements

- **SQL Server Management Studio 22** (built on the Visual Studio 2026 shell)
- Windows; .NET Framework 4.8

## What's in it

| Area | |
|---|---|
| **Schema** | View Schema (`Ctrl+Shift+D`) — CREATE script, indexes, foreign keys for the object under the cursor; hover an object name for its type and row count. Open Definition (`F12`) — the same script straight into a new query window on the same server and database, as `CREATE OR ALTER` for modules (configurable); the View Schema preview has an Open in Script Window button too. Locate in Object Explorer (`Ctrl+[, S`) — expands Object Explorer to the object under the cursor and selects it, connecting the server if needed. Persistent schema cache with background refresh: Schema Cache window (`Ctrl+Alt+C`), Refresh Schema Cache (`Ctrl+Shift+R`), Refresh All Databases, Clear Schema Cache. Encrypted modules can be decrypted through the DAC (off by default). |
| **Search** | SQL Search (`Ctrl+Alt+F`) — object names, columns, and module definitions via a SQLite full-text index; optionally SQL Agent job step commands, which live outside the cache. |
| **Formatting** | Format SQL (`Ctrl+K, Ctrl+F`, or `Ctrl+K, Ctrl+Q` in a query window) — ScriptDom-based T-SQL formatter with a large option set: casing, comma position, list reflow, CASE/CTE/derived-table layout, alias style. |
| **Qualify Columns** | Qualify Columns (`Ctrl+K, Ctrl+A`) — aliases every table in a SELECT and prefixes each column with its table's alias, using the schema cache to know which table owns it. Columns it cannot pin to exactly one table are left as written. **Tab after `SELECT *`** (or `alias.*`) expands the star to the column list the same way, aliasing the table if it has none. |
| **IntelliSense** | Completion over the schema cache and the system catalog (`sys.`, `INFORMATION_SCHEMA`), signature help, snippets (with SQL Prompt snippet import), automatic bracketing of names that need it. |
| **Editor** | Rainbow parentheses (optionally `BEGIN`/`END` and `CASE`/`END` too). Comment tags — comments starting `!`, `?`, `todo` or `*` stand out — and coloured banner headers. A prompt before executing an `UPDATE` or `DELETE` with no `WHERE`. |
| **History & library** | SQL History (`Ctrl+Alt+H`) — every query you run, searchable. Script Library (`Ctrl+Alt+L`) — saved scripts, shipped with sp_WhoIsActive, sp_who3, BPCheck and others. |
| **Monitoring** | Four dockable dashboards, each pinned to the server it was opened from: Performance (`Ctrl+Alt+P`), Agent Jobs (`Ctrl+Alt+J`), Always On (`Ctrl+Alt+A`), Replication (menu only). Also opened from a server's right-click menu in Object Explorer, no query window needed. Live only — nothing is persisted. |
| **Results grid** | Find in Results (`Ctrl+Alt+S`) — search and highlight grid text. Go to Column (`Ctrl+Alt+K`) — jump to a column by name in a wide result set. Grid Aggregates (`Ctrl+Alt+G`) — sum/avg/min/max/distinct/null per selected column. Copy as IN Filter (`Ctrl+Shift+I`) — the selected cells of one column as an `IN (...)` list, quoted by type, NULLs left out. Script Results as INSERT. |
| **Export** | Script a database to one file, or to a folder tree of one file per object — deliberately free of timestamps and other volatile output, so two servers can be compared in WinMerge. From the database or object right-click menu in Object Explorer. |
| **Statistics** | Parse Statistics (`Ctrl+K, Ctrl+G`) — reads `STATISTICS IO`/`TIME` output from the Messages pane. |
| **Environment Tabs** | Colours and renames query tabs by the server and database they're connected to, so production is distinguishable at a glance (off by default). Right-click any tab → **Highlight Tab** to paint it by hand. |
| **Object Explorer** | A SQLExtended submenu on server, database and object nodes: View Schema, Search in Database, Validate Schema, Export, Refresh Schema Cache, the dashboards. Connected servers are grouped into folders matching your Registered Servers groups. |
| **Validation** | Validate Schema References (`Ctrl+Alt+V`) — finds references to objects and columns that no longer exist. |

Everything is on the **SQLExtended** menu in the menu bar, which shows each command's current shortcut.
Right-click in a query window for a SQLExtended submenu of the editor commands, or in the results grid for
the grid ones. Shortcuts can be changed under **Tools → Options → Keyboard** (search for `SQLExtended`).

Settings live under **SQLExtended Settings…**, where most features can be switched off, plus a
Diagnostics tab with an in-session log, since almost every failure in an SSMS extension has to be
swallowed rather than thrown.

## Install

[![VSIX Gallery](https://www.vsixgallery.com/badge/SQLExtended.f1e2d3c4-a5b6-7890-abcd-ef1234567890.svg)](https://www.vsixgallery.com/extension/SQLExtended.f1e2d3c4-a5b6-7890-abcd-ef1234567890/)
[![SSMS VSIX Gallery](https://ssmsgallery.azurewebsites.net/badge/SQLExtended.f1e2d3c4-a5b6-7890-abcd-ef1234567890.svg)](https://ssmsgallery.azurewebsites.net/extension/SQLExtended.f1e2d3c4-a5b6-7890-abcd-ef1234567890/)

Grab the latest `SQLExtended-<version>.vsix` from
[Releases](https://github.com/JamTheRadar/SQLExtended/releases/latest), or from either gallery listing
([VSIX Gallery](https://www.vsixgallery.com/extension/SQLExtended.f1e2d3c4-a5b6-7890-abcd-ef1234567890/),
[SSMS VSIX Gallery](https://ssmsgallery.azurewebsites.net/extension/SQLExtended.f1e2d3c4-a5b6-7890-abcd-ef1234567890/) — the latter is also
what the SSMS Extension Manager installs from).

Close SSMS, then double-click the `.vsix`. It installs per-user — no admin rights needed. Reopen SSMS and
the **SQLExtended** menu appears in the menu bar; if it doesn't, run [`SoluitionDocs/clearSSMScache.ps1`](SoluitionDocs/clearSSMScache.ps1) to clear SSMS's
MEF cache and relaunch. Uninstall from **SSMS → Extensions → Manage Extensions**.

Once installed, the extension checks for newer releases on startup (at most once per 20h, and switchable
off under SQLExtended Settings → Updates). It cannot update itself silently — no VS or SSMS extension can
replace its own loaded assembly — so it points you at the new `.vsix` and asks you to close SSMS first.

## Building

```bash
dotnet restore
dotnet build --configuration Release
dotnet test
```

**The extension project needs SSMS 22 installed on the build machine.** It references undocumented
SSMS assemblies (`SqlWorkbench.Interfaces.dll`, `SQLEditors.dll`, `Microsoft.SqlServer.GridControl.dll`)
by path from the install folder. They are referenced at compile time only and are never redistributed
in the output. If your install path differs from
`C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\`, edit the paths in
`SQLExtended/SQLExtended.csproj`.

The test project links its sources directly and needs none of that, so `dotnet test` runs anywhere —
which is also all that CI can check.

To debug, set the startup program to the SSMS executable in that folder.

## Known limitations

- SSMS 22 only. Earlier SSMS versions are a different shell.
- A good deal of this reaches into SSMS internals by reflection, because there is no public API for
  the active connection, the results grid, tab colouring, or the Messages pane. Those paths are
  defensive and fail soft, but a future SSMS release can break them.
- Parts of the system catalog and monitoring SQL have been verified by parsing rather than against
  every SQL Server release and edition. Bug reports with a version number are useful.

## Contributing

Issues and PRs welcome. Worth knowing before you start: `CLAUDE.md` in the root is the real
architecture document — it records *why* things are shaped the way they are, especially the many
places where the obvious implementation was tried and failed silently. Read the section for the area
you're touching first.

## Licence

MIT — see [LICENSE](LICENSE).

Includes third-party code; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The statistics
parser is vendored from [Brent Ozar Unlimited's Statistics Parser](https://github.com/BrentOzarULTD/StatisticsParserExtension)
(MIT). The Script Library ships four community procedures as install scripts, unmodified and with their
headers intact: [sp_WhoIsActive](https://github.com/amachanic/sp_whoisactive) by Adam Machanic
(GPL-3.0), [sp_who3](https://github.com/ronascentes/sp_who3) by Rodrigo Nascentes (MIT), and
[BPCheck](https://github.com/microsoft/tigertoolbox/tree/master/BPCheck) and
[usp_WhatsUp](https://github.com/microsoft/tigertoolbox/tree/master/usp_WhatsUp) from Microsoft's Tiger Toolbox (MIT).
Its two login-audit scripts come from Lorenzo Uriel's [data-eyes](https://github.com/lorenzouriel/data-eyes) (MIT). Environment Tabs was inspired by [SSMS-EnvTabs](https://github.com/Blake-goofy/SSMS-EnvTabs),
though no code is shared.

Not affiliated with or endorsed by Microsoft. SQL Server and SSMS are trademarks of Microsoft Corporation.
