namespace LlmWiki.Application.Indexing;

/// <summary>One page that could not be embedded/upserted during a reindex (best-effort — NFR-06).</summary>
public sealed record PageEmbedFailure(string RelativePath, string Error);

/// <summary>Outcome of embedding every existing page of a wiki into the vector store.</summary>
public sealed record ReindexReport(
    string WikiName,
    int Embedded,
    IReadOnlyList<PageEmbedFailure> Failures)
{
    public int Failed => Failures.Count;
    public bool HasFailures => Failures.Count > 0;
}

/// <summary>
/// Backfills the Oracle vector store from the file store: embeds + upserts every current page of a
/// wiki (BR-030/033). Unlike ingestion it makes no LLM extraction calls and never edits content —
/// it only (re)builds the derived search index. Best-effort per page (NFR-06).
/// </summary>
public interface IWikiIndexer
{
    Task<ReindexReport> ReindexAsync(string wikiName, CancellationToken cancellationToken = default);
}