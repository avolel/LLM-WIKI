# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A database-driven LLM wiki: content lives in Oracle, is embedded for semantic search, and is authored/served by LLM agents. The repo began as the **Phase 0** skeleton — a buildable, connectable foundation with the canonical layout that feature phases (1–8) drop into. **Phase 1** added the file-backed wiki (`IWikiRepository`); **Phase 2** added the source-ingestion pipeline (`IIngestionService`); **Phase 3** added the agent-owned journal (`IWikiJournal` — a regenerated `index.md` catalogue + append-only `log.md`, maintained as the final step of every ingest); **Phase 4** added hybrid retrieval — per-page 768-dim embeddings + Oracle Text in Oracle (`IVectorStore`), embed-on-change during ingestion, and a CLI `search` command; and **Phase 5** added the query/synthesis workflow (`IQueryService` — read the index → hybrid search → read the top candidate pages → synthesise a grounded, **cited** answer, honestly reporting gaps), exposed as a CLI `ask` REPL and a `POST /query` MVC controller (with Swagger UI), and able to **save a good answer back** as a new `Answer` page that is itself indexed, logged, and embedded; and **Phase 6** added the Oracle-persisted project registry (`IProjectRepository`/`OracleProjectRepository` over a new `wiki_project` table — the last stub, now filled), a host-local active-project pointer (`ICurrentProjectStore`), a `project` CLI group + `/projects` API, and a best-effort ingest step that keeps each project's metadata current; and **Phase 7** added the lint / health-check workflow (`ILintService`/`LintService` — a fresh `Linting` vertical): a deterministic structural pass (broken links, orphans, missing/thin pages, reusing `ResolveLinksAsync`) fused with one JSON-mode LLM call (contradictions, stale claims, suggested questions/sources) into a **prioritised** report (critical → warning → suggestion), with interactive accept/reject stub-creation as the only page-mutating fix, exposed as a CLI `lint [wiki] [--fix|--report]` command and `POST /lint` + `POST /lint/apply` MVC controllers; and **Phase 8** built the product's front door — a **web-first Expo (React Native) client** (`app/`) for chat (clickable citations that open a page, follow-ups, save-answer, honest gaps), a wiki browser, and project list/create/select — plus the API glue it needs: a browse tree (`GET /wikis/{wiki}/pages`), a single-page read (`GET /wikis/{wiki}/pages/{path}`), a dedicated save-answer endpoint (`POST /query/save`), **CORS**, and **string-enum JSON**; the client is deliberately minimal-dependency (a custom tab switcher + custom markdown renderer, one typed `client.ts`), with token streaming and any ingestion/lint UI deferred. The whole BRD surface (backend + CLI + API + web client) is now real. See [docs/plans/](docs/plans/) (`plan-phase-0.md` … `plan-phase-8.md`), the plain-English [docs/code-overview/code-overview.md](docs/code-overview/code-overview.md), and the ADRs [0001-phase-0-foundations.md](docs/adr/0001-phase-0-foundations.md) / [0002-phase-6-project-registry.md](docs/adr/0002-phase-6-project-registry.md) / [0003-phase-7-lint.md](docs/adr/0003-phase-7-lint.md) / [0004-phase-8-client.md](docs/adr/0004-phase-8-client.md).

## Commands

