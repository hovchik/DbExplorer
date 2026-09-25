using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using DbExplorer.Core.Models;
using DbExplorer.Desktop.Converters;

namespace DbExplorer.Desktop.Views;

/// <summary>
/// Renders a set of <see cref="DataComparisonRow"/>s with one column per common table column,
/// showing the left and right value stacked and highlighting the cell when they differ.
/// </summary>
public partial class DataComparisonGridView : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<DataComparisonRow>?> RowsProperty =
        AvaloniaProperty.Register<DataComparisonGridView, IReadOnlyList<DataComparisonRow>?>(nameof(Rows));

    public static readonly StyledProperty<bool> ShowLeftProperty =
        AvaloniaProperty.Register<DataComparisonGridView, bool>(nameof(ShowLeft), true);

    public static readonly StyledProperty<bool> ShowRightProperty =
        AvaloniaProperty.Register<DataComparisonGridView, bool>(nameof(ShowRight), true);

    public IReadOnlyList<DataComparisonRow>? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public bool ShowLeft
    {
        get => GetValue(ShowLeftProperty);
        set => SetValue(ShowLeftProperty, value);
    }

    public bool ShowRight
    {
        get => GetValue(ShowRightProperty);
        set => SetValue(ShowRightProperty, value);
    }

    public DataComparisonGridView()
    {
        InitializeComponent();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RowsProperty || change.Property == ShowLeftProperty || change.Property == ShowRightProperty)
            Rebuild(Rows);
    }

    private void Rebuild(IReadOnlyList<DataComparisonRow>? rows)
    {
        Grid.Columns.Clear();

        if (rows is null || rows.Count == 0)
        {
            Grid.ItemsSource = null;
            return;
        }

        Grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Key", Binding = new Binding("Key"), Width = DataGridLength.Auto
        });
        Grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Status", Binding = new Binding("Status"), Width = DataGridLength.Auto
        });

        var columnNames = rows.SelectMany(r => r.Cells.Select(c => c.Column)).Distinct().ToList();
        for (var i = 0; i < columnNames.Count; i++)
        {
            var index = i;
            Grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = columnNames[index],
                Width = DataGridLength.Auto,
                CellTemplate = new FuncDataTemplate<DataComparisonRow>((_, _) => BuildCell(index, ShowLeft, ShowRight))
            });
        }

        Grid.ItemsSource = (IEnumerable)rows;
    }

    private static Control BuildCell(int cellIndex, bool showLeft, bool showRight)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };

        if (showLeft)
        {
            var left = new TextBlock { FontSize = 12 };
            left.Bind(TextBlock.TextProperty, new Binding($"Cells[{cellIndex}].LeftValue"));
            panel.Children.Add(left);
        }

        if (showRight)
        {
            var right = new TextBlock { FontSize = 12, Opacity = 0.7 };
            right.Bind(TextBlock.TextProperty, new Binding($"Cells[{cellIndex}].RightValue"));
            panel.Children.Add(right);
        }

        var border = new Border { Padding = new Thickness(4, 2), Child = panel };
        border.Bind(Border.BackgroundProperty, new Binding($"Cells[{cellIndex}].IsDifferent")
        {
            Converter = BoolToHighlightBrushConverter.Instance
        });
        return border;
    }
}
