using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Connections;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>Team sharing: the Team button opens the shared folder's connections, queries and snippets.</summary>
public partial class MainWindowViewModel
{
    public TeamViewModel? Team { get; private set; }

    /// <summary>Raised by the Team button; the window shows the Team window.</summary>
    public event Action<TeamViewModel>? ShowTeamRequested;

    public void AttachTeam(TeamViewModel team)
    {
        Team = team;
        team.GetProfiles = () => Profiles.ToList();
        team.SetProfilesAsync = async profiles =>
        {
            var selectedId = SelectedProfile?.Id;
            Profiles.Clear();
            foreach (var profile in ConnectionFolders.Order(profiles)) Profiles.Add(profile);
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Profiles.FirstOrDefault();
            await SaveProfilesAsync();
        };
        team.OpenSql = (title, sql) =>
        {
            Query.OpenInNewTab(sql, title);
            if (IsConnected) SelectedTab = AppTab.Query;
            else StatusText = $"Opened \"{title}\" in a Query tab; connect to see and run it.";
        };
        team.CurrentQuery = () => Query.SelectedDocument is { } doc ? (doc.Title, doc.Sql) : null;
        OnPropertyChanged(nameof(Team));
        team.Start();
    }

    [RelayCommand]
    private void OpenTeam()
    {
        if (Team is { } team) ShowTeamRequested?.Invoke(team);
    }
}
