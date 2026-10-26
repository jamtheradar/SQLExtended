# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

SQLExtended is an SSMS 22 extension (VSIX) providing schema viewing and SQL formatting. It targets .NET Framework 4.8 and runs inside the Visual Studio 2026 shell that SSMS 22 is built on.

Two main commands:
- **View Schema** (Ctrl+Shift+D) — shows CREATE TABLE script, indexes, and foreign keys for the object under cursor
- **Format SQL** (Ctrl+K, Ctrl+Y) — formats T-SQL with customizable options

## Build Commands

```bash
dotnet restore
dotnet build --configuration Release

# Run tests
dotnet test

# Run a specific test class
dotnet test --filter "ClassName=FormatterTests"
```

**Prerequisites**: Visual Studio 2022/2026 with "Visual Studio extension development" workload, .NET SDK 10.0.201+, and SSMS 22 installed. SSMS internal DLL reference paths in `SQLExtended/SQLExtended.csproj` are hardcoded to `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\` — update if your install differs.

**Debug target**: Set startup program to `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Ssms.exe`

## Architecture

### Request Flow (View Schema)

```
SchemaViewerCommand.Execute()
  → EditorHelper.GetObjectNameAtCursor()     // expands cursor to qualified name
  → ConnectionHelper.GetActiveConnectionString()  // reflection into SSMS internals
  → SchemaQueryService.GetSchemaScript()     // T-SQL queries against sys.* views, cached
  → SchemaDialog (WPF)                       // dark-themed display dialog
```

### Request Flow (Format SQL)

```
FormatCommand.ExecuteFormat()
  → FormatterOptions.Load()                  // from %APPDATA%\SQLExtended\SSMS\formatter-options.json
  → SqlFormatterService.Format()             // wraps TransactSql.ScriptDom parser
    → PostProcessor                          // cleans up ScriptDom output
  → Replace in editor with undo context
