namespace DbExplorer.Core.Models;

/// <summary>
/// Cumulative row modifications of one table since the server started tracking them (SQL Server:
/// sys.dm_db_index_operational_stats; PostgreSQL: pg_stat_user_tables). Only the difference between two readings
/// means anything: the change recorder reads them before and after an action to find the tables it touched.
/// </summary>
public sealed record TableChangeCounter
{
    public string Database { get; init; } = "";
    public string Schema { get; init; } = "";
    public string Table { get; init; } = "";
    public long Inserts { get; init; }
    public long Updates { get; init; }
    public long Deletes { get; init; }

    public long Total => Inserts + Updates + Deletes;
}
