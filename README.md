# DB Explorer

A cross-platform desktop app (.NET 8 + Avalonia) for exploring databases without disturbing them.
SQL Server is the primary engine; PostgreSQL is included as a second provider to prove the extension point.

## Features

| Tab | What it does |
|---|---|
| **Objects** | Tables, views, procedures, functions, triggers, sequences, synonyms. Filter by name and type; columns, indexes, foreign keys and source code of the selected object. **Profile** computes null %, distinct count, min/max per column over a sample (one read-only scan) and the most frequent values of a column. |
| **Search names & code** | Finds any object name, column name, or text inside procedure/function/view/trigger code (match case, whole word, regex, line numbers). Runs against a local metadata snapshot, so it puts **zero load** on the server. |
| **Search data** | Finds a value in every text column (LIKE / ILIKE), numeric column (exact) and GUID/UUID column across all tables, with the primary key of each matching row. Double-click a result to open the whole record, *Go to table* to jump to it in Objects. *Relations & combined data* draws the tables the value was found in (across databases), every table directly related to them and the tables linking them (up to 3 foreign-key hops); it loads each table's found rows, the rows of every related table that belong to them (parents they reference, children referencing them), and a combined result that LEFT JOINs it all along the foreign keys; every query can be opened in the Query tab. |
| **Query** | Ad-hoc scripts with a per-tab **Database** picker (statements run there and the editor suggests every table, view, function and column of that database only), context-aware completion (aliases, `db.schema.`, `schema.` and `alias.` qualifiers, Ctrl+Space), cancel, and **Run on multiple databases**: the same script on every selected database, results stacked with a `Database` column. A **row limit** (default 10,000, 0 = none) keeps huge tables cheap: a script that only reads stops each result set on the server one row past the limit (see *Large databases* below). |
| **Diagram** | ER diagram built from the cached foreign keys (no server round-trip): around one table (N hops) or a whole schema. Copy as Mermaid, save as PNG. |
| **Indexes** | Key and included columns, filters, size, rows, usage (seeks/scans/updates), fragmentation (SQL Server, optional). |
| **Locks** | Current locks, waiting sessions, the blocker's SQL, auto-refresh, and a **blocking tree** (head blocker → blocked sessions, cycle-safe). |
| **Activity** | **Running now**: executing requests (and SQL Server sessions sleeping inside an open transaction) with elapsed/CPU/reads/waits/blocker. **Top queries**: most expensive cached statements by CPU, duration, reads or executions (plan cache on SQL Server, `pg_stat_statements` on PostgreSQL). |
| **Comparer** | Two connections side by side (environment badges, swap sides, cancel with Esc). **Overview** compares every table, view, routine and trigger of both databases from the cached metadata (instant, no server load) and lists what differs or exists on one side only; double-click for details. **Schema** diffs definitions line by line or by content, optionally ignoring case/whitespace, with *only changes* folding, F8 / Shift+F8 change navigation and a similarity score. **Structure** compares a table's columns (type, nullability, key, position), indexes and foreign keys, recognising renamed indexes. **Data** matches rows by primary key with filters per status and key, per-column difference counts (click to filter), ignored columns, case/padding-insensitive text, value-based numeric and binary equality, and warnings for duplicate keys, row-limit truncation and columns missing on one side. Schema and data results export as a self-contained HTML or Markdown report. |

