using FullTextSearch.Core;

var tests = new List<(string Name, Func<Task> Run)>
{
    ("Russian text is normalized and found", () => Run(() =>
    {
        var index = SampleIndex();
        Assert(index.Search("ДВИЖКА").Hits.Single().Id == "1", "Expected case-insensitive Russian match.");
    })),
    ("AND requires every term", () => Run(() =>
    {
        var index = SampleIndex();
        Assert(index.Search("разработка AND c").Hits.Single().Id == "1", "AND result is incorrect.");
    })),
    ("OR returns either term", () => Run(() =>
    {
        var index = SampleIndex();
        Assert(index.Search("поиск OR рецепты").Total == 2, "OR result is incorrect.");
    })),
    ("Phrase requires adjacent terms in order", () => Run(() =>
    {
        var index = SampleIndex();
        Assert(index.Search("\"полнотекстовый поиск\"").Hits.Single().Id == "1", "Phrase was not found.");
        Assert(index.Search("\"поиск полнотекстовый\"").Total == 0, "Reversed phrase must not match.");
    })),
    ("Prefix expands matching terms", () => Run(() =>
    {
        var index = SampleIndex();
        Assert(index.Search("разраб*").Hits.Single().Id == "1", "Prefix result is incorrect.");
    })),
    ("NOT excludes matching documents", () => Run(() =>
    {
        var index = SampleIndex();
        Assert(index.Search("поиск OR рецепты NOT выпечка").Hits.Single().Id == "1", "NOT result is incorrect.");
    })),
    ("Matches are highlighted safely", () => Run(() =>
    {
        var index = SampleIndex();
        var hit = index.Search("поиск").Hits.Single();
        Assert(hit.Highlights["title"].Contains("<em>поиск</em>", StringComparison.OrdinalIgnoreCase), "Highlight is missing.");
    })),
    ("Update removes old terms", () => Run(() =>
    {
        var index = SampleIndex();
        index.AddOrUpdate(new("1", new Dictionary<string, string> { ["title"] = "Новый документ" }));
        Assert(index.Search("движка").Total == 0, "Old terms survived update.");
    })),
    ("Delete removes document", () => Run(() =>
    {
        var index = SampleIndex();
        Assert(index.Delete("1") && index.Search("движка").Total == 0, "Delete failed.");
    })),
    ("Snapshot round-trip", async () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"fts-{Guid.NewGuid():N}.json");
        try
        {
            var index = SampleIndex();
            await index.SaveAsync(path);
            var restored = await FullTextIndex.LoadAsync(path);
            Assert(restored.Search("движка").Hits.Single().Id == "1", "Snapshot restore failed.");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    })
};

var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failures++; Console.WriteLine($"FAIL {test.Name}: {exception.Message}"); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} tests passed.");
return failures == 0 ? 0 : 1;

static FullTextIndex SampleIndex()
{
    var index = new FullTextIndex();
    index.AddOrUpdate(new("1", new Dictionary<string, string>
    {
        ["title"] = "Полнотекстовый поиск",
        ["body"] = "Разработка поискового движка на C#"
    }));
    index.AddOrUpdate(new("2", new Dictionary<string, string>
    {
        ["title"] = "Кулинарные рецепты",
        ["body"] = "Домашняя выпечка"
    }));
    return index;
}

static Task Run(Action action) { action(); return Task.CompletedTask; }
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
