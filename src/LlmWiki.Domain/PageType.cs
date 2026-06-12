namespace LlmWiki.Domain;

/// <summary>Classifies a <see cref="WikiPage"/> per the BR-003 frontmatter taxonomy.</summary>
public enum PageType
{
    Summary,
    Entity,
    Concept,
    Overview
}
