using LlmWiki.Domain;

namespace LlmWiki.Domain.Tests;

public class CrossReferenceParserTests
{
    [Fact]
    public void Parse_Wikilinks_ExtractsTargetsAndStripsAliases()
    {
        var refs = CrossReferenceParser.Parse(
            "See [[Acme Corp]] and [[Wile E Coyote|the coyote]].", LinkStyle.Wikilink);

        Assert.Equal(["Acme Corp", "Wile E Coyote"], refs.Select(r => r.Target));
    }

    [Fact]
    public void Parse_MarkdownLinks_ExtractsHrefs()
    {
        var refs = CrossReferenceParser.Parse(
            "See [Acme](entities/acme-corp.md).", LinkStyle.MarkdownLink);

        Assert.Equal(["entities/acme-corp.md"], refs.Select(r => r.Target));
    }
}