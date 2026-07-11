# Plan — Phase 5: Query & Synthesis

## Context

Phases 0–4 gave us a buildable foundation, a file-backed wiki, an ingestion pipeline,
agent-owned `index.md`/`log.md`, and **hybrid retrieval** — every content page is embedded
in Oracle (`VECTOR` + Oracle Text) and reachable via `IVectorStore.SearchAsync` and the CLI
`search` command. Retrieval returns *pages*; nothing yet *answers a question*.

Phase 5 adds the **query/synthesis workflow** (BR-040…BR-045): read the index → hybrid
search → read the top candidate pages in full → synthesise a grounded, **cited** answer,
honestly reporting gaps when coverage is thin. It supports **multi-turn follow-ups** and can
**save a good answer back as a new wiki page** that is itself indexed, logged, and embedded —
the compounding loop the BRD is built around.

**Decisions locked with the user:**
- **Follow-ups via an interactive CLI REPL** — a new `ask` command opens a chat loop that
  keeps conversation history in-process and carries it into each synthesis call (BR-044).
- **Saved answers get a new `Answer` page type** — cleaner semantics than overloading
  `Overview`; touches `PageType`, frontmatter round-trip, the index renderer, and the CLI
  `--type` surface (all enumerated below).
- **CLI + API** — add the `ask` REPL *and* a non-streaming `POST /query` **controller**
  endpoint (a thin MVC `ControllerBase`, with **Swashbuckle Swagger UI** for interactive
  testing) so the workflow is HTTP-reachable before the React Native client (Phase 8) is built.
  This deliberately introduces MVC controllers + Swagger — patterns not used elsewhere yet (the
  API is minimal-API + OpenAPI-doc only today); `/health` and `/diagnostics` stay minimal-API,
  so the query surface is the first controller.

**Design principles held (unchanged from Phase 4):** SK/Oracle stay confined to
Infrastructure (NFR-07); the query orchestrator is a **plain port-based service** (no SK
types) so the SK Process Framework can later slot in behind `IQueryService`; synthesis is
**single-shot non-streaming** — `IChatService.CompleteAsync` returns `Task<string>` and no
streaming port exists (token streaming to the client is deferred to Phase 8, NFR-05/08).
Save-answer's embed step is **best-effort** — a failure is recorded, never corrupts the wiki
(NFR-06). Per-wiki isolation is already enforced by the `wiki_name` predicate in the vector
store (NFR-10).

---

## Key facts grounding the design

- **Retrieval is embed-then-search, already proven.** The `search` command does exactly
  `embeddings.EmbedAsync(query)` → `vectors.SearchAsync(wiki, query, embedding, topK, type)`
  ([Program.cs BuildSearchCommand](src/LlmWiki.Cli/Program.cs)). The query orchestrator
  reuses this verbatim, then hydrates each `VectorSearchHit.RelativePath` to full content via
  `IWikiRepository.ReadPageAsync` — `VectorSearchHit` carries `RelativePath/Title/Type/Score/
  Snippet` but not the body ([IVectorStore.cs](src/LlmWiki.Application/Ports/IVectorStore.cs)).
