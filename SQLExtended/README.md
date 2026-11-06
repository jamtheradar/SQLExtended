# SQLExtended — VSIX project

This is the extension itself: the VSPackage, every subsystem, and the packaging that turns them into a
`.vsix`. It targets **net48** and loads into the Visual Studio 2026 shell that SSMS 22 is built on.

- **What the extension does for a user** — see the [repo README](../README.md).
- **Why the code is shaped the way it is** — see [`../CLAUDE.md`](../CLAUDE.md) and the per-subsystem
  `CLAUDE.md` files listed below. Those record the failures each rule is guarding against, most of
  which were silent on screen.
- **Manifests and the command table** — see [`MANIFESTS.md`](MANIFESTS.md).
- **Cutting a release** — see the `releasing` skill and `../SoluitionDocs/Deployment.md`.

## Entry points

| File | |
|---|---|
| `SsmsSchemaViewerPackage.cs` | The VSPackage. Registers commands, tool windows and the menu resource; owns auto-load |
| `SsmsSchemaViewerPackage.vsct` | Command table — menus, groups, buttons, keybindings |
| `MainMenuService.cs` | Builds the top-level **SQLExtended** menu |
| `SQLExtended.csproj` | Build, VSIX packaging, version stamping, and the xcopy deploy into SSMS |

Root-level helpers are the pieces most subsystems lean on: `ConnectionHelper.cs` (the active connection,
by reflection into SSMS internals), `EditorHelper.cs` (text and cursor in the query editor),
`ObjectExplorerHelper.cs`, `ContentTypeSniffer.cs`, `DatabaseChangeMonitor.cs`, `ServiceCacheProxy.cs`.

## Subsystems

Each folder is self-contained; the ones with design notes are marked. **Read the notes before changing
that folder** — they load automatically when you open a file there, but not while merely planning.

| Folder | | Notes |
|---|---|:-:|
| `Cache/` | Persistent SQLite schema cache, plus the `sys.` catalog cache (notes in `IntelliSense/`) | ○ |
| `Decryption/` | Encrypted module definitions via the DAC | ● |
| `Diagnostics/` | The in-session log the soft catches write to | ● |
| `EnvTabs/` | Query-tab colouring and renaming by connection | ● |
| `Export/` | SMO scripter; folder-per-object output built for diffing | ● |
| `Formatting/` | ScriptDom-based T-SQL formatter and its post-passes | ● |
| `History/` | Query history store | ○ |
| `IntelliSense/` | Completion, signature help, identifier bracketing | ● |
| `Monitoring/` | Performance, Agent Jobs, Always On, Replication dashboards | ● |
| `ObjectExplorer/` | Object Explorer context menu and server-group folders | ● |
| `ResultsGrid/` | Aggregates pane, Find in grid, script results as INSERT | ● |
| `ScriptCast/` | Run one script on many databases and merge the results; Cast Lists; transaction mode (Commit All / Rollback All) | ● |
| `ScriptLibrary/` | Saved script library | ○ |
| `Search/` | SQL Search over the index, including Agent job steps | ● |
| `Settings/` | Settings dialog and the persisted settings model | ○ |
| `Snippets/` | Snippet expansion in the editor, incl. SQL Prompt snippet import | ○ |
| `Statistics/` | `STATISTICS IO`/`TIME` parser — vendored, don't edit | ● |
| `Theme/` | Shared dark WPF resource dictionary | ○ |
| `Updates/` | Startup update check against the published version feed | ○ |
| `Validation/` | Schema-reference validation | ○ |

● has a `CLAUDE.md`  ○ does not

Anything the extension persists lives under `%APPDATA%\SQLExtended\SSMS\` — settings, formatter options
and profiles, the schema cache database, query history, the script library, and logs.

## Dependencies

**NuGet** (the ones packed into the `.vsix`): `Microsoft.Data.SqlClient`,
`Microsoft.SqlServer.TransactSql.ScriptDom`, `Newtonsoft.Json`, `System.Data.SQLite.Core`, `AvalonEdit`.
`Microsoft.SqlServer.SqlManagementObjects` (SMO) and the VS SDK are referenced but *not* packed — SSMS
already provides both, and shipping copies would only risk shadowing its own. See the `Content` item
group in the `.csproj` for the explicit payload list.

**SSMS internal assemblies**, referenced by path from the install folder with `Private=false` so they
never reach the output:

- `SqlWorkbench.Interfaces.dll` — connection and Object Explorer interfaces
- `SQLEditors.dll` — the currently active editor's connection info
- `Microsoft.SqlServer.GridControl.dll` — the results grid

These are undocumented. Everything that touches them is reflection-based and wrapped in soft catches,
because an exception escaping into the shell takes SSMS down with it.

## Building

```bash
dotnet restore
dotnet build --configuration Release
```

**Requires SSMS 22 installed on the build machine** for the three references above. If your install path
differs from `C:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\`, edit
`SsmsInstallDir` in the `.csproj`. The `.vsix` lands in `bin\<Config>\net48\`.

The version comes from `../version.txt` and is stamped into the assembly and the generated manifest at
build time; the build fails outright if there is no version to build with. Don't hand-edit versions —
see `MANIFESTS.md` and the `releasing` skill.

## Debugging

`DeployExtension=false`, so no experimental hive is involved. Instead the `CopyToSsms` target copies the
DLL, the pkgdef and `extension.vsixmanifest` into
`…\Common7\IDE\Extensions\SQLExtended\` after every build. Set the debug startup program to `Ssms.exe`
in that folder and F5.

That first build needs `extension.vsixmanifest` to exist in this folder — it is gitignored and created by
hand once; see `MANIFESTS.md` and `../SoluitionDocs/ManualInstall.md`.

If the **SQLExtended** menu doesn't appear after a change to the `.vsct`, SSMS is serving a stale MEF
cache — run `../SoluitionDocs/clearSSMScache.ps1` and relaunch.

## Tests

`../SQLExtended.Tests/` links formatter and parser sources directly rather than referencing this project,
so `dotnet test` runs without SSMS installed. That is also the only part CI can check.
