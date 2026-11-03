using System.Globalization;

namespace DbExplorer.Application.Jobs;

/// <summary>
/// A pg_cron schedule: five cron fields (minute hour day-of-month month day-of-week, with lists, ranges, steps, month and
/// day names, and pg_cron's <c>$</c> for the last day of the month), an @hourly-style alias, or "N seconds".
/// Parsed to check what is typed, to put it into words and to work out the next runs.
/// </summary>
public sealed class CronSchedule
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly string[] MonthNames = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    private static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["@yearly"] = "0 0 1 1 *", ["@annually"] = "0 0 1 1 *", ["@monthly"] = "0 0 1 * *", ["@weekly"] = "0 0 * * 0",
        ["@daily"] = "0 0 * * *", ["@midnight"] = "0 0 * * *", ["@hourly"] = "0 * * * *"
    };

    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _days = new bool[32];
    private readonly bool[] _months = new bool[13];
    private readonly bool[] _weekdays = new bool[7];
    private bool _lastDay;
    private bool _dayStar;
    private bool _weekdayStar;

    private CronSchedule(string text) => Text = text;

    public string Text { get; }

    /// <summary>The five fields after expanding an alias; empty for "N seconds".</summary>
    public IReadOnlyList<string> Fields { get; private set; } = [];

    /// <summary>pg_cron's "N seconds" interval (1–59).</summary>
    public int? Seconds { get; private set; }

    /// <summary>The weekdays when the day-of-week field is restricted, else null.</summary>
    public IReadOnlyList<DayOfWeek>? DaysOfWeek =>
        _weekdayStar ? null : Enumerable.Range(0, 7).Where(d => _weekdays[d]).Select(d => (DayOfWeek)d).ToList();

    public static bool TryParse(string text, out CronSchedule? schedule, out string? error)
    {
        schedule = null;
        error = null;
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            error = "Type a cron schedule, such as 0 3 * * * (daily at 03:00).";
            return false;
        }
        var result = new CronSchedule(trimmed);
        var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && parts[1].StartsWith("second", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(parts[0], NumberStyles.None, Invariant, out var seconds) || seconds is < 1 or > 59)
            {
                error = "pg_cron takes 1 to 59 seconds (pg_cron 1.5 or later).";
                return false;
            }
            result.Seconds = seconds;
            schedule = result;
            return true;
        }
        if (parts.Length == 1 && parts[0].StartsWith('@'))
        {
            if (!Aliases.TryGetValue(parts[0], out var expanded))
            {
                error = $"{parts[0]} is not a schedule pg_cron runs on; use @hourly, @daily, @weekly, @monthly or @yearly.";
                return false;
            }
            parts = expanded.Split(' ');
        }
        if (parts.Length != 5)
        {
            error = "A cron schedule has five fields: minute hour day-of-month month day-of-week (e.g. 30 2 * * 1-5).";
            return false;
        }
        result.Fields = parts;
        if (!Field(parts[0], 0, 59, null, result._minutes, "minute", out error)
            || !Field(parts[1], 0, 23, null, result._hours, "hour", out error)
            || !DayOfMonth(parts[2], result, out error)
            || !Field(parts[3], 1, 12, MonthNames, result._months, "month", out error)
            || !Weekdays(parts[4], result, out error))
            return false;
        result._dayStar = parts[2] == "*";
        result._weekdayStar = parts[4] == "*";
        schedule = result;
        return true;
    }

    private static bool DayOfMonth(string field, CronSchedule s, out string? error)
    {
        error = null;
        var items = field.Split(',');
        var rest = items.Where(i => i != "$").ToList();
        s._lastDay = rest.Count < items.Length;
        return rest.Count == 0 || Field(string.Join(",", rest), 1, 31, null, s._days, "day of month", out error);
    }

    private static bool Weekdays(string field, CronSchedule s, out string? error)
    {
        var days = new bool[8];
        if (!Field(field, 0, 7, DayNames, days, "day of week", out error)) return false;
        for (var d = 0; d < 7; d++) s._weekdays[d] = days[d];
        if (days[7]) s._weekdays[0] = true; // 7 is Sunday too
        return true;
    }

    private static bool Field(string field, int min, int max, string[]? names, bool[] set, string what, out string? error)
    {
        error = null;
        foreach (var item in field.Split(','))
        {
            var step = 1;
            var range = item;
            var slash = item.IndexOf('/');
            if (slash >= 0)
            {
                if (!int.TryParse(item[(slash + 1)..], NumberStyles.None, Invariant, out step) || step < 1)
                {
                    error = $"The {what} step in '{item}' must be a positive number.";
                    return false;
                }
                range = item[..slash];
            }
            int from, to;
            if (range == "*")
            {
                from = min;
                to = max;
            }
            else
            {
                var dash = range.IndexOf('-');
                if (!Value(dash >= 0 ? range[..dash] : range, names, min, out from)
                    || !Value(dash >= 0 ? range[(dash + 1)..] : range, names, min, out to))
                {
                    error = $"'{item}' is not a valid {what}.";
                    return false;
                }
                // "5/15" means from 5 to the end, every 15.
                if (dash < 0 && slash >= 0) to = max;
            }
            if (from < min || to > max || from > to)
            {
                error = $"The {what} '{item}' is out of range ({min}–{max}).";
                return false;
            }
            for (var v = from; v <= to; v += step) set[v] = true;
        }
        return true;
    }

    private static bool Value(string text, string[]? names, int min, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, Invariant, out value)) return true;
        if (names is not null)
        {
            var i = Array.FindIndex(names, n => n.Equals(text, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                value = i + min;
                return true;
            }
        }
        return false;
    }

    private bool DayMatches(DateTime d)
    {
        var dom = _days[d.Day] || (_lastDay && d.Day == DateTime.DaysInMonth(d.Year, d.Month));
        var dow = _weekdays[(int)d.DayOfWeek];
        // Classic cron: when both fields are restricted, either one matching is enough.
        if (!_dayStar && !_weekdayStar) return dom || dow;
        return (_dayStar || dom) && (_weekdayStar || dow);
    }

    /// <summary>The first run strictly after <paramref name="after"/>, in the same clock (pg_cron's cron.timezone).</summary>
    public DateTime? Next(DateTime after)
    {
        if (Seconds is { } s) return after.AddSeconds(s - after.Second % s);
        var t = new DateTime(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Kind).AddMinutes(1);
        var limit = after.AddYears(5);
        while (t < limit)
        {
            if (!_months[t.Month])
            {
                t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, t.Kind).AddMonths(1);
                continue;
            }
            if (!DayMatches(t))
            {
                t = t.Date.AddDays(1);
                continue;
            }
            if (!_hours[t.Hour])
            {
                t = t.Date.AddHours(t.Hour + 1);
                continue;
            }
            if (!_minutes[t.Minute])
            {
                t = t.AddMinutes(1);
                continue;
            }
            return t;
        }
        return null;
    }

    public IReadOnlyList<DateTime> NextRuns(DateTime after, int count)
    {
        var runs = new List<DateTime>();
        var at = after;
        while (runs.Count < count && Next(at) is { } next)
        {
            runs.Add(next);
            at = next;
        }
        return runs;
    }

    public string Describe()
    {
        if (Seconds is { } s) return s == 1 ? "Every second" : $"Every {s} seconds";
        var spec = JobScheduleText.FromCron(Text);
        if (spec.Kind != ScheduleKind.Cron) return spec.Describe();
        var f = Fields;
        var parts = new List<string>();
        if (f[0] == "*" && f[1] == "*") parts.Add("Every minute");
        else if (int.TryParse(f[0], NumberStyles.None, Invariant, out var minute) && int.TryParse(f[1], NumberStyles.None, Invariant, out var hour))
            parts.Add($"At {hour:00}:{minute:00}");
        else parts.Add($"At minute {f[0]} of hour {f[1]}");
        if (f[2] != "*") parts.Add("on day " + f[2].Replace("$", "last") + " of the month");
        if (f[4] != "*" && DaysOfWeek is { } days) parts.Add((f[2] != "*" ? "or on " : "on ") + JobScheduleText.DayList(days).Replace("Weekly on ", ""));
        if (f[3] != "*") parts.Add("in " + string.Join(", ", Enumerable.Range(1, 12).Where(m => _months[m]).Select(m => Invariant.DateTimeFormat.GetAbbreviatedMonthName(m))));
        return string.Join(" ", parts);
    }
}
