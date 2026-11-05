# IntelliSense

## IntelliSense: bracketing inserted names

`IntelliSense/SqlIdentifierQuoting.cs` decides when a completion inserts `[Ongoing Qty]` rather than
`Ongoing Qty`. Two things make it necessary and **neither reports itself at the point it happens**:

- **A name with a space in it is not a syntax error.** `SELECT t.Ongoing Qty` parses — as the column
  `Ongoing` under the alias `Qty` — so it fails as "invalid column name Ongoing", naming a column nobody
  typed, or on a table that does have an `Ongoing` column it silently returns the wrong one under a
  surprising heading. Warehouse columns like `[Ongoing Qty]` and `[Est Ship Date]` make this the normal case.
- **A reserved word fails in every position, with the parser pointing at the punctuation beside it.**
  Verified against ScriptDom: `SELECT t.Order`, `SELECT Order`, `INSERT INTO t (Order)`, `SET Order = 1` and
  `GROUP BY Order` all fail. Non-reserved keywords (`Value`, `Name`, `Status`, `Type`) are perfectly legal
  column names and are left bare — which is why this consults its own reserved list and **not**
  `SqlKeywords`, whose contents would bracket a large share of ordinary column names.

**The reserved list is verified, not asserted.** `SqlIdentifierQuotingTests` cross-checks it against ScriptDom
in both directions — every word on it must actually be rejected as an identifier, and no word the parser
rejects may be missing (the candidate universe being every single word `SqlKeywords` and
`SqlBuiltInFunctions` know about). A wrong entry either way is invisible at runtime. Seven entries
(SECURITYAUDIT, IDENTITYCOL, DUMP, LOAD, DISK, ROWGUIDCOL, PRECISION) are documented as reserved but
*accepted* by ScriptDom; they are kept and named in the test's allowance list, because bracketing a name that
did not need it costs nothing while preferring the parser to the documentation risks the opposite.

**`QuoteObjectIfNeeded` is a second rule, not a convenience wrapper, because two prefixes carry meaning.**
`@Orders` is a table variable and `[@Orders]` is a table *called* "@Orders" — bracketing changes what the SQL
means, so a name starting with `@` is returned untouched. `#tmp` / `##tmp` are legal bare (brackets round them
also work), and bracketing the most frequently completed name in the list would be a daily irritation for no
gain — so the prefix is set aside and the remainder judged on its own. A temp table with a space in it still
gets brackets, around the whole name including the prefix.

- **Only the inserted text is quoted; the list still displays the bare name** — that is what the user is
  typing to filter on, and what the database-name items already did. The quoted form is appended to
  `filterText` as well, or typing `[` would filter the item out of its own list.
- **Every place a name is written into the editor goes through it.** Columns: the `alias.column` items, the
  plain column items, `*` expansion, the foreign-key JOIN predicates, and both INSERT templates (column list
  and `col = value` assignments). Objects: tables/views, system catalog objects, local temp tables and table
  variables, procedures, user functions and system functions — plus the schema qualifier where one is
  emitted. Missing one leaves the same broken SQL arriving from a different menu. What is deliberately *not*
  quoted is everything that is not a user-supplied name: keywords, built-in function names, collation names,
  snippet names, DBCC commands.
- **`FindApplicableSpan` had to change with it.** The dot-qualified branch (`alias.`, `schema.table`) walked
  back over identifier characters but *not* over `[`, so a user who typed `t.[Ong` — which is how anyone
  reaches a name with a space in it — kept their bracket and got `t.[[Ongoing Qty]`. Brackets are now part
  of the replaced segment, which also fixes the pre-existing `[dbo.MyTable` case.
- **A `]` inside the name is doubled.** Legal in a name, and the one input that would otherwise close the
  bracket early and produce SQL that does not parse at all.
- `IsSimpleIdentifier` is deliberately **stricter than T-SQL's rule** for a regular identifier, which also
  allows `@`, `#` and `$` — a column called `#ET` does parse bare. It is bracketed anyway: over-bracketing
  produces SQL that runs, and the prefixes where bracketing would change *meaning* are handled by
  `QuoteObjectIfNeeded` rather than by loosening this.

