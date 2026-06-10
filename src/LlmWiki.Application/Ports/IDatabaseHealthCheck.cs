namespace LlmWiki.Application.Ports;

/// <summary>
/// Port exercising raw Oracle connectivity for diagnostics: connect, run a
/// <c>CREATE TABLE</c>/drop round-trip, and report success. Implemented in
/// Infrastructure against ODP.NET. Directly backs the Phase 0 acceptance check.
/// </summary>
public interface IDatabaseHealthCheck
{
    /// <summary>Connect and perform a CREATE TABLE + DROP round-trip. Throws on failure.</summary>
    Task CreateTableRoundtripAsync(CancellationToken cancellationToken = default);
}
