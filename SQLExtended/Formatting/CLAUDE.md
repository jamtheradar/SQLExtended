# Formatting Subsystem

`SqlFormatterService` wraps Microsoft's `TransactSql.ScriptDom` for T-SQL parsing. ScriptDom has limited formatting control, so `PostProcessor` applies additional transformations (spacing, indentation, comma positioning) based on `FormatterOptions`. Options are serialized with `Newtonsoft.Json` using `StringEnumConverter`.

**`PostProcessor` runs over the whole script as text, so any pass whose pattern isn't anchored to a
structure edits comments and string literals too.** Both alias passes shipped broken this way, and the
default option (`AliasStyle = AS`) was one of them:

- **`AliasStyleOption.AS` is a deliberate no-op.** The AST records only *that* an alias exists, not whether
  the source spelled it with AS, so ScriptDom's generator has already emitted `AS` before every table and
  column alias — there is nothing left to add. The pass that used to try was a bare "identifier identifier"
  regex, which turned `SET ANSI_NULLS ON` into `SET AS ANSI_NULLS ON`, `IS NULL` into `IS AS NULL`, and
  `-- Author: Alex Rivera` into `Alex AS Rivera`. `AliasStyleTests.AS_ProducesTheSameOutputAsUnchanged` pins
  the equivalence; if it ever fails, ScriptDom changed — don't answer that by reinstating a text pass.
- **`NoAS` whitelists the two positions an alias can occupy** — a SELECT-list item and a table reference —
  rather than matching `AS <word>` and blacklisting exceptions. Every other `AS` in a script has to survive
  (`CAST(x AS INT)`, `CREATE PROC … AS`, `DECLARE @x AS INT`, `CREATE TYPE x AS TABLE`, `EXECUTE AS OWNER`,
  `WITH cte AS (…)`, `XMLNAMESPACES('u' AS ns)`), and the blacklist version silently produced SQL that no
  longer parses. Where the scan can't be sure, it leaves the `AS` — that is the harmless direction to fail.

Both alias passes and `ColumnEquals` share the string/comment/bracket-aware scanners at the bottom of the
file (`FindKeyword`, `SkipSingleQuote`, `SkipBracketToken`, `Skip*Comment`, `SplitTopLevelCommaItems`) and
the SELECT-list walker `TransformSelectLists`. Use them for anything new here; a fresh `Regex.Replace` over
`sql` is how this class breaks. Alias tests re-parse the formatted output (`FormatAndReparse`), which is what
catches an `AS` added or dropped in the wrong place — assertions on the text alone did not.

Three more things about this file, all of them switches whose failure mode is output that is still valid
T-SQL and merely not what was asked for (`FormatterListLayoutTests` pins each):

- **`KeywordCasing` does not reach function names**, which is why `BuiltInFunctionCase` exists. A call is an
  *identifier* in the AST, so ScriptDom regenerates `row_number()` exactly as it was typed while uppercasing
  the `OVER`/`PARTITION BY` around it. `ApplyBuiltInFunctionCase` re-cases a name only where it is
  **immediately followed by `(` and not preceded by `.`** — those two guards are the entire safety of the
  pass, because most of these names (`Count`, `Max`, `Value`, `Status`) are also perfectly good column names
  and only the call position separates them. The dot test is what leaves `dbo.Count(@x)` — someone's own
  function named after a built-in — alone. The name set comes from `IntelliSense.SqlBuiltInFunctions` rather
  than a second list here, so a function added for completion is cased by the formatter too.
- **`ReflowItems` groups first and renders second**, so `CommaPosition` decides which side of a line break a
  separator lands on and never *where* the list breaks — the same list has to wrap at the same items under
  either setting. It has to place the commas itself because the lists it reflows (INSERT targets, VALUES,
  procedure parameters) are the ones ScriptDom emits on a **single line**: the line-to-line `ApplyLeadingCommas`
  pass never sees them, so a leading-comma profile silently produced trailing commas there for as long as this
  existed. `InsertOpenParenthesisOnSameLine` is a *third* bracket layout, not a rename of
  `InsertParenthesesOnSameLine`: the older switch also pulls the first column up onto the table line and the
  closing bracket onto the last one, which is not what "keep the bracket with the table name" asks for.