```bash
# Infrastructure (Oracle Free 23ai + Ollama) — required for diagnostics to pass
cd docker && docker compose up -d
docker compose exec ollama ollama pull nomic-embed-text   # one-time, 768-dim embedding model
docker compose exec ollama ollama pull llama3.1           # one-time chat model (for CHAT_PROVIDER=ollama)
cd ..

# Secrets — env/.env is gitignored; the .NET hosts load it automatically (see DotEnvLoader)
cp env/.env.example env/.env                              # fill ORACLE_PWD, conn string, and a chat provider
                                                          # (keyless local: CHAT_PROVIDER=ollama, CHAT_MODEL=llama3.1)

# .NET (note the .slnx XML solution format from the .NET 10 SDK)
dotnet build LlmWiki.slnx
dotnet test  LlmWiki.slnx
dotnet test  tests/LlmWiki.Domain.Tests/LlmWiki.Domain.Tests.csproj   # single project
dotnet test  LlmWiki.slnx --filter "FullyQualifiedName~HealthEndpoint" # single test/class

# Run the connectivity checks (Oracle round-trip, 768-dim embedding, chat reply)
dotnet run --project src/LlmWiki.Cli -- doctor
dotnet run --project src/LlmWiki.Api          # http://localhost:5080 → GET /health, /diagnostics

# Wiki (Phase 1) + ingestion (Phase 2) + hybrid search (Phase 4)
dotnet run --project src/LlmWiki.Cli -- wiki create demo
dotnet run --project src/LlmWiki.Cli -- ingest demo ./docs/sample-source.md   # copies to raw/, builds + embeds pages
dotnet run --project src/LlmWiki.Cli -- wiki inspect demo
dotnet run --project src/LlmWiki.Cli -- search demo "how the thing works" --top-k 5 --type entity
dotnet run --project src/LlmWiki.Cli -- reindex demo          # backfill: embed all existing pages into Oracle

# Query & synthesis (Phase 5) — grounded, cited answers; REPL keeps follow-up history; :save persists an Answer page
dotnet run --project src/LlmWiki.Cli -- ask demo "how does the thing work?"   # one-shot answer + Sources list
dotnet run --project src/LlmWiki.Cli -- ask demo                              # no question → interactive REPL (:save, :quit)
# HTTP: POST /query {"wiki":"demo","question":"…"} — browse http://localhost:5080/swagger to invoke it

# Projects (Phase 6) — Oracle registry (wiki_project) + a persisted "active project" pointer
dotnet run --project src/LlmWiki.Cli -- project create ml-papers   # scaffold wiki + register in Oracle + make active
dotnet run --project src/LlmWiki.Cli -- project list               # registry + metadata; * marks the active project
dotnet run --project src/LlmWiki.Cli -- project select ml-papers   # persist the active project ({WIKI_ROOT}/.current-project)
# With a project selected, ingest/search/ask default to it when the wiki name is omitted:
dotnet run --project src/LlmWiki.Cli -- ingest ./docs/sample-source.md   # into the active project
dotnet run --project src/LlmWiki.Cli -- search "how the thing works"     # active project
dotnet run --project src/LlmWiki.Cli -- ask                              # REPL on the active project
# HTTP: GET /projects, GET /projects/{name}, POST /projects {"name":"…"} — browse http://localhost:5080/swagger

# Lint / health-check (Phase 7) — prioritised findings (critical → warning → suggestion) + accept/reject
dotnet run --project src/LlmWiki.Cli -- lint demo            # interactive accept/reject/modify stub-creation
dotnet run --project src/LlmWiki.Cli -- lint demo --report   # print only; exit non-zero iff a critical finding
dotnet run --project src/LlmWiki.Cli -- lint demo --fix      # auto-apply every fix-bearing finding (no prompt)
dotnet run --project src/LlmWiki.Cli -- lint                 # active project when the wiki name is omitted
# HTTP: POST /lint {"wiki":"demo"} → report; POST /lint/apply {"wiki":"demo","finding":{…}} → apply one fix

# Browse + save API (Phase 8) — the read surface the client needs; CORS + string-enum JSON are on.
# HTTP: GET /wikis/demo/pages → page tree by category; GET /wikis/demo/pages/entities/<slug>.md → one page;
#       POST /query/save {"wiki":"demo","result":{…covered QueryResult…}} → persist an Answer page (400 if uncovered)

# Expo client (Phase 8, Node 24) — web-first chat/browse/projects UI. `lint` is a typecheck — there is no ESLint.
cd app && npm install && npm run web          # http://localhost:8081 → Chat / Browse / Projects / Status tabs (also: ios / android)
npm run lint                                  # tsc --noEmit
npm run export:web                            # web bundle (CI gate)
```

CI ([.github/workflows/ci.yml](.github/workflows/ci.yml)) runs three jobs: `.NET build+test` (Release), `Expo build+lint`, and a **Gitleaks secret scan** that fails the build on any committed credential.

## Architecture

Clean / layered; **dependencies point inward**. Semantic Kernel and Oracle are confined to `Infrastructure`/`Agents` — never reference them from `Domain` or `Application`.

```
Domain  ←  Application (ports)  ←  Infrastructure (adapters: Oracle, Ollama, OpenAI/Anthropic)
                  ↑                          ↑
                Agents                     Shared (config + .env loading)
                  ↑                          ↑
                      Api / Cli  (composition roots)
```

