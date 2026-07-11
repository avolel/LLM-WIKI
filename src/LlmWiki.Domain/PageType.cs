namespace LlmWiki.Domain;

/// <summary>Classifies a <see cref="WikiPage"/> per the frontmatter taxonomy.</summary>
public enum PageType
{
    Summary,
    Entity,
    Concept,
    Overview,

    /// <summary>A synthesised, cited answer saved back into the wiki.</summary>
    Answer
}
