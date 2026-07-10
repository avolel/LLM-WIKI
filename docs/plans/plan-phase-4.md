# Plan — Phase 4: Hybrid Retrieval in Oracle

## Context

Phases 0–3 gave us a buildable foundation, a file-backed wiki, a source-ingestion
pipeline, and agent-owned `index.md`/`log.md`. Everything is on disk; **nothing is
searchable yet**. Oracle is provisioned and its `VECTOR` + Oracle Text primitives are
validated in [docker/oracle/spike-vector.sql](docker/oracle/spike-vector.sql), but the
`IVectorStore` adapter is still a `NotImplementedException` stub.

Phase 4 makes the wiki *retrievable*: every content page gets a 768-dim embedding stored
in Oracle alongside its metadata; ingestion re-embeds **only the pages a run changed**
(BR-033); and a new CLI `search` command returns pages by **hybrid** ranking — vector
cosine similarity for paraphrased queries, Oracle Text `CONTAINS` for exact names/terms
(BR-030…BR-035, NFR-10).

**Decisions locked with the user:** full hybrid now (vector + Oracle Text, fused);
CLI `search` command as the verification surface (React Native UI stays Phase 8);
report-driven embed-on-change (embed exactly the pages `IngestionReport` marked
Created/Updated/Stub plus contradiction-noted pages — no content-hash frontmatter field).

**Design principles held:** SK/Oracle stay confined to Infrastructure (NFR-07); the real
adapter *replaces* the existing stub (stub convention — no parallel type); ingestion
stays a plain port-based orchestrator (no SK dependency) so the Process Framework can
later slot in behind `IIngestionService`. Embedding/vector-store calls during ingestion
are **best-effort** — a failure (e.g. Oracle down) is recorded, never corrupts the wiki
(NFR-06); file-only ingestion keeps working.

---

## Key facts grounding the design

