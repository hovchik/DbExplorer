using System.Text;
using System.Text.RegularExpressions;

namespace DbExplorer.Application.Vim;

/// <summary>Vim keys for a text editor: normal, insert, visual and visual line modes, counts, operators with motions and
/// text objects, registers, "." repeat, "/" search and a few ":" commands. Insert mode leaves typing to the editor;
/// every other key goes through <see cref="Key"/>.
///
/// <para>Keys are single characters ("d", "$", "G") or names in angle brackets: &lt;Esc&gt;, &lt;CR&gt;, &lt;BS&gt;, &lt;Del&gt;,
/// &lt;Tab&gt;, &lt;Left&gt;, &lt;Right&gt;, &lt;Up&gt;, &lt;Down&gt;, &lt;Home&gt;, &lt;End&gt;, &lt;C-r&gt;.</para></summary>
public sealed class VimEngine
{
    public const int IndentSize = 4;
    private const string InsertedTextMark = "\u0001";

    private readonly IVimHost _host;
    private readonly List<string> _pending = [];
    private string _text = "";

    private string _register = "";
    private bool _registerLinewise;
    private int _desiredColumn = -1;
    private bool _keepDesiredColumn;
    private int _visualAnchor;

    private char _findChar;
    private string? _findKind;

    private string? _lastSearch;
    private bool _lastSearchForward = true;

    private VimMode _modeBeforeCommandLine;
    private string _commandPrefix = "";
    private string _commandText = "";

    private bool _isChange;
    private List<string>? _recording;
    private List<string>? _lastChange;
    private bool _replaying;
    private int _insertStart;
    private int _insertLengthAtStart;
    private int _insertRepeat = 1;

    public VimEngine(IVimHost host) => _host = host;

    public VimMode Mode { get; private set; }

    /// <summary>What the status line shows while typing a command: ":wq", "/select".</summary>
    public string CommandLine => Mode == VimMode.CommandLine ? _commandPrefix + _commandText : "";

    /// <summary>Keys typed so far of an unfinished command ("2d", "f"), like Vim's showcmd.</summary>
    public string PendingKeys => string.Concat(_pending.Select(k => k.Length == 1 ? k : ""));

    /// <summary>The last message ("Pattern not found: x", "3 substitutions on 2 lines"); cleared by the next key.</summary>
    public string? Message { get; private set; }

    /// <summary>The unnamed register (what y, d and c keep and p puts).</summary>
    public string Register => _register;

    public bool RegisterIsLinewise => _registerLinewise;

    /// <summary>Raised after every key that changed the mode, the pending keys, the command line or the message.</summary>
    public event Action? StateChanged;

    /// <summary>":w", ":q", ":wq" for the editor to carry out.</summary>
    public event Action<VimExCommand>? ExCommand;

    /// <summary>Goes back to normal mode without touching the text (another tab was shown, Vim mode was turned on).</summary>
    public void Reset()
    {
        _pending.Clear();
        _recording = null;
        Message = null;
        if (Mode is VimMode.Visual or VimMode.VisualLine) _host.ClearSelection();
        Mode = VimMode.Normal;
        StateChanged?.Invoke();
    }

    /// <summary>Handles one key. False means the editor should handle it (any key in insert mode except Esc).</summary>
    public bool Key(string key)
    {
        if (Mode == VimMode.Insert)
        {
            if (key is not ("<Esc>" or "<C-[>")) return false;
            LeaveInsert();
            StateChanged?.Invoke();
            return true;
        }

        _text = _host.Text;
        if (!_replaying) Message = null;
        if (Mode == VimMode.CommandLine)
        {
            CommandLineKey(key);
            StateChanged?.Invoke();
            return true;
        }

        if (key == "<C-[>") key = "<Esc>";
        if (Mode == VimMode.Normal && _pending.Count == 0 && _host.MouseSelection is { } sel && sel.End > sel.Start)
        {
            // A selection made with the mouse acts like one made with v.
            Mode = VimMode.Visual;
            _visualAnchor = sel.Start;
            _host.Caret = Math.Max(sel.Start, sel.End - 1);
        }

        _pending.Add(key);
        var keys = _pending.ToList();
        var done = true;
        _isChange = false;
        _keepDesiredColumn = false;
        var changing = false;
        try
        {
            if (Mode is VimMode.Visual or VimMode.VisualLine) RunVisual(new KeyReader(keys));
            else RunNormal(new KeyReader(keys), () => { if (!changing) { changing = true; _host.BeginChange(); } });
        }
        catch (IncompleteCommand)
        {
            done = false;
        }
        catch (InvalidCommand)
        {
            // Unknown or impossible (no match for f, no line below for j): nothing happens, like Vim's beep.
        }
        finally
        {
            if (changing) _host.EndChange();
        }

        if (done)
        {
            _pending.Clear();
            if (_isChange && !_replaying)
            {
                if (Mode == VimMode.Insert) _recording = keys;
                else _lastChange = keys;
            }
            if (!_keepDesiredColumn) _desiredColumn = -1;
            if (Mode == VimMode.Normal) ClampNormalCaret();
            else if (Mode is VimMode.Visual or VimMode.VisualLine) ShowVisual();
        }
        StateChanged?.Invoke();
        return true;
    }

    // ================================================================= normal mode

