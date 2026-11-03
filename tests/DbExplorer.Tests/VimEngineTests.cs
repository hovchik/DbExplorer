using DbExplorer.Application.Vim;

namespace DbExplorer.Tests;

public class VimEngineTests
{
    /// <summary>A plain-string editor: undo snapshots per change group, a selection, and typing in insert mode.</summary>
    private sealed class Host : IVimHost
    {
        private readonly Stack<(string Text, int Caret)> _undo = new();
        private readonly Stack<(string Text, int Caret)> _redo = new();
        private int _depth;
        private (string, int)? _before;

        public Host(string text, int caret)
        {
            Text = text;
            Caret = caret;
        }

        public string Text { get; private set; }
        public int Caret { get; set; }
        public (int Start, int End)? Selection { get; private set; }
        public (int Start, int End)? MouseSelection { get; set; }

        public void Select(int start, int end, int caret)
        {
            Selection = (start, end);
            Caret = caret;
        }

        public void ClearSelection()
        {
            Selection = null;
            MouseSelection = null;
        }

        public void Replace(int offset, int length, string text)
        {
            if (_depth == 0) Push();
            Text = Text.Remove(offset, length).Insert(offset, text);
        }

        public void BeginChange()
        {
            if (_depth++ == 0) _before = (Text, Caret);
        }

        public void EndChange()
        {
            if (--_depth > 0 || _before is not { } before) return;
            if (before.Item1 != Text) { _undo.Push(before); _redo.Clear(); }
            _before = null;
        }

        private void Push()
        {
            _undo.Push((Text, Caret));
            _redo.Clear();
        }

        public void Undo()
        {
            if (_undo.Count == 0) return;
            _redo.Push((Text, Caret));
            (Text, Caret) = _undo.Pop();
        }

        public void Redo()
        {
            if (_redo.Count == 0) return;
            _undo.Push((Text, Caret));
            (Text, Caret) = _redo.Pop();
        }

        /// <summary>What the editor does with a typed character in insert mode.</summary>
        public void Type(string text)
        {
            Replace(Caret, 0, text);
            Caret += text.Length;
        }
    }

    private static IEnumerable<string> Keys(string keys)
    {
        for (var i = 0; i < keys.Length; i++)
        {
            if (keys[i] == '<' && keys.IndexOf('>', i) is var close and > 0 && close - i > 1 && keys[(i + 1)..close].All(c => char.IsLetter(c) || c == '-' || c == '['))
            {
                yield return keys[i..(close + 1)];
                i = close;
            }
            else yield return keys[i].ToString();
        }
    }

    /// <summary>Text with "|" at the caret, the keys as typed (insert-mode typing goes to the editor).</summary>
    private static (Host Host, VimEngine Vim) Start(string marked)
    {
        var caret = marked.IndexOf('|');
        var host = new Host(marked.Remove(caret, 1), caret);
        return (host, new VimEngine(host));
    }

    private static void Feed(Host host, VimEngine vim, string keys)
    {
        foreach (var key in Keys(keys))
            if (!vim.Key(key))
            {
                if (key == "<CR>") host.Type("\n");
                else if (key == "<BS>") { host.Replace(host.Caret - 1, 1, ""); host.Caret--; }
                else host.Type(key);
            }
    }

    private static string Run(string marked, string keys)
    {
        var (host, vim) = Start(marked);
        Feed(host, vim, keys);
        return host.Text.Insert(host.Caret, "|");
    }

