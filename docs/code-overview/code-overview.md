# Code Overview

A plain-English tour of the LLM Wiki codebase for developers who are new to it but comfortable
with C#/.NET and clean architecture. It explains **what the system does, how the pieces fit,
and how the main flows work end to end** — enough to find your way around and make a change with
confidence. For the "why" behind individual phases, see the [plans](../plans/) and the
[ADR](../adr/0001-phase-0-foundations.md).

---

## 1. The big idea

The product is a **wiki that grows itself from source documents**. You drop a document into a
wiki, and an LLM agent reads it, extracts the entities/concepts/topics, writes and cross-links
markdown pages, keeps a catalogue and a changelog, and makes everything searchable by meaning and
by keyword. You can then **ask it questions** and get a grounded, cited answer synthesised from the
pages it retrieved — and save that answer back as a new page, so the wiki compounds on itself.

Two design commitments shape everything:

1. **Markdown files on disk are the source of truth.** A wiki is just a directory of `.md` files
   with YAML frontmatter. You can read it, `grep` it, and diff it in git without the app running.
2. **Oracle is a *derived* search index, not the primary store.** Phase 4 embeds each page into an
   Oracle table (`wiki_page`) for hybrid search. If Oracle is down, ingestion still writes the
   files — the database can always be rebuilt from disk.

Everything else follows from those two ideas.

---

## 2. Architecture at a glance

The solution is **clean / layered**, and **dependencies point inward** — inner layers never know
about outer ones. Semantic Kernel (the LLM SDK) and Oracle are confined to the two outer
implementation layers; the domain and application layers stay pure and testable.

```
Domain  ←  Application (ports)  ←  Infrastructure (adapters: Oracle, Ollama, OpenAI/Anthropic)
                  ↑                          ↑
                Agents                     Shared (config + .env loading)
                  ↑                          ↑
                      Api / Cli  (composition roots)
```

| Project | Role | May depend on |
|---|---|---|
| **`LlmWiki.Domain`** | Pure business types and pure functions (entities + renderers). No I/O, no framework. | nothing |
| **`LlmWiki.Application`** | **Ports** (interfaces the app needs) + orchestration contracts/DTOs. Defines *what* is needed, not *how*. | Domain |
| **`LlmWiki.Infrastructure`** | **Adapters** — the real implementations of every port (Oracle, Ollama, OpenAI/Anthropic, file store). Owns *all* Semantic Kernel + Oracle wiring. | Application, Shared |
| **`LlmWiki.Agents`** | LLM agent orchestration. Today: the ingestion pipeline, the backfill indexer, the query/synthesis service, and the lint/health-check service — all written against ports only (no SK types) so they stay unit-testable. | Application, Shared |
| **`LlmWiki.Shared`** | Cross-cutting config: `env/.env` loading + strongly-typed options. A leaf with no project deps. | nothing |
| **`LlmWiki.Api`** | ASP.NET host — minimal APIs (`/health`, `/diagnostics`) **plus** the Phase 5 `POST /query`, Phase 6 `GET`·`POST /projects`, and Phase 7 `POST /lint`·`/lint/apply` MVC controllers and Swagger UI. A thin **composition root**. | Infrastructure, Agents, Shared |
| **`LlmWiki.Cli`** | Command-line host (`doctor`, `wiki`, `ingest`, `search`, `reindex`, `ask`, `project`, `lint`). The other composition root. | Infrastructure, Agents, Shared |

**Why this matters when you edit code:** if you find yourself wanting to `using Oracle.…` or
`using Microsoft.SemanticKernel` in Domain, Application, or (for SK) Agents, stop — that's a smell.
Those dependencies belong behind a port in Infrastructure. The layering is enforced only by
project references and code review, so keep it honest.

### Ports & adapters, concretely

A **port** is an interface in `LlmWiki.Application/Ports/`. An **adapter** is its implementation in
`LlmWiki.Infrastructure/`. The composition roots (`Api`/`Cli`) wire port → adapter in DI, so the
orchestration code only ever sees interfaces.

