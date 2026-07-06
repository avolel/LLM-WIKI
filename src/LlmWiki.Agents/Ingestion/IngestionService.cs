using System.Text;
using System.Text.Json;
using LlmWiki.Agents.Prompts;
using LlmWiki.Application.Ingestion;
using LlmWiki.Application.Ports;
using LlmWiki.Domain;

namespace LlmWiki.Agents.Ingestion;

/// <summary>
/// Plain orchestrator for source ingestion (BR-010…BR-016). Discrete steps; per-page write boundaries
/// so a single failure is recorded, not fatal (NFR-06). Uses only Application ports — no SK types — so
/// the Process Framework can later replace it behind <see cref="IIngestionService"/>.
/// </summary>
public sealed class IngestionService(
    IChatService chat,
    IWikiRepository wiki) : IIngestionService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<IngestionReport> IngestAsync(
        string wikiName, string sourceRelativePath, string sourceContent, CancellationToken ct = default)
    {
        var schema = await wiki.ReadSchemaAsync(wikiName, ct);
        var existing = await wiki.ListPagesAsync(wikiName, ct);

        var extraction = await ExtractAsync(schema, sourceContent, ct);

        var outcomes = new List<PageOutcome>();
        var gaps = new List<KnowledgeGap>();

        // Summary page (BR-012) — one per source.
        var summaryPath = $"summaries/{Slug.From(extraction.SourceTitle)}.md";
        await WritePageAsync(wikiName, summaryPath, new WikiPage
        {
            Title = extraction.SourceTitle,
            Type = PageType.Summary,
            Tags = extraction.Tags,
            Sources = [sourceRelativePath],
            Content = BuildSummaryBody(extraction),
        }, outcomes, ct);

        // Entity & concept pages (BR-013, BR-015).
        await WriteItemsAsync(wikiName, "entities", PageType.Entity, extraction.Entities, sourceRelativePath, existing, outcomes, gaps, ct);
        await WriteItemsAsync(wikiName, "concepts", PageType.Concept, extraction.Concepts, sourceRelativePath, existing, outcomes, gaps, ct);

        // Topic overview connecting the source to existing knowledge (BR-013).
        if (!string.IsNullOrWhiteSpace(extraction.TopicTitle))
        {
            var topicPath = $"topics/{Slug.From(extraction.TopicTitle)}.md";
            await WritePageAsync(wikiName, topicPath, new WikiPage
            {
                Title = extraction.TopicTitle,
                Type = PageType.Overview,
                Tags = extraction.Tags,
                Sources = [sourceRelativePath],
                Content = BuildTopicBody(extraction, schema, topicPath),
            }, outcomes, ct);
        }

        // Light contradiction pass against existing pages we touched by name (BR-014).
        var contradictions = await ReconcileAsync(wikiName, extraction, existing, ct);

        return new IngestionReport(wikiName, sourceRelativePath, outcomes, contradictions, gaps);
    }

    private async Task<ExtractionResult> ExtractAsync(WikiSchema schema, string source, CancellationToken ct)
    {
        var raw = await chat.CompleteAsync(IngestionPrompts.Extract(schema, source), ct);
        return JsonSerializer.Deserialize<ExtractionResult>(StripFence(raw), Json)
               ?? throw new InvalidOperationException("Extraction returned no parseable JSON.");
    }

    private async Task WriteItemsAsync(
        string wikiName, string dir, PageType type, IReadOnlyList<ExtractedItem> items,
        string sourcePath, IReadOnlyList<string> existing,
        List<PageOutcome> outcomes, List<KnowledgeGap> gaps, CancellationToken ct)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Name)) continue;
            var path = $"{dir}/{Slug.From(item.Name)}.md";

            if (existing.Contains(path))
            {
                // Append the new source's contribution and add provenance.
                var current = await wiki.ReadPageAsync(wikiName, path, ct);
                var merged = current with
                {
                    Content = $"{current.Content}\n\n## From {sourcePath}\n\n{item.Description}",
                    Sources = current.Sources.Contains(sourcePath) ? current.Sources : [.. current.Sources, sourcePath],
                    UpdatedAt = DateTimeOffset.UtcNow,
                };
                await WritePageAsync(wikiName, path, merged, outcomes, ct, PageChange.Updated);
            }
            else
            {
                if (item.Thin) gaps.Add(new KnowledgeGap(item.Name, "Mentioned in passing; stub created."));
                await WritePageAsync(wikiName, path, new WikiPage
                {
                    Title = item.Name,
                    Type = type,
                    Sources = [sourcePath],
                    Content = item.Description,
                }, outcomes, ct, item.Thin ? PageChange.StubCreated : PageChange.Created);
            }
        }
    }

    private async Task<IReadOnlyList<Contradiction>> ReconcileAsync(
        string wikiName, ExtractionResult extraction, IReadOnlyList<string> existing, CancellationToken ct)
    {
        var names = extraction.Entities.Concat(extraction.Concepts).Select(i => i.Name);
        var matched = names
            .Select(n => $"entities/{Slug.From(n)}.md")
            .Concat(names.Select(n => $"concepts/{Slug.From(n)}.md"))
            .Where(existing.Contains)
            .Distinct()
            .ToList();
        if (matched.Count == 0) return [];

        var block = new StringBuilder();
        foreach (var path in matched)
        {
            var page = await wiki.ReadPageAsync(wikiName, path, ct);
            block.AppendLine($"### {path}\n{page.Content}\n");
        }

        var raw = await chat.CompleteAsync(IngestionPrompts.Reconcile(extraction.Summary, block.ToString()), ct);
        var result = JsonSerializer.Deserialize<ReconcileResult>(StripFence(raw), Json) ?? new ReconcileResult();

        var contradictions = new List<Contradiction>();
        foreach (var c in result.Contradictions.Where(c => existing.Contains(c.Page)))
        {
            // Note the discrepancy on the relevant page rather than overwrite (BR-014).
            var page = await wiki.ReadPageAsync(wikiName, c.Page, ct);
            await wiki.WritePageAsync(wikiName, c.Page, page with
            {
                Content = $"{page.Content}\n\n> **Contradiction noted:** {c.Description}",
                UpdatedAt = DateTimeOffset.UtcNow,
            }, ct);
            contradictions.Add(new Contradiction(c.Page, c.Description));
        }
        return contradictions;
    }

    private async Task WritePageAsync(
        string wikiName, string path, WikiPage page,
        List<PageOutcome> outcomes, CancellationToken ct, PageChange change = PageChange.Created)
    {
        try
        {
            await wiki.WritePageAsync(wikiName, path, page, ct);
            outcomes.Add(new PageOutcome(path, page.Title, change));
        }
        catch (Exception ex)
        {
            outcomes.Add(new PageOutcome(path, page.Title, PageChange.Failed, ex.Message));
        }
    }

    private static string BuildSummaryBody(ExtractionResult e)
    {
        var sb = new StringBuilder();
        sb.AppendLine(e.Summary).AppendLine();
        if (e.KeyPoints.Count > 0)
        {
            sb.AppendLine("## Key points").AppendLine();
            foreach (var p in e.KeyPoints) sb.AppendLine($"- {p}");
        }
        return sb.ToString().TrimEnd();
    }

    private static string BuildTopicBody(ExtractionResult e, WikiSchema schema, string topicPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine(e.TopicSummary).AppendLine();
        var links = e.Entities.Concat(e.Concepts)
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .Select(i =>
            {
                var dir = e.Entities.Contains(i) ? "entities" : "concepts";
                var target = $"{dir}/{Slug.From(i.Name)}.md";
                return "- " + CrossReferenceWriter.Link(i.Name, target, topicPath, schema.LinkStyle);
            })
            .ToList();
        if (links.Count > 0)
        {
            sb.AppendLine("## Related").AppendLine();
            foreach (var l in links) sb.AppendLine(l);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Tolerate models that wrap JSON in a ```json fence.</summary>
    private static string StripFence(string s)
    {
        s = s.Trim();
        if (!s.StartsWith("```")) return s;
        var firstNl = s.IndexOf('\n');
        var body = firstNl >= 0 ? s[(firstNl + 1)..] : s;
        var fenceEnd = body.LastIndexOf("```", StringComparison.Ordinal);
        return (fenceEnd >= 0 ? body[..fenceEnd] : body).Trim();
    }
}
