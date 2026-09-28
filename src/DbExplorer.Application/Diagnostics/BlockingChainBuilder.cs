using DbExplorer.Core.Models;

namespace DbExplorer.Application.Diagnostics;

/// <summary>One session in a blocking chain; <see cref="Children"/> are the sessions it blocks.</summary>
public sealed class BlockingNode
{
    public required int SessionId { get; init; }
    public string? LoginName { get; init; }
    public string? HostName { get; init; }
    public string? ProgramName { get; init; }
    public string? WaitType { get; init; }
    public int? WaitMs { get; init; }
    public string? WaitingOn { get; init; }
    public string? SqlText { get; init; }

    /// <summary>The session is part of a blocking cycle (it transitively blocks itself); the chain is cut where it repeats.</summary>
    public bool IsInCycle { get; init; }

    /// <summary>No lock rows were returned for this session (e.g. it holds locks in another database).</summary>
    public bool IsUnknown { get; init; }

    public IReadOnlyList<BlockingNode> Children { get; internal set; } = [];

    /// <summary>Sessions waiting on this one, directly or transitively.</summary>
    public int TotalBlocked => Children.Count + Children.Sum(c => c.TotalBlocked);

    public string Title
    {
        get
        {
            var who = string.Join(" · ", new[] { LoginName, HostName, ProgramName }.Where(s => !string.IsNullOrWhiteSpace(s)));
            var role = Children.Count > 0 ? $"blocks {TotalBlocked}" : "waiting";
            var flags = (IsInCycle ? " · CYCLE" : "") + (IsUnknown ? " · no lock details" : "");
            return $"Session {SessionId} ({role}){flags}" + (who.Length > 0 ? " — " + who : "");
        }
    }

    public string? Detail
    {
        get
        {
            if (WaitType is null && WaitingOn is null) return null;
            var waited = WaitMs is int ms ? $" for {ms:N0} ms" : "";
            return $"waiting on {WaitingOn ?? "?"} ({WaitType ?? "lock"}){waited}";
        }
    }
}

/// <summary>Turns a flat lock list into "head blocker → blocked sessions" trees.</summary>
public static class BlockingChainBuilder
{
    public static IReadOnlyList<BlockingNode> Build(IReadOnlyList<DbLock> locks)
    {
        var bySession = locks.GroupBy(l => l.SessionId).ToDictionary(g => g.Key, g => g.ToList());

        var blockerOf = new Dictionary<int, int>();
        foreach (var (session, rows) in bySession)
        {
            var waiting = rows.FirstOrDefault(r => r.BlockedBy is not null && r.BlockedBy != session);
            if (waiting?.BlockedBy is int blocker) blockerOf[session] = blocker;
        }
        if (blockerOf.Count == 0) return [];

        var blockedBy = blockerOf.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).OrderBy(s => s).ToList());
        var inCycle = FindCycleMembers(blockerOf);

        var heads = blockedBy.Keys.Where(s => !blockerOf.ContainsKey(s)).OrderBy(s => s).ToList();
        var visited = new HashSet<int>();
        var roots = heads.Select(h => BuildNode(h)).ToList();

        // Pure cycles have no head blocker: start each one from its lowest session id.
        foreach (var s in inCycle.OrderBy(s => s))
            if (!visited.Contains(s)) roots.Add(BuildNode(s));

        return roots.OrderByDescending(r => r.TotalBlocked).ThenBy(r => r.SessionId).ToList();

        BlockingNode BuildNode(int session)
        {
            visited.Add(session);
            var node = CreateNode(session, bySession.GetValueOrDefault(session), inCycle.Contains(session));
            node.Children = (blockedBy.GetValueOrDefault(session) ?? [])
                .Where(child => !visited.Contains(child))
                .Select(BuildNode)
                .ToList();
            return node;
        }
    }

    private static HashSet<int> FindCycleMembers(Dictionary<int, int> blockerOf)
    {
        var members = new HashSet<int>();
        foreach (var start in blockerOf.Keys)
        {
            var path = new List<int>();
            var onPath = new HashSet<int>();
            var current = start;
            while (blockerOf.TryGetValue(current, out var next))
            {
                if (members.Contains(current)) break;
                if (!onPath.Add(current))
                {
                    members.UnionWith(path.SkipWhile(s => s != current));
                    break;
                }
                path.Add(current);
                current = next;
            }
        }
        return members;
    }

    private static BlockingNode CreateNode(int session, List<DbLock>? rows, bool inCycle)
    {
        if (rows is null || rows.Count == 0)
            return new BlockingNode { SessionId = session, IsUnknown = true, IsInCycle = inCycle };

        var waiting = rows.FirstOrDefault(r => r.IsWaiting) ?? rows.FirstOrDefault(r => r.BlockedBy is not null);
        var any = rows[0];
        return new BlockingNode
        {
            SessionId = session,
            LoginName = any.LoginName,
            HostName = any.HostName,
            ProgramName = any.ProgramName,
            WaitType = waiting?.WaitType,
            WaitMs = waiting?.WaitMs,
            WaitingOn = waiting is null ? null : $"{waiting.LockMode} {waiting.ResourceType} {waiting.ObjectName}".Trim(),
            SqlText = rows.Select(r => r.SqlText).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
            IsInCycle = inCycle
        };
    }
}