| **Lab** *(experimental)* | **Change recorder**: press Start, do something in your application, press Stop — lists every table whose insert/update/delete counters moved (`sys.dm_db_index_operational_stats` / `pg_stat_user_tables`) and the rows involved: an exact before/after diff for tables snapshotted at Start (tables up to 2,000 rows by default), otherwise the rows written since Start (PostgreSQL `xmin`, SQL Server `rowversion` columns), otherwise counts only. **Schema history**: every catalog read from the server is stored locally as a version when something changed (definitions de-duplicated by hash), so you can compare any two versions or see one object's revisions — no DDL triggers or audit on the server. **Inferred relationships**: foreign keys the schema does not declare, proposed from column names and types (`Orders.CustomerId → Customers.Id`, `order_lines.product_id → products.id`), checked against 2,000 sampled values, and — once accepted — used by the Diagram (dashed), Relations and JOIN suggestions. Accepted ones live in a local file, never in the database. |
| **API** | Import **Postman** collections (v2.0 / v2.1) and environments, and **Insomnia** exports (v4 JSON), then browse, edit and send their requests; no database connection needed. `{{variables}}` resolve from the collection, overridden by the selected environment (nested references and Postman's `{{$guid}}`, `{{$timestamp}}`, `{{$isoTimestamp}}`, `{{$randomInt}}` work); the URL preview names any variable without a value. Bearer, Basic and API-key auth are inherited from folders and the collection; raw, form, URL-encoded and GraphQL bodies are sent. Scripts, file uploads and other auth types are not run and are listed after the import. Collections, environments and the last 200 sends are kept in the app's data folder. |

**Lab tools in the Query tab** (*Lab ▾*):

- **Why isn't my row here?** — give the row you expected as a condition (`o.OrderId = 1001`); the statement at the caret is replayed with read-only COUNT probes: does the row exist, which join drops it (and which part of its ON condition, with the values it looked for), which WHERE condition excludes it (with the row's actual values and a hint when a NULL is involved), whether HAVING removes its group, or whether TOP / LIMIT cut it (and its position).
- **Dry run** — runs the selection / script in a transaction that is always rolled back and shows what it would change: per UPDATE the old and new value of every changed column, per DELETE the rows, per INSERT the new rows (RETURNING / OUTPUT). Probes run under savepoints, so a shape it cannot preview (OUTPUT on a table with triggers, an unreadable type) only loses its detail. Locks are held until the rollback (5 s lock timeout); triggers fire and sequences / identities still advance.
- **Lock impact** — before running a script: the estimated plan (nothing is executed) gives the tables it writes and how many rows; engine rules turn that into the locks it will take (SQL Server lock escalation past 5,000 rows, Sch-M / ACCESS EXCLUSIVE for most DDL, SHARE for a non-concurrent CREATE INDEX…), and the live lock list and running requests name the sessions in the way right now.
- **Value suggestions** — after `column =`, `<>`, `LIKE` or inside `IN (`, the editor suggests the values the column actually holds, most frequent first (sampled once per column and connection from the first 10,000 rows, like profiling).

**Locks → Flight recorder**: every refresh (auto-refresh: every 5 s) is kept for the last hour; periods with blocking become incidents that can be replayed afterwards with a slider (who blocked whom at each moment) and copied as a text report.

Everywhere:

