using System.Data.Common;
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

    private static ProjectInfo Map(DbDataReader r) => new(
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
