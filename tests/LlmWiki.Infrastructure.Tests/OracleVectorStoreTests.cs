using LlmWiki.Domain;
using LlmWiki.Infrastructure.VectorStore;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;
using Oracle.ManagedDataAccess.Client;

namespace LlmWiki.Infrastructure.Tests;

/// <summary>
/// Opt-in integration tests for the real Oracle adapter. They only run when
/// ORACLE_CONNECTION_STRING points at a live 23ai container (Docker up + schema ensured on
/// first use); otherwise they no-op so CI without a database stays green. Each test cleans up
/// its rows under a unique wiki name.
/// </summary>
public sealed class OracleVectorStoreTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("ORACLE_CONNECTION_STRING");

    private static OracleVectorStore NewStore() => new(
        Options.Create(new OracleOptions { ConnectionString = ConnectionString! }),
        Options.Create(new EmbeddingOptions()));

    [Fact]
    public async Task Hybrid_Ranks_SemanticNeighbour_And_ExactTerm()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;   // opt-in: skip without a live DB

        var wiki = $"itest_{Guid.NewGuid():N}";
        var store = NewStore();
        try
        {
            var animals = new WikiPage
            {
                Title = "Domestic cats",
                Type = PageType.Entity,
                Content = "A feline kept as a pet; it purrs, hunts mice, and grooms its fur.",
            };
            var vehicles = new WikiPage
            {
                Title = "Diesel locomotives",
                Type = PageType.Entity,
                Content = "A railway engine that pulls freight wagons along steel tracks.",
            };

            // Distinct embeddings so the semantic arm can separate the two pages.
            await store.UpsertAsync(wiki, "entities/cats.md", animals, Axis(0), default);
            await store.UpsertAsync(wiki, "entities/trains.md", vehicles, Axis(1), default);

            // Semantic arm: a query vector aligned with the cats page ranks it first.
            var semantic = await store.SearchAsync(wiki, "household pet feline", Axis(0), topK: 2);
            Assert.NotEmpty(semantic);
            Assert.Equal("entities/cats.md", semantic[0].RelativePath);

            // Lexical arm: an exact body term returns its page.
            var lexical = await store.SearchAsync(wiki, "locomotives", Axis(0), topK: 2);
            Assert.Contains(lexical, h => h.RelativePath == "entities/trains.md");

            // Type filter (BR-032) keeps results within the type.
            var typed = await store.SearchAsync(wiki, "feline", Axis(0), topK: 2, typeFilter: PageType.Entity);
            Assert.All(typed, h => Assert.Equal(PageType.Entity, h.Type));
        }
        finally
        {
            await DeleteWikiRowsAsync(wiki);
        }
    }

    /// <summary>A 768-dim unit vector pointing along one axis — gives well-separated cosine directions.</summary>
    private static ReadOnlyMemory<float> Axis(int index)
    {
        var v = new float[768];
        v[index] = 1f;
        return v;
    }

    private static async Task DeleteWikiRowsAsync(string wiki)
    {
        await using var conn = new OracleConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM wiki_page WHERE wiki_name = :wiki";
        cmd.BindByName = true;
        cmd.Parameters.Add(":wiki", wiki);
        await cmd.ExecuteNonQueryAsync();
    }
}