Free of the VS editor assemblies so the test project links it (`SQLExtended.Tests/SqlIdentifierQuotingTests.cs`),
the same split `ExportFileNaming` exists for. The behaviour tests **re-parse** `SELECT t.<quoted> FROM dbo.T AS t`
and assert one select element carrying **no alias** — comparing strings cannot tell a bracketed name from a
name plus an alias, which is the entire bug — and check the quoted forms in the four positions a bare reserved
word was shown to fail in.

## IntelliSense: what Space may commit (issue #11)

Space ends every word, so whatever is hard-selected when a word is finished replaces it. `DELETE FROM` + Space
became `DELETE DATEFROMPARTS`: the list after DELETE held no plain FROM, the substring match was the best item,
and it was hard-selected. The earlier fixes added the missing keyword to one context at a time (ON after a JOIN,
FROM after SELECT), and the next context always had a gap. `CompletionCommitRules` closes the class:

- **Only a word-start match is hard-selected** (`IsWordStartMatch`, used by `SqlCompletionItemManager`): the
  typed text starts the display text, the name after the last dot, or one of the space-separated filter-text
  forms (that is where `[Ongoing Qty]` and `'DISABLE_…` live). A substring or camel-hump best match is listed
  but soft-selected.
- **Space never commits** (`SpaceMayCommit`, in `ShouldCommitCompletion`) with nothing typed, a multi-word item
  on its first word, a non-word-start match, or **a different item over a complete reserved word** — `IS` must
  not become `ISNULL`, nor `IN` become `INT`. It is checked in the commit manager as well as by soft selection,
  so it holds whatever the editor does with a soft-selected item on Space.

`CompletionCommitRulesTests` pins these, including a sweep of every built-in function against the common
reserved words. Tab and Enter are not restricted.

## IntelliSense: the system catalog

`IntelliSense/` completes from `SchemaCache`, and that cache loads **`is_ms_shipped = 0` only** — so every
system object was absent from it by construction and `sys.` produced an empty list. `Cache/SystemCatalogCache.cs`
holds the other half: the `sys` and `INFORMATION_SCHEMA` surface (catalog views, DMVs, table-valued DMVs,
system functions, and every column on them), read with `is_ms_shipped = 1` as the exact complement so nothing
is loaded twice.

**It is keyed per server, not per database, and that is the whole reason it is a separate class.** Dropping
the `is_ms_shipped` filter in `SchemaCacheLoader` would have been the two-character version of this feature,
and it would then load ~1,100 objects and ~9,000 columns again *for every database on the instance*. The
catalog surface belongs to the engine build, not to the database — every database on an instance exposes the
same catalog views — so it is read once per server and shared. One query, per server, per session.

That has a price worth knowing: the read runs against whatever database the connection is already pointing at,
with no `USE`, so **whichever database the first completion happened in is the one that answers for the server
all session**. Visible only on a contained database or Azure SQL Database, whose surface differs from a box
instance's. A load per database costs far more than that edge is worth.

- **Nothing is persisted to SQLite.** It is one query, and the answer changes only when the instance is patched.
- **Both a load-in-flight and a failed load are memoised, per server.** Completion asks on every keystroke:
  without the first a slow instance stacks a query per character, and without the second a login that cannot
  read the catalog pays the full command timeout per character for an answer that will not change.
  `SchemaCache.ClearAll` clears it — the only way to retry a server that failed earlier in the session, the
  same arrangement `ModuleDecryptionService`'s negative memos have.
- **System objects are only ever offered behind a typed schema qualifier.** Folding them into the bare `FROM`
  list would bury a database's own tables under ~1,100 system objects. The user asks by typing `sys.`.
- **The column fallback in `GetColumnsWithFlags` fires after the schema cache misses, not before it**, so a
  user table in a schema literally named `sys` still wins.
- Types are restricted to V/U/IF/TF/FN. **System stored and extended procedures (P/X) are deliberately not
  loaded** — they belong to the EXEC completion path, which does not read this cache. S (internal base tables,
  DAC-only) is not usefully queryable.

