using LlmWiki.Agents.Ingestion;
using LlmWiki.Application.Ingestion;
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using LlmWiki.Infrastructure.FileStore;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Agents.Tests;

public sealed class IngestionServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "llmwiki-ingest-tests", Guid.NewGuid().ToString("N"));
    private readonly IWikiRepository _repo;
    private readonly IWikiJournal _journal;
    private readonly FileSystemWikiFileStore _files;
    private readonly FakeEmbeddingService _embeddings = new();
    private readonly FakeVectorStore _vectors = new();
    private readonly FakeProjectRepository _projects = new();

    public IngestionServiceTests()
    {
        _files = new FileSystemWikiFileStore(Options.Create(new WikiOptions { RootPath = _root }));
        _repo = new FileSystemWikiRepository(_files);
        _journal = new FileSystemWikiJournal(_repo, _files);
    }

    private IngestionService BuildService(
        IChatService chat, IVectorStore? vectors = null, IProjectRepository? projects = null) =>
        new(chat, _repo, _journal, _embeddings, vectors ?? _vectors, _files, projects ?? _projects,
            Options.Create(new EmbeddingOptions()));

    [Fact]
    public async Task Ingest_WritesSummaryEntityAndTopicPages()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        var chat = new ScriptedChat(extraction: """
            {"sourceTitle":"Anvil Report","summary":"Acme builds anvils.","keyPoints":["Anvils are heavy"],
             "entities":[{"name":"Acme Corp","description":"An anvil maker.","thin":false}],
             "concepts":[],"tags":["anvils"],"topicTitle":"Anvils","topicSummary":"Overview of anvils."}
            """);
        var svc = BuildService(chat);

        var report = await svc.IngestAsync("alpha", "raw/anvils.md", "Acme builds heavy anvils.");

        Assert.False(report.HasFailures);
        Assert.Contains(report.Outcomes, o => o.RelativePath == "summaries/anvil-report.md");
        Assert.Contains(report.Outcomes, o => o.RelativePath == "entities/acme-corp.md" && o.Change == PageChange.Created);
        Assert.Contains(report.Outcomes, o => o.RelativePath == "topics/anvils.md");

        var entity = await _repo.ReadPageAsync("alpha", "entities/acme-corp.md");
        Assert.Equal(PageType.Entity, entity.Type);
        Assert.Contains("raw/anvils.md", entity.Sources);

        // Phase 3: the journal ran as the final step — index lists the summary page, log has an entry.
        var index = await File.ReadAllTextAsync(Path.Combine(_root, "alpha", "index.md"));
        Assert.Contains("Anvil Report", index);
        var log = await File.ReadAllTextAsync(Path.Combine(_root, "alpha", "log.md"));
        Assert.Contains("## [", log);
    }

    [Fact]
    public async Task Ingest_EmbedsEveryChangedPage_IntoTheVectorStore()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        var chat = new ScriptedChat(extraction: """
            {"sourceTitle":"Anvil Report","summary":"Acme builds anvils.","keyPoints":[],
             "entities":[{"name":"Acme Corp","description":"An anvil maker.","thin":false}],
             "concepts":[],"tags":["anvils"],"topicTitle":"Anvils","topicSummary":"Overview of anvils."}
            """);
        var svc = BuildService(chat);

        var report = await svc.IngestAsync("alpha", "raw/anvils.md", "Acme builds heavy anvils.");

        // BR-033: every created/updated page in the report was pushed to the vector store, exactly once.
        var changed = report.Outcomes
            .Where(o => o.Change is PageChange.Created or PageChange.Updated or PageChange.StubCreated)
            .Select(o => o.RelativePath)
            .ToList();
        Assert.NotEmpty(changed);
        foreach (var path in changed)
            Assert.Contains(_vectors.Upserts, u => u.Wiki == "alpha" && u.Path == path);
        Assert.Equal(changed.Count, _vectors.Upserts.Count);
    }

    [Fact]
    public async Task Ingest_ContradictionNotedPage_IsAlsoReEmbedded()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        await _repo.WritePageAsync("alpha", "entities/acme-corp.md", new WikiPage
        { Title = "Acme Corp", Type = PageType.Entity, Content = "Acme was founded in 1990." });

        var chat = new ScriptedChat(
            extraction: """
                {"sourceTitle":"S2","summary":"Acme was founded in 1888.","keyPoints":[],
                 "entities":[{"name":"Acme Corp","description":"Founded 1888.","thin":false}],
                 "concepts":[],"tags":[],"topicTitle":"","topicSummary":""}
                """,
            reconcile: """
                {"contradictions":[{"page":"entities/acme-corp.md","description":"Source says 1888; page says 1990."}]}
                """);
        var svc = BuildService(chat);

        await svc.IngestAsync("alpha", "raw/s2.md", "Acme was founded in 1888.");

        Assert.Contains(_vectors.Upserts, u => u.Path == "entities/acme-corp.md");
    }

    [Fact]
    public async Task Ingest_EmbeddingStoreFailure_IsRecordedNotThrown()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        var chat = new ScriptedChat(extraction: """
            {"sourceTitle":"S","summary":"x","keyPoints":[],
             "entities":[{"name":"Acme Corp","description":"maker","thin":false}],
             "concepts":[],"tags":[],"topicTitle":"","topicSummary":""}
            """);
        var svc = BuildService(chat, vectors: new ThrowingVectorStore());

        // NFR-06: an Oracle-down embed failure never throws — it lands as a Failed outcome.
        var report = await svc.IngestAsync("alpha", "raw/s.md", "...");

        Assert.True(report.HasFailures);
        Assert.Contains(report.Outcomes, o => o.Change == PageChange.Failed && o.Detail!.StartsWith("embed:"));
        // The file-side write still succeeded.
        var page = await _repo.ReadPageAsync("alpha", "entities/acme-corp.md");
        Assert.Equal("Acme Corp", page.Title);
    }

    [Fact]
    public async Task Ingest_RecordsProjectMetadata_WithPageAndSourceCounts()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        // A raw source on disk so the source count is observable (excludes the scaffolded .gitkeep).
        await _files.WriteAsync("alpha/raw/anvils.md", "Acme builds heavy anvils.");
        var chat = new ScriptedChat(extraction: """
            {"sourceTitle":"Anvil Report","summary":"Acme builds anvils.","keyPoints":[],
             "entities":[{"name":"Acme Corp","description":"An anvil maker.","thin":false}],
             "concepts":[],"tags":["anvils"],"topicTitle":"Anvils","topicSummary":"Overview of anvils."}
            """);
        var svc = BuildService(chat);

        await svc.IngestAsync("alpha", "raw/anvils.md", "Acme builds heavy anvils.");

        // BR-052: exactly one metadata record with the recomputed counts (1 source, some pages).
        var record = Assert.Single(_projects.Records);
        Assert.Equal("alpha", record.Name);
        Assert.True(record.PageCount > 0);
        Assert.Equal(1, record.SourceCount);
    }

    [Fact]
    public async Task Ingest_ProjectRecordFailure_IsRecordedNotThrown()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        var chat = new ScriptedChat(extraction: """
            {"sourceTitle":"S","summary":"x","keyPoints":[],
             "entities":[{"name":"Acme Corp","description":"maker","thin":false}],
             "concepts":[],"tags":[],"topicTitle":"","topicSummary":""}
            """);
        var svc = BuildService(chat, projects: new ThrowingProjectRepository());

        // NFR-06: an Oracle-down metadata write never throws — it lands as a Failed "project" outcome,
        // and the pages are still written.
        var report = await svc.IngestAsync("alpha", "raw/s.md", "...");

        Assert.Contains(report.Outcomes, o => o.Change == PageChange.Failed && o.Detail!.StartsWith("project:"));
        var page = await _repo.ReadPageAsync("alpha", "entities/acme-corp.md");
        Assert.Equal("Acme Corp", page.Title);
    }

    [Fact]
    public async Task Ingest_ThinItem_IsFlaggedAsGapAndStub()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        var chat = new ScriptedChat(extraction: """
            {"sourceTitle":"S","summary":"x","keyPoints":[],
             "entities":[{"name":"Wile E Coyote","description":"Mentioned once.","thin":true}],
             "concepts":[],"tags":[],"topicTitle":"","topicSummary":""}
            """);
        var svc = BuildService(chat);

        var report = await svc.IngestAsync("alpha", "raw/s.md", "...Wile E Coyote...");

        Assert.Contains(report.Gaps, g => g.Subject == "Wile E Coyote");
        Assert.Contains(report.Outcomes, o => o.RelativePath == "entities/wile-e-coyote.md" && o.Change == PageChange.StubCreated);
    }

    [Fact]
    public async Task Ingest_ContradictionWithExistingPage_IsNotedNotOverwritten()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        await _repo.WritePageAsync("alpha", "entities/acme-corp.md", new WikiPage
        { Title = "Acme Corp", Type = PageType.Entity, Content = "Acme was founded in 1990." });

        var chat = new ScriptedChat(
            extraction: """
                {"sourceTitle":"S2","summary":"Acme was founded in 1888.","keyPoints":[],
                 "entities":[{"name":"Acme Corp","description":"Founded 1888.","thin":false}],
                 "concepts":[],"tags":[],"topicTitle":"","topicSummary":""}
                """,
            reconcile: """
                {"contradictions":[{"page":"entities/acme-corp.md","description":"Source says 1888; page says 1990."}]}
                """);
        var svc = BuildService(chat);

        var report = await svc.IngestAsync("alpha", "raw/s2.md", "Acme was founded in 1888.");

        Assert.Contains(report.Contradictions, c => c.PageRelativePath == "entities/acme-corp.md");
        var page = await _repo.ReadPageAsync("alpha", "entities/acme-corp.md");
        Assert.Contains("1990", page.Content);              // original retained
        Assert.Contains("Contradiction noted", page.Content); // discrepancy appended
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Fake chat: returns the extraction script first, then the reconcile script.</summary>
    private sealed class ScriptedChat(string extraction, string? reconcile = null) : IChatService
    {
        private int _call;
        public Task<string> CompleteAsync(string prompt, bool jsonMode = false, CancellationToken cancellationToken = default)
            => Task.FromResult(_call++ == 0 ? extraction : reconcile ?? "{\"contradictions\":[]}");
    }

    /// <summary>Fake embedder: returns a fixed 768-length vector so the strategy path is exercised.</summary>
    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<ReadOnlyMemory<float>>(new float[768]);
    }

    /// <summary>Fake vector store: records upsert calls; search is unused here.</summary>
    private sealed class FakeVectorStore : IVectorStore
    {
        public List<(string Wiki, string Path)> Upserts { get; } = [];

        public Task UpsertAsync(string wikiName, string relativePath, WikiPage page,
            ReadOnlyMemory<float> embedding, CancellationToken cancellationToken = default)
        {
            Upserts.Add((wikiName, relativePath));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(string wikiName, string queryText,
            ReadOnlyMemory<float> queryEmbedding, int topK, PageType? typeFilter = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<VectorSearchHit>>([]);

        public Task DeleteWikiAsync(string wikiName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    /// <summary>Fake project registry: records the metadata write so counts can be asserted.</summary>
    private sealed class FakeProjectRepository : IProjectRepository
    {
        public List<ProjectInfo> Records { get; } = [];

        public Task RecordIngestAsync(string name, int pageCount, int sourceCount, CancellationToken cancellationToken = default)
        {
            Records.Add(new ProjectInfo(name, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, pageCount, sourceCount));
            return Task.CompletedTask;
        }

        public Task RegisterAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProjectInfo>>(Records);
        public Task<ProjectInfo?> GetAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult(Records.FirstOrDefault(r => r.Name == name));
        public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            Records.RemoveAll(r => r.Name == name);
            return Task.CompletedTask;
        }
    }

    /// <summary>Fake project registry that fails the metadata write (simulates Oracle being down).</summary>
    private sealed class ThrowingProjectRepository : IProjectRepository
    {
        public Task RecordIngestAsync(string name, int pageCount, int sourceCount, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("oracle down");
        public Task RegisterAsync(string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProjectInfo>>([]);
        public Task<ProjectInfo?> GetAsync(string name, CancellationToken cancellationToken = default)
            => Task.FromResult<ProjectInfo?>(null);
        public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("oracle down");
    }

    /// <summary>Fake vector store that fails every upsert (simulates Oracle being down).</summary>
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