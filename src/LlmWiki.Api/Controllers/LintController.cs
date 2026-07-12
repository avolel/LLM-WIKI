using LlmWiki.Application.Ingestion;
using LlmWiki.Application.Linting;
using LlmWiki.Application.Ports;
using Microsoft.AspNetCore.Mvc;

namespace LlmWiki.Api.Controllers;

/// <summary>
/// Thin MVC controller for the lint / health-check workflow (BR-060…063): a report endpoint and an
/// apply endpoint so the future React-Native client (Phase 8) can drive accept/reject over HTTP.
/// Mirrors <see cref="QueryController"/> — no logic here beyond a wiki-existence guard.
/// </summary>
[ApiController]
[Route("lint")]
public sealed class LintController(ILintService svc, IWikiRepository repo) : ControllerBase
{
    /// <summary>Run a lint pass and return the prioritised findings (BR-060/061). Non-interactive.</summary>
    [HttpPost]
    public async Task<IActionResult> PostAsync(LintRequest req, CancellationToken ct)
    {
        if (!await repo.WikiExistsAsync(req.Wiki, ct)) return NotFound();
        return Ok(await svc.LintAsync(req.Wiki, ct));
    }

    /// <summary>Apply one finding's fix (stub creation). The client echoes back a finding from the
    /// report; only findings carrying a Fix are applyable (BR-063).</summary>
    [HttpPost("apply")]
    public async Task<IActionResult> ApplyAsync(ApplyFixRequest req, CancellationToken ct)
    {
        if (!await repo.WikiExistsAsync(req.Wiki, ct)) return NotFound();
        if (req.Finding.Fix is null) return BadRequest("finding has no applyable fix");
        var outcome = await svc.ApplyFixAsync(req.Wiki, req.Finding, ct);
        return outcome.Change == PageChange.Failed ? UnprocessableEntity(outcome) : Ok(outcome);
    }
}

/// <summary>Request body for <c>POST /lint</c>.</summary>
public record LintRequest(string Wiki);

/// <summary>Request body for <c>POST /lint/apply</c>: a finding echoed back from the report.</summary>
public record ApplyFixRequest(string Wiki, LintFinding Finding);
