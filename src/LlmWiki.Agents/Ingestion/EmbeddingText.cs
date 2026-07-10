using LlmWiki.Domain;
using LlmWiki.Shared.Configuration;

namespace LlmWiki.Agents.Ingestion;

/// <summary>
/// Selects which text of a page is fed to the embedding model (BR-034). Pure — no I/O — so the
/// choice is easy to test and swap. Lives in Agents (its only caller, IngestionService) rather
/// than Application so the strategy enum can stay next to <see cref="EmbeddingOptions"/> in Shared
/// without Application taking a dependency on Shared.
/// </summary>
public static class EmbeddingText
{
    public static string For(WikiPage page, EmbeddingStrategy strategy) => strategy switch
    {
        EmbeddingStrategy.FullText => page.Content,
        EmbeddingStrategy.Summary => FirstParagraph(page.Content),
        _ => $"{page.Title}\n\n{page.Content}",   // TitleAndBody (default)
    };

    private static string FirstParagraph(string content)
    {
        var trimmed = content.TrimStart();
        var breakIdx = trimmed.IndexOf("\n\n", StringComparison.Ordinal);
        return breakIdx < 0 ? trimmed : trimmed[..breakIdx].TrimEnd();
    }
}