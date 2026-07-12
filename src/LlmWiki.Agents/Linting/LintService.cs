using System.Text;
using System.Text.Json;
using LlmWiki.Agents.Ingestion;      // EmbeddingText.For
using LlmWiki.Agents.Prompts;
using LlmWiki.Application.Ingestion;  // PageOutcome / PageChange
using LlmWiki.Application.Linting;
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Agents.Linting;

/// <summary>
/// Plain orchestrator for the lint / health-check workflow (BR-060…063). Mirrors
/// <see cref="Query.QueryService"/>: primary-ctor DI over Application ports only (no Semantic Kernel
/// types), a single JSON-mode LLM call parsed through the same fence-stripper, and a best-effort
/// apply step that never corrupts the wiki (NFR-06). <see cref="LintAsync"/> is a deterministic
/// structural pass fused with one best-effort semantic call; <see cref="ApplyFixAsync"/> writes a
/// stub page then best-effort rebuilds the index, logs, and embeds — shaped exactly like
/// <c>QueryService.SaveAnswerAsync</c>.
/// </summary>
public sealed class LintService(
    IChatService chat,
    IWikiRepository wiki,
    IWikiFileStore files,          // to read index.md for the LLM digest (IWikiJournal has no read port)
    IWikiJournal journal,
    IVectorStore vectors,
    IEmbeddingService embeddings,
    IOptions<EmbeddingOptions> embedOptions) : ILintService
{
    private const int ThinContentThreshold = 200;   // chars; pages shorter than this are "thin" (BR-060)
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // The typed content directories ingestion actually writes. Note this is a superset of
    // WikiSchema.Directories, which scaffolds "summaries/entities/topics/raw" but not "concepts"
    // (IngestionService mkdirs concepts/ on first write). A stub is only created into one of these.
    private static readonly HashSet<string> TypedDirs =
        new(["summaries", "entities", "concepts", "topics"], StringComparer.Ordinal);

    private readonly EmbeddingStrategy _strategy = embedOptions.Value.Strategy;

    public async Task<LintReport> LintAsync(string wikiName, CancellationToken ct = default)
    {
        var schema = await wiki.ReadSchemaAsync(wikiName, ct);
        var paths = await wiki.ListPagesAsync(wikiName, ct);
        var findings = new List<LintFinding>();

        // 1. Structural pass (deterministic, reliable). Reuse ResolveLinksAsync per page — it also
        //    yields the resolved inbound-link graph we need for orphans, with no new parsing code.
        var inbound = new HashSet<string>(StringComparer.Ordinal);
        var digest = new StringBuilder();
        foreach (var path in paths)
        {
            var page = await wiki.ReadPageAsync(wikiName, path, ct);
            var contentLength = page.Content.Trim().Length;
            if (contentLength < ThinContentThreshold)
                findings.Add(new LintFinding(LintSeverity.Suggestion, LintCategory.ThinPage,
                    $"'{path}' is thin ({contentLength} chars) — consider expanding.", [path]));

            var links = await wiki.ResolveLinksAsync(wikiName, path, ct);
            foreach (var l in links.Links)
            {
                if (l.Exists) { inbound.Add(l.ResolvedPath!); continue; }
                // A broken link is a warning; when the intended target normalizes to an unambiguous
                // typed path (dir/name.md) we attach a stub-creation Fix so it doubles as a "missing
                // page" the user can accept (BR-060 broken-link + missing-page; BR-063 applyable).
                var candidate = ResolveIntendedPath(schema.LinkStyle, path, l.Reference.Target);
                var fix = TryStubFix(candidate);
                findings.Add(new LintFinding(LintSeverity.Warning,
                    fix is null ? LintCategory.BrokenLink : LintCategory.MissingPage,
                    $"'{path}' links to '{l.Reference.Target}' which does not resolve.",
                    [path],
                    fix is null ? "Fix or remove the link." : $"Create stub {fix.RelativePath}.",
                    fix));
            }

            digest.AppendLine($"### {path} — {page.Title} [{page.Type}]");
            digest.AppendLine(page.Content);
            digest.AppendLine();
        }

        // Orphans: content pages nothing links to (BR-060). Literal definition; advisory.
        foreach (var path in paths.Where(p => !inbound.Contains(p)))
            findings.Add(new LintFinding(LintSeverity.Warning, LintCategory.Orphan,
                $"'{path}' has no inbound links (orphan).", [path], "Link it from a related page."));

        // 2. Semantic pass (one LLM call). Best-effort — a chat/parse failure drops semantic findings
        //    but the structural report still returns (NFR-06).
        try
        {
            string index;
            try { index = await files.ReadAsync($"{wikiName}/index.md", ct); } catch { index = string.Empty; }
            var raw = await chat.CompleteAsync(LintPrompts.Analyze(schema, index, digest.ToString()), jsonMode: true, ct);
            var analysis = JsonSerializer.Deserialize<LintAnalysis>(ExtractJson(raw), Json) ?? new LintAnalysis();

            foreach (var c in analysis.Contradictions.Where(c => c.Pages.Any(paths.Contains)))
                findings.Add(new LintFinding(LintSeverity.Critical, LintCategory.Contradiction,
                    c.Description, c.Pages.Where(paths.Contains).ToList(), "Reconcile or note the discrepancy."));
            foreach (var s in analysis.StaleClaims.Where(s => paths.Contains(s.Page)))
                findings.Add(new LintFinding(LintSeverity.Critical, LintCategory.StaleClaim,
                    s.Description, [s.Page], "Review and update the claim."));
            foreach (var q in analysis.Questions)
                findings.Add(new LintFinding(LintSeverity.Suggestion, LintCategory.SuggestedQuestion, q, []));
            foreach (var src in analysis.Sources)
                findings.Add(new LintFinding(LintSeverity.Suggestion, LintCategory.SuggestedSource, src, []));
        }
        catch { /* semantic findings are best-effort; structural report is authoritative */ }

        // 3. Prioritise (BR-061). Stable within a severity preserves the deterministic-pass order.
        var sorted = findings.OrderBy(f => (int)f.Severity).ToList();

        // Best-effort log line so the pass is recorded in the wiki's history (BR-021).
        try
        {
            var criticalCount = sorted.Count(f => f.Severity == LintSeverity.Critical);
            var body = $"- Findings: {sorted.Count} ({criticalCount} critical)";
            await journal.AppendLogAsync(wikiName,
                new LogEntry(DateOnly.FromDateTime(DateTime.UtcNow), "lint", $"{sorted.Count} finding(s)", body), ct);
        }
        catch { /* logging is best-effort */ }

        return new LintReport(wikiName, sorted);
    }

    public async Task<PageOutcome> ApplyFixAsync(string wikiName, LintFinding finding, CancellationToken ct = default)
    {
        if (finding.Fix is not { } fix)
            return new PageOutcome("(none)", finding.Summary, PageChange.Failed, "finding has no applyable fix");

        var page = new WikiPage { Title = fix.Title, Type = fix.Type, Content = fix.Body };

        // Write boundary (mirrors SaveAnswerAsync): a failure is a Failed outcome, never thrown.
        try { await wiki.WritePageAsync(wikiName, fix.RelativePath, page, ct); }
        catch (Exception ex) { return new PageOutcome(fix.RelativePath, page.Title, PageChange.Failed, ex.Message); }

        string? detail = null;

        // Journal: regenerate index.md (the stub appears) + append a greppable lint log line.
        // Best-effort — the page is already saved, so a journal failure is recorded, not thrown.
        try
        {
            await journal.RebuildIndexAsync(wikiName, ct);
            await journal.AppendLogAsync(wikiName, new LogEntry(
                DateOnly.FromDateTime(DateTime.UtcNow), "lint", $"stub created: {fix.RelativePath}", null), ct);
        }
        catch (Exception ex) { detail = $"journal: {ex.Message}"; }

        // Best-effort embed so the new stub is itself searchable. A failure (e.g. Oracle down) is
        // recorded on the outcome detail, never thrown (NFR-06).
        try
        {
            var vector = await embeddings.EmbedAsync(EmbeddingText.For(page, _strategy), ct);
            await vectors.UpsertAsync(wikiName, fix.RelativePath, page, vector, ct);
        }
        catch (Exception ex)
        {
            var embed = $"embed: {ex.Message}";
            detail = detail is null ? embed : $"{detail}; {embed}";
        }

        return new PageOutcome(fix.RelativePath, page.Title, PageChange.StubCreated, detail);
    }

    /// <summary>Resolve a broken link's raw target to the wiki-relative path it *intended*, so a stub
    /// can be created there. Markdown links are relative to the source page's directory (mirrors
    /// <c>FileSystemWikiRepository.ResolveTarget</c>); wikilink targets are bare titles with no
    /// unambiguous directory, so they stay report-only (null).</summary>
    private static string? ResolveIntendedPath(LinkStyle style, string fromPath, string target)
    {
        if (style != LinkStyle.MarkdownLink) return null;
        var baseDir = Path.GetDirectoryName(fromPath)?.Replace(Path.DirectorySeparatorChar, '/') ?? string.Empty;
        var combined = string.IsNullOrEmpty(baseDir) ? target.Trim() : $"{baseDir}/{target.Trim()}";
        return NormalizePath(combined);
    }

    private static string NormalizePath(string path)
    {
        var parts = new Stack<string>();
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == ".." && parts.Count > 0) parts.Pop();
            else parts.Push(seg);
        }
        return string.Join('/', parts.Reverse());
    }

    /// <summary>Map a normalized intended path to a stub fix when it names an unambiguous typed page
    /// (e.g. <c>concepts/anvil.md</c> under a known typed directory). Null → report-only.</summary>
    private static SuggestedFix? TryStubFix(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return null;
        var slashIdx = candidate.IndexOf('/');
        if (slashIdx > 0 && candidate.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var dir = candidate[..slashIdx];
            // Only a single typed directory (no nested dirs) yields an unambiguous stub location.
            if (TypedDirs.Contains(dir) && candidate.IndexOf('/', slashIdx + 1) < 0)
            {
                var title = Path.GetFileNameWithoutExtension(candidate).Replace('-', ' ');
                return new SuggestedFix(candidate, title, TypeForDir(dir), $"Stub page for **{title}** (created by lint).");
            }
        }
        return null;   // bare wikilink target [[Foo]] has no unambiguous directory — report-only
    }

    private static PageType TypeForDir(string dir) => dir switch
    {
        "entities" => PageType.Entity,
        "concepts" => PageType.Concept,
        "topics"   => PageType.Overview,
        _          => PageType.Summary,
    };

    private static string ExtractJson(string s)  // identical tolerance to QueryService/IngestionService
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
