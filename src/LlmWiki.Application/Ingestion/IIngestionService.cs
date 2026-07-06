namespace LlmWiki.Application.Ingestion;

/// <summary>
/// Orchestrates source ingestion (BR-010…BR-016): read source → extract → write summary →
/// create/update entity, concept and topic pages → flag contradictions/gaps. Implemented in
/// LlmWiki.Agents against the chat + wiki-repository ports. <paramref name="sourceRelativePath"/>
/// points at a file already under the wiki's immutable raw/ directory (NFR-02); the caller places it there.
/// </summary>
public interface IIngestionService
{
    Task<IngestionReport> IngestAsync(
        string wikiName,
        string sourceRelativePath,
        string sourceContent,
        CancellationToken cancellationToken = default);
}
