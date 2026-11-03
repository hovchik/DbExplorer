namespace DbExplorer.Application.Shortcuts;

[Flags]
public enum KeyMods
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4
}

/// <summary>One key with its modifiers, written the way the shortcut sheet shows it ("Ctrl+Shift+F", "Alt+↑", "F5").
/// The key is a display name: a letter, a digit, F1–F24, a named key (Enter, Space, Esc, PgUp, ↑…) or one of the
/// punctuation keys (/ = - , . ; ' [ ] \ `). On the Mac, Cmd counts as Ctrl.</summary>
public readonly record struct KeyChord(KeyMods Mods, string Key)
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Return"] = "Enter", ["Escape"] = "Esc", ["Del"] = "Delete", ["Ins"] = "Insert", ["Back"] = "Backspace",
        ["PageUp"] = "PgUp", ["PageDown"] = "PgDn", ["Prior"] = "PgUp", ["Next"] = "PgDn",
        ["Up"] = "↑", ["Down"] = "↓", ["Left"] = "←", ["Right"] = "→",
        ["Slash"] = "/", ["OemQuestion"] = "/", ["Oem2"] = "/", ["Divide"] = "/",
        ["Plus"] = "=", ["OemPlus"] = "=", ["Equals"] = "=", ["Add"] = "=",
        ["Minus"] = "-", ["OemMinus"] = "-", ["Subtract"] = "-",
        ["Comma"] = ",", ["OemComma"] = ",", ["Period"] = ".", ["OemPeriod"] = ".",
        ["Semicolon"] = ";", ["OemSemicolon"] = ";", ["Quote"] = "'", ["OemQuotes"] = "'",
        ["OpenBracket"] = "[", ["OemOpenBrackets"] = "[", ["CloseBracket"] = "]", ["OemCloseBrackets"] = "]",
        ["Backslash"] = "\\", ["OemPipe"] = "\\", ["Backtick"] = "`", ["OemTilde"] = "`"
    };

    private static readonly HashSet<string> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        "Enter", "Space", "Tab", "Esc", "Backspace", "Delete", "Insert", "Home", "End", "PgUp", "PgDn", "↑", "↓", "←", "→"
    };

    private const string Punctuation = "/=-,.;'[]\\`";

    public bool Ctrl => Mods.HasFlag(KeyMods.Ctrl);
    public bool Alt => Mods.HasFlag(KeyMods.Alt);
    public bool Shift => Mods.HasFlag(KeyMods.Shift);

    public bool IsFunctionKey => Key.Length >= 2 && Key[0] == 'F' && int.TryParse(Key.AsSpan(1), out var n) && n is >= 1 and <= 24;

    /// <summary>A key that types a character when pressed alone or with Shift (letters, digits, punctuation, Space).</summary>
    public bool IsTypingKey => Key.Length == 1 || Key == "Space";

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        parts.Add(Key);
        return string.Join('+', parts);
    }

    /// <summary>The canonical key name for <paramref name="name"/> (case, aliases), or null when it is not a key.</summary>
    public static string? NormalizeKey(string name)
    {
        name = name.Trim();
        if (name.Length == 0) return null;
        if (Aliases.TryGetValue(name, out var alias)) return alias;
        if (name.Length == 1)
        {
            var c = name[0];
            if (char.IsAsciiLetter(c)) return char.ToUpperInvariant(c).ToString();
            if (char.IsAsciiDigit(c) || Punctuation.Contains(c) || c is '↑' or '↓' or '←' or '→') return name;
            return null;
        }
        if (name.Length == 2 && name[0] == 'D' && char.IsAsciiDigit(name[1])) return name[1].ToString();
        if (name.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && name.Length == 7 && char.IsAsciiDigit(name[6])) return name[6].ToString();
        if ((name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out var n) && n is >= 1 and <= 24) return "F" + n;
        return Named.TryGetValue(name, out var named) ? named : null;
    }

    /// <summary>Reads "Ctrl+Shift+F", "alt+up", "Ctrl+=" or "Ctrl++" (the last part is the key).</summary>
    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        var mods = KeyMods.None;
        // The key can itself be "+" (written "Ctrl++"): split off modifiers from the left only.
        while (true)
        {
            var plus = text.IndexOf('+');
            if (plus <= 0 || plus == text.Length - 1) break;
            var mod = text[..plus].Trim();
            var flag = mod.ToLowerInvariant() switch
            {
                "ctrl" or "control" or "cmd" or "meta" => KeyMods.Ctrl,
                "alt" or "option" => KeyMods.Alt,
                "shift" => KeyMods.Shift,
                _ => (KeyMods?)null
            };
            if (flag is null) return false;
            mods |= flag.Value;
            text = text[(plus + 1)..];
        }
        var key = text == "+" ? "=" : NormalizeKey(text);
        if (key is null) return false;
        chord = new KeyChord(mods, key);
        return true;
    }

    public static KeyChord Parse(string text) =>
        TryParse(text, out var chord) ? chord : throw new FormatException($"Not a key: {text}");
}