- **Change signal is free.** `IngestionReport.Outcomes` already lists every touched page
  by wiki-relative path + `PageChange`. Changed set =
  `Outcomes.Where(Created|Updated|StubCreated)` ∪ `Contradictions[].PageRelativePath`
  (the reconcile pass writes contradiction notes *outside* the tracked `Outcomes` helper,
  so we must union them in — see [IngestionService.cs:165-175](src/LlmWiki.Agents/Ingestion/IngestionService.cs#L165-L175)).
- **Key pages by `{wikiName}/{relativePath}`, not `WikiPage.Id`.** `Id` is regenerated on
  every read and never persisted; the durable identity is the path.
- **Reuse the ODP.NET pattern** from [OracleDatabaseHealthCheck.cs](src/LlmWiki.Infrastructure/Persistence/OracleDatabaseHealthCheck.cs):
  `IOptions<OracleOptions>`, guard empty connection string, per-call
  `await using OracleConnection` + `OpenAsync(ct)`, `CreateCommand`, `ExecuteNonQueryAsync(ct)`.
- **Reuse `OllamaEmbeddingService`** ([IEmbeddingService](src/LlmWiki.Application/Ports/IEmbeddingService.cs)) as-is for both ingest-time and query-time embedding — it's real and 768-dim.
- **SQL blueprint is validated**: `VECTOR(768, FLOAT32)`, `TO_VECTOR('[...]')`,
  `VECTOR_DISTANCE(emb, ?, COSINE)` (ascending = closest), and Oracle Text
  `INDEXTYPE IS CTXSYS.CONTEXT` + `CONTAINS(body, ?, 1)` / `SCORE(1)`. The `llmwiki` user
  already has `EXECUTE ON CTXSYS.CTX_DDL` and `CREATE TABLE` grants
  ([01-init.sql](docker/oracle/01-init.sql)).
- **No application schema exists yet.** `01-init.sql` only creates the user; init scripts
  don't re-run on an existing container. The adapter therefore **ensures its own schema
  idempotently** on first use; we also commit a canonical DDL file for reproducibility.

---

## Files to change

### 1. `src/LlmWiki.Application/Ports/IVectorStore.cs` — path-keyed, hybrid signature

The current signature keys on `WikiPage` (Id is meaningless) and `SearchAsync` takes only
an embedding (can't do the lexical half). Replace with a path-keyed, wiki-scoped, hybrid
contract. `VectorSearchHit` returns resolvable path metadata (Phase 5 re-reads full
content from disk via `IWikiRepository`).

```csharp
using LlmWiki.Domain;

namespace LlmWiki.Application.Ports;

/// <summary>A page identified by its wiki-relative path, paired with its hybrid score.</summary>
public sealed record VectorSearchHit(
    string WikiName,
    string RelativePath,
    string Title,
    PageType Type,
    double Score,
    string? Snippet = null);

/// <summary>
/// Per-page embeddings + metadata in Oracle (VECTOR + Oracle Text). Data is partitioned by
/// wiki name so a search never crosses projects (NFR-10). Real ODP.NET adapter: Phase 4.
/// </summary>
public interface IVectorStore
{
    /// <summary>Insert or replace the row for one page (keyed by wiki + relative path).</summary>
    Task UpsertAsync(
        string wikiName, string relativePath, WikiPage page,
        ReadOnlyMemory<float> embedding, CancellationToken cancellationToken = default);

    /// <summary>
    /// Hybrid search within one wiki: cosine VECTOR_DISTANCE (semantic) fused with Oracle Text
    /// CONTAINS (lexical) via reciprocal-rank fusion. Optional page-type filter (BR-032).
    /// </summary>
    Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
        string wikiName, string queryText, ReadOnlyMemory<float> queryEmbedding,
        int topK, PageType? typeFilter = null, CancellationToken cancellationToken = default);
}
```

Note: `SavePageAsync`-style deletion for stale pages (BR-024 on the *search* side) is
deferred to Phase 6 (project management / re-sync); Phase 4 only upserts changed pages,
which meets the acceptance criteria.

### 2. `src/LlmWiki.Infrastructure/VectorStore/OracleVectorStore.cs` — the real adapter

Replaces the stub. Responsibilities: (a) idempotent schema ensure; (b) `MERGE` upsert
binding the vector via `TO_VECTOR(:emb)` (a `[f,f,…]` string — the driver-safe path proven
by the spike); (c) hybrid search = vector top-N ∪ lexical top-N fused by reciprocal rank.

Schema (also committed as `docker/oracle/02-schema.sql`, below):

```sql
CREATE TABLE wiki_page (
  wiki_name  VARCHAR2(128)      NOT NULL,
  path       VARCHAR2(400)      NOT NULL,
  title      VARCHAR2(400),
  type       VARCHAR2(20),
  tags       VARCHAR2(2000),
  snippet    VARCHAR2(2000),
  content    CLOB,
  emb        VECTOR(768, FLOAT32),
  updated_at TIMESTAMP,
  CONSTRAINT wiki_page_pk PRIMARY KEY (wiki_name, path)
);
CREATE INDEX wiki_page_txt ON wiki_page (content) INDEXTYPE IS CTXSYS.CONTEXT;
-- Vector index (optional at this corpus size; exact scan is correct and fast for hundreds of pages):
-- CREATE VECTOR INDEX wiki_page_vec ON wiki_page (emb) ORGANIZATION INMEMORY NEIGHBOR GRAPH
--   DISTANCE COSINE WITH TARGET ACCURACY 95;
```

Adapter shape:

```csharp
using System.Globalization;
using System.Text;
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;
using Oracle.ManagedDataAccess.Types;

namespace LlmWiki.Infrastructure.VectorStore;

public sealed class OracleVectorStore(IOptions<OracleOptions> oracle, IOptions<EmbeddingOptions> embedding)
    : IVectorStore
{
    private readonly OracleOptions _oracle = oracle.Value;
    private readonly int _dim = embedding.Value.Dimensions;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private bool _schemaReady;

    public async Task UpsertAsync(
        string wikiName, string relativePath, WikiPage page,
        ReadOnlyMemory<float> embedding, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        // MERGE keyed on (wiki_name, path); vector bound as a TO_VECTOR string (spike-proven).
        const string sql = """
            MERGE INTO wiki_page t
            USING (SELECT :wiki AS wiki_name, :path AS path FROM dual) s
            ON (t.wiki_name = s.wiki_name AND t.path = s.path)
            WHEN MATCHED THEN UPDATE SET
                t.title = :title, t.type = :type, t.tags = :tags,
                t.snippet = :snippet, t.content = :content,
                t.emb = TO_VECTOR(:emb), t.updated_at = SYSTIMESTAMP
            WHEN NOT MATCHED THEN INSERT
                (wiki_name, path, title, type, tags, snippet, content, emb, updated_at)
                VALUES (:wiki, :path, :title, :type, :tags, :snippet, :content,
                        TO_VECTOR(:emb), SYSTIMESTAMP)
            """;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.BindByName = true;
        cmd.Parameters.Add(":wiki", wikiName);
        cmd.Parameters.Add(":path", relativePath);
        cmd.Parameters.Add(":title", page.Title);
        cmd.Parameters.Add(":type", page.Type.ToString());
        cmd.Parameters.Add(":tags", string.Join(",", page.Tags));
        cmd.Parameters.Add(":snippet", Snippet(page.Content));
        cmd.Parameters.Add(new OracleParameter(":content", OracleDbType.Clob) { Value = page.Content });
        cmd.Parameters.Add(":emb", ToVectorLiteral(embedding.Span));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
        string wikiName, string queryText, ReadOnlyMemory<float> queryEmbedding,
        int topK, PageType? typeFilter = null, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var typeClause = typeFilter is null ? "" : " AND type = :type";
        var fetch = Math.Max(topK * 4, 20);   // over-fetch each arm before fusion

        // Semantic arm: cosine distance ascending = closest.
        var vector = await QueryAsync(conn,
            $"""
             SELECT path, title, type, snippet FROM (
               SELECT path, title, type, snippet,
                      VECTOR_DISTANCE(emb, TO_VECTOR(:emb), COSINE) AS dist
                 FROM wiki_page
                WHERE wiki_name = :wiki{typeClause}
                ORDER BY dist)
             WHERE ROWNUM <= :fetch
             """,
            cmd =>
            {
                cmd.Parameters.Add(":emb", ToVectorLiteral(queryEmbedding.Span));
                cmd.Parameters.Add(":wiki", wikiName);
                if (typeFilter is not null) cmd.Parameters.Add(":type", typeFilter.ToString());
                cmd.Parameters.Add(":fetch", fetch);
            }, ct);

        // Lexical arm: Oracle Text. Skip gracefully if the query has no indexable terms.
        var lexical = string.IsNullOrWhiteSpace(queryText) ? [] : await QueryAsync(conn,
            $"""
             SELECT path, title, type, snippet FROM (
               SELECT path, title, type, snippet, SCORE(1) AS rank
                 FROM wiki_page
                WHERE wiki_name = :wiki{typeClause} AND CONTAINS(content, :q, 1) > 0
                ORDER BY rank DESC)
             WHERE ROWNUM <= :fetch
             """,
            cmd =>
            {
                cmd.Parameters.Add(":wiki", wikiName);
                if (typeFilter is not null) cmd.Parameters.Add(":type", typeFilter.ToString());
                cmd.Parameters.Add(":q", ToContainsQuery(queryText));
                cmd.Parameters.Add(":fetch", fetch);
            }, ct, ignoreTextErrors: true);

        return Fuse(vector, lexical, wikiName, topK);
    }

    // --- Reciprocal-rank fusion: score = Σ 1/(k + rank); k=60 is the standard constant. ---
    private static IReadOnlyList<VectorSearchHit> Fuse(
        List<Row> vector, List<Row> lexical, string wikiName, int topK)
    {
        const double k = 60;
        var scores = new Dictionary<string, (Row row, double score)>();
        void Accumulate(List<Row> rows)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var add = 1.0 / (k + i + 1);
                scores[r.Path] = scores.TryGetValue(r.Path, out var e)
                    ? (e.row, e.score + add) : (r, add);
            }
        }
        Accumulate(vector);
        Accumulate(lexical);
        return scores.Values
            .OrderByDescending(x => x.score)
            .Take(topK)
            .Select(x => new VectorSearchHit(
                wikiName, x.row.Path, x.row.Title, ParseType(x.row.Type), x.score, x.row.Snippet))
            .ToList();
    }

    private async Task<OracleConnection> OpenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_oracle.ConnectionString))
            throw new InvalidOperationException("ORACLE_CONNECTION_STRING is not configured (set it in env/.env).");
        var conn = new OracleConnection(_oracle.ConnectionString);
        await conn.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        return conn;
    }

    // Idempotent: create table + Text index once per process if absent (USER_TABLES / USER_INDEXES).
    private async Task EnsureSchemaAsync(OracleConnection conn, CancellationToken ct) { /* guarded by _schemaGate/_schemaReady */ }

    private record struct Row(string Path, string Title, string Type, string? Snippet);
    private static string ToVectorLiteral(ReadOnlySpan<float> v) { /* "[0.1,0.2,...]" invariant-culture */ }
    private static string ToContainsQuery(string q) { /* tokenize -> "t1 OR t2 ...", escape Text operators */ }
    private static string Snippet(string content) => content.Length <= 2000 ? content : content[..2000];
    private static PageType ParseType(string s) => Enum.TryParse<PageType>(s, true, out var t) ? t : PageType.Summary;
    // QueryAsync(conn, sql, bind, ct, ignoreTextErrors) — helper that runs a reader into List<Row>.
}
```

Key adapter notes:
- **`ToContainsQuery`** must sanitize free text before `CONTAINS` — Oracle Text treats
  `&|~-,` etc. as operators. Split on whitespace, drop empties, escape/quote each token,
  join with `OR` so any term can match (lexical is a recall arm; fusion handles precision).
- **`ignoreTextErrors`** on the lexical arm: a malformed CONTAINS (`DRG-*`) returns empty
  rather than failing the whole search — the vector arm still answers.
- No explicit vector index for Phase 4 (exact cosine scan is correct and fast for the
  50–100-doc / hundreds-of-pages target, NFR-08); the `CREATE VECTOR INDEX` line is
  documented for the scale path (R-08).

### 3. `src/LlmWiki.Agents/Ingestion/IngestionService.cs` — embed-on-change step

Inject the two ports (keeps IngestionService port-only — no SK) and add a **best-effort**
embedding step mirroring the existing Phase 3 journal block. Runs after the report is
built; a failure is recorded on `outcomes`, never thrown (NFR-06), so file-only ingestion
(Oracle down) still succeeds.

```csharp
public sealed class IngestionService(
    IChatService chat,
    IWikiRepository wiki,
    IWikiJournal journal,
    IEmbeddingService embeddings,      // new
    IVectorStore vectors,              // new
    IOptions<EmbeddingOptions> embedOptions) : IIngestionService   // new (strategy)
```

After `var report = new IngestionReport(...)`, before/after the journal block:

```csharp
// Embed only the pages this run created/updated (BR-033) — the report is the change set.
// Contradiction-only edits are written outside the tracked helper, so union them in.
var changed = report.Outcomes
    .Where(o => o.Change is PageChange.Created or PageChange.Updated or PageChange.StubCreated)
    .Select(o => o.RelativePath)
    .Concat(report.Contradictions.Select(c => c.PageRelativePath))
    .Distinct();

foreach (var path in changed)
{
    try
    {
        var page = await wiki.ReadPageAsync(wikiName, path, ct);
        var text = EmbeddingText.For(page, _strategy);          // BR-034 configurable
        var vector = await embeddings.EmbedAsync(text, ct);
        await vectors.UpsertAsync(wikiName, path, page, vector, ct);
    }
    catch (Exception ex)
    {
        outcomes.Add(new PageOutcome(path, path, PageChange.Failed, $"embed: {ex.Message}"));
    }
}
```

### 4. `src/LlmWiki.Application/Embeddings/EmbeddingText.cs` — strategy selector (BR-034)

Small pure helper (no deps) selecting what text to embed per configurable strategy:

```csharp
public enum EmbeddingStrategy { TitleAndBody, FullText, Summary }

public static class EmbeddingText
{
    public static string For(WikiPage page, EmbeddingStrategy strategy) => strategy switch
    {
        EmbeddingStrategy.FullText => page.Content,
        EmbeddingStrategy.Summary  => FirstParagraph(page.Content),
        _                          => $"{page.Title}\n\n{page.Content}",   // TitleAndBody (default)
    };
}
```

### 5. Config — `EmbeddingOptions`, `LlmWikiConfiguration`, `env/.env.example`

- `EmbeddingOptions`: add `public EmbeddingStrategy Strategy { get; set; } = EmbeddingStrategy.TitleAndBody;`
- `LlmWikiConfiguration.EnvToConfigKey`: add `["EMBEDDING_STRATEGY"] = "Embedding:Strategy"`.
- `env/.env.example`: document `EMBEDDING_STRATEGY=TitleAndBody` (values: TitleAndBody | FullText | Summary).
- `ORACLE_CONNECTION_STRING` already exists and is reused.

### 6. `src/LlmWiki.Cli/Program.cs` — `search` command

Follows the `BuildIngestCommand` pattern; resolves `IEmbeddingService` (query embed) +
`IVectorStore.SearchAsync`. Registered via `root.Subcommands.Add(BuildSearchCommand())`.

```
llm-wiki search <wiki> <query> [--top-k 5] [--type entity|concept|summary|overview]
```

Action: verify wiki exists → `embeddings.EmbedAsync(query)` → `vectors.SearchAsync(wiki,
query, embedding, topK, type)` → print ranked `#. <path>  <score>  — <title>`; empty
result prints "no matches". `--type` uses `Option<PageType>` enum binding (already
demonstrated in `BuildPageCommand`).

### 7. `docker/oracle/02-schema.sql` — canonical DDL (reproducibility, NFR-04)

The `wiki_page` table + Oracle Text index from §2, committed for manual/reference use and
so a fresh container provisions the schema. The adapter's `EnsureSchemaAsync` is the
runtime safety net for already-created containers.

### 8. DI — no signature change needed

`DependencyInjection.cs` already registers `IVectorStore -> OracleVectorStore` (singleton);
the real class just gains `IOptions<OracleOptions>` + `IOptions<EmbeddingOptions>` ctor
args, both already registered. `IngestionService`'s new ports resolve from the same
provider. While here, fix the pre-existing harmless duplicate `IWikiFileStore`
registration (lines 40 & 43).

### 9. Tests

- **`tests/LlmWiki.Agents.Tests/IngestionServiceTests.cs`** (update): the existing
  hand-rolled fakes pattern (`ScriptedChat`) — add in-memory `FakeEmbeddingService`
  (returns a fixed 768-length vector) and `FakeVectorStore` (records `Upsert` calls).
  Update `IngestionService` construction with the new ctor args. New assertion: after
  ingest, the vector store received an upsert for **each** created/updated page path in
  the report (and a contradiction-noted page). Also assert an embedding-store failure is
  recorded as a `Failed` outcome, not thrown (NFR-06).
- **`tests/LlmWiki.Infrastructure.Tests/OracleVectorStoreTests.cs`** (new, opt-in
  integration): env-gated (skip when `ORACLE_CONNECTION_STRING` unset, like a
  `[SkippableFact]`/guard) — ensure-schema, upsert two pages, assert a paraphrased query
  ranks the semantically-right page first (vector arm) and an exact term returns its page
  (lexical arm). Cleans up its rows.
- **DI registration** test still passes (stub → real is resolve-compatible).

---

## Requirements covered

BR-030 (embed every content page + metadata), BR-031 (vector + Oracle Text indexes),
BR-032 (type filter), BR-033 (embed only changed pages), BR-034 (configurable strategy),
BR-035 (hybrid vector + full-text), NFR-10 (per-wiki isolation via `wiki_name` predicate),
NFR-07 (SK/Oracle confined to Infrastructure), NFR-06 (best-effort, wiki never corrupted).

---

## Verification (end-to-end)

1. **Infra up:** `cd docker && docker compose up -d`; confirm oracle + ollama healthy;
   `docker compose exec ollama ollama pull nomic-embed-text`. Ensure `env/.env` has
   `ORACLE_CONNECTION_STRING` and a working `CHAT_PROVIDER` (keyless: `ollama`+`llama3.1`).
2. **(Optional) Prove primitives:** run [spike-vector.sql](docker/oracle/spike-vector.sql)
   once via sqlplus to confirm VECTOR + Text on this container.
3. **Build & unit test:** `dotnet build LlmWiki.slnx` and `dotnet test LlmWiki.slnx`
   (fakes-based ingestion embed test is green; Oracle integration test skips without a
   connection string, runs when set).
4. **Ingest → embed:** `dotnet run --project src/LlmWiki.Cli -- wiki create demo` then
   `... -- ingest demo ./docs/sample-source.md`. Report shows created/updated pages.
5. **Oracle inspection (BR-030/031):** via sqlplus —
   `SELECT wiki_name, path, title, type FROM wiki_page;` count equals the wiki's page
   count; `SELECT index_name, index_type FROM user_indexes WHERE table_name='WIKI_PAGE';`
   shows the DOMAIN (CTXSYS.CONTEXT) index; each row has a non-null `emb`.
6. **Hybrid search (BR-035):**
   - Paraphrased query whose words don't match a title →
     `... -- search demo "how the thing actually works" --top-k 5` returns the right page
     (vector arm).
   - Exact entity name/technical term → returns its page (lexical arm), e.g.
     `... -- search demo "AcmeCorp" ` and `... -- search demo "entity term" --type entity`.
7. **Embed-on-change (BR-033):** re-ingest a source that changes one page; confirm only
   that page's `updated_at` advanced in `wiki_page` (others unchanged).
8. **Isolation (NFR-10):** create a second wiki, ingest a different source, search each —
   neither returns the other's pages.
