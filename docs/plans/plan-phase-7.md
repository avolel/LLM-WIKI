# Plan — Phase 7: Linting / health check

## Context

Phases 0–6 built a database-driven LLM wiki that **ingests** sources into typed markdown pages, **journals** an `index.md`/`log.md`, **retrieves** with hybrid vector + Oracle Text search, **synthesises** cited answers, and tracks projects. What is missing is the maintenance half of the value proposition: an agent that keeps the wiki *internally consistent* — the whole reason the BRD prefers a compiled wiki over plain RAG (§3.3). Today nothing detects that a page links to a page that no longer exists, that a page is orphaned, that two pages contradict each other, or that a referenced concept was never written.

Phase 7 (BR-060…BR-063) adds a **lint / health-check workflow**: walk the wiki, produce a **prioritised** report (critical → warning → suggestion), and let the user **accept/reject** actionable suggestions interactively — no page changes without confirmation unless an explicit auto-fix mode is on (BR-063). It also doubles as a research-planning aid, suggesting new questions and sources (BR-062).

**Decisions locked with the user:**
1. **Hybrid computation.** A deterministic structural pass (reusing `IWikiRepository.ResolveLinksAsync` + `CrossReferenceParser`) reliably catches broken links, orphans, missing pages and thin pages — the checks the acceptance criteria say must be caught *every time*. A single JSON-mode LLM call adds the semantic findings (contradictions, stale claims, suggested questions/sources) that need judgment. This mirrors the `QueryService` orchestrator shape exactly (ports only, one `chat.CompleteAsync(jsonMode:true)`, shared `ExtractJson`).
2. **Stub-creation is the only applyable fix.** Accepting a *missing-page* finding writes a stub page (then rebuilds the index, appends a `lint` log line, and best-effort embeds it — mirroring `QueryService.SaveAnswerAsync`). This satisfies the "cross-page concept lacking a page → creation suggested → accept applies it" acceptance criterion. All other findings (contradictions, orphans, stale, suggestions) are **report-only** — we do not let LLM-authored writes mutate pages this phase.
3. **CLI + full API.** CLI `lint [wiki] [--fix] [--report]` (interactive accept/reject by default). API gets **both** a report endpoint (`POST /lint`) and an apply endpoint (`POST /lint/apply`) so the future React-Native client (Phase 8) can drive accept/reject over HTTP.

**Design principles held:** the lint orchestrator is a **plain orchestrator** — Application ports only, no Semantic Kernel types (NFR-07), so the SK Process Framework can later replace it behind `ILintService`. Every write on apply is best-effort with per-step boundaries so a journal/embed failure is recorded, never thrown (NFR-06). No new Oracle table — lint output is *derived/computed*, not persisted. `raw/` is never touched (NFR-02). Files stay canonical.

---

## Key facts grounding the design

