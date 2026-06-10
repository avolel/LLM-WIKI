namespace LlmWiki.Application.Ports;

/// <summary>
/// Port for turning text into a dense vector. Backed by Ollama nomic-embed-text (768-dim)
/// in Infrastructure. The connectivity path is exercised by Phase 0 diagnostics.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>Embed a single string. The returned vector length must equal the configured dimension.</summary>
    Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default);
}
