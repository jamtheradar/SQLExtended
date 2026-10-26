# Qualify Columns

`QualifyColumnsCommand` (Ctrl+B, Ctrl+Q — SQL Prompt's Qualify object names; SQLExtended menu and editor right-click) runs `ColumnQualifier` over the
selection, or the whole document when nothing is selected — the same rule as Format SQL — under one undo step.
It came from GitHub issue #3. **It shipped on Ctrl+K, Ctrl+Q and the shortcut never worked**: SSMS 22's own
`Extensions\Microsoft\SSMS.SqlFormatter` binds that chord to Format Document in the same Global scope, the shell
gives it to Microsoft's command, and neither the cache clear nor `/setup` changes that. (Since issue #8 it is on SQL Prompt's
Ctrl+B, Ctrl+Q; what follows is why its previous chord needed the editor scope, which still applies.) Its replacement, Ctrl+K, Ctrl+A, was
Edit.SwapAnchor in the Text Editor scope, which beats a Global binding — so it is bound in the **SQL Query Editor**
scope (`guidSqlQueryEditorScope`), which beats Text Editor. Check a new chord against
`ApplicationPrivateSettings.xml` (the shell's conflict records) and against the Text Editor scope's own bindings before shipping it. `ColumnQualifier` is free of the editor so the test project links it
(`SQLExtended.Tests/Qualify/ColumnQualifierTests.cs`); the command supplies the column lookup from the schema cache,
`LocalTableScanner` (temp tables, table variables) and `SystemCatalogCache` (`sys.`).

**Every wrong answer this pass can give is SQL that still runs.** A column prefixed with the wrong alias binds to that
table whenever it has a column of the same name; a select-list alias qualified in ORDER BY silently sorts by the
underlying column. So the rule throughout is: **prefix a column only when exactly one table can be proved to own it,
and leave it as written otherwise.** Leaving it is always the harmless direction — the status bar counts what was left.

- **Edits are spliced in at ScriptDom's token offsets, never regenerated.** Regenerating would reformat the user's
  script and lose comment placement (see `Formatting/CLAUDE.md` for how much work that took to get right). The output
  is re-parsed before it is written back, and a parse failure, or two overlapping edits, changes nothing.
- **Resolution follows SQL Server's own: innermost scope first, outward only when no table there has the column.** A
  scope containing any source whose columns are unknown (an uncached table, OPENJSON, PIVOT, a derived table with `*`)
  stops the search — the unknown source might own the column. More than one owner in a scope stops it too. Resolving to
  an outer scope also checks that no inner scope exposes the same name (`IsShadowed`).
- **A derived table cannot see the other tables of its FROM; an APPLY's right side can.** `ExplicitVisit(QueryDerivedTable)`
  drops the enclosing scope unless the table is the right side of CROSS/OUTER APPLY (`_lateral`). Getting this wrong
  qualifies a column with a sibling's alias, which would turn "invalid column" into a valid reference to the wrong table.
- **A CTE body sees nothing of the query using it**, and a one-part name matching a CTE of the statement takes the CTE's
  columns (its column list, else its first branch's select list), hiding any real table of that name.
- **ORDER BY is three different things.** A query's own ORDER BY may name a select-list alias, which wins over a column
  of the same name, so those are left. A UNION's ORDER BY names the union's output columns and is left entirely
  (`_setOrderBys`). `OVER (ORDER BY …)` and `WITHIN GROUP (ORDER BY …)` are ordinary column positions.
- **Some column references cannot take a prefix.** The datepart of `DATEADD(Day, …)` parses as a column reference — a
  table with a `Day` column would get `DATEADD(o.Day, …)`. `CONTAINSTABLE`/`FREETEXTTABLE`/semantic table functions take
  bare column names, and `CONTAINS`/`FREETEXT` predicates are skipped too, to be safe.
- **Only SELECT query specifications are touched.** UPDATE/DELETE/MERGE/INSERT targets and their own FROM clauses are
  outside any `QuerySpecification`, so nothing there is aliased or qualified (aliasing an UPDATE target changes the
  statement's shape to `UPDATE p … FROM Person p`). Subqueries inside those statements are handled like any other query.
  The INSERT column list is likewise never prefixed.
- **Where the alias goes is per-construct:** after the name and before `TABLESAMPLE` / `WITH (hints)`, but *after*
  `FOR SYSTEM_TIME …`, which is what the grammar requires. Table variables are aliased too, since `@tv.Col` does not
  parse. TVFs without an alias get one after the closing parenthesis.
- **New aliases come from `JoinClauseBuilder.MakeAlias`**, the one IntelliSense uses, and avoid every table name, alias
  and CTE name in the statement and in the query tree (`NameCollector`). Separate statements start again, so each
  `SELECT … FROM Person` gets `p` rather than `p`, `p2`, `p3` down the script.
- **A qualifier that names a table by its own name** (`Person.AddressId`, `dbo.Person.AddressId`, `Person.*`) is
  rewritten to the new alias — once a table has an alias its name no longer resolves, so leaving them would break the
  query.
- **The command refuses to run on a cache that has not loaded** and starts the load instead: running anyway would alias
  every table and leave every column bare, which reads as the feature not working. Another database referenced in the
  query starts its own load and its scope is left alone this time. Linked server tables are treated as unknown.
- **Columns and table names are matched case-insensitively**, as the rest of the cache's lookups are. Only a
  case-sensitive catalog collation could tell `City` from `city`, and there the prefix still lands on the table that
  has the column — only which spelling the user typed is not checked.

## Tab after `*` (issue #4)

`StarExpandCommandFilter` catches Tab straight after a SELECT-list `*` or `alias.*` and runs
`ColumnQualifier.ExpandStar`: the star becomes the column list, each column prefixed with its table's alias, and a
table it covers that has no alias gets one. It is the same walker in a narrower mode, so every rule above holds — the
edits are spliced at token offsets and the result re-parsed. Setting: `TabExpandsSelectStar` (on by default).

- **Only the tables the star covers are aliased, and bare columns elsewhere are left as written.** A user pressing Tab
  asked for one star, not for Qualify Columns over the statement. Qualifiers that name an aliased table by its own name
  (`Person.AddressId`) are still rewritten, or the statement stops resolving. Aliasing is decided in `BuildScope`
  after the FROM is collected (`ScopeTable.AliasAfter`), because which tables a star covers is only known then.
- **Any unknown source the star covers refuses the whole expansion** (uncached table, OPENJSON, a derived table with
  `*` or with an unnamed column). Leaving one table's columns out would be a shorter select list that still runs.
  `OutputColumns` now returns null for an unnamed column rather than dropping it, for that reason.
- **Columns come out in declared order** — `ColumnSet.Ordered`, fed from `CachedColumn.Ordinal` — which is what
  SQL Server returns for `*`. Every column is prefixed, even for one table, as the issue asked.
- **The gate is cheap and the Tab is otherwise passed on.** Nothing happens unless the character before the caret is
  `*`, `SqlContextAnalyzer` reads it as a SELECT-list star, and no completion list or snippet session is open (both own
  Tab). Past the gate the Tab is swallowed even when expansion fails, with the reason on the status bar: a tab
  character typed after the `*` would stop the next Tab from finding it.
- **The GO batch is parsed first, then the statement alone** (`GetBatchSpan` / `GetStatementSpan`) — a script being
  written rarely parses as a whole. All edits go in one `ITextEdit`, so one undo.
- **Layout follows the formatter options**: `SameLine` puts the list on one line; any stacked layout puts one column per
  line, lined up under the first (tabs in front of the `*` kept), with the formatter's comma position.
- The Ctrl+Space item for `*` (`BuildStarExpansionCompletion`) is older and separate: it does not alias.
- `StarExpansionTests` pins all of this, and checks the edits the editor applies give the same text as `Sql`.
