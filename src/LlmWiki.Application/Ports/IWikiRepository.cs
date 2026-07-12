using LlmWiki.Domain;

namespace LlmWiki.Application.Ports;

/// <summary>Summary of a wiki on disk, for list/inspect.</summary>
public sealed record WikiInfo(string Name, LinkStyle LinkStyle, int PageCount);

/// <summary>
/// High-level, wiki-aware operations over the file store: scaffold wikis (BR-001/2),
/// read/write pages with frontmatter (BR-003), list wikis/pages, and resolve cross-references
/// (BR-004). Backed in Infrastructure by <see cref="IWikiFileStore"/> + YAML serialization.
/// </summary>
public interface IWikiRepository
{
    Task CreateWikiAsync(WikiSchema schema, CancellationToken cancellationToken = default);

    Task<bool> WikiExistsAsync(string wikiName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WikiInfo>> ListWikisAsync(CancellationToken cancellationToken = default);

    Task<WikiSchema> ReadSchemaAsync(string wikiName, CancellationToken cancellationToken = default);

    Task WritePageAsync(string wikiName, string relativePath, WikiPage page, CancellationToken cancellationToken = default);

    Task<WikiPage> ReadPageAsync(string wikiName, string relativePath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListPagesAsync(string wikiName, CancellationToken cancellationToken = default);

    Task<LinkResolutionReport> ResolveLinksAsync(string wikiName, string relativePath, CancellationToken cancellationToken = default);

    Task DeleteWikiAsync(string wikiName, CancellationToken cancellationToken = default);
}