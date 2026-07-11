namespace LlmWiki.Application.Ports;

/// <summary>Oracle-persisted metadata for a project (a project == a wiki). BR-052.</summary>
public sealed record ProjectInfo(
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastIngestAt,
    int PageCount,
    int SourceCount);

/// <summary>
/// Port for the Oracle-backed project registry (Phase 6): which projects exist and their metadata.
/// A "project" is the existing per-wiki tenant — isolation is already enforced by the vector store's
/// wiki_name predicate (NFR-10); this adds durable metadata + enumeration (BR-050/052/053).
/// Implemented in Infrastructure; Oracle stays out of Domain/Application (NFR-07).
/// </summary>
public interface IProjectRepository
{
    /// <summary>Insert the project row if absent (idempotent create). BR-050/052.</summary>
    Task RegisterAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>All registered projects with metadata, ordered by name. BR-050/053.</summary>
    Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One project's metadata, or null if unregistered. BR-050.</summary>
    Task<ProjectInfo?> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Record an ingest: stamp last_ingest_at = now and store recomputed counts. BR-052.</summary>
    Task RecordIngestAsync(string name, int pageCount, int sourceCount, CancellationToken cancellationToken = default);
}
