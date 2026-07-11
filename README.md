# Database-Driven LLM Wiki

A wiki whose content is stored in Oracle, embedded for semantic search, and authored/served with
the help of LLM agents. **Phase 0** established the buildable, connectable foundation and the
canonical layout that feature phases (1–8) drop into; **Phase 1** landed a real, file-backed
wiki — typed directories, YAML frontmatter, cross-references, and a CLI to scaffold and inspect
wikis; **Phase 2** makes the wiki *grow from sources* — drop a source into a wiki's `raw/` and an
LLM agent extracts entities/concepts, writes a summary, creates/updates entity, concept and topic
pages, and notes contradictions; **Phase 3** adds the agent-owned journal — every ingest
regenerates an `index.md` catalogue and appends to a `log.md` history; and **Phase 4** makes
the wiki *searchable* — each changed page is embedded into Oracle (768-dim `VECTOR` + Oracle Text)
and a CLI `search` command ranks pages by a hybrid of semantic similarity and full-text matching;
and **Phase 5** makes the wiki *answer questions* — an `ask` command (one-shot or an interactive
follow-up REPL) and a `POST /query` HTTP endpoint read the index, run hybrid search, read the top
pages, and synthesise a grounded, **cited** answer that honestly reports gaps — and can save a good
answer back as a new, itself-indexed `Answer` page; and **Phase 6** adds a first-class **project
registry** — an Oracle-persisted `wiki_project` table of projects + metadata (created / last-ingest /
page & source counts), a `project` CLI group (`create`/`list`/`select`) and `GET`/`POST /projects`
API, and a persisted "active project" so `ingest`/`search`/`ask` default to it when you omit the wiki
name (a project *is* a wiki — isolation is unchanged, this adds the durable registry on top).
See
[docs/plans/plan-phase-0.md](docs/plans/plan-phase-0.md),
[docs/plans/plan-phase-1.md](docs/plans/plan-phase-1.md),
[docs/plans/plan-phase-2.md](docs/plans/plan-phase-2.md),
[docs/plans/plan-phase-3.md](docs/plans/plan-phase-3.md),
[docs/plans/plan-phase-4.md](docs/plans/plan-phase-4.md),
[docs/plans/plan-phase-5.md](docs/plans/plan-phase-5.md),
[docs/plans/plan-phase-6.md](docs/plans/plan-phase-6.md), a plain-English
[code overview for developers](docs/code-overview/code-overview.md), and the architecture decisions in
[docs/adr/0001-phase-0-foundations.md](docs/adr/0001-phase-0-foundations.md) and
[docs/adr/0002-phase-6-project-registry.md](docs/adr/0002-phase-6-project-registry.md).

## Layout

```
docker/   docker-compose.yml + Oracle init/spike scripts
env/      .env.example (committed) → .env (gitignored)
docs/     plans + architecture decision records
src/      LlmWiki.{Domain,Application,Infrastructure,Agents,Shared,Api,Cli}
app/      Expo client (web + iOS + Android)
tests/    LlmWiki.{Domain,Application,Infrastructure,Agents,Api}.Tests
```

### Architecture (clean / layered, NFR-07)

Dependencies point inward. Semantic Kernel and Oracle are confined to `Infrastructure`/`Agents`.

```
Domain  ←  Application (ports)  ←  Infrastructure (adapters: Oracle, Ollama, OpenAI/Anthropic)
                   ↑                         ↑
                 Agents                    Shared (config + .env loading)
                   ↑                         ↑
                       Api / Cli  (composition roots)
```

## Prerequisites

.NET 10 SDK · Docker + Docker Compose · Node 24 · git. Ollama runs **as a Docker service** — no
local install needed.

## Quick start

