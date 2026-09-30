namespace FullTextSearch.Core;

internal enum QueryOperator { Or, And }

internal sealed record ParsedQuery(QueryOperator Operator, IReadOnlyList<string> Terms)
{
    public static ParsedQuery Parse(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Search query cannot be empty.", nameof(query));

        var raw = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var operation = raw.Any(x => x.Equals("AND", StringComparison.OrdinalIgnoreCase))
            ? QueryOperator.And
            : QueryOperator.Or;

        var terms = raw
            .Where(x => !x.Equals("AND", StringComparison.OrdinalIgnoreCase)
                     && !x.Equals("OR", StringComparison.OrdinalIgnoreCase))
            .SelectMany(TextAnalyzer.Analyze)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (terms.Length == 0)
            throw new ArgumentException("Search query contains no searchable terms.", nameof(query));

        return new ParsedQuery(operation, terms);
    }
}
