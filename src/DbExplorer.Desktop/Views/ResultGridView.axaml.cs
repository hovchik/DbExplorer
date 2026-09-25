using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

/// <summary>Renders a <see cref="ResultSetView"/> as a DataGrid with dynamically generated,
/// sortable columns (AutoGenerateColumns doesn't work for row types with an indexer-only shape).</summary>
public partial class ResultGridView : UserControl
{
    public static readonly StyledProperty<ResultSetView?> ResultSetProperty =
        AvaloniaProperty.Register<ResultGridView, ResultSetView?>(nameof(ResultSet));

    public ResultSetView? ResultSet
    {
        get => GetValue(ResultSetProperty);
        set => SetValue(ResultSetProperty, value);
    }

    public ResultGridView()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ResultSetProperty)
            Rebuild(change.GetNewValue<ResultSetView?>());
    }

    private void Rebuild(ResultSetView? resultSet)
    {
        Grid.Columns.Clear();

        if (resultSet is null)
        {
            Grid.ItemsSource = null;
            return;
        }

        for (var i = 0; i < resultSet.Columns.Count; i++)
        {
            var index = i;
            var binding = new Binding($"Values[{index}]") { Mode = BindingMode.OneWay };
            Grid.Columns.Add(new DataGridTextColumn
            {
                Header = resultSet.Columns[index],
                Binding = binding,
                SortMemberPath = $"Values[{index}]",
                Width = DataGridLength.Auto
            });
        }

        Grid.ItemsSource = (IEnumerable)resultSet.Rows;
    }
}
