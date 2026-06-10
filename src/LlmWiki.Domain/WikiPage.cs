namespace LlmWiki.Domain;

/// <summary>
/// Placeholder domain entity for Phase 0. A wiki page is the core unit the system
/// stores, embeds and serves. Fields here are intentionally minimal; the full model
/// (revisions, links, project association, embeddings metadata) lands in later phases.
/// </summary>
public sealed class WikiPage
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Title { get; init; }

    public string Content { get; init; } = string.Empty;

    public PageType Type { get; init; } = PageType.Article;

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
