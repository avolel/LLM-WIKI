namespace LlmWiki.Domain;

/// <summary>Classifies a <see cref="WikiPage"/>. Expanded in later phases.</summary>
public enum PageType
{
    Article,
    Reference,
    Note,
    Generated
}
