using LlmWiki.Application.Ports;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Infrastructure.FileStore;

/// <summary>
/// Stores the active-project pointer as a single line in <c>{WIKI_ROOT}/.current-project</c>.
/// Host-local state (BR-050); no Oracle dependency, so `project select` works offline.
/// </summary>
public sealed class FileCurrentProjectStore(IOptions<WikiOptions> options) : ICurrentProjectStore
{
    private readonly string _path =
        Path.Combine(Path.GetFullPath(options.Value.RootPath), ".current-project");

    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return null;
        var name = (await File.ReadAllTextAsync(_path, ct)).Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    public async Task SetAsync(string name, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, name.Trim(), ct);
    }
}