- **Domain** — pure entities + pure renderers (`WikiPage`, `PageType`, `Slug`, `CrossReference`/`CrossReferenceWriter`, and the Phase 3 `IndexEntry`/`IndexRenderer` and `LogEntry`/`LogFormatter`). No dependencies.
- **Application** — `Ports/` defines interfaces (`IChatService`, `IEmbeddingService`, `IDatabaseHealthCheck`, `IProjectRepository`, `IVectorStore`, `IWikiFileStore`, `IWikiRepository`, `IWikiJournal`); `Diagnostics/`, `Ingestion/`, and (Phase 5) `Query/` hold orchestration contracts + DTOs (`IIngestionService`/`IngestionReport`, `IQueryService`/`QueryResult`/`SynthesisResult`/`ConversationTurn`) that the API and CLI call.
- **Infrastructure** — implements every port. `AddLlmWikiInfrastructure` is the single DI entry point and **owns all SK + Oracle wiring**. Hosts never reference SK directly.
- **Agents** — agent orchestration. `AddLlmWikiAgents` registers `IIngestionService` (Phase 2), `IWikiIndexer` (Phase 4 backfill), and `IQueryService` (Phase 5). `Query/QueryService` is a **plain orchestrator** (like `IngestionService`: ports only, no SK): `AnswerAsync` reads `index.md` (via `IWikiFileStore`, since `IWikiJournal` has no read port) → embeds the question → `IVectorStore.SearchAsync` → hydrates each hit to full content via `IWikiRepository.ReadPageAsync` → one JSON-mode `IChatService` call (`Prompts/QueryPrompts`, reusing the `ExtractJson` fence-stripper) → keeps only citations that were actually retrieved (resolvable, BR-041). `SaveAnswerAsync` writes an `answers/<slug>.md` `Answer` page (write boundary → `PageOutcome`), rebuilds the index + appends a `query` log line, then **best-effort** embeds it — a journal/embed failure is recorded on the outcome detail, never thrown (NFR-06). `Ingestion/IngestionService` is a **plain orchestrator** against the `IChatService` + `IWikiRepository` ports (no direct SK dependency) so it's unit-testable and the SK Process Framework can later replace it behind the port. Its **final step** rebuilds `index.md` and appends to `log.md` via `IWikiJournal` (Phase 3); its Phase 4 embed-on-change step then embeds only the pages the run changed via the `IEmbeddingService` + `IVectorStore` ports (still no SK). Both are **best-effort** — a failure is recorded as a `Failed` outcome, never thrown, so file-only ingestion keeps working (NFR-06). `Indexing/WikiIndexer` (behind `IWikiIndexer`) is the **backfill** path — it embeds *every* existing page of a wiki (the CLI `reindex` command) for content authored before Phase 4 or written while Oracle was down; same ports, no LLM calls, no content edits. `Ingestion/EmbeddingText` selects the text to embed per `EMBEDDING_STRATEGY` (BR-034); `Prompts/IngestionPrompts` holds the page-type-specific extraction/reconcile prompts.
- **Shared** — `env/.env` loading and strongly-typed options (`OracleOptions`, `EmbeddingOptions`, `ChatOptions`, `WikiOptions`).
- **Api / Cli** — thin composition roots. The API exposes `/health` (liveness) and `/diagnostics` (both minimal-API), plus the Phase 5 `POST /query` and Phase 6 `GET`/`POST /projects` **MVC controllers** (`Controllers/QueryController` + `Controllers/ProjectController`; Swagger UI at `/swagger` in development, `AddControllers`/`MapControllers` alongside the existing minimal APIs). The CLI exposes `doctor`, the Phase 5 `ask` command (one-shot when a question is passed, otherwise an interactive REPL that keeps follow-up history in-process and supports `:save`/`:quit`), and the Phase 6 `project` group (`create`/`list`/`select`). `ingest`/`search`/`ask` default to the active project (persisted via `ICurrentProjectStore`) when the wiki name is omitted. Diagnostics run the **same three checks** via `IDiagnosticsService`.

### Stub convention

Adapters not yet needed are registered in DI but **throw `NotImplementedException` with a "...not implemented until Phase N" message** so accidental use fails loudly. When implementing a later phase, fill the matching stub — don't add a parallel type. Real today: the embedding/chat/Oracle-health diagnostics paths, the file-backed wiki store/repository (Phase 1), the ingestion service (Phase 2), the Oracle VECTOR + Oracle Text hybrid store (`OracleVectorStore`, Phase 4), the query/synthesis service (`QueryService`, Phase 5), the Oracle project registry (`OracleProjectRepository`, Phase 6), and the lint/health-check service (`LintService`, Phase 7). **No stubs remain** — Phase 8 built out the Expo client (`app/`) and its browse/save API, so the whole BRD surface is real.

