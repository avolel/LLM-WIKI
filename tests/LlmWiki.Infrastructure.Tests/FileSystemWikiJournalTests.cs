using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using LlmWiki.Infrastructure.FileStore;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Infrastructure.Tests;

public sealed class FileSystemWikiJournalTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "llmwiki-journal-tests", Guid.NewGuid().ToString("N"));
    private readonly IWikiRepository _repo;
    private readonly IWikiJournal _journal;

    public FileSystemWikiJournalTests()
    {
        var files = new FileSystemWikiFileStore(Options.Create(new WikiOptions { RootPath = _root }));
        _repo = new FileSystemWikiRepository(files);
        _journal = new FileSystemWikiJournal(_repo, files);
    }

    [Fact]
    public async Task RebuildIndex_ListsPages_AndIsExcludedFromCatalogue()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        await _repo.WritePageAsync("alpha", "entities/acme-corp.md", new WikiPage
        { Title = "Acme Corp", Type = PageType.Entity, Content = "Acme makes anvils." });
        await _repo.WritePageAsync("alpha", "summaries/report.md", new WikiPage
        { Title = "Report", Type = PageType.Summary, Content = "A summary." });

        await _journal.RebuildIndexAsync("alpha");

        var index = await File.ReadAllTextAsync(Path.Combine(_root, "alpha", "index.md"));
        Assert.Contains("## Sources", index);
        Assert.Contains("## Entities", index);
        Assert.Contains("Acme Corp", index);

        // index.md itself is agent-owned, not counted as a content page.
        var pages = await _repo.ListPagesAsync("alpha");
        Assert.DoesNotContain("index.md", pages);
        Assert.Contains("entities/acme-corp.md", pages);
    }

    [Fact]
    public async Task RebuildIndex_AfterPageDeleted_DropsStaleEntry() // BR-024
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        await _repo.WritePageAsync("alpha", "entities/acme-corp.md", new WikiPage
        { Title = "Acme Corp", Type = PageType.Entity, Content = "Acme." });
        await _repo.WritePageAsync("alpha", "entities/gizmo.md", new WikiPage
        { Title = "Gizmo", Type = PageType.Entity, Content = "A gizmo." });
        await _journal.RebuildIndexAsync("alpha");

        File.Delete(Path.Combine(_root, "alpha", "entities", "gizmo.md"));
        await _journal.RebuildIndexAsync("alpha");

        var index = await File.ReadAllTextAsync(Path.Combine(_root, "alpha", "index.md"));
        Assert.Contains("Acme Corp", index);
        Assert.DoesNotContain("Gizmo", index);
    }

    [Fact]
    public async Task AppendLog_TwoEntries_AreChronologicalAndGreppable()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        await _journal.AppendLogAsync("alpha", new LogEntry(new DateOnly(2026, 7, 6), "ingest", "first.md"));
        await _journal.AppendLogAsync("alpha", new LogEntry(new DateOnly(2026, 7, 7), "ingest", "second.md"));

        var log = await File.ReadAllTextAsync(Path.Combine(_root, "alpha", "log.md"));
        var headers = log.Split('\n').Where(l => l.StartsWith("## [", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, headers.Count);
        Assert.Contains("first.md", headers[0]);   // chronological: first appended first
        Assert.Contains("second.md", headers[1]);

        // log.md is agent-owned, not a content page.
        Assert.DoesNotContain("log.md", await _repo.ListPagesAsync("alpha"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}