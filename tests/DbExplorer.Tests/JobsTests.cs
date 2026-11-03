using DbExplorer.Application.Jobs;

namespace DbExplorer.Tests;

public class CronScheduleTests
{
    private static CronSchedule Parse(string text)
    {
        Assert.True(CronSchedule.TryParse(text, out var cron, out var error), error);
        return cron!;
    }

    [Theory]
    [InlineData("*/15 * * * *", "2026-11-03 10:07", "2026-11-03 10:15")]
    [InlineData("0 3 * * *", "2026-11-03 03:00", "2026-11-04 03:00")]
    [InlineData("30 2 * * 1-5", "2026-11-06 03:00", "2026-11-09 02:30")] // Friday after the run: next is Monday
    [InlineData("0 0 $ * *", "2026-02-10 00:00", "2026-02-28 00:00")]
    [InlineData("0 0 1 * 1", "2026-11-03 00:00", "2026-11-09 00:00")] // day 1 OR Monday: Monday comes first
    [InlineData("@daily", "2026-11-03 10:00", "2026-11-04 00:00")]
    [InlineData("0 12 * JAN,feb SUN", "2026-11-03 00:00", "2027-01-03 12:00")]
    [InlineData("5/20 * * * *", "2026-11-03 10:26", "2026-11-03 10:45")]
    [InlineData("0 0 * * 7", "2026-11-03 00:00", "2026-11-08 00:00")] // 7 is Sunday
    public void Next_run(string expression, string after, string expected) =>
        Assert.Equal(DateTime.Parse(expected), Parse(expression).Next(DateTime.Parse(after)));

    [Fact]
    public void Seconds_interval()
    {
        var cron = Parse("30 seconds");
        Assert.Equal(30, cron.Seconds);
        Assert.Equal("Every 30 seconds", cron.Describe());
    }

    [Theory]
    [InlineData("61 * * * *", "out of range")]
    [InlineData("* * *", "five fields")]
    [InlineData("@reboot", "@reboot")]
    [InlineData("0 25 * * *", "out of range")]
    [InlineData("x * * * *", "not a valid minute")]
    [InlineData("*/0 * * * *", "positive")]
    [InlineData("90 seconds", "1 to 59")]
    [InlineData("", "Type a cron")]
    public void Rejects(string expression, string message)
    {
        Assert.False(CronSchedule.TryParse(expression, out _, out var error));
        Assert.Contains(message, error);
    }

    [Theory]
    [InlineData("0 3 * * *", "Daily at 03:00")]
    [InlineData("*/10 * * * *", "Every 10 minutes")]
    [InlineData("* * * * *", "Every minute")]
    [InlineData("15 * * * *", "Every hour at :15")]
    [InlineData("0 */6 * * *", "Every 6 hours")]
    [InlineData("30 2 * * 1-5", "Weekdays at 02:30")]
    [InlineData("0 9 * * 1,3", "Weekly on Mon, Wed at 09:00")]
    [InlineData("0 0 1 * *", "Monthly on day 1 at 00:00")]
    [InlineData("0 0 $ * *", "At 00:00 on day last of the month")]
    [InlineData("0 9 * 1 *", "At 09:00 in Jan")]
    [InlineData("@hourly", "Every hour")]
    public void Describes(string expression, string expected) => Assert.Equal(expected, Parse(expression).Describe());

    [Fact]
    public void Next_runs_in_the_cron_time_zone_come_back_in_local_time()
    {
        var cron = Parse("0 3 * * *");
        var utcNow = new DateTime(2026, 11, 3, 1, 0, 0, DateTimeKind.Utc);
        var next = JobCatalogLoader.NextCronRun(cron, "GMT", utcNow);
        Assert.Equal(new DateTime(2026, 11, 3, 3, 0, 0, DateTimeKind.Utc).ToLocalTime(), next);
        Assert.Equal(3, JobCatalogLoader.NextCronRuns(cron, null, utcNow, 3).Count);
    }
}

