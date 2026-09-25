using DbExplorer.Core.Models;

namespace DbExplorer.Application.Search;

[Flags]
public enum MetadataSearchScope
{
    None = 0,
    ObjectNames = 1,
    ColumnNames = 2,
    Definitions = 4,
    All = ObjectNames | ColumnNames | Definitions
}

public sealed record MetadataSearchQuery(
    string Text,
    MetadataSearchScope Scope,
    bool MatchCase = false,
    bool WholeWord = false,
    bool UseRegex = false,
    int MaxResults = 5000);

public enum MetadataMatchKind
{
    Object,
    Column,
    Definition
}

public sealed record MetadataSearchResult(
    MetadataMatchKind Kind,
    string Schema,
    string ObjectName,
    DbObjectType ObjectType,
    string Detail,
    int? Line);
