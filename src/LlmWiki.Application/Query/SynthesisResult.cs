using System.Text.Json.Serialization;

namespace LlmWiki.Application.Query;

/// <summary>
/// The JSON shape the synthesis LLM returns. Non-null defaults so a partial reply still
/// deserializes — exactly <see cref="Ingestion.ExtractionResult"/>'s pattern.
/// </summary>
public sealed record SynthesisResult
{
    [JsonPropertyName("title")]     public string Title { get; init; } = string.Empty;
    [JsonPropertyName("answer")]    public string Answer { get; init; } = string.Empty;
    [JsonPropertyName("covered")]   public bool Covered { get; init; }
    [JsonPropertyName("citations")] public IReadOnlyList<string> Citations { get; init; } = []; // relative paths
}
