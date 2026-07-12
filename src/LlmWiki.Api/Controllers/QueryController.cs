using LlmWiki.Application.Ports;
using LlmWiki.Application.Query;
using LlmWiki.Domain;
using Microsoft.AspNetCore.Mvc;

namespace LlmWiki.Api.Controllers;

/// <summary>
/// Thin MVC controller for the query workflow: constructor-injects the ports and
/// delegates straight to <see cref="IQueryService"/> — no logic here (it's a composition
/// shim over the port). Non-streaming JSON; first controller (the rest stay minimal-API).
/// </summary>
[ApiController]
[Route("query")]
public sealed class QueryController(IQueryService svc, IWikiRepository repo) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> PostAsync(QueryRequest req, CancellationToken ct)
    {
        if (!await repo.WikiExistsAsync(req.Wiki, ct)) return NotFound();

        var result = await svc.AnswerAsync(
            req.Wiki, req.Question, req.History ?? [],
            new QueryOptions(req.TopK ?? 5, req.Type), ct);

        // Optional best-effort save when the answer is grounded; never affects the response.
        if (req.Save == true && result.Covered)
        {
            await svc.SaveAnswerAsync(req.Wiki, result, ct);
        }

        return Ok(result);
    }

    /// <summary>
    /// Persist an already-synthesised, covered answer as an Answer page (BR-045) without paying for a
    /// second synthesis — the Phase 8 client's "Save answer" button. Refuses uncovered (gap) answers.
    /// </summary>
    [HttpPost("save")]
    public async Task<IActionResult> SaveAsync(SaveAnswerRequest req, CancellationToken ct)
    {
        if (!await repo.WikiExistsAsync(req.Wiki, ct)) return NotFound();
        if (!req.Result.Covered) return BadRequest("won't save an uncovered (gap) answer");
        var outcome = await svc.SaveAnswerAsync(req.Wiki, req.Result, ct);
        return Ok(outcome);
    }
}

/// <summary>Request body for <c>POST /query</c>. <see cref="History"/> carries follow-up context.</summary>
public record QueryRequest(
    string Wiki,
    string Question,
    IReadOnlyList<ConversationTurn>? History,
    int? TopK,
    PageType? Type,
    bool? Save);

/// <summary>Request body for <c>POST /query/save</c>: persist a produced result as an Answer page.</summary>
public record SaveAnswerRequest(string Wiki, QueryResult Result);