- **The FROM/JOIN passes track paren depth, and a derived table is why.** ScriptDom emits a join to a
  subquery across as many lines as the body needs, with the JOIN keyword on one line, the subquery
  spanning several, and the alias arriving on the line that closes the paren
  (`… WHERE TerminationDate IS NULL) AS CC`). Two things follow, and both shipped wrong:
  `ApplyJoinOnSameLine` cannot ask "is the previous line a JOIN line" — the ON's partner is the *closing*
  line, so a JOIN is recorded as **awaiting** its ON and the ON is merged onto whatever precedes it when
  it arrives (cancelled by a clause keyword at the same depth, so a CROSS JOIN doesn't adopt the next
  ON it sees). And `ApplyAlignFromAndJoins` keys its alignment target **by depth**: with one target for
  the script, the subquery's own FROM became the outer query's target and every join below it was
  indented to wherever that inner FROM happened to sit, while the subquery's WHERE cancelled the outer
  target early. Keying by depth also means the nested query is still aligned to *its* FROM rather than
  skipped. `ParenDelta` is the depth source and it does not track block comments, so an unbalanced
  paren inside `/* … */` desyncs it — that fails safe (the pass stops aligning and leaves ScriptDom's
  layout) but it is why neither pass may do anything destructive on the strength of depth alone.
- **`DerivedTableStackedLayout` reflows a subquery in FROM/JOIN/APPLY, and where it runs is the design.**
  Like the CTE pass it runs **early, before the SELECT-column and comma passes**, so the subquery body is
  normalised by them like any other query — that is what makes a stacked derived table read like a stacked
  CTE instead of merely being moved sideways. It emits body lines as **"opener indent + N whole indent
  units"**, never as a column, and that shape is load-bearing: `ApplyAlignFromAndJoins` later pulls the
  `LEFT JOIN (` line out to the FROM's column and has to bring the body and the closing `)` with it, which
  it does as a **prefix swap** (`BlockMove`). A column shift would have to land on fractions of a tab.
  `BlockIsUnitIndented` gates that swap, and is not a formality — ScriptDom's own column alignment shares
  the opener's indent as a prefix often enough to look movable while having *some* lines a whole unit past
  the base and others one or two columns past it, so an ungated swap moves half a subquery and leaves the
  rest. Its look-ahead must apply the enclosing blocks' moves before testing, or a nested derived table is
  measured against an indent its parent has already changed. What is reflowed is decided by
  `FindDerivedTableParen`: a `(SELECT` preceded by a table-reference keyword, which is what keeps it off
  `IN (SELECT …`, `EXISTS (SELECT …` and a scalar subquery in a SELECT list — all of which contain the same
  text and none of which is a table reference. The keyword test is **not anchored to the start of the
  line**: ScriptDom used to keep an APPLY on the FROM line (`FROM A AS a CROSS APPLY (SELECT …`), and
  anchoring would have missed every one. **180.107.0 changed that** — an APPLY now gets its own line,
  indented to the column after `FROM ` (five spaces, not an indent unit) — so `JoinOrApplyLineStart` was
  added for `ApplyAlignFromAndJoins` to pull it back to the FROM's column the way a JOIN already was.
  Leave the test un-anchored anyway: it costs nothing and the generator has now moved this once.
  APPLY is deliberately **not** in `JoinLineStart`, which drives `ApplyJoinOnSameLine` — matching there
  means "awaiting an ON", and an APPLY has none to await.
