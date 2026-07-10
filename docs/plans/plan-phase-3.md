# Phase 3 — Index & Log

## Context

Phase 2 made the wiki **grow from sources**: `IngestionService` (in `LlmWiki.Agents`) reads a source,
writes a summary page, creates/updates entity & concept pages, revises a topic overview, notes
contradictions, and returns a structured `IngestionReport` (`src/LlmWiki.Application/Ingestion/IngestionReport.cs`).
Today that report is only printed by the CLI — its doc comment already says *"Phase 3 will turn it into
index/log updates."*

Phase 3 delivers **BR-020…BR-024**: two agent-owned files per wiki, written at the wiki root alongside
`SCHEMA.md`:

- **`index.md`** — a content catalogue of every page, grouped by category (Sources, Entities, Concepts,
  Overviews), each entry a link + one-line summary + optional metadata (created date, source count).
- **`log.md`** — an append-only chronological record; each entry headed `## [YYYY-MM-DD] ingest | <title>`
  so it is greppable (`grep "^## \[" log.md`).

Every ingestion updates the index and appends to the log as its **final steps** (BR-022), including
recording a partial/failed ingestion in the log. Reading the index into query context and the log into
session context (BR-023) is a **read-side** concern that belongs to Phase 5 (query) / Phase 8 (UI); Phase 3
builds only the write side plus the storage convention.

**Confirmed scope decisions:** core generation only — no new CLI view commands (viewing stays via
`wiki inspect` / opening the files); `index.md` is **deterministic** — regenerated from disk each ingest,
stably sorted, with **no** generated-at timestamp, so git diffs stay clean and BR-024 (deleted page →
stale entry vanishes) falls out for free.

## Design

Follows the established layering: pure renderers in **Domain** (like `Slug` / `CrossReferenceWriter`), a
**port** in Application, a **file-store adapter** in Infrastructure, wired into `IngestionService` in Agents.

```
IngestionService (Agents)
   ├─ …existing extract / write pages / reconcile…
   └─ FINAL STEPS  ──►  IWikiJournal (Application port)
                              │  FileSystemWikiJournal (Infrastructure)
                              ├─ RebuildIndexAsync → reads every page, renders index.md (Domain.IndexRenderer)
                              └─ AppendLogAsync    → appends one entry to log.md (Domain.LogFormatter)
```

- **Index is regenerated, not patched.** `RebuildIndexAsync` lists current pages, reads each for
  frontmatter (title, type, created, source count) + a derived one-line summary, and rewrites `index.md`
  from scratch. A deleted page is simply absent from the rebuild → its entry disappears (BR-024) with no
  special delete path.
- **Log is append-only.** `AppendLogAsync` reads the existing `log.md` (creating it with a `# Log` header
  if absent) and appends the formatted entry. Always appending yields chronological order.
- **`index.md` / `log.md` are agent-owned, not content pages.** `IsPage` in `FileSystemWikiRepository` must
  exclude them so they are not counted in `PageCount`, listed by `ListPagesAsync`, included in the catalogue
  itself, or scanned by link resolution.
- **Resilience (NFR-06).** The journal step is wrapped in `IngestionService` so a journal failure is
  recorded as a `PageChange.Failed` outcome rather than throwing — the wiki is never left corrupt, and the
  contradiction/gap work already done still returns.

## New / Updated Files

### Domain (pure, no deps)

**`src/LlmWiki.Domain/IndexEntry.cs`** (NEW) — input record for the renderer:
```csharp
namespace LlmWiki.Domain;

/// <summary>One page as it appears in the index catalogue (BR-020).</summary>
public sealed record IndexEntry(
    string RelativePath,
    string Title,
    PageType Type,
    string Summary,
    DateTimeOffset Created,
    int SourceCount);
```

**`src/LlmWiki.Domain/IndexRenderer.cs`** (NEW) — pure catalogue renderer:
- `Render(IReadOnlyList<IndexEntry> entries, LinkStyle style)` → full `index.md` body.
- Title `# Index`, then fixed-order sections for the four categories, rendered **only when non-empty**:
  `Summary`→**Sources**, `Entity`→**Entities**, `Concept`→**Concepts**, `Overview`→**Overviews**.
