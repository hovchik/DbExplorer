namespace DbExplorer.Core.Models;

/// <summary>One statement of a <see cref="ReadOnlyScript"/>: its text, where it starts in the script, and whether it
/// is a query that returns rows (SELECT, WITH, VALUES, TABLE) rather than a declaration or session setting.</summary>
public sealed record ReadOnlyStatement(string Text, int Offset, bool ReturnsRows);

/// <summary>
/// A script made only of statements that read: queries, variable declarations and session settings — no writes,
/// DDL, procedure calls, SELECT … INTO, cursors or transaction control. Stopping such a script's result sets at the row
/// limit changes nothing but the rows returned, so a provider may stop on the server instead of streaming (and
/// discarding) every row of a large table.
/// </summary>
public sealed record ReadOnlyScript(IReadOnlyList<ReadOnlyStatement> Statements);