- **`AlignSetWithUpdate` is a post-pass, not ScriptDom's `IndentSetClause = false`.** That generator option
  does left-align SET, but it also re-flows the clause to its own "river" alignment (SET padded out to the
  item column) and follows neither `IndentSize` nor the tab setting. `ApplySetClauseAlignment` instead shifts
  the SET line to the UPDATE's indent and moves **every line of the clause by that same delta**, so a
  multi-line assignment expression keeps its position relative to its item instead of being flattened to one
  level, and an UPDATE nested in a procedure body keeps the enclosing indent. It runs before the CTE/SELECT/
  comma passes so everything downstream sees the clause at its final indentation.

**`CaseWhenLayout` flattens the CASE before it re-emits it, and that is the whole pass.** ScriptDom has no
CASE layout at all: the expression comes back on one line, broken in exactly one place — where a WHEN's
condition is a multi-part boolean and `MultilineWherePredicatesList` is on — and that break is indented to the
column the *condition* started in. Across several WHENs those columns compound, so a real reporting CASE
walks a couple of hundred characters off the right of the screen and takes the SELECT items after it with it.
A pass that only inserted line breaks would inherit that indent, so `ApplyCaseWhenLayout` collapses every
whitespace run in the region (outside literals) and renders the clauses itself. Four things it must keep
doing:

- **The body is anchored to the column the CASE keyword occupies, not to the line's indent** — a nested CASE
  starts mid-line (`ISNULL(CASE`, `ELSE (CASE`) and there is no other anchor. `IndentToColumn` copies the
  line's own leading whitespace verbatim and spells the rest of the offset in spaces: a tab-indented script
  stays tab-indented, and the part that cannot be an indent is the only part that is invented.
- **It runs after `ApplyAliasStyle` and before `ApplyLeadingCommas`, and both halves of that were bugs.**
  `ColumnEquals` moves the alias to the *head* of the item, which moves the CASE keyword sideways — reflow
  first and every WHEN and END lines up on wherever the CASE used to be. And the comma pass has to see the
  reflowed shape, because it decides which side of a break a separator lands on by comparing indents.
- **A `--` comment inside the region is lifted onto a line of its own.** Flattening a region containing one
  pulls the rest of the CASE into it — the failure this file has now shipped three times. Lifting rather than
  bailing is deliberate: these scripts are full of comments and a pass that gives up on the first one is a
  layout option that silently does nothing.
- **Nested CASEs are reached by resuming the scan just past the `CASE` keyword just emitted**, not by
  recursing over the region — the text around the inner one is then already in its final shape, so it aligns
  to its own column rather than to a column its parent is about to change.

`CaseWhenLayoutTests` re-parses every output. A reflow that drops a keyword, joins two clauses, or comments
one out leaves text that `Assert.Contains` is perfectly happy with, and it walks a comment down every line of
the same CASE for the reason `CommentPreservationTests` does.

**`ApplyLeadingCommas` compares the next item against the indent the current item *started* at.** It used to
compare against the line the comma is coming off, and a multi-line item does not end where it began — a
reflowed CASE ends on its `END`, a wrapped predicate on its last `AND`. Deeper closing line than the item
below it read as "this is not a list": the comma stayed stranded at the end of the `END` line and the column
under it began with none. `itemIndent` is the shallowest code line since the last comma the pass moved, which
for a list is the list's own indent.

**`ColumnEquals` handles every spelling an alias arrives in, and changes none of them.** A warehouse SELECT
list is mostly `AS 'Ongoing Qty'` and `AS #Ongoing`; while the pass only accepted bare and bracketed names it
looked like it worked on half a query, with every AS the user was watching for left exactly where it was. All
four forms are legal to the left of the `=` — `AliasStyleTests` pins that by re-parsing, since an alias and a
comparison against a literal are the same characters. What the pass must **not** do is re-quote on the way
past: bracketed names are the only thing `ApplyIdentifierCase` touches, so rendering `'Split ship'` as
`[Split ship]` hands it to that pass and under `IdentifierCase = Upper` the result set's column heading comes
back as `SPLIT SHIP`. Choosing an alias style may not rename a column. The one rewrite that is kept —
`[Simple]` → `Simple` — is the one that cannot. And because this is the only pass that writes to the *head* of
an item, `LiftLeadingComments` moves a comment the item begins with out in front of it first; left in place it
takes the alias onto its own line (`'Ongoing Qty' = -- CHANGE 2: …`) with the expression stranded below.

