using System.Globalization;

namespace DbExplorer.Application.Jobs;

public enum ScheduleKind
{
    EveryMinutes,
    EveryHours,
    Daily,
    Weekly,
    Monthly,
    Once,

    /// <summary>A cron expression typed as is (pg_cron only).</summary>
    Cron
}

/// <summary>
/// A schedule as the Jobs form edits it. Each engine stores it its own way (a cron expression, an event's EVERY / AT
/// clause, an Agent schedule); <see cref="JobScheduleText"/> converts both ways and says when an engine cannot
/// express it.
/// </summary>
public sealed record JobScheduleSpec
{
    public ScheduleKind Kind { get; init; } = ScheduleKind.Daily;

    /// <summary>Every N minutes or hours.</summary>
    public int Interval { get; init; } = 1;

    /// <summary>Time of day (Daily, Weekly, Monthly); for EveryHours only the minute past the hour counts.</summary>
    public TimeOnly At { get; init; } = new(3, 0);
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [DayOfWeek.Monday];
    public int DayOfMonth { get; init; } = 1;
    public DateTime? OnceAt { get; init; }
    public string? Cron { get; init; }

    /// <summary>Whether both describe the same schedule (only the fields the kind uses count).</summary>
    public bool Matches(JobScheduleSpec? other)
    {
        if (other is null || other.Kind != Kind) return false;
        return Kind switch
        {
            ScheduleKind.EveryMinutes => Interval == other.Interval,
            ScheduleKind.EveryHours => Interval == other.Interval && At.Minute == other.At.Minute,
            ScheduleKind.Daily => At == other.At,
            ScheduleKind.Weekly => At == other.At && Days.ToHashSet().SetEquals(other.Days),
            ScheduleKind.Monthly => At == other.At && DayOfMonth == other.DayOfMonth,
            ScheduleKind.Once => OnceAt == other.OnceAt,
            _ => string.Equals(Cron?.Trim(), other.Cron?.Trim(), StringComparison.Ordinal)
        };
    }

    public string Describe() => Kind switch
    {
        ScheduleKind.EveryMinutes => Interval == 1 ? "Every minute" : $"Every {Interval} minutes",
        ScheduleKind.EveryHours => (Interval == 1 ? "Every hour" : $"Every {Interval} hours") + (At.Minute == 0 ? "" : $" at :{At.Minute:00}"),
        ScheduleKind.Daily => $"Daily at {At:HH\\:mm}",
        ScheduleKind.Weekly => $"{JobScheduleText.DayList(Days)} at {At:HH\\:mm}",
        ScheduleKind.Monthly => $"Monthly on day {DayOfMonth} at {At:HH\\:mm}",
        ScheduleKind.Once => OnceAt is { } at ? $"Once on {at:yyyy-MM-dd HH\\:mm}" : "Once",
        _ => CronSchedule.TryParse(Cron ?? "", out var cron, out _) ? cron!.Describe() : "Cron " + Cron
    };

    /// <summary>Why the engine cannot store this schedule, or null when it can.</summary>
    public string? Problem(JobEngine engine)
    {
        switch (Kind)
        {
            case ScheduleKind.EveryMinutes when Interval is < 1 or > 59:
                return "Every N minutes takes 1 to 59.";
            case ScheduleKind.EveryHours when Interval is < 1 or > 23:
                return "Every N hours takes 1 to 23.";
            case ScheduleKind.Weekly when Days.Count == 0:
                return "Pick at least one day.";
            case ScheduleKind.Monthly when DayOfMonth is < 1 or > 31:
                return "Day of month takes 1 to 31.";
            case ScheduleKind.Once when OnceAt is null:
                return "Give the date and time to run at, as yyyy-MM-dd HH:mm.";
        }
        return engine switch
        {
            JobEngine.PgCron when Kind == ScheduleKind.Once => "pg_cron has no one-time jobs: pick a repeating schedule.",
            JobEngine.PgCron when Kind == ScheduleKind.Cron => CronSchedule.TryParse(Cron ?? "", out _, out var error) ? null : error,
            JobEngine.PgCron when Kind == ScheduleKind.EveryMinutes && 60 % Interval != 0 =>
                $"Cron restarts the count every hour, so every {Interval} minutes would also run at :00. Use 1, 2, 3, 4, 5, 6, 10, 12, 15, 20 or 30.",
            JobEngine.PgCron when Kind == ScheduleKind.EveryHours && 24 % Interval != 0 =>
                $"Cron restarts the count every day, so every {Interval} hours would also run at midnight. Use 1, 2, 3, 4, 6, 8 or 12.",
            not JobEngine.PgCron when Kind == ScheduleKind.Cron => "Only pg_cron takes a cron expression.",
            JobEngine.MySqlEvents when Kind == ScheduleKind.Weekly && Days.Distinct().Count() > 1 =>
                "A MySQL event repeats every N weeks from one start, so it runs on one weekday: pick one day, or make one event per day.",
            _ => null
        };
    }
}

