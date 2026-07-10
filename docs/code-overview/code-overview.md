# Code Overview

A plain-English tour of the LLM Wiki codebase for developers who are new to it but comfortable
with C#/.NET and clean architecture. It explains **what the system does, how the pieces fit,
and how the main flows work end to end** — enough to find your way around and make a change with
confidence. For the "why" behind individual phases, see the [plans](plans/) and the
[ADR](adr/0001-phase-0-foundations.md).

---

## 1. The big idea

The product is a **wiki that grows itself from source documents**. You drop a document into a
wiki, and an LLM agent reads it, extracts the entities/concepts/topics, writes and cross-links
markdown pages, keeps a catalogue and a changelog, and makes everything searchable by meaning and
by keyword.

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
| **`LlmWiki.Agents`** | LLM agent orchestration. Today: the ingestion pipeline, written against ports only (no SK types) so it stays unit-testable. | Application, Shared |
| **`LlmWiki.Shared`** | Cross-cutting config: `env/.env` loading + strongly-typed options. A leaf with no project deps. | nothing |
| **`LlmWiki.Api`** | ASP.NET minimal-API host. A thin **composition root**. | Infrastructure, Agents, Shared |
| **`LlmWiki.Cli`** | Command-line host (`doctor`, `wiki`, `ingest`, `search`). The other composition root. | Infrastructure, Agents, Shared |

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
| [`IWikiFileStore`](../src/LlmWiki.Application/Ports/IWikiFileStore.cs) | `FileSystemWikiFileStore` | Raw read/write/list of files under `WIKI_ROOT`. |
| [`IWikiRepository`](../src/LlmWiki.Application/Ports/IWikiRepository.cs) | `FileSystemWikiRepository` | Wiki-aware operations: scaffold wikis, read/write pages with frontmatter, list, resolve links. |
| [`IWikiJournal`](../src/LlmWiki.Application/Ports/IWikiJournal.cs) | `FileSystemWikiJournal` | Maintains `index.md` (regenerated) and `log.md` (append-only). |
| [`IChatService`](../src/LlmWiki.Application/Ports/IChatService.cs) | `SemanticKernelChatService` / `NotConfiguredChatService` | One-shot LLM completion, optional JSON mode. |
| [`IEmbeddingService`](../src/LlmWiki.Application/Ports/IEmbeddingService.cs) | `OllamaEmbeddingService` | Turn text into a 768-dim vector. |
| [`IVectorStore`](../src/LlmWiki.Application/Ports/IVectorStore.cs) | `OracleVectorStore` | Upsert page embeddings + hybrid (vector + full-text) search in Oracle. |
| [`IDatabaseHealthCheck`](../src/LlmWiki.Application/Ports/IDatabaseHealthCheck.cs) | `OracleDatabaseHealthCheck` | Connectivity probe (CREATE TABLE round-trip). |
| [`IProjectRepository`](../src/LlmWiki.Application/Ports/IProjectRepository.cs) | `OracleProjectRepository` **(stub)** | Project/tenant persistence — throws until Phase 6. |
| [`IIngestionService`](../src/LlmWiki.Application/Ingestion/IIngestionService.cs) | `IngestionService` (Agents) | The whole ingest pipeline. |

---

## 3. The storage model

### A wiki on disk

Under `WIKI_ROOT` (default `wiki/`), each wiki is a directory:

```
wiki/
  demo/
    SCHEMA.md              # the wiki's conventions (link style + frontmatter fields)
    index.md              # agent-owned catalogue (regenerated every ingest)     ── Phase 3
    log.md                # agent-owned append-only changelog                    ── Phase 3
    summaries/            # one page per ingested source (PageType.Summary)
    entities/             # people/orgs/things (PageType.Entity)
    topics/              # overarching topic overviews (PageType.Overview)
    raw/                 # immutable copies of the original sources (write-once, NFR-02)
```

- The four typed directories (`summaries`, `entities`, `topics`, `raw`) are fixed for every wiki —
  see [`WikiSchema.Directories`](../src/LlmWiki.Domain/WikiSchema.cs). (Concept pages live under
  `concepts/`, created on demand.)
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
[docker/oracle/02-schema.sql](../docker/oracle/02-schema.sql).

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

---

## 4. The Domain layer (pure building blocks)

Everything here is deterministic and dependency-free — trivial to unit-test.

- [`WikiPage`](../src/LlmWiki.Domain/WikiPage.cs) — the page record: title, `PageType`, content,
  tags, sources, timestamps. An immutable `record` (use `with` to edit).
- [`PageType`](../src/LlmWiki.Domain/PageType.cs) — `Summary | Entity | Concept | Overview`.
- [`WikiSchema`](../src/LlmWiki.Domain/WikiSchema.cs) / [`LinkStyle`](../src/LlmWiki.Domain/LinkStyle.cs)
  — the per-wiki conventions.
- [`Slug`](../src/LlmWiki.Domain/Slug.cs) — `"Acme Corp" → "acme-corp"`. The single source of truth
  for turning a title into a filename, so the repository and the agent produce **matching** paths.
- [`CrossReference`](../src/LlmWiki.Domain/CrossReference.cs) — parses links out of a body
  (`CrossReferenceParser`, regex-based, per link style) and models link-resolution results;
  [`CrossReferenceWriter`](../src/LlmWiki.Domain/CrossReferenceWriter.cs) is the write-side mirror
  (render a link in the right style).
- [`IndexRenderer`](../src/LlmWiki.Domain/IndexRenderer.cs) + `IndexEntry` — render `index.md`:
  fixed section order (Sources / Entities / Concepts / Overviews), empty sections omitted, entries
  **stably sorted by path** so the file is deterministic (clean git diffs; a deleted page's line
  just vanishes on the next rebuild — BR-024).