`Cache/SystemCatalogSql.cs` is free of SqlClient so the test project can link it
(`SQLExtended.Tests/Cache/SystemCatalogSqlTests.cs`), the same split `ExportFileNaming` and
`MonitorCollection` exist for — and for the same reason, that every failure here is silent: the cache
swallows the exception and memoises the server as failed, which on screen is indistinguishable from a
permission problem or from the load not having finished. The tests pin that it parses, that it still returns
**exactly two result sets** in the order the reader's single `NextResult` steps through (one statement more or
less and the columns are read as objects — a populated, entirely wrong list rather than an error), and that it
reads `sys.all_objects`/`sys.all_columns` rather than `sys.objects`/`sys.columns`. That last substitution is
the one that leaves the feature parsing, connecting and succeeding while returning nothing.

Parsing cannot tell whether a column exists on a given release. **None of this has been run against a live
instance** — worth doing before trusting the column shapes.

## IntelliSense: settings that were never read

Fifteen settings were persisted to the settings file and bound to their controls in the dialog, and
**nothing anywhere read any of them** — a repo-wide search for each name returned the property, the line
that loads the control and the line that saves it, and nothing else. Six were this subsystem's
(`IntelliSenseEnabled`, `SuppressBuiltInIntelliSense`, `AutoTriggerAfterKeyword`, `CamelCaseMatching`,
`ShowColumnTypeInfo`, `ShowRowCounts`); the rest were Search's and the schema cache's, and are recorded in
their own notes. All fifteen are wired now, and `SettingsAreReadTests` fails the build if a new one joins
them — a source scan, because a dead setting compiles, round-trips to JSON and loads its checkbox, so no
behaviour test of the feature it is meant to control can notice that nothing calls it.

The master switch is the one a user reaches for first when the editor misbehaves, so its failure mode is
worse than a dead feature: unticking "Enable IntelliSense", seeing no change and concluding the extension is
innocent is a wrong answer delivered confidently, and it costs the whole diagnosis.

- **The master switch is gated in `SqlCompletionSource.InitializeCompletion`, not in the provider.** Every
  route into the list — typing, Ctrl+Space, `SqlCompletionCommandFilter`, `SqlInvokeCompletionCommandHandler`
  — arrives through that one method, and the explicit-trigger paths already treat a null session as "not
  handled" and fall through. Refusing to compose the MEF part instead would have needed an SSMS restart to
  take effect, which is not what a checkbox in a dialog promises.
- **`SuppressBuiltInIntelliSense` swallows the completion commands** (COMPLETEWORD / AUTOCOMPLETE /
  SHOWMEMBERLIST) when our own list did not open, rather than passing them down the `IOleCommandTarget`
  chain — handing them on is what lets SSMS's list open in place of ours, which is the interference the
  setting names. It is honoured **only while `IntelliSenseEnabled` is on**: swallowing them with our
  completion off leaves the editor with no completion at all, which is a worse setting than a dead one.
  It does not reach SSMS's auto-list-on-typing, and the dialog's description says so.

- **`AutoTriggerAfterKeyword` is enforced by declining to participate, not by dismissing a session.** With
  nothing typed yet, participating *is* the list opening by itself the moment a keyword lands, so the gate
  is a `return default` from `InitializeCompletion` when the applicable span is empty and the trigger is not
  an explicit Ctrl+Space. `ColumnAfterDot` is exempt: that list opened because the user typed `.`, which is
  not a keyword trigger and not what the checkbox names.
- **`CamelCaseMatching` ranks last (6), below every exact and substring rule.** `IsCamelCaseMatch` had been
  sitting in `SqlCompletionContext` unreferenced, so nothing had ever called it — `CamelCaseMatchTests`
  pins it now. Every item it admits is one the five rules above already rejected, so turning it on can only
  add to the bottom of the list; turning it off leaves the "nothing matched, dismiss" path exactly as it was.
