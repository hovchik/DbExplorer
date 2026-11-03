using System.Globalization;
using System.Text;
using DbExplorer.Core.Abstractions;

namespace DbExplorer.Application.Jobs;

/// <summary>
/// The scripts behind the Jobs tab's buttons: create, change, enable or disable, delete, run now and stop. Each engine
/// uses its own API (msdb's sp_*_job procedures, pg_cron's functions, CREATE / ALTER / DROP EVENT), so the script is
/// the same one a DBA would type, and it is shown before it runs.
/// </summary>
public static class JobScriptBuilder
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Why the draft cannot be saved, or null.</summary>
    public static string? Problem(JobCatalog catalog, JobDraft draft, ScheduledJob? existing)
    {
        if (string.IsNullOrWhiteSpace(draft.Name)) return "Give the job a name.";
        if (draft.Name.Trim().Length > 128) return "Names are at most 128 characters.";
        var caps = catalog.Capabilities;
        if (existing is null || caps.CanRename && !string.Equals(existing.Name, draft.Name.Trim(), StringComparison.Ordinal))
        {
            var clash = catalog.Jobs.FirstOrDefault(j => !ReferenceEquals(j, existing) && j.Name.Equals(draft.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                && (catalog.Engine != JobEngine.MySqlEvents || string.Equals(j.Database, draft.Database, StringComparison.OrdinalIgnoreCase)));
            if (clash is not null)
                return catalog.Engine == JobEngine.PgCron
                    ? $"A job named {clash.Name} exists; pg_cron would silently replace it. Pick another name."
                    : $"A job named {clash.Name} exists already.";
        }
        if ((existing is null || existing.CommandEditable) && string.IsNullOrWhiteSpace(draft.Command)) return "Type the SQL the job runs.";
        if (catalog.Engine == JobEngine.MySqlEvents && string.IsNullOrWhiteSpace(draft.Database)) return "Pick the database (schema) the event belongs to.";
        if (existing is null && draft.Schedule is null) return "Pick a schedule.";
        return draft.Schedule?.Problem(catalog.Engine);
    }

    public static JobScript Create(JobCatalog catalog, JobDraft draft, DateTime now) => catalog.Engine switch
    {
        JobEngine.SqlServerAgent => SqlServerCreate(draft, now),
        JobEngine.PgCron => PgCronCreate(catalog, draft),
        _ => MySqlCreate(draft, now)
    };

    /// <summary>A script that would create the existing job again (all steps and schedules on SQL Server), to copy or keep.</summary>
    public static string ScriptAsCreate(JobCatalog catalog, ScheduledJob job, DateTime now)
    {
        var draft = DraftOf(job);
        if (catalog.Engine != JobEngine.SqlServerAgent)
        {
            var schedule = draft.Schedule ?? (catalog.Engine == JobEngine.PgCron ? JobScheduleText.FromCron(job.ScheduleText) : null);
            if (catalog.Engine == JobEngine.PgCron) return PgCronCreate(catalog, draft with { Schedule = schedule }).Sql;
            if (schedule is null)
                return $"CREATE EVENT {MyName(job.Database!, job.Name)}\n  ON SCHEDULE {job.ScheduleText}\n  ON COMPLETION {(job.Preserve ? "" : "NOT ")}PRESERVE\n  " +
                       (job.Enabled ? "ENABLE" : "DISABLE") + (job.Description is { } c ? "\n  COMMENT " + MyText(c) : "") + "\nDO " + MyBody(job.Command) + ";";
            return MySqlCreate(draft with { Schedule = schedule }, now).Sql;
        }

        var sb = new StringBuilder();
        sb.AppendLine("DECLARE @job_id uniqueidentifier;");
        sb.AppendLine($"EXEC msdb.dbo.sp_add_job @job_name = {N(job.Name)}, @enabled = {Bit(job.Enabled)}" +
                      (job.Description is { } d ? $", @description = {N(d)}" : "") +
                      (job.Category is { Length: > 0 } cat && cat != "[Uncategorized (Local)]" ? $", @category_name = {N(cat)}" : "") +
                      ", @job_id = @job_id OUTPUT;");
        foreach (var step in job.Steps)
            sb.AppendLine($"EXEC msdb.dbo.sp_add_jobstep @job_id = @job_id, @step_name = {N(step.Name)}, @subsystem = {N(step.Subsystem)}" +
                          (step.Database is { Length: > 0 } db ? $", @database_name = {N(db)}" : "") +
                          $", @command = {N(step.Command)}, @on_success_action = {ActionCode(step.OnSuccess, 3, step == job.Steps[^1])}" +
                          $", @on_fail_action = {ActionCode(step.OnFailure, 2, true)};");
        foreach (var schedule in job.AgentSchedules) sb.AppendLine(AddSchedule("@job_id", schedule));
        sb.AppendLine("EXEC msdb.dbo.sp_add_jobserver @job_id = @job_id, @server_name = N'(local)';");
        return sb.ToString().TrimEnd();
    }

    private static int ActionCode(string? action, int fallback, bool last) => action switch
    {
        "quit with success" => 1,
        "quit with failure" => 2,
        "go to the next step" => 3,
        _ => last && fallback == 3 ? 1 : fallback
    };

    /// <summary>The form's starting values for an existing job.</summary>
    public static JobDraft DraftOf(ScheduledJob job) => new()
    {
        Name = job.Name, Database = job.Database, Enabled = job.Enabled, Description = job.Description, Command = job.Command,
        Schedule = job.Schedule, Preserve = job.Preserve
    };

    /// <summary>The changes from <paramref name="job"/> to <paramref name="draft"/>; an empty script when nothing changed.
    /// A null <see cref="JobDraft.Schedule"/> keeps the schedule.</summary>
    public static JobScript Update(JobCatalog catalog, ScheduledJob job, JobDraft draft, DateTime now) => catalog.Engine switch
    {
        JobEngine.SqlServerAgent => SqlServerUpdate(job, draft, now),
        JobEngine.PgCron => PgCronUpdate(catalog, job, draft),
        _ => MySqlUpdate(job, draft, now)
    };

    public static JobScript SetEnabled(JobCatalog catalog, ScheduledJob job, bool enabled)
    {
        var verb = enabled ? "Enable" : "Disable";
        return catalog.Engine switch
        {
            JobEngine.SqlServerAgent => new($"EXEC msdb.dbo.sp_update_job @job_id = {N(job.Id)}, @enabled = {Bit(enabled)};", null, false, $"{verb} {job.Name}"),
            JobEngine.PgCron => new($"SELECT cron.alter_job(job_id := {PgId(job)}, active := {(enabled ? "true" : "false")});", catalog.HomeDatabase, true, $"{verb} {job.Name}"),
            _ => new($"ALTER EVENT {MyName(job.Database!, job.Name)} {(enabled ? "ENABLE" : "DISABLE")};", job.Database, false, $"{verb} {job.Name}")
        };
    }

    public static JobScript Delete(JobCatalog catalog, ScheduledJob job) => catalog.Engine switch
    {
        JobEngine.SqlServerAgent => new($"EXEC msdb.dbo.sp_delete_job @job_id = {N(job.Id)}, @delete_unused_schedule = 1;", null, false, $"Delete {job.Name}"),
        JobEngine.PgCron => new($"SELECT cron.unschedule({PgId(job)});", catalog.HomeDatabase, true, $"Delete {job.Name}"),
        _ => new($"DROP EVENT {MyName(job.Database!, job.Name)};", job.Database, false, $"Delete {job.Name}")
    };

    /// <summary>SQL Server only: starts the job now (at its first step) without waiting for it.</summary>
    public static JobScript RunNow(ScheduledJob job) =>
        new($"EXEC msdb.dbo.sp_start_job @job_id = {N(job.Id)};", null, false, $"Run {job.Name} now");

    public static JobScript Stop(ScheduledJob job) =>
        new($"EXEC msdb.dbo.sp_stop_job @job_id = {N(job.Id)};", null, false, $"Stop {job.Name}");

    /// <summary>MySQL: turns the event scheduler on until the server restarts.</summary>
    public static JobScript StartEventScheduler() => new("SET GLOBAL event_scheduler = ON;", null, false, "Turn the event scheduler on");

    /// <summary>What a Query tab gets for "Open in Query tab": the command (every step, on SQL Server) to run it by hand.</summary>
    public static string CommandForQuery(ScheduledJob job)
    {
        if (job.Engine != JobEngine.SqlServerAgent || job.Steps.Count <= 1)
            return job.Engine == JobEngine.SqlServerAgent && job.Steps is [{ IsTransactSql: false } only]
                ? $"-- {only.Subsystem} step, not T-SQL: it runs in SQL Server Agent, not here.\n/*\n{only.Command}\n*/"
                : job.Command;
        var sb = new StringBuilder();
        foreach (var step in job.Steps)
        {
            sb.AppendLine($"-- Step {step.Id}: {step.Name}" + (step.Database is { Length: > 0 } db ? $" (in {db})" : "") +
                          (step.IsTransactSql ? "" : $" · {step.Subsystem}, not T-SQL"));
            if (step.Database is { Length: > 0 } use && step.IsTransactSql) sb.AppendLine($"USE {Bracket(use)};");
            sb.AppendLine(step.IsTransactSql ? step.Command : "/*\n" + step.Command + "\n*/");
            sb.AppendLine("GO");
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    // ---------- SQL Server Agent ----------

    private static JobScript SqlServerCreate(JobDraft draft, DateTime now)
    {
        var name = draft.Name.Trim();
        var sb = new StringBuilder();
        sb.AppendLine("DECLARE @job_id uniqueidentifier;");
        sb.AppendLine($"EXEC msdb.dbo.sp_add_job @job_name = {N(name)}, @enabled = {Bit(draft.Enabled)}" +
                      (draft.Description is { Length: > 0 } d ? $", @description = {N(d)}" : "") + ", @job_id = @job_id OUTPUT;");
        sb.AppendLine($"EXEC msdb.dbo.sp_add_jobstep @job_id = @job_id, @step_name = N'Run', @subsystem = N'TSQL', " +
                      $"@database_name = {N(string.IsNullOrWhiteSpace(draft.Database) ? "master" : draft.Database!)}, @command = {N(draft.Command)};");
        sb.AppendLine(AddSchedule("@job_id", JobScheduleText.ToAgent(draft.Schedule!, name, now.Date)));
        sb.AppendLine("EXEC msdb.dbo.sp_add_jobserver @job_id = @job_id, @server_name = N'(local)';");
        return new JobScript(sb.ToString().TrimEnd(), null, true, $"Create job {name}");
    }

    private static string AddSchedule(string jobId, AgentSchedule s) =>
        $"EXEC msdb.dbo.sp_add_jobschedule @job_id = {jobId}, @name = {N(s.Name)}, @enabled = {Bit(s.Enabled)}, @freq_type = {s.FreqType}, " +
        $"@freq_interval = {s.FreqInterval}, @freq_subday_type = {s.FreqSubdayType}, @freq_subday_interval = {s.FreqSubdayInterval}, " +
        $"@freq_relative_interval = {s.FreqRelativeInterval}, @freq_recurrence_factor = {s.FreqRecurrenceFactor}, " +
        $"@active_start_date = {s.ActiveStartDate}, @active_end_date = {s.ActiveEndDate}, " +
        $"@active_start_time = {s.ActiveStartTime}, @active_end_time = {s.ActiveEndTime};";

    private static JobScript SqlServerUpdate(ScheduledJob job, JobDraft draft, DateTime now)
    {
        var name = draft.Name.Trim();
        var id = N(job.Id);
        var lines = new List<string>();
        var set = new List<string>();
        if (!string.Equals(name, job.Name, StringComparison.Ordinal)) set.Add($"@new_name = {N(name)}");
        if (!string.Equals(Empty(draft.Description), Empty(job.Description), StringComparison.Ordinal)) set.Add($"@description = {N(draft.Description ?? "")}");
        if (draft.Enabled != job.Enabled) set.Add($"@enabled = {Bit(draft.Enabled)}");
        if (set.Count > 0) lines.Add($"EXEC msdb.dbo.sp_update_job @job_id = {id}, {string.Join(", ", set)};");

        if (job.CommandEditable)
        {
            var database = string.IsNullOrWhiteSpace(draft.Database) ? "master" : draft.Database!;
            if (job.Steps.Count == 0)
                lines.Add($"EXEC msdb.dbo.sp_add_jobstep @job_id = {id}, @step_name = N'Run', @subsystem = N'TSQL', @database_name = {N(database)}, @command = {N(draft.Command)};");
            else
            {
                var step = new List<string>();
                if (!string.Equals(draft.Command, job.Command, StringComparison.Ordinal)) step.Add($"@command = {N(draft.Command)}");
                if (!string.Equals(database, job.Steps[0].Database ?? "master", StringComparison.OrdinalIgnoreCase)) step.Add($"@database_name = {N(database)}");
                if (step.Count > 0) lines.Add($"EXEC msdb.dbo.sp_update_jobstep @job_id = {id}, @step_id = {job.Steps[0].Id}, {string.Join(", ", step)};");
            }
        }

        if (draft.Schedule is { } schedule && !schedule.Matches(job.Schedule))
        {
            // The form edits one schedule: the job's schedules make way for the new one (deleted when no other job uses them).
            foreach (var old in job.AgentSchedules)
                lines.Add($"EXEC msdb.dbo.sp_detach_schedule @job_id = {id}, @schedule_id = {old.Id}, @delete_unused_schedule = 1;");
            lines.Add(AddSchedule(id, JobScheduleText.ToAgent(schedule, name, now.Date)));
        }
        return new JobScript(string.Join("\n", lines), null, true, $"Change job {job.Name}");
    }

    private static string N(string text) => "N'" + text.Replace("'", "''") + "'";

    private static string Bit(bool value) => value ? "1" : "0";

    private static string Bracket(string name) => "[" + name.Replace("]", "]]") + "]";

    private static string Empty(string? text) => string.IsNullOrWhiteSpace(text) ? "" : text;

    // ---------- pg_cron ----------

    private static JobScript PgCronCreate(JobCatalog catalog, JobDraft draft)
    {
        var name = draft.Name.Trim();
        var schedule = JobScheduleText.ToCron(draft.Schedule!);
        var database = string.IsNullOrWhiteSpace(draft.Database) ? catalog.HomeDatabase : draft.Database;
        string sql;
        if (SupportsScheduleInDatabase(catalog.Version))
            sql = $"SELECT cron.schedule_in_database({PgText(name)}, {PgText(schedule)}, {Dollar(draft.Command)}, {PgText(database ?? "postgres")}, " +
                  $"active := {(draft.Enabled ? "true" : "false")});";
        else
        {
            sql = $"SELECT cron.schedule({PgText(name)}, {PgText(schedule)}, {Dollar(draft.Command)});";
            if (!draft.Enabled) sql = $"SELECT cron.alter_job(cron.schedule({PgText(name)}, {PgText(schedule)}, {Dollar(draft.Command)}), active := false);";
        }
        return new JobScript(sql, catalog.HomeDatabase, true, $"Create job {name}");
    }

    private static JobScript PgCronUpdate(JobCatalog catalog, ScheduledJob job, JobDraft draft)
    {
        var args = new List<string>();
        if (draft.Schedule is { } spec)
        {
            var schedule = JobScheduleText.ToCron(spec);
            if (!string.Equals(schedule, job.ScheduleText.Trim(), StringComparison.Ordinal)) args.Add($"schedule := {PgText(schedule)}");
        }
        if (!string.Equals(draft.Command, job.Command, StringComparison.Ordinal)) args.Add($"command := {Dollar(draft.Command)}");
        if (!string.IsNullOrWhiteSpace(draft.Database) && !string.Equals(draft.Database, job.Database, StringComparison.Ordinal))
            args.Add($"database := {PgText(draft.Database!)}");
        if (draft.Enabled != job.Enabled) args.Add($"active := {(draft.Enabled ? "true" : "false")}");
        var sql = args.Count == 0 ? "" : $"SELECT cron.alter_job(job_id := {PgId(job)}, {string.Join(", ", args)});";
        return new JobScript(sql, catalog.HomeDatabase, true, $"Change job {job.Name}");
    }

    private static bool SupportsScheduleInDatabase(string? version) =>
        Version.TryParse(version is { } v && !v.Contains('.') ? v + ".0" : version, out var parsed) && parsed >= new Version(1, 4);

    private static string PgId(ScheduledJob job) =>
        long.TryParse(job.Id, NumberStyles.None, Invariant, out var id) ? id.ToString(Invariant) : throw new ArgumentException("Not a pg_cron job id: " + job.Id);

    private static string PgText(string text) => "'" + text.Replace("'", "''") + "'";

    /// <summary>Dollar-quoted, so the command reads as typed; the tag is one the command does not contain.</summary>
    public static string Dollar(string text)
    {
        foreach (var tag in new[] { "$cmd$", "$job$", "$sql$" })
            if (!text.Contains(tag, StringComparison.Ordinal)) return tag + text + tag;
        for (var i = 1; ; i++)
        {
            var tag = $"$c{i}$";
            if (!text.Contains(tag, StringComparison.Ordinal)) return tag + text + tag;
        }
    }

    // ---------- MySQL events ----------

    private static JobScript MySqlCreate(JobDraft draft, DateTime now)
    {
        var name = draft.Name.Trim();
        var schedule = JobScheduleText.ToMySql(draft.Schedule!, now);
        var sb = new StringBuilder();
        sb.Append($"CREATE EVENT {MyName(draft.Database!, name)}\n  ON SCHEDULE {schedule}\n  ON COMPLETION {(draft.Preserve ? "" : "NOT ")}PRESERVE\n  ");
        sb.Append(draft.Enabled ? "ENABLE" : "DISABLE");
        if (draft.Description is { Length: > 0 } c) sb.Append("\n  COMMENT ").Append(MyText(c));
        sb.Append("\nDO ").Append(MyBody(draft.Command)).Append(';');
        return new JobScript(sb.ToString(), draft.Database, false, $"Create event {name}");
    }

    private static JobScript MySqlUpdate(ScheduledJob job, JobDraft draft, DateTime now)
    {
        var name = draft.Name.Trim();
        var clauses = new List<string>();
        if (draft.Schedule is { } spec && !spec.Matches(job.Schedule)) clauses.Add("ON SCHEDULE " + JobScheduleText.ToMySql(spec, now));
        if (draft.Preserve != job.Preserve) clauses.Add($"ON COMPLETION {(draft.Preserve ? "" : "NOT ")}PRESERVE");
        var schema = string.IsNullOrWhiteSpace(draft.Database) ? job.Database! : draft.Database!;
        if (!string.Equals(name, job.Name, StringComparison.Ordinal) || !string.Equals(schema, job.Database, StringComparison.Ordinal))
            clauses.Add("RENAME TO " + MyName(schema, name));
        if (draft.Enabled != job.Enabled) clauses.Add(draft.Enabled ? "ENABLE" : "DISABLE");
        if (!string.Equals(Empty(draft.Description), Empty(job.Description), StringComparison.Ordinal)) clauses.Add("COMMENT " + MyText(draft.Description ?? ""));
        if (!string.Equals(draft.Command.Trim().TrimEnd(';').Trim(), job.Command.Trim().TrimEnd(';').Trim(), StringComparison.Ordinal))
            clauses.Add("DO " + MyBody(draft.Command));
        var sql = clauses.Count == 0 ? "" : $"ALTER EVENT {MyName(job.Database!, job.Name)}\n  " + string.Join("\n  ", clauses) + ";";
        return new JobScript(sql, job.Database, false, $"Change event {job.Name}");
    }

    private static string MyName(string schema, string name) => Tick(schema) + "." + Tick(name);

    private static string Tick(string name) => "`" + name.Replace("`", "``") + "`";

    private static string MyText(string text) => "'" + text.Replace("\\", "\\\\").Replace("'", "''") + "'";

    /// <summary>An event runs one statement; several are wrapped in BEGIN … END (a body that already is one stays as typed).</summary>
    public static string MyBody(string command)
    {
        var body = command.Trim();
        while (body.EndsWith(';')) body = body[..^1].TrimEnd();
        if (body.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase) && (body.Length == 5 || !char.IsLetterOrDigit(body[5]))) return body;
        if (!body.Contains(';')) return body;
        var inner = string.Join("\n", body.Split('\n').Select(l => "  " + l.TrimEnd()));
        return "BEGIN\n" + inner + ";\nEND";
    }

    /// <summary>Runs a job script: in one transaction where the engine allows (SQL Server's msdb procedures, pg_cron's
    /// functions), as is for MySQL, whose event DDL commits by itself, and for Run now / Stop.</summary>
    public static async Task RunAsync(IDatabaseProvider provider, JobScript script, int timeoutSeconds, CancellationToken ct = default)
    {
        if (script.IsEmpty) return;
        await using var run = await provider.BeginScriptSessionAsync(script.Database, script.Transactional, ct);
        await run.ExecuteAsync(script.Sql, timeoutSeconds, ct);
        if (script.Transactional) await run.CommitAsync(ct);
    }
}
