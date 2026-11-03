using System.Globalization;
using DbExplorer.Application.Copy;
using DbExplorer.Core.Abstractions;
using DbExplorer.Core.Models;
using DbExplorer.Core.Search;

namespace DbExplorer.Application.Jobs;

/// <summary>Reads an engine's scheduled jobs (SQL Server Agent, pg_cron, MySQL events) with read-only queries, and
/// says how to set the scheduler up when it is missing.</summary>
public static class JobCatalogLoader
{
    private const int MaxRows = 20_000;
    private static readonly DataSearchOptions Options = new(MaxRows, QueryTimeoutSeconds: 30, LockTimeoutMs: 3000);
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static JobEngine EngineOf(string providerKey) => providerKey switch
    {
        SqlDialect.SqlServerKey => JobEngine.SqlServerAgent,
        SqlDialect.MySqlKey => JobEngine.MySqlEvents,
        _ => JobEngine.PgCron
    };

    public static Task<JobCatalog> LoadAsync(IDatabaseProvider provider, string? defaultDatabase, CancellationToken ct = default) =>
        SqlDialect.IsFileEngine(provider.ProviderKey)
            ? Task.FromResult(new JobCatalog
            {
                Engine = JobEngine.PgCron, Available = false, Source = "SQLite",
                Setup = "SQLite is a file, not a server, so it has no job scheduler. Run scripts on a schedule with the " +
                        "operating system instead (Task Scheduler on Windows, cron elsewhere) using the sqlite3 command-line tool."
            })
            : EngineOf(provider.ProviderKey) switch
        {
            JobEngine.SqlServerAgent => LoadSqlServerAsync(provider, ct),
            JobEngine.MySqlEvents => LoadMySqlAsync(provider, ct),
            _ => LoadPostgresAsync(provider, defaultDatabase, ct)
        };

    // ---------- SQL Server Agent ----------

