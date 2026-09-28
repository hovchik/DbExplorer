using DbExplorer.Application.Query;
using DbExplorer.Core.Models;

namespace DbExplorer.Tests;

public class MultiDatabaseQueryServiceTests
{
    private static DatabaseRunResult Ok(string db, params QueryResultSet[] sets) =>
        new(db, new QueryExecutionResult { ResultSets = sets }, null);

    private static QueryResultSet Set(string[] columns, params object?[][] rows) =>
        new() { Columns = columns, Rows = rows.Select(r => (IReadOnlyList<object?>)r).ToList() };

    [Fact]
    public void Same_shaped_sets_are_stacked_with_database_column()
    {
        var merged = MultiDatabaseQueryService.Merge(
        [
            Ok("db1", Set(["Id", "Name"], [1, "a"], [2, "b"])),
            Ok("db2", Set(["id", "NAME"], [3, "c"]))
        ]);

        var set = Assert.Single(merged);
        Assert.Equal("Result set 1", set.Title);
        Assert.Equal(["Database", "Id", "Name"], set.Columns);
        Assert.Equal(3, set.Rows.Count);
        Assert.Equal(["db2", 3, "c"], set.Rows[2]);
        Assert.All(set.Rows, r => Assert.IsType<object?[]>(r));
    }

    [Fact]
    public void Different_shapes_become_separate_grids()
    {
        var merged = MultiDatabaseQueryService.Merge(
        [
            Ok("db1", Set(["Id"], [1])),
            Ok("db2", Set(["Id", "Extra"], [2, "x"])),
            Ok("db3", Set(["Id"], [3]))
        ]);

        Assert.Equal(["Result set 1 (a)", "Result set 1 (b)"], merged.Select(m => m.Title));
        Assert.Equal(2, merged[0].Rows.Count);
        Assert.Single(merged[1].Rows);
    }

    [Fact]
    public void Multiple_result_sets_and_failures_are_handled()
    {
        var merged = MultiDatabaseQueryService.Merge(
        [
            Ok("db1", Set(["A"], [1]), Set(["B"], [2])),
            new DatabaseRunResult("db2", null, "boom"),
            Ok("db3", Set(["A"], [3]))
        ]);

        Assert.Equal(["Result set 1", "Result set 2"], merged.Select(m => m.Title));
        Assert.Equal(2, merged[0].Rows.Count);
        Assert.Single(merged[1].Rows);
    }

    [Fact]
    public void Existing_database_column_is_not_shadowed()
    {
        var merged = MultiDatabaseQueryService.Merge([Ok("db1", Set(["Database"], ["x"]))]);
        Assert.Equal(["Database_2", "Database"], merged[0].Columns);
    }
}
