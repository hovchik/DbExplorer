using DbExplorer.Application.Compare;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>
/// The Comparer's navigation and guidance: which section is shown (database-wide sections and one-object sections),
/// the section's main action (the header button and F5), the setup steps shown before both sides are connected, and
/// keeping the Overview up to date when a side's connection or database changes.
/// </summary>
public partial class ComparerViewModel
{
    // ----- Sections -----

    public bool IsOverviewTab { get => SelectedTabIndex == OverviewTab; set => SelectTab(OverviewTab, value); }
    public bool IsMigrationTab { get => SelectedTabIndex == MigrationTab; set => SelectTab(MigrationTab, value); }
    public bool IsSchemaTab { get => SelectedTabIndex == SchemaTab; set => SelectTab(SchemaTab, value); }
    public bool IsStructureTab { get => SelectedTabIndex == StructureTab; set => SelectTab(StructureTab, value); }
    public bool IsDataTab { get => SelectedTabIndex == DataTab; set => SelectTab(DataTab, value); }
    public bool IsCopyTab { get => SelectedTabIndex == CopyTab; set => SelectTab(CopyTab, value); }

    /// <summary>The section works on one pair of objects, so the object pickers are shown.</summary>
    public bool IsObjectTab => SelectedTabIndex is SchemaTab or StructureTab or DataTab or CopyTab;

    /// <summary>The Data section's main action reads both tables, or compares them.</summary>
    public bool ShowCompareDataAction => IsDataTab && !IsFullDataSetsView;
    public bool ShowLoadBothAction => IsDataTab && IsFullDataSetsView;
    public bool ShowPreviewCopyAction => IsCopyTab && !IsCopyBatchMode && HasCopyActions;

    private void SelectTab(int tab, bool selected)
    {
        if (selected) SelectedTabIndex = tab;
        else RaiseTabFlags(); // a click on the selected section keeps it selected
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        RaiseTabFlags();
        RefreshHints();
    }

    private void RaiseTabFlags()
    {
        OnPropertyChanged(nameof(IsOverviewTab));
        OnPropertyChanged(nameof(IsMigrationTab));
        OnPropertyChanged(nameof(IsSchemaTab));
        OnPropertyChanged(nameof(IsStructureTab));
        OnPropertyChanged(nameof(IsDataTab));
        OnPropertyChanged(nameof(IsCopyTab));
        OnPropertyChanged(nameof(IsObjectTab));
        RaiseActionFlags();
    }

    private void RaiseActionFlags()
    {
        OnPropertyChanged(nameof(ShowCompareDataAction));
        OnPropertyChanged(nameof(ShowLoadBothAction));
        OnPropertyChanged(nameof(ShowPreviewCopyAction));
    }

    /// <summary>F5: the current section's main action (the accent button in the header), when it can run.</summary>
    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task RunSectionAsync()
    {
        CommunityToolkit.Mvvm.Input.IAsyncRelayCommand? asyncCommand = SelectedTabIndex switch
        {
            SchemaTab or StructureTab => CompareSchemaCommand,
            DataTab => IsFullDataSetsView ? LoadBothDataCommand : CompareDataCommand,
            CopyTab when !IsCopyBatchMode => PreviewCopyCommand,
            MigrationTab => GenerateMigrationCommand,
            _ => null
        };

        if (SelectedTabIndex == OverviewTab)
        {
            if (CompareOverviewCommand.CanExecute(null)) CompareOverviewCommand.Execute(null);
        }
        else if (asyncCommand is not null && asyncCommand.CanExecute(null))
        {
            await asyncCommand.ExecuteAsync(null);
        }
    }

    // ----- Setup steps (shown until both sides are connected) -----

    public bool IsSetupComplete => LeftSession is not null && RightSession is not null;

    public string LeftStepText => LeftSession is { } s
        ? $"Left (source): {s.Profile.DisplayName}"
        : "Pick the left (source) connection and Connect, or Use current";

    public string RightStepText => RightSession is { } s
        ? $"Right (target): {s.Profile.DisplayName}"
        : "Pick the right (target) connection and Connect, or Use current";

    private void RefreshSetupSteps()
    {
        OnPropertyChanged(nameof(IsSetupComplete));
        OnPropertyChanged(nameof(LeftStepText));
        OnPropertyChanged(nameof(RightStepText));
    }

    // ----- Overview kept current -----

    /// <summary>Set when a side's connection or database changed, so the Overview no longer describes the two sides.</summary>
    private bool _overviewStale;

    /// <summary>
    /// The Overview only reads the cached catalogs, so it is re-run as soon as both sides are known instead of
    /// showing the previous pair's result (or nothing) until Compare all objects is clicked.
    /// </summary>
    private void MarkOverviewStale()
    {
        _overviewStale = true;
        if (LeftSession is null || RightSession is null)
        {
            _allOverview = [];
            OverviewStatus = "";
            ApplyOverviewFilter();
        }
        AutoCompareOverview();
    }

    private void AutoCompareOverview()
    {
        if (!_overviewStale || IsBusy || LeftSession is null || RightSession is null) return;
        CompareOverview();
    }

    /// <summary>"3 selected": the Overview's action bar.</summary>
    public string OverviewSelectionText => SelectedOverviewEntries.Count switch
    {
        0 => "Select an object to open its diff, script it or copy it",
        1 => SelectedOverviewEntries[0].FullName,
        var n => $"{n:N0} objects selected"
    };

    public bool HasOverviewSelection => SelectedOverviewEntries.Count > 0;
}
