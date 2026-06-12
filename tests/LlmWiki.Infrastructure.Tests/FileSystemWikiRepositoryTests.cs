using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using LlmWiki.Infrastructure.FileStore;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Infrastructure.Tests;

public sealed class FileSystemWikiRepositoryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "llmwiki-tests", Guid.NewGuid().ToString("N"));
    private readonly IWikiRepository _repo;

    public FileSystemWikiRepositoryTests()
    {
        var files = new FileSystemWikiFileStore(Options.Create(new WikiOptions { RootPath = _root }));
        _repo = new FileSystemWikiRepository(files);
    }

    [Fact]
    public async Task CreateWiki_WritesTypedDirsAndSchema()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });

        foreach (var dir in WikiSchema.Directories)
        {
            Assert.True(Directory.Exists(Path.Combine(_root, "alpha", dir)));
        }
        Assert.True(File.Exists(Path.Combine(_root, "alpha", "SCHEMA.md")));
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsFrontmatter()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        await _repo.WritePageAsync("alpha", "entities/acme-corp.md", new WikiPage
        {
            Title = "Acme Corp",
            Type = PageType.Entity,
            Tags = ["company", "tech"],
            Sources = ["raw/acme.md"],
            Content = "Acme makes anvils. See [[Wile E Coyote]].",
        });

        var read = await _repo.ReadPageAsync("alpha", "entities/acme-corp.md");

        Assert.Equal("Acme Corp", read.Title);
        Assert.Equal(PageType.Entity, read.Type);
        Assert.Equal(["company", "tech"], read.Tags);
        Assert.Equal(["raw/acme.md"], read.Sources);
        Assert.Contains("anvils", read.Content);
    }

    [Fact]
    public async Task ResolveLinks_FlagsResolvedAndBroken()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha", LinkStyle = LinkStyle.Wikilink });
        await _repo.WritePageAsync("alpha", "entities/wile-e-coyote.md",
            new WikiPage { Title = "Wile E Coyote", Type = PageType.Entity });
        await _repo.WritePageAsync("alpha", "entities/acme-corp.md", new WikiPage
        {
            Title = "Acme Corp",
            Type = PageType.Entity,
            Content = "Customer: [[Wile E Coyote]]. Rival: [[Road Runner]].",
        });

        var report = await _repo.ResolveLinksAsync("alpha", "entities/acme-corp.md");

        Assert.Equal(2, report.Links.Count);
        Assert.Single(report.Broken);
        Assert.Equal("Road Runner", report.Broken[0].Reference.Target);
    }

    [Fact]
    public async Task TwoWikis_DifferentSchemas_CoexistAndAreListed()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha", LinkStyle = LinkStyle.Wikilink });
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "beta", LinkStyle = LinkStyle.MarkdownLink });

        var wikis = await _repo.ListWikisAsync();

        Assert.Equal(2, wikis.Count);
        Assert.Equal(LinkStyle.Wikilink, wikis.Single(w => w.Name == "alpha").LinkStyle);
        Assert.Equal(LinkStyle.MarkdownLink, wikis.Single(w => w.Name == "beta").LinkStyle);
    }

    [Fact]
    public async Task DeletingWikiDirectory_RemovesItFromListing()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "beta" });

        Directory.Delete(Path.Combine(_root, "beta"), recursive: true);

        var wikis = await _repo.ListWikisAsync();

        Assert.Equal(["alpha"], wikis.Select(w => w.Name));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}