    private void RunNormal(KeyReader keys, Action beginChange)
    {
        var count1 = ReadCount(keys);
        var count = count1 ?? 1;
        var caret = _host.Caret;
        var key = keys.Next();

        void Change()
        {
            _isChange = true;
            beginChange();
        }

        switch (key)
        {
            case "<Esc>":
                return;
            case "i":
                Change();
                StartInsert(caret, count);
                return;
            case "a":
                Change();
                StartInsert(Math.Min(caret + (IsLineEmpty(caret) ? 0 : 1), LineContentEnd(caret)), count);
                return;
            case "I":
                Change();
                StartInsert(FirstNonBlank(caret), count);
                return;
            case "A":
                Change();
                StartInsert(LineContentEnd(caret), count);
                return;
            case "o":
            case "O":
            {
                Change();
                var indent = Indentation(caret);
                if (key == "o")
                {
                    var end = LineContentEnd(caret);
                    Replace(end, 0, Newline + indent);
                    StartInsert(end + Newline.Length + indent.Length, 1);
                }
                else
                {
                    var start = LineStart(caret);
                    Replace(start, 0, indent + Newline);
                    StartInsert(start + indent.Length, 1);
                }
                return;
            }
            case "v":
                EnterVisual(VimMode.Visual, caret);
                return;
            case "V":
                EnterVisual(VimMode.VisualLine, caret);
                return;
            case "x":
            case "<Del>":
            {
                if (IsLineEmpty(caret)) return;
                Change();
                var end = Math.Min(caret + count, LineContentEnd(caret));
                Delete(caret, end, linewise: false);
                return;
            }
            case "X":
            {
                var start = Math.Max(LineStart(caret), caret - count);
                if (start == caret) return;
                Change();
                Delete(start, caret, linewise: false);
                _host.Caret = start;
                return;
            }
            case "D":
                Change();
                ApplyOperator("d", ToLineEnd(caret, count), caret);
                return;
            case "C":
                Change();
                ApplyOperator("c", ToLineEnd(caret, count), caret);
                return;
            case "s":
            {
                Change();
                var end = Math.Min(caret + count, LineContentEnd(caret));
                ApplyOperator("c", new Range(caret, end, false), caret);
                return;
            }
            case "S":
                Change();
                ApplyOperator("c", LinesRange(LineOf(caret), LineOf(caret) + count - 1), caret);
                return;
            case "Y":
                ApplyOperator("y", LinesRange(LineOf(caret), LineOf(caret) + count - 1), caret);
                return;
            case "p":
            case "P":
                Change();
                Put(after: key == "p", count);
                return;
            case "r":
            {
                var replacement = keys.Next();
                var ch = replacement switch { "<CR>" => "\n", { Length: 1 } => replacement, _ => throw new InvalidCommand() };
                if (caret + count > LineContentEnd(caret)) throw new InvalidCommand();
                Change();
                if (ch == "\n")
                {
                    Replace(caret, count, Newline);
                    _host.Caret = caret + Newline.Length;
                }
                else
                {
                    Replace(caret, count, new string(ch[0], count));
                    _host.Caret = caret + count - 1;
                }
                return;
            }
            case "J":
                Change();
                JoinLines(LineOf(caret), Math.Max(2, count), spaces: true);
                return;
            case "~":
            {
                if (IsLineEmpty(caret)) return;
                Change();
                var end = Math.Min(caret + count, LineContentEnd(caret));
                Replace(caret, end - caret, ToggleCase(_text[caret..end]));
                _host.Caret = Math.Min(end, LineContentEnd(caret) - 1);
                return;
            }
            case "u":
                for (var i = 0; i < count; i++) _host.Undo();
                return;
            case "<C-r>":
                for (var i = 0; i < count; i++) _host.Redo();
                return;
            case ".":
                beginChange();
                RepeatLastChange(count1);
                _isChange = false;
                return;
            case ":":
                EnterCommandLine(":", count1 is { } n ? $".,.+{n - 1}" : "");
                return;
            case "/":
            case "?":
                EnterCommandLine(key, "");
                return;
            case "d":
            case "c":
            case "y":
            case ">":
            case "<":
                if (key != "y") Change();
                Operator(key, keys, count1, caret);
                return;
            case "g":
            {
                var next = keys.Peek();
                if (next is "~" or "u" or "U")
                {
                    keys.Next();
                    Change();
                    Operator("g" + next, keys, count1, caret);
                    return;
                }
                if (next == "J")
                {
                    keys.Next();
                    Change();
                    JoinLines(LineOf(caret), Math.Max(2, count), spaces: false);
                    return;
                }
                break;
            }
        }

        // Anything else is a motion that moves the caret.
        keys.Back();
        var motion = Motion(keys, count, count1 is not null, caret, forOperator: false);
        _host.Caret = motion.Linewise && motion.FirstNonBlank ? FirstNonBlank(motion.Target) : motion.Target;
    }

    private void Operator(string op, KeyReader keys, int? count1, int caret)
    {
        var count2 = ReadCount(keys);
        var hasCount = count1 is not null || count2 is not null;
        var count = (count1 ?? 1) * (count2 ?? 1);
        var key = keys.Next();
        Range range;
        if (key == op[^1].ToString() || (op.Length == 2 && key == "g" && keys.Peek() == op[1].ToString() && keys.Next().Length > 0))
        {
            var line = LineOf(caret);
            range = LinesRange(line, line + count - 1);
        }
        else if (key is "i" or "a")
        {
            range = TextObject(key == "a", keys.Next(), caret, count) ?? throw new InvalidCommand();
        }
        else
        {
            keys.Back();
            // cw on a word changes to its end, like ce (Vim's special case).
            if (op == "c" && keys.Peek() is "w" or "W" && caret < _text.Length && !IsBlank(_text[caret]))
            {
                var big = keys.Next() == "W";
                var end = caret;
                for (var i = 0; i < count; i++) end = WordEnd(i == 0 ? end - 1 : end, big);
                range = new Range(caret, Math.Min(end + 1, _text.Length), false);
            }
            else
            {
                var motion = Motion(keys, count, hasCount, caret, forOperator: true);
                range = motion.Linewise
                    ? LinesRange(Math.Min(LineOf(caret), LineOf(motion.Target)), Math.Max(LineOf(caret), LineOf(motion.Target)))
                    : new Range(Math.Min(caret, motion.Target), Math.Max(caret, motion.Target) + (motion.Inclusive ? 1 : 0), false);
                range = range with { End = Math.Min(range.End, _text.Length) };
            }
        }
        ApplyOperator(op, range, caret);
    }

    private void ApplyOperator(string op, Range range, int caret)
    {
        switch (op)
        {
            case "y":
                Yank(range);
                if (!range.Linewise) _host.Caret = range.Start;
                else if (LineOf(caret) > LineOf(range.Start)) _host.Caret = LineStart(range.Start) + Math.Min(Column(caret), LineLength(range.Start));
                break;
            case "d":
                Delete(range.Start, range.End, range.Linewise);
                break;
            case "c":
                if (range.Linewise)
                {
                    Yank(range);
                    var indent = Indentation(range.Start);
                    var lastLineEnd = LineContentEnd(Math.Max(range.Start, range.End - 1));
                    Replace(range.Start, lastLineEnd - range.Start, indent);
                    StartInsert(range.Start + indent.Length, 1);
                }
                else
                {
                    Yank(range);
                    Replace(range.Start, range.End - range.Start, "");
                    StartInsert(range.Start, 1);
                }
                break;
            case ">":
            case "<":
                Indent(LineOf(range.Start), LineOf(Math.Max(range.Start, range.End - 1)), op == ">");
                break;
            case "g~":
            case "gu":
            case "gU":
            {
                var end = range.Linewise ? LineContentEnd(Math.Max(range.Start, range.End - 1)) : range.End;
                var text = _text[range.Start..end];
                Replace(range.Start, end - range.Start, op switch
                {
                    "gu" => text.ToLowerInvariant(),
                    "gU" => text.ToUpperInvariant(),
                    _ => ToggleCase(text)
                });
                _host.Caret = range.Start;
                break;
            }
        }
    }

    // ================================================================= visual mode

    private void EnterVisual(VimMode mode, int caret)
    {
        Mode = mode;
        _visualAnchor = caret;
    }

