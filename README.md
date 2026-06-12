# Database-Driven LLM Wiki

A wiki whose content is stored in Oracle, embedded for semantic search, and authored/served with
the help of LLM agents. **Phase 0** established the buildable, connectable foundation and the
canonical layout that feature phases (1–8) drop into; **Phase 1** has landed a real, file-backed
wiki — typed directories, YAML frontmatter, cross-references, and a CLI to scaffold and inspect
wikis. Oracle and embeddings remain the diagnostics path until the persistence phases. See
[docs/plans/plan-phase-0.md](docs/plans/plan-phase-0.md),
[docs/plans/plan-phase-1.md](docs/plans/plan-phase-1.md), and the foundational decisions in
[docs/adr/0001-phase-0-foundations.md](docs/adr/0001-phase-0-foundations.md).

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
docker compose exec ollama ollama pull nomic-embed-text   # one-time, 768-dim model
cd ..

# 2. Configure secrets (never committed)
cp env/.env.example env/.env
#   then edit env/.env: set ORACLE_PWD, the connection string, and a chat API key

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
`Summary`, `Entity`, `Concept`, `Overview`. Cross-references use either `[[Wikilink]]` or markdown
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

## Configuration

All secrets live in `env/.env` (gitignored); `env/.env.example` documents every key with
placeholders. The .NET hosts load `env/.env` at startup and bind strongly-typed options
(`OracleOptions`, `EmbeddingOptions`, `ChatOptions`, `WikiOptions`) in `LlmWiki.Shared`. The
file-backed wiki lives under `WIKI_ROOT` (default `wiki/`). The Expo app reads
`EXPO_PUBLIC_API_BASE_URL` (see `app/.env.example`).

### Chat provider

`CHAT_PROVIDER=openai` (default) wires the Semantic Kernel OpenAI connector. `CHAT_PROVIDER=anthropic`
is a drop-in alternative wired via Anthropic's OpenAI-compatible endpoint — set `ANTHROPIC_API_KEY`
and a Claude `CHAT_MODEL`. No keys are ever stored in source.

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

Oracle `VECTOR` columns and Oracle Text indexes are **Phase 4**; the wiki currently persists to the
local file store, not the database. `docker/oracle/spike-vector.sql` is a manual spike (R-02) to
validate `VECTOR` DML + Oracle Text before the Phase 4 adapter is built. The Oracle persistence
adapters (`OracleProjectRepository`, `OracleVectorStore`) remain stubs that throw
`NotImplementedException`; the embedding/chat/Oracle-health paths exercised by diagnostics and the
file-backed wiki store/repository are real.

> Note: the .NET 10 SDK emits a solution as `LlmWiki.slnx` (XML solution format). Use
> `dotnet build LlmWiki.slnx` (or just `dotnet build`).
