using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using Microsoft.AspNetCore.Mvc;

namespace LlmWiki.Api.Controllers;

/// <summary>
/// MVC controller for the project registry (Phase 6). Thin composition shim over the ports:
/// list/get read the Oracle registry; create scaffolds the wiki (files are canonical) then
/// registers it. `select` is a CLI-local concept (the active-project pointer is host-local),
/// so there is no endpoint for it.
/// </summary>
[ApiController]
[Route("projects")]
public sealed class ProjectController(
    IProjectRepository projects, 
    IWikiRepository wiki, 
    IVectorStore vectors) : ControllerBase
{
    /// <summary>List registered projects with metadata (BR-050/052).</summary>
    [HttpGet]
    public async Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct) => await projects.ListAsync(ct);

    /// <summary>One project's metadata, or 404 (BR-050).</summary>
    [HttpGet("{name}")]
    public async Task<IActionResult> GetAsync(string name, CancellationToken ct) =>
        await projects.GetAsync(name, ct) is { } info ? Ok(info) : NotFound();

    /// <summary>Create a project: scaffold the wiki + register it (BR-050).</summary>
    [HttpPost]
    public async Task<IActionResult> CreateAsync(CreateProjectRequest req, CancellationToken ct)
    {
        if (await wiki.WikiExistsAsync(req.Name, ct)) return Conflict();
        await wiki.CreateWikiAsync(new WikiSchema { WikiName = req.Name, LinkStyle = req.LinkStyle ?? LinkStyle.Wikilink }, ct);
        await projects.RegisterAsync(req.Name, ct);
        return Created($"/projects/{req.Name}", await projects.GetAsync(req.Name, ct));
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> DeleteAsync(string name, CancellationToken ct)
    {
        if (!await wiki.WikiExistsAsync(name, ct)) return NotFound();
        await wiki.DeleteWikiAsync(name, ct);                 // canonical
        try { await vectors.DeleteWikiAsync(name, ct); } catch { /* best-effort (NFR-06) */ }
        try { await projects.DeleteAsync(name, ct); } catch { /* best-effort */ }
        return NoContent();
    }
}

/// <summary>Request body for <c>POST /projects</c>.</summary>
public record CreateProjectRequest(string Name, LinkStyle? LinkStyle);
