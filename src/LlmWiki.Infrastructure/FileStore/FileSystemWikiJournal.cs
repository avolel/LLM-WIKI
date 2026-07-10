using LlmWiki.Application.Ports;
using LlmWiki.Domain;

namespace LlmWiki.Infrastructure.FileStore;

/// <summary>
/// File-backed <see cref="IWikiJournal"/>: rewrites the agent-owned <c>index.md</c> from disk each call
/// (deterministic, so BR-024 stale-entry removal falls out for free) and appends to <c>log.md</c>
/// (BR-020…BR-024). Both live at the wiki root and are excluded from the page catalogue itself.
/// </summary>
public sealed class FileSystemWikiJournal(IWikiRepository wiki, IWikiFileStore files) : IWikiJournal
{
    private const string IndexFile = "index.md";
    private const string LogFile = "log.md";
    private const int SummaryCap = 200;

    public async Task RebuildIndexAsync(string wikiName, CancellationToken cancellationToken = default)
    {
        var schema = await wiki.ReadSchemaAsync(wikiName, cancellationToken);
        var paths = await wiki.ListPagesAsync(wikiName, cancellationToken);

        var entries = new List<IndexEntry>();
        foreach (var path in paths)
        {
            var page = await wiki.ReadPageAsync(wikiName, path, cancellationToken);
            entries.Add(new IndexEntry(
                path, page.Title, page.Type, Summarize(page.Content), page.CreatedAt, page.Sources.Count));
        }

        await files.WriteAsync($"{wikiName}/{IndexFile}",
            IndexRenderer.Render(entries, schema.LinkStyle), cancellationToken);
    }

    public async Task AppendLogAsync(string wikiName, LogEntry entry, CancellationToken cancellationToken = default)
    {
        var path = $"{wikiName}/{LogFile}";
        var existing = await files.ExistsAsync(path, cancellationToken)
            ? await files.ReadAsync(path, cancellationToken)
            : "# Log\n";

        var body = existing.TrimEnd() + "\n\n" + LogFormatter.Format(entry);
        await files.WriteAsync(path, body, cancellationToken);
    }

    /// <summary>First non-empty, non-heading/quote line of the body, trimmed and capped.</summary>
    private static string Summarize(string content)
    {
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('>')) continue;
            return line.Length <= SummaryCap ? line : line[..SummaryCap].TrimEnd() + "…";
        }
        return string.Empty;
    }
}