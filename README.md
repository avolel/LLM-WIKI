# Database-Driven LLM Wiki

A wiki whose content is stored in Oracle, embedded for semantic search, and authored/served with
the help of LLM agents. This repository is the **Phase 0** skeleton: buildable, connectable
infrastructure and the canonical layout that feature phases (1–8) drop into. See
[docs/plans/plan-phase-0.md](docs/plans/plan-phase-0.md).

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
#   or start the API and GET /diagnostics:
dotnet run --project src/LlmWiki.Api          # http://localhost:5080
#   curl http://localhost:5080/diagnostics

# 5. Run the Expo client
cd app && npm install && npm run web          # also: npm run ios / npm run android
```

## Configuration

All secrets live in `env/.env` (gitignored); `env/.env.example` documents every key with
placeholders. The .NET hosts load `env/.env` at startup and bind strongly-typed options
(`OracleOptions`, `EmbeddingOptions`, `ChatOptions`) in `LlmWiki.Shared`. The Expo app reads
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

## Deferrals

Oracle `VECTOR` columns and Oracle Text indexes are **Phase 4**; Phase 0 only proves basic
connectivity. `docker/oracle/spike-vector.sql` is a manual spike (R-02) to validate `VECTOR` DML +
Oracle Text before the Phase 4 adapter is built. Port implementations in `Infrastructure` are stubs
that throw `NotImplementedException` except the embedding/chat/Oracle paths exercised by diagnostics.

> Note: the .NET 10 SDK emits a solution as `LlmWiki.slnx` (XML solution format). Use
> `dotnet build LlmWiki.slnx` (or just `dotnet build`).
