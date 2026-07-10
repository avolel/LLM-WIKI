using LlmWiki.Domain;

namespace LlmWiki.Domain.Tests;

public class LogFormatterTests
{
    [Fact]
    public void Format_HeaderIsGreppable()
    {
        var md = LogFormatter.Format(new LogEntry(new DateOnly(2026, 7, 7), "ingest", "raw/anvils.md"));

        Assert.Matches(@"^## \[\d{4}-\d{2}-\d{2}\] ingest \| ", md);
        Assert.Contains("raw/anvils.md", md);
    }

    [Fact]
    public void Format_RendersBodyWhenPresent()
    {
        var md = LogFormatter.Format(new LogEntry(new DateOnly(2026, 7, 7), "ingest", "S", "- 1 created"));

        Assert.Contains("- 1 created", md);
    }
}