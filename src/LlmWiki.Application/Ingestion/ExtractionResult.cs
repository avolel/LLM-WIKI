using System.Text.Json.Serialization;

namespace LlmWiki.Application.Ingestion;

/// <summary>JSON shape returned by the single structured extraction call. Maps the source's
/// knowledge into the wiki's page taxonomy without inventing facts (BR-012).</summary>
public sealed record ExtractionResult
{
    [JsonPropertyName("sourceTitle")] public string SourceTitle { get; init; } = "Untitled Source";
    [JsonPropertyName("summary")] public string Summary { get; init; } = string.Empty;
    [JsonPropertyName("keyPoints")] public List<string> KeyPoints { get; init; } = [];
    [JsonPropertyName("entities")] public List<ExtractedItem> Entities { get; init; } = [];
    [JsonPropertyName("concepts")] public List<ExtractedItem> Concepts { get; init; } = [];
    [JsonPropertyName("tags")] public List<string> Tags { get; init; } = [];
    [JsonPropertyName("topicTitle")] public string TopicTitle { get; init; } = string.Empty;
    [JsonPropertyName("topicSummary")] public string TopicSummary { get; init; } = string.Empty;
}

public sealed record ExtractedItem
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
    /// <summary>True when the source only mentions this in passing — written as a stub (BR-015).</summary>
    [JsonPropertyName("thin")] public bool Thin { get; init; }
}

/// <summary>Result of the optional reconciliation call against existing pages (BR-014).</summary>
public sealed record ReconcileResult
{
    [JsonPropertyName("contradictions")] public List<ReconcileItem> Contradictions { get; init; } = [];
}

public sealed record ReconcileItem
{
    [JsonPropertyName("page")] public string Page { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
}