- **Mirror the ingestion orchestrator.** `IngestionService` is the template: primary-ctor DI
  over ports only, a single JSON-mode LLM call deserialized through an `ExtractJson`
  fence-stripper, per-page **write boundaries** that record outcomes instead of throwing, and
  a **best-effort embed step** identical to what save-answer needs
  ([IngestionService.cs:98-122](src/LlmWiki.Agents/Ingestion/IngestionService.cs#L98-L122)).
  Reuse its `ExtractJson` helper pattern and its `LogEntry` construction.
- **`index.md` has no read port.** `IWikiJournal` only *writes* the index/log; to feed the
  index into the query prompt (BR-040 "read the index"), read it directly via
  `IWikiFileStore.ReadAsync($"{wikiName}/index.md")`.
- **Journal round-trip for save-answer is one call each.** After writing the page,
  `journal.RebuildIndexAsync` regenerates `index.md` deterministically from disk (the new page
  appears automatically) and `journal.AppendLogAsync(wiki, new LogEntry(today, "query", …))`
  appends a greppable line — exactly IngestionService's final block.
- **New page types are cheap to read back.** `OracleVectorStore.ParseType` already uses
  case-insensitive `Enum.TryParse<PageType>` so a new `Answer` value round-trips through the
  store with no adapter change; `FrontmatterSerializer` lowercases on write. Only the index
  renderer's fixed section list must be extended by hand.
- **`answers/` needs no scaffolding change.** `FileSystemWikiFileStore.WriteAsync` creates
  parent directories (ingestion already writes `concepts/`, which BR-001 does not scaffold),
  and `IsPage` only excludes `SCHEMA.md`/`index.md`/`log.md`/`raw/` — so `answers/*.md` pages
  are automatically listed, catalogued, and embeddable.
- **Tests are hand-rolled fakes + temp dirs, no Moq.** `IngestionServiceTests` (`ScriptedChat`,
  `FakeEmbeddingService`, `RecordingVectorStore`, `ThrowingVectorStore`) and the
  `WebApplicationFactory<Program>` health test are the exact fixtures to copy.

---

## Files to change

### 1. `src/LlmWiki.Domain/PageType.cs` — add the `Answer` member

```csharp
public enum PageType { Summary, Entity, Concept, Overview, Answer }
```
No other Domain change: `FrontmatterSerializer` writes `type.ToString().ToLower()` → `answer`
and reads back via case-insensitive parse; `OracleVectorStore.ParseType` already tolerant.

### 2. `src/LlmWiki.Domain/IndexRenderer.cs` — an "Answers" section

Extend the fixed `Sections` list so saved answers are catalogued under their own heading
(BR-020 organises by category). Add `(PageType.Answer, "Answers")` after `Overview`; the
stable path-sort and grouping logic is unchanged. Add a Domain test (below).

### 3. `src/LlmWiki.Application/Query/IQueryService.cs` (NEW) — the port

```csharp
namespace LlmWiki.Application.Query;

public interface IQueryService
{
    /// <summary>Read index → hybrid search → read candidates → synthesise a cited answer (BR-040…BR-043).
    /// <paramref name="history"/> carries prior turns for follow-ups (BR-044).</summary>
    Task<QueryResult> AnswerAsync(
        string wikiName, string question,
        IReadOnlyList<ConversationTurn> history, QueryOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Persist a produced answer as an Answer page, then index + log + embed it (BR-045).</summary>
    Task<PageOutcome> SaveAnswerAsync(
        string wikiName, QueryResult result, CancellationToken cancellationToken = default);
}
```

### 4. `src/LlmWiki.Application/Query/QueryResult.cs` (NEW) — DTOs

Mirror `IngestionReport.cs` (records, non-null defaults). `PageOutcome` is reused from
`Application.Ingestion`.

```csharp
public sealed record QueryOptions(int TopK = 5, PageType? TypeFilter = null);

public sealed record ConversationTurn(string Question, string Answer);

/// <summary>A page the answer drew from, resolvable to an openable file (BR-041).</summary>
public sealed record Citation(string RelativePath, string Title, PageType Type);

public sealed record QueryResult(
    string WikiName,
    string Question,
    string Answer,               // markdown; format chosen by the model (BR-043)
    bool Covered,                // false => honest gap report (BR-042)
    IReadOnlyList<Citation> Citations,
    string SuggestedTitle);      // used as the slug when saved (BR-045)
```

### 5. `src/LlmWiki.Application/Query/SynthesisResult.cs` (NEW) — LLM-facing DTO

The JSON shape the model returns, `[JsonPropertyName]`-annotated with non-null defaults so a
partial reply still deserializes — exactly `ExtractionResult`'s pattern.

```csharp
public sealed record SynthesisResult
{
    [JsonPropertyName("title")]     public string Title { get; init; } = string.Empty;
    [JsonPropertyName("answer")]    public string Answer { get; init; } = string.Empty;
    [JsonPropertyName("covered")]   public bool Covered { get; init; }
    [JsonPropertyName("citations")] public IReadOnlyList<string> Citations { get; init; } = []; // relative paths
}
```

### 6. `src/LlmWiki.Agents/Prompts/QueryPrompts.cs` (NEW)

Sibling to `IngestionPrompts` — `internal static class`, raw `$$"""…"""` literals,
schema-parameterised, **grounded to the provided context** (the R-01 constraint). One method:

```csharp
internal static class QueryPrompts
{
    public static string Synthesize(
        WikiSchema schema, string indexMarkdown, string question,
        string retrievedContext, IReadOnlyList<ConversationTurn> history) => $$"""
        You answer questions about the wiki "{{schema.WikiName}}" using ONLY the CONTEXT below.
        - Cite the specific pages you use by their relative path (e.g. entities/acme.md).
        - If the CONTEXT does not cover the question, set "covered": false and say so plainly —
          do NOT speculate (honest-gap requirement).
        - Choose the format that fits: prose, a markdown table for comparisons, a list for timelines.
        Return one JSON object, no fence: {"title":"…","answer":"…","covered":true,"citations":["…"]}

        WIKI INDEX:
        {{indexMarkdown}}

        {{FormatHistory(history)}}
        QUESTION: {{question}}

        CONTEXT:
        {{retrievedContext}}
        """;
}
```
`retrievedContext` is built by the orchestrator from the hydrated candidate pages
(`### {relativePath} — {title}\n{content}`); `FormatHistory` renders prior Q/A turns (empty
for a first question).

### 7. `src/LlmWiki.Agents/Query/QueryService.cs` (NEW) — the orchestrator

Plain port-based service (no SK), primary-ctor DI, mirroring `IngestionService`.

```csharp
public sealed class QueryService(
    IChatService chat,
    IWikiRepository wiki,
    IWikiFileStore files,          // to read index.md (no journal read port)
    IVectorStore vectors,
    IEmbeddingService embeddings,
    IWikiJournal journal,
    IOptions<EmbeddingOptions> embedOptions) : IQueryService
{
    // AnswerAsync:
    //  1. index = await files.ReadAsync($"{wikiName}/index.md", ct)   (best-effort; "" if absent)
    //  2. schema = await wiki.ReadSchemaAsync(wikiName, ct)
    //  3. emb = await embeddings.EmbedAsync(question, ct)
    //  4. hits = await vectors.SearchAsync(wikiName, question, emb, options.TopK, options.TypeFilter, ct)
    //  5. hydrate each hit via wiki.ReadPageAsync → build retrievedContext block
    //  6. raw = await chat.CompleteAsync(QueryPrompts.Synthesize(...), jsonMode: true, ct)
    //     result = Deserialize<SynthesisResult>(ExtractJson(raw))   // reuse the fence-stripper
    //  7. map citations: keep only paths that were actually in `hits` (resolvable, BR-041),
    //     attach Title/Type from the hit; return QueryResult (Covered from the model).

    // SaveAnswerAsync (BR-045):
    //  path = $"answers/{Slug.From(result.SuggestedTitle)}.md"
    //  page = new WikiPage { Title = SuggestedTitle, Type = PageType.Answer,
    //                        Content = result.Answer, Sources = citation paths }
    //  await wiki.WritePageAsync(...);                         // write boundary → PageOutcome
    //  await journal.RebuildIndexAsync + AppendLogAsync(new LogEntry(today,"query",question,body));
    //  best-effort embed: EmbeddingText.For(page,_strategy) → EmbedAsync → vectors.UpsertAsync
    //     (failure recorded on the outcome detail, never thrown — NFR-06)
}
```

### 8. `src/LlmWiki.Agents/DependencyInjection.cs` — register the service

```csharp
services.AddSingleton<IQueryService, QueryService>();
```

### 9. `src/LlmWiki.Cli/Program.cs` — the `ask` REPL command

New `BuildAskCommand()`, registered via `root.Subcommands.Add(BuildAskCommand())`.
Args/opts mirror `search`: `Argument<string> wiki`, optional `Argument<string>? question`
(if supplied, answer once and exit; if omitted, enter the REPL), `--top-k` (default 5),
`--type` (`Option<PageType?>`). Builds **one** provider, resolves `IQueryService` + repo,
guards `WikiExistsAsync`, then loops:

```
$ llm-wiki ask demo
> <question>            → AnswerAsync(history); print answer, then "Sources:" citation list;
                          append (question, answer) to in-process history (BR-044)
> :save                 → SaveAnswerAsync(lastResult); print the created answers/*.md path
> :quit                 → exit
```
Uncovered answers print a clear "⚠ not covered by this wiki" banner (BR-042). Reuse the
existing column-aligned `Console.WriteLine` style; `--type` help text lists
`entity|concept|summary|overview|answer`.

### 10. `src/LlmWiki.Api/Controllers/QueryController.cs` (NEW) — `POST /query` + Swagger

A **thin MVC controller** (`[ApiController]`) that constructor-injects the ports and delegates
straight to `IQueryService` — no logic in the controller (NFR-07: it's a composition-root shim
over the port). Non-streaming JSON, same behaviour as the original minimal-API sketch (404 for
a missing wiki, 200 + `QueryResult`, optional best-effort save when covered):

```csharp
[ApiController]
[Route("query")]
public sealed class QueryController(IQueryService svc, IWikiRepository repo) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Post(QueryRequest req, CancellationToken ct)
    {
        if (!await repo.WikiExistsAsync(req.Wiki, ct)) return NotFound();
        var result = await svc.AnswerAsync(req.Wiki, req.Question, req.History ?? [],
                                           new QueryOptions(req.TopK ?? 5, req.Type), ct);
        if (req.Save == true && result.Covered) await svc.SaveAnswerAsync(req.Wiki, result, ct);
        return Ok(result);
    }
}

public record QueryRequest(string Wiki, string Question, IReadOnlyList<ConversationTurn>? History,
                           int? TopK, PageType? Type, bool? Save);
```

History in the request body carries follow-up context over HTTP (BR-044); `QueryResult` is the
response shape (citations already resolvable, BR-041).

**`src/LlmWiki.Api/Program.cs` — enable MVC + Swagger UI (light touch).** Add
`builder.Services.AddControllers();` and `builder.Services.AddSwaggerGen();`, then in the
existing `IsDevelopment()` block `app.UseSwagger(); app.UseSwaggerUI();`, and
`app.MapControllers();` before `app.Run()`. The existing `/health` and `/diagnostics` stay as
minimal-API `MapGet` calls (deliberate mix); `AddOpenApi`/`MapOpenApi` remain untouched so
those endpoints' docs are unaffected.

**`src/LlmWiki.Api/LlmWiki.Api.csproj`** — add `<PackageReference Include="Swashbuckle.AspNetCore" />`
(no `Version=` — pinned centrally, see §14). MVC controllers need no extra package in the Web SDK.

### 11. `src/LlmWiki.Infrastructure/FileStore/SchemaRenderer` + wiki scaffolding (light touch)

Document the `answers/` directory and the `answer` page type in the generated `SCHEMA.md`
(BR-002 records conventions). No new directory-creation code is required — `WriteAsync`
mkdirs on first save — but add `answers/` to the schema's directory listing for discoverability.

### 12. Tests

- **`tests/LlmWiki.Agents.Tests/QueryServiceTests.cs`** (NEW): temp-dir repo seeded with two
  real pages; `ScriptedChat` returns a canned `SynthesisResult` JSON; `FakeEmbeddingService`
  (768-length); `FakeVectorStore.SearchAsync` returns hits for the seeded paths. Assert:
  (a) the answer's citations resolve to seeded page paths (BR-041); (b) a `covered:false`
  script yields `QueryResult.Covered == false` (BR-042); (c) `SaveAnswerAsync` writes
  `answers/<slug>.md` with `type: answer`, appends a `## [date] query | …` log line, and
  upserts to a `RecordingVectorStore`; (d) with a `ThrowingVectorStore` the save still
  succeeds and records the embed failure (NFR-06).
- **`tests/LlmWiki.Api.Tests/QueryEndpointTests.cs`** (NEW): `WebApplicationFactory<Program>`
  hosts the controller exactly as it hosts the existing minimal APIs; with `IQueryService`
  **overridden by a fake** returning a canned result (keeps it hermetic — no Oracle/LLM). Assert
  `POST /query` → 200 + expected JSON for a known wiki, and 404 for a missing wiki.
- **`tests/LlmWiki.Domain.Tests/IndexRendererTests.cs`** (UPDATE): assert an `Answer`-typed
  page renders under the new "Answers" heading.
- **`AgentsRegistrationTests`**: `IQueryService` resolves from the provider.

### 13. `Directory.Packages.props` — pin Swashbuckle

Add a pinned `<PackageVersion Include="Swashbuckle.AspNetCore" Version="…" />` under the
"Web API" group (next to `Microsoft.AspNetCore.OpenApi`), version confirmed against nuget.org
for a `net10.0`-compatible release. Required — central package management forbids a bare version
in the csproj (§10).

---

## Requirements covered

BR-040 (index → hybrid search → read candidates → synthesise), BR-041 (resolvable page
citations), BR-042 (honest gap via `Covered=false`), BR-043 (model-chosen format), BR-044
(follow-ups via REPL/HTTP conversation history), BR-045 (save answer → write + index + log +
embed). NFR-07 (SK/Oracle stay in Infrastructure; orchestrator is port-only), NFR-06
(best-effort save-embed never corrupts the wiki), NFR-10 (per-wiki isolation inherited from
the vector store's `wiki_name` predicate).

---

## Verification (end-to-end)

1. **Build & unit test:** `dotnet build LlmWiki.slnx` && `dotnet test LlmWiki.slnx` — new
   `QueryServiceTests`, `QueryEndpointTests`, and the renderer test are green; nothing else
   regresses.
2. **Infra + data:** `cd docker && docker compose up -d`; ensure `env/.env` has
   `ORACLE_CONNECTION_STRING` and a working `CHAT_PROVIDER` (keyless: `ollama` + `llama3.1`).
   `dotnet run --project src/LlmWiki.Cli -- wiki create demo` then
   `... -- ingest demo ./docs/sample-source.md` so there are embedded pages to answer from.
3. **Answer + citations (BR-040/041/043):**
   `dotnet run --project src/LlmWiki.Cli -- ask demo "how does the thing work?"` →
   a synthesised markdown answer followed by a `Sources:` list whose paths open to real pages;
   a comparison question yields a table.
4. **Honest gap (BR-042):** ask something the corpus does not cover → the "⚠ not covered"
   banner, no fabricated facts.
5. **Follow-ups (BR-044):** run `ask demo` with no question to enter the REPL; ask a question,
   then a pronoun-only follow-up ("and how does it relate to X?") → context is maintained.
6. **Save-answer (BR-045):** in the REPL type `:save` → an `answers/<slug>.md` appears on disk
   with `type: answer`; `grep "^## \[" .../log.md` shows a new `query` line; `index.md` lists
   it under **Answers**; re-run `search demo "<the saved title>"` → the new page is returned
   (proving it was embedded).
7. **API (BR-040/041/044):** `dotnet run --project src/LlmWiki.Api` then either browse
   **`http://localhost:5080/swagger`** and invoke `POST /query` interactively, or
   `curl -s localhost:5080/query -H 'content-type: application/json' -d '{"wiki":"demo","question":"…"}'`
   → JSON `{answer, covered, citations[…], suggestedTitle}`; a bad wiki name → 404.
8. **Isolation (NFR-10):** create a second wiki, ingest a different source, `ask` each — neither
   answer cites the other's pages.

---

## Out of scope (later phases)

Token **streaming** to the client (Phase 8, needs a new streaming chat port + SSE); the React
Native chat UI, clickable citation rendering, and the wiki-tree browser (Phase 8); lint/health
checks (Phase 7); Oracle-backed **project** metadata and multi-project management (Phase 6).
