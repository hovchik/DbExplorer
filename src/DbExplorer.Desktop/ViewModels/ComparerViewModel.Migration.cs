using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Compare;
using DbExplorer.Application.Sessions;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// Migration tab: one reviewed script that makes the right database's schema match the left's (every object, or the
/// ones picked on the Overview tab). It is built from both catalogs plus the column defaults read from the servers, and
/// never run here: copy it, save it as .sql, or open it in a Query tab when the right side is the main connection.
/// </summary>
public partial class ComparerViewModel
{
    public const int MigrationTab = 5;

    /// <summary>Raised to open SQL in a new Query tab of the main window: the script and the database it is for.</summary>
    public event Action<string, string?>? OpenSqlRequested;

    [ObservableProperty] private bool _migrationDropExtras;
    [ObservableProperty] private bool _migrationOnlySelected;
    [ObservableProperty] private string _migrationScript = "";
    [ObservableProperty] private IReadOnlyList<MigrationItem> _migrationItems = [];
    [ObservableProperty] private IReadOnlyList<string> _migrationWarnings = [];
    [ObservableProperty] private string _migrationStatus = "";
    [ObservableProperty] private bool _showUnchangedMigrationItems;

    /// <summary>The script on screen was built for this right database.</summary>
    private string? _migrationDatabase;
    private IReadOnlyList<MigrationItem> _allMigrationItems = [];

    public bool HasMigration => MigrationScript.Length > 0;
    public bool HasMigrationWarnings => MigrationWarnings.Count > 0;

    public string MigrationOnlySelectedLabel => SelectedOverviewEntries.Count > 0
        ? $"Only the {SelectedOverviewEntries.Count} object(s) selected on Overview"
        : "Only the objects selected on Overview";

    /// <summary>Where the script goes, in its header and the tab: "dev · Shop → t01 · Shop".</summary>
    public string MigrationDirection => LeftSession is null || RightSession is null
        ? "Connect both sides: the script makes the right database match the left one."
        : $"Makes {Side(RightSession, SelectedRightDatabase)} match {Side(LeftSession, SelectedLeftDatabase)}";

    public string MigrationFileStem =>
        $"migrate_{SelectedRightDatabase ?? RightSession?.Profile.DisplayName ?? "target"}_from_{SelectedLeftDatabase ?? LeftSession?.Profile.DisplayName ?? "source"}"
            .Replace(' ', '_');

    private static string Side(DatabaseSession session, string? database) =>
        session.Profile.DisplayName + (string.IsNullOrEmpty(database) ? "" : " · " + database);

    partial void OnMigrationScriptChanged(string value)
    {
        OnPropertyChanged(nameof(HasMigration));
        OpenMigrationInQueryCommand.NotifyCanExecuteChanged();
        CopyMigrationCommand.NotifyCanExecuteChanged();
    }

    partial void OnMigrationWarningsChanged(IReadOnlyList<string> value) => OnPropertyChanged(nameof(HasMigrationWarnings));

    partial void OnShowUnchangedMigrationItemsChanged(bool value) => ApplyMigrationFilter();

    private void ApplyMigrationFilter() =>
        MigrationItems = ShowUnchangedMigrationItems ? _allMigrationItems : _allMigrationItems.Where(i => i.Action != MigrationAction.Unchanged).ToList();

    private bool CanGenerateMigration => LeftSession is not null && RightSession is not null && !IsBusy;