    private void RunVisual(KeyReader keys)
    {
        var count1 = ReadCount(keys);
        var count = count1 ?? 1;
        var caret = _host.Caret;
        var key = keys.Next();
        var range = VisualRange(caret);

        void Leave()
        {
            Mode = VimMode.Normal;
            _host.ClearSelection();
        }

        void Edit(Action edit)
        {
            _host.BeginChange();
            try { edit(); }
            finally { _host.EndChange(); }
        }

        switch (key)
        {
            case "<Esc>":
                Leave();
                _host.Caret = caret;
                return;
            case "v":
            case "V":
            {
                var mode = key == "v" ? VimMode.Visual : VimMode.VisualLine;
                if (Mode == mode) { Leave(); _host.Caret = caret; }
                else Mode = mode;
                return;
            }
            case "o":
                (_visualAnchor, caret) = (caret, _visualAnchor);
                _host.Caret = caret;
                return;
            case "d":
            case "x":
            case "<Del>":
            case "X":
            case "D":
            case "y":
            case "Y":
            case "c":
            case "s":
            case "C":
            case "S":
            case "R":
            {
                var linewise = key is "X" or "D" or "Y" or "C" or "S" or "R";
                var r = linewise && !range.Linewise ? LinesRange(LineOf(range.Start), LineOf(Math.Max(range.Start, range.End - 1))) : range;
                var op = key switch { "y" or "Y" => "y", "c" or "s" or "C" or "S" or "R" => "c", _ => "d" };
                Leave();
                if (op == "y") ApplyOperator("y", r, r.Start);
                else Edit(() => ApplyOperator(op, r, r.Start));
                if (op == "y") _host.Caret = r.Start;
                return;
            }
            case ">":
            case "<":
                Leave();
                Edit(() =>
                {
                    for (var i = 0; i < count; i++) Indent(LineOf(range.Start), LineOf(Math.Max(range.Start, range.End - 1)), key == ">");
                });
                _host.Caret = FirstNonBlank(LineStartOf(LineOf(range.Start)));
                return;
            case "~":
            case "u":
            case "U":
                Leave();
                Edit(() => ApplyOperator(key switch { "u" => "gu", "U" => "gU", _ => "g~" }, range, range.Start));
                return;
            case "J":
            {
                var first = LineOf(range.Start);
                var last = LineOf(Math.Max(range.Start, range.End - 1));
                Leave();
                Edit(() => JoinLines(first, Math.Max(2, last - first + 1), spaces: true));
                return;
            }
            case "p":
            case "P":
            {
                var register = _register;
                var registerLinewise = _registerLinewise;
                Leave();
                Edit(() =>
                {
                    Delete(range.Start, range.End, range.Linewise);
                    _text = _host.Text;
                    if (range.Linewise && !registerLinewise)
                    {
                        Replace(range.Start, 0, register + Newline);
                        _host.Caret = range.Start;
                    }
                    else if (registerLinewise)
                    {
                        var insertAt = range.Linewise ? Math.Min(range.Start, _text.Length) : range.Start;
                        var text = ToDocumentNewlines(register);
                        if (!range.Linewise) text = Newline + text.TrimEnd('\n', '\r') + Newline;
                        Replace(insertAt, 0, text);
                        _host.Caret = FirstNonBlank(insertAt + (range.Linewise ? 0 : Newline.Length));
                    }
                    else
                    {
                        Replace(range.Start, 0, ToDocumentNewlines(register));
                        _host.Caret = range.Start + Math.Max(0, register.Length - 1);
                    }
                });
                // Vim keeps the replaced text in the register.
                return;
            }
            case "r":
            {
                var ch = keys.Next();
                if (ch.Length != 1) throw new InvalidCommand();
                var end = range.Linewise ? LineContentEnd(Math.Max(range.Start, range.End - 1)) : range.End;
                var replaced = new StringBuilder();
                foreach (var c in _text[range.Start..end]) replaced.Append(c is '\r' or '\n' ? c : ch[0]);
                Leave();
                Edit(() => Replace(range.Start, end - range.Start, replaced.ToString()));
                _host.Caret = range.Start;
                return;
            }
            case "i":
            case "a":
            {
                var obj = TextObject(key == "a", keys.Next(), caret, count) ?? throw new InvalidCommand();
                if (Mode == VimMode.VisualLine) Mode = VimMode.Visual;
                _visualAnchor = obj.Start;
                _host.Caret = Math.Max(obj.Start, obj.End - 1);
                return;
            }
            case ":":
                EnterCommandLine(":", "'<,'>");
                return;
            case "/":
            case "?":
                EnterCommandLine(key, "");
                return;
        }

        keys.Back();
        var motion = Motion(keys, count, count1 is not null, caret, forOperator: false);
        _host.Caret = motion.Linewise && motion.FirstNonBlank ? FirstNonBlank(motion.Target) : motion.Target;
    }

    private Range VisualRange(int caret)
    {
        var start = Math.Min(_visualAnchor, caret);
        var end = Math.Max(_visualAnchor, caret);
        start = Math.Clamp(start, 0, _text.Length);
        end = Math.Clamp(end, 0, _text.Length);
        if (Mode == VimMode.VisualLine) return LinesRange(LineOf(start), LineOf(end));
        return new Range(start, Math.Min(end + 1, _text.Length), false);
    }

    private void ShowVisual()
    {
        _text = _host.Text;
        var caret = Math.Clamp(_host.Caret, 0, _text.Length);
        _visualAnchor = Math.Clamp(_visualAnchor, 0, _text.Length);
        var range = VisualRange(caret);
        var end = range.End;
        if (Mode == VimMode.VisualLine) end = LineContentEnd(Math.Max(range.Start, range.End - 1));
        _host.Select(range.Start, end, caret);
    }

    // ================================================================= insert mode

    private void StartInsert(int at, int repeat)
    {
        _host.ClearSelection();
        _host.Caret = at;
        Mode = VimMode.Insert;
        _insertStart = at;
        _text = _host.Text;
        _insertLengthAtStart = _text.Length;
        _insertRepeat = Math.Max(1, repeat);
    }

    private void LeaveInsert()
    {
        _text = _host.Text;
        var caret = _host.Caret;
        var added = _text.Length - _insertLengthAtStart;
        var inserted = added > 0 && _insertStart + added <= _text.Length && caret >= _insertStart && caret <= _insertStart + added
            ? _text.Substring(_insertStart, added)
            : "";

        if (_insertRepeat > 1 && inserted.Length > 0)
        {
            var more = string.Concat(Enumerable.Repeat(inserted, _insertRepeat - 1));
            _host.BeginChange();
            try { Replace(caret, 0, more); }
            finally { _host.EndChange(); }
            caret += more.Length;
        }
        _insertRepeat = 1;

        if (_recording is not null)
        {
            _recording.Add(InsertedTextMark + inserted);
            _lastChange = _recording;
            _recording = null;
        }

        Mode = VimMode.Normal;
        _text = _host.Text;
        _host.Caret = Math.Max(LineStart(Math.Min(caret, _text.Length)), caret - 1);
        ClampNormalCaret();
    }

    // ================================================================= "." repeat