| Port | Adapter | What it does |
|---|---|---|
| [`IWikiFileStore`](../../src/LlmWiki.Application/Ports/IWikiFileStore.cs) | `FileSystemWikiFileStore` | Raw read/write/list of files under `WIKI_ROOT`. |
| [`IWikiRepository`](../../src/LlmWiki.Application/Ports/IWikiRepository.cs) | `FileSystemWikiRepository` | Wiki-aware operations: scaffold wikis, read/write pages with frontmatter, list, resolve links. |
| [`IWikiJournal`](../../src/LlmWiki.Application/Ports/IWikiJournal.cs) | `FileSystemWikiJournal` | Maintains `index.md` (regenerated) and `log.md` (append-only). |
| [`IChatService`](../../src/LlmWiki.Application/Ports/IChatService.cs) | `SemanticKernelChatService` / `NotConfiguredChatService` | One-shot LLM completion, optional JSON mode. |
| [`IEmbeddingService`](../../src/LlmWiki.Application/Ports/IEmbeddingService.cs) | `OllamaEmbeddingService` | Turn text into a 768-dim vector. |
| [`IVectorStore`](../../src/LlmWiki.Application/Ports/IVectorStore.cs) | `OracleVectorStore` | Upsert page embeddings + hybrid (vector + full-text) search in Oracle. |
| [`IDatabaseHealthCheck`](../../src/LlmWiki.Application/Ports/IDatabaseHealthCheck.cs) | `OracleDatabaseHealthCheck` | Connectivity probe (CREATE TABLE round-trip). |
| [`IProjectRepository`](../../src/LlmWiki.Application/Ports/IProjectRepository.cs) | `OracleProjectRepository` | Project registry: register/list/get projects + record ingest metadata in Oracle `wiki_project` (Phase 6). |
| [`ICurrentProjectStore`](../../src/LlmWiki.Application/Ports/ICurrentProjectStore.cs) | `FileCurrentProjectStore` | Host-local active-project pointer (`{WIKI_ROOT}/.current-project`) — offline, no Oracle (Phase 6). |
| [`IIngestionService`](../../src/LlmWiki.Application/Ingestion/IIngestionService.cs) | `IngestionService` (Agents) | The whole ingest pipeline. |
| [`IWikiIndexer`](../../src/LlmWiki.Application/Indexing/IWikiIndexer.cs) | `WikiIndexer` (Agents) | Backfill: embed every existing page of a wiki into the vector store. |
| [`IQueryService`](../../src/LlmWiki.Application/Query/IQueryService.cs) | `QueryService` (Agents) | Query/synthesis: index → hybrid search → read candidates → cited answer; save an answer back as a page. |
| [`ILintService`](../../src/LlmWiki.Application/Linting/ILintService.cs) | `LintService` (Agents) | Lint/health-check: structural pass (broken links, orphans, thin pages) + one LLM call (contradictions, stale claims, suggestions) → prioritised report; apply a stub-creation fix (Phase 7). |

---

## 3. The storage model

### A wiki on disk

Under `WIKI_ROOT` (default `wiki/`), each wiki is a directory:

```
wiki/
  .current-project        # host-local active-project pointer (one line)          ── Phase 6
  demo/
    SCHEMA.md              # the wiki's conventions (link style + frontmatter fields)
    index.md              # agent-owned catalogue (regenerated every ingest)     ── Phase 3
    log.md                # agent-owned append-only changelog                    ── Phase 3
    summaries/            # one page per ingested source (PageType.Summary)
    entities/             # people/orgs/things (PageType.Entity)
    topics/              # overarching topic overviews (PageType.Overview)
    answers/             # saved query answers (PageType.Answer), created on demand   ── Phase 5
    raw/                 # immutable copies of the original sources (write-once, NFR-02)
```

- The four typed directories (`summaries`, `entities`, `topics`, `raw`) are fixed for every wiki —
  see [`WikiSchema.Directories`](../../src/LlmWiki.Domain/WikiSchema.cs). (`concepts/` for concept
  pages and `answers/` for saved answers are created on demand — `WriteAsync` mkdirs on first use —
  and `answers/` is documented in the generated `SCHEMA.md`.)
- **`SCHEMA.md`** records the two per-wiki toggles: the **link style** (`[[Wikilink]]` vs.
  `[text](path.md)`) and the frontmatter field set. It's how `wiki create` and `wiki inspect` know
  a directory is a real wiki.
- Pages are markdown with **YAML frontmatter** (`title`, `type`, `created`, `updated`, `tags`,
  `sources`), serialized by `FrontmatterSerializer` (YamlDotNet).
- **`raw/` is immutable.** Sources are copied in once and never modified; the CLI won't overwrite an
  existing raw file.
- **`index.md` / `log.md` are agent-owned, not content pages.** `FileSystemWikiRepository.IsPage`
  excludes them (and `SCHEMA.md`, and anything under `raw/`), so they're never counted in
  `PageCount`, listed by `ListPagesAsync`, catalogued into the index itself, or link-scanned.

### The Oracle search index (Phase 4)

One table, keyed by `(wiki_name, path)` — the durable identity of a page is its **wiki + relative
path**, *not* `WikiPage.Id` (which is regenerated on every read and never persisted). Canonical DDL:
[docker/oracle/02-schema.sql](../../docker/oracle/02-schema.sql).

```sql
wiki_page(
  wiki_name, path,               -- composite primary key
  title, type, tags, snippet,    -- metadata for display / filtering
  content   CLOB,               -- body, indexed by Oracle Text (CONTAINS)
  emb       VECTOR(768,FLOAT32), -- embedding, queried by cosine VECTOR_DISTANCE
  updated_at)
```

