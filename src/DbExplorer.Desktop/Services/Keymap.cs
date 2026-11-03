using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using DbExplorer.Application.Shortcuts;

namespace DbExplorer.Desktop.Services;

/// <summary>The keymap the views read their keys from. Views are created by XAML, not by the service container, so the
/// app hands its <see cref="KeymapService"/> over here at start-up; until then (tests, previews) it holds the defaults.</summary>
public static class Keymap
{
    private static KeymapService _current = new();

    public static KeymapService Current
    {
        get => _current;
        set
        {
            if (ReferenceEquals(_current, value)) return;
            _current.Changed -= RaiseChanged;
            _current = value;
            _current.Changed += RaiseChanged;
            RaiseChanged();
        }
    }

    /// <summary>Raised when the keys or the Vim switch changed, or another keymap was installed.</summary>
    public static event Action? Changed;

    private static void RaiseChanged() => Changed?.Invoke();

    /// <summary>The command of <paramref name="area"/> that the key press runs, if any.</summary>
    public static string? Find(string area, KeyEventArgs e) => Chord(e) is { } chord ? Current.Find(area, chord) : null;

    /// <summary>"F5 / Ctrl+E", or null when the command has no keys.</summary>
    public static string? Display(string id) => Current.Display(id);

    /// <summary>The key press as a chord (Cmd counts as Ctrl), or null for a lone modifier or a key shortcuts can't use.</summary>
    public static KeyChord? Chord(KeyEventArgs e) => Chord(e.Key, e.KeyModifiers);

    public static KeyChord? Chord(Key key, KeyModifiers modifiers)
    {
        var name = KeyName(key);
        if (name is null) return null;
        var mods = KeyMods.None;
        if (modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta)) mods |= KeyMods.Ctrl;
        if (modifiers.HasFlag(KeyModifiers.Alt)) mods |= KeyMods.Alt;
        if (modifiers.HasFlag(KeyModifiers.Shift)) mods |= KeyMods.Shift;
        return new KeyChord(mods, name);
    }

    public static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
        >= Key.F1 and <= Key.F24 => "F" + (key - Key.F1 + 1),
        Key.Enter => "Enter",
        Key.Space => "Space",
        Key.Tab => "Tab",
        Key.Escape => "Esc",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Insert => "Insert",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PgUp",
        Key.PageDown => "PgDn",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.Left => "←",
        Key.Right => "→",
        Key.Oem2 or Key.Divide => "/",
        Key.OemPlus or Key.Add => "=",
        Key.OemMinus or Key.Subtract => "-",
        Key.OemComma => ",",
        Key.OemPeriod or Key.Decimal => ".",
        Key.Oem1 => ";",
        Key.Oem7 => "'",
        Key.Oem4 => "[",
        Key.Oem6 => "]",
        Key.Oem5 => "\\",
        Key.Oem3 => "`",
        _ => null
    };

    public static Key? AvaloniaKey(string name) => name switch
    {
        { Length: 1 } when char.IsAsciiLetterUpper(name[0]) => Key.A + (name[0] - 'A'),
        { Length: 1 } when char.IsAsciiDigit(name[0]) => Key.D0 + (name[0] - '0'),
        "Enter" => Key.Enter,
        "Space" => Key.Space,
        "Tab" => Key.Tab,
        "Esc" => Key.Escape,
        "Backspace" => Key.Back,
        "Delete" => Key.Delete,
        "Insert" => Key.Insert,
        "Home" => Key.Home,
        "End" => Key.End,
        "PgUp" => Key.PageUp,
        "PgDn" => Key.PageDown,
        "↑" => Key.Up,
        "↓" => Key.Down,
        "←" => Key.Left,
        "→" => Key.Right,
        "/" => Key.Oem2,
        "=" => Key.OemPlus,
        "-" => Key.OemMinus,
        "," => Key.OemComma,
        "." => Key.OemPeriod,
        ";" => Key.Oem1,
        "'" => Key.Oem7,
        "[" => Key.Oem4,
        "]" => Key.Oem6,
        "\\" => Key.Oem5,
        "`" => Key.Oem3,
        _ when name.StartsWith('F') && int.TryParse(name.AsSpan(1), out var n) && n is >= 1 and <= 24 => Key.F1 + (n - 1),
        _ => null
    };

    public static KeyGesture? Gesture(KeyChord? chord)
    {
        if (chord is not { } c || AvaloniaKey(c.Key) is not { } key) return null;
        var mods = KeyModifiers.None;
        if (c.Ctrl) mods |= KeyModifiers.Control;
        if (c.Alt) mods |= KeyModifiers.Alt;
        if (c.Shift) mods |= KeyModifiers.Shift;
        return new KeyGesture(key, mods);
    }

    /// <summary>Fills <paramref name="target"/>'s KeyBindings from the keymap (every key of each command) and refills
    /// them whenever the keys change, for windows and views whose shortcuts are plain commands.</summary>
    public static void Bind(InputElement target, Func<IReadOnlyList<(string Id, ICommand? Command)>> commands)
    {
        void Fill()
        {
            target.KeyBindings.RemoveAll(b => b is KeymapBinding);
            foreach (var (id, command) in commands())
            {
                if (command is null) continue;
                foreach (var chord in Current.ChordsFor(id))
                    if (Gesture(chord) is { } gesture)
                        target.KeyBindings.Add(new KeymapBinding { Gesture = gesture, Command = command });
            }
        }

        Fill();
        void OnChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(Fill);
        target.AttachedToVisualTree += (_, _) => { Changed -= OnChanged; Changed += OnChanged; Fill(); };
        target.DetachedFromVisualTree += (_, _) => Changed -= OnChanged;
        if (target is StyledElement styled) styled.DataContextChanged += (_, _) => Fill();
    }

    private sealed class KeymapBinding : KeyBinding;

    /// <summary>Shows the command's current first key on a menu item (the keys themselves are handled by the view).</summary>
    public static readonly AttachedProperty<string?> CommandProperty =
        AvaloniaProperty.RegisterAttached<MenuItem, string?>("Command", typeof(Keymap));

    public static string? GetCommand(MenuItem item) => item.GetValue(CommandProperty);

    public static void SetCommand(MenuItem item, string? value) => item.SetValue(CommandProperty, value);

    private static void ShowGesture(MenuItem item)
    {
        if (GetCommand(item) is { } id) item.InputGesture = Gesture(Current.Primary(id));
    }

    static Keymap()
    {
        _current.Changed += RaiseChanged;
        // Menus read the key when they open, so a changed key shows the next time the menu is shown.
        CommandProperty.Changed.AddClassHandler<MenuItem>((item, _) =>
        {
            ShowGesture(item);
            item.AttachedToVisualTree -= OnMenuItemAttached;
            item.AttachedToVisualTree += OnMenuItemAttached;
        });
    }

    private static void OnMenuItemAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is MenuItem item) ShowGesture(item);
    }
}
