using DbExplorer.Core.Connections;

namespace DbExplorer.Application.Workspace;

public enum StartupConnectionAction
{
    /// <summary>Nothing saved, or the saved connection is gone: the first connection is selected as before.</summary>
    None,

    /// <summary>Select the saved connection in the toolbar and leave it closed.</summary>
    Select,

    /// <summary>Select it and open it.</summary>
    Connect
}

/// <summary>Decides what the start-up does with the connection that was picked when DbExplorer last closed.</summary>
public static class StartupConnection
{
    public static (StartupConnectionAction Action, ConnectionProfile? Profile, string? Note) Decide(
        WorkspaceState state, IReadOnlyList<ConnectionProfile> profiles, bool reconnectOnStart)
    {
        if (state.ConnectionId is not { } id || profiles.FirstOrDefault(p => p.Id == id) is not { } profile)
            return (StartupConnectionAction.None, null, null);
        if (!state.WasConnected || !reconnectOnStart) return (StartupConnectionAction.Select, profile, null);

        // Never open production by itself: the user connects knowingly.
        if (profile.IsProduction)
            return (StartupConnectionAction.Select, profile, $"{profile.DisplayName} is a production connection, so it was not reopened. Click Connect to open it.");

        // A password that was not saved would pop a dialog before the window is even up; let the user ask for it.
        if (profile.NeedsDatabasePassword || profile.Ssh.NeedsPassword)
            return (StartupConnectionAction.Select, profile, $"{profile.DisplayName} needs a password, so it was not reopened. Click Connect to open it.");

        return (StartupConnectionAction.Connect, profile, null);
    }
}
