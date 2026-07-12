using LlmWiki.Application.Ingestion;   // reuse PageOutcome (as IQueryService does)

namespace LlmWiki.Application.Linting;

/// <summary>
/// The lint / health-check workflow (BR-060…063): walk a wiki, produce a prioritised report of
/// issues + research suggestions, and optionally apply a confirmed fix. Implemented in Agents as a
/// plain orchestrator (ports only, no Semantic Kernel), so the Process Framework can later replace it.
/// </summary>
public interface ILintService
{
    /// <summary>Walk the wiki and return findings, sorted critical → warning → suggestion (BR-060/061).</summary>
    Task<LintReport> LintAsync(string wikiName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Apply a finding's suggested fix (only findings carrying a <see cref="LintFinding.Fix"/> are
    /// applyable — stub-page creation this phase). Mirrors QueryService.SaveAnswerAsync: write the page,
    /// then best-effort rebuild index + log + embed. Never thrown for per-step failures (NFR-06).
    /// </summary>
    Task<PageOutcome> ApplyFixAsync(string wikiName, LintFinding finding, CancellationToken cancellationToken = default);
}
