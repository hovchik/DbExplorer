using System.Text.RegularExpressions;
using DbExplorer.Application.Metadata;
using DbExplorer.Core.Models;

namespace DbExplorer.Application.Search;

/// <summary>
/// Searches object names, column names and procedure/function/view source code.
/// Runs entirely against the local snapshot: zero load and zero locks on the server.
/// </summary>
public sealed class MetadataSearchService
{
    private const int MaxLinesPerModule = 50;
    private const int MaxDetailLength = 300;

    /// <exception cref="ArgumentException">Invalid regular expression.</exception>
    public IReadOnlyList<MetadataSearchResult> Search(
        MetadataSnapshot snapshot, MetadataSearchQuery query, CancellationToken ct = default)
    {
        var results = new List<MetadataSearchResult>();
        if (string.IsNullOrWhiteSpace(query.Text) || query.Scope == MetadataSearchScope.None) return results;

        var regex = BuildRegex(query);

        bool Add(MetadataSearchResult r)
        {
            results.Add(r);
            return results.Count < query.MaxResults;
        }

        if (query.Scope.HasFlag(MetadataSearchScope.ObjectNames))
        {
            foreach (var o in snapshot.Objects)
            {
                ct.ThrowIfCancellationRequested();
                if (IsMatch(regex, o.Name) && !Add(new MetadataSearchResult(
                        MetadataMatchKind.Object, o.Schema, o.Name, o.Type, o.Type.ToString(), null)))
                    return results;
            }
        }

        if (query.Scope.HasFlag(MetadataSearchScope.ColumnNames))
        {
            var types = snapshot.Objects
                .Where(o => o.IsTableLike)
                .GroupBy(o => (o.Schema, o.Name))
                .ToDictionary(g => g.Key, g => g.First().Type);

            foreach (var c in snapshot.Columns)
            {
                ct.ThrowIfCancellationRequested();
                if (!IsMatch(regex, c.Name)) continue;
                var type = types.GetValueOrDefault((c.Schema, c.Table), DbObjectType.Table);
                if (!Add(new MetadataSearchResult(
                        MetadataMatchKind.Column, c.Schema, c.Table, type, $"{c.Name}  {c.DataType}", null)))
                    return results;
            }
        }

        if (query.Scope.HasFlag(MetadataSearchScope.Definitions))
        {
            foreach (var m in snapshot.Modules)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(m.Definition) || !IsMatch(regex, m.Definition)) continue;

                var lines = m.Definition.Split('\n');
                var found = 0;
                for (var i = 0; i < lines.Length && found < MaxLinesPerModule; i++)
                {
                    if (!IsMatch(regex, lines[i])) continue;
                    found++;
                    var detail = lines[i].Trim();
                    if (detail.Length > MaxDetailLength) detail = detail[..MaxDetailLength] + "…";
                    if (!Add(new MetadataSearchResult(
                            MetadataMatchKind.Definition, m.Schema, m.Name, m.Type, detail, i + 1)))
                        return results;
                }
            }
        }

        return results;
    }

    private static Regex BuildRegex(MetadataSearchQuery query)
    {
        var pattern = query.UseRegex ? query.Text : Regex.Escape(query.Text);
        if (query.WholeWord) pattern = $@"\b(?:{pattern})\b";

        var options = RegexOptions.CultureInvariant;
        if (!query.MatchCase) options |= RegexOptions.IgnoreCase;

        return new Regex(pattern, options, TimeSpan.FromSeconds(2));
    }

    private static bool IsMatch(Regex regex, string input)
    {
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
