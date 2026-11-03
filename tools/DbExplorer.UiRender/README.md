# DbExplorer.UiRender

Renders the desktop app's windows to PNGs with Avalonia's headless Skia backend, so the UI can be
checked (both themes, 1400x900 and 1024x700) on a machine without a display. It is a dev tool: it is
not part of the solution and `dotnet test` does not run it.

It boots the real `App` (same services as `App.OnFrameworkInitializationCompleted`, with a temporary
`AppPaths` root so the user's connections and settings are never touched), connects to a PostgreSQL
database, drives every tab and opens every dialog, and writes `<name>-light.png` / `<name>-dark.png`
plus `avalonia-log.txt` (every binding/layout warning Avalonia logged) to `.ui-renders/` at the repo root.
It also writes `accessibility-names.txt` (the name a screen reader reads for every visible button and input, per
view) and `accessibility-missing.txt` (the ones it would read as just "button" or "edit"; should stay empty).

```sh
# a sample database the tabs can show (tables with foreign keys, a view, a function, a procedure, roles)
PGPASSWORD=postgres psql -h localhost -U postgres -f tools/DbExplorer.UiRender/seed-postgres.sql

dotnet run --project tools/DbExplorer.UiRender                # → .ui-renders/
dotnet run --project tools/DbExplorer.UiRender -- /some/dir   # another output folder
```

Environment variables:

| Variable | Meaning | Default |
|---|---|---|
| `DBX_RENDER_PG` | `host;port;user;password;database` of the PostgreSQL sample | `localhost;5432;postgres;postgres;uirender_shop` |
| `DBX_RENDER_MSSQL` | same for SQL Server; when set, the engine-specific tabs are rendered on it too | unset |
| `DBX_RENDER_MYSQL` | same for MySQL / MariaDB | unset |
| `DBX_RENDER_ONLY` | comma-separated steps to run: `errors`, `navigator`, `tabs`, `small`, `toolbar`, `querytabs`, `pins`, `dialogs`, `leave`, `localhistory`, `reopen`, `sqlfiles`, `offline`, `palette`, `usages`, `search`, `import`, `preferences`, `docs`, `testdata`, `migration`, `keys`, `fullresult`, `datasearch`, `updates`, `charts`, `backup`, `jobs`, `multi`, `inline`, `sqlserver`, `mysql`, `sqlite` (seeds `samples/shop.db` under the output folder; needs no server) | all |

The frames are deterministic apart from timestamps, so two runs can be diffed to review a UI change.