```bash
# 1. Bring up infrastructure
cd docker && docker compose up -d            # Oracle Free + Ollama
docker compose exec ollama ollama pull nomic-embed-text   # one-time, 768-dim embedding model
docker compose exec ollama ollama pull llama3.1           # one-time chat model (for CHAT_PROVIDER=ollama)
cd ..

# 2. Configure secrets (never committed)
cp env/.env.example env/.env
#   then edit env/.env: set ORACLE_PWD, the connection string, and a chat provider.
#   For a keyless local setup: CHAT_PROVIDER=ollama, CHAT_MODEL=llama3.1

# 3. Build + test the .NET solution
dotnet build LlmWiki.slnx
dotnet test  LlmWiki.slnx

# 4. Run the Phase 0 acceptance checks (Oracle CREATE TABLE, 768-dim embedding, chat reply)
dotnet run --project src/LlmWiki.Cli -- doctor
#   or start the API and hit its endpoints:
dotnet run --project src/LlmWiki.Api          # http://localhost:5080
#   curl http://localhost:5080/health         # liveness probe
#   curl http://localhost:5080/diagnostics    # the three checks

# 5. Run the Expo client
cd app && npm install && npm run web          # also: npm run ios / npm run android
npm run lint                                  # typecheck (tsc --noEmit; there is no ESLint)
npm run export:web                            # build the web bundle (CI gate)
```

## Wiki (Phase 1)

Wikis are plain directories under `WIKI_ROOT` (default `wiki/`). Each wiki has fixed typed
directories — `summaries/`, `entities/`, `topics/`, `raw/` (BR-001) — and a `SCHEMA.md` that records
its conventions (link style + frontmatter field set, BR-002/005). Pages are markdown with YAML
frontmatter (`title`, `type`, `created`, `updated`, `tags`, `sources`, BR-003); `type` is one of
`Summary`, `Entity`, `Concept`, `Overview`, `Answer` (the last for saved query answers — Phase 5).
Cross-references use either `[[Wikilink]]` or markdown
`[text](path.md)` style per the wiki's schema (BR-004), and can be resolved to flag broken links.

The `LlmWiki.Cli` exposes this surface:

```bash
# Scaffold / inspect wikis
dotnet run --project src/LlmWiki.Cli -- wiki create my-wiki --link-style Wikilink
dotnet run --project src/LlmWiki.Cli -- wiki list
dotnet run --project src/LlmWiki.Cli -- wiki inspect my-wiki        # schema + page list

# Author / read pages (frontmatter is written for you)
dotnet run --project src/LlmWiki.Cli -- wiki page add my-wiki entities/acme-corp.md \
    --title "Acme Corp" --type Entity --tag company --tag customer --body "..."
dotnet run --project src/LlmWiki.Cli -- wiki page show my-wiki entities/acme-corp.md   # prints + resolves links
```

`--body-file` reads the page body from a file; `--tag`/`--source` are repeatable.

## Ingestion (Phase 2)

`ingest` grows a wiki from a source (BR-010…BR-016). It copies the file into the wiki's immutable
`raw/` directory (write-once; never modified — NFR-02), then makes a single structured LLM call to
extract a summary, key points, entities, concepts and an overarching topic — **only** facts present
in the source (no invention). From that it writes a summary page, creates or updates entity, concept
and topic pages (appending provenance to existing pages rather than overwriting), stubs thin/
mentioned-in-passing items, and runs a light contradiction pass against existing pages it matched by
name — noting any conflict inline instead of overwriting. Each page write is an independent boundary,
so a single failure is recorded, not fatal (NFR-06); the run returns a structured `IngestionReport`
the CLI prints. As its **final steps** it updates the journal (Phase 3) and embeds the pages it
changed (Phase 4).

```bash
dotnet run --project src/LlmWiki.Cli -- wiki create demo
dotnet run --project src/LlmWiki.Cli -- ingest demo ./docs/sample-source.md
#   → prints per-page outcomes: summaries/…, entities/…, topics/…, plus any contradictions/gaps
dotnet run --project src/LlmWiki.Cli -- wiki inspect demo   # see the new pages on disk
```

Ingestion needs a working chat provider (see below) — with a local Ollama model it runs fully
offline. The journal step is file-only; the embedding step is **best-effort** — if Oracle/Ollama
are down the pages are still written and the failure is recorded, not thrown (NFR-06).

