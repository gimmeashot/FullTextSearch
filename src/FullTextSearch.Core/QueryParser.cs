using System.Text.RegularExpressions;

namespace FullTextSearch.Core;

internal enum QueryOperator { Or, And }
internal enum QueryClauseKind { Term, Prefix, Phrase }

internal sealed record QueryClause(QueryClauseKind Kind, IReadOnlyList<string> Terms, bool Negated);

internal sealed record ParsedQuery(QueryOperator Operator, IReadOnlyList<QueryClause> Clauses)
{
    private static readonly Regex Parts = new("\"[^\"]+\"|\\S+", RegexOptions.Compiled);

    public static ParsedQuery Parse(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("Search query cannot be empty.", nameof(query));

        var operation = QueryOperator.Or;
        var negateNext = false;
        var clauses = new List<QueryClause>();

        foreach (Match match in Parts.Matches(query))
        {
            var part = match.Value;
            if (part.Equals("AND", StringComparison.OrdinalIgnoreCase))
            {
                operation = QueryOperator.And;
                continue;
            }
            if (part.Equals("OR", StringComparison.OrdinalIgnoreCase)) continue;
            if (part.Equals("NOT", StringComparison.OrdinalIgnoreCase))
            {
                negateNext = true;
                continue;
            }

            var isPhrase = part.Length >= 2 && part[0] == '"' && part[^1] == '"';
            var isPrefix = !isPhrase && part.EndsWith('*');
            var source = isPhrase ? part[1..^1] : isPrefix ? part[..^1] : part;
            var terms = TextAnalyzer.Analyze(source);
            if (terms.Count == 0) continue;

            clauses.Add(new QueryClause(
                isPhrase ? QueryClauseKind.Phrase : isPrefix ? QueryClauseKind.Prefix : QueryClauseKind.Term,
                isPrefix ? [terms[0]] : terms,
                negateNext));
            negateNext = false;
        }

        if (clauses.All(x => x.Negated))
            throw new ArgumentException("Search query must contain at least one positive clause.", nameof(query));

        return new ParsedQuery(operation, clauses);
    }
}
