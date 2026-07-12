using LlmWiki.Agents.Linting;
using LlmWiki.Application.Ingestion;
using LlmWiki.Application.Linting;
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using LlmWiki.Infrastructure.FileStore;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Agents.Tests;

public sealed class LintServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "llmwiki-lint-tests", Guid.NewGuid().ToString("N"));
    private readonly IWikiFileStore _files;
    private readonly IWikiRepository _repo;
    private readonly IWikiJournal _journal;
    private readonly FakeEmbeddingService _embeddings = new();

    public LintServiceTests()
    {
        _files = new FileSystemWikiFileStore(Options.Create(new WikiOptions { RootPath = _root }));
        _repo = new FileSystemWikiRepository(_files);
        _journal = new FileSystemWikiJournal(_repo, _files);
    }

    private LintService BuildService(IChatService chat, IVectorStore vectors) =>
        new(chat, _repo, _files, _journal, vectors, _embeddings, Options.Create(new EmbeddingOptions()));

    /// <summary>A chat that returns no semantic findings — isolates the structural pass.</summary>
    private const string EmptyAnalysis =
        """{"contradictions":[],"staleClaims":[],"questions":[],"sources":[]}""";

    private async Task CreateWikiAsync() =>
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha", LinkStyle = LinkStyle.MarkdownLink });

    [Fact]
    public async Task Lint_BrokenLinkToTypedPath_ProducesMissingPageFindingWithFix()
    {
        await CreateWikiAsync();
        // A page that links (markdown-style) to a nonexistent typed page.
        await _repo.WritePageAsync("alpha", "entities/acme.md", new WikiPage
        {
            Title = "Acme",
            Type = PageType.Entity,
            Content = "Acme forges the [Anvil](../concepts/anvil.md) and more, a substantial description here.",
        });
        var svc = BuildService(new ScriptedChat(EmptyAnalysis), new RecordingVectorStore());

        var report = await svc.LintAsync("alpha");

        var missing = Assert.Single(report.Findings, f => f.Category == LintCategory.MissingPage);
        Assert.NotNull(missing.Fix);
        Assert.Equal("concepts/anvil.md", missing.Fix!.RelativePath);
        Assert.Equal(PageType.Concept, missing.Fix.Type);
        Assert.Equal(LintSeverity.Warning, missing.Severity);
    }

    [Fact]
    public async Task Lint_OrphanPage_IsFlagged_LinkedPageIsNot()
    {
        await CreateWikiAsync();
        // acme links to anvil; anvil is therefore not an orphan, acme is (nothing links to it).
        await _repo.WritePageAsync("alpha", "concepts/anvil.md", new WikiPage
        { Title = "Anvil", Type = PageType.Concept, Content = "A heavy iron block used by smiths, described at length here." });
        await _repo.WritePageAsync("alpha", "entities/acme.md", new WikiPage
        { Title = "Acme", Type = PageType.Entity, Content = "Acme forges the [Anvil](../concepts/anvil.md) daily, a full description here." });
        var svc = BuildService(new ScriptedChat(EmptyAnalysis), new RecordingVectorStore());

        var report = await svc.LintAsync("alpha");

        Assert.Contains(report.Findings, f => f.Category == LintCategory.Orphan && f.Pages.Contains("entities/acme.md"));
        Assert.DoesNotContain(report.Findings, f => f.Category == LintCategory.Orphan && f.Pages.Contains("concepts/anvil.md"));
    }

    [Fact]
    public async Task Lint_ThinPage_ProducesSuggestion()
    {
        await CreateWikiAsync();
        await _repo.WritePageAsync("alpha", "entities/tiny.md", new WikiPage
        { Title = "Tiny", Type = PageType.Entity, Content = "tiny" });
        var svc = BuildService(new ScriptedChat(EmptyAnalysis), new RecordingVectorStore());

        var report = await svc.LintAsync("alpha");

        var thin = Assert.Single(report.Findings, f => f.Category == LintCategory.ThinPage);
        Assert.Equal(LintSeverity.Suggestion, thin.Severity);
    }

    [Fact]
    public async Task Lint_Contradiction_FromLlm_IsCriticalAndSortedFirst()
    {
        await CreateWikiAsync();
        await _repo.WritePageAsync("alpha", "entities/a.md", new WikiPage
        { Title = "A", Type = PageType.Entity, Content = "The tower is 100 metres tall, per the survey documented here." });
        await _repo.WritePageAsync("alpha", "entities/b.md", new WikiPage
        { Title = "B", Type = PageType.Entity, Content = "The tower is 200 metres tall, per the newer survey documented here." });
        var chat = new ScriptedChat("""
            {"contradictions":[{"pages":["entities/a.md","entities/b.md"],"description":"Tower height conflicts."}],
             "staleClaims":[],"questions":[],"sources":[]}
            """);
        var svc = BuildService(chat, new RecordingVectorStore());

        var report = await svc.LintAsync("alpha");

        var contradiction = Assert.Single(report.Findings, f => f.Category == LintCategory.Contradiction);
        Assert.Equal(LintSeverity.Critical, contradiction.Severity);
        Assert.Contains("entities/a.md", contradiction.Pages);
        Assert.Contains("entities/b.md", contradiction.Pages);
        // Critical findings sort ahead of warnings/suggestions (BR-061).
        Assert.Equal(LintSeverity.Critical, report.Findings[0].Severity);
    }

    [Fact]
    public async Task Apply_MissingPageFix_CreatesStubAndEmbeds()
    {
        await CreateWikiAsync();
        await _repo.WritePageAsync("alpha", "entities/acme.md", new WikiPage
        { Title = "Acme", Type = PageType.Entity, Content = "Acme forges the [Anvil](../concepts/anvil.md), a full description here." });
        var vectors = new RecordingVectorStore();
        var svc = BuildService(new ScriptedChat(EmptyAnalysis), vectors);

        var report = await svc.LintAsync("alpha");
        var missing = Assert.Single(report.Findings, f => f.Category == LintCategory.MissingPage);

        var outcome = await svc.ApplyFixAsync("alpha", missing);

        Assert.Equal(PageChange.StubCreated, outcome.Change);
        Assert.True(File.Exists(Path.Combine(_root, "alpha", "concepts", "anvil.md")));
        Assert.Contains(vectors.Upserts, u => u.Wiki == "alpha" && u.Path == "concepts/anvil.md");
    }

    [Fact]
    public async Task Lint_ChatThrows_StructuralFindingsStillReturned()
    {
        await CreateWikiAsync();
        await _repo.WritePageAsync("alpha", "entities/tiny.md", new WikiPage
        { Title = "Tiny", Type = PageType.Entity, Content = "tiny" });
        var svc = BuildService(new ThrowingChat(), new RecordingVectorStore());

        var report = await svc.LintAsync("alpha");   // must not throw

        Assert.Contains(report.Findings, f => f.Category == LintCategory.ThinPage);
    }

    [Fact]
    public async Task Apply_EmbedFailure_StubStillWritten_NoteRecorded()
    {
        await CreateWikiAsync();
        await _repo.WritePageAsync("alpha", "entities/acme.md", new WikiPage
        { Title = "Acme", Type = PageType.Entity, Content = "Acme forges the [Anvil](../concepts/anvil.md), a full description here." });
        var svc = BuildService(new ScriptedChat(EmptyAnalysis), new ThrowingVectorStore());

        var report = await svc.LintAsync("alpha");
        var missing = Assert.Single(report.Findings, f => f.Category == LintCategory.MissingPage);

        var outcome = await svc.ApplyFixAsync("alpha", missing);

        Assert.Equal(PageChange.StubCreated, outcome.Change);
        Assert.Contains("embed:", outcome.Detail);
        Assert.True(File.Exists(Path.Combine(_root, "alpha", "concepts", "anvil.md")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class ScriptedChat(string reply) : IChatService
    {
        public Task<string> CompleteAsync(string prompt, bool jsonMode = false, CancellationToken cancellationToken = default)
            => Task.FromResult(reply);
    }

    private sealed class ThrowingChat : IChatService
    {
        public Task<string> CompleteAsync(string prompt, bool jsonMode = false, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("chat down");
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
        {
            Upserts.Add((wikiName, relativePath));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(string wikiName, string queryText,
            ReadOnlyMemory<float> queryEmbedding, int topK, PageType? typeFilter = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<VectorSearchHit>>([]);
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
    }
}
