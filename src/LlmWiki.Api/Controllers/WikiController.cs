using LlmWiki.Application.Ports;
using Microsoft.AspNetCore.Mvc;

namespace LlmWiki.Api.Controllers;

/// <summary>
/// Read-only browse surface for the Phase 8 client: the wiki page tree grouped by category
/// (BR-073) and a single page's full content so a citation can be opened (BR-071).
/// Thin passthrough to <see cref="IWikiRepository"/> — no new orchestration.
/// </summary>
[ApiController]
[Route("wikis")]
public sealed class WikiController(IWikiRepository repo) : ControllerBase
{
    /// <summary>Page tree grouped by top-level typed directory (BR-073).</summary>
    [HttpGet("{wiki}/pages")]
    public async Task<IActionResult> TreeAsync(string wiki, CancellationToken ct)
    {
        if (!await repo.WikiExistsAsync(wiki, ct)) return NotFound();
        var paths = await repo.ListPagesAsync(wiki, ct);
        var categories = paths
            .GroupBy(p => p.Contains('/') ? p[..p.IndexOf('/')] : "root")
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PageCategory(
                g.Key,
                g.OrderBy(p => p, StringComparer.Ordinal)
                 .Select(p => new PageRef(p, SlugOf(p)))
                 .ToList()))
            .ToList();
        return Ok(new WikiTree(wiki, categories));
    }

    /// <summary>One page's full content so a citation opens a real page (BR-071).
    /// Catch-all: <paramref name="relativePath"/> is e.g. "entities/acme.md".</summary>
    [HttpGet("{wiki}/pages/{**relativePath}")]
    public async Task<IActionResult> PageAsync(string wiki, string relativePath, CancellationToken ct)
    {
        if (!await repo.WikiExistsAsync(wiki, ct)) return NotFound();
        try
        {
            var page = await repo.ReadPageAsync(wiki, relativePath, ct);
            return Ok(page);
        }
        catch (FileNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
    }

    private static string SlugOf(string path)
    {
        var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
    }
}

/// <summary>A page in the tree: its relative path (the key <c>ReadPageAsync</c> takes) + a display slug.</summary>
public record PageRef(string RelativePath, string Slug);

/// <summary>One category (a top-level typed directory) and its pages.</summary>
public record PageCategory(string Name, IReadOnlyList<PageRef> Pages);

/// <summary>The full page tree for a wiki, grouped by category.</summary>
public record WikiTree(string WikiName, IReadOnlyList<PageCategory> Categories);
