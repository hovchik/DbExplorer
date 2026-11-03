using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using DbExplorer.Application.Export;
using DbExplorer.Application.Query;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

/// <summary>Writing a result row by row, and the full export that streams a re-run query straight into the file.</summary>
public class FullResultExportTests
{
    private static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static IEnumerable<IReadOnlyList<object?>> Numbers(int count) =>
        Enumerable.Range(1, count).Select(i => (IReadOnlyList<object?>)[i, $"name {i}"]);

    [Fact]
    public void Streamed_csv_json_and_insert_match_the_in_memory_exports()
    {
        string[] columns = ["id", "name"];
        var rows = Numbers(3).ToList();
        foreach (var format in new[] { ExportFormat.Csv, ExportFormat.Json, ExportFormat.Insert })
        {
            using var ms = new MemoryStream();
            var options = new ResultFileOptions { TargetTable = "t", Quote = q => $"[{q}]" };
            var written = ResultExporter.WriteAll(ResultFileWriter.Create(format, ms, options), columns, rows);
            Assert.Equal(3, written);

            var text = Encoding.UTF8.GetString(ms.ToArray()).TrimStart('﻿');
            var expected = format switch
            {
                ExportFormat.Csv => ResultExporter.ToCsv(columns, rows),
                ExportFormat.Json => ResultExporter.ToJson(columns, rows),
                _ => ResultExporter.ToInsertStatements(columns, rows, "t", SqlDialect.SqlServer, q => $"[{q}]")
            };
            Assert.Equal(expected, text);
        }
    }

    [Fact]
    public void Csv_file_starts_with_a_byte_order_mark_for_excel()
    {
        using var ms = new MemoryStream();
        ResultExporter.WriteAll(ResultFileWriter.Create(ExportFormat.Csv, ms), ["a"], [[1]]);
        Assert.Equal([0xEF, 0xBB, 0xBF], ms.ToArray()[..3]);
    }

    [Fact]
    public void Json_writer_flushes_large_results_to_the_file_as_it_goes()
    {
        using var ms = new MemoryStream();
        using var writer = ResultFileWriter.Create(ExportFormat.Json, ms);
        writer.Begin(["id", "name"]);
        foreach (var row in Numbers(20_000)) writer.WriteRow(row);
        // Most of it is in the file already, before Complete.
        Assert.True(ms.Length > 500_000, $"only {ms.Length} bytes written before Complete");
        writer.Complete();

        using var doc = JsonDocument.Parse(ms.ToArray());
        Assert.Equal(20_000, doc.RootElement.GetArrayLength());
        Assert.Equal("name 20000", doc.RootElement[19_999].GetProperty("name").GetString());
    }

    [Fact]
    public void Insert_script_carries_notes_as_comments()
    {
        var sql = ResultExporter.ToInsertStatements(["a"], [[1]], "t", SqlDialect.Postgres, q => q,
            ["Only the first 1 of 5 rows:\nthe result was cut off."]);
        Assert.Equal("-- Only the first 1 of 5 rows: the result was cut off.\nINSERT INTO t (a) VALUES (1);\n", sql);
    }

    [Fact]
    public void Excel_continues_on_new_sheets_past_the_sheet_row_limit()
    {
        using var ms = new MemoryStream();
        // 3 rows per sheet (header + 2): 5 rows take three sheets.
        var writer = new XlsxResultWriter(ms, "Orders", maxRowsPerSheet: 3);
        ResultExporter.WriteAll(writer, ["id", "name"], Numbers(5));
        Assert.Equal(3, writer.SheetCount);
        Assert.Contains("3 sheets", writer.Note);

        using var zip = new ZipArchive(new MemoryStream(ms.ToArray()), ZipArchiveMode.Read);
        var workbook = XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open());
        Assert.Equal(["Orders", "Orders (2)", "Orders (3)"],
            workbook.Descendants(Main + "sheet").Select(s => (string)s.Attribute("name")!));