## Journal — index & log (Phase 3)

Each wiki carries two **agent-owned** files at its root, next to `SCHEMA.md`, maintained as the
final step of every ingest (BR-020…024):

- **`index.md`** — a catalogue of every content page, grouped into Sources / Entities / Concepts /
  Overviews, each entry a link + a one-line summary + `(created; N source(s))`. It is
  **regenerated deterministically** from disk on each ingest — stably sorted with no timestamp — so
  git diffs stay clean and a deleted page's entry simply disappears on the next rebuild (BR-024).
- **`log.md`** — an append-only history; each run adds a greppable
  `## [YYYY-MM-DD] ingest | <source>` heading with a bullet summary of what changed (created /
  updated / stub / failed counts, plus contradictions and gaps). A partial/failed ingest is logged
  too.

These two files are not content pages: they're excluded from `wiki inspect`, page counts, the
catalogue itself, and link resolution. View them by opening the files (there are no dedicated CLI
view commands). A journal failure is recorded on the report as a `Failed` outcome, never fatal
(NFR-06).

## Search (Phase 4)

Ingestion embeds **only the pages a run changed** — the ones the `IngestionReport` marked
created/updated/stubbed, plus any page that got a contradiction note (BR-033) — and upserts each
into Oracle: one `wiki_page` row per page with a 768-dim `VECTOR(768, FLOAT32)` embedding, an
Oracle Text index over the body, and metadata (title, type, tags, snippet). The `search` command
then answers **hybrid** queries within a wiki:

```bash
# Paraphrase → semantic (vector) arm finds the right page even when words don't match a title
dotnet run --project src/LlmWiki.Cli -- search demo "how the thing actually works" --top-k 5

# Exact name/term → full-text (Oracle Text) arm returns its page; optional page-type filter
dotnet run --project src/LlmWiki.Cli -- search demo "AcmeCorp" --type entity
```

Both arms run and their rankings are combined by **reciprocal-rank fusion**, so semantic recall and
exact-term precision reinforce each other. Searches never cross wikis (a `wiki_name` predicate scopes
every query — NFR-10). What text of a page is embedded is configurable via `EMBEDDING_STRATEGY`
(`TitleAndBody` default, `FullText`, or `Summary` — BR-034). Search needs Oracle up and the embedding
model pulled; the `wiki_page` schema is created automatically on first use (canonical DDL:
[docker/oracle/02-schema.sql](docker/oracle/02-schema.sql)).

### Backfilling existing wikis — `reindex`

Ingestion embeds only the pages a run **changes**, so pages authored before Phase 4 — or written
during a run when Oracle was unreachable — are on disk but absent from `wiki_page`, and `search`
returns "no matches" for them. `reindex` fixes that: it walks every current page of a wiki and
embeds + upserts it, with **no LLM calls and no edits to your content** (it only rebuilds the derived
search index). Run it once after enabling search on an existing wiki:

```bash
dotnet run --project src/LlmWiki.Cli -- reindex demo
#   → "Reindexed 'demo': N embedded, 0 failed."
dotnet run --project src/LlmWiki.Cli -- search demo "your query"
```

Like the ingest embed-step it is best-effort per page (a page that fails to embed is reported, not
fatal — NFR-06), and `UpsertAsync` is keyed by `(wiki_name, path)` so re-running is idempotent.

## Query & synthesis (Phase 5)

Search returns *pages*; `ask` **answers a question**. It reads the wiki's `index.md`, runs the same
hybrid search to pick the top candidate pages, reads them in full, and makes a single LLM call to
synthesise a grounded answer — choosing its own format (prose, a table for comparisons, a list for
timelines — BR-043) and citing the specific pages it used by path (BR-041). If the corpus doesn't
cover the question it says so plainly instead of speculating (BR-042). Only citations that were
actually retrieved survive, so every "Sources:" line opens to a real page.

