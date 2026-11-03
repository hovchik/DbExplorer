namespace DbExplorer.Application.Jobs;

/// <summary>Where an engine keeps its scheduled jobs.</summary>
public enum JobEngine
{
    /// <summary>SQL Server Agent jobs in msdb: steps, schedules and run history.</summary>
    SqlServerAgent,

    /// <summary>The pg_cron extension (cron.job and cron.job_run_details) on PostgreSQL.</summary>
    PgCron,

    /// <summary>MySQL / MariaDB events (information_schema.EVENTS) run by the event scheduler.</summary>
    MySqlEvents
}

public enum JobOutcome
{
    Succeeded,
    Failed,
    Running,
    Cancelled,
    Retrying,
    Unknown
}

/// <summary>What the Jobs tab can do with one engine's jobs.</summary>
public sealed record JobCapabilities(
    bool CanRunNow, bool CanStop, bool HasHistory, bool HasSteps, bool CanRename, bool HasDescription, bool CanRunOnce)
{
    public static JobCapabilities For(JobEngine engine) => engine switch
    {
        JobEngine.SqlServerAgent => new(CanRunNow: true, CanStop: true, HasHistory: true, HasSteps: true, CanRename: true, HasDescription: true, CanRunOnce: true),
        // pg_cron has no "run now"; alter_job cannot rename.
        JobEngine.PgCron => new(CanRunNow: false, CanStop: false, HasHistory: true, HasSteps: false, CanRename: false, HasDescription: false, CanRunOnce: false),
        // Events keep only LAST_EXECUTED, and cannot be started by hand.
        _ => new(CanRunNow: false, CanStop: false, HasHistory: false, HasSteps: false, CanRename: true, HasDescription: true, CanRunOnce: true)
    };
}

/// <summary>One step of a SQL Server Agent job. pg_cron jobs and MySQL events have a single command instead.</summary>
public sealed record JobStep(int Id, string Name, string Subsystem, string? Database, string Command, string? OnSuccess = null, string? OnFailure = null)
{
    public bool IsTransactSql => string.Equals(Subsystem, "TSQL", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One SQL Server Agent schedule attached to a job, in msdb's own encoding (freq_type and friends).</summary>
public sealed record AgentSchedule
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public int FreqType { get; init; }
    public int FreqInterval { get; init; }
    public int FreqSubdayType { get; init; }
    public int FreqSubdayInterval { get; init; }
    public int FreqRelativeInterval { get; init; }
    public int FreqRecurrenceFactor { get; init; }
    public int ActiveStartDate { get; init; }
    public int ActiveEndDate { get; init; } = 99991231;
    public int ActiveStartTime { get; init; }
    public int ActiveEndTime { get; init; } = 235959;
    public DateTime? NextRun { get; init; }

    public string Description => JobScheduleText.DescribeAgent(this);
}

/// <summary>A scheduled job of any engine, in the shape the Jobs tab shows.</summary>
public sealed record ScheduledJob
{
    /// <summary>SQL Server: job_id; pg_cron: jobid; MySQL: schema.name.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required JobEngine Engine { get; init; }

    /// <summary>pg_cron: the database the command runs in; MySQL: the event's schema; SQL Server: the first step's database.</summary>
    public string? Database { get; init; }
    public bool Enabled { get; init; }

    /// <summary>The schedule as the engine stores it: a cron expression, "EVERY 1 DAY STARTS …", or the Agent schedules.</summary>
    public string ScheduleText { get; init; } = "";

    /// <summary>The schedule in words.</summary>
    public string ScheduleDescription { get; init; } = "";

    /// <summary>What a new edit form starts from; null when the stored schedule has no form equivalent (it stays as it is).</summary>
    public JobScheduleSpec? Schedule { get; init; }
    public IReadOnlyList<AgentSchedule> AgentSchedules { get; init; } = [];
    public string? Owner { get; init; }
    public string? Description { get; init; }
    public string? Category { get; init; }

    /// <summary>pg_cron command, MySQL event body, or the only step's command of an Agent job.</summary>
    public string Command { get; init; } = "";
    public IReadOnlyList<JobStep> Steps { get; init; } = [];
    public DateTime? LastRunAt { get; init; }
    public JobOutcome? LastOutcome { get; init; }
    public TimeSpan? LastDuration { get; init; }
    public string? LastMessage { get; init; }
    public DateTime? NextRunAt { get; init; }
    public bool IsRunning { get; init; }
    public DateTime? RunningSince { get; init; }

    /// <summary>MySQL: ON COMPLETION PRESERVE (a one-time event is kept after it ran).</summary>
    public bool Preserve { get; init; }

    /// <summary>Whether the form can edit the command: always for pg_cron and events; on SQL Server only for a job with
    /// no steps or a single T-SQL step (other jobs are changed step by step in a Query tab).</summary>
    public bool CommandEditable => Engine != JobEngine.SqlServerAgent || Steps is [{ IsTransactSql: true }] or [];
}

/// <summary>One run (SQL Server: one job outcome or step) from the engine's history.</summary>
public sealed record JobRun(DateTime? StartedAt, TimeSpan? Duration, JobOutcome Outcome, string? Message, int? StepId = null, string? StepName = null);

/// <summary>Everything the Jobs tab read from a server.</summary>
public sealed record JobCatalog
{
    public required JobEngine Engine { get; init; }

    /// <summary>Jobs can be listed (Agent present, pg_cron installed). When false, <see cref="Setup"/> says what to do.</summary>
    public bool Available { get; init; } = true;

    /// <summary>A short line for the toolbar: "SQL Server Agent · running", "pg_cron 1.6 in postgres", "event scheduler ON".</summary>
    public string Source { get; init; } = "";

    /// <summary>Setup guidance or a warning that jobs will not run (Agent stopped, event scheduler OFF).</summary>
    public string? Setup { get; init; }

    /// <summary>MySQL only: the event scheduler is OFF and SET GLOBAL event_scheduler = ON would start it.</summary>
    public bool SchedulerCanBeStarted { get; init; }

    /// <summary>pg_cron: the database cron.job lives in (scripts run there). Null on the other engines.</summary>
    public string? HomeDatabase { get; init; }

    /// <summary>pg_cron: cron.timezone, which the cron expressions are read in.</summary>
    public string? TimeZone { get; init; }

    /// <summary>The server's clock when the catalog was read, for next-run times.</summary>
    public DateTime? ServerNow { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<ScheduledJob> Jobs { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public JobCapabilities Capabilities => JobCapabilities.For(Engine);
}

/// <summary>What the edit form produces: a new job, or the changed fields of an existing one.</summary>
public sealed record JobDraft
{
    public string Name { get; init; } = "";
    public string? Database { get; init; }
    public bool Enabled { get; init; } = true;
    public string? Description { get; init; }
    public string Command { get; init; } = "";

    /// <summary>Null keeps the existing job's schedule.</summary>
    public JobScheduleSpec? Schedule { get; init; }

    /// <summary>MySQL: keep a one-time event after it ran (ON COMPLETION PRESERVE).</summary>
    public bool Preserve { get; init; } = true;
}

/// <summary>A script the Jobs tab runs, with where it runs.</summary>
public sealed record JobScript(string Sql, string? Database, bool Transactional, string Summary)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Sql);
}
