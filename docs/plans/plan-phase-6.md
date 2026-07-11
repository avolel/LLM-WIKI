# Plan — Phase 6: Project management

## Context

Phases 0–5 delivered a database-driven LLM wiki where **a "project" and a "wiki" are the same thing**: a named directory on disk (`{WIKI_ROOT}/<name>/…`, existence = a `SCHEMA.md` file) whose derived embeddings + metadata live in Oracle's `wiki_page` table, partitioned by a `wiki_name` column. Per-tenant isolation (NFR-10) is already real — every `OracleVectorStore` query filters `WHERE wiki_name = :wiki`, so a search never crosses projects. What is **missing** is a first-class, Oracle-persisted *registry* of projects and their metadata, plus the notion of a currently-selected project.

Phase 6 (BR-050…BR-053, NFR-10) fills that gap by:
- **Filling the last stub** — `OracleProjectRepository` (registered in DI since Phase 0, currently throwing) becomes a real ODP.NET adapter over a new `wiki_project` table holding `name, created_at, last_ingest_at, page_count, source_count` (BR-052).
- **Redesigning the orphaned `IProjectRepository` port** — its Phase-0 page-shaped methods (`GetPageAsync(Guid)`, …) are unused and contradict the codebase's real identity model (`(wiki_name, path)`, no persisted `Guid`). They are replaced with project-registry CRUD, exactly as Phase 4 replaced the `IVectorStore` signature.
- **Adding a `project` surface** — a CLI `project` command group (`create` / `list` / `select`) and an API `ProjectController` (`POST`/`GET /projects`). `project create` scaffolds the wiki *and* registers it; `project list` reads the Oracle registry (BR-050).
- **A persisted "active project" pointer** — a new `ICurrentProjectStore` writes `{WIKI_ROOT}/.current-project`; `ingest`/`search`/`ask` default to it when the name argument is omitted (BR-050, "select the active project on startup").
- **Keeping metadata current** — `IngestionService` gains a best-effort final step that stamps `last_ingest_at` and the recomputed page/source counts (BR-052), never failing the ingest if Oracle is down (NFR-06).

**Decisions locked with the user:** (1) a project **is** a wiki, unified — Phase 6 adds the Oracle registry + metadata on top of the existing per-wiki tenant, it does not invent a parallel concept; (2) `project create` **both** scaffolds the wiki (reusing `IWikiRepository.CreateWikiAsync`) and writes the Oracle row, while the file-only `wiki` commands stay as-is; (3) project metadata (`last_ingest_at`, counts) is updated **best-effort on each ingest**, wired into `IngestionService` as a port alongside the Phase 4 embed-on-change step; (4) `project select` **persists a current-project pointer** so subsequent commands default to it.

**Design principles held:** files stay canonical, Oracle stays derived — the current-project pointer is host-local (a dotfile, not Oracle), so `select` works offline and never depends on the DB; SK/Oracle stay confined to Infrastructure (NFR-07); every Oracle write from ingestion is best-effort so the wiki is never corrupted (NFR-06); the stub is *filled*, not shadowed (stub convention).

---

## Key facts grounding the design

