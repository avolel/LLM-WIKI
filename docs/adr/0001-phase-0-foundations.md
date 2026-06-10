# ADR 0001 — Phase 0 foundations

- Status: Accepted
- Date: 2026-06-09

## Context

Phase 0 stands up local infrastructure and the canonical repository layout for the
Database-Driven LLM Wiki. Several foundational choices were locked with the stakeholder.

## Decisions

1. **Embeddings via Ollama `nomic-embed-text` (768-dim), run as a Docker Compose service.**
   Ollama is not installed locally; a container keeps the dev environment reproducible. CPU is
   sufficient for Phase 0 (GPU is an optional compose override).

2. **Chat via a hosted provider through Semantic Kernel `IChatCompletionService`, selected by
   config.** Default wiring uses the OpenAI connector; Anthropic is a documented drop-in via its
   OpenAI-compatible endpoint. No API key lives in source — keys come from `env/.env`.

3. **Oracle Database 23ai Free in Docker.** Ships the `VECTOR` datatype and Oracle Text, which the
   Phase 4 persistence/vector adapter will use. Phase 0 proves only basic connectivity; a manual
   spike script (`docker/oracle/spike-vector.sql`, R-02) validates `VECTOR` DML + Oracle Text ahead
   of Phase 4.

4. **Clean/layered architecture (NFR-07).** Dependencies point inward; Semantic Kernel and Oracle
   are confined to `Infrastructure`/`Agents`. Application defines ports; Infrastructure provides
   adapters (stubs until their phase).

5. **Central Package Management.** `Directory.Packages.props` pins every package version to contain
   Semantic Kernel API churn (R-04).

6. **git + GitHub Actions CI.** Build + test for .NET, build/lint for the Expo app, plus a secret
   scan (R-06).

## Consequences

The repository is buildable and connectable end-to-end. Feature phases add adapter bodies behind the
existing ports without restructuring. The `.slnx` (XML) solution format is used because the .NET 10
SDK emits it by default.
