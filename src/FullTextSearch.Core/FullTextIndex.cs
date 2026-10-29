using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FullTextSearch.Core;

public sealed class FullTextIndex
{
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly Dictionary<string, SearchDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, List<int>>>> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _fieldLengths = new(StringComparer.Ordinal);

    public IndexStatistics Statistics
    {
        get
        {
            _lock.EnterReadLock();
            try { return new(_documents.Count, _postings.Count); }
            finally { _lock.ExitReadLock(); }
        }
    }

    public void AddOrUpdate(SearchDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(document.Id);
        ArgumentNullException.ThrowIfNull(document.Fields);
        var copy = new SearchDocument(document.Id,
            new Dictionary<string, string>(document.Fields, StringComparer.OrdinalIgnoreCase));

        _lock.EnterWriteLock();
        try
        {
            RemoveInternal(document.Id);
            _documents[document.Id] = copy;
            _fieldLengths[document.Id] = new(StringComparer.OrdinalIgnoreCase);

            foreach (var (field, value) in copy.Fields)
            {
                var tokens = TextAnalyzer.Analyze(value);
                _fieldLengths[document.Id][field] = tokens.Count;
                for (var position = 0; position < tokens.Count; position++)
                {
                    var term = tokens[position];
                    if (!_postings.TryGetValue(term, out var documents))
                        _postings[term] = documents = new(StringComparer.Ordinal);
                    if (!documents.TryGetValue(document.Id, out var fields))
                        documents[document.Id] = fields = new(StringComparer.OrdinalIgnoreCase);
                    if (!fields.TryGetValue(field, out var positions))
                        fields[field] = positions = [];
                    positions.Add(position);
                }
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool Delete(string documentId)
    {
        _lock.EnterWriteLock();
        try { return RemoveInternal(documentId); }
        finally { _lock.ExitWriteLock(); }
    }

    public SearchResponse Search(string query, SearchOptions? options = null)
    {
        options ??= new SearchOptions();
        if (options.Skip < 0 || options.Take is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(options), "Skip must be non-negative and Take must be between 1 and 1000.");

        var parsed = ParsedQuery.Parse(query);
        var timer = Stopwatch.StartNew();
        _lock.EnterReadLock();
        try
        {
            var allowedFields = options.Fields is null
                ? null
                : new HashSet<string>(options.Fields, StringComparer.OrdinalIgnoreCase);
            var positive = parsed.Clauses.Where(x => !x.Negated).Select(x => MatchClause(x, allowedFields)).ToArray();
            var scores = new Dictionary<string, double>(positive[0], StringComparer.Ordinal);

            foreach (var clauseMatches in positive.Skip(1))
            {
                if (parsed.Operator == QueryOperator.And)
                {
                    foreach (var documentId in scores.Keys.ToArray())
                    {
                        if (clauseMatches.TryGetValue(documentId, out var score)) scores[documentId] += score;
                        else scores.Remove(documentId);
                    }
                }
                else
                {
                    foreach (var (documentId, score) in clauseMatches)
                        scores[documentId] = scores.GetValueOrDefault(documentId) + score;
                }
            }

            foreach (var negative in parsed.Clauses.Where(x => x.Negated))
                foreach (var documentId in MatchClause(negative, allowedFields).Keys)
                    scores.Remove(documentId);

            var ranked = scores.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToArray();
            var hits = ranked.Skip(options.Skip).Take(options.Take)
                .Select(x => new SearchHit(x.Key, x.Value, _documents[x.Key].Fields,
                    BuildHighlights(_documents[x.Key], parsed.Clauses.Where(c => !c.Negated), allowedFields)))
                .ToArray();
            timer.Stop();
            return new SearchResponse(ranked.Length, hits, timer.ElapsedMilliseconds);
        }
        finally { _lock.ExitReadLock(); }
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        SearchDocument[] snapshot;
        _lock.EnterReadLock();
        try { snapshot = _documents.Values.ToArray(); }
        finally { _lock.ExitReadLock(); }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, cancellationToken: cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        File.Move(temporaryPath, fullPath, true);
    }

    public static async Task<FullTextIndex> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var index = new FullTextIndex();
        if (!File.Exists(path)) return index;
        await using var stream = File.OpenRead(path);
        var documents = await JsonSerializer.DeserializeAsync<SearchDocument[]>(stream, cancellationToken: cancellationToken) ?? [];
        foreach (var document in documents) index.AddOrUpdate(document);
        return index;
    }

    private Dictionary<string, double> MatchClause(QueryClause clause, HashSet<string>? allowedFields) =>
        clause.Kind switch
        {
            QueryClauseKind.Term => MatchTerms([clause.Terms[0]], allowedFields),
            QueryClauseKind.Prefix => MatchTerms(_postings.Keys.Where(x => x.StartsWith(clause.Terms[0], StringComparison.Ordinal)), allowedFields),
            QueryClauseKind.Phrase => MatchPhrase(clause.Terms, allowedFields),
            _ => []
        };

    private Dictionary<string, double> MatchTerms(IEnumerable<string> terms, HashSet<string>? allowedFields)
    {
        var matches = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            if (!_postings.TryGetValue(term, out var posting)) continue;
            var idf = CalculateIdf(posting.Count);
            foreach (var (documentId, fields) in posting)
            {
                var score = fields.Where(x => allowedFields is null || allowedFields.Contains(x.Key))
                    .Sum(x => CalculateBm25(idf, x.Value.Count, _fieldLengths[documentId][x.Key], AverageFieldLength(x.Key)));
                if (score > 0) matches[documentId] = matches.GetValueOrDefault(documentId) + score;
            }
        }
        return matches;
    }

    private Dictionary<string, double> MatchPhrase(IReadOnlyList<string> terms, HashSet<string>? allowedFields)
    {
        var matches = new Dictionary<string, double>(StringComparer.Ordinal);
        if (terms.Any(x => !_postings.ContainsKey(x))) return matches;

        foreach (var documentId in _postings[terms[0]].Keys)
        {
            foreach (var (field, starts) in _postings[terms[0]][documentId])
            {
                if (allowedFields is not null && !allowedFields.Contains(field)) continue;
                if (terms.Skip(1).Any(term => !_postings[term].TryGetValue(documentId, out var fields) || !fields.ContainsKey(field))) continue;
                var found = starts.Any(start => terms.Skip(1).Select((term, offset) =>
                    _postings[term][documentId][field].Contains(start + offset + 1)).All(x => x));
                if (!found) continue;

                var score = terms.Sum(term => CalculateBm25(CalculateIdf(_postings[term].Count),
                    _postings[term][documentId][field].Count, _fieldLengths[documentId][field], AverageFieldLength(field)));
                matches[documentId] = matches.GetValueOrDefault(documentId) + score * 1.5;
            }
        }
        return matches;
    }

    private static IReadOnlyDictionary<string, string> BuildHighlights(
        SearchDocument document, IEnumerable<QueryClause> clauses, HashSet<string>? allowedFields)
    {
        var patterns = clauses.Select(clause => clause.Kind switch
        {
            QueryClauseKind.Phrase => string.Join(@"\s+", clause.Terms.Select(Regex.Escape)),
            QueryClauseKind.Prefix => Regex.Escape(clause.Terms[0]) + @"[\p{L}\p{N}]*",
            _ => Regex.Escape(clause.Terms[0])
        }).ToArray();
        var combinedPattern = string.Join("|", patterns.Select(x => $"(?:{x})"));
        var highlights = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, value) in document.Fields)
        {
            if (allowedFields is not null && !allowedFields.Contains(field)) continue;
            var encoded = WebUtility.HtmlEncode(value);
            var highlighted = Regex.Replace(encoded, combinedPattern, "<em>$0</em>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (highlighted != encoded) highlights[field] = highlighted;
        }
        return highlights;
    }

    private bool RemoveInternal(string documentId)
    {
        if (!_documents.Remove(documentId)) return false;
        _fieldLengths.Remove(documentId);
        foreach (var term in _postings.Keys.ToArray())
        {
            _postings[term].Remove(documentId);
            if (_postings[term].Count == 0) _postings.Remove(term);
        }
        return true;
    }

    private double AverageFieldLength(string field)
    {
        var lengths = _fieldLengths.Values.Where(x => x.ContainsKey(field)).Select(x => x[field]).ToArray();
        return lengths.Length == 0 ? 1 : lengths.Average();
    }

    private double CalculateIdf(int documentFrequency) =>
        Math.Log(1 + (_documents.Count - documentFrequency + 0.5) / (documentFrequency + 0.5));

    private static double CalculateBm25(double idf, int frequency, int length, double averageLength)
    {
        const double k1 = 1.2;
        const double b = 0.75;
        return idf * frequency * (k1 + 1) /
               (frequency + k1 * (1 - b + b * length / Math.Max(1, averageLength)));
    }
}