- [`LogEntry` + `LogFormatter`](../src/LlmWiki.Domain/LogEntry.cs) — format one greppable
  `## [YYYY-MM-DD] ingest | <source>` log block.

The pattern to notice: **pure rendering/parsing logic lives in Domain; the file I/O that feeds it
lives in Infrastructure.** `FileSystemWikiJournal` reads pages off disk and hands plain records to
`IndexRenderer`/`LogFormatter`.

---

## 5. Configuration & startup

Config is deliberately boring and centralized. Flow:

1. **`env/.env`** (gitignored; template is `env/.env.example`) holds flat keys like
   `ORACLE_CONNECTION_STRING`, `CHAT_PROVIDER`, `EMBEDDING_STRATEGY`.
2. [`DotEnvLoader`](../src/LlmWiki.Shared/Configuration/DotEnvLoader.cs) walks up from the working
   directory to find `env/.env` and loads it into process environment variables (no-ops if absent,
   so CI can supply vars directly).
3. [`LlmWikiConfiguration`](../src/LlmWiki.Shared/Configuration/LlmWikiConfiguration.cs) maps each
   flat name to a bindable `Section:Key` path (its `EnvToConfigKey` dictionary) and registers the
   strongly-typed options objects.
4. Options classes ([`OracleOptions`](../src/LlmWiki.Shared/Configuration/OracleOptions.cs),
   [`EmbeddingOptions`](../src/LlmWiki.Shared/Configuration/EmbeddingOptions.cs),
   [`ChatOptions`](../src/LlmWiki.Shared/Configuration/ChatOptions.cs),
   [`WikiOptions`](../src/LlmWiki.Shared/Configuration/WikiOptions.cs)) are injected as
   `IOptions<T>`.

The API host calls `builder.Configuration.AddLlmWikiEnv()`; the CLI calls
`LlmWikiConfiguration.Build()`. Both then call
[`AddLlmWikiInfrastructure`](../src/LlmWiki.Infrastructure/DependencyInjection.cs) (wires every
adapter + owns SK/Oracle) and `AddLlmWikiAgents` (wires the ingestion service).

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
[`DiagnosticsService`](../src/LlmWiki.Application/Diagnostics/DiagnosticsService.cs), which runs
three **independent** checks (one failing never masks the others):

1. **Oracle** — connect and do a `CREATE TABLE` / `DROP` round-trip.
2. **Embedding** — embed a probe string and assert the vector length is exactly 768.
3. **Chat** — a chat round-trip returns non-empty text.

The API returns `200` if all pass, `503` otherwise; the CLI prints a PASS/FAIL table.
`GET /health` is a separate, dependency-free liveness probe.

### 6b. Create a wiki — `wiki create <name>`

[`FileSystemWikiRepository.CreateWikiAsync`](../src/LlmWiki.Infrastructure/FileStore/FileSystemWikiRepository.cs)
refuses if the wiki exists, then writes a `.gitkeep` into each typed directory and renders
`SCHEMA.md`. That's it — a wiki is a directory with a schema file.

### 6c. Ingest a source — `ingest <wiki> <file>` (Phases 2–4 together)

This is the heart of the system. The CLI copies the file into `raw/` (write-once), then calls
[`IngestionService.IngestAsync`](../src/LlmWiki.Agents/Ingestion/IngestionService.cs). Steps:

1. **Load context** — read the wiki's `SCHEMA.md` and the list of existing pages.
2. **Extract (LLM call #1)** — one structured, JSON-mode call
   ([`IngestionPrompts.Extract`](../src/LlmWiki.Agents/Prompts/IngestionPrompts.cs)) turns the raw
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
   [`EmbeddingText`](../src/LlmWiki.Agents/Ingestion/EmbeddingText.cs).

**Two things to internalize about this pipeline:**

- **Every side effect is a boundary, and steps 8–9 are best-effort.** A single page write, the
  journal, or the embedding step can fail without taking the run down — the failure is recorded as a
  `Failed` outcome on the report and the rest proceeds. This is NFR-06: *the wiki is never left
  corrupt, and file-only ingestion keeps working even if Oracle/Ollama are down.*
- **The change set is free.** We don't hash content or add a "dirty" frontmatter field to decide
  what to re-embed — the `IngestionReport` already knows exactly which pages changed. (One subtlety
  the code calls out: contradiction notes are written *outside* the tracked outcomes list, so they
  are unioned back in.)

### 6d. Search — `search <wiki> <query> [--top-k] [--type]` (Phase 4)

1. The CLI embeds the query text with the same `IEmbeddingService` used at ingest time.
2. [`OracleVectorStore.SearchAsync`](../src/LlmWiki.Infrastructure/VectorStore/OracleVectorStore.cs)
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
Phase 4 did to `OracleVectorStore`) — you don't add a parallel type. Today the only remaining stub
is `OracleProjectRepository` (Phase 6).

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
| 5 | Query/answering (read the index into query context, the log into session context) | ⏳ next |
| 6 | Project/tenant persistence (`OracleProjectRepository`) | 🔲 stub |
| 8 | React Native / Expo UI | 🔲 skeleton in `app/` |

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

Run everything with `dotnet test LlmWiki.slnx`; scope to one project or filter by name as shown in
[CLAUDE.md](../CLAUDE.md).

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
```

**API** — `GET /health` (liveness), `GET /diagnostics` (the three checks; 200/503).

**Infra** — `cd docker && docker compose up -d`, then pull the models
(`ollama pull nomic-embed-text`, `ollama pull llama3.1`).