- Within a section, entries sorted ordinally by path for determinism; each line:
  `- {link} — {summary} ({created:yyyy-MM-dd}; {n} source[s])`.
- Reuse `CrossReferenceWriter.Link(title, relativePath, "index.md", style)` for the link so it honours the
  wiki's `LinkStyle` (wikilink `[[Title]]`; markdown link resolves relative to the root `index.md`).

**`src/LlmWiki.Domain/LogEntry.cs`** (NEW) — record + pure formatter (one file):
```csharp
namespace LlmWiki.Domain;

/// <summary>One append-only log record (BR-021). Header is greppable: "## [YYYY-MM-DD] type | Description".</summary>
public sealed record LogEntry(DateOnly Date, string Type, string Description, string? Body = null);

public static class LogFormatter
{
    public static string Format(LogEntry e) => /* "## [yyyy-MM-dd] {type} | {description}" + optional body */;
}
```

### Application (port)

**`src/LlmWiki.Application/Ports/IWikiJournal.cs`** (NEW) — the write-side journal port:
```csharp
using LlmWiki.Domain;

namespace LlmWiki.Application.Ports;

/// <summary>Maintains a wiki's agent-owned index.md (regenerated) and log.md (append-only) — BR-020…024.</summary>
public interface IWikiJournal
{
    Task RebuildIndexAsync(string wikiName, CancellationToken cancellationToken = default);
    Task AppendLogAsync(string wikiName, LogEntry entry, CancellationToken cancellationToken = default);
}
```
Two write methods only — the BR-023 read path (index→query, log→session) is Phase 5/8.

### Infrastructure

**`src/LlmWiki.Infrastructure/FileStore/FileSystemWikiJournal.cs`** (NEW) —
`FileSystemWikiJournal(IWikiRepository wiki, IWikiFileStore files) : IWikiJournal`:
- `RebuildIndexAsync`: `wiki.ListPagesAsync` → for each, `wiki.ReadPageAsync` → build `IndexEntry`
  (`Summary` = first non-empty, non-`#`/`>` line of `Content`, trimmed/capped; `SourceCount = Sources.Count`)
  → `IndexRenderer.Render(entries, schema.LinkStyle)` → `files.WriteAsync("{wiki}/index.md", ...)`.
- `AppendLogAsync`: read `{wiki}/log.md` via `files.ReadAsync` (seed `# Log\n` if `!files.ExistsAsync`),
  append `LogFormatter.Format(entry)`, write back. Plain markdown via `IWikiFileStore` (no frontmatter).

**`src/LlmWiki.Infrastructure/FileStore/FileSystemWikiRepository.cs`** (UPDATE) — extend `IsPage`
(line 127) to also exclude root `index.md` and `log.md`:
```csharp
private const string IndexFile = "index.md";
private const string LogFile = "log.md";
// …in IsPage: && !path.EndsWith($"/{IndexFile}", StringComparison.Ordinal)
//             && !path.EndsWith($"/{LogFile}",   StringComparison.Ordinal)
```

**`src/LlmWiki.Infrastructure/DependencyInjection.cs`** (UPDATE) — register the journal in
`AddLlmWikiInfrastructure`: `services.AddSingleton<IWikiJournal, FileSystemWikiJournal>();`.

### Agents

**`src/LlmWiki.Agents/Ingestion/IngestionService.cs`** (UPDATE):
- Add `IWikiJournal journal` to the primary constructor (`IChatService chat, IWikiRepository wiki, IWikiJournal journal`).
- Replace the final `return` (line 62-64) with journal finalisation **before** returning:
  1. Build a `LogEntry` from the report — `Type = "ingest"`, `Description = <source title/path>`,
     `Body` = a bullet summary of counts (created / updated / stub / **failed**) + contradictions + gaps
     (satisfies BR-022 failure recording). Add a private `BuildLogEntry(IngestionReport report)`.
  2. `try { await journal.RebuildIndexAsync(wikiName, ct); await journal.AppendLogAsync(wikiName, entry, ct); }`
     `catch (Exception ex) { outcomes.Add(new PageOutcome("index.md", "Index/Log", PageChange.Failed, ex.Message)); }`
     (NFR-06 — journal failure is recorded, not fatal).
