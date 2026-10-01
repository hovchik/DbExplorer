using System.Text;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Diagnostics;

/// <summary>One poll of the lock list, reduced to the locks of sessions involved in blocking (empty when none was).</summary>
public sealed record LockSample(DateTimeOffset At, IReadOnlyList<DbLock> Locks)
{
    public bool HasBlocking => Locks.Any(l => l.BlockedBy is not null || l.IsWaiting);

    public int WaitingSessions => Locks.Where(l => l.BlockedBy is not null || l.IsWaiting).Select(l => l.SessionId).Distinct().Count();
}

/// <summary>A period during which some session was blocked, with every sample taken meanwhile.</summary>
public sealed record BlockingIncident(DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<LockSample> Samples)
{
    public TimeSpan Duration => End - Start;

    public int MaxWaitingSessions => Samples.Count == 0 ? 0 : Samples.Max(s => s.WaitingSessions);

    public int? MaxWaitMs => Samples.SelectMany(s => s.Locks).Max(l => l.WaitMs);

    /// <summary>Sessions that were the root of a blocking chain at some point of the incident.</summary>
    public IReadOnlyList<int> HeadBlockers => Samples
        .SelectMany(s => BlockingChainBuilder.Build(s.Locks).Select(n => n.SessionId))
        .Distinct().Order().ToList();

    public IReadOnlyList<string> Objects => Samples.SelectMany(s => s.Locks)
        .Where(l => l.IsWaiting && !string.IsNullOrEmpty(l.ObjectName)).Select(l => l.ObjectName!)
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();

    public string ObjectsText => Objects.Count == 0 ? "" : "contended: " + string.Join(", ", Objects);

    public string Title =>
        $"{Start:T} – {End:T} ({FormatDuration(Duration)}) · up to {MaxWaitingSessions} waiting · head blocker(s) {string.Join(", ", HeadBlockers)}";

    internal static string FormatDuration(TimeSpan d) =>
        d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} min {d.Seconds} s" : $"{Math.Max(0, d.TotalSeconds):0} s";
}

/// <summary>
/// A flight recorder for blocking: the Locks tab feeds it every poll; it keeps the last <see cref="Capacity"/> samples
/// in memory (an hour at the 5 s auto-refresh) and groups them into incidents that can be replayed afterwards — after
/// the blocking is gone and nobody was looking at the screen when it happened.
/// </summary>
public sealed class BlockingRecorder(int capacity = 720)
{
    private readonly LinkedList<LockSample> _samples = new();
    private readonly object _gate = new();

    public int Capacity { get; } = capacity;

    public int Count
    {
        get { lock (_gate) return _samples.Count; }
    }

    public void Add(DateTimeOffset at, IReadOnlyList<DbLock> locks)
    {
        var sample = new LockSample(at, Relevant(locks));
        lock (_gate)
        {
            _samples.AddLast(sample);
            while (_samples.Count > Capacity) _samples.RemoveFirst();
        }
    }

    public void Clear()
    {
        lock (_gate) _samples.Clear();
    }

    public IReadOnlyList<LockSample> Samples
    {
        get { lock (_gate) return _samples.ToList(); }
    }

    public IReadOnlyList<BlockingIncident> Incidents(TimeSpan maxGap) => GroupIncidents(Samples, maxGap);

    /// <summary>Locks of waiting sessions and of the sessions they wait on; everything else is noise for replay.</summary>
    public static IReadOnlyList<DbLock> Relevant(IReadOnlyList<DbLock> locks)
    {
        var involved = new HashSet<int>();
        foreach (var l in locks)
        {
            if (l.BlockedBy is int blocker) { involved.Add(l.SessionId); involved.Add(blocker); }
            if (l.IsWaiting) involved.Add(l.SessionId);
        }
        return involved.Count == 0 ? [] : locks.Where(l => involved.Contains(l.SessionId)).ToList();
    }

    /// <summary>
    /// Consecutive samples with blocking form one incident. It ends at the first sample without blocking, or where
    /// samples are further apart than <paramref name="maxGap"/> (recording was paused, so nothing is known in between).
    /// </summary>
    public static IReadOnlyList<BlockingIncident> GroupIncidents(IReadOnlyList<LockSample> samples, TimeSpan maxGap)
    {
        var incidents = new List<BlockingIncident>();
        var current = new List<LockSample>();
        DateTimeOffset? previous = null;

        void Close(DateTimeOffset end)
        {
            if (current.Count > 0) incidents.Add(new BlockingIncident(current[0].At, end, current.ToList()));
            current.Clear();
        }

        foreach (var s in samples.OrderBy(s => s.At))
        {
            if (previous is { } p && s.At - p > maxGap) Close(p);
            if (s.HasBlocking) current.Add(s);
            else Close(s.At);
            previous = s.At;
        }
        if (previous is { } last) Close(last);
        incidents.Reverse(); // newest first
        return incidents;
    }

    /// <summary>A plain-text account of an incident: timeline, then the blocking tree at its worst moment.</summary>
    public static string Report(BlockingIncident incident)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Blocking incident {incident.Start:yyyy-MM-dd HH:mm:ss} – {incident.End:HH:mm:ss} ({BlockingIncident.FormatDuration(incident.Duration)})");
        sb.AppendLine($"Head blocker(s): {string.Join(", ", incident.HeadBlockers)} · up to {incident.MaxWaitingSessions} waiting session(s)" +
                      (incident.MaxWaitMs is int ms ? $" · longest wait {ms:N0} ms" : ""));
        if (incident.Objects.Count > 0) sb.AppendLine("Contended objects: " + string.Join(", ", incident.Objects));
        sb.AppendLine();
        sb.AppendLine("Timeline:");
        foreach (var s in incident.Samples)
        {
            var heads = BlockingChainBuilder.Build(s.Locks);
            sb.AppendLine($"  {s.At:HH:mm:ss}  {s.WaitingSessions} waiting · " +
                          string.Join("; ", heads.Select(h => $"{h.SessionId} blocks {h.TotalBlocked}")));
        }

        var worst = incident.Samples.OrderByDescending(s => s.WaitingSessions).ThenBy(s => s.At).FirstOrDefault();
        if (worst is not null)
        {
            sb.AppendLine();
            sb.AppendLine($"Blocking tree at {worst.At:HH:mm:ss}:");
            foreach (var node in BlockingChainBuilder.Build(worst.Locks)) Append(sb, node, 1);
        }
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, BlockingNode node, int depth)
    {
        var indent = new string(' ', depth * 2);
        sb.AppendLine(indent + node.Title);
        if (node.Detail is { } detail) sb.AppendLine(indent + "  " + detail);
        if (!string.IsNullOrWhiteSpace(node.SqlText))
        {
            var sql = string.Join(" ", node.SqlText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            sb.AppendLine(indent + "  SQL: " + (sql.Length > 300 ? sql[..300] + "…" : sql));
        }
        foreach (var child in node.Children) Append(sb, child, depth + 1);
    }
}
