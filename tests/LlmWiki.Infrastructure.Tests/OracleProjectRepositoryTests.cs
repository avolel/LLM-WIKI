using LlmWiki.Infrastructure.Persistence;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;

namespace LlmWiki.Infrastructure.Tests;

/// <summary>
/// Opt-in integration tests for the real Oracle project registry (Phase 6). They only run when
/// ORACLE_CONNECTION_STRING points at a live 23ai container (schema ensured on first use);
/// otherwise they no-op so CI without a database stays green. Each test cleans up its row under a
/// unique project name.
/// </summary>
public sealed class OracleProjectRepositoryTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("ORACLE_CONNECTION_STRING");

    private static OracleProjectRepository NewRepo() =>
        new(Options.Create(new OracleOptions { ConnectionString = ConnectionString! }));

    [Fact]
    public async Task Register_IsIdempotent_And_Get_List_ReturnTheRow()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;   // opt-in: skip without a live DB

        var name = $"itest_{Guid.NewGuid():N}";
        var repo = NewRepo();
        try
        {
            await repo.RegisterAsync(name);
            await repo.RegisterAsync(name);   // second call must not throw or duplicate

            var got = await repo.GetAsync(name);
            Assert.NotNull(got);
            Assert.Equal(name, got!.Name);
            Assert.Null(got.LastIngestAt);
            Assert.Equal(0, got.PageCount);
            Assert.Equal(0, got.SourceCount);

            var all = await repo.ListAsync();
            Assert.Contains(all, p => p.Name == name);
        }
        finally
        {
            await DeleteProjectAsync(name);
        }
    }

    [Fact]
    public async Task RecordIngest_StampsLastIngest_And_Counts_And_UpsertsWhenUnregistered()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;

        var name = $"itest_{Guid.NewGuid():N}";
        var repo = NewRepo();
        try
        {
            // Upsert path: RecordIngest works even though the project was never explicitly registered.
            await repo.RecordIngestAsync(name, pageCount: 7, sourceCount: 3);

            var got = await repo.GetAsync(name);
            Assert.NotNull(got);
            Assert.NotNull(got!.LastIngestAt);
            Assert.Equal(7, got.PageCount);
            Assert.Equal(3, got.SourceCount);
        }
        finally
        {
            await DeleteProjectAsync(name);
        }
    }

    private static async Task DeleteProjectAsync(string name)
    {
        await using var conn = new OracleConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM wiki_project WHERE name = :name";
        cmd.BindByName = true;
        cmd.Parameters.Add(":name", name);
        await cmd.ExecuteNonQueryAsync();
    }
}
