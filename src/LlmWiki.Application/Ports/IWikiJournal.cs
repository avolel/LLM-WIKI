using LlmWiki.Domain;

namespace LlmWiki.Application.Ports;

/// <summary>Maintains a wiki's agent-owned index.md (regenerated) and log.md (append-only) — BR-020…024.</summary>
public interface IWikiJournal
{
    /// <summary>Rewrites index.md from the current pages on disk (deterministic; deleted pages vanish, BR-024).</summary>
    Task RebuildIndexAsync(string wikiName, CancellationToken cancellationToken = default);

    /// <summary>Appends one entry to log.md, seeding the file if absent (append-only, chronological).</summary>
    Task AppendLogAsync(string wikiName, LogEntry entry, CancellationToken cancellationToken = default);
}