using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbExplorer.Application.Shortcuts;
using DbExplorer.Desktop.Services;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>The F1 sheet: every keyboard shortcut, grouped by where it works, with a filter on keys and text. Changeable
/// shortcuts get new keys here (press the keys, then Replace or Add), and Vim mode for the Query editor is switched here.</summary>
public partial class ShortcutSheetViewModel : ViewModelBase
{
    private readonly KeymapService _keymap;

    public ShortcutSheetViewModel() : this(Keymap.Current) { }

    public ShortcutSheetViewModel(KeymapService keymap)
    {
        _keymap = keymap;
        _groups = ShortcutCatalog.Grouped(ShortcutCatalog.Build(keymap), null);
        _keymap.Changed += OnKeymapChanged;
    }

    /// <summary>Stops following the keymap (the window closed).</summary>
    public void Detach() => _keymap.Changed -= OnKeymapChanged;

    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private IReadOnlyList<ShortcutGroup> _groups;

    public bool IsEmpty => Groups.Count == 0;

    partial void OnFilterChanged(string value) => Refresh();

    partial void OnGroupsChanged(IReadOnlyList<ShortcutGroup> value) => OnPropertyChanged(nameof(IsEmpty));

    private void Refresh() => Groups = ShortcutCatalog.Grouped(ShortcutCatalog.Build(_keymap), Filter);

    private void OnKeymapChanged()
    {
        Refresh();
        OnPropertyChanged(nameof(VimMode));
        OnPropertyChanged(nameof(HasCustomizations));
    }

    /// <summary>Vim keys in the Query editor.</summary>
    public bool VimMode
    {
        get => _keymap.VimMode;
        set => _keymap.SetVimMode(value);
    }

    public bool HasCustomizations => _keymap.HasCustomizations;

    [RelayCommand]
    private void ResetAll()
    {
        CancelEdit();
        _keymap.ResetAll();
    }

    [RelayCommand]
    private void Reset(ShortcutInfo? item)
    {
        if (item?.Id is not { } id) return;
        if (Editing?.Id == id) CancelEdit();
        _keymap.Reset(id);
    }

    // ----- Changing a shortcut: pick the row, press the keys, then Replace or Add -----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private ShortcutInfo? _editing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordedText), nameof(HasRecorded))]
    private KeyChord? _recorded;

    [ObservableProperty] private string? _editMessage;
    [ObservableProperty] private bool _editMessageIsError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AssignCommand), nameof(AddKeyCommand))]
    private bool _canAssign;

    public bool IsEditing => Editing is not null;

    public string RecordedText => Recorded?.ToString() ?? "Press the new keys…";

    public bool HasRecorded => Recorded is not null;

    [RelayCommand]
    private void StartEdit(ShortcutInfo? item)
    {
        if (item is not { IsEditable: true }) return;
        Editing = item;
        Recorded = null;
        CanAssign = false;
        EditMessageIsError = false;
        EditMessage = item.HasNoKeys ? "No keys yet." : $"Now: {item.Keys}";
    }

    /// <summary>The keys pressed while a row is being changed.</summary>
    public void Record(KeyChord chord)
    {
        if (Editing?.Id is not { } id) return;
        Recorded = chord;
        if (_keymap.Check(id, chord) is { } error)
        {
            EditMessage = error;
            EditMessageIsError = true;
            CanAssign = false;
            return;
        }
        EditMessageIsError = false;
        CanAssign = true;
        var conflicts = _keymap.Conflicts(id, chord);
        if (_keymap.Matches(id, chord)) EditMessage = $"{chord} is already a key of this command.";
        else if (conflicts.Count > 0)
            EditMessage = $"{chord} is now the key of {string.Join(", ", conflicts.Select(c => $"“{c.Action}” ({c.Area})"))}; it moves to this command.";
        else EditMessage = $"{chord} is free.";
    }

    /// <summary>The recorded key becomes the command's only key.</summary>
    [RelayCommand(CanExecute = nameof(CanAssign))]
    private void Assign() => Apply(keepExisting: false);

    /// <summary>The recorded key is added next to the command's keys.</summary>
    [RelayCommand(CanExecute = nameof(CanAssign))]
    private void AddKey() => Apply(keepExisting: true);

    private void Apply(bool keepExisting)
    {
        if (Editing?.Id is not { } id || Recorded is not { } chord || !CanAssign) return;
        _keymap.Assign(id, chord, keepExisting);
        CancelEdit();
    }

    [RelayCommand]
    private void RemoveKeys()
    {
        if (Editing?.Id is not { } id) return;
        _keymap.Clear(id);
        CancelEdit();
    }

    [RelayCommand]
    private void CancelEdit()
    {
        Editing = null;
        Recorded = null;
        EditMessage = null;
        EditMessageIsError = false;
        CanAssign = false;
    }
}
