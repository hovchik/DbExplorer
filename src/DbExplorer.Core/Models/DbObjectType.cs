namespace DbExplorer.Core.Models;

public enum DbObjectType
{
    Table,
    View,
    MaterializedView,
    ForeignTable,
    Procedure,
    Function,
    ScalarFunction,
    TableFunction,
    Trigger,
    Sequence,
    Synonym,
    Other
}
