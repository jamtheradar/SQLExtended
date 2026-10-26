# SQL Search: Agent job steps

`Search/` searches the schema cache — object names and columns from memory, definitions from the SQLite FTS
index. `Search/JobStepSearchService.cs` adds the one source that is not in the cache at all: SQL Server Agent
job step commands, matched alongside step and job names, behind the **Agent jobs** checkbox in "Search in".

Jobs are a *server* object, and that is what shapes everything here:

- **It runs once per search, not once per selected database.** A step's target database is a column on the
  step (`sysjobsteps.database_name`, and only meaningful for the TSQL subsystem), not the database the job
  lives in — scoping the search to the database selection would drop every non-TSQL step. The type filter
  (Tables/Views/…) does not apply to it either. The checkbox's tooltip says both.
- **It is read live, not cached.** `sysjobsteps` is a few hundred rows behind a server-side LIKE, the search
  is already debounced, and job steps are edited far more casually than modules — a stale command body is
  worth less than the round trip that avoids it.
- **The match is server-side under an explicit `COLLATE Latin1_General_CI_AS`.** On a case-sensitive instance
  msdb would otherwise answer case-sensitively while every other part of the search is `OrdinalIgnoreCase`,
  so the same term would find a procedure and miss the job step that calls it. *Which* field matched is then
  worked out client-side, where the text is in hand and the snippet has to be built anyway — and a row the
  client scan cannot place (CI and OrdinalIgnoreCase disagree on width, kana and a few accent pairs) is kept
  with a generic label rather than dropped. The server is the authority on what matched — **except under Match
  whole word**, below, where the server only narrows and the client decides.
- **The LIKE pattern escapes `[` as well as `%` and `_`.** Searching for a bracketed identifier is a normal
  thing to type here, and unescaped it opens a character class that swallows the rest of the pattern — the
  search then returns nothing, which is indistinguishable from no matches. The escape character is `\`, which
  is not LIKE's default, so the `ESCAPE '\'` clause is load-bearing; `JobStepSearchTests` pins both, along
  with parsing the two batches.
- **A login outside SQLAgentReaderRole silently sees only the jobs it owns**, and an instance with no Agent
  returns nothing at all. Both are probed and reported on the status line, for the reason the Agent jobs
  dashboard does it: a short list that looks complete is the worst outcome available.
- A job whose *name* matches returns all of its steps, deliberately — the step is the addressable thing, each
  row says which field matched, and a job named for what you searched for is the one whose steps you wanted.

Results ride the same list as object results but are **not** `SearchResult`s: `JobStepMatch` is a separate
payload on `SearchResultViewModel` (`IsJobStep`), because a job step has no schema, no database and nothing to
script, and squeezing it into the schema/object fields would put a job name where every consumer expects a
schema. The item template swaps its name line on two Visibility properties rather than a converter. Selecting
one shows the step's command with no round trip — the text arrived with the search — and double-click opens
**SSMS's own Job Properties dialog** via `JobDialogLauncher`, the job equivalent of "Open in Schema Viewer".
That needs `SERVERPROPERTY('ServerName')` rather than the connection string's Data Source (they differ behind
an AG listener or a CNAME), which is why the probe returns it and the control remembers it.

## SQL Search: Match whole word (issue #9)

`Cache/SearchTextMatcher.cs` is the single definition of a match, used by the in-memory name and column pass,
the SQLite definition pass, `JobStepSearchService.Classify` and `SearchTermHighlighter`. Keep it that way: a
row listed by one rule and highlighted by another reads as a wrong result.

- **Word characters are T-SQL identifier characters** (letters, digits, `_`, `@`, `#`, `$`), not VS Find's
  letters-digits-underscore — `person` must not find `person_id`, `@person` or `#person`. A boundary is only
  checked on a side where the term itself ends in a word character, so `[person]` and `dbo.person` work.
- **Neither LIKE engine can express it**, so in SQLite and msdb LIKE narrows to substring hits and the word
  check runs on the client. **The SQL row cap must then be lifted** (SQLite `LIMIT -1`, msdb `TOP` given
  `int.MaxValue`) and applied after the check — capped first, substring-only rows fill the cap and the real hits
  behind them are never read, which on screen is "fewer matches than there are".
- **The definition pass's `object_name NOT LIKE` exclusion moves to the client too**, as a word check: a
  substring exclusion would drop `vPersonAddress` whose body uses `person` as a word.
- **That exclusion only applies when a name pass ran** (`ExcludeNameMatchesFromDefinitions`, set from
  `SearchObjectNames`). It exists to avoid listing an object twice; it used to apply always, so Find
  References — definitions only — silently missed `vPerson` when asked what uses `Person`.
- **Job steps: a row with no whole-word hit is dropped**, not given the generic `JobStep` label. That label
  exists because the server's CI collation is the authority; under whole word it is not.
- **The checkbox saves its last state** (`SearchMatchWholeWord`, as `GridFind*` do), unlike the `DefaultSearch*`
  boxes below, and toggling it re-runs the search.

SQLite's LIKE patterns are escaped (`%`, `_`, `\`) with `ESCAPE '\'` on every LIKE. They were not, so every
column search containing `_` — most of them — matched any character there. SQLite has no `[…]` class, so `[`
is not escaped there, unlike msdb.

## SQL Search: the Search tab's defaults

All five settings on the Search tab (`DefaultSearchScope`, the three `DefaultSearch*` "search in" boxes and
`DefaultMaxSearchResults`) were **never read** — the three checkboxes got their state from `IsChecked="True"`
in the XAML and the cap was a literal `MaxResults = 500` at both call sites, so the tab read as configuration
and behaved as decoration. The general case is recorded in `IntelliSense/CLAUDE.md`; what is specific here:

- **They are a starting point, not a policy.** Applied once from `Loaded`, before `LoadServers`; anything
  changed during a session stays changed, which is what "you can override them per search" in the dialog
  means. `FindReferences_Click` still forces definitions-only, because that is a different question being
  asked, not a default being overridden.
- **A target from the Object Explorer menu beats the default scope**, because it is an explicit answer to the
  same question. Only when there is no pending target does `ApplyDefaultScope` run.
- **`CurrentDatabase` falls back to selecting everything when the current database is not in the list** — the
  normal case when the chosen server is not the one the active query window is on. The literal reading would
  be an empty selection, and the window would then open unable to search at all, reporting "Select at least
  one database" for a scope the user never picked per search.
- **`DefaultMaxSearchResults` is clamped to at least 1.** A hand-edited `0` would otherwise make every search
  return nothing, which on screen is "no matches" — the same class of silence as the dead setting itself.
  The cap applies to the object search; job steps take it too, via the same `SearchOptions.MaxResults`.

## SQL Search: loading the database list on open

`LoadServers` suppresses `ServerCombo_SelectionChanged` with `_isLoadingServers` while it clears and refills
the combo, so **the selection it then makes never raises the event** and the database load has to be called
explicitly at the end. It used to be called only when there was a single server or a pending Object Explorer
target — which left the ordinary first open (two or more connected servers, no target) showing a server name
in the combo above an empty database list. Reselecting the same combo item raises nothing, so the only way
out was to pick a different server and come back. The trigger is now unconditional on anything being
selected; the single-server and pending-target cases were always just instances of that.

`Validation/SchemaValidationControl.xaml.cs` is a copy of this server/database block, carried the same bug and
took the same fix. There is no third copy — but if one is made, it starts with this.
