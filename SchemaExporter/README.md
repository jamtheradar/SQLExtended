# SchemaExporter

A console tool that runs nightly. It exports the schema of every SQL Server you list, writing one `.sql` file
per object, and commits whatever changed to a git repository in Azure DevOps or GitHub. It can also export
SSRS report servers (reports, shared data sources and datasets, resources) into the same repository. The result is a lazy
change history: when an object changed, you can see the day it changed and the diff.

The layout matches the **Schema Cache window's Export** in SQLExtended exactly, so you can compare the history
against an export taken by hand in SSMS:

```
<repo>/
  .gitattributes                 *.sql -text   (keeps CRLF + BOM as written)
  PROD-SQL01/
    Sales/
      Tables/dbo.Customer.sql
      Views/...
      Stored Procedures/...
      Functions/  Table Types/  User-Defined Types/  Sequences/  Synonyms/  Schemas/  Database Triggers/
  PROD-SQL02_SALES/
    ...
  PROD-SSRS01_SSRS/              one per entry in reportServers
    .gitattributes               * -text
    Finance/Monthly Sales.rdl
    Data Sources/Warehouse.rds
    Shared Datasets/Customers.rsd
```

## Setup

1. **Build:** `dotnet publish SchemaExporter -c Release -r win-x64 --self-contained -o publish\SchemaExporter`.
   Copy the `publish\SchemaExporter` folder to the machine that will run the export, for example
   `C:\Tools\SchemaExporter`. A self-contained build doesn't need .NET installed there. To use the machine's own
   .NET 10 runtime instead, leave out `--self-contained`.
2. **Clone the repository** that will hold the history, as the account that will run the task:
   - Azure DevOps: `git clone https://dev.azure.com/<org>/<project>/_git/SchemaHistory D:\SchemaHistory`
   - GitHub: `git clone https://github.com/<org>/schema-history.git D:\SchemaHistory`
3. **Config:** copy `SchemaExporter.sample.json` to `SchemaExporter.json` next to the exe and edit it.
   `outputPath` is the clone from step 2.
4. **Credentials for the push.** Use one of these:
   - A PAT. In Azure DevOps it needs the *Code (Read & write)* scope. In GitHub, use a fine-grained token with
     *Contents: read and write*. Save it in a machine environment variable, for example
     `setx SCHEMA_EXPORTER_PAT <token> /M`, and set `git.tokenEnvironmentVariable` to that name. The tool sends
     it as a header, so it never appears in `.git/config` or on a command line.
   - Or leave `tokenEnvironmentVariable` null and sign in once with Git Credential Manager as the service account.
5. **SQL permissions:** the login needs `VIEW DEFINITION` (or `VIEW ANY DEFINITION` at server level) in every
   database. Turning on `decryptEncryptedModules` needs sysadmin and the DAC. Read `SQLExtended/Decryption/CLAUDE.md`
   before you turn it on.
6. **Try it** without git first: `SchemaExporter.exe --no-git -v`. Then run it with commits but no push:
   `SchemaExporter.exe --no-push`.

### If a run is killed during its git step

The first run commits tens of thousands of files, and staging them can take many minutes (the console shows a
running count). If a run is killed anyway, its export is still on disk, just not committed or pushed. To finish it
without exporting again:

1. Make sure no `git.exe` is still running, then delete `.git\index.lock` in the repository if it's there. The tool
   refuses to start while that file exists.
2. Run `SchemaExporter.exe --push-only`. It commits whatever each configured folder has uncommitted, one commit per
   server as usual, then pushes. `--server NAME` limits it to one server.

### Portable Git

Git doesn't have to be installed. Unpack Portable Git (the `PortableGit-*-64-bit.7z.exe` download from
git-scm.com) anywhere the task's account can read, for example `D:\Tools\PortableGit`, and set:

```json
"git": { "executable": "D:\\Tools\\PortableGit\\cmd\\git.exe", ... }
```

Use `cmd\git.exe`, not `bin\git.exe` or `mingw64\bin\git.exe`. The `cmd` one sets up the paths git's helpers
need, including the HTTPS transport. Do the clone in step 2 with that same `git.exe`. A relative `executable` is
relative to the config file, so a `PortableGit` folder beside `SchemaExporter.exe` can be `PortableGit\cmd\git.exe`.

## Schedule it

Run this in an elevated PowerShell session. The task runs as a domain service account that has SQL access
through Integrated Security:

```powershell
$action  = New-ScheduledTaskAction -Execute 'C:\Tools\SchemaExporter\SchemaExporter.exe' -WorkingDirectory 'C:\Tools\SchemaExporter'
$trigger = New-ScheduledTaskTrigger -Daily -At 2am
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 4) -StartWhenAvailable
Register-ScheduledTask -TaskName 'SQLExtended Schema Export' -Action $action -Trigger $trigger -Settings $settings `
    -User 'DOMAIN\svc-schema' -Password '<password>' -RunLevel Limited
