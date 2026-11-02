using System.Text.Json;

namespace DbExplorer.Application.Shortcuts;

/// <summary>The keys of every <see cref="ShortcutCommand"/>: the defaults plus the user's changes, kept in keymap.json
/// next to settings.json, and whether the Query editor uses Vim keys. Only changed commands are stored, so new
/// commands and changed defaults in later versions reach users who customized other keys.</summary>
public sealed class KeymapService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string? _file;
    private readonly Dictionary<string, IReadOnlyList<KeyChord>> _overrides = new();

    /// <summary>Defaults only, nothing saved (the render harness, tests).</summary>
    public KeymapService() { }

    public KeymapService(AppPaths paths)
    {
        _file = Path.Combine(paths.Root, "keymap.json");
        Load();
    }

    public string? FilePath => _file;

    /// <summary>Raised after any key or the Vim switch changed, so menus, the palette and the sheet show the new keys.</summary>
    public event Action? Changed;

    /// <summary>Whether the Query editor uses Vim keys (normal, insert and visual modes).</summary>
    public bool VimMode { get; private set; }

    public void SetVimMode(bool on)
    {
        if (VimMode == on) return;
        VimMode = on;
        Save();
        Changed?.Invoke();
    }

    public IReadOnlyList<KeyChord> ChordsFor(string id) =>
        _overrides.TryGetValue(id, out var chords) ? chords : ShortcutCommands.Get(id)?.Defaults ?? [];

    /// <summary>The keys as the sheet and menus show them ("F5 / Ctrl+E"), or null when the command has none.</summary>
    public string? Display(string id)
    {
        var chords = ChordsFor(id);
        return chords.Count == 0 ? null : string.Join(" / ", chords);
    }

    /// <summary>The first key of the command, for a menu item's gesture.</summary>
    public KeyChord? Primary(string id) => ChordsFor(id) is { Count: > 0 } chords ? chords[0] : null;

    public bool IsCustomized(string id) => _overrides.ContainsKey(id);

    public bool HasCustomizations => _overrides.Count > 0;

    public bool Matches(string id, KeyChord chord) => ChordsFor(id).Contains(chord);

    /// <summary>The command of <paramref name="area"/> that <paramref name="chord"/> runs, if any.</summary>
    public string? Find(string area, KeyChord chord)
    {
        foreach (var command in ShortcutCommands.All)
            if (command.Area == area && ChordsFor(command.Id).Contains(chord)) return command.Id;
        return null;
    }

    /// <summary>Other commands that already use <paramref name="chord"/> where <paramref name="id"/> works.</summary>
    public IReadOnlyList<ShortcutCommand> Conflicts(string id, KeyChord chord)
    {
        var command = ShortcutCommands.Get(id);
        if (command is null) return [];
        return ShortcutCommands.All
            .Where(other => other.Id != id && ShortcutAreas.Overlap(command.Area, other.Area) && ChordsFor(other.Id).Contains(chord))
            .ToList();
    }

    /// <summary>Why <paramref name="chord"/> cannot be a shortcut, or null when it can. A key that types a character
    /// needs Ctrl or Alt, otherwise it could never be typed again.</summary>
    public static string? Validate(KeyChord chord)
    {
        if (chord.Key is "Esc" or "Tab" or "Backspace" && chord.Mods is KeyMods.None or KeyMods.Shift)
            return $"{chord} is needed to close popups, indent or delete; add Ctrl or Alt.";
        if (chord.IsTypingKey && !chord.Ctrl && !chord.Alt)
            return $"{chord} types a character; add Ctrl or Alt.";
        if (chord.Key is "Enter" or "Delete" or "Home" or "End" or "PgUp" or "PgDn" or "↑" or "↓" or "←" or "→" && chord.Mods is KeyMods.None or KeyMods.Shift)
            return $"{chord} moves the caret or edits text; add Ctrl or Alt.";
        return null;
    }

    /// <summary>Why <paramref name="chord"/> can't be a key of <paramref name="id"/>, or null when it can (conflicts with
    /// other changeable commands are allowed: <see cref="Assign"/> takes the key away from them).</summary>
    public string? Check(string id, KeyChord chord)
    {
        if (Validate(chord) is { } error) return error;
        if (ShortcutCommands.Get(id) is not { } command) return "Unknown command.";
        foreach (var fixedKey in ShortcutCommands.Fixed)
        {
            if (!ShortcutAreas.Overlap(command.Area, fixedKey.Area)) continue;
            foreach (var part in fixedKey.Keys.Split(" / ", StringSplitOptions.TrimEntries))
                if (KeyChord.TryParse(part, out var taken) && taken == chord)
                    return $"{chord} is already {fixedKey.Action} ({fixedKey.Area}), which can't be changed.";
        }
        return null;
    }

    /// <summary>Gives <paramref name="id"/> the key <paramref name="chord"/>, either instead of its keys or in addition to
    /// them, and takes it away from every command it conflicted with.</summary>
    public void Assign(string id, KeyChord chord, bool keepExisting = false)
    {
        if (ShortcutCommands.Get(id) is null) throw new ArgumentException($"Unknown command {id}", nameof(id));
        if (Check(id, chord) is { } error) throw new ArgumentException(error, nameof(chord));
        foreach (var other in Conflicts(id, chord))
            SetChords(other.Id, ChordsFor(other.Id).Where(c => c != chord).ToList());
        var chords = keepExisting ? ChordsFor(id).Where(c => c != chord).Append(chord).ToList() : [chord];
        SetChords(id, chords);
        Save();
        Changed?.Invoke();
    }

    /// <summary>Takes one key away from a command (the command keeps its other keys, or has none).</summary>
    public void Remove(string id, KeyChord chord)
    {
        var chords = ChordsFor(id);
        if (!chords.Contains(chord)) return;
        SetChords(id, chords.Where(c => c != chord).ToList());
        Save();
        Changed?.Invoke();
    }

    /// <summary>Takes every key away from a command (it stays in the palette and menus, without keys).</summary>
    public void Clear(string id)
    {
        if (ChordsFor(id).Count == 0) return;
        SetChords(id, []);
        Save();
        Changed?.Invoke();
    }

    public void Reset(string id)
    {
        if (!_overrides.Remove(id)) return;
        Save();
        Changed?.Invoke();
    }

    public void ResetAll()
    {
        if (_overrides.Count == 0) return;
        _overrides.Clear();
        Save();
        Changed?.Invoke();
    }

    private void SetChords(string id, IReadOnlyList<KeyChord> chords)
    {
        var defaults = ShortcutCommands.Get(id)?.Defaults ?? [];
        if (chords.SequenceEqual(defaults)) _overrides.Remove(id);
        else _overrides[id] = chords;
    }

    private void Load()
    {
        if (_file is null || !File.Exists(_file)) return;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredKeymap>(File.ReadAllText(_file), Json);
            if (stored is null) return;
            VimMode = stored.VimMode;
            foreach (var (id, keys) in stored.Keys ?? [])
            {
                if (ShortcutCommands.Get(id) is null) continue;
                var chords = (keys ?? []).Select(k => KeyChord.TryParse(k, out var c) && Validate(c) is null ? c : (KeyChord?)null)
                    .OfType<KeyChord>().Distinct().ToList();
                SetChords(id, chords);
            }
        }
        catch
        {
            // A damaged file falls back to the default keys.
        }
    }

    private void Save()
    {
        if (_file is null) return;
        try
        {
            var stored = new StoredKeymap
            {
                VimMode = VimMode,
                Keys = _overrides.Count == 0 ? null : _overrides
                    .OrderBy(o => o.Key, StringComparer.Ordinal)
                    .ToDictionary(o => o.Key, o => o.Value.Select(c => c.ToString()).ToList())
            };
            AtomicFile.WriteAllText(_file, JsonSerializer.Serialize(stored, Json));
        }
        catch
        {
            // Best-effort, like settings.json.
        }
    }

    private sealed class StoredKeymap
    {
        public bool VimMode { get; set; }

        /// <summary>Changed commands only: command id to its keys (an empty list removes every key).</summary>
        public Dictionary<string, List<string>>? Keys { get; set; }
    }
}
