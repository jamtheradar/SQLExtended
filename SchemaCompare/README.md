# Schema Compare

Side-by-side compare of two database schemas, highlighting what differs, with the noise you don't care about switched off.

Each side can be:

- a **folder export** from SQLExtended's Schema Cache window or the nightly SchemaExporter (`Tables\`, `Views\`, …),
- a **Visual Studio database project**: the `.sqlproj`, or the folder that holds it,
- **any folder of `.sql` files**,
- a **SQL Server database**: click the server button beside the box, pick the server, sign-in (Windows, SQL login,
  Entra MFA or Entra Integrated) and database. It shows in the box as `mssql://server/database`, which you can also
  type or pass on the command line. Each compare scripts the database afresh with the same export as the Schema
  Cache window, so it lines up with an export of it file for file. Sign-ins are remembered per server; a SQL password
  only if you tick **Remember password** (encrypted for your Windows account), otherwise you're asked once a session.

Objects are matched by type and name, not by file path, so an export and a project line up even though one writes
`Tables\dbo.Customer.sql` and the other `dbo\Tables\Customer.sql`, and even though SMO scripts defaults and foreign keys as
separate `ALTER TABLE` statements while a project writes them inline.

## Running

In SSMS: **SQLExtended > Schema Compare** opens it as a tab. There, a database side uses a connection SSMS already has
(the active query window's, or any server the schema cache has seen this session), so there is no sign-in to fill in.

Without SSMS, run the standalone app:

```
dotnet run --project SchemaCompare
SchemaCompare.exe "D:\SchemaHistory\PROD-SQL01\MyGenea" "C:\Source\MyGenea\MyGenea.Database"
```

With two paths on the command line the compare starts straight away. Either can be a database:
`SchemaCompare.exe "mssql://PROD-SQL01/MyGenea" "C:\Source\MyGenea\MyGenea.Database"`.

## Using it

- **Left list**: every object with its status. `≠` different, `◀` only on the left, `▶` only on the right, `=` identical
  (hidden until ticked). Type in the box to filter by name.
- **Diff**: yellow lines changed (the changed words darker), red lines only on the left, green only on the right, grey
  filler opposite them. The strip between the panes shows every difference in the object; click it to jump.
- **Original** shows the scripts as written in the files, instead of the normalised form the options produce.
- **Options** (right panel) decide what counts as a difference. Changes apply straight away, without re-reading files.
  Defaults ignore comments, whitespace, case, brackets, `SET ANSI_NULLS`/`QUOTED_IDENTIFIER`, default index options
  (`PAD_INDEX = OFF, ALLOW_ROW_LOCKS = ON, …`), `ON [PRIMARY]`, column `COLLATE`, system-generated constraint names, extra
  parentheses (`((0))` vs `0`), and users/roles/permissions.
- **Exclude objects**: wildcards, one per line: `HangFire.*`, `Table:dbo.__RefactorLog`. Right-click an object to add it.
- **Equivalent names**: `left = right` pairs for names that differ by environment, e.g.
  `GENGER.Patient = GENGER-uat.Patient`, or `GENGER.Patient = $(PatientDb)` for a project's SQLCMD variable.

## Change script

**Change script** (beside Compare) writes the T-SQL that makes one side like the other, for the differences the list
shows: untick a status or type in the filter to narrow it. For the object you're looking at alone, use **Script** above
the diff (or right-click it in the list).

- **Make right like left / left like right** picks the source; the script runs against the other side.
- Procedures, views, functions and triggers become `CREATE OR ALTER` with the source's own text.
- Tables are altered in place: columns added, altered and dropped, defaults, constraints, indexes and extended
  properties changed one by one. Whatever stands on a changed column is dropped first and put back after; foreign keys
  are dropped first and added last, including those on other tables that point at a key being rebuilt.
- **Drop objects only on the target** is off by default; what wasn't dropped is listed at the top.
- **Wrap in a transaction** (on by default): the first error rolls everything back and the rest doesn't run.
- What can't be done in place — a changed type, sequence or schema, an IDENTITY change, column order, table options —
  is a `-- REVIEW` comment, never guessed. Data-loss steps carry a `-- WARNING`. Both are listed at the top.
- Equivalent names are written the target's way round.

Read it before you run it. The script can be edited in the window before it's copied or saved.

| Key | |
|---|---|
| F5 | Compare (re-reads both sides) |
| Alt+↓ / Alt+↑ (F8 / Shift+F8) | Next / previous difference |
| Ctrl+Alt+↓ / Ctrl+Alt+↑ | Next / previous object |

**Theme** (bottom right): **SSMS** matches whatever theme SSMS is using, including Blue and custom ones, and follows it
when you switch. This needs the SQLExtended extension, which leaves its colours in
`%APPDATA%\SQLExtended\SSMS\theme.ini`. Without the extension it follows Windows' light/dark app setting. ☀ and ☾ force
light or dark.

Options and recent paths are kept in `%APPDATA%\SQLExtended\SchemaCompare\settings.json`.
