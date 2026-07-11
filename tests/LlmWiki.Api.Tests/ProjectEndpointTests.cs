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
    // Swap the real registry + repository for hermetic fakes — no Oracle/filesystem.
    private HttpClient Client(FakeProjectRepository projects) => factory.WithWebHostBuilder(b =>
        b.ConfigureTestServices(services =>
        {
            services.RemoveAll<IProjectRepository>();
            services.AddSingleton<IProjectRepository>(projects);
            services.RemoveAll<IWikiRepository>();
            services.AddSingleton<IWikiRepository>(new FakeRepo(projects));
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
    }

    /// <summary>Wiki existence is derived from the registry fake; create is a no-op (files not exercised here).</summary>
    private sealed class FakeRepo(FakeProjectRepository projects) : IWikiRepository
    {
        public Task<bool> WikiExistsAsync(string wikiName, CancellationToken cancellationToken = default)
            => Task.FromResult(projects.Rows.ContainsKey(wikiName));

        public Task CreateWikiAsync(WikiSchema schema, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<WikiInfo>> ListWikisAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WikiSchema> ReadSchemaAsync(string wikiName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task WritePageAsync(string wikiName, string relativePath, WikiPage page, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WikiPage> ReadPageAsync(string wikiName, string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListPagesAsync(string wikiName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LinkResolutionReport> ResolveLinksAsync(string wikiName, string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
