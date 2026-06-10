using LlmWiki.Application.Ports;
using Microsoft.Extensions.AI;

namespace LlmWiki.Infrastructure.Embeddings;

/// <summary>
/// Real embedding adapter backed by the Semantic Kernel Ollama connector's
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> (Microsoft.Extensions.AI).
/// Targets nomic-embed-text (768-dim). Exercised by Phase 0 diagnostics.
/// </summary>
public sealed class OllamaEmbeddingService(IEmbeddingGenerator<string, Embedding<float>> generator)
    : IEmbeddingService
{
    public async Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        var embedding = await generator.GenerateAsync(text, cancellationToken: cancellationToken);
        return embedding.Vector;
    }
}
