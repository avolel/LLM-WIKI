# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A database-driven LLM wiki: content lives in Oracle, is embedded for semantic search, and is authored/served by LLM agents. This repo is the **Phase 0** skeleton — a buildable, connectable foundation with the canonical layout that feature phases (1–8) drop into. Most adapters are intentional stubs; see [docs/plans/plan-phase-0.md](docs/plans/plan-phase-0.md) and [docs/adr/0001-phase-0-foundations.md](docs/adr/0001-phase-0-foundations.md).

## Commands

```bash
# Infrastructure (Oracle Free 23ai + Ollama) — required for diagnostics to pass
cd docker && docker compose up -d
docker compose exec ollama ollama pull nomic-embed-text   # one-time, 768-dim model
cd ..

# Secrets — env/.env is gitignored; the .NET hosts load it automatically (see DotEnvLoader)
cp env/.env.example env/.env                              # then fill ORACLE_PWD, conn string, a chat key

# .NET (note the .slnx XML solution format from the .NET 10 SDK)
dotnet build LlmWiki.slnx
dotnet test  LlmWiki.slnx
dotnet test  tests/LlmWiki.Domain.Tests/LlmWiki.Domain.Tests.csproj   # single project
dotnet test  LlmWiki.slnx --filter "FullyQualifiedName~HealthEndpoint" # single test/class

# Run the connectivity checks (Oracle round-trip, 768-dim embedding, chat reply)
dotnet run --project src/LlmWiki.Cli -- doctor
dotnet run --project src/LlmWiki.Api          # http://localhost:5080 → GET /health, /diagnostics

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

- **Domain** — pure entities (`WikiPage`, `PageType`). No dependencies.
- **Application** — `Ports/` defines interfaces (`IChatService`, `IEmbeddingService`, `IDatabaseHealthCheck`, `IProjectRepository`, `IVectorStore`, `IWikiFileStore`) and `Diagnostics/` holds the orchestration that the API and CLI both call.
- **Infrastructure** — implements every port. `AddLlmWikiInfrastructure` is the single DI entry point and **owns all SK + Oracle wiring**. Hosts never reference SK directly.
- **Agents** — SK plugins + Process Framework workflows. `AddLlmWikiAgents` is a Phase 0 no-op; `Plugins/`, `Processes/`, `Prompts/` are empty until the agent phases.
- **Shared** — `env/.env` loading and strongly-typed options (`OracleOptions`, `EmbeddingOptions`, `ChatOptions`).
- **Api / Cli** — thin composition roots. The API exposes `/health` (liveness) and `/diagnostics`; the CLI exposes `doctor`. Both run the **same three checks** via `IDiagnosticsService`.

### Stub convention

Phase 0 only proves connectivity. Adapters not yet needed (`OracleProjectRepository`, `OracleVectorStore`, `FileSystemWikiFileStore`) are registered in DI but **throw `NotImplementedException` with a "...not implemented until Phase N" message** so accidental use fails loudly. When implementing a later phase, fill the matching stub — don't add a parallel type. Only the embedding/chat/Oracle-health paths exercised by diagnostics are real.

### Configuration flow

`env/.env` uses flat names (`ORACLE_CONNECTION_STRING`, `EMBEDDING_DIM`, `CHAT_PROVIDER`, ...). `LlmWikiConfiguration` maps these to bindable `Section:Key` paths and registers the typed options; `DotEnvLoader` walks up from the working dir to find `env/.env` and no-ops if absent (CI supplies vars via the environment). The API calls `AddLlmWikiEnv()` on its host builder; the CLI calls `LlmWikiConfiguration.Build()`. To add a new setting: add it to `env/.env.example`, the options class, and the `EnvToConfigKey` map.

### Chat provider switch

`CHAT_PROVIDER=openai` (default) wires the SK OpenAI connector. `CHAT_PROVIDER=anthropic` is a drop-in via Anthropic's OpenAI-compatible endpoint (`https://api.anthropic.com/v1/`) — set `ANTHROPIC_API_KEY` and a Claude `CHAT_MODEL`. If the selected provider's key is missing, DI registers `NotConfiguredChatService` so the host still starts (the chat diagnostic then fails cleanly). See `AddChat` in [DependencyInjection.cs](src/LlmWiki.Infrastructure/DependencyInjection.cs).

## Conventions

- **Central Package Management**: all NuGet versions are pinned in [Directory.Packages.props](Directory.Packages.props) (never put `Version=` in a `.csproj`); shared build settings live in [Directory.Build.props](Directory.Build.props). This deliberately contains Semantic Kernel's API churn — several SK connectors are `-alpha`/`-preview` and their `SKEXPxxxx` warnings are suppressed centrally.
- Target framework `net10.0`, nullable + implicit usings enabled, `LangVersion=latest`.
- Embeddings are **768-dim** (`nomic-embed-text` via Ollama); the diagnostics check asserts this dimension.
- Oracle `VECTOR` columns and Oracle Text indexes are **Phase 4**; [docker/oracle/spike-vector.sql](docker/oracle/spike-vector.sql) is a manual spike to validate them, not wired into the app.
- Never commit secrets — Gitleaks gates CI and `env/.env` is gitignored.

## Plans

Save implementation plans as markdown in [docs/plans/](docs/plans/), following the existing [plan-phase-0.md](docs/plans/plan-phase-0.md) (e.g. `plan-phase-1.md`). Record cross-cutting architecture decisions as ADRs in [docs/adr/](docs/adr/).

## Git Policy

**Claude is NEVER allowed to commit to this repository.**

Claude may stage files and draft a commit message, but must stop there. The human reviews the staged changes and runs `git commit` manually.

## Working agreement
When proposing a plan or a change set, always list every new/updated file and include the full code to be added/changed, so it can be reviewed before implementation.

IWikiFileStore/FileSystemWikiFileStore is now implemented (Phase 1), and IWikiRepository/FileSystemWikiRepository is the new wiki-aware port. Note WIKI_ROOT config and the new YamlDotNet dependency.