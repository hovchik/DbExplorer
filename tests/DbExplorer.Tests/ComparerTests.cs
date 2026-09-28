using DbExplorer.Application.Compare;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class TextDifferTests
{
    [Fact]
    public void Ignore_case_and_whitespace_only_affect_matching_not_display()
    {
        var diff = TextDiffer.Diff("SELECT a\n  FROM t", "select a\nFROM   t", SchemaCompareMode.LineByLine,
            new DiffOptions { IgnoreCase = true, IgnoreWhitespace = true });

        Assert.All(diff, d => Assert.Equal(DiffLineKind.Equal, d.Kind));
        Assert.Equal("SELECT a", diff[0].LeftText);
        Assert.Equal("select a", diff[0].RightText);
    }

    [Fact]
    public void Without_options_case_and_indentation_changes_are_reported()
    {
        var stats = TextDiffer.Statistics(TextDiffer.Diff("SELECT a\n  FROM t", "select a\nFROM t"));
        Assert.Equal(2, stats.Removed);
        Assert.Equal(2, stats.Added);
        Assert.Equal(1, stats.ChangeBlocks);
        Assert.Equal(0, stats.SimilarityPercent);
    }

    [Fact]
    public void Common_prefix_and_suffix_keep_line_numbers_aligned()
    {
        var left = string.Join('\n', Enumerable.Range(1, 50).Select(i => $"line {i}"));
        var right = left.Replace("line 25", "line twenty-five");
        var diff = TextDiffer.Diff(left, right);

        var removed = Assert.Single(diff, d => d.Kind == DiffLineKind.Removed);
        var added = Assert.Single(diff, d => d.Kind == DiffLineKind.Added);
        Assert.Equal(25, removed.LeftLineNumber);
        Assert.Equal(25, added.RightLineNumber);
        Assert.Equal(50, diff[^1].LeftLineNumber);
        Assert.Equal(50, diff[^1].RightLineNumber);
        Assert.Equal(51, diff.Count);
    }

    [Fact]
    public void Statistics_count_blocks_and_similarity()
    {
        var diff = TextDiffer.Diff("a\nb\nc\nd", "a\nX\nc\nY");
        var stats = TextDiffer.Statistics(diff);
        Assert.Equal(2, stats.Unchanged);
        Assert.Equal(2, stats.ChangeBlocks);
        Assert.Equal(50, stats.SimilarityPercent);
        Assert.Equal(new[] { 1, 4 }, TextDiffer.ChangeBlockStarts(diff));
        Assert.True(TextDiffer.Statistics(TextDiffer.Diff("", "")).IsIdentical);
    }

    [Fact]
    public void Collapse_keeps_context_around_changes()
    {
        var left = string.Join('\n', Enumerable.Range(1, 20).Select(i => i.ToString()));
        var right = left.Replace("\n10\n", "\nten\n");
        var collapsed = TextDiffer.CollapseUnchanged(TextDiffer.Diff(left, right), context: 2);

        Assert.Equal(DiffLineKind.Skipped, collapsed[0].Kind);
        Assert.Contains("7 unchanged", collapsed[0].LeftText);
        Assert.Equal("8", collapsed[1].LeftText);
        Assert.Equal(DiffLineKind.Skipped, collapsed[^1].Kind);
        Assert.Contains("8 unchanged", collapsed[^1].LeftText);
        Assert.Equal(8, collapsed.Count); // skip, 8, 9, -10, +ten, 11, 12, skip
    }
}

public class TableStructureComparerTests
{
    private static DbColumn Col(string name, int ordinal, string type, bool nullable = false, bool pk = false) =>
        new() { Name = name, Ordinal = ordinal, DataType = type, IsNullable = nullable, IsPrimaryKey = pk };

    [Fact]
    public void Reports_type_nullability_added_and_removed_columns()
    {
        var rows = TableStructureComparer.Compare(
            [Col("Id", 1, "int", pk: true), Col("Name", 2, "nvarchar(50)"), Col("Legacy", 3, "int", nullable: true)], [], [],
            [Col("id", 1, "int", pk: true), Col("Name", 2, "nvarchar(100)", nullable: true), Col("Email", 3, "nvarchar(200)")], [], []);

        Assert.Equal(StructureChange.Same, rows.Single(r => r.Name == "Id").Change);
        var name = rows.Single(r => r.Name == "Name");
        Assert.Equal(StructureChange.Changed, name.Change);
        Assert.Contains("type nvarchar(50) → nvarchar(100)", name.Details);
        Assert.Contains("now NULL", name.Details);
        Assert.Equal(StructureChange.OnlyLeft, rows.Single(r => r.Name == "Legacy").Change);
        Assert.Equal(StructureChange.OnlyRight, rows.Single(r => r.Name == "Email").Change);
    }

    [Fact]
    public void An_added_column_does_not_mark_later_columns_as_moved()
    {
        var rows = TableStructureComparer.Compare(
            [Col("A", 1, "int"), Col("B", 2, "int")], [], [],
            [Col("A", 1, "int"), Col("New", 2, "int"), Col("B", 3, "int")], [], []);

        Assert.Equal(StructureChange.Same, rows.Single(r => r.Name == "B").Change);
    }

