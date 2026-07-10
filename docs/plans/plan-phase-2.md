# Phase 2 — Source Ingestion Pipeline

## Context

Phase 1 delivered the file-backed wiki: `WikiPage`/`WikiSchema`/`PageType`/`LinkStyle` in the Domain,
`CrossReferenceParser` for reading links, and `IWikiRepository`/`FileSystemWikiRepository` for scaffolding
wikis and round-tripping pages with YAML frontmatter. Phase 0 proved the LLM path via `IChatService`
(SK chat connector, OpenAI/Anthropic) and embeddings via `IEmbeddingService`.

Phase 2 makes the wiki **grow from sources** (BR-010…BR-017). The user drops a markdown/text source into
a wiki's `raw/` directory and asks the agent to ingest it; the agent reads it, extracts entities/concepts/
claims, writes a summary page, creates/updates entity & concept pages, revises a topic overview connecting
the source to existing knowledge, notes contradictions against pages it already holds, and creates stub
pages for thin/referenced-but-absent entities. `raw/` is never modified (NFR-02). Index/log maintenance
(BR-020…024) is **Phase 3** and embeddings/search (BR-030…) are **Phase 4** — Phase 2 returns a structured
`IngestionReport` that the CLI prints and that Phase 3 will consume to write `log.md`.

## Locked Decisions (from review)

- **Orchestration: plain orchestrator, not the SK Process Framework.** An `IIngestionService` port in
  Application with discrete step methods, implemented in `LlmWiki.Agents` against the `IChatService` and
  `IWikiRepository` ports only — no direct SK dependency. Fully unit-testable with a fake chat service.
  The Process Framework (alpha, R-04) can later replace the implementation behind the same port without
  touching callers. Steps are written as discrete private methods so they lift cleanly into Process steps.
- **Extraction: a single structured LLM call.** One call returns JSON (summary + key points, entities,
  concepts, claims, suggested topic). Cheaper/faster and fewer failure points for a moderate corpus (NFR-08).
- **Scope: non-interactive CLI ingest + light contradiction detection.** Contradictions/gaps are checked
  only against existing entity/topic pages the agent loads **by name** (no vector/full-text search yet).
  Interactive confirmation (BR-017) defers to the Phase 8 UI.
- **Resilience (NFR-06): per-page write boundaries.** Each page write is independent; a failure is captured
  in the `IngestionReport` and the run continues, leaving the wiki truthful. No partial-page corruption.

## Architecture

```
CLI `ingest`  ─►  IIngestionService (Application port)
                        │  implemented in LlmWiki.Agents
                        ▼
        IngestionService ── IChatService (extract / reconcile)
                         └─ IWikiRepository (read schema, read/write/list pages)
                         └─ IWikiFileStore (read raw/ source content)
        Domain helpers: Slug.From, CrossReferenceWriter.Link
```

- **Domain** — two new pure helpers: `Slug` (title → filename slug, lifted out of the repository so Agents
  can reuse it without depending on Infrastructure) and `CrossReferenceWriter` (build a link string for a
  target title per `LinkStyle`, the write-side mirror of `CrossReferenceParser`).
- **Application** — new `Ingestion/` folder: the `IIngestionService` port and the `IngestionReport` result
  DTOs (pages created/updated, contradictions, gaps, failures). No vendor types — DTOs only.
- **Agents** — `IngestionService` (the orchestrator) + `ExtractionResult` JSON DTOs + `IngestionPrompts`
  (page-type-specific prompt builders guided by `SCHEMA.md`, BR-016). Registered via `AddLlmWikiAgents`.
- **Infrastructure** — `FileSystemWikiRepository.Slugify` is replaced by `Domain.Slug.From` (behaviour
  preserved); no other infra change.
- **Cli** — new `ingest <wiki> <file>` command; `BuildProvider` now also calls `AddLlmWikiAgents()`.

## New / Updated Files

