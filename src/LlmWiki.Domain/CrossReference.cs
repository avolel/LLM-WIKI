using System.Text.RegularExpressions;

namespace LlmWiki.Domain;

/// <summary>A cross-reference found in a page body.</summary>
public sealed record CrossReference(string Target, string Raw);

/// <summary>Outcome of resolving one cross-reference against the wiki's pages.</summary>
public sealed record ResolvedLink(CrossReference Reference, bool Exists, string? ResolvedPath);

/// <summary>Aggregate link-resolution result for a page (BR-004).</summary>
public sealed record LinkResolutionReport(string PagePath, IReadOnlyList<ResolvedLink> Links)
{
    public IReadOnlyList<ResolvedLink> Broken => Links.Where(l => !l.Exists).ToList();
    public bool AllResolved => Links.All(l => l.Exists);
}

/// <summary>Extracts cross-references from markdown according to a wiki's link style.</summary>
public static partial class CrossReferenceParser
{
    // [[Target]] or [[Target|alias]]
    [GeneratedRegex(@"\[\[([^\]|]+)(?:\|[^\]]+)?\]\]")]
    private static partial Regex WikilinkRegex();

    // [text](relative/path.md)
    [GeneratedRegex(@"\[[^\]]+\]\(([^)]+)\)")]
    private static partial Regex MarkdownLinkRegex();

    public static IReadOnlyList<CrossReference> Parse(string body, LinkStyle style)
    {
        var regex = style == LinkStyle.Wikilink ? WikilinkRegex() : MarkdownLinkRegex();
        var results = new List<CrossReference>();
        foreach (Match m in regex.Matches(body))
        {
            results.Add(new CrossReference(m.Groups[1].Value.Trim(), m.Value));
        }
        return results;
    }
}