```

Task Scheduler shows the exit code as "Last Run Result":

| Code | Meaning |
|------|---------|
| 0 | Every server exported. There may be warnings, which are in the log. |
| 1 | At least one server, or the git step, failed. The other servers were still committed. |
| 2 | Bad arguments or config. |
| 3 | Cancelled. |

Each run writes a log to `%ProgramData%\SQLExtended\SchemaExporter\logs` and keeps 30 days by default.

## What counts as a change

- **No volatile content.** Files have no dates, row counts or headers, so a night with no schema changes makes
  no commit.
- **One commit per server.** The first line gives counts (`PROD-SQL01: 2 added, 5 changed, 1 removed`) and the
  body lists the files.
- **A failure never shows up as a drop.** The tool keeps the last good scripts when a server is unreachable, a
  database is offline or inaccessible, a type of object can't be read, or a single object fails to script. It
  logs a warning instead.
- **Dropped databases** have their folder removed only when the server's `databases` list is empty, which means
  "every user database". If you list databases by name, the tool never removes a folder for a missing one,
  because it can't tell a dropped database from a typo.
- Leave out system databases, snapshots and the distribution database. Use `excludeDatabases` for others such as `SSISDB`.

## Skipping unchanged databases

Before exporting a database, the tool reads a **schema fingerprint**: one catalog query that summarises every
object, column, index, constraint, trigger, schema, user-defined type and extended property. If it matches the
fingerprint from that database's last clean export, the database is skipped. This takes well under a second,
where a full export of a big database takes minutes. The log shows `unchanged since … — skipped`.

| Setting | Default | |
|---|---|---|
| `skipUnchangedDatabases` | `true` | Turn the whole thing off with `false`. |
| `fullScanEveryDays` | `7` | Export every database in full at least this often, fingerprint or not. `0` = never. |
| `stateDirectory` | `%ProgramData%\SQLExtended\SchemaExporter\state` | One `<server folder>.json` per server. Keep it out of `outputPath`. |

`--full` exports everything tonight regardless.

It errs towards exporting. A database is always exported in full when:
- it has no saved fingerprint, or its last export was not clean (a failed object, folder or decryption);
- the fingerprint can't be read, or its folder is missing from the repository;
- the exporter itself was rebuilt or updated, or the decryption setting changed;
- the full scan is due.

**If you re-clone or reset the repository**, run once with `--full`. Changes that were exported but never
committed (for example, the push failed and the clone was then thrown away) are otherwise only picked up at the
next full scan, or the next change to that database.

## SSRS report servers

Add a `reportServers` section to the config (see the sample). For each entry, the tool reads the report server's
**catalog database** (`ReportServer` by default) over an ordinary SQL connection, then writes one file per item
under its catalog path and commits it as `<name>: 2 added, 1 changed, 0 removed`, the same as a schema server.
SSRS 2008 and later work, and so does Power BI Report Server. The web service isn't used, so no SSRS role is
needed, only `SELECT` on `dbo.Catalog` and `dbo.DataSource` (or `db_datareader` on the catalog database).

| Catalog item | File |
|---|---|
| Report | `.rdl`, exactly as uploaded (what the portal's Download gives) |
| Shared dataset | `.rsd` |
| Shared data source | `.rds`: extension and credential mode only. See below. |
| Resource, Excel workbook | its own name, for example `logo.png` |
| Report part, model, KPI, mobile report | `.rsc`, `.smdl`, `.kpi`, `.rsmobile` |
| Folder, linked report, Power BI report | not exported |

- **Shared data source connection strings need `reportServerUrl`.** The catalog holds them encrypted with the
  report server's own key. Set `reportServerUrl` to the web service URL (`http://<server>/ReportServer`, not the
  `/Reports` portal) and the tool asks the report server for each one (`ReportService2010.asmx`,
  `GetDataSourceContents`), using the task account's Windows credentials. That account needs the **Content
  Manager** role. Without the URL, the `.rds` records the data extension and how credentials are supplied only.
  Connection strings of data sources embedded in a report are in the `.rdl` either way.
- **Passwords aren't exported.** Stored credentials are never returned by the report server. A `Password=` or
  `Pwd=` typed into a connection string itself is written as `********`.
- **If the web service can't be reached**, each data source keeps its previous file and the run logs a warning,
  so a night with the web service down doesn't commit every connection string as removed.
- **The whole catalog is read before anything is written.** If the read fails (server down, no permission), the
  folder stays as it was and the error is logged (exit code 1).
- **The folder belongs to the tool.** A report deleted from the server is deleted from the folder, and so is
  anything else you put there. A path added to `excludePaths` is removed from the folder on the next run.
- `--server NAME` matches report servers by name too.
