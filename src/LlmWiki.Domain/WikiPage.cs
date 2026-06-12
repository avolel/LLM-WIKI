namespace LlmWiki.Domain;

/// <summary>
/// A markdown wiki page: YAML frontmatter (BR-003) plus a markdown body. Identified on disk
/// by its relative path within a wiki; persisted to Oracle in later phases.
/// </summary>
public sealed class WikiPage
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Title { get; init; }

    public PageType Type { get; init; } = PageType.Summary;

    public string Content { get; init; } = string.Empty;

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<string> Sources { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