- **The display settings are read once per session, on the UI thread.** `SqlCompletionSource` captures the
  settings object in `InitializeCompletion` and the background phases read that copy, because
  `GetCompletionContextAsync` — where the items and their suffixes are built — is a worker thread and
  `SQLExtendedSettings.Current` must not be faulted in from one. It also means one list is built from one
  consistent set of values. `ShowColumnTypeInfo` off still keeps the `(PK)` / `(FK)` / `(Identity)` flags:
  those are not type information, and they are what people scan the line for.

A dialog that shows a switch which does nothing is indistinguishable from the feature behind it being broken.

## IntelliSense: recasing while typing edits *after* the keystroke, not during it

`KeywordCaseController` hooks `ITextBuffer.Changed` and rewrites the word just completed. It used to apply
that replace from **inside** the event handler, and both halves of that were wrong in ways nothing reports:

- The edit was **re-entrant** — applied to the buffer that was still raising the event for the character
  just typed, with the editor's own typing and caret handling part way through it.
- The span was computed against `e.After` but applied to `CreateEdit()`'s `CurrentSnapshot`, and those are
  **not the same snapshot** whenever another `Changed` handler edits ahead of this one — `SnippetSession`'s
  linked-field sync is one in this codebase. Every offset then shifts and the replace lands somewhere else,
  duplicating text instead of recasing it.

Both failures are swallowed by the method's catch (a recasing failure must never disrupt typing), so what
reaches the screen is mangled typing with nothing naming the cause. The recase is now posted to the view's
dispatcher at `Normal` priority — which runs **ahead of pending input**, so fast typing cannot outrun it —
and re-validated against the snapshot as it is then: the replace happens only if an `ITrackingSpan` still
resolves to the same word, unchanged. Anything else (the user kept typing, an undo ran, a completion
replaced the span) means the recase no longer applies, and applying it anyway is what corrupts the line.

**The word set and the recaser must split words the same way.** `SqlKeywords.BuildKeywordWordSet` used to
split entries into runs of letters, while `IsWordChar` treats `_` and digits as part of a word. So
`IDENTITY_INSERT`, `SQL_VARIANT` and `DATETIME2` were looked up whole and never matched, while their pieces
(`STATUS` from `@@FETCH_STATUS`) did go in and recased ordinary column names. Both now run over letters,
digits and `_`. **Every word in `SqlKeywords` is recased as it is typed** unless the entry is marked
`recase: false`. That flag is for multi-word entries with a column name among their words: `STATISTICS IO`
/ `PROFILE`, `TRANSACTION ISOLATION LEVEL` and the isolation levels (`READ COMMITTED`, `SNAPSHOT`, …) are
offered in completion but never recased, because the recaser sees one word with no context and cannot tell
`SET STATISTICS IO` from a column called `IO`. One-word options that *are* column names (`LANGUAGE`,
`ROWCOUNT`, `TEXTSIZE`) are left out altogether. `SqlKeywordWordTests` pins which words stay out of the set.
The levels have their own context, `AfterIsolationLevel`, which is not part of `General`.

## IntelliSense: joining on a foreign key from the table position

There are two halves to the foreign-key join support, and they answer different questions at different
moments. `BuildJoinConditionCompletion` answers `JOIN dbo.Customer c ON <here>` — the table has been named,
the predicate is all that is left. `JoinClauseBuilder` answers `JOIN <here>`, where the table has *not* been
chosen, so a suggestion has to carry the table, an alias for it and the predicate together as one committed
string: `dbo.Customer c ON c.Id = o.CustomerId`.

- **Both directions of the key are offered, and the reverse one needed a new cache lookup.**
  `GetForeignKeys` only answers "what does this table point at", which finds `Order → Customer` and is blind
  to `OrderItem → Order` — the child table, which is at least as often the one being joined.
  `ISchemaCache.GetReferencingForeignKeys` is the complement, filtering the same per-database list on the
  referenced side. It compares schemas only when both sides name one, for the reason `ReferencesTable` does:
  a parsed reference usually omits the schema, and requiring a match there turns every incoming key into a
  table that silently appears to have no relationships.