/// <summary>Converts schedules between the form and each engine's own format, and puts them into words.</summary>
public static class JobScheduleText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal static string DayList(IReadOnlyCollection<DayOfWeek> days)
    {
        var set = days.Distinct().OrderBy(d => ((int)d + 6) % 7).ToList();
        if (set.Count == 7) return "Every day";
        if (set.Count == 5 && !set.Contains(DayOfWeek.Saturday) && !set.Contains(DayOfWeek.Sunday)) return "Weekdays";
        if (set.Count == 2 && set.Contains(DayOfWeek.Saturday) && set.Contains(DayOfWeek.Sunday)) return "Weekends";
        return "Weekly on " + string.Join(", ", set.Select(d => Invariant.DateTimeFormat.GetAbbreviatedDayName(d)));
    }

    // ---------- pg_cron ----------

    /// <summary>The cron expression for a form schedule. Throws <see cref="ArgumentException"/> when pg_cron cannot express it.</summary>
    public static string ToCron(JobScheduleSpec spec)
    {
        if (spec.Problem(JobEngine.PgCron) is { } problem) throw new ArgumentException(problem);
        var m = spec.At.Minute.ToString(Invariant);
        var h = spec.At.Hour.ToString(Invariant);
        return spec.Kind switch
        {
            ScheduleKind.EveryMinutes => spec.Interval == 1 ? "* * * * *" : $"*/{spec.Interval} * * * *",
            ScheduleKind.EveryHours => spec.Interval == 1 ? $"{m} * * * *" : $"{m} */{spec.Interval} * * *",
            ScheduleKind.Daily => $"{m} {h} * * *",
            ScheduleKind.Weekly => $"{m} {h} * * {string.Join(",", spec.Days.Distinct().Select(d => (int)d).Order())}",
            ScheduleKind.Monthly => $"{m} {h} {spec.DayOfMonth} * *",
            _ => spec.Cron!.Trim()
        };
    }

    /// <summary>The form schedule a cron expression corresponds to; a Cron-kind spec (kept as typed) when no simpler one fits.</summary>
    public static JobScheduleSpec FromCron(string expression)
    {
        var raw = new JobScheduleSpec { Kind = ScheduleKind.Cron, Cron = expression.Trim() };
        if (!CronSchedule.TryParse(expression, out var cron, out _) || cron!.Seconds is not null) return raw;
        var f = cron.Fields;
        bool Star(int i) => f[i] == "*";
        bool Number(int i, out int value) => int.TryParse(f[i], NumberStyles.None, Invariant, out value);
        bool Step(int i, out int value)
        {
            value = 0;
            return f[i].StartsWith("*/", StringComparison.Ordinal) && int.TryParse(f[i][2..], NumberStyles.None, Invariant, out value);
        }

        if (!Star(3)) return raw;
        if (Star(2) && Star(4))
        {
            if (Star(0) && Star(1)) return new JobScheduleSpec { Kind = ScheduleKind.EveryMinutes, Interval = 1 };
            if (Step(0, out var everyMinutes) && Star(1) && everyMinutes is >= 1 and <= 59)
                return new JobScheduleSpec { Kind = ScheduleKind.EveryMinutes, Interval = everyMinutes };
            if (Number(0, out var minute) && minute < 60)
            {
                if (Star(1)) return new JobScheduleSpec { Kind = ScheduleKind.EveryHours, Interval = 1, At = new TimeOnly(0, minute) };
                if (Step(1, out var everyHours) && everyHours is >= 1 and <= 23)
                    return new JobScheduleSpec { Kind = ScheduleKind.EveryHours, Interval = everyHours, At = new TimeOnly(0, minute) };
                if (Number(1, out var hour) && hour < 24) return new JobScheduleSpec { Kind = ScheduleKind.Daily, At = new TimeOnly(hour, minute) };
            }
            return raw;
        }
        if (!Number(0, out var min) || min > 59 || !Number(1, out var hr) || hr > 23) return raw;
        if (Star(2) && cron.DaysOfWeek is { } days)
            return new JobScheduleSpec { Kind = ScheduleKind.Weekly, At = new TimeOnly(hr, min), Days = days };
        if (Star(4) && Number(2, out var dom) && dom is >= 1 and <= 31)
            return new JobScheduleSpec { Kind = ScheduleKind.Monthly, At = new TimeOnly(hr, min), DayOfMonth = dom };
        return raw;
    }

    // ---------- MySQL events ----------

    /// <summary>The ON SCHEDULE clause for a form schedule. Daily, weekly and monthly events start at the next such
    /// moment after <paramref name="now"/> (the server's clock), so they repeat at that time of day.</summary>
    public static string ToMySql(JobScheduleSpec spec, DateTime now)
    {
        if (spec.Problem(JobEngine.MySqlEvents) is { } problem) throw new ArgumentException(problem);
        string Ts(DateTime t) => "'" + t.ToString("yyyy-MM-dd HH:mm:ss", Invariant) + "'";
        DateTime NextTime(Func<DateTime, bool> dayMatches)
        {
            var day = now.Date;
            for (var i = 0; i < 400; i++, day = day.AddDays(1))
            {
                var at = day + spec.At.ToTimeSpan();
                if (at > now && dayMatches(at)) return at;
            }
            throw new ArgumentException("No such day in the next year.");
        }

        return spec.Kind switch
        {
            ScheduleKind.EveryMinutes => $"EVERY {spec.Interval} MINUTE",
            ScheduleKind.EveryHours => spec.At.Minute == 0
                ? $"EVERY {spec.Interval} HOUR"
                : $"EVERY {spec.Interval} HOUR STARTS {Ts(NextMinutePast(now, spec.At.Minute))}",
            ScheduleKind.Daily => $"EVERY 1 DAY STARTS {Ts(NextTime(_ => true))}",
            ScheduleKind.Weekly => $"EVERY 1 WEEK STARTS {Ts(NextTime(d => d.DayOfWeek == spec.Days[0]))}",
            ScheduleKind.Monthly when spec.DayOfMonth > 28 =>
                throw new ArgumentException("MySQL adds a month to the start date, so a day after the 28th drifts in shorter months. Pick 1 to 28."),
            ScheduleKind.Monthly => $"EVERY 1 MONTH STARTS {Ts(NextTime(d => d.Day == spec.DayOfMonth))}",
            _ => $"AT {Ts(spec.OnceAt!.Value)}"
        };
    }

    private static DateTime NextMinutePast(DateTime now, int minute)
    {
        var start = now.Date.AddHours(now.Hour).AddMinutes(minute);
        return start > now ? start : start.AddHours(1);
    }

    /// <summary>The form schedule of an event, or null when it has no form equivalent (compound intervals, ENDS, odd steps).</summary>
    public static JobScheduleSpec? FromMySql(string? eventType, DateTime? executeAt, string? intervalValue, string? intervalField, DateTime? starts, DateTime? ends)
    {
        if (string.Equals(eventType, "ONE TIME", StringComparison.OrdinalIgnoreCase))
            return executeAt is { } at ? new JobScheduleSpec { Kind = ScheduleKind.Once, OnceAt = at } : null;
        if (ends is not null || !int.TryParse(intervalValue?.Trim('\''), NumberStyles.None, Invariant, out var n) || n < 1) return null;
        var time = starts is { } s ? TimeOnly.FromDateTime(s) : new TimeOnly(0, 0);
        return intervalField?.ToUpperInvariant() switch
        {
            "MINUTE" when n <= 59 => new JobScheduleSpec { Kind = ScheduleKind.EveryMinutes, Interval = n },
            "HOUR" when n <= 23 => new JobScheduleSpec { Kind = ScheduleKind.EveryHours, Interval = n, At = new TimeOnly(0, time.Minute) },
            "DAY" when n == 1 => new JobScheduleSpec { Kind = ScheduleKind.Daily, At = time },
            "WEEK" when n == 1 && starts is { } w => new JobScheduleSpec { Kind = ScheduleKind.Weekly, At = time, Days = [w.DayOfWeek] },
            "MONTH" when n == 1 && starts is { } d && d.Day <= 28 => new JobScheduleSpec { Kind = ScheduleKind.Monthly, At = time, DayOfMonth = d.Day },
            _ => null
        };
    }

    public static string DescribeMySql(string? eventType, DateTime? executeAt, string? intervalValue, string? intervalField, DateTime? starts, DateTime? ends)
    {
        if (string.Equals(eventType, "ONE TIME", StringComparison.OrdinalIgnoreCase))
            return executeAt is { } at ? $"Once on {at:yyyy-MM-dd HH\\:mm}" : "Once";
        if (FromMySql(eventType, executeAt, intervalValue, intervalField, starts, null) is { } spec && spec.Kind != ScheduleKind.Once)
            return spec.Describe() + (ends is { } e ? $" until {e:yyyy-MM-dd HH\\:mm}" : "");
        var unit = (intervalField ?? "").ToLowerInvariant().Replace('_', ' ');
        return $"Every {intervalValue?.Trim('\'')} {unit}" + (starts is { } st ? $" from {st:yyyy-MM-dd HH\\:mm}" : "") +
               (ends is { } en ? $" until {en:yyyy-MM-dd HH\\:mm}" : "");
    }

    public static string MySqlScheduleText(string? eventType, DateTime? executeAt, string? intervalValue, string? intervalField, DateTime? starts, DateTime? ends)
    {
        string Ts(DateTime t) => "'" + t.ToString("yyyy-MM-dd HH:mm:ss", Invariant) + "'";
        if (string.Equals(eventType, "ONE TIME", StringComparison.OrdinalIgnoreCase)) return executeAt is { } at ? "AT " + Ts(at) : "AT ?";
        return $"EVERY {intervalValue} {intervalField}" + (starts is { } s ? " STARTS " + Ts(s) : "") + (ends is { } e ? " ENDS " + Ts(e) : "");
    }

    /// <summary>The next time an event fires after <paramref name="now"/>, for simple intervals; null otherwise.</summary>
    public static DateTime? NextMySql(string? eventType, DateTime? executeAt, string? intervalValue, string? intervalField, DateTime? starts, DateTime? ends, DateTime now)
    {
        if (string.Equals(eventType, "ONE TIME", StringComparison.OrdinalIgnoreCase)) return executeAt > now ? executeAt : null;
        if (starts is not { } start || !long.TryParse(intervalValue?.Trim('\''), NumberStyles.None, Invariant, out var n) || n < 1) return null;
        if (start > now) return start;
        DateTime? next = (intervalField ?? "").ToUpperInvariant() switch
        {
            "SECOND" => Fixed(TimeSpan.FromSeconds(n)),
            "MINUTE" => Fixed(TimeSpan.FromMinutes(n)),
            "HOUR" => Fixed(TimeSpan.FromHours(n)),
            "DAY" => Fixed(TimeSpan.FromDays(n)),
            "WEEK" => Fixed(TimeSpan.FromDays(7 * n)),
            "MONTH" => Months(n),
            "QUARTER" => Months(3 * n),
            "YEAR" => Months(12 * n),
            _ => null
        };
        return next is { } t && (ends is null || t <= ends) ? t : null;

        DateTime Fixed(TimeSpan step)
        {
            var steps = (now - start).Ticks / step.Ticks + 1;
            return start + TimeSpan.FromTicks(step.Ticks * steps);
        }

        DateTime? Months(long months)
        {
            var k = ((now.Year - start.Year) * 12 + now.Month - start.Month) / months;
            for (var i = Math.Max(0, k - 1); i < k + 3; i++)
            {
                var t = start.AddMonths((int)(i * months));
                if (t > now) return t;
            }
            return null;
        }
    }

    // ---------- SQL Server Agent ----------

    /// <summary>The Agent schedule for a form schedule; <paramref name="today"/> is the first day it is active.</summary>
    public static AgentSchedule ToAgent(JobScheduleSpec spec, string name, DateTime today)
    {
        if (spec.Problem(JobEngine.SqlServerAgent) is { } problem) throw new ArgumentException(problem);
        var time = spec.At.Hour * 10000 + spec.At.Minute * 100 + spec.At.Second;
        var start = AgentDate(today);
        var daily = new AgentSchedule { Name = name, FreqType = 4, FreqInterval = 1, FreqSubdayType = 1, ActiveStartDate = start, ActiveStartTime = time };
        return spec.Kind switch
        {
            ScheduleKind.EveryMinutes => daily with { FreqSubdayType = 4, FreqSubdayInterval = spec.Interval, ActiveStartTime = 0 },
            ScheduleKind.EveryHours => daily with { FreqSubdayType = 8, FreqSubdayInterval = spec.Interval, ActiveStartTime = spec.At.Minute * 100 },
            ScheduleKind.Daily => daily,
            ScheduleKind.Weekly => daily with { FreqType = 8, FreqInterval = spec.Days.Distinct().Sum(d => 1 << (int)d), FreqRecurrenceFactor = 1 },
            ScheduleKind.Monthly => daily with { FreqType = 16, FreqInterval = spec.DayOfMonth, FreqRecurrenceFactor = 1 },
            _ => daily with
            {
                FreqType = 1, FreqInterval = 0,
                ActiveStartDate = AgentDate(spec.OnceAt!.Value),
                ActiveStartTime = spec.OnceAt.Value.Hour * 10000 + spec.OnceAt.Value.Minute * 100
            }
        };
    }

    /// <summary>The form schedule of an Agent schedule, or null when it has no form equivalent.</summary>
    public static JobScheduleSpec? FromAgent(AgentSchedule s)
    {
        var at = AgentTime(s.ActiveStartTime);
        if (s.FreqType == 1) return AgentDateTime(s.ActiveStartDate, s.ActiveStartTime) is { } once ? new JobScheduleSpec { Kind = ScheduleKind.Once, OnceAt = once } : null;
        if (s.FreqType == 4 && s.FreqInterval == 1)
        {
            var allDay = s.ActiveEndTime >= 235900;
            return s.FreqSubdayType switch
            {
                1 => new JobScheduleSpec { Kind = ScheduleKind.Daily, At = at },
                4 when allDay && s.ActiveStartTime == 0 && s.FreqSubdayInterval is >= 1 and <= 59 =>
                    new JobScheduleSpec { Kind = ScheduleKind.EveryMinutes, Interval = s.FreqSubdayInterval },
                8 when allDay && at.Hour == 0 && s.FreqSubdayInterval is >= 1 and <= 23 =>
                    new JobScheduleSpec { Kind = ScheduleKind.EveryHours, Interval = s.FreqSubdayInterval, At = at },
                _ => null
            };
        }
        if (s.FreqSubdayType != 1 || s.FreqRecurrenceFactor > 1) return null;
        if (s.FreqType == 8)
            return new JobScheduleSpec { Kind = ScheduleKind.Weekly, At = at, Days = Enum.GetValues<DayOfWeek>().Where(d => (s.FreqInterval & (1 << (int)d)) != 0).ToList() };
        if (s.FreqType == 16) return new JobScheduleSpec { Kind = ScheduleKind.Monthly, At = at, DayOfMonth = s.FreqInterval };
        return null;
    }

    public static string DescribeAgent(AgentSchedule s)
    {
        var at = AgentTime(s.ActiveStartTime);
        string Time(TimeOnly t) => t.ToString("HH:mm", Invariant);
        string Every(int n, string unit) => n <= 1 ? "every " + unit : $"every {n} {unit}s";
        string Subday() => s.FreqSubdayType switch
        {
            2 => Every(s.FreqSubdayInterval, "second"),
            4 => Every(s.FreqSubdayInterval, "minute"),
            8 => Every(s.FreqSubdayInterval, "hour"),
            _ => "at " + Time(at)
        } + (s.FreqSubdayType is 2 or 4 or 8 && (s.ActiveStartTime != 0 || s.ActiveEndTime < 235900)
            ? $" between {Time(at)} and {Time(AgentTime(s.ActiveEndTime))}"
            : s.FreqSubdayType == 8 && at.Minute != 0 ? $" from :{at.Minute:00}" : "");

        var text = s.FreqType switch
        {
            1 => AgentDateTime(s.ActiveStartDate, s.ActiveStartTime) is { } once ? $"Once on {once:yyyy-MM-dd HH\\:mm}" : "Once",
            4 when s.FreqInterval <= 1 => s.FreqSubdayType is 2 or 4 or 8 ? Capitalized(Subday()) : "Daily " + Subday(),
            4 => $"Every {s.FreqInterval} days, " + Subday(),
            8 => (s.FreqRecurrenceFactor <= 1 ? "" : $"Every {s.FreqRecurrenceFactor} weeks: ") +
                 DayList(Enum.GetValues<DayOfWeek>().Where(d => (s.FreqInterval & (1 << (int)d)) != 0).ToList()) + " " + Subday(),
            16 => (s.FreqRecurrenceFactor <= 1 ? "Monthly" : $"Every {s.FreqRecurrenceFactor} months") + $" on day {s.FreqInterval} " + Subday(),
            32 => (s.FreqRecurrenceFactor <= 1 ? "Monthly" : $"Every {s.FreqRecurrenceFactor} months") + " on the " + Relative(s.FreqRelativeInterval) + " " +
                  RelativeDay(s.FreqInterval) + " " + Subday(),
            64 => "When SQL Server Agent starts",
            128 => "When the CPU is idle",
            _ => $"Schedule type {s.FreqType}"
        };
        if (s.ActiveEndDate is > 0 and < 99991231 && AgentDateTime(s.ActiveEndDate, 0) is { } end) text += $" until {end:yyyy-MM-dd}";
        return s.Enabled ? text : text + " (schedule disabled)";

        static string Capitalized(string t) => t.Length == 0 ? t : char.ToUpperInvariant(t[0]) + t[1..];
        static string Relative(int r) => r switch { 1 => "first", 2 => "second", 4 => "third", 8 => "fourth", 16 => "last", _ => "?" };
        static string RelativeDay(int d) => d switch
        {
            >= 1 and <= 7 => Invariant.DateTimeFormat.GetDayName((DayOfWeek)(d - 1)),
            8 => "day",
            9 => "weekday",
            10 => "weekend day",
            _ => "?"
        };
    }

    public static int AgentDate(DateTime d) => d.Year * 10000 + d.Month * 100 + d.Day;

    public static TimeOnly AgentTime(int hhmmss) =>
        new(Math.Clamp(hhmmss / 10000, 0, 23), Math.Clamp(hhmmss / 100 % 100, 0, 59), Math.Clamp(hhmmss % 100, 0, 59));

    /// <summary>msdb's yyyymmdd and hhmmss integers as a date and time; null for 0 (never).</summary>
    public static DateTime? AgentDateTime(int yyyymmdd, int hhmmss)
    {
        if (yyyymmdd <= 0) return null;
        try
        {
            return new DateTime(yyyymmdd / 10000, yyyymmdd / 100 % 100, yyyymmdd % 100) + AgentTime(hhmmss).ToTimeSpan();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>msdb's run_duration (hhmmss, hours unbounded).</summary>
    public static TimeSpan AgentDuration(int hhmmss) => new(hhmmss / 10000, hhmmss / 100 % 100, hhmmss % 100);
}
