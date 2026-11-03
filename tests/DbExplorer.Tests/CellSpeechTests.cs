using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public sealed class CellSpeechTests
{
    [Fact]
    public void Says_where_the_cell_is_and_its_value()
    {
        Assert.Equal("Row 3 of 120, city: Oslo", CellSpeech.Describe(3, 120, "city", "Oslo"));
        Assert.Equal("Row 1 of 2, note: NULL", CellSpeech.Describe(1, 2, "note", null));
        Assert.Equal("Row 1 of 2, note: empty text", CellSpeech.Describe(1, 2, "note", ""));
        Assert.Equal("Row 1 of 2, note: two lines, edited", CellSpeech.Describe(1, 2, "note", "two\nlines", edited: true));
        Assert.EndsWith(", cut", CellSpeech.Describe(1, 1, "body", new string('x', 500)));
    }
}
