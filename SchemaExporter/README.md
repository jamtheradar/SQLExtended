# SchemaExporter

A console tool that runs nightly. It exports the schema of every SQL Server you list, writing one `.sql` file
per object, and commits whatever changed to a git repository in Azure DevOps or GitHub. It can also export
SSRS report servers (reports, shared data sources and datasets, resources) and the results of any query you
give it (scheduled jobs, config tables, anything) into the same repository. The result is a lazy
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

## Query exports

Add a `queries` section (see the sample) to export the result of any SQL you write: one file per row, or one file
for the whole result. It's meant for things that live in tables rather than in the schema, such as Hangfire,
DataByte.Scheduler and SQL Agent job lists, or config and mapping tables. Each query is committed as
`<name>: 1 added, 2 changed, 1 removed`, so the history shows the day a job was added, rescheduled, disabled or
removed. Ready-made queries for those three schedulers are in `samples\queries` (copied beside the exe).

`--queries-only` runs just the queries and skips the schema and report servers. Use it to schedule the queries on
their own from the same config, for example more often than the nightly schema export. `--server NAME` narrows it
to one query.

| Setting | Default | |
|---|---|---|
| `name` | required | Names the commit; `--server NAME` matches it. Must differ from every server's name. |
| `query` / `queryFile` | one required | The SQL, or a `.sql` file relative to the config. Only the first result set is used. |
| `connectionString`, `database` | | The connection when there are no `targets`, and the fallback for targets that leave them out. |
| `targets` | none | `[{ name, connectionString, database }]`: run the same query against each. `{target}` is the target's name. |
| `mode` | `filePerRow` | Or `singleFile`. |
| `format` | `json` | `json`, `text` (`Column: value` lines), `csv` (singleFile only), `raw` (filePerRow only: `contentColumn` written as-is, binary or text). |
| `fileName` | name (singleFile) | Template of `{Column}` tokens and `{target}`. `/` makes subfolders. Required for filePerRow, which needs at least one column token. |
| `extension` | from format | `.json`, `.txt`, `.csv`, none for raw. `""` = none. |
| `folder` | name | Under `outputPath`. `/` makes subfolders; may use `{target}`. |
| `excludeColumns` | none | Left out of the content, still usable in `fileName`. |
| `minRows` | `1` | A target returning fewer rows is treated as broken. |
| `maxDeletePercent` | `25` | A run that would delete more than this share of a folder's files (and more than one file) is refused. `null` = off. |
| `commandTimeoutSeconds` | `600` | `0` = no limit. |

**Whatever the query returns is committed.** The tool can't tell a value that changes every night from one that
matters, so keep the SQL to definitions: leave out run times, durations and counts, or turn them into a state that
only changes when something happens (`Healthy` / `Failing` / `Stale`), as the samples do. Otherwise every query
commits every night.

- **Output is deterministic.** Columns are written in query order and nulls are explicit. Dates are ISO 8601 without
  trailing zeros, numbers use `.`, GUIDs are lower case and binary is base64. Text is UTF-8 without a BOM, CRLF. For
  `singleFile`, put an `ORDER BY` in the query; the run warns if there isn't one.
- **A failure never shows up as a drop.** Every target is read before anything is written. A folder is left exactly
  as it was when one of its targets fails, returns fewer than `minRows` rows, or returns a result that doesn't fit the
  config (a `fileName` token with no column, two columns of one name, or a column named `target`). The same applies
  when a run would delete more than `maxDeletePercent` of the folder. Exit code 1 in each case. `--force` lets the
  deletes through for one run.
- **File names are made safe.** A value can't add a folder or climb out of one (`/`, `\` and `..` become `_`), and
  empty values become `_`. When two rows map to one file (names are compared ignoring case, like Windows), the first
  keeps it and the run logs a warning. Targets sharing a folder should put `{target}` in `fileName`.
- **The folder belongs to the query.** Anything in it that tonight's result doesn't produce is deleted. A query's
  folder can't be the same as, inside, or around any other server's, report server's or query's folder; the config
  is refused.
- **Read-only.** The query runs inside a transaction that is always rolled back, so a statement that writes changes
  nothing. Still give the login read-only rights only.
- Columns of CLR types (`hierarchyid`, `geography`, `geometry`) need a `CAST` to text in the query.