        var rels = XDocument.Load(zip.GetEntry("xl/_rels/workbook.xml.rels")!.Open()).Descendants(Rel + "Relationship")
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);
        var types = new StreamReader(zip.GetEntry("[Content_Types].xml")!.Open()).ReadToEnd();
        var ids = new List<int>();
        foreach (var sheet in workbook.Descendants(Main + "sheet"))
        {
            var rid = (string)sheet.Attribute(XName.Get("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships"))!;
            var target = rels[rid];
            Assert.Contains($"/xl/{target}", types);
            var data = XDocument.Load(zip.GetEntry("xl/" + target)!.Open());
            var rows = data.Descendants(Main + "row").ToList();
            // Every sheet repeats the header.
            Assert.Equal("id", rows[0].Descendants(Main + "t").First().Value);
            ids.AddRange(rows.Skip(1).Select(r => int.Parse(r.Descendants(Main + "v").First().Value)));
        }
        Assert.Equal([1, 2, 3, 4, 5], ids);
    }

    [Fact]
    public void Excel_with_a_single_sheet_has_no_note()
    {
        using var ms = new MemoryStream();
        var writer = new XlsxResultWriter(ms, "R");
        ResultExporter.WriteAll(writer, ["id"], Numbers(2).Select(r => (IReadOnlyList<object?>)[r[0]]));
        Assert.Equal(1, writer.SheetCount);
        Assert.Null(writer.Note);
    }

    [Fact]
    public void Result_set_stream_takes_only_its_result_set_once()
    {
        var rows = new List<object?[]>();
        IReadOnlyList<string>? columns = null;
        var stream = new ResultSetStream(1, c => columns = c, rows.Add);
        Assert.False(stream.Takes(0));
        Assert.True(stream.Takes(1));

        stream.Feed(new QueryExecutionResult
        {
            ResultSets =
            [
                new QueryResultSet { Columns = ["x"], Rows = [[1]] },
                new QueryResultSet { Columns = ["a", "b"], Rows = [[1, DBNull.Value], [2, "z"]] }
            ]
        });
        Assert.Equal(["a", "b"], columns);
        Assert.Equal(2, stream.RowCount);
        Assert.Null(rows[0][1]);
        Assert.False(stream.Takes(1));
        stream.EnsureStarted();

        var missing = new ResultSetStream(3, _ => { }, _ => { });
        missing.Feed(new QueryExecutionResult());
        Assert.Throws<InvalidOperationException>(missing.EnsureStarted);
    }

    [Fact]
    public async Task Full_export_projects_columns_applies_filters_and_reports_progress()
    {
        var reports = new List<ExportProgress>();
        using var ms = new MemoryStream();
        using var writer = ResultFileWriter.Create(ExportFormat.Json, ms);
        var projection = new ExportProjection([2, 0], Filters: [new ColumnFilter(1) { Operator = ColumnFilterOperator.Equals, Value = "even" }]);

        var result = await FullResultExport.RunAsync(
            (stream, _) =>
            {
                stream.Begin(["id", "kind", "label"]);
                for (var i = 1; i <= 10; i++) stream.Write([i, i % 2 == 0 ? "even" : "odd", $"#{i}"]);
                return Task.CompletedTask;
            },
            0, ["id", "kind", "label"], writer, projection, new SyncProgress(reports.Add), CancellationToken.None);

        Assert.Equal(10, result.RowsRead);
        Assert.Equal(5, result.RowsWritten);
        Assert.Equal(new ExportProgress(10, 5), reports[^1]);
        using var doc = JsonDocument.Parse(ms.ToArray());
        var first = doc.RootElement[0];
        Assert.Equal(["label", "id"], first.EnumerateObject().Select(p => p.Name));
        Assert.Equal("#2", first.GetProperty("label").GetString());
        Assert.Equal([2, 4, 6, 8, 10], doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetInt32()));
    }

    [Fact]
    public async Task Full_export_refuses_a_query_that_now_returns_other_columns()
    {
        using var ms = new MemoryStream();
        using var writer = ResultFileWriter.Create(ExportFormat.Csv, ms);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => FullResultExport.RunAsync(
            (stream, _) => { stream.Begin(["id", "renamed"]); return Task.CompletedTask; },
            0, ["id", "name"], writer, new ExportProjection([0, 1]), null, CancellationToken.None));
        Assert.Contains("different columns", error.Message);
    }

    [Fact]
    public async Task Full_export_fails_when_the_result_set_is_gone()
    {
        using var ms = new MemoryStream();
        using var writer = ResultFileWriter.Create(ExportFormat.Csv, ms);
        await Assert.ThrowsAsync<InvalidOperationException>(() => FullResultExport.RunAsync(
            (_, _) => Task.CompletedTask, 1, ["id"], writer, new ExportProjection([0]), null, CancellationToken.None));
    }

    [Fact]
    public void Row_filter_keeps_what_the_grid_view_keeps()
    {
        IReadOnlyList<object?>[] rows = [[1, "Apple"], [2, "banana"], [3, null], [12, "cherry"]];
        var filters = new[] { new ColumnFilter(0) { Operator = ColumnFilterOperator.GreaterThan, Value = "1" } };
        var keep = ResultViewQuery.RowFilter("an", filters);
        var applied = ResultViewQuery.Apply(rows, r => r, "an", filters, []);
        Assert.Equal(applied, rows.Where(keep));
        Assert.True(ResultViewQuery.RowFilter(" ", [])(rows[2]));
    }

    /// <summary>Reports on the calling thread (Progress&lt;T&gt; posts to a thread pool when there is no context).</summary>
    private sealed class SyncProgress(Action<ExportProgress> report) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => report(value);
    }
}