    [Fact]
    public void Indexes_with_different_generated_names_are_reported_as_renames()
    {
        var left = new DbIndex { Name = "IX_abc123", Columns = "A, B", Type = "Nonclustered" };
        var right = new DbIndex { Name = "IX_def456", Columns = "A,B", Type = "NONCLUSTERED" };
        var pkLeft = new DbIndex { Name = "PK__T__1", IsPrimaryKey = true, Columns = "Id", Type = "Clustered" };
        var pkRight = new DbIndex { Name = "PK__T__2", IsPrimaryKey = true, Columns = "Id", Type = "Clustered" };

        var rows = TableStructureComparer.Compare([], [left, pkLeft], [], [], [right, pkRight], []);

        Assert.Equal(StructureChange.Same, rows.Single(r => r.Name == "(primary key)").Change);
        var renamed = rows.Single(r => r.Name == "IX_abc123");
        Assert.Equal(StructureChange.Changed, renamed.Change);
        Assert.Contains("renamed IX_abc123 → IX_def456", renamed.Details);
    }
}

public class DatabaseSchemaComparerTests
{
    private static MetadataSnapshot Snapshot(IReadOnlyList<DbObject> objects, IReadOnlyList<DbColumn> columns, IReadOnlyList<DbModule> modules) =>
        new() { Objects = objects, Columns = columns, Modules = modules, ForeignKeys = [], Indexes = [], RefreshedAt = DateTimeOffset.Now };

    [Fact]
    public void Classifies_every_object()
    {
        var left = Snapshot(
            [
                new DbObject { Schema = "dbo", Name = "T", Type = DbObjectType.Table },
                new DbObject { Schema = "dbo", Name = "Same", Type = DbObjectType.Table },
                new DbObject { Schema = "dbo", Name = "p", Type = DbObjectType.Procedure },
                new DbObject { Schema = "dbo", Name = "fmt", Type = DbObjectType.Procedure },
                new DbObject { Schema = "dbo", Name = "OldOnly", Type = DbObjectType.View },
                new DbObject { Schema = "dbo", Name = "seq", Type = DbObjectType.Sequence }
            ],
            [
                new DbColumn { Schema = "dbo", Table = "T", Name = "Id", DataType = "int", Ordinal = 1 },
                new DbColumn { Schema = "dbo", Table = "Same", Name = "Id", DataType = "int", Ordinal = 1 }
            ],
            [
                new DbModule { Schema = "dbo", Name = "p", Type = DbObjectType.Procedure, Definition = "CREATE PROC p AS SELECT 1" },
                new DbModule { Schema = "dbo", Name = "fmt", Type = DbObjectType.Procedure, Definition = "CREATE PROC fmt AS\n  SELECT 1" }
            ]);
        var right = Snapshot(
            [
                new DbObject { Schema = "DBO", Name = "t", Type = DbObjectType.Table },
                new DbObject { Schema = "dbo", Name = "Same", Type = DbObjectType.Table },
                new DbObject { Schema = "dbo", Name = "p", Type = DbObjectType.Procedure },
                new DbObject { Schema = "dbo", Name = "fmt", Type = DbObjectType.Procedure },
                new DbObject { Schema = "dbo", Name = "NewOnly", Type = DbObjectType.View },
                new DbObject { Schema = "dbo", Name = "seq", Type = DbObjectType.Sequence }
            ],
            [
                new DbColumn { Schema = "DBO", Table = "t", Name = "Id", DataType = "bigint", Ordinal = 1 },
                new DbColumn { Schema = "dbo", Table = "Same", Name = "Id", DataType = "int", Ordinal = 1 }
            ],
            [
                new DbModule { Schema = "dbo", Name = "p", Type = DbObjectType.Procedure, Definition = "CREATE PROC p AS SELECT 2" },
                new DbModule { Schema = "dbo", Name = "fmt", Type = DbObjectType.Procedure, Definition = "CREATE PROC fmt AS SELECT 1" }
            ]);

        var entries = DatabaseSchemaComparer.Compare(left, null, right, null);
        ObjectCompareStatus StatusOf(string name) => entries.Single(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Status;

        Assert.Equal(ObjectCompareStatus.Different, StatusOf("T"));
        Assert.Contains("1 column change(s)", entries.Single(e => e.Name == "T").Details);
        Assert.Equal(ObjectCompareStatus.Identical, StatusOf("Same"));
        Assert.Equal(ObjectCompareStatus.Different, StatusOf("p"));
        Assert.Equal(ObjectCompareStatus.Identical, StatusOf("fmt"));
        Assert.Equal(ObjectCompareStatus.OnlyLeft, StatusOf("OldOnly"));
        Assert.Equal(ObjectCompareStatus.OnlyRight, StatusOf("NewOnly"));
        Assert.Equal(ObjectCompareStatus.NotCompared, StatusOf("seq"));
        Assert.Equal(ObjectCompareStatus.Different, entries[0].Status); // most relevant first
    }

    [Fact]
    public void Only_objects_of_the_selected_databases_are_compared()
    {
        var left = Snapshot(
            [
                new DbObject { Database = "A", Schema = "dbo", Name = "X", Type = DbObjectType.Table },
                new DbObject { Database = "B", Schema = "dbo", Name = "Y", Type = DbObjectType.Table }
            ], [], []);
        var right = Snapshot([new DbObject { Database = "C", Schema = "dbo", Name = "X", Type = DbObjectType.Table }], [], []);

        var entry = Assert.Single(DatabaseSchemaComparer.Compare(left, "A", right, "C"));
        Assert.Equal(ObjectCompareStatus.Identical, entry.Status);
    }
}

public class DataMatchingTests
{
    private static IReadOnlyList<object?> Row(params object?[] values) => values;

