namespace LlmWiki.Domain;

/// <summary>
/// Write-side companion to <see cref="CrossReferenceParser"/>: renders a cross-reference to a target
/// page in the wiki's configured <see cref="LinkStyle"/> (BR-004). Wikilinks use the title; markdown
/// links use a path relative to the linking page's directory.
/// </summary>
public static class CrossReferenceWriter
{
    /// <param name="targetTitle">Display title of the target page.</param>
    /// <param name="targetRelativePath">Target page path within the wiki, e.g. "entities/acme-corp.md".</param>
    /// <param name="fromRelativePath">Path of the page that holds the link, e.g. "topics/anvils.md".</param>
    public static string Link(
        string targetTitle, 
        string targetRelativePath, 
        string fromRelativePath, 
        LinkStyle style)
    {
        if (style == LinkStyle.Wikilink)
        {
            return $"[[{targetTitle}]]";
        }

        var fromDir = Path.GetDirectoryName(fromRelativePath)?.Replace('\\', '/') ?? string.Empty;
        var rel = Path.GetRelativePath(fromDir.Length == 0 ? "." : fromDir, targetRelativePath).Replace('\\', '/');
        return $"[{targetTitle}]({rel})";
    }
}
