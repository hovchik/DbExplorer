using DbExplorer.Application.Query;

namespace DbExplorer.Tests;

public class TabOrderTests
{
    [Theory]
    [InlineData(0, 0, 4, 0)] // dropped where it already is
    [InlineData(0, 1, 4, 0)] // the gap right after itself
    [InlineData(0, 2, 4, 1)] // past its right neighbour: swaps with it
    [InlineData(0, 4, 4, 3)] // after the last tab
    [InlineData(3, 0, 4, 0)] // to the front
    [InlineData(2, 1, 4, 1)] // before its left neighbour: swaps with it
    [InlineData(2, 3, 4, 2)]
    [InlineData(1, 9, 4, 3)] // a gap past the end is clamped
    [InlineData(1, -3, 4, 0)]
    public void Dropping_in_a_gap_moves_the_tab_there(int from, int slot, int count, int expected) =>
        Assert.Equal(expected, TabOrder.DropTarget(from, slot, count));

    [Theory]
    [InlineData(1, -1, 4, 0)]
    [InlineData(1, 1, 4, 2)]
    [InlineData(0, -1, 4, 0)] // already first
    [InlineData(3, 1, 4, 3)]  // already last
    public void Shifting_stops_at_either_end(int from, int offset, int count, int expected) =>
        Assert.Equal(expected, TabOrder.Shift(from, offset, count));

    [Fact]
    public void An_unknown_tab_does_not_move()
    {
        Assert.Equal(-1, TabOrder.DropTarget(-1, 2, 4));
        Assert.Equal(-1, TabOrder.Shift(-1, 1, 4));
    }
}