- **The new table's alias leads the predicate in both directions** — `c.Id = o.CustomerId` *and*
  `oi.OrderId = o.Id`. Letting the sides follow the direction of the key reads as an inconsistency in a list
  where the two are adjacent, and nothing downstream would ever report it.
- **One alias per candidate table, not per key.** Two keys to the same table (`BillToAddressId` and
  `ShipToAddressId`, both → `Address`) are alternatives and only one is ever committed; separate aliases
  would imply both could be taken. The key's *name* is the item's suffix, because that is the only thing
  that tells two otherwise identical `dbo.Address a ON ...` lines apart.
- **A generated alias avoids everything already in scope, and every reserved word.** A collision with an
  existing alias is the failure that still runs — the predicate quietly binds to the enclosing table instead
  of the joined one. A reserved word is the failure that does not: `InventoryStock` initials to `is`, and
  `JOIN dbo.InventoryStock is ON ...` does not parse, with the parser blaming the punctuation beside it.
  Both are resolved by the same counter (`is2`), not by bracketing — `[is]` would be ugly in every query the
  completion is ever pasted into, where `is2` is ugly once.
- **They are offered above the plain table list, never instead of it.** Joining a table with no declared
  relationship is entirely ordinary, and a list that silently dropped every unrelated table would be worse
  than offering nothing.
- **`IsJoinTarget` keys on the word JOIN, not on "a table name is expected here".** `FROM`, `INSERT INTO`
  and `UPDATE` have no left-hand table for a predicate to reference, and `CROSS APPLY` takes a table but has
  no `ON` clause at all — a whole join clause in any of those positions inserts SQL that cannot be finished.
  The bare `\bJOIN\s+$` covers `INNER`, `LEFT OUTER`, `FULL OUTER` and the rest for free, since all of them
  end in the word.

`JoinClauseBuilder` is free of the VS editor assemblies so the test project links it, the same split
`SqlIdentifierQuoting` exists for, and `JoinClauseBuilderTests` pins the parts that fail silently: an
inverted predicate and a reused alias both produce SQL that runs and returns the wrong rows, and a composite
key paired out of order joins on the wrong column of the right table. `SqlCompletionSource` keeps the cache
lookups and the item construction; the builder gets plain `CachedForeignKey` rows and returns plain strings.

## IntelliSense: an alias for every table picked after FROM / JOIN

`AutoAliasTables` (on by default) inserts a picked table as `dbo.Company c`, and offers the columns of an
aliased table as `c.ACTIVE` **even when it is the only table** — before it, a single table's columns were
always bare. The alias is `JoinClauseBuilder.MakeAlias`, the same one the foreign-key join clauses use, so the
two agree; it is **no longer capped at four letters** (`ORDER_LINE_ITEM_TAX_PAYMENT` → `olitp`), because a
cut-off alias stops telling apart tables that share a prefix.

- **Only where the grammar has room for an alias** — `AnalysisResult.AcceptsAlias`: any JOIN, and a FROM that
  is not `DELETE [TOP (n)] FROM`. `INSERT INTO dbo.T t`, `UPDATE dbo.T t SET`, `DELETE FROM dbo.T t` and
  `TRUNCATE TABLE dbo.T t` do not parse. `TableAliasContextTests` pins both sides **with ScriptDom**, not by
  assertion: every accepting position must parse with `dbo.T t` committed there, every refusing one must not.
- **Not when the name being replaced already has an alias after it** (`IsFollowedByAlias`, same line only):
  re-picking `FROM dbo.Comp| c` with Ctrl+Space would otherwise give `c c`. A reserved word next (WHERE,
  JOIN, WITH, ON) is not an alias; `AS`, a bracket or any other word is.
- **It avoids the aliases and table names already in the statement**, via the counter (`gc2`) — the same
  rule, and the same reason, as the join clauses: a collision still runs and binds to the wrong table.
- Tables, views, temp tables and table variables get one; **table-valued functions do not** — their
  arguments come next, and an alias in front of `(` does not parse.

## IntelliSense: the UPDATE … SET template

