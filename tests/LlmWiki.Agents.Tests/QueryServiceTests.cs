using LlmWiki.Agents.Query;
using LlmWiki.Application.Ingestion;
using LlmWiki.Application.Ports;
using LlmWiki.Application.Query;
using LlmWiki.Domain;
using LlmWiki.Infrastructure.FileStore;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Agents.Tests;

public sealed class QueryServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "llmwiki-query-tests", Guid.NewGuid().ToString("N"));
    private readonly IWikiFileStore _files;
    private readonly IWikiRepository _repo;
    private readonly IWikiJournal _journal;
    private readonly FakeEmbeddingService _embeddings = new();

    public QueryServiceTests()
    {
        _files = new FileSystemWikiFileStore(Options.Create(new WikiOptions { RootPath = _root }));
        _repo = new FileSystemWikiRepository(_files);
        _journal = new FileSystemWikiJournal(_repo, _files);
    }

    private QueryService BuildService(IChatService chat, IVectorStore vectors) =>
        new(chat, _repo, _files, vectors, _embeddings, _journal, Options.Create(new EmbeddingOptions()));

    /// <summary>Seed a wiki with two real pages and return the hits a search would surface for them.</summary>
    private async Task<IReadOnlyList<VectorSearchHit>> SeedAsync()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        await _repo.WritePageAsync("alpha", "entities/acme.md", new WikiPage
        { Title = "Acme", Type = PageType.Entity, Content = "Acme makes anvils." });
        await _repo.WritePageAsync("alpha", "concepts/anvil.md", new WikiPage
        { Title = "Anvil", Type = PageType.Concept, Content = "A heavy iron block." });
        return
        [
            new VectorSearchHit("alpha", "entities/acme.md", "Acme", PageType.Entity, 0.9),
            new VectorSearchHit("alpha", "concepts/anvil.md", "Anvil", PageType.Concept, 0.8),
        ];
    }

    [Fact]
    public async Task Answer_CitationsResolveToSeededPages()
    {
        var hits = await SeedAsync();
        var chat = new ScriptedChat("""
            {"title":"How Acme works","answer":"Acme makes anvils [entities/acme.md].",
             "covered":true,"citations":["entities/acme.md","made/up.md"]}
            """);
        var svc = BuildService(chat, new RecordingVectorStore(hits));

        var result = await svc.AnswerAsync("alpha", "how does acme work?", [], new QueryOptions());

        //only citations that were actually retrieved survive; the fabricated path is dropped.
        Assert.Contains(result.Citations, c => c.RelativePath == "entities/acme.md" && c.Type == PageType.Entity);
        Assert.DoesNotContain(result.Citations, c => c.RelativePath == "made/up.md");
        Assert.True(result.Covered);
    }

    [Fact]
    public async Task Answer_UncoveredScript_ReportsGap()
    {
        var hits = await SeedAsync();
        var chat = new ScriptedChat("""
            {"title":"Unknown","answer":"The wiki does not cover this.","covered":false,"citations":[]}
            """);
        var svc = BuildService(chat, new RecordingVectorStore(hits));

        var result = await svc.AnswerAsync("alpha", "what is the meaning of life?", [], new QueryOptions());

        // honest gap flows through as Covered == false.
        Assert.False(result.Covered);
    }

    [Fact]
    public async Task Save_WritesAnswerPage_LogsAndEmbeds()
    {
        var hits = await SeedAsync();
        var chat = new ScriptedChat("""
            {"title":"How Acme works","answer":"Acme makes anvils.","covered":true,
             "citations":["entities/acme.md"]}
            """);
        var vectors = new RecordingVectorStore(hits);
        var svc = BuildService(chat, vectors);

        var result = await svc.AnswerAsync("alpha", "how does acme work?", [], new QueryOptions());
        var outcome = await svc.SaveAnswerAsync("alpha", result);

        // the answer is written as an Answer page under answers/.
        Assert.Equal(PageChange.Created, outcome.Change);
        Assert.Equal("answers/how-acme-works.md", outcome.RelativePath);
        var saved = await _repo.ReadPageAsync("alpha", "answers/how-acme-works.md");
        Assert.Equal(PageType.Answer, saved.Type);
        Assert.Contains("entities/acme.md", saved.Sources);

        // …a greppable query line lands in the log…
        var log = await File.ReadAllTextAsync(Path.Combine(_root, "alpha", "log.md"));
        Assert.Contains("] query |", log);

        // …index.md now catalogues it under the Answers heading…
        var index = await File.ReadAllTextAsync(Path.Combine(_root, "alpha", "index.md"));
        Assert.Contains("## Answers", index);

        // …and it was embedded into the vector store.
        Assert.Contains(vectors.Upserts, u => u.Wiki == "alpha" && u.Path == "answers/how-acme-works.md");
    }

    [Fact]
    public async Task Save_EmbeddingFailure_IsRecordedNotThrown()
    {
        var hits = await SeedAsync();
        var chat = new ScriptedChat("""
            {"title":"How Acme works","answer":"Acme makes anvils.","covered":true,
             "citations":["entities/acme.md"]}
            """);
        var svc = BuildService(chat, new ThrowingVectorStore(hits));

        var result = await svc.AnswerAsync("alpha", "how does acme work?", [], new QueryOptions());
        var outcome = await svc.SaveAnswerAsync("alpha", result);

        // the page still saved (write boundary succeeded); the embed failure is recorded.
        Assert.Equal(PageChange.Created, outcome.Change);
        Assert.Contains("embed:", outcome.Detail);
        var saved = await _repo.ReadPageAsync("alpha", "answers/how-acme-works.md");
        Assert.Equal("How Acme works", saved.Title);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Fake chat: returns a fixed canned synthesis reply.</summary>
    private sealed class ScriptedChat(string reply) : IChatService
    {
        public Task<string> CompleteAsync(string prompt, bool jsonMode = false, CancellationToken cancellationToken = default)
            => Task.FromResult(reply);
    }

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default)
            => Task.FromResult<ReadOnlyMemory<float>>(new float[768]);
    }

    /// <summary>Fake vector store: returns preset hits and records upserts.</summary>
    private sealed class RecordingVectorStore(IReadOnlyList<VectorSearchHit> hits) : IVectorStore
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
            => Task.FromResult(hits);
    }

    /// <summary>Returns hits for search, but fails every upsert (simulates Oracle down on save).</summary>
    private sealed class ThrowingVectorStore(IReadOnlyList<VectorSearchHit> hits) : IVectorStore
    {
        public Task UpsertAsync(string wikiName, string relativePath, WikiPage page,
            ReadOnlyMemory<float> embedding, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("oracle down");

        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(string wikiName, string queryText,
            ReadOnlyMemory<float> queryEmbedding, int topK, PageType? typeFilter = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(hits);
    }
}