- Order: index rebuilt from the freshly-written pages, then log appended describing the run.

### Tests (xUnit; real temp-dir filesystem fixtures, hand-rolled fakes — matching existing style)

- **`tests/LlmWiki.Domain.Tests/IndexRendererTests.cs`** (NEW) — grouping into the four categories in fixed
  order, empty sections omitted, deterministic ordering, correct link per `LinkStyle`, `; N sources`
  pluralisation.
- **`tests/LlmWiki.Domain.Tests/LogFormatterTests.cs`** (NEW) — header matches `^## \[\d{4}-\d{2}-\d{2}\] ingest \| ` and body renders.
- **`tests/LlmWiki.Infrastructure.Tests/FileSystemWikiJournalTests.cs`** (NEW) — over a temp `WikiOptions.RootPath`
  with real `FileSystemWikiFileStore` + `FileSystemWikiRepository`: (a) rebuild after writing pages →
  `index.md` lists them and is excluded from `ListPagesAsync`; (b) **delete a page then rebuild → stale
  entry gone** (BR-024); (c) two `AppendLogAsync` calls → both headers present, chronological, greppable.
- **`tests/LlmWiki.Agents.Tests/IngestionServiceTests.cs`** (UPDATE) — the three existing tests construct
  `new IngestionService(chat, _repo)`; add a `FileSystemWikiJournal` field and pass it as the third arg.
  Add one assertion that after ingest `index.md` exists and lists the summary page, and `log.md` contains an
  `## [` ingest header.
- **`tests/LlmWiki.Agents.Tests/AgentsRegistrationTests.cs`** — no change expected (journal is registered in
  Infrastructure DI, which those tests already compose); verify it still resolves `IIngestionService`.

## Verification

1. **Unit tests (no infra):**
   `dotnet test tests/LlmWiki.Domain.Tests/LlmWiki.Domain.Tests.csproj`,
   `dotnet test tests/LlmWiki.Infrastructure.Tests/LlmWiki.Infrastructure.Tests.csproj`,
   `dotnet test tests/LlmWiki.Agents.Tests/LlmWiki.Agents.Tests.csproj`, then `dotnet build LlmWiki.slnx`.
2. **End-to-end CLI (real LLM — `docker compose up -d` + chat key in `env/.env`):**
   ```bash
   dotnet run --project src/LlmWiki.Cli -- wiki create demo
   dotnet run --project src/LlmWiki.Cli -- ingest demo ./docs/sample-source.md
   ```
   Then inspect on disk:
   - `wiki/demo/index.md` — has linked one-line entries for the new summary/entity/topic pages, grouped by
     category (BR-020). `wiki inspect demo` still lists content pages but **not** `index.md`/`log.md`.
   - `wiki/demo/log.md` — `grep "^## \[" wiki/demo/log.md` yields one clean `## [YYYY-MM-DD] ingest | …` line.
3. **Second ingest (shared entity):** ingest another source that mentions the same entity; confirm `index.md`
   entry count/updates reflect the merged page and a **second** log line appears, chronologically after the first.
4. **BR-024 (stale entry):** `rm` a page file under `wiki/demo/…`, re-ingest any source, confirm the deleted
   page's line is gone from the rebuilt `index.md` (no dead link).
5. **NFR-06 (failure recorded):** confirm a failing page write still yields a `log.md` entry whose body lists
   the failure, and the ingest completes without throwing.

## Out of Scope (later phases)

- Reading the index into query context / the log into session context (BR-023) — **Phase 5 / Phase 8**.
- Embedding created/updated pages + Oracle vector/Text indexes — **Phase 4**.
- CLI `wiki index` / `wiki log` view commands — deferred (confirmed core-only).
- Optional: documenting `index.md` / `log.md` in `SCHEMA.md` — small `SchemaSerializer` body tweak, can fold
  in if desired but not required by BR-020…024.
