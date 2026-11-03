namespace DbExplorer.Application.Jobs;

/// <summary>Catalog queries behind the Jobs tab. They only read (msdb tables, cron.job, information_schema.EVENTS).</summary>
internal static class JobQueries
{
    // ---------- SQL Server Agent ----------

    /// <summary>Edition (4 Express has no Agent, 5 Azure SQL Database has none either), the clock, and whether the
    /// Agent is connected; sys.dm_exec_sessions shows other sessions only with VIEW SERVER STATE.</summary>
    public const string SqlServerProbe = """
        SELECT CAST(SERVERPROPERTY('EngineEdition') AS int) AS edition,
               CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)) AS version,
               GETDATE() AS server_now,
               HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') AS can_view_state,
               (SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE program_name LIKE N'SQLAgent - %') AS agent_sessions,
               IS_SRVROLEMEMBER('sysadmin') AS is_sysadmin
        """;

    public const string SqlServerJobs = """
        SELECT CONVERT(nvarchar(36), j.job_id) AS job_id, j.name, j.enabled, j.description, c.name AS category,
               SUSER_SNAME(j.owner_sid) AS owner,
               (SELECT TOP (1) s.database_name FROM msdb.dbo.sysjobsteps s WHERE s.job_id = j.job_id ORDER BY s.step_id) AS database_name,
               h.run_date, h.run_time, h.run_duration, h.run_status, h.message,
               CASE WHEN a.start_execution_date IS NOT NULL AND a.stop_execution_date IS NULL THEN a.start_execution_date END AS running_since,
               a.next_scheduled_run_date
        FROM msdb.dbo.sysjobs j
        LEFT JOIN msdb.dbo.syscategories c ON c.category_id = j.category_id
        OUTER APPLY (SELECT TOP (1) x.run_date, x.run_time, x.run_duration, x.run_status, x.message
                     FROM msdb.dbo.sysjobhistory x WHERE x.job_id = j.job_id AND x.step_id = 0 ORDER BY x.instance_id DESC) h
        OUTER APPLY (SELECT TOP (1) ja.start_execution_date, ja.stop_execution_date, ja.next_scheduled_run_date
                     FROM msdb.dbo.sysjobactivity ja
                     WHERE ja.job_id = j.job_id AND ja.session_id = (SELECT MAX(ss.session_id) FROM msdb.dbo.syssessions ss)
                     ORDER BY ja.run_requested_date DESC) a
        ORDER BY j.name
        """;

    public const string SqlServerSteps = """
        SELECT CONVERT(nvarchar(36), s.job_id) AS job_id, s.step_id, s.step_name, s.subsystem, s.database_name, s.command,
               s.on_success_action, s.on_fail_action
        FROM msdb.dbo.sysjobsteps s
        ORDER BY s.job_id, s.step_id
        """;

    public const string SqlServerSchedules = """
        SELECT CONVERT(nvarchar(36), js.job_id) AS job_id, s.schedule_id, s.name, s.enabled, s.freq_type, s.freq_interval,
               s.freq_subday_type, s.freq_subday_interval, s.freq_relative_interval, s.freq_recurrence_factor,
               s.active_start_date, s.active_end_date, s.active_start_time, s.active_end_time, js.next_run_date, js.next_run_time
        FROM msdb.dbo.sysjobschedules js
        JOIN msdb.dbo.sysschedules s ON s.schedule_id = js.schedule_id
        ORDER BY js.job_id, s.schedule_id
        """;

    /// <summary>Newest first: each run's job outcome (step 0) and its steps.</summary>
    public static string SqlServerHistory(Guid jobId, int max) => $"""
        SELECT TOP ({max}) h.step_id, h.step_name, h.run_status, h.run_date, h.run_time, h.run_duration, h.message
        FROM msdb.dbo.sysjobhistory h
        WHERE h.job_id = CONVERT(uniqueidentifier, N'{jobId:D}')
        ORDER BY h.instance_id DESC
        """;

    // ---------- pg_cron ----------

    /// <summary>cron.database_name and cron.timezone exist only when pg_cron is in shared_preload_libraries.</summary>
    public const string PostgresProbe = """
        SELECT (SELECT e.extversion FROM pg_catalog.pg_extension e WHERE e.extname = 'pg_cron') AS version,
               current_setting('cron.database_name', true) AS home,
               current_setting('cron.timezone', true) AS time_zone,
               current_setting('shared_preload_libraries', true) AS preload,
               current_database() AS current_db,
               current_user AS current_user_name,
               (SELECT r.rolsuper FROM pg_catalog.pg_roles r WHERE r.rolname = current_user) AS is_superuser,
               now() AS server_now
        """;

    public const string PostgresJobs = """
        SELECT j.*, r.status AS last_status, r.return_message AS last_message, r.start_time AS last_start, r.end_time AS last_end
        FROM cron.job j
        LEFT JOIN LATERAL (SELECT d.status, d.return_message, d.start_time, d.end_time
                           FROM cron.job_run_details d WHERE d.jobid = j.jobid ORDER BY d.runid DESC LIMIT 1) r ON true
        ORDER BY j.jobid
        """;

    /// <summary>pg_cron before 1.4 has no cron.job_run_details.</summary>
    public const string PostgresJobsWithoutHistory = "SELECT j.* FROM cron.job j ORDER BY j.jobid";

    public static string PostgresHistory(long jobId, int max) => $"""
        SELECT d.runid, d.status, d.return_message, d.start_time, d.end_time
        FROM cron.job_run_details d
        WHERE d.jobid = {jobId}
        ORDER BY d.runid DESC
        LIMIT {max}
        """;

    // ---------- MySQL / MariaDB events ----------

    public const string MySqlProbe = "SELECT @@global.event_scheduler AS scheduler, NOW() AS server_now, VERSION() AS version, CURRENT_USER() AS current_user_name";

    public const string MySqlEvents = """
        SELECT EVENT_SCHEMA AS event_schema, EVENT_NAME AS event_name, DEFINER AS definer, EVENT_DEFINITION AS body,
               EVENT_TYPE AS event_type, EXECUTE_AT AS execute_at, CAST(INTERVAL_VALUE AS CHAR) AS interval_value,
               INTERVAL_FIELD AS interval_field, STARTS AS starts, ENDS AS ends, STATUS AS status, ON_COMPLETION AS on_completion,
               LAST_EXECUTED AS last_executed, EVENT_COMMENT AS event_comment
        FROM information_schema.EVENTS
        ORDER BY EVENT_SCHEMA, EVENT_NAME
        """;
}
