using LlmWiki.Application.Ingestion;

namespace LlmWiki.Application.Query;

/// <summary>
/// Query/synthesis workflow: read the index → hybrid search → read the top
/// candidate pages in full → synthesise a grounded, cited answer, reporting gaps honestly.
/// A plain port (no Semantic Kernel types) so the Semantic Kernel Process Framework can later slot in behind it.
/// </summary>
public interface IQueryService
{
    /// <summary>Read index → hybrid search → read candidates → synthesise a cited answer.
    /// <paramref name="history"/> carries prior turns for follow-ups.</summary>
    Task<QueryResult> AnswerAsync(
        string wikiName, string question,
        IReadOnlyList<ConversationTurn> history, QueryOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Persist a produced answer as an Answer page, then index + log + embed it.</summary>
    Task<PageOutcome> SaveAnswerAsync(
        string wikiName, QueryResult result, CancellationToken cancellationToken = default);
}
