using LlmWiki.Agents.Ingestion;              // EmbeddingText
using LlmWiki.Application.Indexing;
using LlmWiki.Application.Ports;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Agents.Indexing;

/// <summary>
/// Plain orchestrator (ports only, no SK) that embeds every existing page of a wiki into the
/// <see cref="IVectorStore"/>. Mirrors ingestion's embed-on-change step but over the full page set,
/// so wikis authored before Phase 4 (or while Oracle was unreachable) can be backfilled.
/// </summary>
public sealed class WikiIndexer(
    IWikiRepository wiki,
    IEmbeddingService embeddings,
    IVectorStore vectors,
    IOptions<EmbeddingOptions> embedOptions) : IWikiIndexer
{
    private readonly EmbeddingStrategy _strategy = embedOptions.Value.Strategy;

    public async Task<ReindexReport> ReindexAsync(string wikiName, CancellationToken ct = default)
    {
        var paths = await wiki.ListPagesAsync(wikiName, ct);   // excludes SCHEMA/index/log/raw already
        var failures = new List<PageEmbedFailure>();
        var embedded = 0;

        foreach (var path in paths)
        {
            try
            {
                var page = await wiki.ReadPageAsync(wikiName, path, ct);
                var text = EmbeddingText.For(page, _strategy);   // BR-034 configurable
                var vector = await embeddings.EmbedAsync(text, ct);
                await vectors.UpsertAsync(wikiName, path, page, vector, ct);
                embedded++;
            }
            catch (Exception ex)
            {
                failures.Add(new PageEmbedFailure(path, ex.Message));   // recorded, never thrown
            }
        }

        return new ReindexReport(wikiName, embedded, failures);
    }
}