    private static async Task<JobCatalog> LoadSqlServerAsync(IDatabaseProvider provider, CancellationToken ct)
    {
        var warnings = new List<string>();
        var probe = JobRows.Of(await provider.QueryReadOnlyAsync(JobQueries.SqlServerProbe, null, Options, 1, ct)).FirstOrDefault();
        var edition = probe.Int("edition") ?? 0;
        var version = probe.Text("version");
        if (edition == 5)
            return new JobCatalog
            {
                Engine = JobEngine.SqlServerAgent, Available = false, Source = "Azure SQL Database", Version = version,
                Setup = "Azure SQL Database has no SQL Server Agent. Schedule work there with Elastic Jobs, Azure Automation or an " +
                        "Azure Function, or connect to a SQL Server or Azure SQL Managed Instance, which have the Agent."
            };

        QueryResultSet jobs;
        try
        {
            jobs = await provider.QueryReadOnlyAsync(JobQueries.SqlServerJobs, null, Options, MaxRows, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new JobCatalog
            {
                Engine = JobEngine.SqlServerAgent, Available = false, Source = "SQL Server Agent", Version = version,
                Setup = edition == 4
                    ? "SQL Server Express has no SQL Server Agent. Use Windows Task Scheduler with sqlcmd, or another edition."
                    : "The server did not show its Agent jobs (" + ex.Message + "). Reading jobs needs sysadmin, or membership in " +
                      "SQLAgentReaderRole (or SQLAgentUserRole for your own jobs) in msdb."
            };
        }
        if (jobs.IsTruncated) warnings.Add($"Only the first {MaxRows:N0} jobs are shown.");

        var steps = await TryRead(provider, JobQueries.SqlServerSteps, null, "job steps", warnings, ct);
        var schedules = await TryRead(provider, JobQueries.SqlServerSchedules, null, "job schedules", warnings, ct);
        var stepsByJob = JobRows.Of(steps).GroupBy(r => r.Text("job_id") ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => new JobStep(
                r.Int("step_id") ?? 0, r.Text("step_name") ?? "", r.Text("subsystem") ?? "", r.Text("database_name"), r.Text("command") ?? "",
                AgentAction(r.Int("on_success_action")), AgentAction(r.Int("on_fail_action")))).ToList(), StringComparer.OrdinalIgnoreCase);
        var schedulesByJob = JobRows.Of(schedules).GroupBy(r => r.Text("job_id") ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(r => new AgentSchedule
            {
                Id = r.Int("schedule_id") ?? 0, Name = r.Text("name") ?? "", Enabled = r.Bool("enabled"),
                FreqType = r.Int("freq_type") ?? 0, FreqInterval = r.Int("freq_interval") ?? 0,
                FreqSubdayType = r.Int("freq_subday_type") ?? 0, FreqSubdayInterval = r.Int("freq_subday_interval") ?? 0,
                FreqRelativeInterval = r.Int("freq_relative_interval") ?? 0, FreqRecurrenceFactor = r.Int("freq_recurrence_factor") ?? 0,
                ActiveStartDate = r.Int("active_start_date") ?? 0, ActiveEndDate = r.Int("active_end_date") ?? 99991231,
                ActiveStartTime = r.Int("active_start_time") ?? 0, ActiveEndTime = r.Int("active_end_time") ?? 235959,
                NextRun = JobScheduleText.AgentDateTime(r.Int("next_run_date") ?? 0, r.Int("next_run_time") ?? 0)
            }).ToList(), StringComparer.OrdinalIgnoreCase);

        var list = JobRows.Of(jobs).Select(r =>
        {
            var id = r.Text("job_id") ?? "";
            var jobSteps = stepsByJob.GetValueOrDefault(id) ?? [];
            var jobSchedules = schedulesByJob.GetValueOrDefault(id) ?? [];
            var lastAt = JobScheduleText.AgentDateTime(r.Int("run_date") ?? 0, r.Int("run_time") ?? 0);
            var runningSince = r.Date("running_since");
            var nextFromSchedules = jobSchedules.Where(s => s.Enabled && s.NextRun is not null).Select(s => s.NextRun).Min();
            return new ScheduledJob
            {
                Id = id,
                Name = r.Text("name") ?? "",
                Engine = JobEngine.SqlServerAgent,
                Database = r.Text("database_name"),
                Enabled = r.Bool("enabled"),
                ScheduleText = string.Join("; ", jobSchedules.Select(s => s.Name)),
                ScheduleDescription = jobSchedules.Count == 0 ? "No schedule (runs only when started)" : string.Join("; ", jobSchedules.Select(s => s.Description)),
                Schedule = jobSchedules is [var only] ? JobScheduleText.FromAgent(only) : null,
                AgentSchedules = jobSchedules,
                Owner = r.Text("owner"),
                Description = r.Text("description") is { Length: > 0 } d && d != "No description available." ? d : null,
                Category = r.Text("category"),
                Command = jobSteps is [var step] ? step.Command : "",
                Steps = jobSteps,
                LastRunAt = lastAt,
                LastOutcome = lastAt is null ? null : AgentOutcome(r.Int("run_status")),
                LastDuration = lastAt is null ? null : JobScheduleText.AgentDuration(r.Int("run_duration") ?? 0),
                LastMessage = r.Text("message"),
                IsRunning = runningSince is not null,
                RunningSince = runningSince,
                NextRunAt = r.Date("next_scheduled_run_date") ?? nextFromSchedules
            };
        }).ToList();

        // Without VIEW SERVER STATE the session list shows only this session, so a stopped Agent cannot be told apart.
        var canTell = probe.Bool("can_view_state");
        var agentUp = (probe.Int("agent_sessions") ?? 0) > 0;
        var setup = edition == 4
            ? "SQL Server Express has no SQL Server Agent: jobs are listed but never run. Use Windows Task Scheduler with sqlcmd instead."
            : canTell && !agentUp
                ? "SQL Server Agent is not running, so jobs do not run on schedule and Run now fails. Start the \"SQL Server Agent\" " +
                  "service (SQL Server Configuration Manager, or services.msc); on Linux run mssql-conf set sqlagent.enabled true and restart."
                : null;
        return new JobCatalog
        {
            Engine = JobEngine.SqlServerAgent,
            Source = "SQL Server Agent" + (edition == 4 ? " · Express (no Agent)" : !canTell ? "" : agentUp ? " · running" : " · stopped"),
            Setup = setup,
            ServerNow = probe.Date("server_now"),
            Version = version,
            Jobs = list,
            Warnings = warnings
        };
    }

    private static string? AgentAction(int? action) => action switch
    {
        1 => "quit with success",
        2 => "quit with failure",
        3 => "go to the next step",
        4 => "go to another step",
        _ => null
    };

    private static JobOutcome AgentOutcome(int? status) => status switch
    {
        0 => JobOutcome.Failed,
        1 => JobOutcome.Succeeded,
        2 => JobOutcome.Retrying,
        3 => JobOutcome.Cancelled,
        4 => JobOutcome.Running,
        _ => JobOutcome.Unknown
    };

    // ---------- pg_cron ----------