    [Theory]
    [InlineData("SELECT |a, b FROM t", "w", "SELECT a|, b FROM t")]
    [InlineData("SELECT |a, b FROM t", "W", "SELECT a, |b FROM t")]
    [InlineData("SELECT a, b FROM |t", "b", "SELECT a, b |FROM t")]
    [InlineData("SELECT a, b FROM |t", "3b", "SELECT a|, b FROM t")]
    [InlineData("|SELECT a", "e", "SELEC|T a")]
    [InlineData("SELECT a|", "0", "|SELECT a")]
    [InlineData("  |SELECT a", "$", "  SELECT |a")]
    [InlineData("  SELECT |a", "^", "  |SELECT a")]
    [InlineData("|SELECT a FROM t WHERE x = 1", "fW", "SELECT a FROM t |WHERE x = 1")]
    [InlineData("|SELECT a FROM t WHERE x = 1", "tW", "SELECT a FROM t| WHERE x = 1")]
    [InlineData("|a.b.c.d", "f.;;", "a.b.c|.d")]
    [InlineData("|a.b.c.d", "f.;,", "a|.b.c.d")]
    [InlineData("a|bc\nd\nefgh", "jj", "abc\nd\ne|fgh")]
    [InlineData("abc\nd\nefg|h", "kk", "ab|c\nd\nefgh")]
    [InlineData("a\nb\n|c", "gg", "|a\nb\nc")]
    [InlineData("|a\nb\n  c", "G", "a\nb\n  |c")]
    [InlineData("|a\nb\nc", "2G", "a\n|b\nc")]
    [InlineData("SUM(|a + (b * c))", "%", "SUM(a + (b * c|))")]
    [InlineData("a\n|b\n\nc\nd", "}", "a\nb\n|\nc\nd")]
    [InlineData("SELECT |abc", "l", "SELECT a|bc")]
    [InlineData("SELECT ab|c", "l", "SELECT ab|c")]
    [InlineData("SELECT a|bc", "10h", "|SELECT abc")]
    public void Motions_move_the_caret(string text, string keys, string expected) =>
        Assert.Equal(expected, Run(text, keys));

    [Theory]
    [InlineData("SELECT |a, b FROM t", "dw", "SELECT |, b FROM t")]
    [InlineData("SELECT |a, b FROM t", "d2W", "SELECT |FROM t")]
    [InlineData("SELECT a, |b\nFROM t", "dw", "SELECT a,| \nFROM t")]
    [InlineData("SELECT |a, b FROM t", "D", "SELECT| ")]
    [InlineData("SELECT |abc FROM t", "x", "SELECT |bc FROM t")]
    [InlineData("SELECT |abc FROM t", "3x", "SELECT | FROM t")]
    [InlineData("SELECT ab|c", "X", "SELECT a|c")]
    [InlineData("a\n|b\nc", "dd", "a\n|c")]
    [InlineData("a\nb\n|c", "dd", "a\n|b")]
    [InlineData("|a\nb\nc", "2dd", "|c")]
    [InlineData("|a\nb\nc", "dj", "|c")]
    [InlineData("a\nb\n|c", "dk", "|a")]
    [InlineData("|a\nb\nc", "dG", "|")]
    [InlineData("SELECT a FROM t WHERE |x = 1", "dt1", "SELECT a FROM t WHERE |1")]
    [InlineData("SELECT a FROM t WHERE |x = 1", "df=", "SELECT a FROM t WHERE | 1")]
    [InlineData("WHERE name = 'Ma|ry' AND x", "di'", "WHERE name = '|' AND x")]
    [InlineData("WHERE name = 'Ma|ry' AND x", "da'", "WHERE name = |AND x")]
    [InlineData("COUNT(a, (b|), c)", "di(", "COUNT(a, (|), c)")]
    [InlineData("COUNT(a, (b|), c)", "d2i(", "COUNT(|)")]
    [InlineData("COUNT(a, (b|), c)", "da)", "COUNT(a, |, c)")]
    [InlineData("SELECT my_co|lumn FROM t", "diw", "SELECT | FROM t")]
    [InlineData("SELECT my_co|lumn FROM t", "daw", "SELECT |FROM t")]
    [InlineData("a\n\n|b\nc\n\nd", "dap", "a\n\n|d")]
    public void Delete_with_motions_and_text_objects(string text, string keys, string expected) =>
        Assert.Equal(expected, Run(text, keys));