    [Fact]
    public void Matches_by_key_and_counts_differences_per_column()
    {
        string[] cols = ["Id", "Name", "Price"];
        var result = ObjectComparisonServiceMatch(cols,
            [Row(1, "a", 1.50m), Row(2, "b", 2m), Row(3, "c", 3m)],
            [Row(1, "a", 1.5m), Row(2, "B", 2.10m), Row(4, "d", 4m)],
            ["Name", "Price"], ["Id"]);

        Assert.Equal(DataRowStatus.Same, result.Rows.Single(r => r.Key == "1").Status); // 1.50 == 1.5
        var two = result.Rows.Single(r => r.Key == "2");
        Assert.Equal(DataRowStatus.Different, two.Status);
        Assert.Equal(2, two.DifferentColumnCount);
        Assert.Equal(DataRowStatus.OnlyLeft, result.Rows.Single(r => r.Key == "3").Status);
        Assert.Equal(DataRowStatus.OnlyRight, result.Rows.Single(r => r.Key == "4").Status);
        Assert.Equal(new[] { "Name", "Price" }, result.ColumnDifferences.Select(c => c.Column));
        Assert.Equal(3, result.LeftRowCount);
    }

    [Fact]
    public void Options_ignore_case_and_padding()
    {
        string[] cols = ["Id", "Code"];
        var result = ObjectComparisonServiceMatch(cols, [Row(1, "abc  ")], [Row(1, "ABC")], ["Code"], ["Id"],
            new DataCompareOptions { IgnoreCase = true, TrimWhitespace = true });

        Assert.Equal(DataRowStatus.Same, Assert.Single(result.Rows).Status);
    }

    [Fact]
    public void Numbers_of_different_types_compare_by_value_and_binary_by_content()
    {
        string[] cols = ["Id", "N", "Bin"];
        var result = ObjectComparisonServiceMatch(cols,
            [Row(1, 5, new byte[] { 1, 2 }), Row(2, 5, new byte[] { 1, 2 })],
            [Row(1L, 5L, new byte[] { 1, 2 }), Row(2L, 5.0m, new byte[] { 1, 3 })],
            ["N", "Bin"], ["Id"]);

        Assert.Equal(DataRowStatus.Same, result.Rows.Single(r => r.Key == "1").Status);
        var two = result.Rows.Single(r => r.Key == "2");
        Assert.Equal(DataRowStatus.Different, two.Status);
        Assert.Equal("Bin", Assert.Single(two.Cells, c => c.IsDifferent).Column);
        Assert.Contains("0x0102 → 0x0103", two.Summary);
    }

    [Fact]
    public void Duplicate_keys_are_counted_and_composite_keys_are_readable()
    {
        string[] cols = ["A", "B", "V"];
        var result = ObjectComparisonServiceMatch(cols,
            [Row(1, null, "x"), Row(1, null, "y"), Row(1, "", "z")],
            [Row(1, null, "x")],
            ["V"], ["A", "B"]);

        Assert.Equal(1, result.LeftDuplicateKeys);
        Assert.Equal(DataRowStatus.Same, result.Rows.Single(r => r.Key == "1 | NULL").Status);
        Assert.Equal(DataRowStatus.OnlyLeft, result.Rows.Single(r => r.Key == "1 | ").Status); // NULL and '' are distinct keys
    }

    [Fact]
    public void Notes_explain_partial_results()
    {
        var notes = ComparisonReportBuilder.DataNotes(new DataComparisonResult
        {
            KeyColumns = ["Id"], ComparedColumns = ["Name"], LeftTruncated = true, RightDuplicateKeys = 2,
            ColumnsOnlyInRight = ["Extra"], IgnoredColumns = ["ModifiedAt"]
        });

        Assert.Contains(notes, n => n.StartsWith("Row limit reached on the left"));
        Assert.Contains(notes, n => n.Contains("0 in left, 2 in right"));
        Assert.Contains("Columns only in right (not compared): Extra.", notes);
        Assert.Contains("Ignored columns: ModifiedAt.", notes);
    }

    private static DataComparisonResult ObjectComparisonServiceMatch(
        string[] columns, IReadOnlyList<IReadOnlyList<object?>> left, IReadOnlyList<IReadOnlyList<object?>> right,
        string[] compared, string[] keys, DataCompareOptions? options = null) =>
        ObjectComparisonService.MatchRows(columns, left, columns, right, compared, keys, options);
}
