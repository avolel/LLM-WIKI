using LlmWiki.Application.Ports;
using LlmWiki.Infrastructure.FileStore;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Infrastructure.Tests;

public sealed class FileSystemWikiFileStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "llmwiki-tests", Guid.NewGuid().ToString("N"));
    private readonly IWikiFileStore _store;

    public FileSystemWikiFileStoreTests()
    {
        _store = new FileSystemWikiFileStore(Options.Create(new WikiOptions { RootPath = _root }));
    }

    [Fact]
    public async Task DeleteAsync_RemovesDirectoryTree()
    {
        await _store.WriteAsync("alpha/entities/a.md", "x");
        await _store.WriteAsync("alpha/entities/b.md", "y");

        await _store.DeleteAsync("alpha");

        Assert.False(Directory.Exists(Path.Combine(_root, "alpha")));
    }

    [Fact]
    public async Task DeleteAsync_MissingPath_IsNoOp()
    {
        // Must complete without throwing when the target does not exist.
        await _store.DeleteAsync("does-not-exist");
    }

    [Fact]
    public async Task DeleteAsync_TraversalOutsideRoot_Throws()
    {
        // The escape-guard runs before any disk access (Resolve).
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.DeleteAsync("../escape"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
