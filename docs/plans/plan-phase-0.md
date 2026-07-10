# Plan — Phase 0: Dev Environment & Solution/Folder Structure

## Context

This is the foundational phase of the **Database-Driven LLM Wiki** (see `LLM-Wiki-BRD.md`). The
working directory `/home/avolel/Code/LLM-WIKI` is currently empty. Phase 0 stands up all local
infrastructure and the canonical repository layout so that feature phases (1–8) drop into a known
structure. No feature logic is built here — the goal is a buildable, connectable skeleton.

**Decisions locked with the user:**
- Embeddings: **Ollama `nomic-embed-text` (768-dim)** running as a Docker Compose service.
- Chat: **hosted provider** via Semantic Kernel `IChatCompletionService`, selected by config
  (default wiring = OpenAI connector; Anthropic documented as a drop-in alternative). No key in source.
- **git init + GitHub Actions CI** (build + test for .NET, build/lint for the Expo app).

**Environment verified:** .NET 10.0.108 ✓, Docker 29.5.3 ✓, Node 24.11 ✓, npx/Expo 4.0 ✓,
git 2.43 ✓, 60 GB RAM / 2.4 TB free. Ollama is **not** installed (hence Docker Compose service).

**Acceptance target (BRD Phase 0):** Oracle container runs and accepts a `CREATE TABLE`; embedding
model returns a **768-dim** vector for a test string; a hosted chat call returns a valid response;
`env/.env` is read and **no credentials live in source**; `dotnet build` succeeds across the
solution; the Expo app launches on web + one mobile target; CI runs build + tests.

---

## 1. Repository skeleton

Create the BRD's canonical tree at the repo root:

```
docker/        docker-compose.yml, oracle/ init scripts
env/           .env.example (committed), .env (gitignored)
docs/          move LLM-Wiki-BRD.md here; ADR folder
src/           LlmWiki.{Domain,Application,Infrastructure,Agents,Api,Cli,Shared}
app/           Expo (web + iOS + Android) client
tests/         LlmWiki.{Domain,Application,Infrastructure,Agents,Api}.Tests
.github/workflows/ci.yml
.gitignore, .editorconfig, Directory.Build.props, Directory.Packages.props, README.md
```

## 2. Infrastructure (`docker/`)

**`docker/docker-compose.yml`** — two services + named volumes:
- `oracle`: `container-registry.oracle.com/database/free:latest`, port `1521:1521`,
  `ORACLE_PWD` from env, data volume, init scripts mounted from `docker/oracle/` (executed on first
  boot), healthcheck on the listener.
- `ollama`: `ollama/ollama`, port `11434:11434`, named model volume, healthcheck.
- Document the one-time `ollama pull nomic-embed-text` (run via `docker compose exec ollama ollama
  pull nomic-embed-text`, or an init helper). Note GPU is optional — CPU is fine for Phase 0.

**`docker/oracle/01-init.sql`** — minimal: create the application user/schema + grants so the
acceptance `CREATE TABLE` works. Vector/Oracle Text index setup is deferred to Phase 4, but per risk
**R-02** the plan leaves a clearly marked spike script (`docker/oracle/spike-vector.sql`) to validate
`VECTOR` DML + Oracle Text via ODP.NET before Phase 4 builds the full adapter.

## 3. Secrets & configuration (`env/`)

- **`env/.env.example`** (committed, placeholders only — satisfies NFR-01): `ORACLE_PWD`,
  `ORACLE_CONNECTION_STRING`, `OLLAMA_ENDPOINT` (`http://localhost:11434`), `EMBEDDING_MODEL`
  (`nomic-embed-text`), `EMBEDDING_DIM` (`768`), `CHAT_PROVIDER` (`openai`|`anthropic`),
  `CHAT_MODEL`, `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`.
- **`env/.env`** — created by the user from the example at run time; **gitignored**. (Created during
  execution, not in plan mode.)
- Config binding lives in `LlmWiki.Shared` (strongly-typed options: `OracleOptions`,
  `EmbeddingOptions`, `ChatOptions`). Load `.env` into the process environment at startup (e.g.
  `DotNetEnv`) then bind via `IConfiguration`. **No secrets in source, logs, or VCS.**

## 4. .NET 10 solution (`src/` + `tests/`)

Use `dotnet new sln` + project templates. Layered/clean architecture (NFR-07) — dependency
direction inward; SK/Oracle confined to Infrastructure/Agents:

