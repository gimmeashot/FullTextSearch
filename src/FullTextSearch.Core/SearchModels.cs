namespace FullTextSearch.Core;

public sealed record SearchDocument(string Id, IReadOnlyDictionary<string, string> Fields);

public sealed record SearchHit(
    string Id,
    double Score,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyDictionary<string, string> Highlights);

public sealed record SearchResponse(
    int Total,
    IReadOnlyList<SearchHit> Hits,
    long ElapsedMilliseconds);

public sealed record IndexStatistics(int DocumentCount, int TermCount);

public sealed class SearchOptions
{
    public int Skip { get; init; }
    public int Take { get; init; } = 20;
    public IReadOnlyCollection<string>? Fields { get; init; }
}
