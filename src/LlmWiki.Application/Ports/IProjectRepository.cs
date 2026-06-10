using LlmWiki.Domain;

namespace LlmWiki.Application.Ports;

/// <summary>
/// Port for persistence of projects and their pages (Oracle relational store).
/// Implemented in Infrastructure. Signatures only in Phase 0; bodies land in Phase 3.
/// </summary>
public interface IProjectRepository
{
    Task<WikiPage?> GetPageAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WikiPage>> ListPagesAsync(CancellationToken cancellationToken = default);

    Task SavePageAsync(WikiPage page, CancellationToken cancellationToken = default);
}