- **No lint code exists yet.** The only `HealthCheck` in the repo is `IDatabaseHealthCheck` (Oracle connectivity) — unrelated. Phase 7 is a fresh `Linting` vertical alongside `Ingestion`/`Query`/`Indexing`/`Diagnostics`.
- **`ResolveLinksAsync` already does the hard part.** [FileSystemWikiRepository.cs:85-98](../Code/LLM-WIKI/src/LlmWiki.Infrastructure/FileStore/FileSystemWikiRepository.cs#L85) parses a page's cross-refs against the wiki's `LinkStyle` and returns a `LinkResolutionReport` whose `.Broken` are unresolved links and each `ResolvedLink` carries `Exists`/`ResolvedPath` ([CrossReference.cs](../Code/LLM-WIKI/src/LlmWiki.Domain/CrossReference.cs)). Looping `ListPagesAsync` → `ResolveLinksAsync` gives broken-link findings *and* the resolved inbound-link graph for orphan detection with **zero new parsing code**.
- **`ListPagesAsync` already excludes non-content files.** `IsPage` filters `SCHEMA.md`/`index.md`/`log.md`/`raw/*` (FileSystemWikiRepository), so lint never mis-flags them.
- **`QueryService` is the orchestrator + apply template.** [QueryService.cs](../Code/LLM-WIKI/src/LlmWiki.Agents/Query/QueryService.cs): primary-ctor over ports only; one JSON call parsed through `ExtractJson`; `SaveAnswerAsync` writes a page then **best-effort** rebuilds the index, appends a log line, and embeds — a journal/embed failure is recorded on the outcome `Detail`, never thrown (lines 99-137). `ApplyFixAsync` copies this exactly.
- **Report DTO conventions exist.** `DiagnosticsReport(IReadOnlyList<DiagnosticCheck>)` + aggregate bool, and `ReindexReport(..., int Embedded, IReadOnlyList<PageEmbedFailure> Failures)` with `HasFailures`, are the shape to mirror for `LintReport`/`LintFinding`. Cross-vertical DTO reuse is established: `IQueryService.SaveAnswerAsync` returns `Application.Ingestion.PageOutcome`; `ApplyFixAsync` returns the same.
- **LLM JSON contract convention.** `ExtractionResult`/`ReconcileResult` (public records in `Application.Ingestion`, `[JsonPropertyName]`-annotated) are the deserialization pattern; `IChatService.CompleteAsync(prompt, jsonMode:true, ct)` is the call; `IngestionPrompts`/`QueryPrompts` (internal static, `$$"""…"""`) are the prompt pattern.
- **CLI/API wiring.** New agent services register with one line in `AddLlmWikiAgents` ([Agents/DependencyInjection.cs](../Code/LLM-WIKI/src/LlmWiki.Agents/DependencyInjection.cs)) and both hosts pick them up. `BuildReindexCommand` is the simple-command template; the `ask` REPL (Program.cs:242-291) is the interactive accept/reject template; `ResolveWikiAsync` (Program.cs:40-55) defaults the wiki to the active project. `QueryController`/`ProjectController` are the MVC-controller template, auto-wired by `app.MapControllers()`.
- **Tests: xUnit + hand-written fakes**, no mocking lib. `LintServiceTests` mirrors `QueryServiceTests`: temp-dir `WikiOptions.RootPath`, real `FileSystemWiki*` adapters, `ScriptedChat`/`FakeEmbeddingService`/`RecordingVectorStore`/`Throwing*` nested fakes. API tests use `WebApplicationFactory<Program>` + `ConfigureTestServices` + `RemoveAll<T>()`.

---

## Files to change

### 1. `src/LlmWiki.Application/Linting/ILintService.cs` (NEW) — the port

```csharp
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
```

### 2. `src/LlmWiki.Application/Linting/LintReport.cs` (NEW) — the DTOs

```csharp
using LlmWiki.Domain;

namespace LlmWiki.Application.Linting;

/// <summary>Priority bucket for a finding (BR-061): critical → warning → suggestion.</summary>
public enum LintSeverity { Critical, Warning, Suggestion }

/// <summary>What kind of issue a finding is (BR-060/062).</summary>
public enum LintCategory
{
    Contradiction, StaleClaim,          // critical (LLM)
    BrokenLink, MissingPage, Orphan,    // warning (structural)
    ThinPage, SuggestedQuestion, SuggestedSource   // suggestion
}

/// <summary>
/// A concrete, applyable fix carried by a finding. This phase only stub-page creation: accepting a
/// MissingPage finding writes this page. Report-only findings carry <c>Fix = null</c>.
/// </summary>
public sealed record SuggestedFix(string RelativePath, string Title, PageType Type, string Body);

/// <summary>One lint finding. <see cref="Pages"/> names the specific page(s) — both sides for a
/// contradiction (BR-061). <see cref="Fix"/> present ⇒ applyable via <c>ApplyFixAsync</c>.</summary>
public sealed record LintFinding(
    LintSeverity Severity,
    LintCategory Category,
    string Summary,
    IReadOnlyList<string> Pages,
    string? SuggestedAction = null,
    SuggestedFix? Fix = null);

/// <summary>Result of one lint pass. Findings are pre-sorted critical → warning → suggestion (BR-061).</summary>
public sealed record LintReport(string WikiName, IReadOnlyList<LintFinding> Findings)
{
    public bool IsClean => Findings.Count == 0;
    public int CriticalCount => Findings.Count(f => f.Severity == LintSeverity.Critical);
    public int WarningCount  => Findings.Count(f => f.Severity == LintSeverity.Warning);
}
```

### 3. `src/LlmWiki.Application/Linting/LintAnalysis.cs` (NEW) — the LLM JSON contract

Mirrors `ExtractionResult`/`ReconcileResult` (public, `[JsonPropertyName]`). This is what the single semantic call returns.

```csharp
using System.Text.Json.Serialization;

namespace LlmWiki.Application.Linting;

/// <summary>JSON shape of the single semantic-analysis call: findings that need judgment, not
/// structure. Grounded to the pages provided; no page mutation results from this directly (BR-060/062).</summary>
public sealed record LintAnalysis
{
    [JsonPropertyName("contradictions")] public List<AnalyzedContradiction> Contradictions { get; init; } = [];
    [JsonPropertyName("staleClaims")]    public List<AnalyzedIssue> StaleClaims { get; init; } = [];
    [JsonPropertyName("questions")]      public List<string> Questions { get; init; } = [];
    [JsonPropertyName("sources")]        public List<string> Sources { get; init; } = [];
}

public sealed record AnalyzedContradiction
{
    [JsonPropertyName("pages")]       public List<string> Pages { get; init; } = [];
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
}

public sealed record AnalyzedIssue
{
    [JsonPropertyName("page")]        public string Page { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
}
```

### 4. `src/LlmWiki.Agents/Prompts/LintPrompts.cs` (NEW) — the semantic prompt

Sibling to `IngestionPrompts`/`QueryPrompts`.

```csharp
using LlmWiki.Domain;

namespace LlmWiki.Agents.Prompts;

/// <summary>
/// Semantic health-check prompt: given the index + a digest of the pages, find contradictions and
/// stale claims, and suggest research directions. Structural issues (broken links, orphans) are found
/// deterministically, NOT here, so this prompt stays focused on judgment (BR-060/062).
/// </summary>
internal static class LintPrompts
{
    public static string Analyze(WikiSchema schema, string indexMarkdown, string pagesDigest) => $$"""
        You are auditing the wiki "{{schema.WikiName}}" for internal consistency using ONLY the PAGES below.
        Report ONLY issues you can justify from the PAGES — do not invent problems.
        - contradictions: two pages that assert incompatible facts. List BOTH page paths and describe the conflict.
        - staleClaims: a claim that is likely outdated or superseded by another page. Name the page.
        - questions: useful follow-up questions the wiki does not yet answer (research planning).
        - sources: kinds of source material the user should seek to fill gaps.
        Return one JSON object, no fence:
        {"contradictions":[{"pages":["a.md","b.md"],"description":"…"}],
         "staleClaims":[{"page":"…","description":"…"}],
         "questions":["…"],"sources":["…"]}

        WIKI INDEX:
        {{indexMarkdown}}

        PAGES:
        {{pagesDigest}}
        """;
}
```

### 5. `src/LlmWiki.Agents/Linting/LintService.cs` (NEW) — the orchestrator

Plain orchestrator; ctor ports only. `LintAsync` = deterministic structural pass + one best-effort LLM call, merged and sorted. `ApplyFixAsync` = `SaveAnswerAsync`-shaped stub write.

```csharp
using System.Text;
using System.Text.Json;
using LlmWiki.Agents.Ingestion;      // EmbeddingText.For
using LlmWiki.Agents.Prompts;
using LlmWiki.Application.Ingestion;  // PageOutcome / PageChange
using LlmWiki.Application.Linting;
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Agents.Linting;

public sealed class LintService(
    IChatService chat,
    IWikiRepository wiki,
    IWikiFileStore files,          // to read index.md for the LLM digest (IWikiJournal has no read port)
    IWikiJournal journal,
    IVectorStore vectors,
    IEmbeddingService embeddings,
    IOptions<EmbeddingOptions> embedOptions) : ILintService
{
    private const int ThinContentThreshold = 200;   // chars; pages shorter than this are "thin" (BR-060)
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly EmbeddingStrategy _strategy = embedOptions.Value.Strategy;

    public async Task<LintReport> LintAsync(string wikiName, CancellationToken ct = default)
    {
        var schema = await wiki.ReadSchemaAsync(wikiName, ct);
        var paths = await wiki.ListPagesAsync(wikiName, ct);
        var findings = new List<LintFinding>();

        // 1. Structural pass (deterministic, reliable). Reuse ResolveLinksAsync per page — it also
        //    yields the resolved inbound-link graph we need for orphans, with no new parsing code.
        var inbound = new HashSet<string>(StringComparer.Ordinal);
        var digest = new StringBuilder();
        foreach (var path in paths)
        {
            var page = await wiki.ReadPageAsync(wikiName, path, ct);
            if (page.Content.Trim().Length < ThinContentThreshold)
                findings.Add(new LintFinding(LintSeverity.Suggestion, LintCategory.ThinPage,
                    $"'{path}' is thin ({page.Content.Trim().Length} chars) — consider expanding.", [path]));

            var links = await wiki.ResolveLinksAsync(wikiName, path, ct);
            foreach (var l in links.Links)
            {
                if (l.Exists) { inbound.Add(l.ResolvedPath!); continue; }
                // A broken link is a warning; when the intended target is an unambiguous typed path
                // (dir/name.md) we attach a stub-creation Fix so it doubles as a "missing page" the
                // user can accept (BR-060 broken-link + missing-page; BR-063 applyable).
                var fix = TryStubFix(l.Reference.Target);
                findings.Add(new LintFinding(LintSeverity.Warning,
                    fix is null ? LintCategory.BrokenLink : LintCategory.MissingPage,
                    $"'{path}' links to '{l.Reference.Target}' which does not resolve.",
                    [path],
                    fix is null ? "Fix or remove the link." : $"Create stub {fix.RelativePath}.",
                    fix));
            }

            digest.AppendLine($"### {path} — {page.Title} [{page.Type}]");
            digest.AppendLine(page.Content);
            digest.AppendLine();
        }

        // Orphans: content pages nothing links to (BR-060). Literal definition; advisory.
        foreach (var path in paths.Where(p => !inbound.Contains(p)))
            findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.Orphan,
                $"'{path}' has no inbound links (orphan).", [path], "Link it from a related page."));

        // 2. Semantic pass (one LLM call). Best-effort — a chat/parse failure drops semantic findings
        //    but the structural report still returns (NFR-06).
        try
        {
            string index;
            try { index = await files.ReadAsync($"{wikiName}/index.md", ct); } catch { index = string.Empty; }
            var raw = await chat.CompleteAsync(LintPrompts.Analyze(schema, index, digest.ToString()), jsonMode: true, ct);
            var analysis = JsonSerializer.Deserialize<LintAnalysis>(ExtractJson(raw), Json) ?? new LintAnalysis();

            foreach (var c in analysis.Contradictions.Where(c => c.Pages.Any(paths.Contains)))
                findings.Add(new LintFinding(LintSeverity.Critical, LintCategory.Contradiction,
                    c.Description, c.Pages.Where(paths.Contains).ToList(), "Reconcile or note the discrepancy."));
            foreach (var s in analysis.StaleClaims.Where(s => paths.Contains(s.Page)))
                findings.Add(new LintFinding(LintSeverity.Critical, LintCategory.StaleClaim,
                    s.Description, [s.Page], "Review and update the claim."));
            foreach (var q in analysis.Questions)
                findings.Add(new LintFinding(LintSeverity.Suggestion, LintCategory.SuggestedQuestion, q, []));
            foreach (var src in analysis.Sources)
                findings.Add(new LintFinding(LintSeverity.Suggestion, LintCategory.SuggestedSource, src, []));
        }
        catch { /* semantic findings are best-effort; structural report is authoritative */ }

        // 3. Prioritise (BR-061). Stable within a severity preserves the deterministic-pass order.
        var sorted = findings.OrderBy(f => (int)f.Severity).ToList();

        // Best-effort log line so the pass is recorded in the wiki's history (BR-021).
        try
        {
            var body = $"- Findings: {sorted.Count} ({findings.Count(f => f.Severity == LintSeverity.Critical)} critical)";
            await journal.AppendLogAsync(wikiName,
                new LogEntry(DateOnly.FromDateTime(DateTime.UtcNow), "lint", $"{sorted.Count} finding(s)", body), ct);
        }
        catch { /* logging is best-effort */ }

        return new LintReport(wikiName, sorted);
    }

    public async Task<PageOutcome> ApplyFixAsync(string wikiName, LintFinding finding, CancellationToken ct = default)
    {
        if (finding.Fix is not { } fix)
            return new PageOutcome("(none)", finding.Summary, PageChange.Failed, "finding has no applyable fix");

        var page = new WikiPage { Title = fix.Title, Type = fix.Type, Content = fix.Body };

        // Write boundary (mirrors SaveAnswerAsync): a failure is a Failed outcome, never thrown.
        try { await wiki.WritePageAsync(wikiName, fix.RelativePath, page, ct); }
        catch (Exception ex) { return new PageOutcome(fix.RelativePath, page.Title, PageChange.Failed, ex.Message); }

        string? detail = null;
        try { await journal.RebuildIndexAsync(wikiName, ct);
              await journal.AppendLogAsync(wikiName, new LogEntry(
                  DateOnly.FromDateTime(DateTime.UtcNow), "lint", $"stub created: {fix.RelativePath}", null), ct); }
        catch (Exception ex) { detail = $"journal: {ex.Message}"; }

        try { var vector = await embeddings.EmbedAsync(EmbeddingText.For(page, _strategy), ct);
              await vectors.UpsertAsync(wikiName, fix.RelativePath, page, vector, ct); }
        catch (Exception ex) { var e = $"embed: {ex.Message}"; detail = detail is null ? e : $"{detail}; {e}"; }

        return new PageOutcome(fix.RelativePath, page.Title, PageChange.StubCreated, detail);
    }

    /// <summary>Map an unresolved link target to a stub path when it names an unambiguous typed page
    /// (e.g. <c>concepts/anvil.md</c> or a bare title we slug into a typed dir). Null ⇒ report-only.</summary>
    private static SuggestedFix? TryStubFix(string target)
    {
        var t = target.Trim();
        // Markdown-style explicit path: dir/name.md where dir is a known typed directory.
        var slashIdx = t.IndexOf('/');
        if (slashIdx > 0 && t.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var dir = t[..slashIdx];
            if (WikiSchema.Directories.Contains(dir) && dir != "raw")
            {
                var title = Path.GetFileNameWithoutExtension(t).Replace('-', ' ');
                return new SuggestedFix(t, title, TypeForDir(dir), $"Stub page for **{title}** (created by lint).");
            }
        }
        return null;   // bare wikilink target [[Foo]] has no unambiguous directory → report-only
    }

    private static PageType TypeForDir(string dir) => dir switch
    {
        "entities" => PageType.Entity,
        "concepts" => PageType.Concept,
        "topics"   => PageType.Overview,
        _          => PageType.Summary,
    };

    private static string ExtractJson(string s)  // identical tolerance to QueryService/IngestionService
    {
        s = s.Trim();
        if (s.StartsWith("```"))
        {
            var firstNl = s.IndexOf('\n');
            var body = firstNl >= 0 ? s[(firstNl + 1)..] : s;
            var fenceEnd = body.LastIndexOf("```", StringComparison.Ordinal);
            s = (fenceEnd >= 0 ? body[..fenceEnd] : body).Trim();
        }
        if (!s.StartsWith('{'))
        {
            var open = s.IndexOf('{'); var close = s.LastIndexOf('}');
            if (open >= 0 && close > open) s = s[open..(close + 1)];
        }
        return s;
    }
}
```

> Note: check that `WikiSchema.Directories` is a `string[]`/list exposing `Contains` (it is, per the exploration — `["summaries","entities","topics","raw"]`); add `concepts` handling — confirm the actual `Directories` constant during implementation and align `TypeForDir`/`TryStubFix` to whatever typed dirs it declares (concepts may live under its own dir). If `Directories` lacks `concepts`, extend the check to the dirs ingestion actually writes (`summaries/entities/concepts/topics`).

### 6. `src/LlmWiki.Agents/DependencyInjection.cs` (UPDATE) — register the service

Add one line in `AddLlmWikiAgents`:
```csharp
services.AddSingleton<ILintService, LintService>();
```
All ctor deps are already registered by `AddLlmWikiInfrastructure` — no other wiring. Add the `using LlmWiki.Agents.Linting; using LlmWiki.Application.Linting;` imports.

### 7. `src/LlmWiki.Cli/Program.cs` (UPDATE) — `lint` command

- Register: `root.Subcommands.Add(BuildLintCommand());` (line ~24).
- Add `BuildLintCommand()`. `wiki` is an optional positional (defaults to the active project via `ResolveWikiAsync`). `--fix` auto-applies every finding with a `Fix`; `--report` prints only (never prompts, never writes); default is the interactive accept/reject loop modelled on the `ask` REPL.

```csharp
static Command BuildLintCommand()
{
    var wikiArg = new Argument<string?>("wiki")
    { Description = "Wiki to lint (defaults to the active project).", Arity = ArgumentArity.ZeroOrOne };
    var fix = new Option<bool>("--fix") { Description = "Auto-apply every suggested fix without prompting." };
    var report = new Option<bool>("--report") { Description = "Print the report only; never prompt or change pages." };

    var lint = new Command("lint", "Health-check a wiki: report contradictions, orphans, broken links, gaps.");
    lint.Arguments.Add(wikiArg); lint.Options.Add(fix); lint.Options.Add(report);

    lint.SetAction(async (pr, ct) =>
    {
        await using var provider = BuildProvider();
        var repo = provider.GetRequiredService<IWikiRepository>();
        var current = provider.GetRequiredService<ICurrentProjectStore>();
        var svc = provider.GetRequiredService<ILintService>();

        var wikiName = await ResolveWikiAsync(repo, current, pr.GetValue(wikiArg), ct);
        if (wikiName is null) return 1;

        var result = await svc.LintAsync(wikiName, ct);
        if (result.IsClean) { Console.WriteLine("No issues found. ✓"); return 0; }

        Console.WriteLine($"{result.Findings.Count} finding(s) — {result.CriticalCount} critical, {result.WarningCount} warning:");
        foreach (var f in result.Findings)
        {
            PrintFinding(f);
            var applyable = f.Fix is not null && !pr.GetValue(report);
            if (!applyable) continue;

            if (pr.GetValue(fix))               // auto-fix mode (BR-063)
            { await ApplyAndReport(svc, wikiName, f, ct); continue; }

            // interactive accept/reject/modify (BR-063)
            Console.Write("   apply? [a]ccept / [r]eject / [m]odify title / [q]uit: ");
            var choice = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (choice is "q") break;
            if (choice is "a") await ApplyAndReport(svc, wikiName, f, ct);
            else if (choice is "m")
            {
                Console.Write("   new title: ");
                var title = Console.ReadLine()?.Trim();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    var edited = f with { Fix = f.Fix! with { Title = title } };
                    await ApplyAndReport(svc, wikiName, edited, ct);
                }
            }
        }
        // --report is a health gate: non-zero when critical findings exist.
        return pr.GetValue(report) && result.CriticalCount > 0 ? 1 : 0;
    });
    return lint;
}

