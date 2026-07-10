namespace LlmWiki.Domain;

/// <summary>One page as it appears in the index catalogue (BR-020).</summary>
public sealed record IndexEntry(
    string RelativePath,
    string Title,
    PageType Type,
    string Summary,
    DateTimeOffset Created,
    int SourceCount
);
