using LlmWiki.Domain;

namespace LlmWiki.Domain.Tests;

public class SlugTests
{
    [Theory]
    [InlineData("Acme Corp", "acme-corp")]
    [InlineData("  Wile E. Coyote ", "wile-e-coyote")]
    [InlineData("C# & .NET", "c-net")]
    public void From_ProducesFilesystemSafeSlug(string input, string expected)
        => Assert.Equal(expected, Slug.From(input));
}