### Configuration flow

`env/.env` uses flat names (`ORACLE_CONNECTION_STRING`, `EMBEDDING_DIM`, `CHAT_PROVIDER`, ...). `LlmWikiConfiguration` maps these to bindable `Section:Key` paths and registers the typed options; `DotEnvLoader` walks up from the working dir to find `env/.env` and no-ops if absent (CI supplies vars via the environment). The API calls `AddLlmWikiEnv()` on its host builder; the CLI calls `LlmWikiConfiguration.Build()`. To add a new setting: add it to `env/.env.example`, the options class, and the `EnvToConfigKey` map.

### Chat provider switch

`CHAT_PROVIDER` selects the SK connector in `AddChat` ([DependencyInjection.cs](src/LlmWiki.Infrastructure/DependencyInjection.cs)): `openai` (default) wires the SK OpenAI connector; `anthropic` is a drop-in via Anthropic's OpenAI-compatible endpoint (`https://api.anthropic.com/v1/`) — set `ANTHROPIC_API_KEY` and a Claude `CHAT_MODEL`; `ollama` is a **keyless local** option via Ollama's OpenAI-compatible endpoint (`CHAT_ENDPOINT`, default `http://localhost:11434/v1/`) — set `CHAT_MODEL` to a pulled model (e.g. `llama3.1`). For the hosted providers, if the selected provider's key is missing DI registers `NotConfiguredChatService` so the host still starts (the chat diagnostic then fails cleanly).

## Conventions