### 1. `src/LlmWiki.Domain/Slug.cs` (NEW)

```csharp
namespace LlmWiki.Domain;

/// <summary>
/// Converts a human title into a stable, filesystem-safe slug used for page filenames
/// (e.g. "Acme Corp" → "acme-corp"). Lifted out of the file store so Domain consumers and the
/// Agents layer can produce matching paths without depending on Infrastructure.
/// </summary>
public static class Slug
{
    public static string From(string title)
    {
        var chars = title.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();
        var cleaned = new string(chars);
        return string.Join('-', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
```

### 2. `src/LlmWiki.Domain/CrossReferenceWriter.cs` (NEW)

```csharp
namespace LlmWiki.Domain;

/// <summary>
/// Write-side companion to <see cref="CrossReferenceParser"/>: renders a cross-reference to a target
/// page in the wiki's configured <see cref="LinkStyle"/> (BR-004). Wikilinks use the title; markdown
/// links use a path relative to the linking page's directory.
/// </summary>
public static class CrossReferenceWriter
{
    /// <param name="targetTitle">Display title of the target page.</param>
    /// <param name="targetRelativePath">Target page path within the wiki, e.g. "entities/acme-corp.md".</param>
    /// <param name="fromRelativePath">Path of the page that holds the link, e.g. "topics/anvils.md".</param>
    public static string Link(string targetTitle, string targetRelativePath, string fromRelativePath, LinkStyle style)
    {
        if (style == LinkStyle.Wikilink)
        {
            return $"[[{targetTitle}]]";
        }

        var fromDir = Path.GetDirectoryName(fromRelativePath)?.Replace('\\', '/') ?? string.Empty;
        var rel = Path.GetRelativePath(fromDir.Length == 0 ? "." : fromDir, targetRelativePath).Replace('\\', '/');
        return $"[{targetTitle}]({rel})";
    }
}
```

### 3. `src/LlmWiki.Application/Ingestion/IngestionReport.cs` (NEW)

```csharp
namespace LlmWiki.Application.Ingestion;

/// <summary>What an ingestion did to a single page.</summary>
public enum PageChange { Created, Updated, StubCreated, Failed }

public sealed record PageOutcome(string RelativePath, string Title, PageChange Change, string? Detail = null);

/// <summary>A contradiction the agent noted between the new source and an existing page (BR-014).</summary>
public sealed record Contradiction(string PageRelativePath, string Description);

/// <summary>An entity/concept referenced but thin or absent — flagged or stubbed (BR-015).</summary>
public sealed record KnowledgeGap(string Subject, string Detail);

/// <summary>
/// Structured result of one ingestion run. The CLI prints it now; Phase 3 will turn it into
/// index/log updates. Never throws for per-page failures — those land in <see cref="Outcomes"/>.
/// </summary>
public sealed record IngestionReport(
    string WikiName,
    string SourceRelativePath,
    IReadOnlyList<PageOutcome> Outcomes,
    IReadOnlyList<Contradiction> Contradictions,
    IReadOnlyList<KnowledgeGap> Gaps)
{
    public bool HasFailures => Outcomes.Any(o => o.Change == PageChange.Failed);
}
```

### 4. `src/LlmWiki.Application/Ingestion/IIngestionService.cs` (NEW)

```csharp
namespace LlmWiki.Application.Ingestion;

/// <summary>
/// Orchestrates source ingestion (BR-010…BR-016): read source → extract → write summary →
/// create/update entity, concept and topic pages → flag contradictions/gaps. Implemented in
/// LlmWiki.Agents against the chat + wiki-repository ports. <paramref name="sourceRelativePath"/>
/// points at a file already under the wiki's immutable raw/ directory (NFR-02); the caller places it there.
/// </summary>
public interface IIngestionService
{
    Task<IngestionReport> IngestAsync(
        string wikiName,
        string sourceRelativePath,
        string sourceContent,
        CancellationToken cancellationToken = default);
}
```