    public const string PgCronInstall =
        "pg_cron runs jobs inside PostgreSQL. To set it up: install the pg_cron package for your PostgreSQL version, add " +
        "shared_preload_libraries = 'pg_cron' (and cron.database_name = 'postgres', or the database to keep jobs in) to " +
        "postgresql.conf, restart the server, then run CREATE EXTENSION pg_cron; in that database. Managed services (Amazon RDS, " +
        "Azure, Google Cloud SQL, Supabase) turn it on with a parameter or extension setting instead.";

    private static async Task<JobCatalog> LoadPostgresAsync(IDatabaseProvider provider, string? defaultDatabase, CancellationToken ct)
    {
        var probe = JobRows.Of(await provider.QueryReadOnlyAsync(JobQueries.PostgresProbe, null, Options, 1, ct)).FirstOrDefault();
        var home = probe.Text("home");
        var current = probe.Text("current_db") ?? defaultDatabase;
        var preloaded = (probe.Text("preload") ?? "").Split(',').Any(p => p.Trim().Trim('"').Equals("pg_cron", StringComparison.OrdinalIgnoreCase));
        var version = probe.Text("version");
        var tz = probe.Text("time_zone");
        var homeDb = home ?? current;

        // The extension lives in cron.database_name only; read it there.
        if (version is null && home is not null && !string.Equals(home, current, StringComparison.Ordinal))
        {
            try
            {
                var there = JobRows.Of(await provider.QueryReadOnlyAsync(JobQueries.PostgresProbe, home, Options, 1, ct)).FirstOrDefault();
                version = there.Text("version");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Unavailable($"pg_cron keeps its jobs in the database {home}, which this connection could not open ({ex.Message}).");
            }
        }

        if (version is null)
            return Unavailable(preloaded
                ? $"pg_cron is loaded but the extension is not created yet. Run CREATE EXTENSION pg_cron; in the database {homeDb} " +
                  "(cron.database_name), then Refresh."
                : PgCronInstall);

        var warnings = new List<string>();
        QueryResultSet? jobs = null;
        try
        {
            jobs = await provider.QueryReadOnlyAsync(JobQueries.PostgresJobs, homeDb, Options, MaxRows, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            jobs = await TryRead(provider, JobQueries.PostgresJobsWithoutHistory, homeDb, "pg_cron jobs", warnings, ct);
        }
        if (jobs?.IsTruncated == true) warnings.Add($"Only the first {MaxRows:N0} jobs are shown.");
        var nowUtc = DateTime.UtcNow;
        var list = JobRows.Of(jobs).Select(r =>
        {
            var schedule = r.Text("schedule") ?? "";
            var parsed = CronSchedule.TryParse(schedule, out var cron, out _) ? cron : null;
            var status = r.Text("last_status");
            var start = ToLocal(r.Date("last_start"));
            var end = ToLocal(r.Date("last_end"));
            var running = string.Equals(status, "running", StringComparison.OrdinalIgnoreCase);
            var active = r.Bool("active");
            var nodeName = r.Text("nodename");
            return new ScheduledJob
            {
                Id = r.Text("jobid") ?? "",
                Name = r.Text("jobname") ?? "",
                Engine = JobEngine.PgCron,
                Database = r.Text("database") ?? homeDb,
                Enabled = active,
                ScheduleText = schedule,
                ScheduleDescription = parsed?.Describe() ?? schedule,
                Schedule = JobScheduleText.FromCron(schedule),
                Owner = r.Text("username"),
                Description = nodeName is { Length: > 0 } n && n != "localhost" ? $"Runs on {n}:{r.Text("nodeport")}" : null,
                Command = r.Text("command") ?? "",
                LastRunAt = start,
                LastOutcome = status switch
                {
                    null => null,
                    "succeeded" => JobOutcome.Succeeded,
                    "failed" => JobOutcome.Failed,
                    "running" or "starting" or "sending" or "connecting" => JobOutcome.Running,
                    _ => JobOutcome.Unknown
                },
                LastDuration = start is { } s && end is { } e && e >= s ? e - s : null,
                LastMessage = r.Text("last_message"),
                IsRunning = running,
                RunningSince = running ? start : null,
                NextRunAt = active && parsed is not null ? NextCronRun(parsed, tz, nowUtc) : null
            };
        }).ToList();

        if (!probe.Bool("is_superuser"))
            warnings.Add("pg_cron shows each user only their own jobs; a superuser sees every job.");
        return new JobCatalog
        {
            Engine = JobEngine.PgCron,
            Source = $"pg_cron {version} in {homeDb}" + (tz is { Length: > 0 } && !IsUtc(tz) ? $" · schedules in {tz}" : " · schedules in UTC"),
            HomeDatabase = homeDb,
            TimeZone = tz ?? "GMT",
            ServerNow = DateTime.Now,
            Version = version,
            Jobs = list,
            Warnings = warnings
        };

        JobCatalog Unavailable(string setup) => new()
        {
            Engine = JobEngine.PgCron, Available = false, Source = "pg_cron not installed", Setup = setup, HomeDatabase = homeDb, TimeZone = tz
        };
    }

    private static bool IsUtc(string tz) => tz is "GMT" or "UTC" or "Etc/UTC" or "Etc/GMT" or "Z";

    private static DateTime? ToLocal(DateTime? value) => value is { } v
        ? v.Kind switch { DateTimeKind.Utc => v.ToLocalTime(), _ => v }
        : null;

    /// <summary>The next run of a pg_cron schedule in this machine's local time; cron reads the expression in cron.timezone (GMT by default).</summary>
    public static DateTime? NextCronRun(CronSchedule cron, string? timeZone, DateTime utcNow)
    {
        var zone = FindZone(timeZone);
        var nowThere = TimeZoneInfo.ConvertTimeFromUtc(utcNow, zone);
        if (cron.Next(DateTime.SpecifyKind(nowThere, DateTimeKind.Unspecified)) is not { } next) return null;
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(next, zone).ToLocalTime();
        }
        catch (ArgumentException)
        {
            // A time skipped by a daylight-saving change.
            return TimeZoneInfo.ConvertTimeToUtc(next.AddHours(1), zone).ToLocalTime();
        }
    }

