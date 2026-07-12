using System.Text.Json.Serialization;

namespace LlmWiki.Application.Linting;

/// <summary>JSON shape of the single semantic-analysis call: findings that need judgment, not
/// structure. Grounded to the pages provided; no page mutation results from this directly (BR-060/062).</summary>
public sealed record LintAnalysis
{
    [JsonPropertyName("contradictions")] public List<AnalyzedContradiction> Contradictions { get; init; } = [];
    [JsonPropertyName("staleClaims")]    public List<AnalyzedIssue> StaleClaims { get; init; } = [];
    [JsonPropertyName("questions")]      public List<string> Questions { get; init; } = [];
    [JsonPropertyName("sources")]        public List<string> Sources { get; init; } = [];
}

public sealed record AnalyzedContradiction
{
    [JsonPropertyName("pages")]       public List<string> Pages { get; init; } = [];
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
}

public sealed record AnalyzedIssue
{
    [JsonPropertyName("page")]        public string Page { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
}
