# Replication monitor

> Pinning, staged collection and the rules common to all four dashboards are in
> `SQLExtended/Monitoring/CLAUDE.md` — read that first.

`Monitoring/Replication/` (menu only — Ctrl+Alt+R is Copilot "Optimize"; `ReplMonitorToolWindow`). Tabs: Overview, Diagnostics, Subscriptions,
Publications, Agents, Publisher, Tracer tokens, Errors. Live-only like the others; `ReplHistory` keeps a
120-sample window of end-to-end latency per subscription for the trend sparklines.

**Three databases, not one.** This is the structural difference from the other dashboards, and it drives
everything else:
- the **distribution database** holds publications, subscriptions, every agent and its history — the bulk of the
  window, and readable only when the connected instance is the distributor;
- **master on the publisher** holds the one thing the distributor cannot tell you: whether the log can be
  truncated (`log_reuse_wait_desc = 'REPLICATION'`) and how full it is;
- each **subscriber database** holds its own `MSreplication_subscriptions`, often the only record of a pull
  subscription's progress that does not depend on the distributor being reachable.

So a poll opens a connection per database it needs, and each section has its own try/catch. A login with rights
on master but not on the distribution database still gets the Publisher tab. Rather than demand a particular
connection, the dashboard collects what it can and the Diagnostics tab's first row says what was not visible
from here — a publisher with a remote distributor legitimately shows no subscriptions, and that must not read as
"no subscriptions exist".

**Role detection is inferred, not asked.** There is no `SERVERPROPERTY('IsReplicationEnabled')`:
`sys.databases.is_distributor` marks the distribution database, `is_published` / `is_merge_published` mark
publisher databases, `is_subscribed` marks subscriber databases. An instance can be all three, so
`ReplCapabilities` exposes independent flags rather than an enum. `sp_helpdistributor` supplies the distributor
name and retention values — it is the only thing that answers "who is my distributor" on a publisher whose
distributor is remote.

Query notes (`ReplQueryService`):
- **`MSsubscriptions` is one row per article**, so it is grouped down to (publication, subscriber, subscriber
  database) — the grain everyone thinks in — before the agent's history is applied. The `MAX()`s over
  group-constant columns satisfy `GROUP BY`; they are not combining anything.
- **The latest history row is fetched with `OUTER APPLY … TOP (1) … ORDER BY time DESC` per agent**, never a
  window function over the whole table. `MSdistribution_history` is the largest table in a busy distribution
  database; a ranked scan of all of it on a timer is not acceptable, and agent counts are small.
- **Latency is normalised to seconds on the way in.** The history tables report milliseconds, tracer tokens
  report datetimes, `sp_replcounters` reports seconds. One unit in the model means nothing downstream has to
  remember which. End-to-end latency = log reader hop (publisher→distributor) + distribution hop
  (distributor→subscriber), matched up by publisher database in `LinkLogReaderLatency`.
- **Agent jobs are joined to msdb server-side, not matched in C#.** `MS*_agents.job_id` is `binary(16)` while
  `sysjobs.job_id` is a `uniqueidentifier`; the byte orders only agree under SQL Server's own conversion rule.
  Letting the server compare them is right by construction — doing it in C# is a coin flip that fails silently
  as a lookup miss. Its own section because it needs msdb rights the distribution database's do not imply.
- **The subscriber-database read is dynamic SQL** (one statement per subscribed database, `UNION ALL`ed).
  `state = 0`, `HAS_DBACCESS` and an `OBJECT_ID` check each guard a case that would otherwise fail the whole
  batch, and the trailing separator is trimmed with `DATALENGTH/2`, not `LEN` — `LEN` ignores the trailing space
  of `' UNION ALL '` and cuts a character short.
- **`sp_replcounters` is its own section** because it needs sysadmin or db_owner and reads DBCC internals. A
  monitoring login often has neither, and losing three columns beats losing the tab.
- **The agent-history tables are read against their probed column set, and the four agent types run as four
  commands rather than one batch.** Both because of the same incident: on SQL Server 2025 the Agents section
  failed with `Invalid column name 'comments'. Invalid column name 'error_id'.`, and since a batch binds as a
  whole that emptied the whole tab. So `ReplCapabilities` now probes `sys.all_columns` for the *entire* column
  list of each `MS*_history` / `MSmerge_sessions` table (a set, not named flags — these tables lose columns as
  well as gain them), `HistorySelect` substitutes `CONVERT(<type>, NULL) AS <name>` for anything absent so the
  derived table always exposes the full alias set, and a type whose query still fails costs that type's rows and
  a named warning. A table that was never probed answers "column present", so a hand-built capability set builds
  the same SQL as before. Aliases are bracketed because `time` reads as a type name.
