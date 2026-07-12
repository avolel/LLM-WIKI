using System.Net;
using System.Net.Http.Json;
using LlmWiki.Api.Controllers;
using LlmWiki.Application.Ingestion;
using LlmWiki.Application.Linting;
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LlmWiki.Api.Tests;

public class LintEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    // Swap the real lint service + repository for hermetic fakes — no Oracle/LLM/filesystem.
    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureTestServices(services =>
        {
            services.RemoveAll<ILintService>();
            services.AddSingleton<ILintService, FakeLintService>();
            services.RemoveAll<IWikiRepository>();
            services.AddSingleton<IWikiRepository, FakeRepo>();
        })).CreateClient();

    private static readonly SuggestedFix Fix =
        new("concepts/ghost.md", "Ghost", PageType.Concept, "stub");
    private static readonly LintFinding FixBearing =
        new(LintSeverity.Warning, LintCategory.MissingPage, "links to concepts/ghost.md", ["entities/a.md"],
            "Create stub concepts/ghost.md.", Fix);
    private static readonly LintFinding Fixless =
        new(LintSeverity.Warning, LintCategory.Orphan, "orphan", ["entities/a.md"], "Link it.");

    [Fact]
    public async Task Post_Lint_KnownWiki_Returns200WithFindings()
    {
        var response = await Client().PostAsJsonAsync("/lint", new { wiki = "demo" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("concepts/ghost.md", body);
    }

    [Fact]
    public async Task Post_Lint_MissingWiki_Returns404()
    {
        var response = await Client().PostAsJsonAsync("/lint", new { wiki = "nope" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Post_Apply_FixBearingFinding_Returns200StubCreated()
    {
        var response = await Client().PostAsJsonAsync("/lint/apply",
            new ApplyFixRequest("demo", FixBearing));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var outcome = await response.Content.ReadFromJsonAsync<PageOutcome>();
        Assert.Equal(PageChange.StubCreated, outcome!.Change);
    }

    [Fact]
    public async Task Post_Apply_FixlessFinding_Returns400()
    {
        var response = await Client().PostAsJsonAsync("/lint/apply",
            new ApplyFixRequest("demo", Fixless));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class FakeLintService : ILintService
    {
        public Task<LintReport> LintAsync(string wikiName, CancellationToken cancellationToken = default)
            => Task.FromResult(new LintReport(wikiName, [FixBearing, Fixless]));

        public Task<PageOutcome> ApplyFixAsync(string wikiName, LintFinding finding, CancellationToken cancellationToken = default)
            => Task.FromResult(new PageOutcome(finding.Fix!.RelativePath, finding.Fix.Title, PageChange.StubCreated));
    }

    /// <summary>Only "demo" exists; every other method is unused by the controller path.</summary>
    private sealed class FakeRepo : IWikiRepository
    {
        public Task<bool> WikiExistsAsync(string wikiName, CancellationToken cancellationToken = default)
            => Task.FromResult(wikiName == "demo");

        public Task CreateWikiAsync(WikiSchema schema, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WikiInfo>> ListWikisAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WikiSchema> ReadSchemaAsync(string wikiName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WritePageAsync(string wikiName, string relativePath, WikiPage page, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WikiPage> ReadPageAsync(string wikiName, string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListPagesAsync(string wikiName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LinkResolutionReport> ResolveLinksAsync(string wikiName, string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