**`RestoreCommentLinePlacement` needs the source, and that is why `Apply` takes a third argument.**
ScriptDom moves single-line comments **both ways** — it splits one that trailed a column definition onto its
own line, and it pulls one that was on its own line *up* onto the end of the line above — and **the text it
generates for the two arrangements is identical**. So no pass reading only that text can tell which is which,
and the pass that tried (`RejoinInlineComments`) rejoined everything: a block of three `-- REMOVED: LEFT JOIN …`
notes documenting removed joins came back concatenated onto the end of the FROM line, where they can no longer
be un-commented one at a time. Nothing is disabled by that — a comment can only ever be appended to code, never
the reverse — but it rewrites prose the author placed deliberately. `SqlFormatterService.CollectTrailingComments`
therefore walks `fragment.ScriptTokenStream` and counts the comments the *source* had at the end of a line of
code; the pass restores each comment to whichever side of the line break the source used, in both directions.
Counted rather than a set, because the same note repeated down a column list (`-- from main`) is the shape it
was written for. A null set means "placement unknown" and nothing moves.

**No pass may append to a line whose tail is a `--` comment.** This is the one mistake in this file that
turns working SQL into SQL that no longer runs, rather than SQL that merely looks wrong, and it has now
shipped twice. Any pass that *joins* lines — and most of the layout passes do — puts whatever it appended
inside that comment, and the report is always "the formatter commented out my code":

- **`ApplyJoinOnSameLine`** merges the ON onto the JOIN line it is awaiting. A comment already at the end of
  that line (`INNER JOIN dbo.B b  -- existence filter`) swallowed the entire ON clause. Where the next line
  is a clause keyword that is a parse error several lines away from the comment; where it is another
  predicate it silently becomes a **cross join**. `CanAppendTo` is the guard, and it is the *only* thing
  standing between a trailing comment and a wrong result set.
- **`ApplyCteStackedLayout`** builds each CTE header by joining lines, because the name, the `AS` and the
  `(` arrive on as many lines as ScriptDom felt like using. ScriptDom parks the comment trailing one CTE's
  last body line after the `),` that separates it from the next (`FROM dbo.A AS a), -- note`), so the
  remainder became the *next* CTE's name and the header was emitted as `, -- note Second AS (` — the whole
  second CTE inside a comment. It now lifts comments out of the header as it assembles it (`TakeComment`)
  and re-emits them as their own lines in front of it. Dropping them instead would also have made the parse
  error go away, which is why the test asserts the comment is still there.

Use `FindLineCommentStart` (quote-aware) to test before appending, and prefer lifting the comment onto its
own line over bailing out of the whole pass — these scripts are full of comments, and a pass that gives up
on the first one is a layout option that silently does nothing. `CommentPreservationTests` pins both cases
by **re-parsing the formatted output**, which is the only assertion that catches this: a commented-out
clause is still present in the text, so `Assert.Contains` passes just as happily. It also walks a comment
down every line of a CTE script, because the positions that break are not the ones anyone thinks to write a
case for.

## Format Selection

