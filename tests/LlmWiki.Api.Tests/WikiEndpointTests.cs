using System.Net;
using System.Net.Http.Json;
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LlmWiki.Api.Tests;

public class WikiEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    // Swap the real repository for a hermetic fake — no Oracle/filesystem.
    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWikiRepository>();
            services.AddSingleton<IWikiRepository, FakeRepo>();
        })).CreateClient();

    [Fact]
    public async Task Get_Tree_KnownWiki_GroupsByCategory()
    {
        var response = await Client().GetAsync("/wikis/demo/pages");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("entities", body);
        Assert.Contains("summaries", body);
        Assert.Contains("entities/acme.md", body);
    }

    [Fact]
    public async Task Get_Tree_UnknownWiki_Returns404()
    {
        var response = await Client().GetAsync("/wikis/nope/pages");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Get_Page_KnownPath_ReturnsContentWithStringEnum()
    {
        var response = await Client().GetAsync("/wikis/demo/pages/entities/acme.md");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Acme body", body);
        // string-enum regression: PageType serializes as its name, not the integer 1.
        Assert.Contains("\"Entity\"", body);
    }

    [Fact]
    public async Task Get_Page_MissingPath_Returns404()
    {
        var response = await Client().GetAsync("/wikis/demo/pages/entities/ghost.md");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>"demo" has two pages; a missing page throws FileNotFoundException (as the file store does).</summary>
    private sealed class FakeRepo : IWikiRepository
    {
        public Task<bool> WikiExistsAsync(string wikiName, CancellationToken cancellationToken = default)
            => Task.FromResult(wikiName == "demo");

        public Task<IReadOnlyList<string>> ListPagesAsync(string wikiName, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(["entities/acme.md", "summaries/overview.md"]);

        public Task<WikiPage> ReadPageAsync(string wikiName, string relativePath, CancellationToken cancellationToken = default)
            => relativePath == "entities/acme.md"
                ? Task.FromResult(new WikiPage { Title = "Acme", Type = PageType.Entity, Content = "Acme body" })
                : throw new FileNotFoundException(relativePath);

        public Task CreateWikiAsync(WikiSchema schema, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteWikiAsync(string wikiName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WikiInfo>> ListWikisAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WikiSchema> ReadSchemaAsync(string wikiName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WritePageAsync(string wikiName, string relativePath, WikiPage page, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LinkResolutionReport> ResolveLinksAsync(string wikiName, string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
