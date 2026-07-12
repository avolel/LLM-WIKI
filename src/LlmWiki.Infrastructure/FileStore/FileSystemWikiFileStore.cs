using System.Runtime.CompilerServices;
using LlmWiki.Application.Ports;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Infrastructure.FileStore;

/// <summary>
/// Local-disk implementation of <see cref="IWikiFileStore"/> (BR-001). All paths are relative
/// to the configured wiki root; writes create parent directories so the typed wiki layout
/// materialises on first write. Paths that escape the root are rejected.
/// </summary>
public sealed class FileSystemWikiFileStore : IWikiFileStore
{
    private readonly string _root;

    public FileSystemWikiFileStore(IOptions<WikiOptions> options)
    {
        _root = Path.GetFullPath(options.Value.RootPath);
    }

    public async Task<string> ReadAsync(string relativePath, CancellationToken cancellationToken = default) =>
        await File.ReadAllTextAsync(Resolve(relativePath), cancellationToken);

    public async Task WriteAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        var full = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, cancellationToken);
    }

    public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(Resolve(relativePath)));

    public async IAsyncEnumerable<string> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var dir = Resolve(prefix);
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            await Task.Yield();
        }
    }

    private string Resolve(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (full != _root &&
            !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Path '{relativePath}' escapes the wiki root.");
        }
        return full;
    }

    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
    {
        var full = Resolve(relativePath);
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        else if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }
}
