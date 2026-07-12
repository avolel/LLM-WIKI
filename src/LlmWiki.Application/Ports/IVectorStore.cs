using LlmWiki.Domain;

namespace LlmWiki.Application.Ports;

/// <summary>A page identified by its wiki-relative path, paired with its hybrid score.</summary>
public sealed record VectorSearchHit(
    string WikiName,
    string RelativePath,
    string Title,
    PageType Type,
    double Score,
    string? Snippet = null);

/// <summary>
/// Per-page embeddings + metadata in Oracle (VECTOR + Oracle Text). Data is partitioned by
/// wiki name so a search never crosses projects (NFR-10). Real ODP.NET adapter: Phase 4.
/// </summary>
public interface IVectorStore
{
    /// <summary>Insert or replace the row for one page (keyed by wiki + relative path).</summary>
    Task UpsertAsync(
        string wikiName, string relativePath, WikiPage page,
        ReadOnlyMemory<float> embedding, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hybrid search within one wiki: cosine VECTOR_DISTANCE (semantic) fused with Oracle Text
    /// CONTAINS (lexical) via reciprocal-rank fusion. Optional page-type filter (BR-032).
    /// </summary>
    Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
        string wikiName, string queryText, ReadOnlyMemory<float> queryEmbedding,
        int topK, PageType? typeFilter = null, CancellationToken cancellationToken = default);

    Task DeleteWikiAsync(string wikiName, CancellationToken cancellationToken = default);
}