### 5. `src/LlmWiki.Agents/Ingestion/ExtractionResult.cs` (NEW)

```csharp
using System.Text.Json.Serialization;

namespace LlmWiki.Agents.Ingestion;

/// <summary>JSON shape returned by the single structured extraction call. Maps the source's
/// knowledge into the wiki's page taxonomy without inventing facts (BR-012).</summary>
internal sealed record ExtractionResult
{
    [JsonPropertyName("sourceTitle")] public string SourceTitle { get; init; } = "Untitled Source";
    [JsonPropertyName("summary")] public string Summary { get; init; } = string.Empty;
    [JsonPropertyName("keyPoints")] public List<string> KeyPoints { get; init; } = [];
    [JsonPropertyName("entities")] public List<ExtractedItem> Entities { get; init; } = [];
    [JsonPropertyName("concepts")] public List<ExtractedItem> Concepts { get; init; } = [];
    [JsonPropertyName("tags")] public List<string> Tags { get; init; } = [];
    [JsonPropertyName("topicTitle")] public string TopicTitle { get; init; } = string.Empty;
    [JsonPropertyName("topicSummary")] public string TopicSummary { get; init; } = string.Empty;
}

internal sealed record ExtractedItem
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
    /// <summary>True when the source only mentions this in passing — written as a stub (BR-015).</summary>
    [JsonPropertyName("thin")] public bool Thin { get; init; }
}

/// <summary>Result of the optional reconciliation call against existing pages (BR-014).</summary>
internal sealed record ReconcileResult
{
    [JsonPropertyName("contradictions")] public List<ReconcileItem> Contradictions { get; init; } = [];
}

internal sealed record ReconcileItem
{
    [JsonPropertyName("page")] public string Page { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
}
```

### 6. `src/LlmWiki.Agents/Prompts/IngestionPrompts.cs` (NEW)

```csharp
using LlmWiki.Domain;

namespace LlmWiki.Agents.Prompts;

/// <summary>Page-type-specific prompts (BR-016), constrained to the source content (R-01) and
/// guided by the wiki's schema conventions.</summary>
internal static class IngestionPrompts
{
    public static string Extract(WikiSchema schema, string sourceContent) => $$"""
        You are a knowledge-extraction agent for a structured wiki named "{{schema.WikiName}}".
        Read the SOURCE below and extract ONLY facts present in it. Do NOT invent or infer beyond the text.
        Return a single JSON object, no markdown fence, matching exactly this shape:
        {
          "sourceTitle": string,
          "summary": string,            // 3-6 sentence faithful summary
          "keyPoints": [string],
          "entities": [{"name": string, "description": string, "thin": boolean}],
          "concepts": [{"name": string, "description": string, "thin": boolean}],
          "tags": [string],
          "topicTitle": string,         // the overarching topic this source belongs to
          "topicSummary": string        // how this source relates to that topic
        }
        Set "thin" to true when the source only mentions an entity/concept in passing.

        SOURCE:
        {{sourceContent}}
        """;

    public static string Reconcile(string newSourceSummary, string existingPagesBlock) => $$"""
        Compare the NEW source summary against EXISTING wiki pages. Report only genuine factual
        contradictions — where a claim in the new source conflicts with a claim already on a page.
        Do not report mere additions. Return a single JSON object, no markdown fence:
        { "contradictions": [{"page": "<relative path>", "description": "<both sides, cited>"}] }

        NEW SOURCE SUMMARY:
        {{newSourceSummary}}

        EXISTING PAGES:
        {{existingPagesBlock}}
        """;
}
```

### 7. `src/LlmWiki.Agents/Ingestion/IngestionService.cs` (NEW)