    /// <summary>Overview's button: switches to the Migration tab and builds the script for what is selected there (when
    /// several objects are), or for everything.</summary>
    [RelayCommand(CanExecute = nameof(CanGenerateMigration))]
    private async Task MigrationFromOverviewAsync()
    {
        MigrationOnlySelected = SelectedOverviewEntries.Count > 1;
        SelectedTabIndex = MigrationTab;
        await GenerateMigrationAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGenerateMigration))]
    private async Task GenerateMigrationAsync()
    {
        if (LeftTarget is not { } left || RightTarget is not { } right) return;
        if (MigrationOnlySelected && SelectedOverviewEntries.Count == 0)
        {
            MigrationStatus = "Select objects on the Overview tab first (Ctrl/Shift-click), or untick \"Only the objects selected\".";
            return;
        }

        var only = MigrationOnlySelected
            ? SelectedOverviewEntries.Select(e => (e.Type, e.Schema, e.Name)).ToList()
            : null;
        var leftDatabase = SelectedLeftDatabase;
        var rightDatabase = SelectedRightDatabase;
        var ct = BeginOperation();
        var watch = Stopwatch.StartNew();
        try
        {
            var leftSnapshot = Scoped(left.Snapshot, leftDatabase);
            var rightSnapshot = Scoped(right.Snapshot, rightDatabase);
            bool Wanted(DbObject t) => only is null || only.Any(o => o.Type == t.Type &&
                string.Equals(o.Name, t.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(o.Schema, t.Schema, StringComparison.OrdinalIgnoreCase));
            var leftTables = leftSnapshot.Objects.Where(o => o.Type == DbObjectType.Table && Wanted(o)).ToList();
            var names = leftTables.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Defaults are only needed where a table is created or compared: the right tables with a left namesake.
            var rightTables = rightSnapshot.Objects.Where(o => o.Type == DbObjectType.Table && names.Contains(o.Name)).ToList();

            var leftConstraints = await ReadMigrationConstraintsAsync(left, leftTables, "left", ct);
            var rightConstraints = await ReadMigrationConstraintsAsync(right, rightTables, "right", ct);

            MigrationStatus = "Building the script…";
            var options = new MigrationOptions
            {
                DropExtras = MigrationDropExtras,
                Only = only,
                Title = $"{Side(RightSession!, rightDatabase)} ← {Side(LeftSession!, leftDatabase)}"
            };
            var script = await Task.Run(() => MigrationScriptBuilder.Build(
                new MigrationSide(left.Snapshot, left.Provider.ProviderKey, leftDatabase, leftConstraints),
                new MigrationSide(right.Snapshot, right.Provider.ProviderKey, rightDatabase, rightConstraints),
                options), ct);

            _allMigrationItems = script.Items;
            ApplyMigrationFilter();
            MigrationWarnings = script.Warnings;
            MigrationScript = script.Script;
            _migrationDatabase = rightDatabase;
            MigrationStatus = script.Summary +
                              $" · {watch.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture)} s, from cached metadata" +
                              $" (left {left.Snapshot.RefreshedAt.LocalDateTime:g}, right {right.Snapshot.RefreshedAt.LocalDateTime:g})";
        }
        catch (OperationCanceledException)
        {
            MigrationStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            MigrationStatus = "Could not build the script: " + ex.Message;
        }
        finally
        {
            EndOperation();
            OnPropertyChanged(nameof(MigrationDirection));
            OpenMigrationInQueryCommand.NotifyCanExecuteChanged();
        }
    }

    private static Application.Metadata.MetadataSnapshot Scoped(Application.Metadata.MetadataSnapshot snapshot, string? database) =>
        string.IsNullOrEmpty(database) || !snapshot.ContainsDatabase(database) ? snapshot : snapshot.ForDatabase(database);

    private async Task<Dictionary<DbObject, DbTableConstraints>> ReadMigrationConstraintsAsync(
        DatabaseSession session, IReadOnlyList<DbObject> tables, string side, CancellationToken ct)
    {
        var result = new Dictionary<DbObject, DbTableConstraints>();
        for (var i = 0; i < tables.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            MigrationStatus = $"Reading column defaults on the {side}: table {i + 1} of {tables.Count}…";
            try
            {
                result[tables[i]] = await session.Provider.GetTableConstraintsAsync(tables[i], ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // No permission or a table dropped meanwhile: it counts as having no defaults.
            }
        }
        return result;
    }

    /// <summary>A Query tab runs on the main window's connection, so the script opens there only when that is the right side.</summary>
    public bool RightIsMainConnection => RightSession is not null && _mainSession is not null && RightSession.Profile.Id == _mainSession.Profile.Id;

    public string OpenMigrationTip => RightIsMainConnection
        ? "Open the script in a new Query tab on the right database, to edit and run it there"
        : "The Query tab runs on the main window's connection: connect the main window to the right side's connection to open it there. Copy or Save the script meanwhile.";

    private bool CanOpenMigrationInQuery => HasMigration && RightIsMainConnection && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanOpenMigrationInQuery))]
    private void OpenMigrationInQuery() => OpenSqlRequested?.Invoke(MigrationScript, _migrationDatabase);

    [RelayCommand(CanExecute = nameof(HasMigration))]
    private async Task CopyMigrationAsync()
    {
        await dialogs.CopyTextAsync(MigrationScript);
        MigrationStatus = $"Script copied to the clipboard ({MigrationScript.Length:N0} characters).";
    }

    private void RefreshMigrationCommands()
    {
        GenerateMigrationCommand.NotifyCanExecuteChanged();
        MigrationFromOverviewCommand.NotifyCanExecuteChanged();
        OpenMigrationInQueryCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RightIsMainConnection));
        OnPropertyChanged(nameof(OpenMigrationTip));
        OnPropertyChanged(nameof(MigrationDirection));
        OnPropertyChanged(nameof(MigrationOnlySelectedLabel));
    }
}
