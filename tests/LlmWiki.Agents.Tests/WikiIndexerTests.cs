using LlmWiki.Agents.Indexing;
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using LlmWiki.Infrastructure.FileStore;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Agents.Tests;

public sealed class WikiIndexerTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "llmwiki-reindex-tests", Guid.NewGuid().ToString("N"));
    private readonly IWikiRepository _repo;

    public WikiIndexerTests()
    {
        var files = new FileSystemWikiFileStore(Options.Create(new WikiOptions { RootPath = _root }));
        _repo = new FileSystemWikiRepository(files);
    }

    [Fact]
    public async Task Reindex_EmbedsEveryExistingPage()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        await _repo.WritePageAsync("alpha", "entities/acme-corp.md",
            new WikiPage { Title = "Acme Corp", Type = PageType.Entity, Content = "An anvil maker." });
        await _repo.WritePageAsync("alpha", "concepts/anvils.md",
            new WikiPage { Title = "Anvils", Type = PageType.Concept, Content = "Heavy metal blocks." });

        var vectors = new RecordingVectorStore();
        var indexer = new WikiIndexer(_repo, new FakeEmbeddingService(), vectors,
            Options.Create(new EmbeddingOptions()));

        var report = await indexer.ReindexAsync("alpha");

        Assert.False(report.HasFailures);
        Assert.Equal(2, report.Embedded);
        Assert.Contains(vectors.Upserts, u => u.Path == "entities/acme-corp.md");
        Assert.Contains(vectors.Upserts, u => u.Path == "concepts/anvils.md");
    }

    [Fact]
    public async Task Reindex_VectorStoreFailure_IsRecordedNotThrown()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        await _repo.WritePageAsync("alpha", "entities/acme-corp.md",
            new WikiPage { Title = "Acme Corp", Type = PageType.Entity, Content = "x" });

        var indexer = new WikiIndexer(_repo, new FakeEmbeddingService(), new ThrowingVectorStore(),
            Options.Create(new EmbeddingOptions()));

        var report = await indexer.ReindexAsync("alpha");   // must not throw (NFR-06)

        Assert.True(report.HasFailures);
        Assert.Equal(0, report.Embedded);
        Assert.Contains(report.Failures, f => f.RelativePath == "entities/acme-corp.md");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<ReadOnlyMemory<float>>(new float[768]);
    }

    private sealed class RecordingVectorStore : IVectorStore
    {
        public List<(string Wiki, string Path)> Upserts { get; } = [];
        public Task UpsertAsync(string wikiName, string relativePath, WikiPage page,
            ReadOnlyMemory<float> embedding, CancellationToken cancellationToken = default)
        { Upserts.Add((wikiName, relativePath)); return Task.CompletedTask; }
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(string wikiName, string queryText,
            ReadOnlyMemory<float> queryEmbedding, int topK, PageType? typeFilter = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<VectorSearchHit>>([]);
        public Task DeleteWikiAsync(string wikiName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ThrowingVectorStore : IVectorStore
    {
        public Task UpsertAsync(string wikiName, string relativePath, WikiPage page,
            ReadOnlyMemory<float> embedding, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("oracle down");
        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(string wikiName, string queryText,
            ReadOnlyMemory<float> queryEmbedding, int topK, PageType? typeFilter = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<VectorSearchHit>>([]);
        public Task DeleteWikiAsync(string wikiName, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("oracle down");
    }
}