    private void RepeatLastChange(int? count)
    {
        if (_lastChange is null || _replaying) return;
        var keys = _lastChange.ToList();
        if (count is not null)
        {
            // A count replaces the count of the original command.
            var skip = 0;
            while (skip < keys.Count && keys[skip].Length == 1 && char.IsAsciiDigit(keys[skip][0]) && !(skip == 0 && keys[0] == "0")) skip++;
            keys = [.. count.Value.ToString().Select(c => c.ToString()), .. keys.Skip(skip)];
        }

        _replaying = true;
        _pending.Clear();
        try
        {
            // The replayed command opens its own undo group; this one is already open, so the whole repeat is one step.
            foreach (var key in keys)
            {
                if (key.StartsWith(InsertedTextMark, StringComparison.Ordinal))
                {
                    var text = key[InsertedTextMark.Length..];
                    if (Mode == VimMode.Insert)
                    {
                        var at = _host.Caret;
                        _host.Replace(at, 0, text);
                        _host.Caret = at + text.Length;
                        LeaveInsert();
                    }
                    continue;
                }
                Key(key);
            }
            if (Mode == VimMode.Insert) LeaveInsert();
        }
        finally
        {
            _replaying = false;
            _pending.Clear();
            _text = _host.Text;
        }
    }

    // ================================================================= command line

    private void EnterCommandLine(string prefix, string initial)
    {
        _modeBeforeCommandLine = Mode;
        _commandPrefix = prefix;
        _commandText = initial;
        Mode = VimMode.CommandLine;
    }

    private void CommandLineKey(string key)
    {
        switch (key)
        {
            case "<Esc>":
            case "<C-[>":
                CloseCommandLine();
                return;
            case "<BS>":
                if (_commandText.Length == 0) CloseCommandLine();
                else _commandText = _commandText[..^1];
                return;
            case "<CR>":
            {
                var prefix = _commandPrefix;
                var text = _commandText;
                CloseCommandLine();
                var visual = Mode is VimMode.Visual or VimMode.VisualLine;
                if (prefix == ":")
                {
                    if (visual && !text.StartsWith("'<,'>", StringComparison.Ordinal)) { Mode = VimMode.Normal; _host.ClearSelection(); }
                    RunEx(text.Trim());
                    if (Mode is VimMode.Visual or VimMode.VisualLine) { Mode = VimMode.Normal; _host.ClearSelection(); }
                }
                else
                {
                    if (text.Length > 0) { _lastSearch = text; _searchWholeWord = false; }
                    _lastSearchForward = prefix == "/";
                    var target = Search(_host.Caret, forward: true, 1);
                    if (target is { } t) _host.Caret = t;
                    if (visual) ShowVisual();
                }
                if (Mode == VimMode.Normal) ClampNormalCaret();
                return;
            }
            default:
                if (key == "<Tab>") _commandText += "\t";
                else if (key.Length == 1) _commandText += key;
                return;
        }
    }

    private void CloseCommandLine()
    {
        Mode = _modeBeforeCommandLine;
        _commandText = "";
        _commandPrefix = "";
        if (Mode is VimMode.Visual or VimMode.VisualLine) ShowVisual();
    }

    private static readonly Regex RangePattern = new(@"^(?<range>%|'<,'>|(?<a>\.|\$|\d+)(?:\s*,\s*(?<b>\.(?:\+\d+)?|\$|\d+))?)?\s*(?<cmd>.*)$", RegexOptions.Compiled);

    private void RunEx(string line)
    {
        _text = _host.Text;
        var m = RangePattern.Match(line);
        var cmd = m.Groups["cmd"].Value.Trim();
        var current = LineOf(_host.Caret);
        int first = current, last = current;
        var hasRange = m.Groups["range"].Success && m.Groups["range"].Length > 0;
        if (hasRange)
        {
            var r = m.Groups["range"].Value;
            if (r == "%") (first, last) = (0, LastLine);
            else if (r == "'<,'>")
            {
                var range = VisualRange(_host.Caret);
                (first, last) = (LineOf(range.Start), LineOf(Math.Max(range.Start, range.End - 1)));
            }
            else
            {
                first = LineNumber(m.Groups["a"].Value, current);
                last = m.Groups["b"].Success ? LineNumber(m.Groups["b"].Value, current) : first;
            }
            if (first > last) (first, last) = (last, first);
            first = Math.Clamp(first, 0, LastLine);
            last = Math.Clamp(last, 0, LastLine);
        }

        if (cmd.Length == 0)
        {
            if (hasRange) _host.Caret = FirstNonBlank(LineStartOf(last));
            return;
        }

        switch (cmd)
        {
            case "w":
            case "w!":
            case "write":
                ExCommand?.Invoke(VimExCommand.Save);
                return;
            case "q":
            case "q!":
            case "quit":
            case "close":
                ExCommand?.Invoke(VimExCommand.Close);
                return;
            case "wq":
            case "wq!":
            case "x":
            case "xit":
                ExCommand?.Invoke(VimExCommand.SaveAndClose);
                return;
            case "noh":
            case "nohlsearch":
                return;
            case "d":
            case "delete":
                _host.BeginChange();
                try { Delete(LineStartOf(first), 0, linewise: true, lastLine: last); }
                finally { _host.EndChange(); }
                return;
            case "y":
            case "yank":
                Yank(LinesRange(first, last));
                return;
            case ">":
            case "<":
                _host.BeginChange();
                try { Indent(first, last, cmd == ">"); }
                finally { _host.EndChange(); }
                return;
        }

        if (cmd.Length > 1 && cmd[0] == 's' && !char.IsLetterOrDigit(cmd[1]) && !char.IsWhiteSpace(cmd[1]))
        {
            Substitute(cmd[1..], first, last);
            return;
        }
        Message = $"Not an editor command: {cmd}";
    }

    private int LineNumber(string spec, int current)
    {
        if (spec == "$") return LastLine;
        if (spec.StartsWith('.'))
            return spec.Length > 2 && int.TryParse(spec.AsSpan(2), out var plus) ? current + plus : current;
        return int.TryParse(spec, out var n) ? Math.Max(0, n - 1) : current;
    }

