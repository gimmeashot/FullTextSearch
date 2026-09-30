using System.Diagnostics;
using System.Text.Json;

namespace FullTextSearch.Core;

public sealed class FullTextIndex
{
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly Dictionary<string, SearchDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, int>>> _postings = new(StringComparer.Ordinal);
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

        var copy = new SearchDocument(
            document.Id,
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
                foreach (var group in tokens.GroupBy(x => x, StringComparer.Ordinal))
                {
                    if (!_postings.TryGetValue(group.Key, out var documents))
                        _postings[group.Key] = documents = new(StringComparer.Ordinal);
                    if (!documents.TryGetValue(document.Id, out var fields))
                        documents[document.Id] = fields = new(StringComparer.OrdinalIgnoreCase);
                    fields[field] = group.Count();
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
            var matches = new Dictionary<string, (double Score, int MatchedTerms)>(StringComparer.Ordinal);

            foreach (var term in parsed.Terms)
            {
                if (!_postings.TryGetValue(term, out var posting))
                    continue;

                var documentFrequency = posting.Count;
                var idf = Math.Log(1 + (_documents.Count - documentFrequency + 0.5) / (documentFrequency + 0.5));
                foreach (var (documentId, fields) in posting)
                {
                    var score = fields
                        .Where(x => allowedFields is null || allowedFields.Contains(x.Key))
                        .Sum(x => CalculateBm25(idf, x.Value, _fieldLengths[documentId][x.Key], AverageFieldLength(x.Key)));
                    if (score <= 0) continue;
                    var previous = matches.GetValueOrDefault(documentId);
                    matches[documentId] = (previous.Score + score, previous.MatchedTerms + 1);
                }
            }

            IEnumerable<KeyValuePair<string, (double Score, int MatchedTerms)>> filtered = matches;
            if (parsed.Operator == QueryOperator.And)
                filtered = filtered.Where(x => x.Value.MatchedTerms == parsed.Terms.Count);

            var ranked = filtered
                .OrderByDescending(x => x.Value.Score)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .ToArray();
            var hits = ranked
                .Skip(options.Skip)
                .Take(options.Take)
                .Select(x => new SearchHit(x.Key, x.Value.Score, _documents[x.Key].Fields))
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

    private static double CalculateBm25(double idf, int frequency, int length, double averageLength)
    {
        const double k1 = 1.2;
        const double b = 0.75;
        return idf * frequency * (k1 + 1) /
               (frequency + k1 * (1 - b + b * length / Math.Max(1, averageLength)));
    }
}
