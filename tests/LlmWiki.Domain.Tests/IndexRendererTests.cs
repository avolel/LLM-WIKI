using LlmWiki.Domain;

namespace LlmWiki.Domain.Tests;

public class IndexRendererTests
{
    private static readonly DateTimeOffset Created = new(2026, 7, 7, 0, 0, 0, TimeSpan.Zero);

    private static IndexEntry Entry(string path, string title, PageType type, int sources = 1) =>
        new(path, title, type, $"Summary of {title}.", Created, sources);

    [Fact]
    public void Render_GroupsIntoFixedOrderedSections_OmittingEmpty()
    {
        var md = IndexRenderer.Render(
        [
            Entry("topics/anvils.md", "Anvils", PageType.Overview),
            Entry("summaries/report.md", "Report", PageType.Summary),
            Entry("entities/acme.md", "Acme", PageType.Entity),
        ], LinkStyle.Wikilink);

        Assert.Contains("# Index", md);
        Assert.Contains("## Sources", md);
        Assert.Contains("## Entities", md);
        Assert.Contains("## Overviews", md);
        Assert.DoesNotContain("## Concepts", md); // no concept entries → section omitted

        // Fixed order: Sources < Entities < Overviews, regardless of input order.
        Assert.True(md.IndexOf("## Sources", StringComparison.Ordinal) < md.IndexOf("## Entities", StringComparison.Ordinal));
        Assert.True(md.IndexOf("## Entities", StringComparison.Ordinal) < md.IndexOf("## Overviews", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_PlacesAnswerPages_UnderAnswersHeading()
    {
        var md = IndexRenderer.Render(
        [
            Entry("answers/how-it-works.md", "How it works", PageType.Answer),
            Entry("topics/anvils.md", "Anvils", PageType.Overview),
        ], LinkStyle.Wikilink);

        Assert.Contains("## Answers", md);
        // Answers is the last fixed section (after Overviews).
        Assert.True(md.IndexOf("## Overviews", StringComparison.Ordinal) < md.IndexOf("## Answers", StringComparison.Ordinal));
        Assert.True(md.IndexOf("## Answers", StringComparison.Ordinal) < md.IndexOf("[[How it works]]", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_SortsEntriesWithinSectionByPath()
    {
        var md = IndexRenderer.Render(
        [
            Entry("entities/zeta.md", "Zeta", PageType.Entity),
            Entry("entities/alpha.md", "Alpha", PageType.Entity),
        ], LinkStyle.Wikilink);

        Assert.True(md.IndexOf("[[Alpha]]", StringComparison.Ordinal) < md.IndexOf("[[Zeta]]", StringComparison.Ordinal));
    }

    [Fact]
    public void Render_Wikilink_UsesTitle()
    {
        var md = IndexRenderer.Render([Entry("entities/acme.md", "Acme Corp", PageType.Entity)], LinkStyle.Wikilink);
        Assert.Contains("- [[Acme Corp]] — Summary of Acme Corp. (2026-07-07; 1 source)", md);
    }

    [Fact]
    public void Render_MarkdownLink_UsesPathRelativeToRoot()
    {
        var md = IndexRenderer.Render([Entry("entities/acme.md", "Acme Corp", PageType.Entity)], LinkStyle.MarkdownLink);
        Assert.Contains("- [Acme Corp](entities/acme.md) —", md);
    }

    [Theory]
    [InlineData(1, "1 source")]
    [InlineData(3, "3 sources")]
    public void Render_PluralisesSourceCount(int count, string expected)
    {
        var md = IndexRenderer.Render([Entry("entities/acme.md", "Acme", PageType.Entity, count)], LinkStyle.Wikilink);
        Assert.Contains($"; {expected})", md);
    }
}