`UPDATE [db.][schema.]table ` offers "(all columns) SET col = val" (`UpdateSetTemplate` context,
`UpdateTemplateBuilder`), the UPDATE counterpart of the INSERT templates, with the same placeholders
(`DefaultValueFor`) and the same formatter options (indent, comma position, keyword case).

- **The WHERE is on the primary key, and the key columns are left out of the SET.** A template without a WHERE
  is an UPDATE of every row that looks finished. A table with no primary key gets `WHERE 1 = 0`, which updates
  nothing until a real predicate is written. A composite key is ANDed in the key's declared order.
- **Identity, computed and rowversion/timestamp columns are never set** — any one of them fails the whole
  statement. (The INSERT template does not skip rowversion; worth fixing there too.)
- **Words after UPDATE that are not a table are refused by the pattern** — STATISTICS, TOP, SET, and the
  foreign-key / GRANT forms (`ON UPDATE CASCADE`, `ON UPDATE NO ACTION`, `GRANT UPDATE ON`).
  `UpdateTemplateTests` pins those, the exact template text, and that the committed statement parses.
- **The filter text leaves out "set"**, so typing SET by hand narrows the list away instead of committing the
  template over it.
- `UPDATE c ` naming an alias from a FROM further on is not resolved — the cache has no table `c`, so no item.

## IntelliSense: CREATE INDEX column lists

`CREATE [UNIQUE] [NON]CLUSTERED [COLUMNSTORE] INDEX ix ON dbo.T (` — and `CREATE STATISTICS`, which takes
the same list — is its own context, `IndexColumn`, offering the target table's columns in both the key list
and the `INCLUDE (` list, minus any column already named in either (a column cannot be both key and include).

- **It is checked before the function-argument probe.** `Orders(` looks exactly like a call to a function
  named Orders; if the table happened to share a built-in's name the list would offer data types or dateparts.
- **A column is only offered right after `(` or `,`.** After `(CustomerId ` the next token is ASC/DESC or a
  comma, and a column there inserts SQL that does not parse.
- **A filtered index's `WHERE` offers columns at its start and after each `AND` / `OR`, and nowhere else.**
  A filter compares a column with a constant, so after `=`, `>=` or inside `IN (` a column is never valid —
  the opposite of the query `WHERE`, which is why this does not reuse `ColumnContextPattern`. Nothing is
  excluded there (`Qty >= 1 AND Qty < 10` is ordinary), and a `WITH (` options clause or `;` ends the filter.
- **`INDEX name ON` is in `TableContextBeforeDot`**, or `ON dbo.` would be read as the alias `dbo` and
  offer that non-existent alias's (empty) column list instead of tables. The rule needs the `INDEX name`
  in front of `ON`: a bare `ON` would break every `JOIN ... ON o.` predicate (`IndexColumnContextTests` pins it).

## IntelliSense: OPTION ( … ) query hints, USE HINT names and WITH ( … ) table hints

`OPTION (` offers the query hints (`SqlQueryHints.Hints`) and `USE HINT (` offers the hint names
(`SqlQueryHints.UseHintNames`), inserted as quoted literals. Neither needs a connection.

- **Both lists are verified against ScriptDom, not the documentation.** `QueryHintContextTests` parses every
  hint (with a sample argument) and every name. `DISABLE_OPTIMIZED_PLAN_FORCING` is documented for SQL Server
  2022 but ScriptDom rejects it, so it is left out — offering it would insert SQL the formatter cannot parse.
  Synapse/PDW-only hints (`LABEL`, `FORCE EXTERNALPUSHDOWN`, …) are left out as well.
- **A slot is found by paren depth, not by regex.** The innermost `(` still open at the cursor decides it, so
  `OPTION (OPTIMIZE FOR (@p = 1), ` is a hint slot and `OPTIMIZE FOR (` is not; and only straight after that
  `(` or a top-level `,` — after `MAXDOP ` the next thing is the hint's argument.
