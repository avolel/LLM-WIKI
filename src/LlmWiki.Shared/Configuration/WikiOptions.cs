namespace LlmWiki.Shared.Configuration;

/// <summary>Wiki file-store settings.</summary>
public sealed class WikiOptions
{
    public const string SectionName = "Wiki";

    /// <summary>Root directory holding all wikis, one subdirectory each (env: WIKI_ROOT).</summary>
    public string RootPath { get; set; } = "wiki";
}