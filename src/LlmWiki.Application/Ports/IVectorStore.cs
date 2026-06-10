using LlmWiki.Domain;

namespace LlmWiki.Application.Ports;

/// <summary>A page paired with its similarity score from a vector search.</summary>
public sealed record VectorSearchHit(WikiPage Page, double Score);

/// <summary>
/// Port for the Oracle 23ai VECTOR-backed similarity store. Implemented in Infrastructure.
/// Signatures only in Phase 0; the real ODP.NET + VECTOR adapter is Phase 4.
/// </summary>
public interface IVectorStore
{
    Task UpsertAsync(WikiPage page, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        CancellationToken cancellationToken = default);
}