- **Checked before the function-argument probe**, since `OPTION (` and `HINT (` both look like calls.
- **Table hints (`SqlQueryHints.TableHints`) use the same scan**, for a table reference's `WITH (` and for
  `OPTION (TABLE HINT (obj, ` after the object. **The table reference in front of `WITH` is what makes it a
  table hint** (`TableHintOpenPattern`): `WITH` is also a CTE (`WITH cte (`) and an options clause
  (`CREATE INDEX … (c) WITH (`, `REBUILD WITH (`), and offering NOLOCK there inserts SQL that does not parse.
  The alias group refuses the word WITH, or `FROM dbo.T WITH` would read WITH as the alias.
- **`WITH (INDEX (` and `WITH (FORCESEEK (` offer the hinted table's index names** from the schema cache.
  The table is the reference in front of the *enclosing* `WITH (`, one level down the paren stack, which is
  why `TableHintOpenPattern` captures q1/q2/tbl. `UPDATE o WITH (` names an alias, so an unqualified name is
  first looked up as an alias of the statement (`AliasResolver`). FORCESEEK takes one index, so it is not
  offered after a comma. The cache loads indexes for `U` objects only, so an indexed view (NOEXPAND) gets none.
- **An opening quote already typed is part of the replaced span** for a hint name (`FindApplicableSpan`),
  or committing after `('DISA` would produce `(''DISABLE_…'`.

## IntelliSense: closing single quotes as they are typed

`QuotePairCommandFilter` turns a typed `'` into `''` with the caret between, steps over the closing quote it
inserted, and removes an empty pair it inserted on Backspace (`AutoCloseQuotes`, on by default).
`SqlQuotePairing` decides when, and is free of the editor so `SqlQuotePairingTests` links it.

- **The quote is swallowed and the pair inserted in one edit, not passed down the chain and patched after.**
  Passed down, the keystroke can open a completion session whose applicable span ends at the caret, and a
  closing quote inserted afterwards lands inside that span and becomes filter text. One edit is also one undo.
- **The region is lexed from the top of the document, not the line.** Pairing the quote that *closes* a
  multi-line string opens a new one and silently swallows the rest of the script — the costly direction.
  Quotes are rare keystrokes, so the scan is cheap where it runs. Block comments nest, as T-SQL's do.
- **Only the closer this filter inserted is ever stepped over or deleted**, tracked by `ITrackingPoint`s and
  retired the moment the caret leaves the pair. Overtyping any `'` in front of the caret would make escaping
  a quote in an existing literal (`'It''s'`) impossible to type.
- **A quote glued to a word is an apostrophe** (`O'Brien`) and is not paired — except the `N` of `N'…'`.
- **A USE HINT name commits over the pair** (`SqlCompletionCommitManager.TryCommitQuotedLiteral`): its item
  inserts its own quotes and the span already holds the opening one, so the closer just past the span is
  replaced too, or the commit leaves `'DISABLE_…''`.

## IntelliSense: synonyms

The schema cache always loaded synonyms (`SN`), but every completion list filtered on object type and dropped
them, so `FROM`, `EXEC` and `dbo.` never offered one. A synonym is only useful if it shows up **where its
target would**, so the cache records what each one points at: `CachedObject.BaseObjectName`
(`sys.synonyms.base_object_name`) and `BaseObjectType`, both persisted as SQLite columns. An older cache file
gets the columns from `AddMissingObjectColumns`, with NULLs until the next full load.

- **Same-database targets resolve in the object query itself. Cross-database ones need a second query per
  target database** (`ResolveCrossDatabaseSynonyms`), because a join on `sys.objects` only reads the current
  database. A linked-server target, or a database that is offline or closed to this login, stays unresolved
  (null), and nothing reports it.
- **An unresolved synonym is offered in both the table list and the EXEC list.** Leaving it out is the gap
  this closes, and cross-server synonyms are the commonest kind. It is *not* offered as a function after `dbo.`,
  which would be a third copy of it. Only a synonym known to point at a function goes there.
- **Columns, indexes and parameters follow the synonym** (`SchemaCache.GetColumns` / `GetIndexes` /
  `GetParameters`) when the name has none of its own, so `FROM dbo.Syn s WHERE s.` and the EXEC parameter
  expansion work. That is one hop: a synonym cannot point at another synonym. A target in a database not yet
  cached gives nothing on the first try. `GetColumnsWithFlags` starts that database's load, so the next
  completion has the columns.
