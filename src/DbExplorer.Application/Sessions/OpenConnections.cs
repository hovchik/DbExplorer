namespace DbExplorer.Application.Sessions;

/// <summary>
/// Every connection open at the same time, at most one per saved connection. One of them is active: the Objects
/// tab, the Diagram and the other main tabs show the active one, while each Query tab keeps the connection it is
/// bound to. Closing the active connection makes the one used most recently
/// before it active again.
/// </summary>
public sealed class OpenConnections
{
    private readonly List<DatabaseSession> _opened = [];   // in the order they were opened
    private readonly List<DatabaseSession> _recent = [];   // most recently active first

    /// <summary>The open connections in the order they were opened (the order their colour marks are given in).</summary>
    public IReadOnlyList<DatabaseSession> All => _opened;

    public int Count => _opened.Count;

    /// <summary>The connection the main tabs show; null when nothing is open.</summary>
    public DatabaseSession? Active => _recent.Count > 0 ? _recent[0] : null;

    /// <summary>Raised after a connection was added, removed or made active.</summary>
    public event EventHandler? Changed;

    public DatabaseSession? Find(Guid profileId) => _opened.FirstOrDefault(s => s.Profile.Id == profileId);

    public bool IsOpen(Guid profileId) => Find(profileId) is not null;

    /// <summary>Position among the open connections, for a stable colour mark; -1 when not open.</summary>
    public int IndexOf(Guid profileId) => _opened.FindIndex(s => s.Profile.Id == profileId);

    /// <summary>Adds a newly opened connection and makes it active.</summary>
    /// <exception cref="InvalidOperationException">That saved connection is already open.</exception>
    public void Add(DatabaseSession session)
    {
        if (IsOpen(session.Profile.Id))
            throw new InvalidOperationException($"{session.Profile.DisplayName} is already open.");
        _opened.Add(session);
        _recent.Insert(0, session);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Makes an open connection the active one; false when it is not open.</summary>
    public bool Activate(DatabaseSession session)
    {
        var at = _recent.IndexOf(session);
        if (at < 0) return false;
        if (at == 0) return true;
        _recent.RemoveAt(at);
        _recent.Insert(0, session);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The connection that becomes active when <paramref name="session"/> is closed: the active one when another
    /// connection is closed, otherwise the one used most recently before it; null when nothing would be left.</summary>
    public DatabaseSession? SuccessorOf(DatabaseSession session) =>
        !ReferenceEquals(Active, session) ? Active : _recent.Count > 1 ? _recent[1] : null;

    /// <summary>Forgets a connection (the caller disposes it).</summary>
    /// <returns>The connection that is active afterwards: unchanged when another one was closed, otherwise the one used
    /// most recently before it, or null when nothing is left open.</returns>
    public DatabaseSession? Remove(DatabaseSession session)
    {
        if (_opened.Remove(session))
        {
            _recent.Remove(session);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return Active;
    }
}

/// <summary>Which connection a Query tab is bound to.</summary>
public static class QueryTabConnection
{
    /// <summary>
    /// The connection a new tab made with ＋ New (or Ctrl+N) is bound to: the selected tab's, when that one is open, so a
    /// second script for the same server needs no picking; otherwise the active connection; null when nothing is open
    /// (the tab then takes the first connection opened).
    /// </summary>
    public static Guid? ForNewTab(Guid? selectedTab, Func<Guid, bool> isOpen, Guid? active) =>
        selectedTab is { } id && isOpen(id) ? id : active;
}
