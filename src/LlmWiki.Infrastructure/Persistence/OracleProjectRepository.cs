using LlmWiki.Application.Ports;
using LlmWiki.Domain;

namespace LlmWiki.Infrastructure.Persistence;

/// <summary>
/// Stub adapter for <see cref="IProjectRepository"/>. Real ODP.NET implementation lands in
/// Phase 3. Throws so accidental use in Phase 0 fails loudly.
/// </summary>
public sealed class OracleProjectRepository : IProjectRepository
{
    private const string NotYet = "IProjectRepository is not implemented until Phase 3.";

    public Task<WikiPage?> GetPageAsync(Guid id, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotYet);

    public Task<IReadOnlyList<WikiPage>> ListPagesAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotYet);

    public Task SavePageAsync(WikiPage page, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(NotYet);
}
