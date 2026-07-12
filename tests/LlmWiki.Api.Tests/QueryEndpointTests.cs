using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmWiki.Api.Controllers;
using LlmWiki.Application.Ingestion;
using LlmWiki.Application.Ports;
using LlmWiki.Application.Query;
using LlmWiki.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LlmWiki.Api.Tests;

public class QueryEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    // Swap the real query service + repository for hermetic fakes — no Oracle/LLM/filesystem.
    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IQueryService>();
            services.AddSingleton<IQueryService, FakeQueryService>();
            services.RemoveAll<IWikiRepository>();
            services.AddSingleton<IWikiRepository, FakeRepo>();
        })).CreateClient();

    // The API now emits enums as their string names (Phase 8); match that when deserializing responses.
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Post_KnownWiki_Returns200WithResult()
    {
        var response = await Client().PostAsJsonAsync("/query",
            new { wiki = "demo", question = "how does it work?" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("canned answer", body);
        Assert.Contains("entities/acme.md", body);
    }

    [Fact]
    public async Task Post_MissingWiki_Returns404()
    {
        var response = await Client().PostAsJsonAsync("/query",
            new { wiki = "nope", question = "how does it work?" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_Result_SerializesCitationTypeAsStringEnum()
    {
        var response = await Client().PostAsJsonAsync("/query",
            new { wiki = "demo", question = "how does it work?" });

        var body = await response.Content.ReadAsStringAsync();
        // string-enum regression: Citation.Type is "Entity", not the integer 1.
        Assert.Contains("\"Entity\"", body);
    }

    [Fact]
    public async Task Save_CoveredResult_Returns200WithOutcome()
    {
        var result = new QueryResult("demo", "q", "answer", true,
            [new Citation("entities/acme.md", "Acme", PageType.Entity)], "Canned Answer");

        var response = await Client().PostAsJsonAsync("/query/save",
            new SaveAnswerRequest("demo", result));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var outcome = await response.Content.ReadFromJsonAsync<PageOutcome>(Json);
        Assert.Equal(PageChange.Created, outcome!.Change);
    }

    [Fact]
    public async Task Save_UncoveredResult_Returns400()
    {
        var result = new QueryResult("demo", "q", "gap", false, [], "");

        var response = await Client().PostAsJsonAsync("/query/save",
            new SaveAnswerRequest("demo", result));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Save_MissingWiki_Returns404()
    {
        var result = new QueryResult("nope", "q", "answer", true, [], "T");

        var response = await Client().PostAsJsonAsync("/query/save",
            new SaveAnswerRequest("nope", result));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class FakeQueryService : IQueryService
    {
        public Task<QueryResult> AnswerAsync(string wikiName, string question,
            IReadOnlyList<ConversationTurn> history, QueryOptions options, CancellationToken cancellationToken = default)
            => Task.FromResult(new QueryResult(
                wikiName, question, "canned answer", true,
                [new Citation("entities/acme.md", "Acme", PageType.Entity)], "Canned Answer"));

        public Task<PageOutcome> SaveAnswerAsync(string wikiName, QueryResult result, CancellationToken cancellationToken = default)
            => Task.FromResult(new PageOutcome("answers/canned-answer.md", result.SuggestedTitle, PageChange.Created));
    }

    /// <summary>Only "demo" exists; every other method is unused by the controller path.</summary>
    private sealed class FakeRepo : IWikiRepository
    {
        public Task<bool> WikiExistsAsync(string wikiName, CancellationToken cancellationToken = default)
            => Task.FromResult(wikiName == "demo");

        public Task CreateWikiAsync(WikiSchema schema, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteWikiAsync(string wikiName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WikiInfo>> ListWikisAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WikiSchema> ReadSchemaAsync(string wikiName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WritePageAsync(string wikiName, string relativePath, WikiPage page, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WikiPage> ReadPageAsync(string wikiName, string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListPagesAsync(string wikiName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LinkResolutionReport> ResolveLinksAsync(string wikiName, string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