    public static IReadOnlyList<DateTime> NextCronRuns(CronSchedule cron, string? timeZone, DateTime utcNow, int count)
    {
        var runs = new List<DateTime>();
        var at = utcNow;
        while (runs.Count < count && NextCronRun(cron, timeZone, at) is { } next)
        {
            runs.Add(next);
            at = next.ToUniversalTime();
        }
        return runs;
    }

    private static TimeZoneInfo FindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || IsUtc(id)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }

    // ---------- MySQL events ----------

    private static async Task<JobCatalog> LoadMySqlAsync(IDatabaseProvider provider, CancellationToken ct)
    {
        var warnings = new List<string>();
        var probe = JobRows.Of(await provider.QueryReadOnlyAsync(JobQueries.MySqlProbe, null, Options, 1, ct)).FirstOrDefault();
        var scheduler = (probe.Text("scheduler") ?? "").ToUpperInvariant();
        var now = probe.Date("server_now");
        var events = await provider.QueryReadOnlyAsync(JobQueries.MySqlEvents, null, Options, MaxRows, ct);
        if (events.IsTruncated) warnings.Add($"Only the first {MaxRows:N0} events are shown.");
        var running = scheduler is "ON" or "1";
        var list = JobRows.Of(events).Select(r =>
        {
            var type = r.Text("event_type");
            var at = r.Date("execute_at");
            var value = r.Text("interval_value");
            var field = r.Text("interval_field");
            var starts = r.Date("starts");
            var ends = r.Date("ends");
            var status = r.Text("status") ?? "";
            var enabled = status.Equals("ENABLED", StringComparison.OrdinalIgnoreCase);
            var schema = r.Text("event_schema") ?? "";
            var name = r.Text("event_name") ?? "";
            var last = r.Date("last_executed");
            return new ScheduledJob
            {
                Id = schema + "." + name,
                Name = name,
                Engine = JobEngine.MySqlEvents,
                Database = schema,
                Enabled = enabled,
                ScheduleText = JobScheduleText.MySqlScheduleText(type, at, value, field, starts, ends),
                ScheduleDescription = JobScheduleText.DescribeMySql(type, at, value, field, starts, ends),
                Schedule = JobScheduleText.FromMySql(type, at, value, field, starts, ends),
                Owner = r.Text("definer"),
                Description = r.Text("event_comment") is { Length: > 0 } c ? c : null,
                Category = status.Equals("SLAVESIDE_DISABLED", StringComparison.OrdinalIgnoreCase) || status.Equals("REPLICA_SIDE_DISABLED", StringComparison.OrdinalIgnoreCase)
                    ? "disabled on this replica" : null,
                Command = r.Text("body") ?? "",
                LastRunAt = last,
                // Events keep no outcome: LAST_EXECUTED is when the last run started.
                LastOutcome = last is null ? null : JobOutcome.Unknown,
                NextRunAt = enabled && now is { } n ? JobScheduleText.NextMySql(type, at, value, field, starts, ends, n) : null,
                Preserve = string.Equals(r.Text("on_completion"), "PRESERVE", StringComparison.OrdinalIgnoreCase)
            };
        }).ToList();

        return new JobCatalog
        {
            Engine = JobEngine.MySqlEvents,
            Source = "Events · event scheduler " + (running ? "ON" : scheduler is "" ? "unknown" : scheduler),
            Setup = scheduler switch
            {
                "ON" or "1" or "" => null,
                "DISABLED" => "The event scheduler is DISABLED in the server's startup options, so events never run. Set " +
                              "event_scheduler=ON in my.cnf / my.ini and restart the server.",
                _ => "The event scheduler is OFF, so events do not run. Turn it on below (SET GLOBAL event_scheduler = ON, which needs " +
                     "SYSTEM_VARIABLES_ADMIN or SUPER and lasts until the server restarts; add event_scheduler=ON to my.cnf to keep it)."
            },
            SchedulerCanBeStarted = scheduler is "OFF" or "0",
            ServerNow = now,
            Version = probe.Text("version"),
            Jobs = list,
            Warnings = warnings
        };
    }

    // ---------- history ----------

    /// <summary>The selected job's runs, newest first. Empty for MySQL, which keeps none.</summary>
    public static async Task<IReadOnlyList<JobRun>> LoadHistoryAsync(
        IDatabaseProvider provider, JobCatalog catalog, ScheduledJob job, int max = 200, CancellationToken ct = default)
    {
        switch (catalog.Engine)
        {
            case JobEngine.SqlServerAgent when Guid.TryParse(job.Id, out var id):
            {
                var rows = await provider.QueryReadOnlyAsync(JobQueries.SqlServerHistory(id, max), null, Options, max, ct);
                return JobRows.Of(rows).Select(r =>
                {
                    var step = r.Int("step_id") ?? 0;
                    return new JobRun(
                        JobScheduleText.AgentDateTime(r.Int("run_date") ?? 0, r.Int("run_time") ?? 0),
                        JobScheduleText.AgentDuration(r.Int("run_duration") ?? 0),
                        AgentOutcome(r.Int("run_status")), r.Text("message"), step, step == 0 ? "Job outcome" : r.Text("step_name"));
                }).ToList();
            }
            case JobEngine.PgCron when long.TryParse(job.Id, NumberStyles.None, Invariant, out var jobId):
            {
                var rows = await provider.QueryReadOnlyAsync(JobQueries.PostgresHistory(jobId, max), catalog.HomeDatabase, Options, max, ct);
                return JobRows.Of(rows).Select(r =>
                {
                    var start = ToLocal(r.Date("start_time"));
                    var end = ToLocal(r.Date("end_time"));
                    var status = r.Text("status");
                    return new JobRun(start, start is { } s && end is { } e && e >= s ? e - s : null,
                        status switch
                        {
                            "succeeded" => JobOutcome.Succeeded,
                            "failed" => JobOutcome.Failed,
                            "running" or "starting" or "sending" or "connecting" => JobOutcome.Running,
                            _ => JobOutcome.Unknown
                        },
                        r.Text("return_message"));
                }).ToList();
            }
            default:
                return [];
        }
    }

    private static async Task<QueryResultSet?> TryRead(
        IDatabaseProvider provider, string sql, string? database, string what, List<string> warnings, CancellationToken ct)
    {
        try
        {
            return await provider.QueryReadOnlyAsync(sql, database, Options, MaxRows, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            warnings.Add($"The server did not show the {what}: {ex.Message}");
            return null;
        }
    }
}

