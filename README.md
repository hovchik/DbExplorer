# DB Explorer

A cross-platform desktop app (.NET 8 + Avalonia) for exploring databases without disturbing them.
SQL Server is the primary engine; PostgreSQL is included as a second provider to prove the extension point.

## Features

| Tab | What it does |
|---|---|
| **Objects** | Tables, views, procedures, functions, triggers, sequences, synonyms. Filter by name and type; columns, indexes, foreign keys and source code of the selected object. **Profile** computes null %, distinct count, min/max per column over a sample (one read-only scan) and the most frequent values of a column. |
| **Search names & code** | Finds any object name, column name, or text inside procedure/function/view/trigger code (match case, whole word, regex, line numbers). Runs against a local metadata snapshot, so it puts **zero load** on the server. |
| **Search data** | Finds a value in every text column (LIKE / ILIKE), numeric column (exact) and GUID/UUID column across all tables, with the primary key of each matching row. |
| **Query** | Ad-hoc scripts with a per-tab **Database** picker (statements run there and the editor suggests every table, view, function and column of that database only), context-aware completion (aliases, `db.schema.`, `schema.` and `alias.` qualifiers, Ctrl+Space), cancel, and **Run on multiple databases**: the same script on every selected database, results stacked with a `Database` column. |
| **Diagram** | ER diagram built from the cached foreign keys (no server round-trip): around one table (N hops) or a whole schema. Copy as Mermaid, save as PNG. |
| **Indexes** | Key and included columns, filters, size, rows, usage (seeks/scans/updates), fragmentation (SQL Server, optional). |
| **Locks** | Current locks, waiting sessions, the blocker's SQL, auto-refresh, and a **blocking tree** (head blocker → blocked sessions, cycle-safe). |
| **Activity** | **Running now**: executing requests (and SQL Server sessions sleeping inside an open transaction) with elapsed/CPU/reads/waits/blocker. **Top queries**: most expensive cached statements by CPU, duration, reads or executions (plan cache on SQL Server, `pg_stat_statements` on PostgreSQL). |
| **Comparer** | Two connections side by side (environment badges, swap sides, cancel with Esc). **Overview** compares every table, view, routine and trigger of both databases from the cached metadata (instant, no server load) and lists what differs or exists on one side only; double-click for details. **Schema** diffs definitions line by line or by content, optionally ignoring case/whitespace, with *only changes* folding, F8 / Shift+F8 change navigation and a similarity score. **Structure** compares a table's columns (type, nullability, key, position), indexes and foreign keys, recognising renamed indexes. **Data** matches rows by primary key with filters per status and key, per-column difference counts (click to filter), ignored columns, case/padding-insensitive text, value-based numeric and binary equality, and warnings for duplicate keys, row-limit truncation and columns missing on one side. Schema and data results export as a self-contained HTML or Markdown report. |

Everywhere:

- **Result grids** — refine fetched rows without re-running the query: search across all columns; per-column filters from each header's funnel (conditions such as contains / starts with / = / > / ≤ / is NULL, comparing numbers and dates as values, plus an Excel-style value list with counts), shown as removable chips; *Filter by / Exclude this value* from a cell's context menu; typed multi-column sort (click a header, Shift+click to add; numbers, dates and NULLs order correctly); a *Columns* chooser to hide, find and jump to columns; freeze columns; drag to reorder; row numbers; IDE-style colouring by value type (numbers, dates / times, booleans, GUIDs, binary, NULL — in light and dark themes), zebra rows and right-aligned numbers; and a *Row details* pane that lists the selected row as column / value pairs for wide results. Filters and sort are kept per result tab. Ctrl+C / context menu copy (cell, rows, rows with header, rows as INSERT) and export to CSV, Excel (.xlsx), JSON, Markdown or an INSERT script use the rows and columns in view. CSV neutralizes spreadsheet formulas in text values.
- **Command palette** — Ctrl+K (or Ctrl+P): fuzzy-jump to any table/view/routine, tab or command. Enter opens the object, Shift+Enter shows a table in the diagram, Ctrl+Enter gets data / executes.
- **Environment tags** — mark a connection as Development, Test, Staging or Production. The window shows a colored banner and title tag; on Production, write scripts and stored procedures require typing `PRODUCTION` to confirm.

## Build and run

Requires the .NET 8 SDK.

```bash
dotnet restore
dotnet run --project src/DbExplorer.Desktop
dotnet test
```

Open `DbExplorer.sln` in Visual Studio 2022 / Rider, set `DbExplorer.Desktop` as the startup project.

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

Connections are stored in `connections.json` in the app data folder (including their environment tag). Passwords are saved only when *Save password* is checked, and only on Windows, encrypted with DPAPI for the current user. On macOS/Linux the app asks for the password on connect.

## Known limitations

- Fragmentation is not reported for PostgreSQL (would require `pgstattuple`, which scans the index).
- XML, JSON, binary and spatial columns are not included in data search. Profiling reports only null counts for xml/text/image/spatial columns on SQL Server.
- The Query tab's *Run on multiple databases* confirms once for the whole batch; each database runs independently and a failure on one does not stop the others.
- Table scripts in the Objects tab are generated from catalog metadata (columns + primary key), not full DDL.