public class JobScheduleTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0); // a Tuesday

    public static TheoryData<JobScheduleSpec, string> CronCases => new()
    {
        { new JobScheduleSpec { Kind = ScheduleKind.EveryMinutes, Interval = 5 }, "*/5 * * * *" },
        { new JobScheduleSpec { Kind = ScheduleKind.EveryMinutes, Interval = 1 }, "* * * * *" },
        { new JobScheduleSpec { Kind = ScheduleKind.EveryHours, Interval = 1, At = new TimeOnly(0, 30) }, "30 * * * *" },
        { new JobScheduleSpec { Kind = ScheduleKind.EveryHours, Interval = 4, At = new TimeOnly(0, 0) }, "0 */4 * * *" },
        { new JobScheduleSpec { Kind = ScheduleKind.Daily, At = new TimeOnly(3, 15) }, "15 3 * * *" },
        { new JobScheduleSpec { Kind = ScheduleKind.Weekly, At = new TimeOnly(9, 0), Days = [DayOfWeek.Wednesday, DayOfWeek.Monday] }, "0 9 * * 1,3" },
        { new JobScheduleSpec { Kind = ScheduleKind.Monthly, At = new TimeOnly(0, 0), DayOfMonth = 15 }, "0 0 15 * *" }
    };

    [Theory]
    [MemberData(nameof(CronCases))]
    public void Cron_round_trip(JobScheduleSpec spec, string cron)
    {
        Assert.Equal(cron, JobScheduleText.ToCron(spec));
        Assert.True(spec.Matches(JobScheduleText.FromCron(cron)));
    }

    [Fact]
    public void Unusual_cron_stays_as_typed()
    {
        var spec = JobScheduleText.FromCron("0 9-17 * * 1-5");
        Assert.Equal(ScheduleKind.Cron, spec.Kind);
        Assert.Equal("0 9-17 * * 1-5", JobScheduleText.ToCron(spec));
    }

    [Fact]
    public void Engine_limits()
    {
        Assert.Contains("one-time", new JobScheduleSpec { Kind = ScheduleKind.Once, OnceAt = Now }.Problem(JobEngine.PgCron));
        Assert.Contains("also run at :00", new JobScheduleSpec { Kind = ScheduleKind.EveryMinutes, Interval = 7 }.Problem(JobEngine.PgCron));
        Assert.Null(new JobScheduleSpec { Kind = ScheduleKind.EveryMinutes, Interval = 7 }.Problem(JobEngine.SqlServerAgent));
        Assert.Contains("one weekday", new JobScheduleSpec { Kind = ScheduleKind.Weekly, Days = [DayOfWeek.Monday, DayOfWeek.Friday] }.Problem(JobEngine.MySqlEvents));
        Assert.Contains("Only pg_cron", new JobScheduleSpec { Kind = ScheduleKind.Cron, Cron = "* * * * *" }.Problem(JobEngine.MySqlEvents));
        Assert.Contains("at least one day", new JobScheduleSpec { Kind = ScheduleKind.Weekly, Days = [] }.Problem(JobEngine.SqlServerAgent));
        Assert.Contains("five fields", new JobScheduleSpec { Kind = ScheduleKind.Cron, Cron = "0 3" }.Problem(JobEngine.PgCron));
    }

    [Theory]
    [InlineData(ScheduleKind.Daily, "EVERY 1 DAY STARTS '2026-11-04 03:00:00'")]
    [InlineData(ScheduleKind.Weekly, "EVERY 1 WEEK STARTS '2026-11-09 03:00:00'")]
    [InlineData(ScheduleKind.Monthly, "EVERY 1 MONTH STARTS '2026-12-01 03:00:00'")]
    [InlineData(ScheduleKind.EveryMinutes, "EVERY 15 MINUTE")]
    public void MySql_schedule(ScheduleKind kind, string expected)
    {
        var spec = new JobScheduleSpec { Kind = kind, Interval = 15, At = new TimeOnly(3, 0), Days = [DayOfWeek.Monday], DayOfMonth = 1 };
        Assert.Equal(expected, JobScheduleText.ToMySql(spec, Now));
    }

    [Fact]
    public void MySql_every_hour_at_a_minute_and_once()
    {
        Assert.Equal("EVERY 2 HOUR STARTS '2026-11-03 10:20:00'",
            JobScheduleText.ToMySql(new JobScheduleSpec { Kind = ScheduleKind.EveryHours, Interval = 2, At = new TimeOnly(0, 20) }, Now));
        Assert.Equal("AT '2026-12-24 18:30:00'",
            JobScheduleText.ToMySql(new JobScheduleSpec { Kind = ScheduleKind.Once, OnceAt = new DateTime(2026, 12, 24, 18, 30, 0) }, Now));
        Assert.Throws<ArgumentException>(() => JobScheduleText.ToMySql(new JobScheduleSpec { Kind = ScheduleKind.Monthly, DayOfMonth = 31 }, Now));
    }

    [Fact]
    public void MySql_event_read_back()
    {
        var starts = new DateTime(2026, 1, 1, 3, 0, 0);
        var spec = JobScheduleText.FromMySql("RECURRING", null, "1", "DAY", starts, null);
        Assert.Equal(ScheduleKind.Daily, spec!.Kind);
        Assert.Equal(new TimeOnly(3, 0), spec.At);
        Assert.Equal("Daily at 03:00", JobScheduleText.DescribeMySql("RECURRING", null, "1", "DAY", starts, null));
        Assert.Equal(new DateTime(2026, 11, 4, 3, 0, 0), JobScheduleText.NextMySql("RECURRING", null, "1", "DAY", starts, null, Now));
        Assert.Equal(new DateTime(2026, 12, 1, 3, 0, 0), JobScheduleText.NextMySql("RECURRING", null, "1", "MONTH", starts, null, Now));
        Assert.Null(JobScheduleText.NextMySql("RECURRING", null, "1", "DAY", starts, new DateTime(2026, 6, 1), Now));
        Assert.Null(JobScheduleText.FromMySql("RECURRING", null, "'1 12'", "DAY_HOUR", starts, null));
        Assert.Equal("Every 1 12 day hour from 2026-01-01 03:00", JobScheduleText.DescribeMySql("RECURRING", null, "'1 12'", "DAY_HOUR", starts, null));
        Assert.Equal("EVERY 1 DAY STARTS '2026-01-01 03:00:00'", JobScheduleText.MySqlScheduleText("RECURRING", null, "1", "DAY", starts, null));
        Assert.Equal(ScheduleKind.Once, JobScheduleText.FromMySql("ONE TIME", Now, null, null, null, null)!.Kind);
    }

    [Fact]
    public void Agent_schedules_round_trip()
    {
        var weekly = new JobScheduleSpec { Kind = ScheduleKind.Weekly, At = new TimeOnly(2, 30), Days = [DayOfWeek.Monday, DayOfWeek.Wednesday] };
        var agent = JobScheduleText.ToAgent(weekly, "nightly", Now);
        Assert.Equal(8, agent.FreqType);
        Assert.Equal(2 + 8, agent.FreqInterval);
        Assert.Equal(1, agent.FreqRecurrenceFactor);
        Assert.Equal(23000, agent.ActiveStartTime);
        Assert.Equal(20261103, agent.ActiveStartDate);
        Assert.True(weekly.Matches(JobScheduleText.FromAgent(agent)));
        Assert.Equal("Weekly on Mon, Wed at 02:30", agent.Description);

        foreach (var spec in new[]
                 {
                     new JobScheduleSpec { Kind = ScheduleKind.EveryMinutes, Interval = 15 },
                     new JobScheduleSpec { Kind = ScheduleKind.EveryHours, Interval = 2, At = new TimeOnly(0, 10) },
                     new JobScheduleSpec { Kind = ScheduleKind.Daily, At = new TimeOnly(23, 59) },
                     new JobScheduleSpec { Kind = ScheduleKind.Monthly, At = new TimeOnly(1, 0), DayOfMonth = 31 },
                     new JobScheduleSpec { Kind = ScheduleKind.Once, OnceAt = new DateTime(2027, 1, 2, 4, 5, 0) }
                 })
            Assert.True(spec.Matches(JobScheduleText.FromAgent(JobScheduleText.ToAgent(spec, "s", Now))), spec.Describe());
    }

    [Fact]
    public void Agent_schedules_in_words()
    {
        Assert.Equal("Every 15 minutes", new AgentSchedule { FreqType = 4, FreqInterval = 1, FreqSubdayType = 4, FreqSubdayInterval = 15 }.Description);
        Assert.Equal("Daily at 03:00", new AgentSchedule { FreqType = 4, FreqInterval = 1, FreqSubdayType = 1, ActiveStartTime = 30000 }.Description);
        Assert.Equal("Every 2 hours between 08:00 and 18:00",
            new AgentSchedule { FreqType = 4, FreqInterval = 1, FreqSubdayType = 8, FreqSubdayInterval = 2, ActiveStartTime = 80000, ActiveEndTime = 180000 }.Description);
        Assert.Equal("Monthly on the last Friday at 22:00",
            new AgentSchedule { FreqType = 32, FreqInterval = 6, FreqRelativeInterval = 16, FreqRecurrenceFactor = 1, FreqSubdayType = 1, ActiveStartTime = 220000 }.Description);
        Assert.Equal("When SQL Server Agent starts", new AgentSchedule { FreqType = 64 }.Description);
        Assert.Equal("Daily at 03:00 (schedule disabled)", new AgentSchedule { FreqType = 4, FreqInterval = 1, FreqSubdayType = 1, ActiveStartTime = 30000, Enabled = false }.Description);
        Assert.Null(JobScheduleText.FromAgent(new AgentSchedule { FreqType = 32, FreqInterval = 6, FreqRelativeInterval = 16 }));
        Assert.Equal(new TimeSpan(1, 2, 3), JobScheduleText.AgentDuration(10203));
        Assert.Null(JobScheduleText.AgentDateTime(0, 0));
    }
}

