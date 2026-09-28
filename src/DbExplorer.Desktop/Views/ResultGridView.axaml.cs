using System.Collections;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbExplorer.Application.Export;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

/// <summary>Renders a <see cref="ResultSetView"/> as a DataGrid with dynamically generated,
/// sortable columns (AutoGenerateColumns doesn't work for row types with an indexer-only shape),
/// plus copy/export actions.</summary>
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
        Grid.KeyDown += OnGridKeyDown;
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
            RowCountText.Text = "";
            return;
        }

        for (var i = 0; i < resultSet.Columns.Count; i++)
        {
            var index = i;
            var binding = new Binding($"Values[{index}]")
            {
                Mode = BindingMode.OneWay,
                Converter = Converters.CellValueConverter.Instance,
                TargetNullValue = "NULL"
            };
            Grid.Columns.Add(new DataGridTextColumn
            {
                Header = resultSet.Columns[index],
                Binding = binding,
                SortMemberPath = $"Values[{index}]",
                Width = DataGridLength.Auto,
                Tag = index
            });
        }

        Grid.ItemsSource = (IEnumerable)resultSet.Rows;
        RowCountText.Text = $"{resultSet.Rows.Count:N0} row(s) · {resultSet.Columns.Count:N0} column(s)";
    }

    private IReadOnlyList<IReadOnlyList<object?>> AllRows() =>
        ResultSet?.Rows.Select(r => r.Values).ToList() ?? [];

    private IReadOnlyList<IReadOnlyList<object?>> SelectedRows() =>
        Grid.SelectedItems.OfType<ResultRow>().Select(r => r.Values).ToList();

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = CopyTsvAsync(SelectedRows(), includeHeader: false);
            e.Handled = true;
        }
    }

    private void OnCopyAll(object? sender, RoutedEventArgs e) => _ = CopyTsvAsync(AllRows(), includeHeader: true);

    private void OnCopySelected(object? sender, RoutedEventArgs e) => _ = CopyTsvAsync(SelectedRows(), includeHeader: false);

    private void OnCopySelectedWithHeader(object? sender, RoutedEventArgs e) => _ = CopyTsvAsync(SelectedRows(), includeHeader: true);

    private void OnCopyAllInsert(object? sender, RoutedEventArgs e) => _ = CopyInsertAsync(AllRows());

    private void OnCopySelectedInsert(object? sender, RoutedEventArgs e) => _ = CopyInsertAsync(SelectedRows());

    private void OnCopyCell(object? sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is not ResultRow row || Grid.CurrentColumn?.Tag is not int index || index >= row.Values.Count) return;
        _ = SetClipboardAsync(ResultExporter.FormatInvariant(row.Values[index]));
    }

    private void OnExportCsv(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Csv);
    private void OnExportExcel(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Excel);
    private void OnExportJson(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Json);
    private void OnExportMarkdown(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Markdown);
    private void OnExportInsert(object? sender, RoutedEventArgs e) => _ = ExportAsync(ExportFormat.Insert);

    private Task CopyTsvAsync(IReadOnlyList<IReadOnlyList<object?>> rows, bool includeHeader)
    {
        if (ResultSet is not { } rs || rows.Count == 0) return Task.CompletedTask;
        return SetClipboardAsync(ResultExporter.ToTsv(rs.Columns, rows, includeHeader));
    }

    private Task CopyInsertAsync(IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        if (ResultSet is not { } rs || rows.Count == 0) return Task.CompletedTask;
        return SetClipboardAsync(BuildInsert(rs, rows));
    }

    private static string BuildInsert(ResultSetView rs, IReadOnlyList<IReadOnlyList<object?>> rows) =>
        ResultExporter.ToInsertStatements(rs.Columns, rows, rs.SourceTable ?? rs.Quote("target_table"), rs.Dialect, rs.Quote);

    private async Task SetClipboardAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private async Task ExportAsync(ExportFormat format)
    {
        if (ResultSet is not { } rs || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var extension = ResultExporter.FileExtension(format);
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export results",
            SuggestedFileName = SuggestedName(rs) + "." + extension,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(format.ToString()) { Patterns = ["*." + extension] }]
        });
        if (file is null) return;

        var rows = AllRows();
        try
        {
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            if (format == ExportFormat.Excel)
            {
                ResultExporter.WriteXlsx(stream, rs.Columns, rows, rs.Title);
                RowCountText.Text = $"Exported {rows.Count:N0} row(s) to {file.Name}";
                return;
            }

            var text = format switch
            {
                ExportFormat.Csv => ResultExporter.ToCsv(rs.Columns, rows),
                ExportFormat.Json => ResultExporter.ToJson(rs.Columns, rows),
                ExportFormat.Markdown => ResultExporter.ToMarkdown(rs.Columns, rows),
                _ => BuildInsert(rs, rows)
            };
            // BOM so Excel detects UTF-8 when opening CSV directly.
            var encoding = format == ExportFormat.Csv ? new UTF8Encoding(true) : new UTF8Encoding(false);
            await using var writer = new StreamWriter(stream, encoding);
            await writer.WriteAsync(text);
            RowCountText.Text = $"Exported {rows.Count:N0} row(s) to {file.Name}";
        }
        catch (Exception ex)
        {
            RowCountText.Text = "Export failed: " + ex.Message;
        }
    }

    private static string SuggestedName(ResultSetView rs)
    {
        var name = rs.SourceTable ?? rs.Title;
        var invalid = Path.GetInvalidFileNameChars().Concat(['[', ']', '"', ' ']).ToHashSet();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('_');
        return cleaned.Length == 0 ? "results" : cleaned;
    }
}