The adapter **creates this schema on first use** if it's missing (init scripts only run on a fresh
container), so a running app never fails just because the DDL wasn't applied by hand.

### The Oracle project registry (Phase 6)

A second, small table holds one row per project (== wiki) with just metadata — the page rows still
live in `wiki_page`. Canonical DDL: [docker/oracle/03-schema.sql](../../docker/oracle/03-schema.sql);
`OracleProjectRepository` ensures it idempotently on first use, exactly like `wiki_page`.

```sql
wiki_project(
  name,                          -- primary key (the wiki/project name)
  created_at, last_ingest_at,    -- lifecycle timestamps
  page_count, source_count)      -- recomputed + stamped best-effort on each ingest
```

This is *derived* state: files under `WIKI_ROOT` stay canonical, and the active-project pointer
(`.current-project`) is a host-local dotfile, not a DB row — so `project select` and offline
scaffolding never depend on Oracle.

---

## 4. The Domain layer (pure building blocks)

Everything here is deterministic and dependency-free — trivial to unit-test.

- [`WikiPage`](../../src/LlmWiki.Domain/WikiPage.cs) — the page record: title, `PageType`, content,
  tags, sources, timestamps. An immutable `record` (use `with` to edit).
- [`PageType`](../../src/LlmWiki.Domain/PageType.cs) — `Summary | Entity | Concept | Overview | Answer`
  (`Answer` is a Phase 5 saved query answer; it round-trips through the frontmatter serializer and the
  vector store's case-insensitive `ParseType` with no adapter change).
- [`WikiSchema`](../../src/LlmWiki.Domain/WikiSchema.cs) / [`LinkStyle`](../../src/LlmWiki.Domain/LinkStyle.cs)
  — the per-wiki conventions.
- [`Slug`](../../src/LlmWiki.Domain/Slug.cs) — `"Acme Corp" → "acme-corp"`. The single source of truth
  for turning a title into a filename, so the repository and the agent produce **matching** paths.
- [`CrossReference`](../../src/LlmWiki.Domain/CrossReference.cs) — parses links out of a body
  (`CrossReferenceParser`, regex-based, per link style) and models link-resolution results;
  [`CrossReferenceWriter`](../../src/LlmWiki.Domain/CrossReferenceWriter.cs) is the write-side mirror
  (render a link in the right style).
- [`IndexRenderer`](../../src/LlmWiki.Domain/IndexRenderer.cs) + `IndexEntry` — render `index.md`:
  fixed section order (Sources / Entities / Concepts / Overviews / **Answers**), empty sections
  omitted, entries **stably sorted by path** so the file is deterministic (clean git diffs; a deleted
  page's line just vanishes on the next rebuild — BR-024).
- [`LogEntry` + `LogFormatter`](../../src/LlmWiki.Domain/LogEntry.cs) — format one greppable
  `## [YYYY-MM-DD] ingest | <source>` log block.

The pattern to notice: **pure rendering/parsing logic lives in Domain; the file I/O that feeds it
lives in Infrastructure.** `FileSystemWikiJournal` reads pages off disk and hands plain records to
`IndexRenderer`/`LogFormatter`.

---

## 5. Configuration & startup

Config is deliberately boring and centralized. Flow:

1. **`env/.env`** (gitignored; template is `env/.env.example`) holds flat keys like
   `ORACLE_CONNECTION_STRING`, `CHAT_PROVIDER`, `EMBEDDING_STRATEGY`.
2. [`DotEnvLoader`](../../src/LlmWiki.Shared/Configuration/DotEnvLoader.cs) walks up from the working
   directory to find `env/.env` and loads it into process environment variables (no-ops if absent,
   so CI can supply vars directly).
3. [`LlmWikiConfiguration`](../../src/LlmWiki.Shared/Configuration/LlmWikiConfiguration.cs) maps each
   flat name to a bindable `Section:Key` path (its `EnvToConfigKey` dictionary) and registers the
   strongly-typed options objects.
4. Options classes ([`OracleOptions`](../../src/LlmWiki.Shared/Configuration/OracleOptions.cs),
   [`EmbeddingOptions`](../../src/LlmWiki.Shared/Configuration/EmbeddingOptions.cs),
   [`ChatOptions`](../../src/LlmWiki.Shared/Configuration/ChatOptions.cs),
   [`WikiOptions`](../../src/LlmWiki.Shared/Configuration/WikiOptions.cs)) are injected as
   `IOptions<T>`.

The API host calls `builder.Configuration.AddLlmWikiEnv()`; the CLI calls
`LlmWikiConfiguration.Build()`. Both then call
[`AddLlmWikiInfrastructure`](../../src/LlmWiki.Infrastructure/DependencyInjection.cs) (wires every
adapter + owns SK/Oracle) and `AddLlmWikiAgents` (wires the ingestion service, the backfill indexer,
the query service, and the lint service).

> **To add a new setting:** add it in three places — `env/.env.example`, the options class, and the
> `EnvToConfigKey` map. (That's how `EMBEDDING_STRATEGY` was added in Phase 4.)

### The chat-provider switch

`CHAT_PROVIDER` picks the LLM connector in `AddChat`:

- **`openai`** (default) — SK OpenAI connector; needs `OPENAI_API_KEY`.
- **`anthropic`** — drop-in via Anthropic's OpenAI-compatible endpoint; needs `ANTHROPIC_API_KEY`
  and a Claude `CHAT_MODEL`.
- **`ollama`** — **keyless, local**; talks to Ollama's OpenAI-compatible endpoint. Great for
  offline/free ingestion.

If a hosted provider's key is missing, DI registers `NotConfiguredChatService` instead — the host
still starts, and the chat *diagnostic* fails cleanly rather than the whole process crashing.

---

## 6. Walkthrough: the main flows

### 6a. Diagnostics — `doctor` / `GET /diagnostics`

The cheapest way to know your environment is wired correctly. Both hosts call the same
[`DiagnosticsService`](../../src/LlmWiki.Application/Diagnostics/DiagnosticsService.cs), which runs
three **independent** checks (one failing never masks the others):

1. **Oracle** — connect and do a `CREATE TABLE` / `DROP` round-trip.
2. **Embedding** — embed a probe string and assert the vector length is exactly 768.
3. **Chat** — a chat round-trip returns non-empty text.

The API returns `200` if all pass, `503` otherwise; the CLI prints a PASS/FAIL table.
`GET /health` is a separate, dependency-free liveness probe.

### 6b. Create a wiki — `wiki create <name>`

[`FileSystemWikiRepository.CreateWikiAsync`](../../src/LlmWiki.Infrastructure/FileStore/FileSystemWikiRepository.cs)
refuses if the wiki exists, then writes a `.gitkeep` into each typed directory and renders
`SCHEMA.md`. That's it — a wiki is a directory with a schema file.

### 6c. Ingest a source — `ingest <wiki> <file>` (Phases 2–4 together)

This is the heart of the system. The CLI copies the file into `raw/` (write-once), then calls
[`IngestionService.IngestAsync`](../../src/LlmWiki.Agents/Ingestion/IngestionService.cs). Steps:

1. **Load context** — read the wiki's `SCHEMA.md` and the list of existing pages.
2. **Extract (LLM call #1)** — one structured, JSON-mode call
   ([`IngestionPrompts.Extract`](../../src/LlmWiki.Agents/Prompts/IngestionPrompts.cs)) turns the raw
   source into an `ExtractionResult`: a source title, summary, key points, entities, concepts,
   tags, and an overarching topic. **Only facts present in the source** — no invention.
3. **Write the summary page** — one `summaries/<slug>.md` per source.
4. **Write entity & concept pages** — for each extracted item: if the page is new, create it (or a
   *stub* if the source only mentioned it in passing → also flagged as a "knowledge gap"); if it
   already exists, **append** the new source's contribution and add provenance rather than
   overwriting.
5. **Write the topic overview** — a `topics/<slug>.md` linking the source to related pages, using
   the wiki's link style.
6. **Reconcile (LLM call #2, only if relevant)** — for pages this source touched by name, ask the
   model whether the new source contradicts what's on the page. Contradictions are **noted inline**
   (`> Contradiction noted: …`), never silently overwritten.
7. **Build the `IngestionReport`** — a structured record of every per-page outcome
   (`Created` / `Updated` / `StubCreated` / `Failed`), contradictions, and gaps.
8. **Journal (Phase 3), as a final step** — `RebuildIndexAsync` regenerates `index.md` from the
   pages now on disk, then `AppendLogAsync` adds one `log.md` entry summarizing the run.
9. **Embed-on-change (Phase 4), as a final step** — embed **only the pages this run changed**
   (the `Created/Updated/StubCreated` outcomes ∪ any contradiction-noted page) and upsert each into
   Oracle. What text is embedded is chosen by `EMBEDDING_STRATEGY` via
   [`EmbeddingText`](../../src/LlmWiki.Agents/Ingestion/EmbeddingText.cs).
10. **Record project metadata (Phase 6), as a final step** — `RecordProjectAsync` counts the pages on
    disk and the sources in `raw/` (excluding the `.gitkeep`) and calls
    `IProjectRepository.RecordIngestAsync` to stamp `last_ingest_at` + those counts on the
    `wiki_project` row (upserting it if the project was never explicitly registered).

**Two things to internalize about this pipeline:**

- **Every side effect is a boundary, and steps 8–10 are best-effort.** A single page write, the
  journal, the embedding step, or the project-metadata step can fail without taking the run down — the
  failure is recorded as a `Failed` outcome on the report (e.g. a `project` outcome for step 10) and
  the rest proceeds. This is NFR-06: *the wiki is never left corrupt, and file-only ingestion keeps
  working even if Oracle/Ollama are down.*
- **The change set is free.** We don't hash content or add a "dirty" frontmatter field to decide
  what to re-embed — the `IngestionReport` already knows exactly which pages changed. (One subtlety
  the code calls out: contradiction notes are written *outside* the tracked outcomes list, so they
  are unioned back in.)

### 6d. Search — `search <wiki> <query> [--top-k] [--type]` (Phase 4)

1. The CLI embeds the query text with the same `IEmbeddingService` used at ingest time.
2. [`OracleVectorStore.SearchAsync`](../../src/LlmWiki.Infrastructure/VectorStore/OracleVectorStore.cs)
   runs **two arms** inside one wiki (scoped by a `wiki_name` predicate — searches never cross
   wikis, NFR-10):
   - **Semantic arm** — `VECTOR_DISTANCE(emb, :query, COSINE)`, ascending (closest first). Finds the
     right page even when the query shares no words with the title.
   - **Lexical arm** — Oracle Text `CONTAINS(content, …)` with `SCORE()`. Nails exact names and
     technical terms. Free text is sanitized into a safe query (`ToContainsQuery`: alphanumeric
     tokens, brace-escaped, OR-joined), and a malformed query degrades to "no lexical hits" rather
     than failing the whole search.
3. The two ranked lists are combined by **reciprocal-rank fusion** (`score = Σ 1/(k + rank)`,
   `k = 60`) — a standard, tuning-free way to blend rankings so semantic recall and exact-term
   precision reinforce each other. Each arm over-fetches (`max(topK*4, 20)`) before fusion.

There's deliberately **no vector index** yet — an exact cosine scan is correct and fast at the
target corpus size (hundreds of pages). The DDL documents the `CREATE VECTOR INDEX` line for the
scale path.

### 6e. Backfill — `reindex <wiki>` (Phase 4)

Because ingestion only embeds the pages a run *changes*, pages that predate Phase 4 — or that were
written while Oracle was unreachable (the embed step is best-effort) — exist on disk but are absent
from `wiki_page`, so `search` can't find them. `reindex` closes that gap:
[`WikiIndexer.ReindexAsync`](../../src/LlmWiki.Agents/Indexing/WikiIndexer.cs) lists every current
page of a wiki, embeds it (same `EmbeddingText` strategy as ingest), and upserts it — **no LLM
calls, no content edits**, just a rebuild of the derived index. It reuses the exact ports the ingest
embed-step uses, is best-effort per page (a failure is reported in the `ReindexReport`, not thrown),
and is idempotent because `UpsertAsync` is keyed by `(wiki_name, path)`.

This deliberately stays a *separate* path from `IngestionService`'s inline embed loop: ingestion
records embed failures into its `IngestionReport`, the indexer into a `ReindexReport`. They share
`EmbeddingText` but not the loop, to avoid coupling a backfill to the ingest pipeline — a candidate
for later DRY-ing if it earns its keep.

### 6f. Ask — `ask <wiki> [question]` / `POST /query` (Phase 5)

Retrieval returns *pages*; this flow **answers a question**.
[`QueryService.AnswerAsync`](../../src/LlmWiki.Agents/Query/QueryService.cs) is another plain,
port-only orchestrator (no SK types), and it mirrors `IngestionService`'s shape:

1. **Read the index** — `index.md` via `IWikiFileStore` (the journal has no read port), best-effort:
   an absent index is not fatal, just an empty table-of-contents in the prompt (BR-040).
2. **Hybrid search** — embed the question, then `IVectorStore.SearchAsync` — *the identical retrieval
   the `search` command uses.* This is where relevance is decided; disk is never consulted to *select*
   pages.
3. **Hydrate candidates** — a `VectorSearchHit` carries path/title/type/score but **not the body**, so
   each hit is read to full content via `IWikiRepository.ReadPageAsync` and assembled into a CONTEXT
   block. A hit that no longer resolves on disk is skipped, not fatal.
4. **Synthesise (one LLM call)** — a single JSON-mode call
   ([`QueryPrompts.Synthesize`](../../src/LlmWiki.Agents/Prompts/QueryPrompts.cs), grounded strictly to
   CONTEXT) returns `{title, answer, covered, citations}`, parsed through the **same `ExtractJson`
   fence-stripper** as ingestion. The model chooses the answer's format (BR-043) and sets
   `covered:false` when the corpus doesn't cover the question (BR-042 — no speculation).
5. **Filter citations** — only citations whose path was *actually retrieved* survive (BR-041), each
   re-attached to its hit's title/type, so every citation resolves to a real, openable page.

Follow-ups are just an in-process `IReadOnlyList<ConversationTurn>` (prior Q/A pairs) that the CLI
REPL accumulates and replays into the prompt each turn (BR-044); the HTTP surface carries the same
list in the request body.

**Saving an answer** — [`QueryService.SaveAnswerAsync`](../../src/LlmWiki.Agents/Query/QueryService.cs)
(REPL `:save`, or `"save": true` on a *covered* `POST /query`) writes an `answers/<slug>.md`
`PageType.Answer` page behind a **write boundary** (a failure returns a `Failed` `PageOutcome`, never
throws), then — exactly like ingestion's final block — rebuilds `index.md` (the new page appears under
the **Answers** heading), appends a greppable `## [date] query | <question>` line to `log.md`, and
**best-effort embeds** the page so saved answers are themselves searchable (BR-045). Journal/embed
failures are recorded on the outcome's `Detail`, never thrown (NFR-06) — the page is already safely on
disk.

**Two hosts, one service.** The CLI `ask` command is one-shot when a question is passed and an
interactive REPL (`:save`/`:quit`) otherwise. The API exposes the same workflow through
[`QueryController`](../../src/LlmWiki.Api/Controllers/QueryController.cs) — a thin `[ApiController]` that
constructor-injects the ports and delegates straight to `IQueryService` (no logic in the controller).
This is the codebase's **first MVC controller and its first Swagger UI**: `Program.cs` adds
`AddControllers()` + `AddSwaggerGen()` and maps `/swagger` in development, while `/health` and
`/diagnostics` stay minimal-API — a deliberate mix, so the query surface is the one controller.

### 6g. Projects — `project create|list|select` / `GET`·`POST /projects` (Phase 6)

A **project is a wiki** — the same directory tenant, already isolated by the `wiki_name` predicate on
every search (NFR-10). Phase 6 adds a durable *registry* of projects and their metadata plus a
selectable "active project":

- **`project create <name>`** — scaffolds the wiki (`IWikiRepository.CreateWikiAsync`, files are
  canonical) **and** registers it in Oracle (`IProjectRepository.RegisterAsync`, best-effort — a DB
  outage warns, never fails the scaffold), then sets it as active. `project select <name>` guards that
  the wiki exists, persists the pointer, and best-effort registers.
- **`project list`** — reads the `wiki_project` registry (`ListAsync`) and prints each project's
  created / last-ingest / page & source counts, marking the active one with `*`.
- **The active-project pointer** is host-local, not Oracle: [`ICurrentProjectStore`](../../src/LlmWiki.Application/Ports/ICurrentProjectStore.cs)
  / `FileCurrentProjectStore` writes one line to `{WIKI_ROOT}/.current-project`, so `select` works
  offline and never depends on the DB (files canonical, Oracle derived). `ingest`/`search`/`ask`
  resolve the target project via a shared helper: an explicit positional wins, else the pointer, else
  an error.
- **CLI positional subtlety.** System.CommandLine binds a lone token to the first (optional)
  positional, so `ingest`/`search` treat **one** positional as the payload (wiki from the pointer) and
  **two** as an explicit `<wiki> <payload>` (`SplitProjectAndPayload`); `ask` keeps greedy binding
  (its lone token is the wiki for the REPL).
- **The API surface** is a second MVC controller,
  [`ProjectController`](../../src/LlmWiki.Api/Controllers/ProjectController.cs): `GET /projects`,
  `GET /projects/{name}` (404 if absent), and `POST /projects` (scaffold + register → `201`; duplicate
  → `409`). `select` has no endpoint — the pointer is CLI-local, since the API takes the project name
  per request.

The registry stays current because ingestion's **step 10** (above) stamps `last_ingest_at` + counts
best-effort on every run.

### 6h. Lint / health-check — `lint [wiki] [--fix|--report]` / `POST /lint`·`/lint/apply` (Phase 7)

The maintenance half of the value proposition: keep a wiki *internally consistent* (the reason a
compiled wiki beats plain RAG). [`LintService`](../../src/LlmWiki.Agents/Linting/LintService.cs) is a
**plain orchestrator** (ports only, no SK — the same shape as `QueryService`), computing findings two
ways and merging them:

- **Structural pass (deterministic, reliable).** For each page it reuses
  [`IWikiRepository.ResolveLinksAsync`](../../src/LlmWiki.Infrastructure/FileStore/FileSystemWikiRepository.cs)
  — the resolved links feed an inbound-link graph (**orphans** = pages nothing links to) and each
  unresolved link is a **broken-link** warning. When the intended target normalizes to an unambiguous
  typed path (`concepts/anvil.md` under a known typed dir), the finding is upgraded to a **missing
  page** carrying a stub-creation `Fix`. Pages under the thin-content threshold (200 chars) become
  **thin-page** suggestions. No new parsing code.
- **Semantic pass (one best-effort LLM call).** A single JSON-mode
  [`LintPrompts.Analyze`](../../src/LlmWiki.Agents/Prompts/LintPrompts.cs) call over the page digest
  returns **contradictions** (both page paths), **stale claims**, and suggested **questions/sources**
  ([`LintAnalysis`](../../src/LlmWiki.Application/Linting/LintAnalysis.cs), parsed via the shared
  fence-stripper). A chat/parse failure drops the semantic findings but the structural report still
  returns (NFR-06). LLM-cited pages are filtered to those that actually exist.

Findings are sorted **critical → warning → suggestion** (BR-061) into a
[`LintReport`](../../src/LlmWiki.Application/Linting/LintReport.cs); a `lint` line is appended to
`log.md` best-effort. **Applying a fix** ([`ApplyFixAsync`](../../src/LlmWiki.Agents/Linting/LintService.cs))
is the *only* page mutation this phase and mirrors `SaveAnswerAsync` exactly: write the stub page
(write boundary), then **best-effort** rebuild the index, append a `lint` log line, and embed — a
journal/embed failure is recorded on the `PageOutcome.Detail`, never thrown. Report-only findings
(contradictions, orphans, stale, suggestions) carry no `Fix`.

- **CLI** — `lint [wiki] [--fix] [--report]`: the wiki defaults to the active project; `--report`
  prints only (never prompts, exit non-zero iff a critical finding exists); `--fix` auto-applies every
  fix-bearing finding; the default is an interactive **accept / reject / modify-title / quit** loop
  (BR-063), modelled on the `ask` REPL.
- **API** — [`LintController`](../../src/LlmWiki.Api/Controllers/LintController.cs): `POST /lint`
  returns the report; `POST /lint/apply` applies one finding the client echoes back (404 unknown wiki,
  400 fixless finding, 422 failed apply) — the full report+apply surface the Phase 8 client will drive.

---

## 7. Cross-cutting conventions

### Error-handling philosophy

Two distinct postures, by layer:

- **Diagnostics & pipeline side-effects are best-effort.** They catch, record, and continue
  (`DiagnosticsService`, the journal/embed steps). The goal is *never crash the useful work for a
  peripheral failure.*
- **Core reads/writes throw.** `ReadPageAsync` on a missing page, a missing connection string, etc.
  fail loudly — you want to know immediately.

### The "stub convention"

Adapters for phases not yet built are **registered in DI but throw**
`NotImplementedException("… not implemented until Phase N")`, so accidental use fails loudly instead
of silently doing nothing. When you implement a later phase, you **fill the existing stub** (as
Phase 4 did to `OracleVectorStore` and Phase 6 did to `OracleProjectRepository`) — you don't add a
parallel type. No application-adapter stubs remain — Phase 7's linting/health-check is now wired, leaving only Phase 8's React-Native client unbuilt.

### Security

Secrets live only in `env/.env` (gitignored). CI runs **Gitleaks** and fails the build on any
committed credential. Options classes that hold keys are never logged.

### Package & build conventions

- **Central Package Management**: every NuGet version is pinned in `Directory.Packages.props` — never
  put `Version=` in a `.csproj`. Shared build settings live in `Directory.Build.props` (this is also
  where Semantic Kernel's `-alpha`/`-preview` `SKEXPxxxx` warnings are suppressed).
- Target framework `net10.0`; nullable + implicit usings on; `LangVersion=latest`.
- The solution is `LlmWiki.slnx` (the .NET 10 XML solution format).

---

## 8. Phase map — what's real vs. deferred

| Phase | Delivered | State |
|---|---|---|
| 0 | Buildable skeleton, DI wiring, diagnostics (Oracle/embedding/chat) | ✅ real |
| 1 | File-backed wiki: typed dirs, frontmatter, cross-references, `wiki` CLI | ✅ real |
| 2 | Source ingestion: extract → write pages → reconcile → `IngestionReport` | ✅ real |
| 3 | Agent-owned journal: regenerated `index.md` + append-only `log.md` | ✅ real |
| 4 | Hybrid retrieval: per-page embeddings + Oracle Text, embed-on-change, `search` CLI | ✅ real |
| 5 | Query/synthesis: index → hybrid search → cited answer; `ask` REPL + `POST /query`; save-answer | ✅ real |
| 6 | Project registry: Oracle `wiki_project` metadata, `project` CLI + `/projects` API, active-project pointer, best-effort ingest metadata | ✅ real |
| 7 | Lint / health-check: structural + one-LLM-call findings, prioritised report, interactive accept/reject stub-creation, `lint` CLI + `POST /lint`·`/lint/apply` API | ✅ real |
| 8 | React Native / Expo UI, token streaming, log-into-session-context (BR-023) | 🔲 skeleton in `app/` |

---

## 9. Testing

Tests mirror the projects (`tests/LlmWiki.*.Tests`) and follow a consistent style: **real temp-dir
filesystem fixtures** for the file store, and **hand-rolled fakes** (not a mocking framework) for
ports.

- **Domain tests** — pure renderers/parsers (index grouping, log format, slug, cross-references).
- **Infrastructure tests** — file store / repository / journal over a real temp directory; DI
  resolves every port. `OracleVectorStoreTests` is an **opt-in integration test**: it no-ops unless
  `ORACLE_CONNECTION_STRING` points at a live 23ai container, so CI without a database stays green.
- **Agents tests** — the ingestion pipeline with a `ScriptedChat` fake (returns canned extraction /
  reconcile JSON), a `FakeEmbeddingService`, and a `FakeVectorStore` that records upserts. These
  assert the pipeline's *behavior* — e.g. every changed page is embedded exactly once, a
  contradiction-noted page is re-embedded, and an embed failure becomes a `Failed` outcome rather
  than an exception (NFR-06).
- **Api tests** — host the API via `WebApplicationFactory` (`Program` is exposed `partial` for this).
  `QueryEndpointTests` overrides `IQueryService` and `IWikiRepository` with fakes in
  `ConfigureTestServices`, so `POST /query` is exercised (200 + result, 404 for a missing wiki) with no
  Oracle/LLM.
- **Query (Agents) tests** — `QueryServiceTests` seeds a temp-dir wiki with real pages, a
  `ScriptedChat` returns canned `SynthesisResult` JSON, and a fake vector store returns hits for the
  seeded paths. They assert citations resolve to real pages, `covered:false` flows through, a saved
  answer lands as `answers/<slug>.md` (`type: answer`) with a `query` log line + Answers index entry +
  a vector upsert, and that an embed failure on save is recorded (not thrown — NFR-06).

Run everything with `dotnet test LlmWiki.slnx`; scope to one project or filter by name as shown in
[CLAUDE.md](../../CLAUDE.md).

---

## 10. How to make common changes

- **Add a config setting** → `env/.env.example` + the options class + `EnvToConfigKey` (§5).
- **Add a new capability the app needs** → define a **port** in `Application/Ports`, implement the
  **adapter** in `Infrastructure`, register it in `AddLlmWikiInfrastructure`, and consume it through
  the interface. Keep SK/Oracle types out of Domain/Application/Agents.
- **Implement a future phase** → fill the matching **stub** (don't add a parallel type); flip its
  DI registration comment; add tests alongside the existing style.
- **Change what gets embedded** → `EmbeddingStrategy` in `EmbeddingOptions` + `EmbeddingText.For`.
- **Change ranking/fusion** → `OracleVectorStore.Fuse` (the RRF constant `k`, the over-fetch factor)
  and the two SQL arms.
- **Change how answers are synthesised** → `QueryPrompts.Synthesize` (the grounding/format/gap rules)
  and `QueryService.AnswerAsync` (retrieval → context → citation filtering).

---

## 11. Quick reference

**CLI**

```bash
dotnet run --project src/LlmWiki.Cli -- doctor
dotnet run --project src/LlmWiki.Cli -- wiki create demo [--link-style Wikilink|MarkdownLink]
dotnet run --project src/LlmWiki.Cli -- wiki list | inspect demo
dotnet run --project src/LlmWiki.Cli -- wiki page add|show demo <path> …
dotnet run --project src/LlmWiki.Cli -- ingest demo ./docs/sample-source.md
dotnet run --project src/LlmWiki.Cli -- search demo "how the thing works" --top-k 5 --type entity
dotnet run --project src/LlmWiki.Cli -- reindex demo          # backfill: embed all existing pages
dotnet run --project src/LlmWiki.Cli -- ask demo "how does the thing work?"   # one-shot cited answer
dotnet run --project src/LlmWiki.Cli -- ask demo                              # REPL (:save, :quit)
dotnet run --project src/LlmWiki.Cli -- project create|select ml-papers       # register + make active
dotnet run --project src/LlmWiki.Cli -- project list                          # registry; * = active
dotnet run --project src/LlmWiki.Cli -- ingest|search|ask "…"                 # omit wiki → active project
dotnet run --project src/LlmWiki.Cli -- lint demo                             # health-check (accept/reject)
dotnet run --project src/LlmWiki.Cli -- lint demo --report                    # print only; non-zero on critical
dotnet run --project src/LlmWiki.Cli -- lint demo --fix                       # auto-apply stub-creation fixes
```

**API** — `GET /health` (liveness), `GET /diagnostics` (the three checks; 200/503),
`POST /query` (grounded cited answer), `GET`·`POST /projects` (registry),
`POST /lint`·`/lint/apply` (health-check report + apply; `/swagger` UI in development).

**Infra** — `cd docker && docker compose up -d`, then pull the models
(`ollama pull nomic-embed-text`, `ollama pull llama3.1`).
