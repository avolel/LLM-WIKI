using LlmWiki.Application.Ports;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;

namespace LlmWiki.Infrastructure.Persistence;

/// <summary>
/// Real ODP.NET connectivity probe used by Phase 0 diagnostics. Opens a connection and runs a
/// CREATE TABLE / DROP round-trip against the application schema, proving the container is up
/// and the schema can accept DDL.
/// </summary>
public sealed class OracleDatabaseHealthCheck(IOptions<OracleOptions> options) : IDatabaseHealthCheck
{
    private readonly OracleOptions _options = options.Value;

    public async Task CreateTableRoundtripAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new InvalidOperationException(
                "ORACLE_CONNECTION_STRING is not configured (set it in env/.env).");
        }

        // Unique, schema-safe table name; <=30 chars for older identifier limits.
        var tableName = $"DIAG_{Guid.NewGuid():N}"[..28].ToUpperInvariant();

        await using var connection = new OracleConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await ExecuteAsync(connection, $"CREATE TABLE {tableName} (id NUMBER)", cancellationToken);
        try
        {
            await ExecuteAsync(connection, $"INSERT INTO {tableName} (id) VALUES (1)", cancellationToken);
        }
        finally
        {
            // Always drop the probe table, even if the insert failed.
            await ExecuteAsync(connection, $"DROP TABLE {tableName} PURGE", cancellationToken);
        }
    }

    private static async Task ExecuteAsync(OracleConnection connection, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }
}
