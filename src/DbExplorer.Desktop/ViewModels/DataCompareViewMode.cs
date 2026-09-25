namespace DbExplorer.Desktop.ViewModels;

/// <summary>How the Data tab of the Comparer renders comparison rows.</summary>
public enum DataCompareViewMode
{
    /// <summary>One row per matched key, with a text summary of the columns that differ.</summary>
    Summary,

    /// <summary>One row per matched key, with a left/right value pair per common column.</summary>
    SideBySide,

    /// <summary>The full (unmatched) row set of each side's table, browsed independently.</summary>
    FullDataSets
}