```bash
# One-shot: answer and exit
dotnet run --project src/LlmWiki.Cli -- ask demo "how does the thing work?" --top-k 5 --type entity

# Interactive REPL (omit the question): follow-ups keep conversation history in-process (BR-044)
dotnet run --project src/LlmWiki.Cli -- ask demo
#   > how does the thing work?          → a cited answer, then a "Sources:" list
#   > and how does it relate to X?      → pronoun follow-up; prior turns are carried into the prompt
#   > :save                             → persists the last answer as answers/<slug>.md (BR-045)
#   > :quit
```

**Saving an answer** (`:save`, or `POST /query` with `"save": true`) writes a new `Answer` page under
`answers/`, regenerates `index.md` (the new page appears under an **Answers** heading), appends a
greppable `## [YYYY-MM-DD] query | <question>` line to `log.md`, and **best-effort** embeds the page
so it's itself searchable next time — the compounding loop the product is built around. The embed is
best-effort: if Oracle is down the page is still saved and the failure is recorded, not thrown (NFR-06).
An uncovered answer prints a clear `⚠  not covered by this wiki` banner and is never saved.

The same workflow is HTTP-reachable via a `POST /query` controller — the API's first MVC controller,
with **Swagger UI** for interactive testing:

```bash
dotnet run --project src/LlmWiki.Api            # http://localhost:5080
#   browse http://localhost:5080/swagger and invoke POST /query, or:
curl -s localhost:5080/query -H 'content-type: application/json' \
     -d '{"wiki":"demo","question":"how does the thing work?"}'
#   → { "answer": "…", "covered": true, "citations": [ … ], "suggestedTitle": "…" }
#   a missing wiki → 404. History carries follow-up context over HTTP; "save": true persists a covered answer.
```

`ask`/`/query` need a working chat provider and Oracle with embedded pages to answer from (run
`ingest`, or `reindex` an older wiki, first). `/health` and `/diagnostics` stay minimal-API — the
query surface is a deliberate first use of MVC controllers + Swagger in this codebase.

## Projects (Phase 6)

A **project is a wiki** — the same named directory under `WIKI_ROOT`, already isolated per tenant by
a `wiki_name` predicate on every search (NFR-10). Phase 6 adds a first-class, Oracle-persisted
*registry* on top: a `wiki_project` table holding each project's `name`, `created_at`,
`last_ingest_at`, and recomputed page/source counts (BR-050…053). The `project` CLI group manages it:

```bash
# Create: scaffolds the wiki AND registers it in Oracle, then makes it the active project
dotnet run --project src/LlmWiki.Cli -- project create ml-papers --link-style Wikilink

# List: reads the Oracle registry; the active project is marked with *
dotnet run --project src/LlmWiki.Cli -- project list
#   * ml-papers   created 2026-07-11  last-ingest 2026-07-11  5 page(s)  1 source(s)
#     trains      created 2026-07-11  last-ingest never       0 page(s)  0 source(s)

# Select: persists the active project (a host-local {WIKI_ROOT}/.current-project pointer)
dotnet run --project src/LlmWiki.Cli -- project select ml-papers
```

Once a project is selected, `ingest`/`search`/`ask` **default to it** when you omit the wiki name —
`search "anvils"` runs against the active project, while `search other-wiki "anvils"` still targets an
explicit one:

```bash
dotnet run --project src/LlmWiki.Cli -- ingest ./notes.md        # into the active project
dotnet run --project src/LlmWiki.Cli -- search "anvils"          # active project
dotnet run --project src/LlmWiki.Cli -- ask                      # REPL on the active project
```

The registry is kept current **best-effort** on each ingest (a final step stamps `last_ingest_at` and
the counts); if Oracle is down the wiki on disk is still written and the failure is recorded, never
thrown (NFR-06) — files stay canonical, Oracle stays derived. The active-project pointer is host-local
(a dotfile, not Oracle), so `project select` works offline. The registry is also HTTP-reachable:
`GET /projects`, `GET /projects/{name}`, and `POST /projects {"name":"…"}` (scaffold + register → 201;
duplicate → 409) via a `ProjectController` in Swagger. `select` has no endpoint — the active-project
pointer is a CLI-local concept, since the API takes the project name per request. The `wiki_project`
schema is created automatically on first use (canonical DDL:
[docker/oracle/03-schema.sql](docker/oracle/03-schema.sql)). The file-only `wiki create` command is
unchanged for scaffolding a wiki without registering a project.