```csharp
using System.Text;
using System.Text.Json;
using LlmWiki.Agents.Prompts;
using LlmWiki.Application.Ingestion;
using LlmWiki.Application.Ports;
using LlmWiki.Domain;

namespace LlmWiki.Agents.Ingestion;

/// <summary>
/// Plain orchestrator for source ingestion (BR-010…BR-016). Discrete steps; per-page write boundaries
/// so a single failure is recorded, not fatal (NFR-06). Uses only Application ports — no SK types — so
/// the Process Framework can later replace it behind <see cref="IIngestionService"/>.
/// </summary>
public sealed class IngestionService(
    IChatService chat,
    IWikiRepository wiki) : IIngestionService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<IngestionReport> IngestAsync(
        string wikiName, string sourceRelativePath, string sourceContent, CancellationToken ct = default)
    {
        var schema = await wiki.ReadSchemaAsync(wikiName, ct);
        var existing = await wiki.ListPagesAsync(wikiName, ct);

        var extraction = await ExtractAsync(schema, sourceContent, ct);

        var outcomes = new List<PageOutcome>();
        var gaps = new List<KnowledgeGap>();

        // Summary page (BR-012) — one per source.
        var summaryPath = $"summaries/{Slug.From(extraction.SourceTitle)}.md";
        await WritePageAsync(wikiName, summaryPath, new WikiPage
        {
            Title = extraction.SourceTitle,
            Type = PageType.Summary,
            Tags = extraction.Tags,
            Sources = [sourceRelativePath],
            Content = BuildSummaryBody(extraction),
        }, outcomes, ct);

        // Entity & concept pages (BR-013, BR-015).
        await WriteItemsAsync(wikiName, "entities", PageType.Entity, extraction.Entities, sourceRelativePath, existing, outcomes, gaps, ct);
        await WriteItemsAsync(wikiName, "concepts", PageType.Concept, extraction.Concepts, sourceRelativePath, existing, outcomes, gaps, ct);

        // Topic overview connecting the source to existing knowledge (BR-013).
        if (!string.IsNullOrWhiteSpace(extraction.TopicTitle))
        {
            var topicPath = $"topics/{Slug.From(extraction.TopicTitle)}.md";
            await WritePageAsync(wikiName, topicPath, new WikiPage
            {
                Title = extraction.TopicTitle,
                Type = PageType.Overview,
                Tags = extraction.Tags,
                Sources = [sourceRelativePath],
                Content = BuildTopicBody(extraction, schema, topicPath),
            }, outcomes, ct);
        }

        // Light contradiction pass against existing pages we touched by name (BR-014).
        var contradictions = await ReconcileAsync(wikiName, extraction, existing, ct);

        return new IngestionReport(wikiName, sourceRelativePath, outcomes, contradictions, gaps);
    }

    private async Task<ExtractionResult> ExtractAsync(WikiSchema schema, string source, CancellationToken ct)
    {
        var raw = await chat.CompleteAsync(IngestionPrompts.Extract(schema, source), ct);
        return JsonSerializer.Deserialize<ExtractionResult>(StripFence(raw), Json)
               ?? throw new InvalidOperationException("Extraction returned no parseable JSON.");
    }

    private async Task WriteItemsAsync(
        string wikiName, string dir, PageType type, IReadOnlyList<ExtractedItem> items,
        string sourcePath, IReadOnlyList<string> existing,
        List<PageOutcome> outcomes, List<KnowledgeGap> gaps, CancellationToken ct)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) continue;
            var path = $"{dir}/{Slug.From(item.Name)}.md";

            if (existing.Contains(path))
            {
                // Append the new source's contribution and add provenance.
                var current = await wiki.ReadPageAsync(wikiName, path, ct);
                var merged = current with
                {
                    Content = $"{current.Content}\n\n## From {sourcePath}\n\n{item.Description}",
                    Sources = current.Sources.Contains(sourcePath) ? current.Sources : [.. current.Sources, sourcePath],
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                await WritePageAsync(wikiName, path, merged, outcomes, ct, PageChange.Updated);
            }
            else
            {
                if (item.Thin) gaps.Add(new KnowledgeGap(item.Name, "Mentioned in passing; stub created."));
                await WritePageAsync(wikiName, path, new WikiPage
                {
                    Title = item.Name,
                    Type = type,
                    Sources = [sourcePath],
                    Content = item.Description,
                }, outcomes, ct, item.Thin ? PageChange.StubCreated : PageChange.Created);
            }
        }
    }

    private async Task<IReadOnlyList<Contradiction>> ReconcileAsync(
        string wikiName, ExtractionResult extraction, IReadOnlyList<string> existing, CancellationToken ct)
    {
        var names = extraction.Entities.Concat(extraction.Concepts).Select(i => i.Name);
        var matched = names
            .Select(n => $"entities/{Slug.From(n)}.md")
            .Concat(names.Select(n => $"concepts/{Slug.From(n)}.md"))
            .Where(existing.Contains)
            .Distinct()
            .ToList();
        if (matched.Count == 0) return [];

        var block = new StringBuilder();
        foreach (var path in matched)
        {
            var page = await wiki.ReadPageAsync(wikiName, path, ct);
            block.AppendLine($"### {path}\n{page.Content}\n");
        }

        var raw = await chat.CompleteAsync(IngestionPrompts.Reconcile(extraction.Summary, block.ToString()), ct);
        var result = JsonSerializer.Deserialize<ReconcileResult>(StripFence(raw), Json) ?? new ReconcileResult();

        var contradictions = new List<Contradiction>();
        foreach (var c in result.Contradictions.Where(c => existing.Contains(c.Page)))
        {
            // Note the discrepancy on the relevant page rather than overwrite (BR-014).
            var page = await wiki.ReadPageAsync(wikiName, c.Page, ct);
            await wiki.WritePageAsync(wikiName, c.Page, page with
            {
                Content = $"{page.Content}\n\n> **Contradiction noted:** {c.Description}",
                UpdatedAt = DateTimeOffset.UtcNow,
            }, ct);
            contradictions.Add(new Contradiction(c.Page, c.Description));
        }
        return contradictions;
    }

    private async Task WritePageAsync(
        string wikiName, string path, WikiPage page,
        List<PageOutcome> outcomes, CancellationToken ct, PageChange change = PageChange.Created)
    {
        try
        {
            await wiki.WritePageAsync(wikiName, path, page, ct);
            outcomes.Add(new PageOutcome(path, page.Title, change));
        }
        catch (Exception ex)
        {
            outcomes.Add(new PageOutcome(path, page.Title, PageChange.Failed, ex.Message));
        }
    }

    private static string BuildSummaryBody(ExtractionResult e)
    {
        var sb = new StringBuilder();
        sb.AppendLine(e.Summary).AppendLine();
        if (e.KeyPoints.Count > 0)
        {
            sb.AppendLine("## Key points").AppendLine();
            foreach (var p in e.KeyPoints) sb.AppendLine($"- {p}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string BuildTopicBody(ExtractionResult e, WikiSchema schema, string topicPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine(e.TopicSummary).AppendLine();
        var links = e.Entities.Concat(e.Concepts)
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i =>
            {
                var dir = e.Entities.Contains(i) ? "entities" : "concepts";
                var target = $"{dir}/{Slug.From(i.Name)}.md";
                return "- " + CrossReferenceWriter.Link(i.Name, target, topicPath, schema.LinkStyle);
            })
            .ToList();
        if (links.Count > 0)
        {
            sb.AppendLine("## Related").AppendLine();
            foreach (var l in links) sb.AppendLine(l);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Tolerate models that wrap JSON in a ```json fence.</summary>
    private static string StripFence(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```")) return s;
        var firstNl = s.IndexOf('\n');
        var body = firstNl >= 0 ? s[(firstNl + 1)..] : s;
        var fenceEnd = body.LastIndexOf("```", StringComparison.Ordinal);
        return (fenceEnd >= 0 ? body[..fenceEnd] : body).Trim();
    }
}
```

### 8. `src/LlmWiki.Agents/DependencyInjection.cs` (UPDATE)

Register the orchestrator (replaces the no-op body):

```csharp
using LlmWiki.Agents.Ingestion;
using LlmWiki.Application.Ingestion;
using Microsoft.Extensions.DependencyInjection;