- **The stub is DI-wired and throws today.** `services.AddSingleton<IProjectRepository, OracleProjectRepository>();` at [DependencyInjection.cs:36](../../src/LlmWiki.Infrastructure/DependencyInjection.cs#L36); every method of [OracleProjectRepository.cs](../../src/LlmWiki.Infrastructure/Persistence/OracleProjectRepository.cs) throws `NotImplementedException("… not implemented until Phase 3.")` (a stale "Phase 3" label — should read Phase 6). The DI smoke test already asserts the port resolves ([DependencyInjectionTests.cs:34](../../tests/LlmWiki.Infrastructure.Tests/DependencyInjectionTests.cs#L34)).
- **`OracleVectorStore` is the adapter template.** [OracleVectorStore.cs](../../src/LlmWiki.Infrastructure/VectorStore/OracleVectorStore.cs) shows the exact pattern to copy: primary ctor taking `IOptions<OracleOptions>`; `OpenAsync` guards an empty connection string then opens + `EnsureSchemaAsync`; idempotent DDL guarded by `SemaphoreSlim` + `_schemaReady` and a `SELECT COUNT(*) FROM user_tables WHERE table_name = 'WIKI_PROJECT'` check; `BindByName = true` with named `:params`; `MERGE … WHEN NOT MATCHED` for upserts (VectorStore.cs:34-46, 163-202).
- **Schema is ensured twice — DDL file + runtime.** Init scripts run only on first container creation, so the canonical DDL lives in `docker/oracle/0N-schema.sql` *and* the adapter recreates it idempotently at runtime ([02-schema.sql](../../docker/oracle/02-schema.sql) header). Phase 6 adds `docker/oracle/03-schema.sql`. The `llmwiki` user already has `CREATE TABLE` (01-init.sql) — no new grants.
- **A wiki == a directory, name threaded as a bare string.** `wiki create` builds a `WikiSchema` and calls `IWikiRepository.CreateWikiAsync` ([Program.cs:295-303](../../src/LlmWiki.Cli/Program.cs#L295-L303)); `WikiInfo(Name, LinkStyle, PageCount)` is the nearest metadata record ([IWikiRepository.cs:6](../../src/LlmWiki.Application/Ports/IWikiRepository.cs#L6)). The wiki name is an explicit positional on every command with an existence guard (`repo.WikiExistsAsync`), e.g. [Program.cs:53-58](../../src/LlmWiki.Cli/Program.cs#L53-L58).
- **Ingestion already has a best-effort Oracle final step to mirror.** `IngestionService.EmbedChangedPagesAsync` (embed-on-change) records failures as `PageOutcome(..., PageChange.Failed, …)` rather than throwing ([IngestionService.cs:98-122](../../src/LlmWiki.Agents/Ingestion/IngestionService.cs#L98-L122)). Its ctor takes ports only ([IngestionService.cs:17-23](../../src/LlmWiki.Agents/Ingestion/IngestionService.cs#L17-L23)) — Phase 6 adds `IWikiFileStore` + `IProjectRepository`.
- **`IWikiFileStore.ListAsync` is an `IAsyncEnumerable` and `raw/` contains a `.gitkeep`.** Scaffolding writes `.gitkeep` into each typed dir; source-count must exclude it ([FileSystemWikiFileStore.cs:35-50](../../src/LlmWiki.Infrastructure/FileStore/FileSystemWikiFileStore.cs#L35-L50)).
- **CLI builds a fresh provider per command; API mixes minimal-API + MVC.** `BuildProvider()` at [Program.cs:29-35](../../src/LlmWiki.Cli/Program.cs#L29-L35); a new `project` group registers at [Program.cs:19-23](../../src/LlmWiki.Cli/Program.cs#L19-L23). `QueryController` ([Controllers/QueryController.cs](../../src/LlmWiki.Api/Controllers/QueryController.cs)) is the controller template, auto-wired by `app.MapControllers()`.
- **Test patterns exist for every layer.** Oracle integration tests are opt-in and self-skip when `ORACLE_CONNECTION_STRING` is unset, cleaning up rows under a unique name in `finally` ([OracleVectorStoreTests.cs](../../tests/LlmWiki.Infrastructure.Tests/OracleVectorStoreTests.cs)). API tests swap ports for fakes via `WebApplicationFactory<Program>` + `ConfigureTestServices`. Agent tests use a real `FileSystemWikiRepository` over a temp dir + hand-rolled fakes.

---

## Files to change

### 1. `src/LlmWiki.Application/Ports/IProjectRepository.cs` (UPDATE — redesign the port)

Replace the unused page-shaped stub with the project registry contract + a `ProjectInfo` DTO.

```csharp
namespace LlmWiki.Application.Ports;

/// <summary>Oracle-persisted metadata for a project (a project == a wiki). BR-052.</summary>
public sealed record ProjectInfo(
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastIngestAt,
    int PageCount,
    int SourceCount);

/// <summary>
/// Port for the Oracle-backed project registry (Phase 6): which projects exist and their metadata.
/// A "project" is the existing per-wiki tenant — isolation is already enforced by the vector store's
/// wiki_name predicate (NFR-10); this adds durable metadata + enumeration (BR-050/052/053).
/// Implemented in Infrastructure; Oracle stays out of Domain/Application (NFR-07).
/// </summary>
public interface IProjectRepository
{
    /// <summary>Insert the project row if absent (idempotent create). BR-050/052.</summary>
    Task RegisterAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>All registered projects with metadata, ordered by name. BR-050/053.</summary>
    Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>One project's metadata, or null if unregistered. BR-050.</summary>
    Task<ProjectInfo?> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Record an ingest: stamp last_ingest_at = now and store recomputed counts. BR-052.</summary>
    Task RecordIngestAsync(string name, int pageCount, int sourceCount, CancellationToken cancellationToken = default);
}
```
The `using LlmWiki.Domain;` line is dropped (no `WikiPage` reference remains).

### 2. `src/LlmWiki.Application/Ports/ICurrentProjectStore.cs` (NEW) — the active-project pointer

```csharp
namespace LlmWiki.Application.Ports;

/// <summary>
/// The locally-persisted "active project" pointer (BR-050: select the active project on startup).
/// Single-user, host-local state — deliberately NOT in Oracle, so selection works offline and does
/// not depend on the DB. Implemented over the wiki root by Infrastructure.
/// </summary>
public interface ICurrentProjectStore
{
    Task<string?> GetAsync(CancellationToken cancellationToken = default);
    Task SetAsync(string name, CancellationToken cancellationToken = default);
}
```

### 3. `src/LlmWiki.Infrastructure/FileStore/FileCurrentProjectStore.cs` (NEW)

```csharp
using LlmWiki.Application.Ports;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Infrastructure.FileStore;

/// <summary>
/// Stores the active-project pointer as a single line in <c>{WIKI_ROOT}/.current-project</c>.
/// Host-local state (BR-050); no Oracle dependency, so `project select` works offline.
/// </summary>
public sealed class FileCurrentProjectStore(IOptions<WikiOptions> options) : ICurrentProjectStore
{
    private readonly string _path =
        Path.Combine(Path.GetFullPath(options.Value.RootPath), ".current-project");

    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_path)) return null;
        var name = (await File.ReadAllTextAsync(_path, ct)).Trim();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    public async Task SetAsync(string name, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, name.Trim(), ct);
    }
}
```
(The dotfile sits at the root, not inside any wiki dir, so it is never mistaken for a wiki by `ListWikisAsync` — that filter keeps only top-level segments that own a `SCHEMA.md`.)

### 4. `src/LlmWiki.Infrastructure/Persistence/OracleProjectRepository.cs` (UPDATE — fill the stub)

Full ODP.NET adapter mirroring `OracleVectorStore` (connection guard, idempotent schema, `BindByName`, `MERGE` upserts).

```csharp
using System.Globalization;
using LlmWiki.Application.Ports;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;

namespace LlmWiki.Infrastructure.Persistence;

/// <summary>
/// Real Oracle adapter for <see cref="IProjectRepository"/> (Phase 6): the relational registry of
/// projects + metadata in the <c>wiki_project</c> table. Mirrors OracleVectorStore's connection +
/// idempotent-schema pattern; canonical DDL committed as <c>docker/oracle/03-schema.sql</c>. Fills the
/// Phase 0 stub (stub convention — no parallel type). Confined to Infrastructure (NFR-07).
/// </summary>
public sealed class OracleProjectRepository(IOptions<OracleOptions> oracle) : IProjectRepository
{
    private readonly OracleOptions _oracle = oracle.Value;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public async Task RegisterAsync(string name, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        const string sql = """
            MERGE INTO wiki_project t
            USING (SELECT :name AS name FROM dual) s
            ON (t.name = s.name)
            WHEN NOT MATCHED THEN INSERT (name, created_at, page_count, source_count)
                VALUES (:name, SYSTIMESTAMP, 0, 0)
            """;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.BindByName = true;
        cmd.Parameters.Add(":name", name);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordIngestAsync(string name, int pageCount, int sourceCount, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        // Upsert so it works even if the project was never explicitly registered.
        const string sql = """
            MERGE INTO wiki_project t
            USING (SELECT :name AS name FROM dual) s
            ON (t.name = s.name)
            WHEN MATCHED THEN UPDATE SET
                t.last_ingest_at = SYSTIMESTAMP, t.page_count = :pc, t.source_count = :sc
            WHEN NOT MATCHED THEN INSERT (name, created_at, last_ingest_at, page_count, source_count)
                VALUES (:name, SYSTIMESTAMP, SYSTIMESTAMP, :pc, :sc)
            """;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.BindByName = true;
        cmd.Parameters.Add(":name", name);
        cmd.Parameters.Add(":pc", pageCount);
        cmd.Parameters.Add(":sc", sourceCount);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ProjectInfo?> GetAsync(string name, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name, created_at, last_ingest_at, page_count, source_count
              FROM wiki_project WHERE name = :name
            """;
        cmd.BindByName = true;
        cmd.Parameters.Add(":name", name);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Map(reader) : null;
    }

    public async Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT name, created_at, last_ingest_at, page_count, source_count
              FROM wiki_project ORDER BY name
            """;
        var list = new List<ProjectInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) list.Add(Map(reader));
        return list;
    }

    private static ProjectInfo Map(OracleDataReader r) => new(
        r.GetString(0),
        Utc(r.GetDateTime(1)),
        r.IsDBNull(2) ? null : Utc(r.GetDateTime(2)),
        r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3), CultureInfo.InvariantCulture),
        r.IsDBNull(4) ? 0 : Convert.ToInt32(r.GetValue(4), CultureInfo.InvariantCulture));

    private static DateTimeOffset Utc(DateTime dt) => new(DateTime.SpecifyKind(dt, DateTimeKind.Utc));

    private async Task<OracleConnection> OpenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_oracle.ConnectionString))
            throw new InvalidOperationException(
                "ORACLE_CONNECTION_STRING is not configured (set it in env/.env).");
        var conn = new OracleConnection(_oracle.ConnectionString);
        await conn.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        return conn;
    }

    private async Task EnsureSchemaAsync(OracleConnection conn, CancellationToken ct)
    {
        if (_schemaReady) return;
        await _schemaGate.WaitAsync(ct);
        try
        {
            if (_schemaReady) return;
            await using var check = conn.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM user_tables WHERE table_name = 'WIKI_PROJECT'";
            var exists = Convert.ToInt32(await check.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) > 0;
            if (!exists)
            {
                await using var ddl = conn.CreateCommand();
                ddl.CommandText = """
                    CREATE TABLE wiki_project (
                      name           VARCHAR2(128) NOT NULL,
                      created_at     TIMESTAMP,
                      last_ingest_at TIMESTAMP,
                      page_count     NUMBER DEFAULT 0,
                      source_count   NUMBER DEFAULT 0,
                      CONSTRAINT wiki_project_pk PRIMARY KEY (name)
                    )
                    """;
                await ddl.ExecuteNonQueryAsync(ct);
            }
            _schemaReady = true;
        }
        finally { _schemaGate.Release(); }
    }
}
```

### 5. `docker/oracle/03-schema.sql` (NEW) — canonical DDL (reproducibility, NFR-04)

Mirrors the header/format of `02-schema.sql`.

```sql
-- ============================================================================
-- Phase 6 — application schema: the project registry.
--
-- Canonical DDL for the `wiki_project` table the OracleProjectRepository reads/writes.
-- Init scripts run only on FIRST container creation, so the adapter also ensures this
-- schema idempotently at runtime (OracleProjectRepository.EnsureSchemaAsync). This file
-- is the reproducible source of truth (NFR-04) and can be applied by hand:
--
--   docker compose exec oracle bash -c \
--     "sqlplus llmwiki/Wiki_Dev_0@localhost:1521/FREEPDB1 @/opt/oracle/scripts/startup/03-schema.sql"
-- ============================================================================

ALTER SESSION SET CONTAINER = FREEPDB1;

-- One row per project (== wiki). Metadata only; page rows live in wiki_page (BR-052).
CREATE TABLE wiki_project (
  name           VARCHAR2(128) NOT NULL,
  created_at     TIMESTAMP,
  last_ingest_at TIMESTAMP,
  page_count     NUMBER DEFAULT 0,
  source_count   NUMBER DEFAULT 0,
  CONSTRAINT wiki_project_pk PRIMARY KEY (name)
);

COMMIT;
EXIT;
```

### 6. `src/LlmWiki.Infrastructure/DependencyInjection.cs` (UPDATE) — register the pointer, fix comment

- Change the line 34 comment from "project persistence (stub until Phase 3)" to "project registry (Phase 6)".
- Add next to line 36: `services.AddSingleton<ICurrentProjectStore, FileCurrentProjectStore>();`
No signature change to the existing `IProjectRepository` registration (both ctor deps — `IOptions<OracleOptions>`, `IOptions<WikiOptions>` — are already registered by `AddLlmWikiOptions`).

### 7. `src/LlmWiki.Agents/Ingestion/IngestionService.cs` (UPDATE) — best-effort metadata step

Add `IWikiFileStore files` and `IProjectRepository projects` to the primary ctor (ports only — no SK). After the existing `await EmbedChangedPagesAsync(...)` call ([IngestionService.cs:87](../../src/LlmWiki.Agents/Ingestion/IngestionService.cs#L87)), add `await RecordProjectAsync(wikiName, outcomes, ct);`, and add the helper:

```csharp
/// <summary>
/// Best-effort project-metadata update (BR-052): stamp last-ingest and store recomputed page/source
/// counts. A failure (e.g. Oracle down) is recorded as a Failed outcome, never thrown — file + journal
/// ingestion keep working and the wiki is never corrupted (NFR-06).
/// </summary>
private async Task RecordProjectAsync(string wikiName, List<PageOutcome> outcomes, CancellationToken ct)
{
    try
    {
        var pageCount = (await wiki.ListPagesAsync(wikiName, ct)).Count;
        var sourceCount = 0;
        await foreach (var p in files.ListAsync($"{wikiName}/raw/", ct))
            if (!p.EndsWith(".gitkeep", StringComparison.Ordinal)) sourceCount++;
        await projects.RecordIngestAsync(wikiName, pageCount, sourceCount, ct);
    }
    catch (Exception ex)
    {
        outcomes.Add(new PageOutcome("project", "Project metadata", PageChange.Failed, $"project: {ex.Message}"));
    }
}
```

### 8. `src/LlmWiki.Cli/Program.cs` (UPDATE) — `project` command group + current-project fallback

- Register the group at the root: add `root.Subcommands.Add(BuildProjectCommand());` (line ~23).
- Add `BuildProjectCommand()` modelled on `BuildWikiCommand()`:
  - **`project create <name> [--link-style]`** — `await repo.CreateWikiAsync(schema, ct)` then best-effort `await projects.RegisterAsync(name, ct)` (warn, don't fail, if Oracle is down — files are canonical). Then `await current.SetAsync(name, ct)` so a freshly created project becomes active.
  - **`project list`** — `await projects.ListAsync(ct)`; print `name  created  last-ingest  N page(s)  M source(s)`, marking the active project (from `ICurrentProjectStore.GetAsync`) with `*`.
  - **`project select <name>`** — guard `repo.WikiExistsAsync`, `await current.SetAsync(name, ct)`, best-effort `RegisterAsync`, print `Active project: <name>`.
- **Current-project fallback for `ingest`/`search`/`ask`:** make the `wiki` positional optional (`ArgumentArity.ZeroOrOne`) and resolve via a shared helper. The payload positional (`file`/`query`) stays required, so a lone token binds to it and the project falls back to the pointer; `ask` with no args opens the REPL on the active project.

```csharp
// Resolve the target project: explicit arg wins, else the persisted active project, else error.
static async Task<string?> ResolveWikiAsync(
    IWikiRepository repo, ICurrentProjectStore current, string? explicitName, CancellationToken ct)
{
    var name = string.IsNullOrWhiteSpace(explicitName) ? await current.GetAsync(ct) : explicitName;
    if (string.IsNullOrWhiteSpace(name))
    {
        await Console.Error.WriteLineAsync("No project specified and none selected (use 'project select <name>').");
        return null;
    }
    if (!await repo.WikiExistsAsync(name, ct))
    {
        await Console.Error.WriteLineAsync($"Wiki '{name}' not found.");
        return null;
    }
    return name;
}
```
Each of `ingest`/`search`/`ask` swaps its `pr.GetValue(wikiArg)! + WikiExistsAsync` guard block for `var wikiName = await ResolveWikiAsync(repo, current, pr.GetValue(wikiArg), ct); if (wikiName is null) return 1;` and resolves `ICurrentProjectStore` from the provider. (Note: verify the two-positional arity behaviour during implementation — see Verification step 6; if a lone token proves ambiguous, the fallback is a `-p|--project` option, but the optional-positional form is preferred.)

### 9. `src/LlmWiki.Api/Controllers/ProjectController.cs` (NEW) — `/projects` (copy the QueryController shape)

```csharp
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using Microsoft.AspNetCore.Mvc;

namespace LlmWiki.Api.Controllers;

[ApiController]
[Route("projects")]
public sealed class ProjectController(IProjectRepository projects, IWikiRepository wiki) : ControllerBase
{
    /// <summary>List registered projects with metadata (BR-050/052).</summary>
    [HttpGet]
    public async Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct) => await projects.ListAsync(ct);

    /// <summary>One project's metadata, or 404 (BR-050).</summary>
    [HttpGet("{name}")]
    public async Task<IActionResult> GetAsync(string name, CancellationToken ct) =>
        await projects.GetAsync(name, ct) is { } info ? Ok(info) : NotFound();

    /// <summary>Create a project: scaffold the wiki + register it (BR-050).</summary>
    [HttpPost]
    public async Task<IActionResult> CreateAsync(CreateProjectRequest req, CancellationToken ct)
    {
        if (await wiki.WikiExistsAsync(req.Name, ct)) return Conflict();
        await wiki.CreateWikiAsync(new WikiSchema { WikiName = req.Name, LinkStyle = req.LinkStyle ?? LinkStyle.Wikilink }, ct);
        await projects.RegisterAsync(req.Name, ct);
        return CreatedAtAction(nameof(GetAsync), new { name = req.Name }, await projects.GetAsync(req.Name, ct));
    }
}

public record CreateProjectRequest(string Name, LinkStyle? LinkStyle);
```
Auto-wired by the existing `app.MapControllers()`; appears in Swagger UI. (`select` is a CLI-local concept — no API endpoint, since the active-project pointer is host-local.)

### 10. Tests

- **`tests/LlmWiki.Infrastructure.Tests/OracleProjectRepositoryTests.cs` (NEW)** — opt-in integration mirroring `OracleVectorStoreTests`: skip when `ORACLE_CONNECTION_STRING` unset; under a unique `itest_<guid>` name, assert `RegisterAsync` is idempotent, `GetAsync`/`ListAsync` return the row, `RecordIngestAsync` sets `LastIngestAt` + counts and upserts when unregistered; delete the row in `finally`. (BR-050/052/053, NFR-10)
- **`tests/LlmWiki.Infrastructure.Tests/FileCurrentProjectStoreTests.cs` (NEW)** — real temp `WIKI_ROOT`: `GetAsync` is null before set; `SetAsync` then `GetAsync` round-trips; blank file reads back null. (BR-050)
- **`tests/LlmWiki.Infrastructure.Tests/DependencyInjectionTests.cs` (UPDATE)** — add an assertion that `ICurrentProjectStore` resolves (the `IProjectRepository` assertion already exists).
- **`tests/LlmWiki.Agents.Tests/IngestionServiceTests.cs` (UPDATE)** — extend the ctor calls for the two new params: a real `FileSystemWikiFileStore` over the temp root + a nested `FakeProjectRepository` that records `RecordIngestAsync(name, pageCount, sourceCount)` calls. Add: (a) after a normal ingest, the fake received one `RecordIngest` with `pageCount > 0` and `sourceCount == 1` (BR-052); (b) a `ThrowingProjectRepository` produces a `Failed` "project" outcome but the ingest still returns its pages (NFR-06).
- **`tests/LlmWiki.Api.Tests/ProjectEndpointTests.cs` (NEW)** — `WebApplicationFactory<Program>` + `ConfigureTestServices` swapping in a `FakeProjectRepository` (+ `FakeRepo : IWikiRepository`): `GET /projects` returns the seeded list; `POST /projects` creates + registers and returns 201; `POST` a duplicate returns 409. (BR-050)

### 11. Docs (UPDATE)

- **`docs/code-overview/code-overview.md`** — flip the ports-table row and the §8 phase-map row for `IProjectRepository`/Phase 6 from "stub 🔲" to done, and note the new `wiki_project` table, `ICurrentProjectStore`, and the `project` CLI/API surface.
- **`docs/adr/0002-phase-6-project-registry.md` (NEW)** — record the load-bearing decision: *a project is the existing wiki tenant; Phase 6 adds a derived Oracle `wiki_project` registry + a host-local active-project pointer, rather than a new isolation mechanism* (NFR-10 already satisfied by `wiki_name`). Follows the `0001` format (Status/Date/Context/Decisions/Consequences).
- **`CLAUDE.md`** — append a Phase 6 note to the "What this is" paragraph + working-agreement trailer, consistent with the Phase 1–5 notes.

---

## Requirements covered

BR-050 (create/list/select named projects — `project create|list|select`, persisted active pointer), BR-051 (per-project isolation — inherited from the vector store's `wiki_name` predicate; no cross-project reads), BR-052 (name/created/last-ingest/page-count/source-count stored in Oracle `wiki_project`, updated best-effort on ingest), BR-053 (persistence across runs — registry + files survive restart, reselect restores state), NFR-10 (isolation partitioned per project at the query level, unchanged and reaffirmed), NFR-04 (canonical DDL in `03-schema.sql` for reproducibility), NFR-06 (best-effort metadata + registration never corrupt or fail the wiki), NFR-07 (Oracle confined to Infrastructure; the registry adapter and orchestrator stay port-only).

---

## Verification (end-to-end)

Prereqs: `cd docker && docker compose up -d`; `env/.env` has `ORACLE_CONNECTION_STRING`; `dotnet build LlmWiki.slnx` and `dotnet test LlmWiki.slnx` green (Oracle integration tests run only with the DB up).

1. **Create + register (BR-050/052):** `dotnet run --project src/LlmWiki.Cli -- project create ml-papers` → prints created + `Active project: ml-papers`. In sqlplus: `SELECT * FROM wiki_project;` shows one row with `created_at` set, counts 0.
2. **List (BR-050):** `dotnet run --project src/LlmWiki.Cli -- project create trains` then `project list` → both projects listed, `ml-papers`/`trains`, with `*` beside the currently-active one.
3. **Select persists (BR-050/053):** `project select ml-papers` → `Active project: ml-papers`; confirm `cat wiki/.current-project` prints `ml-papers`.
4. **Metadata updates on ingest (BR-052):** `dotnet run --project src/LlmWiki.Cli -- ingest ml-papers ./docs/sample-source.md` → `project list` now shows `ml-papers` with `last-ingest` set, `page(s) > 0`, `1 source(s)`. Confirm in sqlplus that `wiki_project` reflects the same.
5. **Isolation holds (BR-051/NFR-10):** `search trains "the ingested topic"` returns nothing from `ml-papers`; `search ml-papers "…"` does — no cross-project leakage.
6. **Current-project fallback (BR-050):** with `ml-papers` selected, `dotnet run --project src/LlmWiki.Cli -- search "the topic"` (no project arg) searches `ml-papers`; `ask` with no args opens the REPL on `ml-papers`. (While implementing, confirm the two-positional parse: `search "the topic"` binds the lone token to `query`, not `wiki`.)
7. **Resilience (NFR-06):** `docker compose stop oracle`; `project create offline-demo` still scaffolds the wiki on disk (registration warns); `ingest offline-demo ./docs/sample-source.md` still writes pages/index/log and returns them, with a single `[Failed] project` outcome line. Restart Oracle → re-ingest → metadata appears (BR-053).
8. **API (BR-050):** `dotnet run --project src/LlmWiki.Api`; browse `http://localhost:5080/swagger`; `POST /projects {"name":"api-demo"}` → 201; `GET /projects` → includes `api-demo`; `GET /projects/api-demo` → its metadata; a duplicate `POST` → 409.
9. **Persistence across runs (BR-053):** stop everything, `docker compose up -d`, re-run `project list` → all projects + metadata intact.

---

## Out of scope (later phases)

Linting / health-check (Phase 7) and the React Native client + wiki-tree browser (Phase 8). Project *deletion/rename* is not in BR-050…053 and is deferred. The active-project pointer is CLI-local by design; a server-side "session project" is unnecessary while the API takes the project name per request.