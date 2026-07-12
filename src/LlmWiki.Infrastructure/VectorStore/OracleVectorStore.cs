using System.Globalization;
using System.Text;
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;

namespace LlmWiki.Infrastructure.VectorStore;

/// <summary>
/// Real Oracle 23ai adapter for <see cref="IVectorStore"/> (Phase 4). Stores one row per page keyed
/// by <c>(wiki_name, path)</c> with a 768-dim <c>VECTOR</c> column and an Oracle Text index over the
/// body, and answers hybrid search: cosine <c>VECTOR_DISTANCE</c> (semantic) fused with Oracle Text
/// <c>CONTAINS</c> (lexical) via reciprocal-rank fusion. Confined to Infrastructure (NFR-07); replaces
/// the Phase 0 stub (stub convention — no parallel type). SQL primitives validated in
/// <c>docker/oracle/spike-vector.sql</c>; canonical DDL committed as <c>docker/oracle/02-schema.sql</c>.
/// </summary>
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
        cmd.Parameters.Add(":title", Truncate(page.Title, 400));
        cmd.Parameters.Add(":type", page.Type.ToString());
        cmd.Parameters.Add(":tags", Truncate(string.Join(",", page.Tags), 2000));
        cmd.Parameters.Add(":snippet", Snippet(page.Content));
        cmd.Parameters.Add(new OracleParameter(":content", OracleDbType.Clob) { Value = page.Content });
        // The vector literal exceeds VARCHAR2(4000) at 768 dims; bind as CLOB so TO_VECTOR always fits.
        cmd.Parameters.Add(new OracleParameter(":emb", OracleDbType.Clob) { Value = ToVectorLiteral(embedding.Span) });
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
                cmd.Parameters.Add(new OracleParameter(":emb", OracleDbType.Clob) { Value = ToVectorLiteral(queryEmbedding.Span) });
                cmd.Parameters.Add(":wiki", wikiName);
                if (typeFilter is not null) cmd.Parameters.Add(":type", typeFilter.ToString());
                cmd.Parameters.Add(":fetch", fetch);
            }, ct);

        // Lexical arm: Oracle Text. Skip gracefully when the query has no indexable terms.
        var containsQuery = ToContainsQuery(queryText);
        var lexical = string.IsNullOrEmpty(containsQuery)
            ? new List<Row>()
            : await QueryAsync(conn,
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
                    cmd.Parameters.Add(":q", containsQuery);
                    cmd.Parameters.Add(":fetch", fetch);
                }, ct, ignoreTextErrors: true);

        return Fuse(vector, lexical, wikiName, topK);
    }

    // --- Reciprocal-rank fusion: score = Σ 1/(k + rank); k=60 is the standard constant. ---
    private static IReadOnlyList<VectorSearchHit> Fuse(
        List<Row> vector, List<Row> lexical, string wikiName, int topK)
    {
        const double k = 60;
        var scores = new Dictionary<string, (Row row, double score)>(StringComparer.Ordinal);

        void Accumulate(List<Row> rows)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var add = 1.0 / (k + i + 1);
                scores[r.Path] = scores.TryGetValue(r.Path, out var e)
                    ? (e.row, e.score + add)
                    : (r, add);
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
        {
            throw new InvalidOperationException(
                "ORACLE_CONNECTION_STRING is not configured (set it in env/.env).");
        }

        var conn = new OracleConnection(_oracle.ConnectionString);
        await conn.OpenAsync(ct);
        await EnsureSchemaAsync(conn, ct);
        return conn;
    }

    /// <summary>
    /// Idempotently create the <c>wiki_page</c> table + Oracle Text index once per process. Init
    /// scripts don't re-run on an existing container, so this is the runtime safety net; the
    /// canonical DDL lives in <c>docker/oracle/02-schema.sql</c>.
    /// </summary>
    private async Task EnsureSchemaAsync(OracleConnection conn, CancellationToken ct)
    {
        if (_schemaReady) return;
        await _schemaGate.WaitAsync(ct);
        try
        {
            if (_schemaReady) return;

            if (await ScalarAsync(conn, "SELECT COUNT(*) FROM user_tables WHERE table_name = 'WIKI_PAGE'", ct) == 0)
            {
                await ExecuteAsync(conn,
                    $"""
                     CREATE TABLE wiki_page (
                       wiki_name  VARCHAR2(128)  NOT NULL,
                       path       VARCHAR2(400)  NOT NULL,
                       title      VARCHAR2(400),
                       type       VARCHAR2(20),
                       tags       VARCHAR2(2000),
                       snippet    VARCHAR2(2000),
                       content    CLOB,
                       emb        VECTOR({_dim}, FLOAT32),
                       updated_at TIMESTAMP,
                       CONSTRAINT wiki_page_pk PRIMARY KEY (wiki_name, path)
                     )
                     """, ct);
            }

            if (await ScalarAsync(conn, "SELECT COUNT(*) FROM user_indexes WHERE index_name = 'WIKI_PAGE_TXT'", ct) == 0)
            {
                await ExecuteAsync(conn,
                    "CREATE INDEX wiki_page_txt ON wiki_page (content) INDEXTYPE IS CTXSYS.CONTEXT", ct);
            }

            _schemaReady = true;
        }
        finally
        {
            _schemaGate.Release();
        }
    }

    private static async Task<List<Row>> QueryAsync(
        OracleConnection conn, string sql, Action<OracleCommand> bind, CancellationToken ct,
        bool ignoreTextErrors = false)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.BindByName = true;
        bind(cmd);

        var rows = new List<Row>();
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                rows.Add(new Row(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }
        catch (OracleException) when (ignoreTextErrors)
        {
            // A malformed CONTAINS (DRG-*) yields no lexical hits rather than failing the whole search;
            // the vector arm still answers.
            return new List<Row>();
        }

        return rows;
    }

    private static async Task ExecuteAsync(OracleConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<decimal> ScalarAsync(OracleConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null ? 0 : Convert.ToDecimal(result, CultureInfo.InvariantCulture);
    }

    private readonly record struct Row(string Path, string Title, string Type, string? Snippet);

    /// <summary>Render a vector as the <c>[f,f,…]</c> literal TO_VECTOR expects (invariant culture).</summary>
    private static string ToVectorLiteral(ReadOnlySpan<float> v)
    {
        var sb = new StringBuilder(v.Length * 10 + 2);
        sb.Append('[');
        for (var i = 0; i < v.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(v[i].ToString("R", CultureInfo.InvariantCulture));
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>
    /// Sanitize free text into a safe Oracle Text query: keep alphanumeric tokens, brace-escape each so
    /// reserved operators (<c>&amp;|~-,</c>) match literally, and OR them (recall arm — fusion handles
    /// precision). Returns "" when nothing indexable remains, so the caller skips the lexical arm.
    /// </summary>
    private static string ToContainsQuery(string queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText)) return "";

        var tokens = new List<string>();
        foreach (var raw in queryText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var cleaned = new string(raw.Where(char.IsLetterOrDigit).ToArray());
            if (cleaned.Length > 0) tokens.Add($"{{{cleaned}}}");
        }

        return string.Join(" OR ", tokens);
    }

    private static string Snippet(string content) =>
        content.Length <= 2000 ? content : content[..2000];

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static PageType ParseType(string s) =>
        Enum.TryParse<PageType>(s, ignoreCase: true, out var t) ? t : PageType.Summary;

    public async Task DeleteWikiAsync(string wikiName, CancellationToken cancellationToken = default)
    {
        await using var conn = await OpenAsync(cancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM wiki_page WHERE wiki_name = :wiki";
        cmd.BindByName = true;
        cmd.Parameters.Add(":wiki", wikiName);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}