namespace LlmWiki.Agents;

public static class DependencyInjection
{
    public static IServiceCollection AddLlmWikiAgents(this IServiceCollection services)
    {
        // Phase 2: ingestion orchestrator (plain; SK Process Framework can replace it behind the port).
        services.AddSingleton<IIngestionService, IngestionService>();
        return services;
    }
}
```

### 9. `src/LlmWiki.Cli/Program.cs` (UPDATE)

Add `AddLlmWikiAgents()` to the provider and register an `ingest` command. Changed/added regions only:

```csharp
// usings (add):
using LlmWiki.Agents;
using LlmWiki.Application.Ingestion;

// BuildProvider — also wire the Agents layer so IIngestionService resolves:
static ServiceProvider BuildProvider()
{
    var services = new ServiceCollection();
    services.AddLlmWikiInfrastructure(LlmWikiConfiguration.Build());
    services.AddLlmWikiAgents();
    return services.BuildServiceProvider();
}

// register alongside the other subcommands:
root.Subcommands.Add(BuildIngestCommand());

// new command builder:
static Command BuildIngestCommand()
{
    var wikiArg = new Argument<string>("wiki") { Description = "Target wiki name." };
    var fileArg = new Argument<FileInfo>("file") { Description = "Source file to ingest (markdown/text)." };
    var ingest = new Command("ingest", "Ingest a source into a wiki: copies it into raw/ then builds pages.");
    ingest.Arguments.Add(wikiArg);
    ingest.Arguments.Add(fileArg);
    ingest.SetAction(async (pr, ct) =>
    {
        await using var provider = BuildProvider();
        var repo = provider.GetRequiredService<IWikiRepository>();
        var files = provider.GetRequiredService<IWikiFileStore>();
        var service = provider.GetRequiredService<IIngestionService>();

        var wikiName = pr.GetValue(wikiArg)!;
        if (!await repo.WikiExistsAsync(wikiName, ct))
        {
            await Console.Error.WriteLineAsync($"Wiki '{wikiName}' not found.");
            return 1;
        }

        var file = pr.GetValue(fileArg)!;
        var content = await File.ReadAllTextAsync(file.FullName, ct);

        // Place the source under the immutable raw/ dir (write-once; never overwrite — NFR-02).
        var rawPath = $"{wikiName}/raw/{file.Name}";
        if (!await files.ExistsAsync(rawPath, ct))
        {
            await files.WriteAsync(rawPath, content, ct);
        }

        var report = await service.IngestAsync(wikiName, $"raw/{file.Name}", content, ct);

        Console.WriteLine($"Ingested {file.Name} into '{wikiName}':");
        foreach (var o in report.Outcomes)
            Console.WriteLine($"  [{o.Change,-11}] {o.RelativePath}{(o.Detail is null ? "" : $" — {o.Detail}")}");
        foreach (var c in report.Contradictions)
            Console.WriteLine($"  [contradiction] {c.PageRelativePath}: {c.Description}");
        foreach (var g in report.Gaps)
            Console.WriteLine($"  [gap] {g.Subject}: {g.Detail}");
        Console.WriteLine(report.HasFailures ? "Completed with failures." : "Done.");
        return report.HasFailures ? 1 : 0;
    });
    return ingest;
}
```

### 10. `src/LlmWiki.Infrastructure/FileStore/FileSystemWikiRepository.cs` (UPDATE)

Reuse the Domain slug helper instead of the private `Slugify` (behaviour preserved). Replace the call site
and delete the local method:

```csharp
// at line ~110, was: var slug = Slugify(target);
var slug = Slug.From(target);

