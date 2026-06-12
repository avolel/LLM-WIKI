using LlmWiki.Domain;

namespace LlmWiki.Domain.Tests;

public class WikiPageTests
{
    [Fact]
    public void NewPage_GetsIdAndDefaultsToSummary()
    {
        var page = new WikiPage { Title = "Hello" };

        Assert.NotEqual(Guid.Empty, page.Id);
        Assert.Equal("Hello", page.Title);
        Assert.Equal(PageType.Summary, page.Type);
        Assert.Empty(page.Tags);
        Assert.Empty(page.Sources);
    }
}