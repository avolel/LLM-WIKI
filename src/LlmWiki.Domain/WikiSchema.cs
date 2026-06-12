namespace LlmWiki.Domain;

/// <summary>
/// A wiki's conventions, written to and read back from SCHEMA.md (BR-002, BR-005).
/// "Convention + toggles": typed directories are fixed, while link style and the frontmatter
/// field set are configurable per wiki so wikis with different conventions coexist.
/// </summary>
public sealed class WikiSchema
{
    public required string WikiName { get; init; }

    public LinkStyle LinkStyle { get; init; } = LinkStyle.Wikilink;

    public IReadOnlyList<string> FrontmatterFields { get; init; } = DefaultFrontmatterFields;

    /// <summary>Typed content directories created for every wiki (BR-001).</summary>
    public static readonly IReadOnlyList<string> Directories = ["summaries", "entities", "topics", "raw"];

    /// <summary>Default frontmatter keys per BR-003.</summary>
    public static readonly IReadOnlyList<string> DefaultFrontmatterFields =
        ["title", "type", "created", "updated", "tags", "sources"];
}