/// <summary>Reads catalog result rows by column name.</summary>
internal readonly record struct JobRows(Dictionary<string, int> Index, IReadOnlyList<object?> Values)
{
    public static IEnumerable<JobRows> Of(QueryResultSet? set)
    {
        if (set is null) yield break;
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < set.Columns.Count; i++) index.TryAdd(set.Columns[i], i);
        foreach (var values in set.Rows) yield return new JobRows(index, values);
    }

    private object? Get(string column) =>
        Index is not null && Index.TryGetValue(column, out var i) && i < Values.Count && Values[i] is not (null or DBNull) ? Values[i] : null;

    public string? Text(string column) => Get(column) switch
    {
        null => null,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        var v => Convert.ToString(v, CultureInfo.InvariantCulture)
    };

    public bool Bool(string column) => Get(column) switch
    {
        null => false,
        bool b => b,
        string s => s is "1" or "t" or "true" or "True" or "ON",
        var v => Convert.ToInt64(v, CultureInfo.InvariantCulture) != 0
    };

    public int? Int(string column) => Get(column) switch
    {
        null => null,
        string s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null,
        var v => Convert.ToInt32(v, CultureInfo.InvariantCulture)
    };

    public DateTime? Date(string column) => Get(column) switch
    {
        DateTime d => d,
        DateTimeOffset o => o.UtcDateTime,
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) => d,
        _ => null
    };
}
