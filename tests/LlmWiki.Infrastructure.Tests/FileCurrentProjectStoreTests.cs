using LlmWiki.Infrastructure.FileStore;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Infrastructure.Tests;

/// <summary>
/// The host-local active-project pointer (BR-050). Uses a real temp WIKI_ROOT; no Oracle.
/// </summary>
public sealed class FileCurrentProjectStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "llmwiki-current-tests", Guid.NewGuid().ToString("N"));

    private FileCurrentProjectStore NewStore() =>
        new(Options.Create(new WikiOptions { RootPath = _root }));

    [Fact]
    public async Task Get_BeforeSet_IsNull()
    {
        Assert.Null(await NewStore().GetAsync());
    }

    [Fact]
    public async Task Set_ThenGet_RoundTrips()
    {
        var store = NewStore();
        await store.SetAsync("ml-papers");
        Assert.Equal("ml-papers", await store.GetAsync());
    }

    [Fact]
    public async Task BlankFile_ReadsBackNull()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, ".current-project"), "   \n");
        Assert.Null(await NewStore().GetAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
