namespace LlmWiki.Application.Ingestion;

/// <summary>What an ingestion did to a single page.</summary>
public enum PageChange { Created, Updated, StubCreated, Failed }

public sealed record PageOutcome(
    string RelativePath, 
    string Title, 
    PageChange Change, 
    string? Detail = null);

/// <summary>A contradiction the agent noted between the new source and an existing page (BR-014).</summary>
public sealed record Contradiction(string PageRelativePath, string Description);

/// <summary>An entity/concept referenced but thin or absent — flagged or stubbed (BR-015).</summary>
public sealed record KnowledgeGap(string Subject, string Detail);

/// <summary>
/// Structured result of one ingestion run. The CLI prints it now; Phase 3 will turn it into
/// index/log updates. Never throws for per-page failures — those land in <see cref="Outcomes"/>.
/// </summary>
public sealed record IngestionReport(
    string WikiName,
    string SourceRelativePath,
    IReadOnlyList<PageOutcome> Outcomes,
    IReadOnlyList<Contradiction> Contradictions,
    IReadOnlyList<KnowledgeGap> Gaps)
{
    public bool HasFailures => Outcomes.Any(o => o.Change == PageChange.Failed);
}
