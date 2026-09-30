using FullTextSearch.Core;

var builder = WebApplication.CreateBuilder(args);
var dataPath = builder.Configuration["IndexPath"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "index.json");
var index = await FullTextIndex.LoadAsync(dataPath);
builder.Services.AddSingleton(index);

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    name = "FullTextSearch",
    endpoints = new[] { "PUT /documents/{id}", "DELETE /documents/{id}", "GET /search?q=...", "GET /stats", "POST /commit" }
}));

app.MapPut("/documents/{id}", (string id, DocumentRequest request, FullTextIndex searchIndex) =>
{
    searchIndex.AddOrUpdate(new SearchDocument(id, request.Fields));
    return Results.NoContent();
});

app.MapDelete("/documents/{id}", (string id, FullTextIndex searchIndex) =>
    searchIndex.Delete(id) ? Results.NoContent() : Results.NotFound());

app.MapGet("/search", (string q, int? skip, int? take, string? fields, FullTextIndex searchIndex) =>
{
    var options = new SearchOptions
    {
        Skip = skip ?? 0,
        Take = take ?? 20,
        Fields = fields?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    };
    return Results.Ok(searchIndex.Search(q, options));
});

app.MapGet("/stats", (FullTextIndex searchIndex) => searchIndex.Statistics);
app.MapPost("/commit", async (FullTextIndex searchIndex, CancellationToken cancellationToken) =>
{
    await searchIndex.SaveAsync(dataPath, cancellationToken);
    return Results.NoContent();
});

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status400BadRequest;
    await Results.Problem(statusCode: 400, title: "Invalid request").ExecuteAsync(context);
}));

app.Run();

public sealed record DocumentRequest(Dictionary<string, string> Fields);

public partial class Program;
