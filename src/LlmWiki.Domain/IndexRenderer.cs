using System.Text;

namespace LlmWiki.Domain;

/// <summary>
/// Pure renderer for a wiki's <c>index.md</c> content catalogue. Groups pages into fixed-order
/// sections by <see cref="PageType"/>, omitting empty ones, with entries stably sorted by path so the
/// file is deterministic and git diffs stay clean (stale-entry removal falls out for free).
/// Links honour the wiki's <see cref="LinkStyle"/>, resolved relative to the root <c>index.md</c>.
/// </summary>
public static class IndexRenderer
{
    private const string IndexFile = "index.md";

    /// <summary>Fixed section order: page type → heading.</summary>
    private static readonly (PageType Type, string Heading)[] Sections =
    [
        (PageType.Summary, "Sources"),
        (PageType.Entity, "Entities"),
        (PageType.Concept, "Concepts"),
        (PageType.Overview, "Overviews"),
        (PageType.Answer, "Answers"),
    ];

    public static string Render(IReadOnlyList<IndexEntry> entries, LinkStyle style)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Index");

        foreach (var (type, heading) in Sections)
        {
            var group = entries
                .Where(e => e.Type == type)
                .OrderBy(e => e.RelativePath, StringComparer.Ordinal)
                .ToList();
            if (group.Count == 0) continue;

            sb.AppendLine();
            sb.AppendLine($"## {heading}");
            sb.AppendLine();
            foreach (var e in group)
            {
                sb.AppendLine(Line(e, style));
            }
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    private static string Line(IndexEntry e, LinkStyle style)
    {
        var link = CrossReferenceWriter.Link(e.Title, e.RelativePath, IndexFile, style);
        var plural = e.SourceCount == 1 ? "source" : "sources";
        return $"- {link} — {e.Summary} ({e.Created:yyyy-MM-dd}; {e.SourceCount} {plural})";
    }
}