## Configuration

All secrets live in `env/.env` (gitignored); `env/.env.example` documents every key with
placeholders. The .NET hosts load `env/.env` at startup and bind strongly-typed options
(`OracleOptions`, `EmbeddingOptions`, `ChatOptions`, `WikiOptions`) in `LlmWiki.Shared`. The
file-backed wiki lives under `WIKI_ROOT` (default `wiki/`); `ORACLE_CONNECTION_STRING` points the
Phase 4 vector store at Oracle; `EMBEDDING_STRATEGY` (`TitleAndBody` | `FullText` | `Summary`)
chooses what page text is embedded. The Expo app reads `EXPO_PUBLIC_API_BASE_URL` (see
`app/.env.example`).

### Chat provider

`CHAT_PROVIDER` selects the Semantic Kernel connector wiring:

- **`openai`** (default) — the SK OpenAI connector; set `OPENAI_API_KEY` and a `CHAT_MODEL`.
- **`anthropic`** — a drop-in via Anthropic's OpenAI-compatible endpoint; set `ANTHROPIC_API_KEY`
  and a Claude `CHAT_MODEL`.
- **`ollama`** — a **local, no-key** option via Ollama's OpenAI-compatible endpoint. Set
  `CHAT_MODEL` to a pulled chat model (e.g. `llama3.1`) and leave `CHAT_ENDPOINT` blank to use the
  local server (`http://localhost:11434/v1/`). Pull the model once with
  `docker compose exec ollama ollama pull llama3.1`. Good for offline/free ingestion; extraction
  quality is lower than a hosted model.

No keys are ever stored in source.

## Diagnostics

Both the API `/diagnostics` endpoint and the CLI `doctor` command run the same three checks and
report pass/fail per check:

1. **Oracle** — connect and run a `CREATE TABLE` / `DROP` round-trip.
2. **Embedding** — embed a probe string and assert the vector length is 768.
3. **Chat** — a hosted chat round-trip returns non-empty text.

The API also exposes `GET /health`, a dependency-free liveness probe that returns `{ "status": "ok" }`.

## Continuous integration

[`.github/workflows/ci.yml`](.github/workflows/ci.yml) runs three jobs on every push:

1. **.NET build + test** — `dotnet build`/`dotnet test` in Release.
2. **Expo app build + lint** — `npm run lint` (typecheck) and `npm run export:web`.
3. **Secret scan** — Gitleaks fails the build on any committed credential.

## Deferrals

Wiki content is authored to the **file store** (`WIKI_ROOT`), which remains the source of truth;
Oracle holds **derived** state — a search index (`wiki_page`: embeddings + Oracle Text) written on
ingest (Phase 4), and the project registry (`wiki_project`: metadata) written best-effort on ingest
and by the `project` commands (Phase 6). `docker/oracle/spike-vector.sql` was the manual spike (R-02)
that validated `VECTOR` DML + Oracle Text before the real `OracleVectorStore` adapter was built. No
application-adapter stubs remain — `OracleProjectRepository` is filled (Phase 6). Phase 5 reads the
index into query context; carrying the log into session context (BR-023), token **streaming** to the
client, and the React Native chat UI remain Phase 8; linting / a health-check surface are Phase 7.
Everything else is real: the embedding/chat/Oracle-health diagnostics paths, the file-backed wiki
store/repository, the journal, the hybrid vector store, the query/synthesis workflow (`ask` REPL +
`POST /query`), and the project registry (`project` CLI + `/projects` API).

> Note: the .NET 10 SDK emits a solution as `LlmWiki.slnx` (XML solution format). Use
> `dotnet build LlmWiki.slnx` (or just `dotnet build`).
