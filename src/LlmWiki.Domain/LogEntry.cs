namespace LlmWiki.Domain;

/// <summary>One append-only log record (BR-021). Header is greppable: "## [YYYY-MM-DD] type | Description".</summary>
public sealed record LogEntry(DateOnly Date, string Type, string Description, string? Body = null);

/// <summary>Pure formatter for <see cref="LogEntry"/> blocks appended to a wiki's <c>log.md</c>.</summary>
public static class LogFormatter
{
    public static string Format(LogEntry e)
    {
        var header = $"## [{e.Date:yyyy-MM-dd}] {e.Type} | {e.Description}";
        return string.IsNullOrWhiteSpace(e.Body)
            ? header + "\n"
            : header + "\n\n" + e.Body.TrimEnd() + "\n";
    }
}