    [Theory]
    [InlineData("SELECT |old FROM t", "cwnew<Esc>", "SELECT ne|w FROM t")]
    [InlineData("SELECT |old, x FROM t", "ciwnew<Esc>", "SELECT ne|w, x FROM t")]
    [InlineData("SELECT |a FROM t", "C* FROM u<Esc>", "SELECT * FROM |u")]
    [InlineData("  SELECT |a\nb", "ccx<Esc>", "  |x\nb")]
    [InlineData("WHERE x IN (1, |2)", "ci(3<Esc>", "WHERE x IN (|3)")]
    [InlineData("|abc", "sX<Esc>", "|Xbc")]
    [InlineData("|SELECT", "iX<Esc>", "|XSELECT")]
    [InlineData("|SELECT", "aX<Esc>", "S|XELECT")]
    [InlineData("  SEL|ECT", "IX<Esc>", "  |XSELECT")]
    [InlineData("SEL|ECT", "A;<Esc>", "SELECT|;")]
    [InlineData("  |a\nb", "onew<Esc>", "  a\n  ne|w\nb")]
    [InlineData("a\n  |b", "Onew<Esc>", "a\n  ne|w\n  b")]
    [InlineData("|x", "3ia<Esc>", "aa|ax")]
    public void Change_and_insert(string text, string keys, string expected) =>
        Assert.Equal(expected, Run(text, keys));

    [Theory]
    [InlineData("|a\nb", "yyp", "a\n|a\nb")]
    [InlineData("a\n|b", "yyP", "a\n|b\nb")]
    [InlineData("a\n|b", "yyp", "a\nb\n|b")]
    [InlineData("|ab", "ylp", "a|ab")]
    [InlineData("|ab", "xp", "b|a")]
    [InlineData("|a\nb", "ddp", "b\n|a")]
    [InlineData("|SELECT a", "yw$p", "SELECT aSELECT| ")]
    [InlineData("|a", "yy3p", "a\n|a\na\na")]
    public void Yank_and_put(string text, string keys, string expected) =>
        Assert.Equal(expected, Run(text, keys));

    [Theory]
    [InlineData("|SELECT\n  a,\n  b", "J", "SELECT| a,\n  b")]
    [InlineData("|SELECT\n  a,\n  b", "3J", "SELECT a,| b")]
    [InlineData("|select", "~~~", "SEL|ect")]
    [InlineData("|select a", "gUiw", "|SELECT a")]
    [InlineData("|SELECT a", "guu", "|select a")]
    [InlineData("|a\nb", ">>", "    |a\nb")]
    [InlineData("|a\nb", ">j", "    |a\n    b")]
    [InlineData("      |a", "<<", "  |a")]
    [InlineData("|abc", "rX", "|Xbc")]
    [InlineData("|abc", "2rX", "X|Xc")]
    public void Join_case_indent_replace(string text, string keys, string expected) =>
        Assert.Equal(expected, Run(text, keys));

    [Theory]
    [InlineData("SELECT |ab, b FROM t", "vey", "SELECT |ab, b FROM t", "ab")]
    [InlineData("SELECT |a, b FROM t", "vWd", "SELECT | FROM t", "a, b")]
    [InlineData("a\n|b\nc", "Vjd", "|a", "b\nc\n")]
    [InlineData("SELECT |abc FROM t", "viwU", "SELECT |ABC FROM t", "")]
    [InlineData("WHERE (|a + b)", "vi(c1<Esc>", "WHERE (|1)", "a + b")]
    [InlineData("|a\nb\nc", "Vj>", "    |a\n    b\nc", "")]
    [InlineData("SELECT |abc", "vlo<Esc>", "SELECT |abc", "")]
    public void Visual_mode(string text, string keys, string expected, string register)
    {
        var (host, vim) = Start(text);
        Feed(host, vim, keys);
        Assert.Equal(expected, host.Text.Insert(host.Caret, "|"));
        if (register.Length > 0) Assert.Equal(register, vim.Register);
        Assert.Equal(VimMode.Normal, vim.Mode);
        Assert.Null(host.Selection);
    }

    [Fact]
    public void Visual_mode_selects_inclusively_and_shows_it()
    {
        var (host, vim) = Start("SELECT |abc FROM t");
        Feed(host, vim, "vl");
        Assert.Equal(VimMode.Visual, vim.Mode);
        Assert.Equal((7, 9), host.Selection);
        Feed(host, vim, "V");
        Assert.Equal(VimMode.VisualLine, vim.Mode);
        Assert.Equal((0, 17), host.Selection);
        Feed(host, vim, "<Esc>");
        Assert.Null(host.Selection);
    }

