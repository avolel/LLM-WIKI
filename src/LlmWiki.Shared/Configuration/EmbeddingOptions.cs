namespace LlmWiki.Shared.Configuration;

/// <summary>Which text of a page is fed to the embedding model (BR-034).</summary>
public enum EmbeddingStrategy
{
    /// <summary>Title + full body (default): best general-purpose recall.</summary>
    TitleAndBody,

    /// <summary>Full body only.</summary>
    FullText,

    /// <summary>First paragraph only — cheaper, coarser.</summary>
    Summary,
}

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

    /// <summary>What page text to embed (env: EMBEDDING_STRATEGY). Phase 4, BR-034.</summary>
    public EmbeddingStrategy Strategy { get; set; } = EmbeddingStrategy.TitleAndBody;
}