- **Result grids** — refine fetched rows without re-running the query: search across all columns; per-column filters from each header's funnel (conditions such as contains / starts with / = / > / ≤ / is NULL, comparing numbers and dates as values, plus an Excel-style value list with counts), shown as removable chips; *Filter by / Exclude this value* from a cell's context menu; typed multi-column sort (click a header, Shift+click to add; numbers, dates and NULLs order correctly); a *Columns* chooser to hide, find and jump to columns; freeze columns; drag to reorder; row numbers; IDE-style colouring by value type (numbers, dates / times, booleans, GUIDs, binary, NULL — in light and dark themes), zebra rows and right-aligned numbers; and a *Row details* pane that lists the selected row as column / value pairs for wide results. Filters and sort are kept per result tab. Ctrl+C / context menu copy (cell, rows, rows with header, rows as INSERT) and export to CSV, Excel (.xlsx), JSON, Markdown or an INSERT script use the rows and columns in view. CSV neutralizes spreadsheet formulas in text values.
- **Copy, edit and follow values in results** — Ctrl+C copies the current cell's value (with several rows selected, the rows; Ctrl+Shift+C adds the header); F2 opens the cell's text so part of it can be selected and copied. When a Query-tab result (or a table's *Get data* window) comes from plain table columns — `SELECT * FROM t`, joins with aliases, `t.*`, `col AS alias` — and includes the table's primary key (or a unique key), its values can be edited in place: double-click or F2, Enter keeps the value (checked against the column's type), *Set to NULL* / *Revert* from the context menu. Edited cells are tinted until **Commit** (Ctrl+S) writes them as one transaction of `UPDATE … WHERE <key> = <value as read>` statements — a row that changed or was deleted meanwhile cancels the whole commit — or **Revert all** discards them; with auto-commit off they run in the tab's open transaction. Production connections show the statements and ask for `PRODUCTION` first. Foreign key values are underlined: clicking one opens the referenced row (in a new query tab, or a filtered data window). Results the catalog cannot trace (expressions, GROUP BY / DISTINCT, UNION, derived tables, procedure calls) stay read-only; the toolbar says why.
- **Command palette** — Ctrl+K (or Ctrl+P): fuzzy-jump to any table/view/routine, tab or command. Enter opens the object, Shift+Enter shows a table in the diagram, Ctrl+Enter gets data / executes.
- **Environment tags** — mark a connection as Development, Test, Staging or Production. The window shows a colored banner and title tag; on Production, write scripts and stored procedures require typing `PRODUCTION` to confirm.

## Build and run

Requires the .NET 8 SDK.

```bash
dotnet restore
dotnet run --project src/DbExplorer.Desktop
dotnet test
```

Integration tests for the Lab features run against real servers when these are set (otherwise they are skipped); each creates and drops a `dbx_lab_test` database:

```bash
DBEXPLORER_TEST_PG="localhost;5432;postgres;<password>" \
DBEXPLORER_TEST_MSSQL="localhost;1433;sa;<password>" dotnet test
```

Open `DbExplorer.sln` in Visual Studio 2022 / Rider, set `DbExplorer.Desktop` as the startup project.

### Distributing to other people

**Windows installer** (Inno Setup): `DbExplorer-Setup-<version>.exe` installs a self-contained build into Program Files (or just for the current user), adds Start menu / desktop shortcuts and an uninstaller. Upgrades and uninstalls keep each user's connections and tabs.

```powershell
.\installer\build-installer.ps1 -Version 1.2.0
# → installer\out\DbExplorer-Setup-1.2.0.exe  (installs Inno Setup with winget on first use if missing)
```

The *Windows installer* GitHub workflow builds it too: push a `v1.2.0` tag to get that version, and download the setup from the run's artifacts.

```bash
# Windows (the FolderProfile publish profile): self-contained, ReadyToRun, no .NET install needed on the target machine
dotnet publish src/DbExplorer.Desktop -p:PublishProfile=FolderProfile
# → src/DbExplorer.Desktop/bin/Release/net8.0/publish/  (zip the folder; run DbExplorer.exe)

# Linux / macOS
dotnet publish src/DbExplorer.Desktop -c Release -r linux-x64 --self-contained -o publish/linux-x64
dotnet publish src/DbExplorer.Desktop -c Release -r osx-arm64 --self-contained -o publish/osx-arm64
```

Each user's connections, metadata cache and query tabs live in their own app data folder (see *Metadata cache* below).
Errors that nothing else handled are written to `DbExplorer/logs/errors.log` in that folder instead of closing the app;
ask for that file when someone reports a problem.

## Architecture

```
src/
  DbExplorer.Core                  Models + IDatabaseProvider / IDatabaseProviderFactory (no dependencies)
  DbExplorer.Application           Use cases: sessions, metadata cache (SQLite), name/code search, data search, saved connections
  DbExplorer.Providers.SqlServer   Dapper + Microsoft.Data.SqlClient
  DbExplorer.Providers.Postgres    Dapper + Npgsql
  DbExplorer.Desktop               Avalonia UI, MVVM (CommunityToolkit.Mvvm), DI composition root
tests/
  DbExplorer.Tests                 xUnit tests for pure logic (no database needed)
```

Dependencies point inward: providers and the UI depend on Core; the UI depends on Application; Application never references a concrete provider.

**Metadata cache.** On connect, the catalog (objects, columns, module source) is read once — three short queries in parallel — and stored in `%LocalAppData%/DbExplorer/cache/<hash>.db` (SQLite; `~/.local/share` on Linux, `~/Library/Application Support` on macOS). Later connections load it from disk; *Refresh metadata* re-reads the server. All name and code searches run in memory against this snapshot.

## How "no locking" works

**SQL Server**
- Every batch starts with `SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; SET LOCK_TIMEOUT n; SET DEADLOCK_PRIORITY LOW`. No shared locks are taken on data, a blocked query fails in `n` ms instead of queueing, and the app always loses a deadlock.
- Metadata and data search use separate connection pools (different `Application Name`), and every batch re-applies its settings.
- `ApplicationIntent=ReadOnly` is on by default, so Availability Group listeners route the app to a readable secondary.
- Column profiling reads only the first *N* rows (default 10,000) of one table in a single statement, on the data-search connection pool with the same session prefix and a statement timeout.
- Activity and top queries read DMVs only (`sys.dm_exec_requests`, `sys.dm_exec_query_stats`); top queries resolve `sql_text` only for the rows returned.
- Data search: one query per table (`WHERE col1 LIKE @p OR col2 LIKE @p OR …`, `TOP (@n)`), a command timeout, bounded parallelism (default 3), smallest tables first. A table that hits the lock timeout (error 1222) or the query timeout is listed under *Skipped tables* and the search moves on.
- Fragmentation uses `sys.dm_db_index_physical_stats` in `LIMITED` mode.

**PostgreSQL**
- MVCC readers never block writers. Each query runs in its own transaction with `SET TRANSACTION READ ONLY; SET LOCAL statement_timeout; SET LOCAL lock_timeout`, then rolls back. Column profiling uses the same read-only transaction.

**Large databases**
- The metadata snapshot makes browsing, name/code search, diagrams and the schema overview independent of data size.
- Query tab (also inside a manual transaction and *Run on multiple databases*): when the script only reads — `SELECT` / `WITH` / `VALUES` / `TABLE` queries plus `DECLARE`, `SET`, `PRINT`, `USE`, `SHOW`, and nothing that writes, calls procedures, uses `SELECT … INTO`, cursors, control flow or transaction control — each result set stops on the server one row past the row limit, and the grid shows "first N of more than N". SQL Server uses `SET ROWCOUNT` for the run (reset afterwards; functions and subqueries are not affected); PostgreSQL runs each query through a cursor and `FETCH`es limit + 1 rows, so the planner also optimises for the first rows. Any other script runs unchanged: rows past the limit are read and discarded so every statement still runs, and the total is exact.
- Functions called in the select list of a limited query run only for the rows fetched.

**Lab features**
- Change recorder, why-not debugger and relationship verification read through the data-search path (SQL Server: dirty reads, lock timeout; PostgreSQL: read-only transaction with statement and lock timeouts). The change counters need `VIEW DATABASE STATE` on SQL Server and are read from the primary.
- PostgreSQL publishes other sessions' table counters when their transaction ends, at most about once a second and up to ~10 s later for busy sessions: if a change is missing, press *Check again*.
- Lock impact only asks for the estimated plan; dry run is the one Lab tool that executes statements (always rolled back, after a confirmation — typing `PRODUCTION` on production connections).

**Caveats**
- READ UNCOMMITTED can return uncommitted or duplicated rows. For finding where a value lives that is acceptable; do not use the results as exact counts.
- A data search still reads every row of the searched tables (LIKE '%x%' cannot use an index). Use the schema / table filters, *Skip tables above N rows*, and low parallelism on busy production servers — or point the app at a readable secondary or a restored copy.

## Required permissions

SQL Server: `CONNECT`, `VIEW DEFINITION` (to see code), `SELECT` on the tables to search or profile, `VIEW DATABASE STATE` (index usage and fragmentation) and `VIEW SERVER STATE` (locks, activity, top queries). Without the last two, the Indexes tab falls back to catalog-only data and the Locks/Activity tabs show the permission error.

PostgreSQL: `CONNECT` and `SELECT`; `pg_read_all_stats` or superuser to see other users' queries in the Locks and Activity tabs. *Top queries* needs the `pg_stat_statements` extension (`shared_preload_libraries = 'pg_stat_statements'`, then `CREATE EXTENSION pg_stat_statements;`).

## Adding another engine (e.g. MySQL, Oracle)

1. Create `src/DbExplorer.Providers.MySql` referencing `DbExplorer.Core`.
2. Implement `IDatabaseProviderFactory` (key, display name, default port, `ListDatabasesAsync`) and `IDatabaseProvider` (catalog queries, `IsSearchable`, `SearchTableAsync`, activity/top-query DMVs, `ProfileTableAsync`/`GetTopValuesAsync`). Follow the rules in the interface comment: read-only, lock timeouts, statement timeouts.
3. Add an `AddMySqlProvider()` extension and call it in `App.axaml.cs` next to `AddSqlServerProvider()`.

Nothing else changes: the connection dialog, tabs, caches and searches pick the new engine up through DI.

## Security

Connections are stored in `connections.json` in the app data folder (including their environment tag). Passwords are saved only when *Save password* is checked, and only on Windows, encrypted with DPAPI for the current user. On macOS/Linux the app asks for the password on connect. A damaged `connections.json` is moved aside (`connections.json.corrupt-<time>`) rather than overwritten.

*Encrypt (TLS)* with *Trust certificate* off validates the server certificate and host name on both engines (PostgreSQL `SslMode=VerifyFull`); with *Trust certificate* on, the connection is encrypted but any certificate is accepted (`TrustServerCertificate=True` / `SslMode=Require`). For servers reached over untrusted networks, turn *Encrypt* on and *Trust certificate* off.

## Known limitations

- Fragmentation is not reported for PostgreSQL (would require `pgstattuple`, which scans the index).
- XML, JSON, binary and spatial columns are not included in data search. Profiling reports only null counts for xml/text/image/spatial columns on SQL Server.
- The Query tab's *Run on multiple databases* confirms once for the whole batch; each database runs independently and a failure on one does not stop the others.
- Table scripts in the Objects tab are generated from catalog metadata (columns + primary key), not full DDL.
