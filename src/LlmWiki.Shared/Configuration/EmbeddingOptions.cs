namespace LlmWiki.Shared.Configuration;

/// <summary>Embedding provider settings (Ollama nomic-embed-text, 768-dim).</summary>
public sealed class EmbeddingOptions
{
    public const string SectionName = "Embedding";

    /// <summary>Ollama base endpoint (env: OLLAMA_ENDPOINT).</summary>
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>Model id (env: EMBEDDING_MODEL).</summary>
    public string Model { get; set; } = "nomic-embed-text";

    /// <summary>Expected vector dimensionality (env: EMBEDDING_DIM). Phase 0 asserts 768.</summary>
    public int Dimensions { get; set; } = 768;
}