- **Central Package Management**: all NuGet versions are pinned in [Directory.Packages.props](Directory.Packages.props) (never put `Version=` in a `.csproj`); shared build settings live in [Directory.Build.props](Directory.Build.props). This deliberately contains Semantic Kernel's API churn — several SK connectors are `-alpha`/`-preview` and their `SKEXPxxxx` warnings are suppressed centrally.
- Target framework `net10.0`, nullable + implicit usings enabled, `LangVersion=latest`.
- Embeddings are **768-dim** (`nomic-embed-text` via Ollama); the diagnostics check asserts this dimension.
- Oracle `VECTOR` columns and Oracle Text indexes are live in **Phase 4**: `OracleVectorStore` owns the `wiki_page` table (canonical DDL [docker/oracle/02-schema.sql](docker/oracle/02-schema.sql); the adapter also ensures it idempotently on first use since init scripts don't re-run on an existing container). [docker/oracle/spike-vector.sql](docker/oracle/spike-vector.sql) remains a manual spike that validated the primitives, not wired into the app.
- Never commit secrets — Gitleaks gates CI and `env/.env` is gitignored.

## Plans

Save implementation plans as markdown in [docs/plans/](docs/plans/), following the existing [plan-phase-0.md](docs/plans/plan-phase-0.md) (e.g. `plan-phase-1.md`). Record cross-cutting architecture decisions as ADRs in [docs/adr/](docs/adr/).

## Git Policy

**Claude is NEVER allowed to commit to this repository.**

Claude may stage files and draft a commit message, but must stop there. The human reviews the staged changes and runs `git commit` manually.

## Working agreement
When proposing a plan or a change set, always list every new/updated file and include the full code to be added/changed, so it can be reviewed before implementation.

IWikiFileStore/FileSystemWikiFileStore is now implemented (Phase 1), and IWikiRepository/FileSystemWikiRepository is the new wiki-aware port. Note WIKI_ROOT config and the new YamlDotNet dependency.

Phase 2 added source ingestion: `IIngestionService` (Application `Ingestion/` port + `IngestionReport` DTOs), implemented by `LlmWiki.Agents/Ingestion/IngestionService` and driven by the CLI `ingest` command. Domain gained `Slug` (title→filename slug, lifted out of the repository) and `CrossReferenceWriter` (write-side mirror of `CrossReferenceParser`). `raw/` is immutable (NFR-02).

Phase 3 added the agent-owned journal (BR-020…024): `IWikiJournal` (Application port) implemented by `FileSystemWikiJournal` (Infrastructure), plus pure Domain renderers `IndexRenderer`/`IndexEntry` and `LogFormatter`/`LogEntry`. `index.md` is **regenerated deterministically** from disk each ingest (stably sorted, no timestamp → clean diffs, so a deleted page's entry just vanishes — BR-024); `log.md` is **append-only** with greppable `## [YYYY-MM-DD] ingest | …` headers. `FileSystemWikiRepository.IsPage` excludes root `index.md`/`log.md` so they aren't counted, listed, catalogued, or link-scanned. The journal runs as ingestion's final step, wrapped so a failure is a recorded outcome, not fatal (NFR-06).

Phase 4 added hybrid retrieval (BR-030…035): `IVectorStore` is now path-keyed + hybrid, implemented by the real `OracleVectorStore` (Oracle 23ai `VECTOR(768, FLOAT32)` cosine + Oracle Text `CONTAINS`, fused by reciprocal-rank; schema `wiki_page` ensured idempotently, canonical DDL in `docker/oracle/02-schema.sql`). Ingestion embeds only the pages a run changed (`IngestionReport` outcomes ∪ contradiction pages) via a best-effort step. New: `EmbeddingStrategy`/`EMBEDDING_STRATEGY` config (Shared) + `EmbeddingText` selector (Agents), and the CLI `search <wiki> <query> [--top-k] [--type]` command. Per-wiki isolation is enforced by a `wiki_name` predicate (NFR-10). Agents now references Shared (for `EmbeddingOptions`). A companion **backfill** path — `IWikiIndexer` (Application `Indexing/`) implemented by `Agents/Indexing/WikiIndexer`, driven by the CLI `reindex <wiki>` command — embeds *all* existing pages (no LLM calls, no content edits) so wikis authored before Phase 4, or written while Oracle was unreachable, become searchable.

Phase 5 added the query/synthesis workflow (BR-040…045): `IQueryService` (Application `Query/` port + `QueryResult`/`QueryOptions`/`Citation`/`ConversationTurn`/`SynthesisResult` DTOs), implemented by `LlmWiki.Agents/Query/QueryService` — read `index.md` → hybrid `IVectorStore.SearchAsync` → hydrate hits to full content → single-shot JSON-mode synthesis (`Prompts/QueryPrompts`) → a grounded, cited answer with `Covered=false` for honest gaps (BR-042). Follow-ups are carried by an in-process `ConversationTurn` history over both surfaces (CLI `ask` REPL and the `POST /query` body — BR-044). `Domain.PageType` gained an `Answer` member (round-trips through `FrontmatterSerializer`/`OracleVectorStore.ParseType` unchanged; `IndexRenderer` gained an **Answers** section); `SaveAnswerAsync` persists an `answers/<slug>.md` Answer page, rebuilds the index, appends a `query` log line, and best-effort embeds it so saved answers are themselves searchable (BR-045). New host surface: the CLI `ask` command and the API's **first MVC controller** `POST /query` with **Swashbuckle Swagger UI** (`Swashbuckle.AspNetCore`, pinned centrally); `/health` and `/diagnostics` stay minimal-API (a deliberate mix). `answers/` needs no scaffolding — `WriteAsync` mkdirs on first save and `IsPage` already includes it — but it is documented in the generated `SCHEMA.md`.

Phase 6 added the project registry (BR-050…053, NFR-10): the last stub, `OracleProjectRepository`, is **filled** — a real ODP.NET adapter (mirroring `OracleVectorStore`: connection guard, idempotent runtime schema, `BindByName`, `MERGE` upserts) over a new `wiki_project` table (canonical DDL `docker/oracle/03-schema.sql`). The Phase-0 page-shaped `IProjectRepository` port is **redesigned** into project-registry CRUD (`RegisterAsync`/`ListAsync`/`GetAsync`/`RecordIngestAsync` + a `ProjectInfo` DTO) — a project **is** the existing per-wiki tenant, so isolation is still the `wiki_name` predicate (NFR-10), this just adds durable metadata + enumeration. A new `ICurrentProjectStore`/`FileCurrentProjectStore` persists the active project as `{WIKI_ROOT}/.current-project` (host-local, no Oracle, works offline); `ingest`/`search`/`ask` resolve to it when the wiki arg is omitted. `IngestionService` gained two ctor ports (`IWikiFileStore` + `IProjectRepository`) and a **best-effort** final step (`RecordProjectAsync`) that stamps `last_ingest_at` + recomputed page/source counts — a failure is a `Failed` "project" outcome, never thrown (NFR-06). New host surface: the CLI `project` group (`create` scaffolds the wiki **and** registers it, then selects it; `list` reads the Oracle registry marking the active project with `*`; `select` persists the pointer) and the API `ProjectController` (`GET`/`POST /projects`; `select` has no endpoint — the pointer is host-local). CLI note: `ingest`/`search` make the wiki a leading **optional** positional, but System.CommandLine binds a lone token to the first positional, so a single positional is treated as the payload (wiki from the active pointer) and two as explicit `<wiki> <payload>` (`SplitProjectAndPayload`); `ask` keeps greedy binding (lone token = wiki for the REPL).

Phase 7 added the lint / health-check workflow (BR-060…063): a fresh `Linting` vertical — `ILintService` (Application `Linting/` port + `LintReport`/`LintFinding`/`LintSeverity`/`LintCategory`/`SuggestedFix` DTOs and the `LintAnalysis` JSON contract), implemented by `LlmWiki.Agents/Linting/LintService` as a **plain orchestrator** (ports only, no SK — same shape as `QueryService`). `LintAsync` fuses a **deterministic structural pass** (per-page `IWikiRepository.ResolveLinksAsync` → broken-link warnings, an inbound-link graph for orphans, missing-page findings when a broken link normalizes to an unambiguous typed path, and thin-page suggestions under a 200-char threshold) with **one best-effort JSON-mode LLM call** (`Prompts/LintPrompts` → contradictions, stale claims, suggested questions/sources), sorted critical → warning → suggestion (BR-061); a `lint` line is appended to `log.md` best-effort. `ApplyFixAsync` is the **only** page mutation — it mirrors `SaveAnswerAsync` exactly (write the stub page at a write boundary, then best-effort rebuild index + append `lint` log + embed; a journal/embed failure is a recorded `PageOutcome.Detail`, never thrown — NFR-06). All non-stub findings are report-only; no new Oracle table (lint output is derived, recomputed each run). New host surface: the CLI `lint [wiki] [--fix|--report]` command (interactive accept/reject/modify by default, modelled on the `ask` REPL) and the API `LintController` (`POST /lint` report + `POST /lint/apply` apply-one-finding). Note `WikiSchema.Directories` scaffolds `summaries/entities/topics/raw` but **not** `concepts` (ingestion mkdirs `concepts/` on first write), so `LintService.TryStubFix` matches against its own typed-dir set (`summaries/entities/concepts/topics`), not `WikiSchema.Directories`.

Phase 8 added the interfaces (BR-070…075, NFR-08/09): the web-first Expo (React Native) client in `app/` plus the small HTTP surface it needs — reusing existing ports, no new orchestration or Oracle table. **API:** `Program.cs` turns on a permissive default **CORS** policy (the Expo web build is a browser origin, single local user, NFR-04) and a `JsonStringEnumConverter` on **both** the controller and minimal-API pipelines (enums now serialize as their names, e.g. `"Entity"`; a converter-aware `JsonSerializerOptions` was added to the API tests that read a response enum back). New `WikiController` — `GET /wikis/{wiki}/pages` groups `ListPagesAsync` paths into a `WikiTree` by top-level typed dir (BR-073), and catch-all `GET /wikis/{wiki}/pages/{**relativePath}` returns one page via `ReadPageAsync` (BR-071; `FileNotFound`/`DirectoryNotFound` → 404). `QueryController` gains `POST /query/save` (`SaveAnswerRequest`) → `IQueryService.SaveAnswerAsync` for a covered result (uncovered → 400), so the client persists an answer without re-synthesising (the old `Save` flag on `POST /query` stays for CLI back-compat). **Client** (minimal-dependency — **no** navigation/markdown/state library): one typed `src/api/client.ts` (extended with `postJson` + typed projects/query/save/browse calls; enums are string unions), a custom `useState` tab switcher in `App.tsx` over four screens (Chat/Browse/Projects/Status) above a custom `TabBar`, an `AppProvider` Context holding the active project (persisted to `localStorage` on web — BR-050), and a small custom `Markdown` renderer (headings/lists/code/tables/inline spans → RN primitives, unknown syntax falls back to text; links call `onLinkPress` to open a page). `ChatScreen` is the core surface (spinner while awaiting → markdown answer + clickable `CitationChip`s → `PageModal`; `ConversationTurn[]` history for follow-ups, BR-044; Save-answer on covered, "Not covered" note otherwise); `BrowseScreen` renders the tree with pull-to-refresh; `ProjectsScreen` lists/creates/selects; `HomeScreen` is reused as the Status tab. `state/index.ts` became `state/index.tsx` (now has JSX). Deferred (decision-locked): SSE/token streaming (loading-state stands in for BR-074), native device verification, and any ingestion/lint UI — ingestion stays CLI-only (BR-075).