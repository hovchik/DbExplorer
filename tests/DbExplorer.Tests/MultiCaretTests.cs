using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public class MultiCaretTests
{
    [Fact]
    public void Word_at_the_caret_includes_sql_name_characters()
    {
        const string sql = "SELECT @order_id, #tmp FROM t";
        Assert.Equal((7, 9), MultiCaret.WordAt(sql, 10));
        Assert.Equal((18, 4), MultiCaret.WordAt(sql, 22)); // caret right after the word
        Assert.Null(MultiCaret.WordAt(sql, 17));
    }

    [Fact]
    public void Whole_word_matches_skip_longer_names_and_ignore_case()
    {
        const string sql = "select id, order_id, ID from t where Id = 1";
        Assert.Equal(new[] { 7, 21, 37 }, MultiCaret.FindAll(sql, "id", wholeWord: true));
        Assert.Equal(4, MultiCaret.FindAll(sql, "id", wholeWord: false).Count);
    }

    [Fact]
    public void Next_occurrence_wraps_and_skips_taken_ones()
    {
        const string sql = "a x a x a";
        Assert.Equal(4, MultiCaret.FindNext(sql, "a", true, after: 1, taken: [0]));
        Assert.Equal(0, MultiCaret.FindNext(sql, "a", true, after: 9, taken: [4, 8]));
        Assert.Null(MultiCaret.FindNext(sql, "a", true, after: 1, taken: [0, 4, 8]));
    }

    [Fact]
    public void Edits_belong_to_the_range_they_touch()
    {
        var ranges = new List<(int, int)> { (2, 2), (10, 13) };
        Assert.Equal((0, 0), MultiCaret.Locate(ranges, 2, 0));     // typing at a plain caret
        Assert.Equal((0, -1), MultiCaret.Locate(ranges, 1, 1));    // backspace just before it
        Assert.Equal((1, 0), MultiCaret.Locate(ranges, 10, 3));    // replacing a selected word
        Assert.Equal((1, 3), MultiCaret.Locate(ranges, 13, 0));    // appending to it
        Assert.Null(MultiCaret.Locate(ranges, 6, 1));              // somewhere else
    }

    [Fact]
    public void Mirrored_edits_stay_inside_the_text()
    {
        Assert.Equal((4, 1), MultiCaret.Mirror(5, -1, 1, 20));
        Assert.Equal((0, 0), MultiCaret.Mirror(0, -1, 1, 20));
        Assert.Equal((20, 0), MultiCaret.Mirror(19, 3, 2, 20));
    }
}
