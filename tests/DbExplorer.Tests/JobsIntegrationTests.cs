using DbExplorer.Application.Jobs;
using DbExplorer.Core.Connections;
using DbExplorer.Providers.MySql;
using DbExplorer.Providers.Postgres;
using DbExplorer.Providers.SqlServer;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace DbExplorer.Tests;

/// <summary>
/// The Jobs tab against pg_cron. Skipped unless DBEXPLORER_TEST_PG is set ("host;port;user;password" of a superuser)
/// and the server has pg_cron loaded (shared_preload_libraries) with the extension created in cron.database_name.
/// Jobs named dbx_job_* are created and removed again.
/// </summary>
public sealed class PostgresJobsIntegrationTests : IAsyncLifetime
{
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_PG");
    private PostgresProvider? _provider;
    private bool _hasCron;

    private static ConnectionProfile Profile(string database)
    {
        var p = Settings!.Split(';');
        return new ConnectionProfile
        {
            ProviderKey = PostgresProviderFactory.ProviderKey, Host = p[0], Port = int.Parse(p[1]),
            UserName = p[2], Password = p[3], Database = database, Name = "test"
        };
    }

    public async Task InitializeAsync()
    {
        if (Settings is null) return;
        _provider = new PostgresProvider(Profile("postgres"));
        await using var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(Profile("postgres")));
        await cn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM pg_extension WHERE extname = 'pg_cron'", cn);
        _hasCron = Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
        if (_hasCron) await CleanAsync(cn);
    }

    private static async Task CleanAsync(NpgsqlConnection cn)
    {
        await using var cmd = new NpgsqlCommand("SELECT cron.unschedule(jobid) FROM cron.job WHERE jobname LIKE 'dbx_job_%'", cn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (Settings is null || _provider is null) return;
        if (_hasCron)
        {
            await using var cn = new NpgsqlConnection(PostgresSql.BuildConnectionString(Profile("postgres")));
            await cn.OpenAsync();
            await CleanAsync(cn);
        }
        await _provider.DisposeAsync();
    }

    [SkippableFact]
    public async Task Creates_lists_changes_and_deletes_a_pg_cron_job_and_reads_its_runs()
    {
        Skip.If(Settings is null, "DBEXPLORER_TEST_PG not set");
        Skip.IfNot(_hasCron, "pg_cron is not installed on the test server");
        var catalog = await JobCatalogLoader.LoadAsync(_provider!, "postgres");
        Assert.True(catalog.Available, catalog.Setup);
        Assert.Equal(JobEngine.PgCron, catalog.Engine);
        Assert.Equal("postgres", catalog.HomeDatabase);

        var draft = new JobDraft
        {
            Name = "dbx_job_purge", Database = "postgres", Command = "SELECT 'it''s fine'",
            Schedule = new JobScheduleSpec { Kind = ScheduleKind.Daily, At = new TimeOnly(3, 15) }
        };
        Assert.Null(JobScriptBuilder.Problem(catalog, draft, null));
        await JobScriptBuilder.RunAsync(_provider!, JobScriptBuilder.Create(catalog, draft, DateTime.Now), 30);

        catalog = await JobCatalogLoader.LoadAsync(_provider!, "postgres");
        var job = Assert.Single(catalog.Jobs, j => j.Name == "dbx_job_purge");
        Assert.Equal("15 3 * * *", job.ScheduleText);
        Assert.Equal("Daily at 03:15", job.ScheduleDescription);
        Assert.Equal("SELECT 'it''s fine'", job.Command);
        Assert.True(job.Enabled);
        Assert.NotNull(job.NextRunAt);
        Assert.Contains("exists", JobScriptBuilder.Problem(catalog, draft, null) ?? "");

        await JobScriptBuilder.RunAsync(_provider!, JobScriptBuilder.SetEnabled(catalog, job, false), 30);
        catalog = await JobCatalogLoader.LoadAsync(_provider!, "postgres");
        job = catalog.Jobs.Single(j => j.Name == "dbx_job_purge");
        Assert.False(job.Enabled);
        Assert.Null(job.NextRunAt);

        // Every second, so the run history fills while the test waits.
        var changed = JobScriptBuilder.DraftOf(job) with
        {
            Enabled = true, Command = "SELECT 1", Schedule = new JobScheduleSpec { Kind = ScheduleKind.Cron, Cron = "1 seconds" }
        };
        var update = JobScriptBuilder.Update(catalog, job, changed, DateTime.Now);
        Assert.Contains("cron.alter_job", update.Sql);
        await JobScriptBuilder.RunAsync(_provider!, update, 30);

        IReadOnlyList<JobRun> runs = [];
        for (var i = 0; i < 30 && !runs.Any(r => r.Outcome == JobOutcome.Succeeded); i++)
        {
            await Task.Delay(500);
            runs = await JobCatalogLoader.LoadHistoryAsync(_provider!, catalog, job);
        }
        Assert.Contains(runs, r => r.Outcome == JobOutcome.Succeeded && r.StartedAt is not null);

        catalog = await JobCatalogLoader.LoadAsync(_provider!, "postgres");
        job = catalog.Jobs.Single(j => j.Name == "dbx_job_purge");
        Assert.Equal("1 seconds", job.ScheduleText);
        Assert.NotNull(job.LastRunAt);

        await JobScriptBuilder.RunAsync(_provider!, JobScriptBuilder.Delete(catalog, job), 30);
        catalog = await JobCatalogLoader.LoadAsync(_provider!, "postgres");
        Assert.DoesNotContain(catalog.Jobs, j => j.Name == "dbx_job_purge");
    }
}

