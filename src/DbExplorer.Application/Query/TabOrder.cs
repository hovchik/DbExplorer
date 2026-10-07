namespace DbExplorer.Application.Query;

/// <summary>Index arithmetic for reordering tabs by drag and drop or by keyboard.</summary>
public static class TabOrder
{
    /// <summary>
    /// Where a tab at <paramref name="from"/> ends up when dropped in the gap <paramref name="slot"/>
    /// (0 = before the first tab, <paramref name="count"/> = after the last); equal to <paramref name="from"/> when nothing moves.
    /// </summary>
    public static int DropTarget(int from, int slot, int count)
    {
        if (count <= 0 || from < 0 || from >= count) return from;
        slot = Math.Clamp(slot, 0, count);
        return slot > from ? slot - 1 : slot;
    }

    /// <summary>The index a tab moves to when shifted by <paramref name="offset"/> places; it stops at either end.</summary>
    public static int Shift(int from, int offset, int count) =>
        count <= 0 || from < 0 || from >= count ? from : Math.Clamp(from + offset, 0, count - 1);
}
