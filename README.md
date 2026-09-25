# DB Explorer

A cross-platform desktop app (.NET 8 + Avalonia) for exploring databases without disturbing them.
SQL Server is the primary engine; PostgreSQL is included as a second provider to prove the extension point.

## Features

| Tab | What it does |
|---|---|
| **Objects** | Tables, views, procedures, functions, triggers, sequences, synonyms. Filter by name and type; columns and source code of the selected object. |
| **Search names & code** | Finds any object name, column name, or text inside procedure/function/view/trigger code (match case, whole word, regex, line numbers). Runs against a local metadata snapshot, so it puts **zero load** on the server. |
| **Search data** | Finds a value in every text column (LIKE / ILIKE), numeric column (exact) and GUID/UUID column across all tables, with the primary key of each matching row. |
| **Indexes** | Key and included columns, filters, size, rows, usage (seeks/scans/updates), fragmentation (SQL Server, optional). |
| **Locks** | Current locks, waiting sessions, who blocks whom, the blocker's SQL, auto-refresh. |

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
- Data search: one query per table (`WHERE col1 LIKE @p OR col2 LIKE @p OR …`, `TOP (@n)`), a command timeout, bounded parallelism (default 3), smallest tables first. A table that hits the lock timeout (error 1222) or the query timeout is listed under *Skipped tables* and the search moves on.
- Fragmentation uses `sys.dm_db_index_physical_stats` in `LIMITED` mode.

**PostgreSQL**
- MVCC readers never block writers. Each query runs in its own transaction with `SET TRANSACTION READ ONLY; SET LOCAL statement_timeout; SET LOCAL lock_timeout`, then rolls back.

**Caveats**
- READ UNCOMMITTED can return uncommitted or duplicated rows. For finding where a value lives that is acceptable; do not use the results as exact counts.
- A data search still reads every row of the searched tables (LIKE '%x%' cannot use an index). Use the schema / table filters, *Skip tables above N rows*, and low parallelism on busy production servers — or point the app at a readable secondary or a restored copy.

## Required permissions

SQL Server: `CONNECT`, `VIEW DEFINITION` (to see code), `SELECT` on the tables to search, `VIEW DATABASE STATE` (index usage and fragmentation) and `VIEW SERVER STATE` (locks). Without the last two, the Indexes tab falls back to catalog-only data and the Locks tab shows the permission error.

PostgreSQL: `CONNECT` and `SELECT`; `pg_read_all_stats` or superuser to see other users' queries in the Locks tab.

## Adding another engine (e.g. MySQL, Oracle)

1. Create `src/DbExplorer.Providers.MySql` referencing `DbExplorer.Core`.
2. Implement `IDatabaseProviderFactory` (key, display name, default port, `ListDatabasesAsync`) and `IDatabaseProvider` (catalog queries, `IsSearchable`, `SearchTableAsync`). Follow the rules in the interface comment: read-only, lock timeouts, statement timeouts.
3. Add an `AddMySqlProvider()` extension and call it in `App.axaml.cs` next to `AddSqlServerProvider()`.

Nothing else changes: the connection dialog, tabs, caches and searches pick the new engine up through DI.

## Security

Connections are stored in `connections.json` in the app data folder. Passwords are saved only when *Save password* is checked, and only on Windows, encrypted with DPAPI for the current user. On macOS/Linux the app asks for the password on connect.

## Known limitations

- Fragmentation is not reported for PostgreSQL (would require `pgstattuple`, which scans the index).
- XML, JSON, binary and spatial columns are not included in data search.
- Table scripts in the Objects tab are generated from catalog metadata (columns + primary key), not full DDL.