/// <summary>
/// The Jobs tab against SQL Server Agent. Skipped unless DBEXPLORER_TEST_MSSQL is set ("host;port;user;password" of a
/// sysadmin) and SQL Server Agent runs (Docker: MSSQL_AGENT_ENABLED=true). Jobs named dbx_job_* are removed again.
/// </summary>
public sealed class SqlServerJobsIntegrationTests : IAsyncLifetime
{
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_MSSQL");
    private SqlServerProvider? _provider;

    private static ConnectionProfile Profile()
    {
        var p = Settings!.Split(';');
        return new ConnectionProfile
        {
            ProviderKey = SqlServerProviderFactory.ProviderKey, Host = p[0], Port = int.Parse(p[1]),
            UserName = p[2], Password = p[3], Database = "master", Name = "test", TrustServerCertificate = true
        };
    }

    private static async Task CleanAsync()
    {
        await using var cn = new SqlConnection(SqlServerSql.BuildConnectionString(Profile(), "tests", forceReadWrite: true));
        await cn.OpenAsync();
        await using var cmd = new SqlCommand("""
            DECLARE @id uniqueidentifier;
            WHILE 1 = 1
            BEGIN
                SET @id = (SELECT TOP (1) job_id FROM msdb.dbo.sysjobs WHERE name LIKE N'dbx[_]job[_]%');
                IF @id IS NULL BREAK;
                EXEC msdb.dbo.sp_delete_job @job_id = @id, @delete_unused_schedule = 1;
            END
            """, cn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync()
    {
        if (Settings is null) return;
        await CleanAsync();
        _provider = new SqlServerProvider(Profile());
    }

    public async Task DisposeAsync()
    {
        if (Settings is null) return;
        if (_provider is not null) await _provider.DisposeAsync();
        await CleanAsync();
    }

    [SkippableFact]
    public async Task Creates_runs_changes_and_deletes_an_agent_job()
    {
        Skip.If(Settings is null, "DBEXPLORER_TEST_MSSQL not set");
        var catalog = await JobCatalogLoader.LoadAsync(_provider!, "master");
        Assert.True(catalog.Available, catalog.Setup);
        Skip.If(catalog.Setup is not null, "SQL Server Agent is not running: " + catalog.Setup);
        Assert.Contains("running", catalog.Source);

        var draft = new JobDraft
        {
            Name = "dbx_job_cleanup", Database = "master", Description = "Owner's nightly job", Command = "SELECT N'it''s done';",
            Schedule = new JobScheduleSpec { Kind = ScheduleKind.Weekly, At = new TimeOnly(2, 30), Days = [DayOfWeek.Monday, DayOfWeek.Thursday] }
        };
        Assert.Null(JobScriptBuilder.Problem(catalog, draft, null));
        await JobScriptBuilder.RunAsync(_provider!, JobScriptBuilder.Create(catalog, draft, DateTime.Now), 30);

        catalog = await JobCatalogLoader.LoadAsync(_provider!, "master");
        var job = Assert.Single(catalog.Jobs, j => j.Name == "dbx_job_cleanup");
        Assert.Equal("Owner's nightly job", job.Description);
        Assert.Equal("Weekly on Mon, Thu at 02:30", job.ScheduleDescription);
        Assert.True(draft.Schedule!.Matches(job.Schedule));
        var step = Assert.Single(job.Steps);
        Assert.Equal("master", step.Database);
        Assert.Equal("SELECT N'it''s done';", job.Command);
        Assert.True(job.CommandEditable);
        Assert.Null(job.LastRunAt);

        // Nothing changed: nothing to run.
        Assert.True(JobScriptBuilder.Update(catalog, job, JobScriptBuilder.DraftOf(job), DateTime.Now).IsEmpty);

        await JobScriptBuilder.RunAsync(_provider!, JobScriptBuilder.RunNow(job), 30);
        IReadOnlyList<JobRun> runs = [];
        for (var i = 0; i < 60 && !runs.Any(r => r.StepId == 0); i++)
        {
            await Task.Delay(500);
            runs = await JobCatalogLoader.LoadHistoryAsync(_provider!, catalog, job);
        }
        var outcome = Assert.Single(runs, r => r.StepId == 0);
        Assert.Equal(JobOutcome.Succeeded, outcome.Outcome);
        Assert.Contains(runs, r => r.StepId == 1 && r.StepName == "Run");

        catalog = await JobCatalogLoader.LoadAsync(_provider!, "master");
        job = catalog.Jobs.Single(j => j.Name == "dbx_job_cleanup");
        Assert.Equal(JobOutcome.Succeeded, job.LastOutcome);
        Assert.NotNull(job.LastRunAt);

        var changed = JobScriptBuilder.DraftOf(job) with
        {
            Name = "dbx_job_cleanup2", Enabled = false, Command = "SELECT 2;",
            Schedule = new JobScheduleSpec { Kind = ScheduleKind.EveryMinutes, Interval = 15 }
        };
        var update = JobScriptBuilder.Update(catalog, job, changed, DateTime.Now);
        Assert.Contains("sp_detach_schedule", update.Sql);
        await JobScriptBuilder.RunAsync(_provider!, update, 30);

        catalog = await JobCatalogLoader.LoadAsync(_provider!, "master");
        job = Assert.Single(catalog.Jobs, j => j.Name == "dbx_job_cleanup2");
        Assert.False(job.Enabled);
        Assert.Equal("Every 15 minutes", job.ScheduleDescription);
        Assert.Equal("SELECT 2;", job.Command);
        Assert.Single(job.AgentSchedules);

        // Script job recreates it under another name.
        var script = JobScriptBuilder.ScriptAsCreate(catalog, job, DateTime.Now).Replace("dbx_job_cleanup2", "dbx_job_copy");
        await using (var run = await _provider!.BeginScriptSessionAsync(null, true))
        {
            await run.ExecuteAsync(script, 30);
            await run.CommitAsync();
        }
        catalog = await JobCatalogLoader.LoadAsync(_provider!, "master");
        var copy = Assert.Single(catalog.Jobs, j => j.Name == "dbx_job_copy");
        Assert.Equal(job.ScheduleDescription, copy.ScheduleDescription);
        Assert.Equal("SELECT 2;", copy.Command);

        await JobScriptBuilder.RunAsync(_provider!, JobScriptBuilder.Delete(catalog, job), 30);
        await JobScriptBuilder.RunAsync(_provider!, JobScriptBuilder.Delete(catalog, copy), 30);
        catalog = await JobCatalogLoader.LoadAsync(_provider!, "master");
        Assert.DoesNotContain(catalog.Jobs, j => j.Name.StartsWith("dbx_job_", StringComparison.Ordinal));
    }
}

/// <summary>
/// The Jobs tab against MySQL events. Skipped unless DBEXPLORER_TEST_MYSQL is set ("host;port;user;password" of a login
/// that may create the schema dbx_jobs_test and set event_scheduler).
/// </summary>
public sealed class MySqlJobsIntegrationTests : IAsyncLifetime
{
    private const string Database = "dbx_jobs_test";
    private static readonly string? Settings = Environment.GetEnvironmentVariable("DBEXPLORER_TEST_MYSQL");
    private MySqlProvider? _provider;

    private static ConnectionProfile Profile(string database)
    {
        var p = Settings!.Split(';');
        return new ConnectionProfile
        {
            ProviderKey = MySqlProviderFactory.ProviderKey, Host = p[0], Port = int.Parse(p[1]),
            UserName = p[2], Password = p[3], Database = database, Name = "test"
        };
    }

    private static async Task ExecAsync(string sql)
    {
        await using var cn = new MySqlConnection(MySqlSql.BuildConnectionString(Profile("")));
        await cn.OpenAsync();
        await using var cmd = new MySqlCommand(sql, cn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task InitializeAsync()
    {
        if (Settings is null) return;
        await ExecAsync($"DROP DATABASE IF EXISTS {Database}; CREATE DATABASE {Database}; CREATE TABLE {Database}.ticks (id int AUTO_INCREMENT PRIMARY KEY, at datetime(6));");
        _provider = new MySqlProvider(Profile(Database));
    }

    public async Task DisposeAsync()
    {
        if (Settings is null) return;
        if (_provider is not null) await _provider.DisposeAsync();
        await ExecAsync($"DROP DATABASE IF EXISTS {Database}; SET GLOBAL event_scheduler = ON;");
    }

    [SkippableFact]
    public async Task Creates_runs_changes_and_drops_an_event()
    {
        Skip.If(Settings is null, "DBEXPLORER_TEST_MYSQL not set");
        await ExecAsync("SET GLOBAL event_scheduler = OFF");
        var catalog = await JobCatalogLoader.LoadAsync(_provider!, Database);
        Assert.Equal(JobEngine.MySqlEvents, catalog.Engine);
        Assert.True(catalog.SchedulerCanBeStarted);
        Assert.Contains("OFF", catalog.Setup);
        await JobScriptBuilder.RunAsync(_provider!, JobScriptBuilder.StartEventScheduler(), 30);
        catalog = await JobCatalogLoader.LoadAsync(_provider!, Database);
        Assert.Null(catalog.Setup);

        var draft = new JobDraft
        {
            Name = "dbx_job_tick", Database = Database, Description = "Back\\slash and 'quote'",
            Command = "INSERT INTO ticks (at) VALUES (NOW(6));\nINSERT INTO ticks (at) VALUES (NOW(6));",
            Schedule = new JobScheduleSpec { Kind = ScheduleKind.EveryMinutes, Interval = 1 }
        };
        var create = JobScriptBuilder.Create(catalog, draft, catalog.ServerNow!.Value);
        Assert.Contains("BEGIN", create.Sql);
        await JobScriptBuilder.RunAsync(_provider!, create, 30);

        catalog = await JobCatalogLoader.LoadAsync(_provider!, Database);
        var job = Assert.Single(catalog.Jobs, j => j.Name == "dbx_job_tick");
        Assert.Equal(Database, job.Database);
        Assert.Equal("Back\\slash and 'quote'", job.Description);
        Assert.Equal("Every minute", job.ScheduleDescription);
        Assert.True(job.Enabled);
        Assert.NotNull(job.NextRunAt);
        Assert.Contains("INSERT INTO ticks", job.Command);
        Assert.True(JobScriptBuilder.Update(catalog, job, JobScriptBuilder.DraftOf(job), catalog.ServerNow!.Value).IsEmpty);

        var daily = JobScriptBuilder.DraftOf(job) with
        {
            Name = "dbx_job_daily", Enabled = false, Schedule = new JobScheduleSpec { Kind = ScheduleKind.Daily, At = new TimeOnly(4, 5) }
        };
        await JobScriptBuilder.RunAsync(_provider!, JobScriptBuilder.Update(catalog, job, daily, catalog.ServerNow!.Value), 30);
        catalog = await JobCatalogLoader.LoadAsync(_provider!, Database);
        job = Assert.Single(catalog.Jobs, j => j.Database == Database);
        Assert.Equal("dbx_job_daily", job.Name);
        Assert.False(job.Enabled);
        Assert.Equal("Daily at 04:05", job.ScheduleDescription);

        // A one-time event a second away runs and, with PRESERVE, stays.
        var once = new JobDraft
        {
            Name = "dbx_job_once", Database = Database, Command = "INSERT INTO ticks (at) VALUES (NOW(6))", Preserve = true,
            Schedule = new JobScheduleSpec { Kind = ScheduleKind.Once, OnceAt = catalog.ServerNow!.Value.AddSeconds(2) }
        };
        await JobScriptBuilder.RunAsync(_provider!, JobScriptBuilder.Create(catalog, once, catalog.ServerNow!.Value), 30);
        ScheduledJob? ran = null;
        for (var i = 0; i < 20 && ran?.LastRunAt is null; i++)
        {
            await Task.Delay(500);
            catalog = await JobCatalogLoader.LoadAsync(_provider!, Database);
            ran = catalog.Jobs.SingleOrDefault(j => j.Name == "dbx_job_once");
        }
        Assert.NotNull(ran?.LastRunAt);

        foreach (var e in catalog.Jobs.Where(j => j.Database == Database)) await JobScriptBuilder.RunAsync(_provider!, JobScriptBuilder.Delete(catalog, e), 30);
        catalog = await JobCatalogLoader.LoadAsync(_provider!, Database);
        Assert.DoesNotContain(catalog.Jobs, j => j.Database == Database);
    }
}
