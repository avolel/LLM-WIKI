using LlmWiki.Application.Ports;

namespace LlmWiki.Infrastructure.FileStore;

/// <summary>
/// Stub adapter for <see cref="IWikiFileStore"/>. Real local/object-store implementation
/// lands in Phase 2. Throws so accidental use in Phase 0 fails loudly.
/// </summary>
public sealed class FileSystemWikiFileStore : IWikiFileStore
{
    private const string NotYet = "IWikiFileStore is not implemented until Phase 2.";

    public Task<string> ReadAsync(string relativePath, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotYet);

    public Task WriteAsync(string relativePath, string content, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotYet);

    public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotYet);

    public IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotYet);
}
