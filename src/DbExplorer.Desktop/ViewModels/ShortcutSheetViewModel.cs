using CommunityToolkit.Mvvm.ComponentModel;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>The F1 sheet: every keyboard shortcut, grouped by where it works, with a filter on keys and text.</summary>
public partial class ShortcutSheetViewModel : ViewModelBase
{
    public ShortcutSheetViewModel() => Groups = ShortcutCatalog.Grouped(null);

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private IReadOnlyList<ShortcutGroup> _groups;

    public bool IsEmpty => Groups.Count == 0;

    partial void OnFilterChanged(string value) => Groups = ShortcutCatalog.Grouped(value);

    partial void OnGroupsChanged(IReadOnlyList<ShortcutGroup> value) => OnPropertyChanged(nameof(IsEmpty));
}