// delete the private static Slugify method (lines ~127+); add `using LlmWiki.Domain;` if not present.
```

### 11. `tests/LlmWiki.Agents.Tests/LlmWiki.Agents.Tests.csproj` (UPDATE)

The orchestrator test drives the real `FileSystemWikiRepository` over a temp dir with a fake chat service,
so reference Infrastructure + Shared:

```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\LlmWiki.Agents\LlmWiki.Agents.csproj" />
    <ProjectReference Include="..\..\src\LlmWiki.Infrastructure\LlmWiki.Infrastructure.csproj" />
    <ProjectReference Include="..\..\src\LlmWiki.Shared\LlmWiki.Shared.csproj" />
  </ItemGroup>
```

### 12. `tests/LlmWiki.Agents.Tests/IngestionServiceTests.cs` (NEW)

```csharp
using LlmWiki.Agents.Ingestion;
using LlmWiki.Application.Ingestion;
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using LlmWiki.Infrastructure.FileStore;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Agents.Tests;

public sealed class IngestionServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "llmwiki-ingest-tests", Guid.NewGuid().ToString("N"));
    private readonly IWikiRepository _repo;

    public IngestionServiceTests()
    {
        var files = new FileSystemWikiFileStore(Options.Create(new WikiOptions { RootPath = _root }));
        _repo = new FileSystemWikiRepository(files);
    }

    [Fact]
    public async Task Ingest_WritesSummaryEntityAndTopicPages()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        var chat = new ScriptedChat(extraction: """
            {"sourceTitle":"Anvil Report","summary":"Acme builds anvils.","keyPoints":["Anvils are heavy"],
             "entities":[{"name":"Acme Corp","description":"An anvil maker.","thin":false}],
             "concepts":[],"tags":["anvils"],"topicTitle":"Anvils","topicSummary":"Overview of anvils."}
            """);
        var svc = new IngestionService(chat, _repo);

        var report = await svc.IngestAsync("alpha", "raw/anvils.md", "Acme builds heavy anvils.");

        Assert.False(report.HasFailures);
        Assert.Contains(report.Outcomes, o => o.RelativePath == "summaries/anvil-report.md");
        Assert.Contains(report.Outcomes, o => o.RelativePath == "entities/acme-corp.md" && o.Change == PageChange.Created);
        Assert.Contains(report.Outcomes, o => o.RelativePath == "topics/anvils.md");

        var entity = await _repo.ReadPageAsync("alpha", "entities/acme-corp.md");
        Assert.Equal(PageType.Entity, entity.Type);
        Assert.Contains("raw/anvils.md", entity.Sources);
    }

    [Fact]
    public async Task Ingest_ThinItem_IsFlaggedAsGapAndStub()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        var chat = new ScriptedChat(extraction: """
            {"sourceTitle":"S","summary":"x","keyPoints":[],
             "entities":[{"name":"Wile E Coyote","description":"Mentioned once.","thin":true}],
             "concepts":[],"tags":[],"topicTitle":"","topicSummary":""}
            """);
        var svc = new IngestionService(chat, _repo);

        var report = await svc.IngestAsync("alpha", "raw/s.md", "...Wile E Coyote...");

        Assert.Contains(report.Gaps, g => g.Subject == "Wile E Coyote");
        Assert.Contains(report.Outcomes, o => o.RelativePath == "entities/wile-e-coyote.md" && o.Change == PageChange.StubCreated);
    }

    [Fact]
    public async Task Ingest_ContradictionWithExistingPage_IsNotedNotOverwritten()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        await _repo.WritePageAsync("alpha", "entities/acme-corp.md", new WikiPage
        { Title = "Acme Corp", Type = PageType.Entity, Content = "Acme was founded in 1990." });

        var chat = new ScriptedChat(
            extraction: """
                {"sourceTitle":"S2","summary":"Acme was founded in 1888.","keyPoints":[],
                 "entities":[{"name":"Acme Corp","description":"Founded 1888.","thin":false}],
                 "concepts":[],"tags":[],"topicTitle":"","topicSummary":""}
                """,
            reconcile: """
                {"contradictions":[{"page":"entities/acme-corp.md","description":"Source says 1888; page says 1990."}]}
                """);
        var svc = new IngestionService(chat, _repo);

        var report = await svc.IngestAsync("alpha", "raw/s2.md", "Acme was founded in 1888.");

        Assert.Contains(report.Contradictions, c => c.PageRelativePath == "entities/acme-corp.md");
        var page = await _repo.ReadPageAsync("alpha", "entities/acme-corp.md");
        Assert.Contains("1990", page.Content);              // original retained
        Assert.Contains("Contradiction noted", page.Content); // discrepancy appended
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Fake chat: returns the extraction script first, then the reconcile script.</summary>
    private sealed class ScriptedChat(string extraction, string? reconcile = null) : IChatService
    {
        private int _call;
        public Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
            => Task.FromResult(_call++ == 0 ? extraction : reconcile ?? "{\"contradictions\":[]}");
    }
}
```

### 13. `tests/LlmWiki.Domain.Tests/SlugTests.cs` (NEW)

```csharp
using LlmWiki.Domain;

