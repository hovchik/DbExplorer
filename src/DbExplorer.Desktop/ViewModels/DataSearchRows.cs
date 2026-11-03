using CommunityToolkit.Mvvm.ComponentModel;
using DbExplorer.Application.Search;
using DbExplorer.Core.Models;

namespace DbExplorer.Desktop.ViewModels;

/// <summary>One found value in the Search data results, with the matched text split out for highlighting.</summary>
public sealed class DataMatchRow(DataMatch match, DataMatchHighlight highlight, string tableLabel)
{
    public DataMatch Match { get; } = match;
    public string TableLabel { get; } = tableLabel;
    public string Column => Match.Column;
    public string RowKey => Match.RowKey ?? "(no primary key)";
    public string ValueBefore { get; } = highlight.Before;
    public string ValueHit { get; } = highlight.Hit;
    public string ValueAfter { get; } = highlight.After;

    /// <summary>The whole value on one line, for sorting, filtering, copying and screen readers.</summary>
    public string Value { get; } = highlight.Before + highlight.Hit + highlight.After;

    public bool Matches(string filter) =>
        TableLabel.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        Column.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        (Match.Value?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (Match.RowKey?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>A table the value was found in, with how many results it has; the first entry stands for every table.</summary>
public sealed partial class DataSearchTableHit : ObservableObject
{
    public DataSearchTableHit(string database, string schema, string table, string label)
    {
        Database = database;
        Schema = schema;
        Table = table;
        Label = label;
    }

    private DataSearchTableHit() { Label = "All tables"; IsAll = true; }

    public static DataSearchTableHit All() => new();

    public string Database { get; } = "";
    public string Schema { get; } = "";
    public string Table { get; } = "";
    public string Label { get; }
    public bool IsAll { get; }

    [ObservableProperty] private int _count;

    public bool Contains(DataMatch m) =>
        IsAll || (string.Equals(m.Database, Database, StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(m.Schema, Schema, StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(m.Table, Table, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One column of the found row shown under the results.</summary>
public sealed record DataRecordField(string Column, string Value, bool IsMatch, bool IsKey, bool IsNull);

/// <summary>One name in a multi-select picker (a schema or a table in Search data's scope).</summary>
public sealed partial class PickChoice(string name, string schema = "", string table = "") : ObservableObject
{
    public string Name { get; } = name;
    public string Schema { get; } = schema;
    public string Table { get; } = table;

    [ObservableProperty] private bool _isSelected;
}
