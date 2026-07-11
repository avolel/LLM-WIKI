using System.Text;
using System.Text.Json;
using LlmWiki.Agents.Ingestion;
using LlmWiki.Agents.Prompts;
using LlmWiki.Application.Ingestion;
using LlmWiki.Application.Ports;
using LlmWiki.Application.Query;
using LlmWiki.Domain;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Agents.Query;

/// <summary>
/// Plain orchestrator for the query/synthesis workflow. Mirrors
/// <see cref="IngestionService"/>: primary-ctor DI over Application ports only (no Semantic Kernel types),
/// a single JSON-mode LLM call parsed through the same fence-stripper, and a best-effort embed
/// step on save so a failure never corrupts the wiki. The Semantic Kernel Process Framework can later
/// replace it behind <see cref="IQueryService"/>.
/// </summary>
public sealed class QueryService(
    IChatService chat,
    IWikiRepository wiki,
    IWikiFileStore files,          // to read index.md (IWikiJournal has no read port)
    IVectorStore vectors,
    IEmbeddingService embeddings,
    IWikiJournal journal,
    IOptions<EmbeddingOptions> embedOptions) : IQueryService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly EmbeddingStrategy _strategy = embedOptions.Value.Strategy;

    public async Task<QueryResult> AnswerAsync(
        string wikiName, string question,
        IReadOnlyList<ConversationTurn> history, QueryOptions options,
        CancellationToken ct = default)
    {
        // 1. Read the index (BR-040). Best-effort: an absent/unreadable index is not fatal.
        string index;
        try { index = await files.ReadAsync($"{wikiName}/index.md", ct); }
        catch { index = string.Empty; }

        var schema = await wiki.ReadSchemaAsync(wikiName, ct);

        // 2. Hybrid search: embed the question, then rank pages (identical to the `search` command).
        var embedding = await embeddings.EmbedAsync(question, ct);
        var hits = await vectors.SearchAsync(
            wikiName, question, embedding, options.TopK, options.TypeFilter, ct);

        // 3. Hydrate each hit to full content (VectorSearchHit carries no body) → CONTEXT block.
        var hitByPath = new Dictionary<string, VectorSearchHit>(StringComparer.Ordinal);
        var context = new StringBuilder();
        foreach (var h in hits)
        {
            hitByPath[h.RelativePath] = h;
            try
            {
                var page = await wiki.ReadPageAsync(wikiName, h.RelativePath, ct);
                context.AppendLine($"### {h.RelativePath} — {page.Title}");
                context.AppendLine(page.Content);
                context.AppendLine();
            }
            catch { /* a hit that no longer resolves on disk is skipped, not fatal */ }
        }

        // 4. Single-shot synthesis (non-streaming). Reuse the fence-stripper.
        var raw = await chat.CompleteAsync(
            QueryPrompts.Synthesize(schema, index, question, context.ToString(), history),
            jsonMode: true, ct);
        var synthesis = JsonSerializer.Deserialize<SynthesisResult>(ExtractJson(raw), Json)
                        ?? new SynthesisResult();

        // 5. Keep only citations that were actually retrieved (resolvable, BR-041); attach title/type.
        var citations = synthesis.Citations
            .Where(hitByPath.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .Select(p => new Citation(hitByPath[p].RelativePath, hitByPath[p].Title, hitByPath[p].Type))
            .ToList();

        var title = string.IsNullOrWhiteSpace(synthesis.Title) ? question : synthesis.Title;

        return new QueryResult(
            wikiName, question, synthesis.Answer, synthesis.Covered, citations, title);
    }

    public async Task<PageOutcome> SaveAnswerAsync(
        string wikiName, QueryResult result, CancellationToken ct = default)
    {
        var path = $"answers/{Slug.From(result.SuggestedTitle)}.md";
        var page = new WikiPage
        {
            Title = result.SuggestedTitle,
            Type = PageType.Answer,
            Content = result.Answer,
            Sources = result.Citations.Select(c => c.RelativePath).ToList(),
        };

        // Write boundary: a failure is returned as a Failed outcome, never thrown.
        try
        {
            await wiki.WritePageAsync(wikiName, path, page, ct);
        }
        catch (Exception ex)
        {
            return new PageOutcome(path, page.Title, PageChange.Failed, ex.Message);
        }

        string? detail = null;

        // Journal: regenerate index.md (the new page appears) + append a greppable log line.
        // Best-effort — the page is already saved, so a journal failure is recorded, not thrown.
        try
        {
            await journal.RebuildIndexAsync(wikiName, ct);
            await journal.AppendLogAsync(wikiName, BuildLogEntry(result, path), ct);
        }
        catch (Exception ex)
        {
            detail = $"journal: {ex.Message}";
        }

        // Best-effort embed so the saved answer is itself searchable (BR-045). A failure
        // (e.g. Oracle down) is recorded on the outcome detail, never thrown.
        try
        {
            var text = EmbeddingText.For(page, _strategy);
            var vector = await embeddings.EmbedAsync(text, ct);
            await vectors.UpsertAsync(wikiName, path, page, vector, ct);
        }
        catch (Exception ex)
        {
            var embed = $"embed: {ex.Message}";
            detail = detail is null ? embed : $"{detail}; {embed}";
        }

        return new PageOutcome(path, page.Title, PageChange.Created, detail);
    }

    private static LogEntry BuildLogEntry(QueryResult result, string path)
    {
        var body = new StringBuilder();
        body.AppendLine($"- Saved: {path}");
        body.AppendLine($"- Citations: {result.Citations.Count}");
        return new LogEntry(
            DateOnly.FromDateTime(DateTime.UtcNow), "query", result.Question, body.ToString().TrimEnd());
    }

    /// <summary>Recover the JSON object from a reply — same tolerance as ingestion (json_object
    /// mode makes this rare, but local models are not perfectly obedient).</summary>
    private static string ExtractJson(string s)
    {
        s = s.Trim();
        if (s.StartsWith("```"))
        {
            var firstNl = s.IndexOf('\n');
            var body = firstNl >= 0 ? s[(firstNl + 1)..] : s;
            var fenceEnd = body.LastIndexOf("```", StringComparison.Ordinal);
            s = (fenceEnd >= 0 ? body[..fenceEnd] : body).Trim();
        }
        if (!s.StartsWith('{'))
        {
            var open = s.IndexOf('{');
            var close = s.LastIndexOf('}');
            if (open >= 0 && close > open) s = s[open..(close + 1)];
        }
        return s;
    }
}
