namespace LlmWiki.Application.Diagnostics;

/// <summary>Runs the three Phase 0 acceptance checks and reports pass/fail per check.</summary>
public interface IDiagnosticsService
{
    Task<DiagnosticsReport> RunAsync(CancellationToken cancellationToken = default);
}