```

### Key Design Decisions

- **ConnectionHelper uses three reflection-based fallback strategies** to extract the active connection from undocumented SSMS internals (ServiceCache, ScriptFactory, ServiceProvider). All wrapped in try/catch to prevent SSMS crashes.
- **All three strategies read the connection through `UIConnectionInfoReader`**, which is also the only place that knows how to spot an Entra (Azure AD) sign-in: it carries a token, not a password, so the string is harvested credential-free and `SqlConnectionFactory.Create` attaches the token `EntraTokenBroker` holds for that server. **Open connections through `SqlConnectionFactory.Create`, never `new SqlConnection`** — a direct `new` gets a string with no credentials and fails to log in on Azure.
- **The VSIX ships Microsoft.Data.SqlClient's whole net48 closure**, not just SqlClient itself — see `_SqlClientRuntimeDependency` in `SQLExtended/SQLExtended.csproj`. SqlClient binds to exact versions (`System.Diagnostics.DiagnosticSource` 10.0.0.3 and friends) that resolve only because the host's `SSMS.exe.config` carries a `bindingRedirect` reaching that far, and that ceiling is lower on every older SSMS build. Below it, `SqlConnection`'s static constructor throws `FileNotFoundException` and every feature dies at once. Don't trim the list to "what SSMS already has" — that is the dev box talking.
- **SchemaQueryService caches** results in a `ConcurrentDictionary` keyed by `"connStr|schema|name"`. Cache is per-session only (clears on SSMS restart).
- **All UI thread operations** must go through `ThreadHelper.JoinableTaskFactory`. Database queries run on background threads.
- **SSMS internal DLLs** (`SqlWorkbench.Interfaces.dll`, `SQLEditors.dll`, `Microsoft.SqlServer.GridControl.dll`) are undocumented and referenced from the SSMS install folder — marked `Private=false` so they're excluded from the VSIX output.

## Projects

| Project | Description |
|---------|-------------|
| `SQLExtended/` | Main VSIX extension (net48) |
| `SQLExtended.Tests/` | xUnit tests (links formatter source files directly) |
| `SoluitionDocs/` | Non-building documentation project |

## Subsystem notes

Each subsystem keeps its design notes in a `CLAUDE.md` beside its code, and those notes are
the record of what already shipped wrong there — almost every rule in them is guarding a
failure that was silent on screen. **Read the file for a subsystem before changing it**; they
load automatically when a file in that folder is opened, but not while merely planning.

| Area | Notes |
|------|-------|
| Dark/light palette for every WPF window (`{DynamicResource Sqlx…}`, no colour literals) | `SQLExtended/Theme/CLAUDE.md` |
| Formatting (`PostProcessor`, alias/CASE/comment passes, Format Selection, Format With… and presets) | `SQLExtended/Formatting/CLAUDE.md` |
| Qualify Columns — alias every table, prefix every column (`ColumnQualifier`) | `SQLExtended/Qualify/CLAUDE.md` |
| Align On — line a token up across lines (`ColumnAligner`) | `SQLExtended/Align/CLAUDE.md` |
| The session log, and what the soft catches swallow | `SQLExtended/Diagnostics/CLAUDE.md` |
| Open Definition (F12) and connected query windows (`QueryWindowOpener`) | `SQLExtended/Definition/CLAUDE.md` |
| Schema export (SMO scripter, folder-compare shape) | `SQLExtended/Export/CLAUDE.md` |
| Encrypted modules (DAC, the XOR technique) | `SQLExtended/Decryption/CLAUDE.md` |
| SQL Search, including Agent job steps | `SQLExtended/Search/CLAUDE.md` |
| Schema cache — the refresh timer, the purge, and the four Schema Cache settings | `SQLExtended/Cache/CLAUDE.md` |
| IntelliSense — identifier bracketing, the `sys.` catalog cache (`Cache/SystemCatalogCache.cs`), synonyms, linked servers (`Cache/LinkedServerCache.cs`) | `SQLExtended/IntelliSense/CLAUDE.md` |
| Rainbow parentheses — the depth scanner, the editor tagger, `SQLExtendedSettings.Changed` | `SQLExtended/Rainbow/CLAUDE.md` |
| Comment tags and banner headers — the scanner, the five colour schemes, the Fonts and Colors writer | `SQLExtended/Comments/CLAUDE.md` |
| Results grid — aggregates pane, Find in grid, Go to Column | `SQLExtended/ResultsGrid/CLAUDE.md` |
| Statistics parser (vendored from Brent Ozar — don't edit) | `SQLExtended/Statistics/CLAUDE.md` |
| Monitoring — pinning and staged collection, common to all four dashboards | `SQLExtended/Monitoring/CLAUDE.md` |
| … Performance dashboard and the build catalog | `SQLExtended/Monitoring/Performance/CLAUDE.md` |
| … Agent jobs dashboard and `JobDialogLauncher` | `SQLExtended/Monitoring/Jobs/CLAUDE.md` |
| … Always On monitor | `SQLExtended/Monitoring/AlwaysOn/CLAUDE.md` |
| … Replication monitor | `SQLExtended/Monitoring/Replication/CLAUDE.md` |
| Environment Tabs (tab colouring by connection) | `SQLExtended/EnvTabs/CLAUDE.md` |
| Execution safety — confirm UPDATE/DELETE without WHERE (`Query.Execute` hook) | `SQLExtended/Safety/CLAUDE.md` |
| Object Explorer context menu | `SQLExtended/ObjectExplorer/CLAUDE.md` |

## Releasing

Cutting or publishing a release, or touching `version.txt`, `publish-release.ps1` or the update
check: **invoke the `releasing` skill** (`.claude/skills/releasing/SKILL.md`). It carries the
three-consumers-must-agree version rules and the guards that enforce them; `SoluitionDocs/Deployment.md`
is the long form.

## Claude Code Instructions

- When running `dotnet build`, always use `-v q` (quiet verbosity). On success, do not read the output. On failure, only examine error lines: `dotnet build -v q 2>&1 | grep -E "error |failed"`
- When running `dotnet build` after a recent restore, use `--no-restore` to save time and output
- Exclude `bin/` and `obj/` folders from all file searches and reads
- Save plan files to `.claude/plans/` in the project root, not the user home directory

## Code style

- C# line width: 160 chars. Prefer fewer line breaks; let lines run rather than splitting method chains over many lines.
- Use file-scoped namespaces, target-typed `new`, primary constructors where they reduce noise.
- Don't reformat code outside the scope of the change.

## VSCT (Command Table)

`SsmsSchemaViewerPackage.vsct` defines menu entries and keyboard shortcuts. Commands appear both in a top-level "SQLExtended" menu and the editor context menu (right-click).

**Two menus are not what the VSCT suggests, and a new command must be added to both by hand:**
- **The top-level SQLExtended menu SSMS 22 shows is built in code**, by `MainMenuService.Items` — the VSCT one never
  renders. A command left out of `Items` is reachable only by its shortcut: Qualify Columns and Go to Column shipped
  that way. The menu reads each command's live binding for its shortcut text.
- **The query editor's right-click menu is SQLEditors' `IDM_SQLWB_SQLSCRIPT_CONTEXT`** (`guidSQLEditorGroup`, 0x50),
  holding a SQLExtended submenu; the Messages pane's is `IDM_SQLWB_SQLRESMSG_CONTEXT` (0x60). The shell's
  `IDM_VS_CTXT_CODEWIN` merges without error and never appears in a query window — the editor items sat there unseen.
  The IDs come from `SQLWorkbenchCommands` in `SQLEditors.dll`, read by reflection.

**A shortcut for an editor command must be bound in the SQL Query Editor scope (`guidSqlQueryEditorScope`), not only
Global (`guidVSStd97`).** In a query window a Text Editor binding beats a Global one on the same chord, silently:
Format SQL's Ctrl+K, Ctrl+F lost to Edit.FormatSelection and Qualify Columns' Ctrl+K, Ctrl+A to Edit.SwapAnchor, and
both shipped broken. The comment above `<KeyBindings>` has the rule; check a new chord under Tools > Options >
Keyboard ("Shortcut currently used by") with a reset keyboard scheme before shipping it. Microsoft's own SSMS 22
extensions (`Extensions\Microsoft\*\.vsextension\extension.json`, `"shortcuts"`) win a Global clash with ours: Copilot
has Ctrl+Alt+D/E/F/R, SqlFormatter Ctrl+K, Ctrl+Q, Presenter Mode Ctrl+Shift+F5.

**Where Redgate SQL Prompt has the same command, use its chord** (its quick reference guide: F12 script object,
Ctrl+F12 select in Object Explorer, Ctrl+K, Ctrl+Y format, Ctrl+B, Ctrl+Q / U / B / C qualify, uppercase, brackets,
semicolons). Its users bring those habits; inventing a chord is how the earlier ones ended up clashing. Only commands
with no Prompt equivalent get a chord of our own.

**Bump the version in `[ProvideMenuResource("Menus.ctmenu", N)]` (`SsmsSchemaViewerPackage.cs`) with every `.vsct`
change.** The shell keeps its own merged copy of each extension's command table and re-merges it only when that number
changes (or on `Ssms.exe /setup`). Replacing the DLL is not enough: the build's copy into the Extensions folder deploys the
new table and SSMS goes on using the old one, so every command added since is missing from Tools > Options > Keyboard
and its shortcut does nothing — which looks exactly like a chord conflict. It sat at 1 from the import until issue #8,
by which time six commands' shortcuts were missing. (The deploy also used to copy the *previous* build's pkgdef —
`CopyToSsms` ran before `EnsurePkgDef` regenerated it — so a bump only reached SSMS on the build after; it now depends on it.)
The shell only *looks* for changed extensions when `Extensions\extensions.configurationchanged` has a new timestamp —
what an installer does — so `CopyToSsms` touches it; without that, even a bumped version goes unnoticed. The
`Extensions` folder needs elevation to write, so on a dev box the touch usually fails and the build warns: after a
`.vsct` change, close SSMS and run `Ssms.exe /updateconfiguration` (or `/setup`) once.

The `.vsct` and the three `*.vsixmanifest` files at the project root — which is committed, which is
generated, and which one `StampVsixVersion` actually edits — are documented in
`SQLExtended/MANIFESTS.md`. Read it before touching any of them.
