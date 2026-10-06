using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DbExplorer.Application.Query;
using DbExplorer.Desktop.Converters;

namespace DbExplorer.Desktop.Views;

/// <summary>One line of the pivot grid: the values and the text each cell shows.</summary>
public sealed class PivotRow(object?[] values, string[] texts, bool isTotal)
{
    public object?[] Values { get; } = values;
    public string[] Texts { get; } = texts;
    public bool IsTotal { get; } = isTotal;
}

// The pivot view: the rows in view (after the search box and column filters) cross-tabulated in a second,
// read-only grid that takes the result grid's place while the Pivot toggle is on.
public partial class ResultGridView
{
    private static readonly PivotAggregate[] PivotAggregates =
        [PivotAggregate.Sum, PivotAggregate.Count, PivotAggregate.Average, PivotAggregate.Min, PivotAggregate.Max];

    private readonly Flyout _pivotRowsFlyout = new() { Placement = PlacementMode.BottomEdgeAlignedLeft };
    private PivotResult? _pivot;
    private int _pivotVersion;

    private void InitPivot()
    {
        PivotRowsButton.Flyout = _pivotRowsFlyout;
        _pivotRowsFlyout.Opening += (_, _) => _pivotRowsFlyout.Content = BuildPivotRowsPanel();
        PivotAggregateBox.ItemsSource = PivotAggregates.Select(PivotSpec.Describe).ToList();

        PivotToggle.IsCheckedChanged += (_, _) =>
        {
            if (_rebuilding) return;
            _state.PivotOn = PivotToggle.IsChecked == true;
            if (_state.PivotOn) _state.Pivot ??= DefaultPivot();
            SyncPivotControls();
            UpdatePivot();
        };
        PivotColumnBox.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || _state.Pivot is not { } spec || PivotColumnBox.SelectedIndex < 0) return;
            SetPivot(spec with { ColumnField = PivotColumnBox.SelectedIndex == 0 ? null : PivotColumnBox.SelectedIndex - 1 });
        };
        PivotValueBox.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || _state.Pivot is not { } spec || PivotValueBox.SelectedIndex < 0) return;
            SetPivot(spec with { ValueField = PivotValueBox.SelectedIndex == 0 ? null : PivotValueBox.SelectedIndex - 1 });
        };
        PivotAggregateBox.SelectionChanged += (_, _) =>
        {
            if (_rebuilding || _state.Pivot is not { } spec || PivotAggregateBox.SelectedIndex < 0) return;
            SetPivot(spec with { Aggregate = PivotAggregates[PivotAggregateBox.SelectedIndex] });
        };
        PivotGrid.LoadingRow += (_, e) => e.Row.FontWeight = e.Row.DataContext is PivotRow { IsTotal: true } ? FontWeight.SemiBold : FontWeight.Normal;
    }

    /// <summary>A first pivot that shows something useful: grouped by the first text column, summing the first number column.</summary>
    private PivotSpec DefaultPivot()
    {
        var group = _columns.FirstOrDefault(c => !c.IsNumeric) ?? _columns.FirstOrDefault();
        var value = _columns.FirstOrDefault(c => c.IsNumeric && c != group);
        return new PivotSpec(group is null ? [] : [group.Index], null, value?.Index, value is null ? PivotAggregate.Count : PivotAggregate.Sum);
    }

    private void SetPivot(PivotSpec spec)
    {
        _state.Pivot = spec;
        SyncPivotControls();
        UpdatePivot();
    }

    /// <summary>Puts the toggle, the field pickers and which grid is showing in line with the current result set's pivot.</summary>
    private void SyncPivotControls()
    {
        var wasRebuilding = _rebuilding;
        _rebuilding = true;
        try
        {
            var on = ResultSet is not null && _state.PivotOn;
            PivotToggle.IsChecked = on;
            PivotToggle.IsEnabled = ResultSet is not null && _columns.Count > 0;
            PivotBar.IsVisible = on;
            PivotGrid.IsVisible = on;
            BodyGrid.IsVisible = !on;
            if (!on) return;

            var spec = _state.Pivot ??= DefaultPivot();
            var names = _columns.Select(c => c.Name).ToList();
            PivotColumnBox.ItemsSource = new[] { "(none)" }.Concat(names).ToList();
            PivotColumnBox.SelectedIndex = spec.ColumnField is { } cf && cf < names.Count ? cf + 1 : 0;
            PivotValueBox.ItemsSource = new[] { "(rows)" }.Concat(names).ToList();
            PivotValueBox.SelectedIndex = spec.ValueField is { } vf && vf < names.Count ? vf + 1 : 0;
            PivotAggregateBox.SelectedIndex = spec.ValueField is null ? Array.IndexOf(PivotAggregates, PivotAggregate.Count) : Array.IndexOf(PivotAggregates, spec.Aggregate);
            PivotAggregateBox.IsEnabled = spec.ValueField is not null;
            var rows = spec.RowFields.Where(f => f < names.Count).Select(f => names[f]).ToList();
            PivotRowsButton.Content = (rows.Count == 0 ? "(none)" : string.Join(", ", rows)) + " ▾";
        }
        finally
        {
            _rebuilding = wasRebuilding;
        }
    }

    private Control BuildPivotRowsPanel()
    {
        var list = new StackPanel { Spacing = 2, MinWidth = 200 };
        var selected = _state.Pivot?.RowFields ?? [];
        foreach (var c in _columns)
        {
            var box = new CheckBox { Content = c.Name, IsChecked = selected.Contains(c.Index) };
            box.IsCheckedChanged += (_, _) =>
            {
                if (_state.Pivot is not { } spec) return;
                var fields = spec.RowFields.Where(f => f != c.Index).ToList();
                if (box.IsChecked == true) fields.Add(c.Index); // grouped in the order they were picked
                SetPivot(spec with { RowFields = fields });
            };
            list.Children.Add(box);
        }
        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Group rows by (in the order you tick them)", Classes = { "hint" } },
                new ScrollViewer { MaxHeight = 380, Content = list }
            }
        };
    }

    /// <summary>Re-pivots the rows in view into the pivot grid. Large views pivot off the UI thread; only the latest request is shown.</summary>
    private async void UpdatePivot()
    {
        if (ResultSet is not { } rs || !_state.PivotOn || _state.Pivot is not { } spec)
        {
            _pivot = null;
            PivotGrid.ItemsSource = null;
            return;
        }
        var version = ++_pivotVersion;
        var rows = _viewRows;
        var names = _columns.Select(c => c.Name).ToList();
        PivotResult pivot;
        try
        {
            if (rows.Count < BackgroundViewRows)
                pivot = ResultPivot.Build(rows, r => r.Values, names, spec);
            else
            {
                PivotInfoText.Text = $"Pivoting {rows.Count:N0} row(s)…";
                pivot = await Task.Run(() => ResultPivot.Build(rows, r => r.Values, names, spec));
                if (version != _pivotVersion || !ReferenceEquals(rs, ResultSet)) return;
            }
        }
        catch (Exception ex)
        {
            if (version == _pivotVersion) PivotInfoText.Text = "Could not pivot the rows: " + ex.Message;
            return;
        }
        ShowPivot(pivot, rows.Count);
    }

    private void ShowPivot(PivotResult pivot, int sourceRows)
    {
        _pivot = pivot;
        var lines = pivot.Rows.Select((values, i) =>
            new PivotRow(values, values.Select((v, c) => PivotText(pivot, v, c)).ToArray(), i == pivot.TotalRowIndex)).ToList();

        PivotGrid.FrozenColumnCount = 0;
        PivotGrid.Columns.Clear();
        for (var i = 0; i < pivot.Headers.Count; i++)
        {
            var index = i;
            var numeric = i >= pivot.RowFieldCount && ResultViewQuery.IsNumericColumn(pivot.Rows.Select(r => r[index]));
            PivotGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = pivot.Headers[i],
                CellTemplate = new FuncDataTemplate<PivotRow>((_, _) => new TextBlock
                {
                    Margin = new Thickness(8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = numeric ? TextAlignment.Right : TextAlignment.Left,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    [!TextBlock.TextProperty] = new Binding($"Texts[{index}]")
                }, supportsRecycling: true),
                Width = DataGridLength.Auto,
                MinWidth = 56,
                MaxWidth = 600
            });
        }
        PivotGrid.ItemsSource = lines;
        PivotGrid.FrozenColumnCount = pivot.RowFieldCount;

        var groups = pivot.RowFieldCount == 0 ? 0 : pivot.Rows.Count - 1;
        var columns = pivot.Headers.Count - pivot.RowFieldCount;
        var info = $"{groups:N0} group(s) × {pivot.ColumnValueCount:N0} column value(s) from {sourceRows:N0} row(s) in view";
        if (pivot.ColumnsTruncated) info += $" · only the first {ResultPivot.MaxColumnValues:N0} column values are shown";
        PivotInfoText.Text = columns > 0 ? info : "";
    }

    /// <summary>Row-field values read as in the result grid (NULL included); an empty summary cell is blank, and averages are rounded for reading.</summary>
    private static string PivotText(PivotResult pivot, object? value, int column)
    {
        if (column >= pivot.RowFieldCount && value is null) return "";
        if (value is decimal m) value = Math.Round(m, 6);
        else if (value is double d) value = Math.Round(d, 6);
        return ResultCellTextConverter.Instance.Convert(value, typeof(string), null, CultureInfo.CurrentCulture) as string ?? "";
    }

    private void OnClosePivot(object? sender, RoutedEventArgs e) => PivotToggle.IsChecked = false;

    private void OnCopyPivot(object? sender, RoutedEventArgs e)
    {
        if (_pivot is not { } pivot) return;
        var text = new StringBuilder();
        text.AppendLine(string.Join('\t', pivot.Headers.Select(Clean)));
        foreach (var row in pivot.Rows)
            text.AppendLine(string.Join('\t', row.Select((v, c) => Clean(PivotText(pivot, v, c)))));
        _ = SetClipboardAsync(text.ToString());

        static string Clean(string value) => value.Replace('\t', ' ').ReplaceLineEndings(" ");
    }
}
