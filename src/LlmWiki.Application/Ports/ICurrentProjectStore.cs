namespace LlmWiki.Application.Ports;

/// <summary>
/// The locally-persisted "active project" pointer (BR-050: select the active project on startup).
/// Single-user, host-local state — deliberately NOT in Oracle, so selection works offline and does
/// not depend on the DB. Implemented over the wiki root by Infrastructure.
/// </summary>
public interface ICurrentProjectStore
{
    Task<string?> GetAsync(CancellationToken cancellationToken = default);
    Task SetAsync(string name, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}
