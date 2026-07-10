# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A database-driven LLM wiki: content lives in Oracle, is embedded for semantic search, and is authored/served by LLM agents. The repo began as the **Phase 0** skeleton — a buildable, connectable foundation with the canonical layout that feature phases (1–8) drop into. **Phase 1** added the file-backed wiki (`IWikiRepository`); **Phase 2** added the source-ingestion pipeline (`IIngestionService`); **Phase 3** added the agent-owned journal (`IWikiJournal` — a regenerated `index.md` catalogue + append-only `log.md`, maintained as the final step of every ingest); and **Phase 4** added hybrid retrieval — per-page 768-dim embeddings + Oracle Text in Oracle (`IVectorStore`), embed-on-change during ingestion, and a CLI `search` command. The remaining stub is Oracle project persistence (`OracleProjectRepository`, Phase 6); see [docs/plans/](docs/plans/) (`plan-phase-0.md` … `plan-phase-4.md`), the plain-English [docs/code-overview/code-overview.md](docs/code-overview/code-overview.md), and [docs/adr/0001-phase-0-foundations.md](docs/adr/0001-phase-0-foundations.md).

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

# Expo client (Node 24). `lint` is a typecheck — there is no ESLint.
cd app && npm install && npm run web          # also: npm run ios / npm run android
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
- **Application** — `Ports/` defines interfaces (`IChatService`, `IEmbeddingService`, `IDatabaseHealthCheck`, `IProjectRepository`, `IVectorStore`, `IWikiFileStore`, `IWikiRepository`, `IWikiJournal`); `Diagnostics/` and `Ingestion/` hold orchestration contracts + DTOs (`IIngestionService`, `IngestionReport`) that the API and CLI call.
- **Infrastructure** — implements every port. `AddLlmWikiInfrastructure` is the single DI entry point and **owns all SK + Oracle wiring**. Hosts never reference SK directly.
- **Agents** — agent orchestration. `AddLlmWikiAgents` registers `IIngestionService` (Phase 2) and `IWikiIndexer` (Phase 4 backfill). `Ingestion/IngestionService` is a **plain orchestrator** against the `IChatService` + `IWikiRepository` ports (no direct SK dependency) so it's unit-testable and the SK Process Framework can later replace it behind the port. Its **final step** rebuilds `index.md` and appends to `log.md` via `IWikiJournal` (Phase 3); its Phase 4 embed-on-change step then embeds only the pages the run changed via the `IEmbeddingService` + `IVectorStore` ports (still no SK). Both are **best-effort** — a failure is recorded as a `Failed` outcome, never thrown, so file-only ingestion keeps working (NFR-06). `Indexing/WikiIndexer` (behind `IWikiIndexer`) is the **backfill** path — it embeds *every* existing page of a wiki (the CLI `reindex` command) for content authored before Phase 4 or written while Oracle was down; same ports, no LLM calls, no content edits. `Ingestion/EmbeddingText` selects the text to embed per `EMBEDDING_STRATEGY` (BR-034); `Prompts/IngestionPrompts` holds the page-type-specific extraction/reconcile prompts.
- **Shared** — `env/.env` loading and strongly-typed options (`OracleOptions`, `EmbeddingOptions`, `ChatOptions`, `WikiOptions`).
- **Api / Cli** — thin composition roots. The API exposes `/health` (liveness) and `/diagnostics`; the CLI exposes `doctor`. Both run the **same three checks** via `IDiagnosticsService`.

### Stub convention

Adapters not yet needed (`OracleProjectRepository`) are registered in DI but **throw `NotImplementedException` with a "...not implemented until Phase N" message** so accidental use fails loudly. When implementing a later phase, fill the matching stub — don't add a parallel type. Real today: the embedding/chat/Oracle-health diagnostics paths, the file-backed wiki store/repository (Phase 1), the ingestion service (Phase 2), and the Oracle VECTOR + Oracle Text hybrid store (`OracleVectorStore`, Phase 4). Oracle project persistence remains a stub until Phase 3/6.

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