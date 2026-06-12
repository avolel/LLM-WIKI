namespace LlmWiki.Domain;

/// <summary>Cross-reference style a wiki uses, recorded in its SCHEMA.md (BR-004).</summary>
public enum LinkStyle
{
    /// <summary>Obsidian-style [[Target]] links.</summary>
    Wikilink,

    /// <summary>Standard markdown [text](path.md) links.</summary>
    MarkdownLink
}