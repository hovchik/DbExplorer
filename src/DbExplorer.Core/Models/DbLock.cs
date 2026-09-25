namespace DbExplorer.Core.Models;

public sealed record DbLock
{
    public int SessionId { get; init; }
    public string? LoginName { get; init; }
    public string? HostName { get; init; }
    public string? ProgramName { get; init; }
    public string? DatabaseName { get; init; }
    public string ResourceType { get; init; } = "";
    public string? ObjectName { get; init; }
    public string LockMode { get; init; } = "";

    /// <summary>GRANT, WAIT or CONVERT.</summary>
    public string Status { get; init; } = "";

    public int? BlockedBy { get; init; }
    public int? WaitMs { get; init; }
    public string? WaitType { get; init; }
    public string? SqlText { get; init; }

    public bool IsWaiting => Status is "WAIT" or "CONVERT";
}