static void PrintFinding(LintFinding f)
{
    var pages = f.Pages.Count > 0 ? $"  ({string.Join(", ", f.Pages)})" : "";
    Console.WriteLine($"  [{f.Severity,-10}] {f.Category,-16} {f.Summary}{pages}");
    if (f.SuggestedAction is not null) Console.WriteLine($"     → {f.SuggestedAction}");
}

static async Task ApplyAndReport(ILintService svc, string wiki, LintFinding f, CancellationToken ct)
{
    var o = await svc.ApplyFixAsync(wiki, f, ct);
    Console.WriteLine(o.Change == PageChange.Failed
        ? $"   apply failed: {o.Detail}"
        : $"   created {o.RelativePath}{(o.Detail is null ? "" : $" (note: {o.Detail})")}");
}
```
Add `using LlmWiki.Application.Linting;` at the top of Program.cs.

### 8. `src/LlmWiki.Api/Controllers/LintController.cs` (NEW) — `/lint` report + apply

Copies the `QueryController` shape; request records co-located. `POST /lint` returns the report; `POST /lint/apply` applies one finding the client sends back from the report (full-API path the user chose).

```csharp
using LlmWiki.Application.Ingestion;
using LlmWiki.Application.Linting;
using LlmWiki.Application.Ports;
using Microsoft.AspNetCore.Mvc;