- `SynonymTarget.Parse` splits the name the way PARSENAME does. `SynonymTargetTests` pins it, because a wrong
  split looks columns up in the wrong place, and that just shows up as an empty list.

## IntelliSense: linked servers

`Cache/LinkedServerCache.cs` holds three levels, and the `LinkedServerCompletion` setting (Off / Names / Databases /
Objects, default **Names**) decides how far completion reaches:

1. **Names**: from the local `sys.servers WHERE is_linked = 1`. They appear in the bare `FROM` / `JOIN` / `EXEC`
   list, after `OPENQUERY(` and after `EXEC (…) AT`. The list is read when completion first runs, like the system
   catalog.
2. **Databases**: `sp_catalogs`, after `LinkedSrv.` in a table or EXEC position.
3. **Tables, views and columns**: `sp_tables_ex` after `LinkedSrv.db.` (schemas) and `LinkedSrv.db.schema.`
   (objects). `sp_columns_ex` is read **one table at a time**, when `alias.` or a column list first needs it, so
   the first try comes back empty.

- **The catalog procedures, not `OPENQUERY` on the remote `sys.` views.** They go through the linked server's
  own driver, so Oracle and ODBC targets work too, and they need neither RPC Out nor SQL Server at the far end.
  The cost is that **procedures are never listed**: there is no `sp_procedures_ex`. So `EXEC LinkedSrv.db.`
  stops at the database. The settings dialog says so.
- **The name arguments of `sp_tables_ex` / `sp_columns_ex` are LIKE patterns.** `Order_Lines` also matches
  `OrderXLines`, so `LoadColumns` keeps exact matches only. Drop that filter and a table quietly gets another
  table's columns mixed in.
- **`COLUMN_SIZE` counts characters, not bytes**, so `LinkedServerSql.FormatType` exists rather than reusing
  `SchemaCacheLoader.FormatDataType`, which halves an nvarchar's byte length.
- **Every load is remembered while it runs and after it fails, per local server.** A linked server that is down
  costs its driver's full login timeout, and completion asks on every keystroke. A failure stays until the
  Schema Cache window's right-click refresh, or Clear All Cache. **Nothing is saved to disk.** Without data
  access (`sp_serveroption 'data access'`), level 2 fails straight away with that reason instead of a round trip.
- **The server part survives into column lookups.** `AliasResolver.TableReference.Server` comes from ScriptDom's
  `ServerIdentifier`, and the regex fallback now takes up to three qualifiers, with empty parts (`LS..dbo.T`).
  `GetColumnsForReference` sends a four-part reference to the linked cache and leaves its missing parts null. It
  does **not** fill in the current database or `dbo`, because the remote default schema need not be `dbo`.
  Losing the server part would look the remote table up locally and find nothing.
- **A synonym through a linked server** gets its columns from here too (`GetColumnsWithFlags`).
- **`EXEC (…) AT` must have the `EXEC (…)` in front of it.** `GETDATE() AT TIME ZONE` also has a `) AT `.
  Literals are skipped, because a pass-through query's own parentheses sit inside one. The 500-character window
  means a longer pass-through query is not recognised.
- `StripTrailingPartialIdentifier` now removes an unclosed `[name` whole. `[LNK\INS` and `[My Ta` used to stop
  at the backslash or space and spoil the context check.
- **The Schema Cache window** shows a "Linked servers" branch under each server: state dot, counts, and the
  error as a tooltip. Right-click refreshes the list, a server's databases, or a database's tables. The branch
  is hidden when the setting is Off and nothing has been read.

`LinkedServerContextTests` pins the contexts, four-part names in `AliasResolver` (both the ScriptDom path and the
regex path), the type formatting, and the catalog filtering. **None of the remote calls have been run against a
real linked server**, and the result column names (`CATALOG_NAME`, `TABLE_SCHEM`, `COLUMN_SIZE`, …) are taken from
the documentation.
