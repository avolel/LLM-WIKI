using System.Net;
using System.Net.Http.Json;
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LlmWiki.Api.Tests;

public class ProjectEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    // Swap the real registry + repository + vector store for hermetic fakes — no Oracle/filesystem.
    private HttpClient Client(FakeProjectRepository projects) => factory.WithWebHostBuilder(b =>
        b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(projects);
            services.RemoveAll<IWikiRepository>();
            services.AddSingleton<IWikiRepository>(new FakeRepo(projects));
            services.RemoveAll<IVectorStore>();
            services.AddSingleton<IVectorStore>(new FakeVectorStore());
        })).CreateClient();

    [Fact]
    public async Task Get_Projects_ReturnsSeededList()
    {
        var projects = new FakeProjectRepository();
        projects.Rows["demo"] = new ProjectInfo("demo", DateTimeOffset.UtcNow, null, 3, 1);

        var response = await Client(projects).GetAsync("/projects");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("demo", body);
    }

    [Fact]
    public async Task Post_Project_CreatesRegistersAndReturns201()
    {
        var projects = new FakeProjectRepository();

        var response = await Client(projects).PostAsJsonAsync("/projects", new { name = "fresh" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(projects.Rows.ContainsKey("fresh"));
    }

    [Fact]
    public async Task Post_DuplicateProject_Returns409()
    {
        var projects = new FakeProjectRepository();
        projects.Rows["demo"] = new ProjectInfo("demo", DateTimeOffset.UtcNow, null, 0, 0);

        var response = await Client(projects).PostAsJsonAsync("/projects", new { name = "demo" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Delete_Project_Returns204_ThenGone()
    {
        var projects = new FakeProjectRepository();
        projects.Rows["demo"] = new ProjectInfo("demo", DateTimeOffset.UtcNow, null, 0, 0);
        var client = Client(projects);

        var del = await client.DeleteAsync("/projects/demo");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await client.GetAsync("/projects/demo");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);   // ⇒ WikiExistsAsync is false
        Assert.False(projects.Rows.ContainsKey("demo"));
    }

    [Fact]
    public async Task Delete_MissingProject_Returns404()
    {
        var response = await Client(new FakeProjectRepository()).DeleteAsync("/projects/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private sealed class FakeProjectRepository : IProjectRepository
    {
        public Dictionary<string, ProjectInfo> Rows { get; } = new();

        public Task RegisterAsync(string name, CancellationToken cancellationToken = default)
        {
            Rows.TryAdd(name, new ProjectInfo(name, DateTimeOffset.UtcNow, null, 0, 0));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProjectInfo>>(Rows.Values.OrderBy(p => p.Name).ToList());

        public Task<ProjectInfo?> GetAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(Rows.TryGetValue(name, out var p) ? p : null);

        public Task RecordIngestAsync(string name, int pageCount, int sourceCount, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            Rows.Remove(name);
            return Task.CompletedTask;
        }
    }

    /// <summary>Wiki existence is derived from the registry fake; create is a no-op (files not exercised here).</summary>
    private sealed class FakeRepo(FakeProjectRepository projects) : IWikiRepository
    {
        public Task<bool> WikiExistsAsync(string wikiName, CancellationToken cancellationToken = default)
            => Task.FromResult(projects.Rows.ContainsKey(wikiName));

        public Task CreateWikiAsync(WikiSchema schema, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteWikiAsync(string wikiName, CancellationToken cancellationToken = default)
        {
            projects.Rows.Remove(wikiName);   // existence is derived from the registry fake
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WikiInfo>> ListWikisAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WikiSchema> ReadSchemaAsync(string wikiName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WritePageAsync(string wikiName, string relativePath, WikiPage page, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WikiPage> ReadPageAsync(string wikiName, string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListPagesAsync(string wikiName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LinkResolutionReport> ResolveLinksAsync(string wikiName, string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Delete only needs a no-op vector arm; upsert/search aren't exercised by these tests.</summary>
    private sealed class FakeVectorStore : IVectorStore
    {
        public Task UpsertAsync(string wikiName, string relativePath, WikiPage page, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(string wikiName, string queryText, ReadOnlyMemory<float> queryEmbedding, int topK, PageType? typeFilter = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteWikiAsync(string wikiName, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