- **The merge agent's history is two tables, and conflating them is what caused the error above.**
  `MSmerge_sessions` is one summary row per session, with `upload_inserts` / `download_conflicts` style counts
  and **no `comments` and no `error_id` at all**; `MSmerge_history` is one row per message, keyed by
  `session_id`, and is the only place a merge agent's comment and error id exist. The query read the first while
  naming the second's columns, so it asked for `publisher_conflictcount` (which silently probed false, blanking
  three columns) and for `comments` / `error_id` (which failed the bind and took the whole tab with it). It now
  takes timings and totals from the session and adds two applies over its messages: the **latest** message for
  the comment, and the latest message **with `error_id > 0`** for the error — a session that failed and retried
  ends on a retry line, so reading the error off the last message reports a failure as no error at all. Those
  two are gated on `HasColumn` rather than NULL-substituted, because a `WHERE`/`ORDER BY` binds against the base
  table whatever the select list says. Verify against `sys.columns` before adding a column here; the docs for
  these two tables are easy to mix up.
- Numeric status columns are decoded in `ReplValueParser` — the distribution database has no `*_desc` column
  anywhere. `runstatus` 1–6, publication type 0–2, subscription status 0/1/2, and a retention period in one of
  four units, all normalised to hours. This is the piece with unit tests
  (`SQLExtended.Tests/Monitoring/ReplDiagnosticsTests.cs`).

**Three reads are on demand, never on the timer**, each because it is most expensive exactly when it is most
wanted:
- **Undelivered commands** (`MSdistribution_status`) counts rows in `MSrepl_commands`. Counts survive a refresh
  rather than being blanked and recollected — the Overview says when they were taken so an old number is never
  mistaken for a current one.
- **Errors** (`MSrepl_errors`) is the text the history rows only reference by `error_id`.
- **Tracer tokens**, plus posting one.

**The detail pane** (`ReplMonitorControl.Detail.cs`, `ReplQueryService.Detail.cs`) opens under the tabs when a row
is clicked in Overview, Subscriptions, Agents or Errors, and is a fourth on-demand read. What it is guarding:
- **`MSrepl_errors.id` is not unique.** One failure writes several rows under one id — the agent's wrapper, then
  the subscriber's SQL error, then the data source — and the history row points at the id, so the grids only ever
  show one layer. The pane shows the whole chain. `error_code` is `sysname` (`MSSQL_REPL20011`, `08001`), not int:
  reading it as int threw "Input string was not in a correct format" and killed the Errors tab.
- **The failing command** is `sp_browsereplcmds` with the error's `xact_seqno` as both bounds **plus
  `@publisher_database_id`** (a seek on `MSrepl_commands`' clustered key; without it, a scan). The command id is
  matched client-side, because the procedure demands every other filter alongside `@command_id`. An all-zero
  seqno means the agent failed before reaching a command. `MSarticles` names the source and destination tables.
- **An Errors-tab row does not name its agent** (`source_name` is the layer that raised it), so the agent is found
  by scanning the history tables for `error_id` — no index, which is why it runs only on a click.

**`ReplActionService.PostTracerToken` is the only writing code here.** A tracer token is the only end-to-end
measurement in replication that is not an estimate — a real transaction, timed as it lands — and a topology that
looks idle because nothing has changed is indistinguishable from a broken one until you post one. Rules match
`JobActionService`: it runs `sys.sp_posttracertoken` **in the publication database on the publisher**, so the
picker only offers publications this instance publishes (`CanPostFrom`, which tolerates the `HOST` vs
`HOST\MSSQLSERVER` spelling of a default instance); merge publications are excluded because tracer tokens are a
transactional-replication feature; it confirms first, naming the publication *and* the server, defaulting to No;
and the server's own error is surfaced verbatim.

Diagnostics severity choices worth defending:
- A **deactivated subscription** is critical though nothing is erroring — it passed the distribution retention
  window, and the fix is a reinitialize and a fresh snapshot, not a restart.
- A **published log held by `REPLICATION`** is critical once the log is nearly full, a warning before that: the
  consequence is a full disk, not stale data. This is the failure that takes an instance down and the one the
  distribution database says nothing about.
- A **disabled agent job** is a warning, not an error — often deliberate during maintenance, but nothing moves
  until it is back and the retention window keeps running down.
- A **failed distribution agent** is reported against its subscription only, not twice: the subscription names
  the publication and subscriber, which the agent's generated name does not.