| Project | Template | References | Phase 0 content |
|---|---|---|---|
| `LlmWiki.Domain` | classlib | — (no external deps) | placeholder entity (e.g. `WikiPage`, `PageType` enum) |
| `LlmWiki.Application` | classlib | Domain | **ports** as interfaces: `IWikiFileStore`, `IVectorStore`, `IEmbeddingService`, `IChatService`, `IProjectRepository` (signatures only) |
| `LlmWiki.Infrastructure` | classlib | Application | subfolders `FileStore/ Persistence/ VectorStore/ Embeddings/`; **stub adapters** implementing the ports; pkgs: `Oracle.ManagedDataAccess.Core`, SK Ollama embeddings connector |
| `LlmWiki.Agents` | classlib | Application | subfolders `Plugins/ Processes/ Prompts/` (empty placeholders); pkgs: `Microsoft.SemanticKernel`, SK Process Framework |
| `LlmWiki.Shared` | classlib | — | config options, logging setup, `.env` loading |
| `LlmWiki.Api` | webapi (minimal) | Application, Infrastructure, Agents, Shared | DI wiring + `/health` + **`/diagnostics`** endpoint (runs the 3 connectivity checks) |
| `LlmWiki.Cli` | console | Application, Infrastructure, Agents, Shared | `System.CommandLine`; a **`doctor`** command mirroring the diagnostics checks |
| `tests/*.Tests` (×5) | xunit | matching src project | one smoke test each so CI has something to run |

- **Central package management:** `Directory.Packages.props` pins **all** versions (mitigates SK API
  churn, **R-04**). `Directory.Build.props` sets `net10.0`, `Nullable=enable`, `ImplicitUsings`.
  Exact SK/ODP.NET package versions to be confirmed against NuGet during implementation.
- **DI wiring (`LlmWiki.Api/Program.cs`):** register from config — SK `Kernel`, Ollama embedding
  connector, hosted chat connector (OpenAI default / Anthropic alt), Oracle store adapter (stub),
  file store (stub). Infrastructure adapters bind to Application ports (NFR-03, NFR-07).

**Acceptance helper — `/diagnostics` endpoint + CLI `doctor`** (directly exercises Phase 0
acceptance): (1) Oracle connect + `CREATE TABLE`/drop round-trip; (2) embed a test string and assert
length == 768; (3) hosted chat round-trip returns non-empty text. Reports pass/fail per check.

## 5. Expo client (`app/`)

- `npx create-expo-app` configured for **web + iOS + Android**; enable web (`expo start --web`).
- `src/` subfolders per BRD: `api/` (typed client to `LlmWiki.Api` — base URL from config, a typed
  `getDiagnostics()`/`health` call to prove connectivity), `components/`, `screens/` (Chat, Browse,
  Projects placeholders), `navigation/`, `state/`, `hooks/`, `theme/`.
- Phase 0 scope = launches and reaches the API; full chat UI is Phase 8.

## 6. Version control & CI

- `git init`; `.gitignore` covering .NET (`bin/`, `obj/`), Node (`node_modules/`, Expo `.expo/`),
  and **`env/.env`** (never the real env file).
- **`.github/workflows/ci.yml`:** job 1 — `dotnet restore/build/test` on .NET 10; job 2 — Node
  setup + `app/` install + `expo export`/lint. Secret-scanning note per **R-06**.

---

## Critical files to create

- `docker/docker-compose.yml`, `docker/oracle/01-init.sql`, `docker/oracle/spike-vector.sql`
- `env/.env.example`
- `LlmWiki.sln`, `Directory.Build.props`, `Directory.Packages.props`
- `src/LlmWiki.Application/Ports/*.cs` (port interfaces)
- `src/LlmWiki.Infrastructure/{FileStore,Persistence,VectorStore,Embeddings}/*.cs` (stub adapters)
- `src/LlmWiki.Shared/Configuration/*.cs` (options + `.env` loader)
- `src/LlmWiki.Api/Program.cs` (DI + `/health`, `/diagnostics`)
- `src/LlmWiki.Cli/Program.cs` (`doctor` command)
- `app/` (Expo scaffold) + `app/src/api/client.ts`
- `.gitignore`, `.github/workflows/ci.yml`, `README.md`

## Verification (end-to-end)

1. `cd docker && docker compose up -d` → Oracle + Ollama healthy.
2. `docker compose exec ollama ollama pull nomic-embed-text`.
3. Copy `env/.env.example` → `env/.env`, fill `ORACLE_PWD`, connection string, and a hosted chat key.
4. `dotnet build LlmWiki.sln` → succeeds; `dotnet test` → all smoke tests green.
5. Run `dotnet run --project src/LlmWiki.Cli -- doctor` (or `GET /diagnostics`) → **all three checks
   pass**: Oracle CREATE TABLE, 768-dim embedding, hosted chat reply.
6. `cd app && npx expo start --web` → app loads in browser; verify one mobile target (Expo Go).
7. `git status` confirms `env/.env` is **not** tracked; push triggers CI build + test green.
8. Grep the repo for secrets → only placeholders in `env/.env.example`.

## Notes / deferrals

- Oracle `VECTOR` + Oracle Text indexes are **Phase 4**; Phase 0 only proves basic connectivity and
  leaves the marked spike script (R-02).
- Adapter bodies are stubs that throw `NotImplementedException` (except the embedding/chat/Oracle
  connectivity paths needed for diagnostics); real implementations land in their respective phases.