public class JobScriptBuilderTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0);
    private static readonly JobScheduleSpec Daily = new() { Kind = ScheduleKind.Daily, At = new TimeOnly(3, 0) };

    private static JobCatalog Catalog(JobEngine engine, params ScheduledJob[] jobs) =>
        new() { Engine = engine, Jobs = jobs, HomeDatabase = engine == JobEngine.PgCron ? "postgres" : null, Version = "1.6" };

    private static ScheduledJob Job(JobEngine engine, string id = "1", string name = "purge", string? database = "app") => new()
    {
        Id = id, Name = name, Engine = engine, Database = database, Enabled = true, Command = "DELETE FROM log", Schedule = Daily,
        ScheduleText = engine == JobEngine.PgCron ? "0 3 * * *" : "",
        Steps = engine == JobEngine.SqlServerAgent ? [new JobStep(1, "Run", "TSQL", database, "DELETE FROM log")] : [],
        AgentSchedules = engine == JobEngine.SqlServerAgent ? [new AgentSchedule { Id = 7, Name = "nightly", FreqType = 4, FreqInterval = 1, FreqSubdayType = 1, ActiveStartTime = 30000 }] : []
    };

    [Fact]
    public void Sql_server_create_quotes_text_and_adds_step_schedule_and_server()
    {
        var script = JobScriptBuilder.Create(Catalog(JobEngine.SqlServerAgent),
            new JobDraft { Name = "O'Brien's job", Database = "Sales", Command = "UPDATE t SET a = 'x'", Schedule = Daily, Description = "it's" }, Now);
        Assert.True(script.Transactional);
        Assert.Contains("@job_name = N'O''Brien''s job'", script.Sql);
        Assert.Contains("@description = N'it''s'", script.Sql);
        Assert.Contains("@database_name = N'Sales', @command = N'UPDATE t SET a = ''x'''", script.Sql);
        Assert.Contains("sp_add_jobschedule @job_id = @job_id, @name = N'O''Brien''s job', @enabled = 1, @freq_type = 4", script.Sql);
        Assert.Contains("@active_start_time = 30000", script.Sql);
        Assert.Contains("sp_add_jobserver", script.Sql);
    }

    [Fact]
    public void Sql_server_update_sends_only_changes()
    {
        var job = Job(JobEngine.SqlServerAgent, id: "6f0a3b6e-1111-2222-3333-444455556666");
        var catalog = Catalog(JobEngine.SqlServerAgent, job);
        Assert.True(JobScriptBuilder.Update(catalog, job, JobScriptBuilder.DraftOf(job), Now).IsEmpty);

        var renamed = JobScriptBuilder.Update(catalog, job, JobScriptBuilder.DraftOf(job) with { Name = "purge2", Enabled = false }, Now);
        Assert.Equal("EXEC msdb.dbo.sp_update_job @job_id = N'6f0a3b6e-1111-2222-3333-444455556666', @new_name = N'purge2', @enabled = 0;", renamed.Sql);

        var moved = JobScriptBuilder.Update(catalog, job, JobScriptBuilder.DraftOf(job) with
        {
            Command = "SELECT 1", Schedule = new JobScheduleSpec { Kind = ScheduleKind.EveryMinutes, Interval = 5 }
        }, Now);
        Assert.Contains("sp_update_jobstep @job_id = N'6f0a3b6e-1111-2222-3333-444455556666', @step_id = 1, @command = N'SELECT 1';", moved.Sql);
        Assert.Contains("sp_detach_schedule @job_id = N'6f0a3b6e-1111-2222-3333-444455556666', @schedule_id = 7, @delete_unused_schedule = 1;", moved.Sql);
        Assert.Contains("@freq_subday_type = 4, @freq_subday_interval = 5", moved.Sql);
    }

    [Fact]
    public void Sql_server_actions()
    {
        var job = Job(JobEngine.SqlServerAgent, id: "6f0a3b6e-1111-2222-3333-444455556666");
        var catalog = Catalog(JobEngine.SqlServerAgent, job);
        Assert.Equal("EXEC msdb.dbo.sp_start_job @job_id = N'6f0a3b6e-1111-2222-3333-444455556666';", JobScriptBuilder.RunNow(job).Sql);
        Assert.False(JobScriptBuilder.RunNow(job).Transactional);
        Assert.Contains("sp_stop_job", JobScriptBuilder.Stop(job).Sql);
        Assert.Contains("@delete_unused_schedule = 1", JobScriptBuilder.Delete(catalog, job).Sql);
        Assert.Contains("@enabled = 0", JobScriptBuilder.SetEnabled(catalog, job, false).Sql);
        var script = JobScriptBuilder.ScriptAsCreate(catalog, job, Now);
        Assert.Contains("@step_name = N'Run'", script);
        Assert.Contains("@on_success_action = 1", script);
        Assert.Contains("@name = N'nightly'", script);
    }

    [Fact]
    public void Multi_step_agent_jobs_keep_their_steps()
    {
        var job = Job(JobEngine.SqlServerAgent) with
        {
            Command = "",
            Steps = [new JobStep(1, "Load", "TSQL", "Stage", "EXEC dbo.Load"), new JobStep(2, "Copy", "CmdExec", null, "robocopy a b")]
        };
        Assert.False(job.CommandEditable);
        var sql = JobScriptBuilder.CommandForQuery(job);
        Assert.Contains("-- Step 1: Load (in Stage)", sql);
        Assert.Contains("USE [Stage];", sql);
        Assert.Contains("/*\nrobocopy a b\n*/", sql);
        var catalog = Catalog(JobEngine.SqlServerAgent, job);
        Assert.Null(JobScriptBuilder.Problem(catalog, JobScriptBuilder.DraftOf(job), job)); // the empty command is not asked for
        Assert.DoesNotContain("sp_update_jobstep", JobScriptBuilder.Update(catalog, job, JobScriptBuilder.DraftOf(job) with { Command = "x" }, Now).Sql);
    }

    [Fact]
    public void Pg_cron_create_and_update()
    {
        var catalog = Catalog(JobEngine.PgCron);
        var create = JobScriptBuilder.Create(catalog, new JobDraft { Name = "vacuum", Database = "app", Command = "VACUUM t; -- it's", Schedule = Daily, Enabled = false }, Now);
        Assert.Equal("SELECT cron.schedule_in_database('vacuum', '0 3 * * *', $cmd$VACUUM t; -- it's$cmd$, 'app', active := false);", create.Sql);
        Assert.Equal("postgres", create.Database);

        var old = catalog with { Version = "1.3" };
        Assert.Equal("SELECT cron.schedule('vacuum', '0 3 * * *', $cmd$SELECT 1$cmd$);",
            JobScriptBuilder.Create(old, new JobDraft { Name = "vacuum", Command = "SELECT 1", Schedule = Daily }, Now).Sql);

        var job = Job(JobEngine.PgCron, id: "42");
        catalog = Catalog(JobEngine.PgCron, job);
        Assert.True(JobScriptBuilder.Update(catalog, job, JobScriptBuilder.DraftOf(job), Now).IsEmpty);
        Assert.Equal("SELECT cron.alter_job(job_id := 42, schedule := '*/5 * * * *', active := false);",
            JobScriptBuilder.Update(catalog, job, JobScriptBuilder.DraftOf(job) with
            {
                Enabled = false, Schedule = new JobScheduleSpec { Kind = ScheduleKind.EveryMinutes, Interval = 5 }
            }, Now).Sql);
        Assert.Equal("SELECT cron.unschedule(42);", JobScriptBuilder.Delete(catalog, job).Sql);
        Assert.Equal("SELECT cron.alter_job(job_id := 42, active := true);", JobScriptBuilder.SetEnabled(catalog, job, true).Sql);
    }

    [Fact]
    public void Pg_cron_refuses_a_name_it_would_overwrite()
    {
        var catalog = Catalog(JobEngine.PgCron, Job(JobEngine.PgCron));
        Assert.Contains("silently replace", JobScriptBuilder.Problem(catalog, new JobDraft { Name = "PURGE", Command = "x", Schedule = Daily }, null));
        Assert.Contains("Give the job a name", JobScriptBuilder.Problem(catalog, new JobDraft { Name = " ", Command = "x", Schedule = Daily }, null));
        Assert.Contains("Type the SQL", JobScriptBuilder.Problem(catalog, new JobDraft { Name = "n", Command = " ", Schedule = Daily }, null));
    }

    [Theory]
    [InlineData("$cmd$", "$job$")]
    [InlineData("$cmd$ $job$ $sql$", "$c1$")]
    public void Dollar_quotes_pick_a_free_tag(string text, string tag) => Assert.StartsWith(tag, JobScriptBuilder.Dollar(text));

    [Fact]
    public void MySql_create_alter_and_drop()
    {
        var catalog = Catalog(JobEngine.MySqlEvents);
        var create = JobScriptBuilder.Create(catalog, new JobDraft
        {
            Name = "roll`up", Database = "shop", Command = "DELETE FROM a;\nDELETE FROM b;", Description = "c:\\temp 'x'",
            Schedule = new JobScheduleSpec { Kind = ScheduleKind.EveryHours, Interval = 1 }, Preserve = false
        }, Now);
        Assert.Equal("""
            CREATE EVENT `shop`.`roll``up`
              ON SCHEDULE EVERY 1 HOUR
              ON COMPLETION NOT PRESERVE
              ENABLE
              COMMENT 'c:\\temp ''x'''
            DO BEGIN
              DELETE FROM a;
              DELETE FROM b;
            END;
            """.ReplaceLineEndings("\n"), create.Sql);
        Assert.False(create.Transactional);
        Assert.Contains("Pick the database", JobScriptBuilder.Problem(catalog, new JobDraft { Name = "n", Command = "x", Schedule = Daily }, null));

        var job = Job(JobEngine.MySqlEvents, id: "shop.cleanup", name: "cleanup", database: "shop");
        catalog = Catalog(JobEngine.MySqlEvents, job);
        Assert.True(JobScriptBuilder.Update(catalog, job, JobScriptBuilder.DraftOf(job) with { Command = "DELETE FROM log;" }, Now).IsEmpty);
        Assert.Equal("ALTER EVENT `shop`.`cleanup`\n  RENAME TO `shop`.`cleanup2`\n  DISABLE;",
            JobScriptBuilder.Update(catalog, job, JobScriptBuilder.DraftOf(job) with { Name = "cleanup2", Enabled = false }, Now).Sql);
        Assert.Equal("DROP EVENT `shop`.`cleanup`;", JobScriptBuilder.Delete(catalog, job).Sql);
        Assert.Equal("ALTER EVENT `shop`.`cleanup` ENABLE;", JobScriptBuilder.SetEnabled(catalog, job, true).Sql);
    }

    [Theory]
    [InlineData("SELECT 1;", "SELECT 1")]
    [InlineData("BEGIN\n  SELECT 1;\nEND;", "BEGIN\n  SELECT 1;\nEND")]
    [InlineData("CALL a(); CALL b();", "BEGIN\n  CALL a(); CALL b();\nEND")]
    public void MySql_body(string command, string expected) => Assert.Equal(expected, JobScriptBuilder.MyBody(command));
}