namespace LlmWiki.Domain.Tests;

public class SlugTests
{
    [Theory]
    [InlineData("Acme Corp", "acme-corp")]
    [InlineData("  Wile E. Coyote ", "wile-e-coyote")]
    [InlineData("C# & .NET", "c-net")]
    public void From_ProducesFilesystemSafeSlug(string input, string expected)
        => Assert.Equal(expected, Slug.From(input));
}
```

## Verification

1. **Unit tests (no infra needed):** `dotnet test tests/LlmWiki.Agents.Tests/LlmWiki.Agents.Tests.csproj`
   and `dotnet test tests/LlmWiki.Domain.Tests/LlmWiki.Domain.Tests.csproj` — covers summary/entity/topic
   creation, stub+gap flagging, and contradiction-noting via a scripted fake chat. Then `dotnet build LlmWiki.slnx`.
2. **End-to-end CLI (real LLM):** ensure `docker compose up -d` (Ollama) and a chat key in `env/.env`, then:
   ```bash
   dotnet run --project src/LlmWiki.Cli -- wiki create demo
   dotnet run --project src/LlmWiki.Cli -- ingest demo ./docs/sample-source.md
   ```
   Confirm the printed outcomes list a `summaries/…`, one or more `entities/…`, and a `topics/…` page.
3. **Inspect results on disk:** `dotnet run --project src/LlmWiki.Cli -- wiki inspect demo` lists the new
   pages; `wiki page show demo topics/<slug>.md` renders the overview with resolvable cross-references
   (links resolve via the Phase 1 resolver). Open a summary page and spot-check no fabricated facts (R-01).
4. **Immutability (NFR-02):** verify `wiki/demo/raw/sample-source.md` is byte-identical to the input and a
   re-ingest does not modify it.
5. **Contradiction path:** ingest a second source that conflicts with an existing entity; confirm the entity
   page gains a `> **Contradiction noted:**` block and the original text is retained, and the CLI prints the
   contradiction.

## Out of Scope (later phases)

- `index.md` / `log.md` maintenance and failure logging — **Phase 3** (consumes `IngestionReport`).
- Embedding created/updated pages, vector + Oracle Text indexes, hybrid ranking — **Phase 4** (BR-030…035).
- Interactive ingestion confirmation (BR-017) — **Phase 8** UI.
- Replacing the orchestrator internals with the SK Process Framework — optional later refactor behind the
  unchanged `IIngestionService` port.
```