`SelectionFormatter` (pure; `SelectionFormatterTests`) and `FormatSelectionCommand.cs` at the project root (Ctrl+K,
Ctrl+Shift+F; issue #8). Format SQL is deliberately unchanged — it still formats a selection exactly as selected.

- **The span is chosen from a parse of the whole document**, because the formatter needs a fragment that parses and
  a selection rarely is one. Every `TSqlStatement` is collected with its offsets; the span tried first is the
  narrowest that covers the selection — the innermost statement under a caret, or the siblings the selection runs
  from and to — then each enclosing statement outwards until one formats. That order is what keeps a SELECT inside
  a procedure from reformatting the procedure, and what lifts a selection crossing THEN into ELSE to the IF (the
  sibling span would hand the formatter `… ELSE …`, which does not parse). Whitespace at either end of a selection is
  dropped first, or a selection that began on the blank line above a statement would take in its parent block.
- **If the document does not parse, the selection's whole lines are formatted on their own** — the good part of a
  broken script can still be formatted, which is the usual reason to want this command at all.
- **`Reindent` pads every line after the first with the first line's own leading whitespace**, verbatim (tabs stay
  tabs), skipping blank lines and any line that begins inside a multi-line string, quoted identifier or block comment
  (found with ScriptDom's tokenizer over the formatted text). Padding inside a string changes its value.
- **Offsets, not `AbsoluteCharOffset`.** The command maps the editor's line + `LineCharOffset` onto the text it read
  with `GetText`; `AbsoluteCharOffset` counts CRLF as one character and drifts a line's worth per line.
- Statement fragments include their semicolon, so `TrailingSemicolon = Always` does not leave `;;`
  (`TheSemicolon_IsNotDoubled`).

## Format With… and presets

`PresetFormatter` (pure; `PresetFormatterTests`), `FormatWithCommand.cs` and `Formatting/Ui/FormatWithWindow.xaml`
(issue #8). The window lives in `Ui/` because the test project links `Formatting\*.cs` non-recursively and a WPF
code-behind there would break the test build.

- **Only "Full format" regenerates; every other rule is a token change.** The formatter rebuilds the script from
  ScriptDom's AST, so its layout rules cannot be ticked one at a time — any of them means all of them. The issue
  asked for "indentation, line breaks, comma placement" as separate ticks; the honest offer is Full format plus the
  rules that change individual tokens: keyword case, function-name case (`PostProcessor.ApplyBuiltInFunctionCase`),
  removing unneeded brackets, `LEFT OUTER JOIN` → `LEFT JOIN`, adding semicolons, tabs↔spaces, trimming trailing
  whitespace. The dialog says this under the Full format box.
- **The token rules walk `GetTokenStream`, not a regex over the text** — `PostProcessor`'s regex passes are why this
  file has the history it has. Keyword case touches only tokens ScriptDom types as reserved words, so a column named
  `Status` or `Date` (an Identifier token) is left as typed. If the tokens do not concatenate back to the exact input
  the pass changes nothing.
- **Semicolons need a parse** and are skipped with a note (shown on the status bar) when the text does not parse.
  Containers (`BEGIN…END`, `IF`, `WHILE`, `TRY`, procedure/function/trigger/view bodies) get none themselves — their
  last inner statement ends at the same offset, so they would get a second.
- **Whitespace rules skip lines that start or end inside a multi-line string, quoted identifier or block comment.**
- **Scope:** no selection → whole script; a selection → its whole lines, except Full format, which goes through
  `SelectionFormatter` (whole statements).
- **Presets live in `SQLExtendedSettings.FormatPresets`, null until the user saves one** (`GetFormatPresets` supplies
  "Casing only" and "Full format"). Not initialised with the defaults: Json.NET adds a saved list's items to a list
  the constructor already filled. The dialog writes settings as soon as a preset is saved, moved or deleted.
- **Format Preset 1–5 are `CommandWellOnly` commands with no default shortcut** (`SQLExtended.FormatPreset1`…), the
  slot being the preset's position in the list — Move up is how a preset changes slot. They are deliberately not in
  `MainMenuService.Items`: they exist to be bound to a key, and the dialog's footer says where. `CommandShortcuts` adds
  them after Format With… on the Settings dialog's Keyboard Shortcuts tab, each with the name of the preset in its slot.