public class JobsTabTextTests
{
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0);

    [Fact]
    public void Times_and_durations_read_naturally()
    {
        Assert.Equal("today 03:00", DbExplorer.Desktop.ViewModels.JobTimeText.When(new DateTime(2026, 11, 3, 3, 0, 0), Now));
        Assert.Equal("tomorrow 03:00", DbExplorer.Desktop.ViewModels.JobTimeText.When(new DateTime(2026, 11, 4, 3, 0, 0), Now));
        Assert.Equal("yesterday 23:10", DbExplorer.Desktop.ViewModels.JobTimeText.When(new DateTime(2026, 11, 2, 23, 10, 0), Now));
        Assert.Equal("Sat 01:30", DbExplorer.Desktop.ViewModels.JobTimeText.When(new DateTime(2026, 11, 7, 1, 30, 0), Now));
        Assert.Equal("2026-12-31 23:00", DbExplorer.Desktop.ViewModels.JobTimeText.When(new DateTime(2026, 12, 31, 23, 0, 0), Now));
        Assert.Equal("<1 s", DbExplorer.Desktop.ViewModels.JobTimeText.Duration(TimeSpan.FromMilliseconds(300)));
        Assert.Equal("2 min 5 s", DbExplorer.Desktop.ViewModels.JobTimeText.Duration(TimeSpan.FromSeconds(125)));
        Assert.Equal("1 h 1 min", DbExplorer.Desktop.ViewModels.JobTimeText.Duration(TimeSpan.FromMinutes(61)));
    }

    [Fact]
    public void List_rows_show_state_last_and_next_run()
    {
        var job = new ScheduledJob
        {
            Id = "1", Name = "purge", Engine = JobEngine.PgCron, Enabled = true, ScheduleDescription = "Every 15 minutes",
            LastRunAt = new DateTime(2026, 11, 3, 9, 45, 0), LastOutcome = JobOutcome.Failed, LastDuration = TimeSpan.FromSeconds(3),
            NextRunAt = new DateTime(2026, 11, 3, 10, 15, 0)
        };
        var item = new DbExplorer.Desktop.ViewModels.JobItem(job, Now);
        Assert.True(item.IsFailed);
        Assert.Equal("!", item.Badge);
        Assert.Equal("failed today 09:45 (3 s) · next today 10:15", item.RunsText);
        Assert.Contains("job id 1", item.Facts);

        var idle = new DbExplorer.Desktop.ViewModels.JobItem(job with { LastRunAt = null, LastOutcome = null }, Now);
        Assert.True(idle.IsIdle);
        Assert.Equal("not run yet · next today 10:15", idle.RunsText);

        var off = new DbExplorer.Desktop.ViewModels.JobItem(job with { Enabled = false, LastOutcome = JobOutcome.Succeeded }, Now);
        Assert.True(off.IsDisabled);
        Assert.Equal("Disabled · Every 15 minutes", off.Schedule);
        Assert.DoesNotContain("next", off.RunsText);
    }
}
