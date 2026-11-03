using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Connections;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>A saved connection in a Query tab's connection picker, with whether it is open and its colour mark.</summary>
public sealed record ConnectionChoice(ConnectionProfile Profile, bool IsOpen, bool IsActive, int Mark)
{
    public string Name => Profile.DisplayName;
    public IBrush MarkBrush => ConnectionMarks.Brush(IsOpen ? Mark : -1);
    public string State => IsActive ? "active" : IsOpen ? "open" : "";
    public string ToolTip => Profile.QualifiedName + (IsActive
        ? " · open, and the one the Objects, Diagram and other tabs show"
        : IsOpen ? " · open" : " · not open: picking it shows Connect above the editor");

    /// <summary>Type-to-search in the picker reads this.</summary>
    public override string ToString() => Profile.DisplayName;
}

/// <summary>
/// The colour each open connection is marked with on Query tab headers, in the tabs' connection picker and in the
/// navigator, so tabs on different servers are told apart at a glance. Given in the order connections were opened;
/// -1 (not open) is a neutral grey.
/// </summary>
public static class ConnectionMarks
{
    private static readonly IBrush[] Palette =
    [
        Brush("#2F80ED"), // blue
        Brush("#E2711D"), // orange
        Brush("#27AE60"), // green
        Brush("#9B51E0"), // violet
        Brush("#D6336C"), // pink
        Brush("#0FA3B1"), // teal
        Brush("#B08900"), // gold
        Brush("#7A5C3E")  // brown
    ];

    private static readonly IBrush Closed = Brush("#8A8F98");

    public static IBrush Brush(int index) => index < 0 ? Closed : Palette[index % Palette.Length];

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}

public partial class QueryViewModel
{
    /// <summary>
    /// The saved connection the tab is bound to: it runs there whichever connection is active, and waits with
    /// "Connect to …" while that connection is closed. Null until the tab is first attached (it then takes the
    /// active connection).
    /// </summary>
    public Guid? ConnectionId
    {
        get => _connectionId;
        set
        {
            if (_connectionId == value) return;
            _connectionId = value;
            OnPropertyChanged();
        }
    }

    private Guid? _connectionId;

    /// <summary>The connection the tab is attached to now (the bound one while it is open), or null.</summary>
    public DatabaseSession? AttachedSession => _session;

    /// <summary>The saved connections for the picker in the tab's toolbar (shared by all tabs).</summary>
    [ObservableProperty] private IReadOnlyList<ConnectionChoice> _connectionChoices = [];

    [ObservableProperty] private ConnectionChoice? _selectedConnection;

    /// <summary>The dot before the tab's title: the bound connection's colour (grey while it is closed).</summary>
    [ObservableProperty] private IBrush? _connectionMark;

    /// <summary>Whether the tab headers show the connection dot: only once tabs are on more than one connection.</summary>
    [ObservableProperty] private bool _showConnectionMark;

    /// <summary>"Connection: Shop (open)" in the tab header's tooltip.</summary>
    [ObservableProperty] private string _connectionSummary = "";

    /// <summary>The text of the bar above the editor while the tab cannot run.</summary>
    [ObservableProperty] private string _offlineText = NotConnectedText;

    public const string NotConnectedText = "Not connected. You can write, open and save scripts here; connect to run them.";

    /// <summary>"Use Shop instead" on that bar: rebinds the tab to the active connection.</summary>
    [ObservableProperty] private string _useActiveLabel = "";

    public System.Windows.Input.ICommand? UseActiveCommand { get; set; }

    /// <summary>The user picked another connection in the tab's picker (the workspace rebinds the tab).</summary>
    public event Action<QueryViewModel, ConnectionProfile>? ConnectionPicked;

    /// <summary>A running statement or an open transaction belongs to the connection: it cannot move until it ends.</summary>
    public bool CanChangeConnection => !IsRunning && !HasOpenTransaction;

    private bool _settingConnection;

    /// <summary>The workspace refreshes the picker and the header dot (the list, which connections are open, the mark).</summary>
    public void ShowConnection(IReadOnlyList<ConnectionChoice> choices, bool showMark)
    {
        _settingConnection = true;
        try
        {
            ConnectionChoices = choices;
            var bound = ConnectionId is { } id ? choices.FirstOrDefault(c => c.Profile.Id == id) : null;
            SelectedConnection = bound;
            ConnectionMark = bound?.MarkBrush ?? ConnectionMarks.Brush(-1);
            ShowConnectionMark = showMark;
            ConnectionSummary = bound is null
                ? "No connection yet: it takes the first one you open"
                : $"Connection: {bound.Name}" + (bound.IsOpen ? "" : " (not open)");
        }
        finally
        {
            _settingConnection = false;
        }
    }

    partial void OnSelectedConnectionChanged(ConnectionChoice? oldValue, ConnectionChoice? newValue)
    {
        if (_settingConnection || newValue is null || newValue.Profile.Id == ConnectionId) return;
        if (!CanChangeConnection)
        {
            Status = IsRunning
                ? "Wait for the run to finish (or Cancel it) before moving the tab to another connection."
                : "Commit or Rollback the open transaction before moving the tab to another connection.";
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _settingConnection = true;
                SelectedConnection = oldValue;
                _settingConnection = false;
            });
            return;
        }
        ConnectionPicked?.Invoke(this, newValue.Profile);
    }
}
