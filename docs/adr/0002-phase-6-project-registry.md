# ADR 0002 — Phase 6 project registry

- Status: Accepted
- Date: 2026-07-11

## Context

Through Phase 5 a "project" and a "wiki" were already the same thing: a named directory under
`{WIKI_ROOT}/<name>/…` whose derived embeddings + metadata live in Oracle's `wiki_page` table,
partitioned by a `wiki_name` column. Per-tenant isolation (NFR-10) was therefore already real — every
`OracleVectorStore` query filters `WHERE wiki_name = :wiki`, so a search never crosses projects. What
was missing was a first-class, Oracle-persisted *registry* of projects and their metadata, plus the
notion of a currently-selected project (BR-050…BR-053). `OracleProjectRepository` had been a
DI-registered stub since Phase 0, throwing on use.

## Decisions

1. **A project *is* the existing wiki tenant — unified, not a parallel concept.** Phase 6 adds a
   derived Oracle registry + metadata *on top of* the per-wiki tenant; it does not invent a new
   isolation mechanism. NFR-10 is already satisfied by the `wiki_name` predicate and is only
   reaffirmed here.

2. **Fill the stub, don't shadow it.** The orphaned Phase-0 `IProjectRepository` had page-shaped
   methods (`GetPageAsync(Guid)`, …) that contradicted the codebase's real identity model
   (`(wiki_name, path)`, no persisted `Guid`). They are **replaced** with project-registry CRUD
   (`RegisterAsync`/`ListAsync`/`GetAsync`/`RecordIngestAsync`), exactly as Phase 4 replaced the
   `IVectorStore` signature. `OracleProjectRepository` becomes a real ODP.NET adapter over a new
   `wiki_project` table, mirroring `OracleVectorStore` (connection guard, idempotent runtime schema,
   `BindByName`, `MERGE` upserts). Canonical DDL is committed as `docker/oracle/03-schema.sql`
   (NFR-04).

3. **The active-project pointer is host-local, not Oracle.** `ICurrentProjectStore` /
   `FileCurrentProjectStore` persists the selected project as a single line in
   `{WIKI_ROOT}/.current-project`. Files stay canonical and Oracle stays derived, so `project select`
   works offline and never depends on the DB. `ingest`/`search`/`ask` default to this pointer when the
   wiki name is omitted.

4. **`project create` both scaffolds and registers; ingest metadata is best-effort.** `project create`
   reuses `IWikiRepository.CreateWikiAsync` *and* writes the Oracle row (the file-only `wiki` commands
   stay as-is). `IngestionService` gains a final best-effort step that stamps `last_ingest_at` and the
   recomputed page/source counts. Every Oracle write from create/ingest is best-effort — a failure is
   recorded (a warning, or a `Failed` "project" outcome), never thrown, so the wiki on disk is never
   corrupted (NFR-06).

5. **CLI two-positional disambiguation.** System.CommandLine binds a lone token to the first
   (optional) positional, which would break the bare `search "q"` / `ingest ./file` forms. So for
   `ingest`/`search` a single positional is treated as the required payload (wiki from the active
   pointer) and two positionals as explicit `<wiki> <payload>` — preserving the documented
   `search demo "q"` form while enabling the omit-wiki form. `ask` keeps greedy binding (its lone
   token is the wiki for the REPL).

## Consequences

The last application-adapter stub is filled; the project registry survives restarts (BR-053) and is
enumerable over both surfaces — a `project` CLI group (`create`/`list`/`select`) and a `/projects`
MVC controller (`GET`/`POST`, in Swagger). `select` is CLI-local by design (the pointer is
host-local), so there is no API endpoint for it; the API takes the project name per request. Project
*deletion/rename* is out of BR-050…053 and deferred. Oracle remains confined to Infrastructure
(NFR-07).