namespace LlmWiki.Api.Controllers;

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

public record LintRequest(string Wiki);
public record ApplyFixRequest(string Wiki, LintFinding Finding);
```
Auto-wired by the existing `app.MapControllers()`; appears in Swagger UI. No `Program.cs` change needed.

### 9. Tests

- **`tests/LlmWiki.Agents.Tests/LintServiceTests.cs` (NEW)** — mirror `QueryServiceTests`: temp-dir root, real `FileSystemWikiFileStore`/`FileSystemWikiRepository`/`FileSystemWikiJournal`, `FakeEmbeddingService`, nested `ScriptedChat`/`RecordingVectorStore`/`ThrowingChat`/`ThrowingVectorStore`. `BuildService(chat, vectors)` news up `LintService` directly. Cases:
  - **Broken link (structural):** seed a markdown-link wiki with a page linking to a nonexistent `concepts/anvil.md`; assert a `MissingPage` finding whose `Fix.RelativePath == "concepts/anvil.md"`.
  - **Orphan:** seed a page nothing links to; assert an `Orphan` finding for it (and none for a page that IS linked).
  - **Thin page:** seed a <200-char page → `ThinPage` suggestion.
  - **Contradiction (LLM):** `ScriptedChat` returns `{"contradictions":[{"pages":["entities/a.md","entities/b.md"],"description":"…"}],…}`; assert a `Critical`/`Contradiction` finding citing both pages, and that findings are sorted critical-first.
  - **Apply creates the stub:** `ApplyFixAsync` on the MissingPage finding → outcome `StubCreated`; assert the file exists on disk (`File.Exists(Path.Combine(_root, wiki, "concepts/anvil.md"))`) and `RecordingVectorStore.Upserts` contains it.
  - **Resilience (NFR-06):** `ThrowingChat` → `LintAsync` still returns the structural findings, no throw. `ThrowingVectorStore` on apply → stub still written, outcome `StubCreated` with an `embed:` note in `Detail`.
- **`tests/LlmWiki.Agents.Tests/AgentsRegistrationTests.cs` (UPDATE)** — add `Assert.Contains(services, d => d.ServiceType == typeof(ILintService));`.
- **`tests/LlmWiki.Api.Tests/LintEndpointTests.cs` (NEW)** — `WebApplicationFactory<Program>` + `ConfigureTestServices` swapping `FakeLintService` (returns a seeded report incl. one finding with a `Fix`) + `FakeRepo`: `POST /lint {"wiki":"demo"}` → 200 with findings JSON; unknown wiki → 404; `POST /lint/apply` with a fix-bearing finding → 200 `StubCreated`; with a fixless finding → 400.

### 10. Docs (UPDATE)

- **`docs/code-overview/code-overview.md`** — flip the Phase 7 / lint row from "next 🔲" to done; add the `Linting` vertical (`ILintService`/`LintService`, `LintReport`/`LintFinding`), the `lint` CLI command, and the `/lint` + `/lint/apply` endpoints.
- **`docs/adr/0003-phase-7-lint.md` (NEW)** — record the load-bearing decisions (0001/0002 format): *hybrid computation (deterministic structural + one LLM call); stub-creation is the only page-mutating fix this phase; lint output is derived — no Oracle table; full API (report + apply) ahead of Phase 8's RN client.*
- **`CLAUDE.md`** — append a Phase 7 note to the "What this is" paragraph and the working-agreement trailer, consistent with the Phase 1–6 notes (new `Linting` vertical; hybrid lint; `lint` CLI + `/lint` API; no new stub — this is the last unbuilt application surface, leaving only Phase 8's RN client).

---

## Requirements covered

BR-060 (walk the wiki for contradictions, stale claims, orphans, missing pages, broken cross-references, thin/gap areas — structural deterministically, semantic via one LLM call), BR-061 (prioritised list critical → warning → suggestion, each naming the specific page(s) and a suggested action), BR-062 (suggested questions + sources make it a research-planning aid), BR-063 (interactive accept/reject/modify by default — no page changed without confirmation; `--fix` opt-in auto-apply). NFR-06 (per-step write boundaries on apply; semantic + logging best-effort — never corrupts the wiki), NFR-07 (plain orchestrator, ports only, no SK/Oracle in Domain/Application), NFR-02 (`raw/` untouched — `ListPagesAsync` excludes it).

---

## Verification (end-to-end)

Prereqs: `cd docker && docker compose up -d`; `env/.env` has a working chat + Oracle; `dotnet build LlmWiki.slnx` and `dotnet test LlmWiki.slnx` green.

1. **Seed a wiki with injected defects.** `project create lint-demo`; `ingest lint-demo ./docs/sample-source.md`. Then hand-author defects with the CLI: `wiki page add lint-demo concepts/orphan.md --title "Orphan" --type concept --body "A page nobody links to."` (orphan), and a page whose body contains a broken markdown link `[Ghost](concepts/ghost.md)` (missing page), plus a `--body "tiny"` page (thin).
2. **Report (BR-060/061):** `dotnet run --project src/LlmWiki.Cli -- lint lint-demo --report` → prints findings sorted critical → warning → suggestion: the orphan (warning), the broken/missing `concepts/ghost.md` link (warning, with a "Create stub" action), the thin page (suggestion), and any LLM contradictions/questions/sources. Exit code non-zero iff a critical finding exists.
3. **Interactive apply (BR-063):** `lint lint-demo` (no flags) → at the missing-page finding, type `a` → prints `created concepts/ghost.md`; confirm `wiki inspect lint-demo` now lists it and `search lint-demo "ghost"` can find it (embedded). Re-run `lint` → that finding is gone (page now exists), proving the reconciliation.
4. **Reject makes no change (BR-063):** re-seed another missing link; `lint`, type `r` → no page written; `wiki inspect` unchanged.
5. **Auto-fix (BR-063):** `lint lint-demo --fix` → every fix-bearing finding applied without prompts; the report shrinks on the next run.
6. **Contradiction cites both pages (BR-060/061):** ingest a second source that contradicts the first (or hand-author two entity pages with incompatible claims); `lint` → a `[Critical] Contradiction` finding naming both page paths.
7. **Resilience (NFR-06):** `docker compose stop ollama` (or unset the chat key) → `lint` still returns the structural findings (semantic pass silently skipped), no crash. `docker compose stop oracle` → accepting a stub still writes the page on disk; the outcome shows an `embed:` note, and `log.md` records the pass.
8. **API (full):** `dotnet run --project src/LlmWiki.Api`; `http://localhost:5080/swagger`; `POST /lint {"wiki":"lint-demo"}` → 200 findings; copy a fix-bearing finding into `POST /lint/apply {"wiki":"lint-demo","finding":{…}}` → 200 `StubCreated`; unknown wiki → 404; a fixless finding → 400.
9. **Journal (BR-021):** `grep "^## \[" wiki/lint-demo/log.md` shows `lint` entries alongside `ingest`/`query`.

---

## Out of scope (later phases)

Applying non-stub fixes (auto-editing contradictions/stale claims) is deliberately excluded — those stay report-only this phase. The React-Native lint UI (surfacing findings + accept/reject in the client) is Phase 8. Persisting lint history in Oracle is unnecessary — findings are recomputed each run and the pass is recorded in `log.md`. Page deletion/merge suggestions are not in BR-060…063.