    /// <summary>":s/pattern/replacement/flags" on lines first..last. The pattern is a .NET regular expression; \1 and &amp;
    /// in the replacement work as in Vim; flags g (every match on a line) and i (ignore case).</summary>
    private void Substitute(string spec, int first, int last)
    {
        var delimiter = spec[0];
        var parts = SplitUnescaped(spec[1..], delimiter);
        if (parts.Count < 1 || parts[0].Length == 0 && _lastSearch is null) { Message = "Usage: :s/pattern/replacement/g"; return; }
        var pattern = parts[0].Length == 0 ? Regex.Escape(_lastSearch!) : parts[0];
        var replacement = parts.Count > 1 ? VimReplacement(parts[1]) : "";
        var flags = parts.Count > 2 ? parts[2] : "";
        var options = RegexOptions.None;
        if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        Regex regex;
        try { regex = new Regex(pattern, options, TimeSpan.FromSeconds(2)); }
        catch (ArgumentException ex) { Message = "Bad pattern: " + ex.Message; return; }

        var start = LineStartOf(first);
        var end = LineContentEnd(LineStartOf(last));
        var count = 0;
        var lines = 0;
        var sb = new StringBuilder();
        var lineStart = start;
        try
        {
            while (true)
            {
                var lineEnd = LineContentEnd(lineStart);
                var lineText = _text[lineStart..lineEnd];
                var hits = 0;
                var replaced = flags.Contains('g')
                    ? regex.Replace(lineText, m => { hits++; return m.Result(replacement); })
                    : regex.Replace(lineText, m => { hits++; return m.Result(replacement); }, 1);
                if (hits > 0) { count += hits; lines++; }
                sb.Append(replaced);
                if (lineEnd >= end) break;
                var next = NextLineStart(lineStart);
                sb.Append(_text, lineEnd, next - lineEnd);
                lineStart = next;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            Message = "The pattern took too long";
            return;
        }

        if (count == 0) { Message = $"Pattern not found: {parts[0]}"; return; }
        _host.BeginChange();
        try { Replace(start, end - start, sb.ToString()); }
        finally { _host.EndChange(); }
        _text = _host.Text;
        _host.Caret = FirstNonBlank(LineStartOf(Math.Min(last, LastLine)));
        Message = $"{count} substitution{(count == 1 ? "" : "s")} on {lines} line{(lines == 1 ? "" : "s")}";
    }

    private static List<string> SplitUnescaped(string text, char delimiter)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == delimiter) { sb.Append(delimiter); i++; }
            else if (text[i] == delimiter) { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(text[i]);
        }
        parts.Add(sb.ToString());
        return parts;
    }

    /// <summary>Vim's replacement syntax (\1, &amp;, \n, \&amp;) as a .NET one.</summary>
    private static string VimReplacement(string text)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length)
            {
                var n = text[++i];
                if (char.IsAsciiDigit(n)) sb.Append("${").Append(n).Append('}');
                else if (n == 'n' || n == 'r') sb.Append('\n');
                else if (n == 't') sb.Append('\t');
                else if (n == '$') sb.Append("$$");
                else sb.Append(n);
            }
            else if (c == '&') sb.Append("$0");
            else if (c == '$') sb.Append("$$");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // ================================================================= motions

    private readonly record struct MotionResult(int Target, bool Inclusive, bool Linewise, bool FirstNonBlank = false);

    private MotionResult Motion(KeyReader keys, int count, bool hasCount, int caret, bool forOperator)
    {
        var key = keys.Next();
        var lineEnd = LineContentEnd(caret);
        switch (key)
        {
            case "h":
            case "<Left>":
            case "<BS>":
            {
                var target = Math.Max(LineStart(caret), caret - count);
                if (target == caret && !forOperator) throw new InvalidCommand();
                return new MotionResult(target, false, false);
            }
            case "l":
            case "<Right>":
            case " ":
            {
                var max = forOperator ? lineEnd : Math.Max(LineStart(caret), lineEnd - 1);
                return new MotionResult(Math.Min(caret + count, Math.Max(max, caret)), false, false);
            }
            case "j":
            case "<Down>":
            case "k":
            case "<Up>":
            {
                var down = key is "j" or "<Down>";
                var line = LineOf(caret);
                var targetLine = down ? line + count : line - count;
                if ((down && line >= LastLine) || (!down && line == 0)) throw new InvalidCommand();
                targetLine = Math.Clamp(targetLine, 0, LastLine);
                if (_desiredColumn < 0) _desiredColumn = Column(caret);
                _keepDesiredColumn = true;
                var start = LineStartOf(targetLine);
                var length = LineLength(start);
                var column = Math.Min(_desiredColumn, Math.Max(0, length - (forOperator ? 0 : 1)));
                return new MotionResult(start + column, false, true);
            }
            case "+":
            case "<CR>":
            case "-":
            {
                var line = LineOf(caret);
                var targetLine = Math.Clamp(key == "-" ? line - count : line + count, 0, LastLine);
                if (targetLine == line) throw new InvalidCommand();
                return new MotionResult(LineStartOf(targetLine), false, true, FirstNonBlank: true);
            }
            case "0":
            case "<Home>":
                return new MotionResult(LineStart(caret), false, false);
            case "^":
                return new MotionResult(FirstNonBlank(caret), false, false);
            case "$":
            case "<End>":
            {
                var line = Math.Min(LineOf(caret) + count - 1, LastLine);
                var start = LineStartOf(line);
                var end = LineContentEnd(start);
                _desiredColumn = int.MaxValue;
                _keepDesiredColumn = true;
                return new MotionResult(Math.Max(start, end - 1), forOperator && end > start, false);
            }
            case "w":
            case "W":
            {
                var big = key == "W";
                var target = caret;
                for (var i = 0; i < count; i++) target = NextWordStart(target, big);
                if (forOperator && LineOf(target) > LineOf(caret))
                {
                    // dw on the last word of a line stops at the line end instead of eating the line break.
                    target = Math.Max(caret, LineContentEnd(LineStartOf(LineOf(target) - 1)));
                }
                if (!forOperator) target = Math.Min(target, Math.Max(0, _text.Length - 1));
                return new MotionResult(target, false, false);
            }
            case "b":
            case "B":
            {
                var target = caret;
                for (var i = 0; i < count; i++) target = PreviousWordStart(target, key == "B");
                return new MotionResult(target, false, false);
            }
            case "e":
            case "E":
            {
                var target = caret;
                for (var i = 0; i < count; i++) target = WordEnd(target, key == "E");
                return new MotionResult(target, true, false);
            }
            case "G":
            {
                var line = hasCount ? Math.Clamp(count - 1, 0, LastLine) : LastLine;
                return new MotionResult(LineStartOf(line), false, true, FirstNonBlank: true);
            }
            case "g":
            {
                var next = keys.Next();
                switch (next)
                {
                    case "g":
                        return new MotionResult(LineStartOf(hasCount ? Math.Clamp(count - 1, 0, LastLine) : 0), false, true, FirstNonBlank: true);
                    case "e":
                    case "E":
                    {
                        var target = caret;
                        for (var i = 0; i < count; i++) target = PreviousWordEnd(target, next == "E");
                        return new MotionResult(target, true, false);
                    }
                    case "_":
                    {
                        var start = LineStartOf(Math.Min(LineOf(caret) + count - 1, LastLine));
                        var end = LineContentEnd(start);
                        while (end > start && IsBlank(_text[end - 1])) end--;
                        return new MotionResult(Math.Max(start, end - 1), true, false);
                    }
                    default:
                        throw new InvalidCommand();
                }
            }
            case "_":
                return new MotionResult(LineStartOf(Math.Min(LineOf(caret) + count - 1, LastLine)), false, true, FirstNonBlank: true);
            case "f":
            case "F":
            case "t":
            case "T":
            {
                var ch = keys.Next();
                if (ch.Length != 1) throw new InvalidCommand();
                _findChar = ch[0];
                _findKind = key;
                return FindOnLine(key, ch[0], caret, count, repeat: false);
            }
            case ";":
            case ",":
            {
                if (_findKind is null) throw new InvalidCommand();
                var kind = key == ";" ? _findKind : _findKind switch { "f" => "F", "F" => "f", "t" => "T", _ => "t" };
                return FindOnLine(kind, _findChar, caret, count, repeat: true);
            }
            case "%":
            {
                if (hasCount)
                {
                    var line = Math.Clamp((count * (LastLine + 1) + 99) / 100 - 1, 0, LastLine);
                    return new MotionResult(LineStartOf(line), false, true, FirstNonBlank: true);
                }
                return new MotionResult(MatchBracket(caret) ?? throw new InvalidCommand(), true, false);
            }
            case "}":
            case "{":
            {
                var target = caret;
                for (var i = 0; i < count; i++) target = key == "}" ? NextParagraph(target) : PreviousParagraph(target);
                if (!forOperator && key == "}" && target >= _text.Length) target = Math.Max(0, _text.Length - 1);
                return new MotionResult(target, false, false);
            }
            case "n":
            case "N":
            {
                if (_lastSearch is null) { Message = "No previous search"; throw new InvalidCommand(); }
                return new MotionResult(Search(caret, key == "n", count) ?? throw new InvalidCommand(), false, false);
            }
            case "*":
            case "#":
            {
                var (start, end) = WordAt(caret);
                if (end <= start) throw new InvalidCommand();
                _lastSearch = _text[start..end];
                _lastSearchForward = key == "*";
                _searchWholeWord = true;
                return new MotionResult(Search(start, true, count) ?? throw new InvalidCommand(), false, false);
            }
        }
        throw new InvalidCommand();
    }

    private MotionResult FindOnLine(string kind, char ch, int caret, int count, bool repeat)
    {
        var start = LineStart(caret);
        var end = LineContentEnd(caret);
        var forward = kind is "f" or "t";
        var till = kind is "t" or "T";
        var pos = caret;
        for (var i = 0; i < count; i++)
        {
            // Repeating t/T from just before the character would find the same one again: skip it.
            var from = forward ? pos + 1 + (till && repeat && i == 0 ? 1 : 0) : pos - 1 - (till && repeat && i == 0 ? 1 : 0);
            pos = -1;
            if (forward)
            {
                for (var p = from; p < end; p++) if (_text[p] == ch) { pos = p; break; }
            }
            else
            {
                for (var p = from; p >= start; p--) if (_text[p] == ch) { pos = p; break; }
            }
            if (pos < 0) throw new InvalidCommand();
        }
        if (till) pos += forward ? -1 : 1;
        return new MotionResult(pos, forward, false);
    }

    private bool _searchWholeWord;

    /// <summary>The next match of the last search after (or before) <paramref name="caret"/>, wrapping around the text.
    /// Plain text; ignores case unless the pattern has capitals (Vim's smartcase).</summary>
    private int? Search(int caret, bool forward, int count)
    {
        if (string.IsNullOrEmpty(_lastSearch)) return null;
        var direction = forward == _lastSearchForward;
        var comparison = _lastSearch.Any(char.IsUpper) && !_searchWholeWord ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (_searchWholeWord) comparison = StringComparison.Ordinal;
        var pos = caret;
        for (var i = 0; i < count; i++)
        {
            var found = direction ? FindForward(pos + 1, comparison) : FindBackward(pos - 1, comparison);
            if (found is null)
            {
                Message = $"Pattern not found: {_lastSearch}";
                return null;
            }
            pos = found.Value;
        }
        return pos;
    }

    private int? FindForward(int from, StringComparison comparison)
    {
        for (var index = Math.Max(0, from); index < _text.Length; index++)
            if (IsMatchAt(index, comparison)) return index;
        for (var index = 0; index < Math.Min(from, _text.Length); index++)
            if (IsMatchAt(index, comparison)) { Message = "search hit BOTTOM, continuing at TOP"; return index; }
        return null;
    }

    private int? FindBackward(int from, StringComparison comparison)
    {
        for (var index = Math.Min(from, _text.Length - 1); index >= 0; index--)
            if (IsMatchAt(index, comparison)) return index;
        for (var index = _text.Length - 1; index > from; index--)
            if (IsMatchAt(index, comparison)) { Message = "search hit TOP, continuing at BOTTOM"; return index; }
        return null;
    }

    private bool IsMatchAt(int index, StringComparison comparison)
    {
        var pattern = _lastSearch!;
        return index + pattern.Length <= _text.Length &&
               string.Compare(_text, index, pattern, 0, pattern.Length, comparison) == 0 &&
               IsSearchHit(index, pattern.Length);
    }

    private bool IsSearchHit(int index, int length) =>
        !_searchWholeWord ||
        ((index == 0 || !IsWordChar(_text[index - 1])) && (index + length >= _text.Length || !IsWordChar(_text[index + length])));

    private int? MatchBracket(int caret)
    {
        const string open = "([{";
        const string close = ")]}";
        var end = LineContentEnd(caret);
        var pos = caret;
        while (pos < end && open.IndexOf(_text[pos]) < 0 && close.IndexOf(_text[pos]) < 0) pos++;
        if (pos >= end) return null;
        var ch = _text[pos];
        var forward = open.IndexOf(ch) >= 0;
        var mate = forward ? close[open.IndexOf(ch)] : open[close.IndexOf(ch)];
        var depth = 0;
        for (var p = pos; forward ? p < _text.Length : p >= 0; p += forward ? 1 : -1)
        {
            if (_text[p] == ch) depth++;
            else if (_text[p] == mate && --depth == 0) return p;
        }
        return null;
    }

    private int NextParagraph(int pos)
    {
        var line = LineOf(pos);
        while (line < LastLine && IsLineEmpty(LineStartOf(line))) line++;
        while (line < LastLine && !IsLineEmpty(LineStartOf(line))) line++;
        return IsLineEmpty(LineStartOf(line)) ? LineStartOf(line) : _text.Length;
    }

    private int PreviousParagraph(int pos)
    {
        var line = LineOf(pos);
        while (line > 0 && IsLineEmpty(LineStartOf(line))) line--;
        while (line > 0 && !IsLineEmpty(LineStartOf(line))) line--;
        return LineStartOf(line);
    }

    // ================================================================= words

    private static bool IsBlank(char c) => c is ' ' or '\t' or '\r' or '\n';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int Class(char c, bool big) => IsBlank(c) ? 0 : big || IsWordChar(c) ? 1 : 2;

    private int NextWordStart(int pos, bool big)
    {
        var n = _text.Length;
        if (pos >= n) return n;
        var cls = Class(_text[pos], big);
        if (cls != 0) while (pos < n && Class(_text[pos], big) == cls) pos++;
        while (pos < n && IsBlank(_text[pos]))
        {
            if (_text[pos] == '\n')
            {
                pos++;
                // An empty line counts as a word.
                if (pos < n && (_text[pos] == '\n' || (_text[pos] == '\r' && pos + 1 < n && _text[pos + 1] == '\n'))) return pos;
                continue;
            }
            pos++;
        }
        return pos;
    }

    private int WordEnd(int pos, bool big)
    {
        var n = _text.Length;
        pos++;
        while (pos < n && IsBlank(_text[pos])) pos++;
        if (pos >= n) return Math.Max(0, n - 1);
        var cls = Class(_text[pos], big);
        while (pos + 1 < n && Class(_text[pos + 1], big) == cls) pos++;
        return pos;
    }

    private int PreviousWordStart(int pos, bool big)
    {
        pos--;
        while (pos > 0 && IsBlank(_text[pos])) pos--;
        if (pos <= 0) return 0;
        var cls = Class(_text[pos], big);
        while (pos > 0 && Class(_text[pos - 1], big) == cls) pos--;
        return pos;
    }

    private int PreviousWordEnd(int pos, bool big)
    {
        if (pos >= _text.Length) pos = _text.Length - 1;
        if (pos < 0) return 0;
        var cls = Class(_text[pos], big);
        if (cls != 0) while (pos >= 0 && Class(_text[pos], big) == cls) pos--;
        while (pos >= 0 && IsBlank(_text[pos])) pos--;
        return Math.Max(0, pos);
    }

    /// <summary>The keyword (letters, digits, _) at or after the caret on its line.</summary>
    private (int Start, int End) WordAt(int caret)
    {
        var end = LineContentEnd(caret);
        var pos = caret;
        while (pos < end && !IsWordChar(_text[pos])) pos++;
        if (pos >= end) return (caret, caret);
        var start = pos;
        while (start > LineStart(caret) && IsWordChar(_text[start - 1])) start--;
        var stop = pos;
        while (stop < end && IsWordChar(_text[stop])) stop++;
        return (start, stop);
    }

    // ================================================================= text objects

    private Range? TextObject(bool around, string obj, int caret, int count)
    {
        switch (obj)
        {
            case "w":
            case "W":
                return WordObject(around, obj == "W", caret, count);
            case "\"":
            case "'":
            case "`":
                return QuoteObject(around, obj[0], caret);
            case "(":
            case ")":
            case "b":
                return BracketObject(around, '(', ')', caret, count);
            case "[":
            case "]":
                return BracketObject(around, '[', ']', caret, count);
            case "{":
            case "}":
            case "B":
                return BracketObject(around, '{', '}', caret, count);
            case "<":
            case ">":
                return BracketObject(around, '<', '>', caret, count);
            case "p":
            {
                if (IsLineEmpty(caret)) return null;
                var first = LineOf(caret);
                var last = first;
                while (first > 0 && !IsLineEmpty(LineStartOf(first - 1))) first--;
                while (last < LastLine && !IsLineEmpty(LineStartOf(last + 1))) last++;
                if (around) while (last < LastLine && IsLineEmpty(LineStartOf(last + 1))) last++;
                return LinesRange(first, last);
            }
        }
        return null;
    }

    private Range? WordObject(bool around, bool big, int caret, int count)
    {
        var lineStart = LineStart(caret);
        var lineEnd = LineContentEnd(caret);
        if (caret >= lineEnd) return null;
        var start = caret;
        var end = caret;
        for (var i = 0; i < count; i++)
        {
            if (end >= lineEnd) break;
            var cls = Class(_text[end], big);
            if (i == 0) while (start > lineStart && Class(_text[start - 1], big) == cls) start--;
            while (end < lineEnd && Class(_text[end], big) == cls) end++;
            if (around)
            {
                if (cls != 0)
                {
                    var trailing = end;
                    while (trailing < lineEnd && _text[trailing] is ' ' or '\t') trailing++;
                    if (trailing > end) end = trailing;
                    else if (i == 0) while (start > lineStart && _text[start - 1] is ' ' or '\t') start--;
                }
                else
                {
                    var c2 = end < lineEnd ? Class(_text[end], big) : 0;
                    while (end < lineEnd && c2 != 0 && Class(_text[end], big) == c2) end++;
                }
            }
        }
        return new Range(start, end, false);
    }

    private Range? QuoteObject(bool around, char quote, int caret)
    {
        var lineStart = LineStart(caret);
        var lineEnd = LineContentEnd(caret);
        var quotes = new List<int>();
        for (var p = lineStart; p < lineEnd; p++)
        {
            if (_text[p] != quote) continue;
            // '' inside an SQL string is an escaped quote, not the end.
            if (quote == '\'' && p + 1 < lineEnd && _text[p + 1] == '\'' && quotes.Count % 2 == 1) { p++; continue; }
            quotes.Add(p);
        }
        for (var i = 0; i + 1 < quotes.Count; i += 2)
        {
            var (open, close) = (quotes[i], quotes[i + 1]);
            if (caret > close && i + 2 < quotes.Count) continue;
            if (caret > close) return null;
            if (around)
            {
                var end = close + 1;
                while (end < lineEnd && _text[end] is ' ' or '\t') end++;
                return new Range(open, end, false);
            }
            return new Range(open + 1, close, false);
        }
        return null;
    }

    private Range? BracketObject(bool around, char open, char close, int caret, int count)
    {
        var start = caret < _text.Length && _text[caret] == close ? caret - 1 : caret;
        int openPos = -1;
        for (var level = 0; level < count; level++)
        {
            var depth = 0;
            var p = level == 0 ? start : openPos - 1;
            openPos = -1;
            for (; p >= 0; p--)
            {
                if (_text[p] == close && !(level == 0 && p == caret)) depth++;
                else if (_text[p] == open)
                {
                    if (depth == 0) { openPos = p; break; }
                    depth--;
                }
            }
            if (openPos < 0) return null;
        }
        var d = 0;
        var closePos = -1;
        for (var p = openPos; p < _text.Length; p++)
        {
            if (_text[p] == open) d++;
            else if (_text[p] == close && --d == 0) { closePos = p; break; }
        }
        if (closePos < 0) return null;
        return around ? new Range(openPos, closePos + 1, false) : new Range(openPos + 1, closePos, false);
    }

    // ================================================================= edits

    private string Newline => _text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private string ToDocumentNewlines(string text) =>
        Newline == "\n" ? text : text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    private void Replace(int offset, int length, string text)
    {
        _host.Replace(offset, length, text);
        _text = _host.Text;
    }

    private void Yank(Range range)
    {
        var text = _text[range.Start..range.End].Replace("\r\n", "\n");
        if (range.Linewise && !text.EndsWith('\n')) text += "\n";
        _register = text;
        _registerLinewise = range.Linewise;
    }

    /// <summary>Deletes [start, end) into the register; linewise ranges (or lines start..lastLine) take their line
    /// breaks with them.</summary>
    private void Delete(int start, int end, bool linewise, int? lastLine = null)
    {
        if (lastLine is { } last)
        {
            var range = LinesRange(LineOf(start), last);
            (start, end, linewise) = (range.Start, range.End, true);
        }
        Yank(new Range(start, end, linewise));
        if (linewise)
        {
            var firstLine = LineOf(start);
            if (end >= _text.Length && (end == start || !EndsWithNewline(end)) && start > 0)
            {
                // The last line has no line break of its own: take the one before it.
                var previousEnd = LineContentEnd(LineStartOf(firstLine - 1));
                Replace(previousEnd, _text.Length - previousEnd, "");
                _host.Caret = FirstNonBlank(LineStartOf(Math.Max(0, firstLine - 1)));
            }
            else
            {
                Replace(start, end - start, "");
                _host.Caret = FirstNonBlank(LineStartOf(Math.Min(firstLine, LastLine)));
            }
        }
        else
        {
            Replace(start, end - start, "");
            _host.Caret = Math.Min(start, _text.Length);
        }
    }

    private bool EndsWithNewline(int end) => end > 0 && _text[end - 1] == '\n';

    private void Put(bool after, int count)
    {
        if (_register.Length == 0) return;
        var caret = _host.Caret;
        var text = ToDocumentNewlines(string.Concat(Enumerable.Repeat(_register, count)));
        if (_registerLinewise)
        {
            int at;
            if (after)
            {
                var next = NextLineStart(caret);
                if (next >= _text.Length && !EndsWithNewline(_text.Length))
                {
                    // After the last line: the line break goes before the text instead of after it.
                    at = _text.Length;
                    text = Newline + text[..^Newline.Length];
                    Replace(at, 0, text);
                    _host.Caret = FirstNonBlank(at + Newline.Length);
                    return;
                }
                at = next;
            }
            else at = LineStart(caret);
            Replace(at, 0, text);
            _host.Caret = FirstNonBlank(at);
        }
        else
        {
            var at = after && !IsLineEmpty(caret) ? Math.Min(caret + 1, _text.Length) : caret;
            Replace(at, 0, text);
            _host.Caret = text.Contains('\n') ? at : at + text.Length - 1;
        }
    }

    private void JoinLines(int line, int count, bool spaces)
    {
        for (var i = 1; i < count && line < LastLine; i++)
        {
            var lineStart = LineStartOf(line);
            var end = LineContentEnd(lineStart);
            var next = NextLineStart(lineStart);
            var nextContent = next;
            if (spaces) while (nextContent < _text.Length && _text[nextContent] is ' ' or '\t') nextContent++;
            var trimEnd = end;
            if (spaces) while (trimEnd > lineStart && _text[trimEnd - 1] is ' ' or '\t') trimEnd--;
            var nextIsEmpty = nextContent >= _text.Length || _text[nextContent] is '\r' or '\n';
            var separator = spaces && trimEnd > lineStart && !nextIsEmpty && _text[nextContent] != ')' ? " " : "";
            Replace(trimEnd, nextContent - trimEnd, separator);
            _host.Caret = Math.Max(lineStart, trimEnd + separator.Length - (separator.Length > 0 ? 1 : 0));
        }
    }

    private void Indent(int firstLine, int lastLine, bool right)
    {
        for (var line = lastLine; line >= firstLine; line--)
        {
            var start = LineStartOf(line);
            if (right)
            {
                if (!IsLineEmpty(start)) Replace(start, 0, new string(' ', IndentSize));
            }
            else
            {
                var remove = 0;
                if (start < _text.Length && _text[start] == '\t') remove = 1;
                else while (remove < IndentSize && start + remove < _text.Length && _text[start + remove] == ' ') remove++;
                if (remove > 0) Replace(start, remove, "");
            }
        }
        _host.Caret = FirstNonBlank(LineStartOf(firstLine));
    }

    private static string ToggleCase(string text) =>
        string.Concat(text.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)));

    private Range ToLineEnd(int caret, int count)
    {
        var line = Math.Min(LineOf(caret) + count - 1, LastLine);
        return new Range(caret, LineContentEnd(LineStartOf(line)), false);
    }

    // ================================================================= lines

    private int LastLine => LineOf(_text.Length);

    private int LineStart(int pos)
    {
        pos = Math.Clamp(pos, 0, _text.Length);
        return pos == 0 ? 0 : _text.LastIndexOf('\n', pos - 1) + 1;
    }

    /// <summary>Where the line's text ends (before \r\n or \n).</summary>
    private int LineContentEnd(int pos)
    {
        var nl = _text.IndexOf('\n', LineStart(pos));
        if (nl < 0) return _text.Length;
        return nl > 0 && _text[nl - 1] == '\r' ? nl - 1 : nl;
    }

    private int NextLineStart(int pos)
    {
        var nl = _text.IndexOf('\n', LineStart(pos));
        return nl < 0 ? _text.Length : nl + 1;
    }

    private int LineLength(int pos) => LineContentEnd(pos) - LineStart(pos);

    private bool IsLineEmpty(int pos) => LineContentEnd(pos) == LineStart(pos);

    private int Column(int pos) => Math.Clamp(pos, 0, _text.Length) - LineStart(pos);

    private int LineOf(int pos)
    {
        pos = Math.Clamp(pos, 0, _text.Length);
        var line = 0;
        for (var i = 0; i < pos; i++) if (_text[i] == '\n') line++;
        return line;
    }

    private int LineStartOf(int line)
    {
        var pos = 0;
        for (var i = 0; i < line; i++)
        {
            var nl = _text.IndexOf('\n', pos);
            if (nl < 0) return LineStart(_text.Length);
            pos = nl + 1;
        }
        return pos;
    }

    private int FirstNonBlank(int pos)
    {
        var p = LineStart(pos);
        var end = LineContentEnd(pos);
        while (p < end && _text[p] is ' ' or '\t') p++;
        return p;
    }

    private string Indentation(int pos)
    {
        return _text[LineStart(pos)..FirstNonBlank(pos)];
    }

    private Range LinesRange(int firstLine, int lastLine)
    {
        firstLine = Math.Clamp(firstLine, 0, LastLine);
        lastLine = Math.Clamp(lastLine, firstLine, LastLine);
        return new Range(LineStartOf(firstLine), NextLineStart(LineStartOf(lastLine)), true);
    }

    /// <summary>In normal mode the caret sits on a character, never after the last one of a line.</summary>
    private void ClampNormalCaret()
    {
        _text = _host.Text;
        _host.ClearSelection();
        var caret = Math.Clamp(_host.Caret, 0, _text.Length);
        var start = LineStart(caret);
        var end = LineContentEnd(caret);
        if (caret >= end && end > start) caret = end - 1;
        if (caret != _host.Caret) _host.Caret = caret;
    }

    private static int? ReadCount(KeyReader keys)
    {
        var digits = "";
        while (keys.TryPeek(out var k) && k.Length == 1 && char.IsAsciiDigit(k[0]) && (digits.Length > 0 || k != "0"))
        {
            digits += k;
            keys.Next();
        }
        return digits.Length == 0 ? null : int.TryParse(digits, out var n) ? Math.Min(n, 100_000) : 100_000;
    }

    private readonly record struct Range(int Start, int End, bool Linewise);

    private sealed class IncompleteCommand : Exception;

    private sealed class InvalidCommand : Exception;

    private sealed class KeyReader(List<string> keys)
    {
        private int _index;

        public string Next() => _index < keys.Count ? keys[_index++] : throw new IncompleteCommand();

        public string Peek() => _index < keys.Count ? keys[_index] : throw new IncompleteCommand();

        public bool TryPeek(out string key)
        {
            key = _index < keys.Count ? keys[_index] : "";
            return _index < keys.Count;
        }

        public void Back() => _index--;
    }
}
