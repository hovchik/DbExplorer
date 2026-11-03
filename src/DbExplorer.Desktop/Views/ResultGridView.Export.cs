using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DbExplorer.Application.Export;
using DbExplorer.Desktop.ViewModels;

namespace DbExplorer.Desktop.Views;

/// <summary>
/// Exports: the rows in view (what was fetched, with the grid's filters, sort and hidden columns), and, for a result cut
/// off at the row limit, the full result streamed from the database straight into the file. Also the bar above the grid
/// that says a result was cut off and offers Fetch more.
/// </summary>
public partial class ResultGridView
{
    private static readonly (ExportFormat Format, string Label)[] ShownFormats =
    [
        (ExportFormat.Csv, "CSV (.csv)…"),
        (ExportFormat.Excel, "Excel (.xlsx)…"),
        (ExportFormat.Json, "JSON (.json)…"),
        (ExportFormat.Markdown, "Markdown table (.md)…"),
        (ExportFormat.Html, "Interactive HTML page (.html)…"),
        (ExportFormat.Insert, "SQL INSERT script (.sql)…")
    ];

    private static readonly (ExportFormat Format, string Label)[] FullFormats =
    [
        (ExportFormat.Csv, "CSV (.csv)…"),
        (ExportFormat.Excel, "Excel (.xlsx)…"),
        (ExportFormat.Json, "JSON (.json)…"),
        (ExportFormat.Insert, "SQL INSERT script (.sql)…")
    ];

    /// <summary>The full export running from this grid, if any, and the result it reads.</summary>
    private CancellationTokenSource? _exportCts;
    private ResultSetView? _exportingView;
    private string _exportProgressText = "";

    /// <summary>How the last full export of a result ended, shown in its bar.</summary>
    private (ResultSetView View, string Text)? _exportOutcome;

    private readonly MenuFlyout _exportMenu = new() { Placement = PlacementMode.BottomEdgeAlignedRight };
    private readonly MenuFlyout _fullExportMenu = new() { Placement = PlacementMode.BottomEdgeAlignedRight };

    private void InitExport()
    {
        // The items are filled in whenever the result or the export state changes, not while opening: a menu flyout
        // that is empty when asked to open does not open.
        ExportButton.Flyout = _exportMenu;
        ExportFullButton.Flyout = _fullExportMenu;
        BuildExportMenus();
    }

    private void BuildExportMenus()
    {
        BuildExportMenu(_exportMenu);
        _fullExportMenu.Items.Clear();
        foreach (var item in FullExportItems()) _fullExportMenu.Items.Add(item);
    }