    [Fact]
    public void A_mouse_selection_turns_into_visual_mode()
    {
        var (host, vim) = Start("SELECT |abc FROM t");
        host.MouseSelection = (7, 10);
        Feed(host, vim, "d");
        Assert.Equal("SELECT  FROM t", host.Text);
        Assert.Equal("abc", vim.Register);
    }

    [Fact]
    public void Dot_repeats_the_last_change_including_typed_text()
    {
        Assert.Equal("x x |x", Run("|a b c", "cwx<Esc>w.w."));
        Assert.Equal("|d", Run("|a\nb\nc\nd", "dd.."));
        Assert.Equal("|d", Run("|abcd", "x2."));
        Assert.Equal("  a;\n  b|;", Run("  |a\n  b", "A;<Esc>j."));
    }

    [Fact]
    public void Undo_and_redo_take_a_whole_command()
    {
        Assert.Equal("|a\nb\nc", Run("|a\nb\nc", "2ddu"));
        Assert.Equal("|c", Run("|a\nb\nc", "2ddu<C-r>"));
        Assert.Equal("|a b c", Run("|a b c", "dwdwuu"));
    }

    [Fact]
    public void Counts_multiply_and_zero_is_a_motion()
    {
        Assert.Equal("|g h", Run("|a b c d e f g h", "2d3w"));
        Assert.Equal("|b c", Run("a |b c", "0dw"));
        Assert.Equal("a b c d e f g h i j |k", Run("|a b c d e f g h i j k", "10w"));
    }

    [Fact]
    public void Search_finds_the_next_match_and_wraps()
    {
        var (host, vim) = Start("|SELECT id FROM users u JOIN orders o ON o.user_id = u.id");
        Feed(host, vim, "/id<CR>");
        Assert.Equal(7, host.Caret);
        Feed(host, vim, "n");
        Assert.Equal(47, host.Caret); // user_id
        Feed(host, vim, "n");
        Assert.Equal(54, host.Caret);
        Feed(host, vim, "n");
        Assert.Equal(7, host.Caret);
        Assert.Contains("BOTTOM", vim.Message);
        Feed(host, vim, "N");
        Assert.Equal(54, host.Caret);

        Feed(host, vim, "/nothing<CR>");
        Assert.Equal(54, host.Caret);
        Assert.Equal("Pattern not found: nothing", vim.Message);
    }

    [Fact]
    public void Star_searches_the_whole_word_under_the_caret()
    {
        var (host, vim) = Start("|id, user_id, id");
        Feed(host, vim, "*");
        Assert.Equal(13, host.Caret);
        Feed(host, vim, "#");
        Assert.Equal(0, host.Caret);
    }

    [Fact]
    public void Search_is_case_insensitive_unless_the_pattern_has_capitals()
    {
        Assert.Equal("select from x |FROM", Run("|select from x FROM", "/FROM<CR>"));
        Assert.Equal("select |from x FROM", Run("|select from x FROM", "/from<CR>"));
        Assert.Equal("select from x |FROM", Run("|select from x FROM", "/from<CR>n"));
    }

    [Fact]
    public void Command_line_collects_text_and_runs_on_enter()
    {
        var (host, vim) = Start("|a\nb\nc\nd");
        Feed(host, vim, ":3");
        Assert.Equal(VimMode.CommandLine, vim.Mode);
        Assert.Equal(":3", vim.CommandLine);
        Feed(host, vim, "<CR>");
        Assert.Equal(VimMode.Normal, vim.Mode);
        Assert.Equal(4, host.Caret);

        Feed(host, vim, ":xyz<BS><BS><BS><BS>");
        Assert.Equal(VimMode.Normal, vim.Mode);
        Feed(host, vim, ":bogus<CR>");
        Assert.Equal("Not an editor command: bogus", vim.Message);
    }

    [Fact]
    public void Write_and_quit_are_left_to_the_editor()
    {
        var (host, vim) = Start("|a");
        var seen = new List<VimExCommand>();
        vim.ExCommand += seen.Add;
        Feed(host, vim, ":w<CR>:q<CR>:wq<CR>:x<CR>");
        Assert.Equal([VimExCommand.Save, VimExCommand.Close, VimExCommand.SaveAndClose, VimExCommand.SaveAndClose], seen);
    }

