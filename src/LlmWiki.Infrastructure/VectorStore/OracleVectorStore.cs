using LlmWiki.Application.Ports;
using LlmWiki.Domain;

namespace LlmWiki.Infrastructure.VectorStore;

/// <summary>
/// Stub adapter for <see cref="IVectorStore"/>. The real Oracle 23ai VECTOR + Oracle Text
/// implementation lands in Phase 4 (see docker/oracle/spike-vector.sql). Throws so accidental
/// use in Phase 0 fails loudly.
/// </summary>
public sealed class OracleVectorStore : IVectorStore
{
    private const string NotYet = "IVectorStore is not implemented until Phase 4.";

    public Task UpsertAsync(WikiPage page, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotYet);

    public Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotYet);
}