    private void BuildExportMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        var rs = ResultSet;
        if (rs is { IsTruncated: true })
        {
            menu.Items.Add(new MenuItem
            {
                Header = $"⚠ Cut off at the row limit: {rs.Rows.Count:N0} of {rs.TotalRowsText} rows fetched",
                IsEnabled = false
            });
            menu.Items.Add(new MenuItem { Header = "Rows in view", IsEnabled = false });
        }
        foreach (var (format, label) in ShownFormats)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => _ = ExportShownAsync(format);
            menu.Items.Add(item);
        }
        if (rs is not { IsTruncated: true }) return;

        menu.Items.Add(new Separator());
        if (rs.Rerun is null)
        {
            menu.Items.Add(new MenuItem { Header = "Full result: " + (rs.RerunUnavailable ?? "not available here"), IsEnabled = false });
            return;
        }
        var full = new MenuItem { Header = "Full result (runs the query again)" };
        foreach (var item in FullExportItems()) full.Items.Add(item);
        menu.Items.Add(full);
    }

    private IEnumerable<Control> FullExportItems()
    {
        var busy = _exportCts is not null;
        foreach (var (format, label) in FullFormats)
        {
            var item = new MenuItem { Header = label, IsEnabled = !busy };
            item.Click += (_, _) => _ = ExportFullAsync(format);
            yield return item;
        }
        yield return new Separator();
        yield return new MenuItem
        {
            Header = "Filters and hidden columns apply; rows keep the query's order",
            IsEnabled = false
        };
    }

    /// <summary>"Only the first N of M rows …" for a result cut off at the row limit; null otherwise.</summary>
    private static string? CutOffNote(ResultSetView rs) => rs.IsTruncated
        ? $"Only the first {rs.Rows.Count:N0} of {rs.TotalRowsText} rows: the result was cut off at the row limit."
        : null;

    private async Task ExportShownAsync(ExportFormat format)
    {
        if (ResultSet is not { } rs || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var file = await PickExportFileAsync(storage, format, SuggestedName(rs));
        if (file is null) return;

        // The rows and columns as shown now (rows are replaced, never changed, when edited); formatting and writing a
        // large result happen off the UI thread.
        var visible = VisibleColumns();
        var columns = Names(visible);
        var shown = _viewRows.ToList();
        var note = CutOffNote(rs);
        var html = new HtmlExportInfo(rs.SourceTable ?? rs.Title, DateTimeOffset.Now)
        {
            Source = rs.SourceTable is null ? rs.Title : rs.SourceTable,
            Connection = rs.Connection,
            FetchedRowCount = rs.Rows.Count,
            FetchedColumnCount = _columns.Count,
            IsTruncated = rs.IsTruncated,
            TotalRowCount = rs.TotalRowCount,
            TotalRowCountIsExact = rs.TotalRowCountIsExact
        };
        var options = FileOptions(rs, note is null ? [] : [note]);
        try
        {
            await using var stream = await file.OpenWriteAsync();
            RowCountText.Text = $"Exporting {shown.Count:N0} row(s)…";
            await Task.Run(async () =>
            {
                stream.SetLength(0);
                var rows = Project(shown, visible);
                if (ResultFileWriter.CanStream(format))
                {
                    ResultExporter.WriteAll(ResultFileWriter.Create(format, stream, options), columns, rows);
                    return;
                }
                var text = format == ExportFormat.Html
                    ? ResultExporter.ToHtml(columns, rows, html)
                    : ResultExporter.ToMarkdown(columns, rows) + (note is null ? "" : $"\n> {note}\n");
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                await writer.WriteAsync(text);
            });
            RowCountText.Text = $"Exported {shown.Count:N0} row(s) to {file.Name}" +
                                (note is null ? "" : rs.Rerun is null
                                    ? $" · first {rs.Rows.Count:N0} of {rs.TotalRowsText} only (row limit)"
                                    : $" · first {rs.Rows.Count:N0} of {rs.TotalRowsText} only: Export full result writes them all");
        }
        catch (Exception ex)
        {
            RowCountText.Text = "Export failed: " + ex.Message;
        }
    }

    private async Task ExportFullAsync(ExportFormat format)
    {
        if (ResultSet is not { Rerun: { } rerun } rs || _exportCts is not null ||
            TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        var file = await PickExportFileAsync(storage, format, SuggestedName(rs) + "-full");
        if (file is null || _exportCts is not null) return;

        var visible = VisibleColumns();
        var projection = new ExportProjection(visible.Select(c => c.Index).ToList(), _state.QuickFilter, _state.Filters.Values.ToList());
        var options = FileOptions(rs, []);
        var cts = new CancellationTokenSource();
        _exportCts = cts;
        _exportingView = rs;
        _exportOutcome = null;
        _exportProgressText = "Running the query again…";
        UpdateMoreRowsBar();
        var progress = new Progress<ExportProgress>(p =>
        {
            if (_exportCts != cts) return;
            _exportProgressText = projection.HasRowFilter
                ? $"Exporting… {p.RowsWritten:N0} row(s) written of {p.RowsRead:N0} read"
                : $"Exporting… {p.RowsWritten:N0} row(s) written";
            UpdateMoreRowsBar();
        });

        string outcome;
        var removeFile = false;
        try
        {
            FullExportResult result;
            await using (var stream = await file.OpenWriteAsync())
            {
                result = await rerun.ExportAsync(async (streamer, ct) =>
                {
                    stream.SetLength(0);
                    using var writer = ResultFileWriter.Create(format, stream, options);
                    return await FullResultExport.RunAsync(
                        (s, t) => streamer(s, t), rerun.ResultSetIndex, rs.Columns, writer, projection, progress, ct);
                }, cts.Token);
            }
            outcome = $"Exported the full result to {file.Name}: {result.RowsWritten:N0} row(s)" +
                      (projection.HasRowFilter ? $" of {result.RowsRead:N0} read (grid filters applied)" : "") +
                      $" in {result.Elapsed.TotalSeconds:N1} s." +
                      (result.Note is null ? "" : " " + result.Note);
        }
        catch (Exception ex) when (cts.IsCancellationRequested || ex is OperationCanceledException)
        {
            outcome = "Export cancelled; the unfinished file was removed.";
            removeFile = true;
        }
        catch (Exception ex)
        {
            outcome = "Export failed: " + ex.Message;
            removeFile = true;
        }
        finally
        {
            _exportCts = null;
            _exportingView = null;
            cts.Dispose();
        }

        if (removeFile)
        {
            try { await file.DeleteAsync(); }
            catch { outcome += $" ({file.Name} could not be removed and is incomplete.)"; }
        }
        file.Dispose();
        _exportOutcome = (rs, outcome);
        UpdateMoreRowsBar();
    }

    private static async Task<IStorageFile?> PickExportFileAsync(IStorageProvider storage, ExportFormat format, string name)
    {
        var extension = ResultExporter.FileExtension(format);
        return await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export results",
            SuggestedFileName = name + "." + extension,
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(format.ToString()) { Patterns = ["*." + extension] }]
        });
    }

    private static ResultFileOptions FileOptions(ResultSetView rs, IReadOnlyList<string> notes) => new()
    {
        SheetName = SuggestedName(rs),
        TargetTable = rs.SourceTable ?? rs.Quote("target_table"),
        Dialect = rs.Dialect,
        Quote = rs.Quote,
        Notes = notes
    };

    private void UpdateMoreRowsBar()
    {
        BuildExportMenus();
        var rs = ResultSet;
        var exporting = _exportCts is not null && ReferenceEquals(rs, _exportingView);
        var show = rs is { IsTruncated: true } && (rs.Rerun is not null || rs.RerunUnavailable is not null) || exporting;
        MoreRowsBar.IsVisible = show;
        if (!show || rs is null) return;

        CancelExportButton.IsVisible = exporting;
        var rerun = rs.Rerun;
        FetchNextButton.IsVisible = !exporting && rerun is { Step: > 0 and < int.MaxValue };
        FetchAllButton.IsVisible = !exporting && rerun is not null;
        ExportFullButton.IsVisible = !exporting && rerun is not null;
        ExportFullButton.IsEnabled = _exportCts is null;
        if (rerun is { Step: > 0 and < int.MaxValue } r) FetchNextButton.Content = $"Fetch next {r.Step:N0}";

        if (exporting)
        {
            MoreRowsText.Text = _exportProgressText;
            return;
        }
        var text = $"Showing the first {rs.Rows.Count:N0} of {rs.TotalRowsText} rows (row limit).";
        if (rerun is null && rs.RerunUnavailable is { } why) text += " " + why;
        if (_exportOutcome is { } done && ReferenceEquals(done.View, rs)) text += " " + done.Text;
        MoreRowsText.Text = text;
    }

    private void OnFetchNext(object? sender, RoutedEventArgs e)
    {
        if (ResultSet is not { Rerun: { } rerun } rs) return;
        _ = rerun.FetchAsync((int)Math.Min(int.MaxValue, (long)rs.Rows.Count(r => !r.IsNew) + rerun.Step));
    }

    private void OnFetchAll(object? sender, RoutedEventArgs e)
    {
        if (ResultSet is { Rerun: { } rerun }) _ = rerun.FetchAsync(int.MaxValue);
    }

    private void OnCancelExport(object? sender, RoutedEventArgs e)
    {
        _exportCts?.Cancel();
        _exportProgressText = "Cancelling…";
        UpdateMoreRowsBar();
    }
}