    [Fact]
    public void Substitute_uses_regular_expressions_on_a_range()
    {
        Assert.Equal("|SELECT b FROM a WHERE a = 1", Run("|SELECT a FROM a WHERE a = 1", ":s/\\ba\\b/b/<CR>"));

        var (host, vim) = Start("|x = 1\nx = 2\ny = 3");
        Feed(host, vim, ":%s/(\\w) = (\\d)/\\2 = \\1/g<CR>");
        Assert.Equal("1 = x\n2 = x\n3 = y", host.Text);
        Assert.Equal("3 substitutions on 3 lines", vim.Message);

        Feed(host, vim, "u");
        Assert.Equal("x = 1\nx = 2\ny = 3", host.Text);

        Feed(host, vim, ":1,2s/x/col/<CR>");
        Assert.Equal("col = 1\ncol = 2\ny = 3", host.Text);

        Feed(host, vim, "ggVj:s/col/c/<CR>");
        Assert.Equal("c = 1\nc = 2\ny = 3", host.Text);
        Assert.Equal(VimMode.Normal, vim.Mode);
    }

    [Fact]
    public void Substitute_replaces_only_the_first_match_without_g()
    {
        var (host, vim) = Start("|a a a");
        Feed(host, vim, ":s/a/b/<CR>");
        Assert.Equal("b a a", host.Text);
        Feed(host, vim, ":s/a/b/g<CR>");
        Assert.Equal("b b b", host.Text);
        Feed(host, vim, ":s/zzz/b/<CR>");
        Assert.Equal("Pattern not found: zzz", vim.Message);
    }

    [Fact]
    public void Ex_line_commands_delete_yank_and_indent()
    {
        var (host, vim) = Start("|a\nb\nc\nd");
        Feed(host, vim, ":2,3d<CR>");
        Assert.Equal("a\nd", host.Text);
        Feed(host, vim, ":%>\r".Replace("\r", "<CR>"));
        Assert.Equal("    a\n    d", host.Text);
    }

    [Fact]
    public void Insert_mode_leaves_keys_to_the_editor_and_esc_steps_back()
    {
        var (host, vim) = Start("SELECT| a");
        Assert.True(vim.Key("i"));
        Assert.Equal(VimMode.Insert, vim.Mode);
        Assert.False(vim.Key("x"));
        Assert.False(vim.Key("<CR>"));
        Assert.True(vim.Key("<Esc>"));
        Assert.Equal(VimMode.Normal, vim.Mode);
        Assert.Equal(5, host.Caret);
    }

    [Fact]
    public void Pending_keys_show_until_the_command_is_complete()
    {
        var (host, vim) = Start("|a b");
        Feed(host, vim, "2d");
        Assert.Equal("2d", vim.PendingKeys);
        Assert.Equal("a b", host.Text);
        Feed(host, vim, "<Esc>");
        Assert.Equal("", vim.PendingKeys);
        Assert.Equal("a b", host.Text);
    }

    [Fact]
    public void Unknown_keys_do_nothing_and_do_not_type()
    {
        Assert.Equal("|a b", Run("|a b", "QZ!"));
        Assert.Equal("a |b", Run("a |b", "fz"));
    }

    [Fact]
    public void Normal_mode_caret_never_sits_after_the_last_character()
    {
        Assert.Equal("ab|c\nd", Run("abc|\nd", "<Esc>"));
        Assert.Equal("a|b", Run("|ab", "A<Esc>"));
    }

    [Fact]
    public void Windows_line_breaks_are_kept()
    {
        Assert.Equal("a\r\n|a\r\nb", Run("|a\r\nb", "yyp"));
        Assert.Equal("a\r\n|c", Run("a\r\n|b\r\nc", "dd"));
        Assert.Equal("a\r\n|b", Run("a\r\nb\r\n|c", "dd"));
        Assert.Equal("ab|c\r\nd", Run("|abc\r\nd", "$"));
        Assert.Equal("a\r\n|b", Run("|a\r\nb", "jA<Esc>"));
    }
}
