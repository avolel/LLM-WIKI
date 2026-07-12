namespace LlmWiki.Application.Ports;

/// <summary>
/// Port for the canonical file-backed store of wiki content (markdown on disk / object store).
/// Implemented in Infrastructure. Signatures only in Phase 0; bodies land in Phase 2.
/// </summary>
public interface IWikiFileStore
{
    Task<string> ReadAsync(string relativePath, CancellationToken cancellationToken = default);

    Task WriteAsync(string relativePath, string content, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> ListAsync(string prefix, CancellationToken cancellationToken = default);

    Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);
}
