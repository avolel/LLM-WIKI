using LlmWiki.Application.Ports;
using LlmWiki.Domain;

namespace LlmWiki.Infrastructure.FileStore;

/// <summary>
/// Wiki-aware orchestration over <see cref="IWikiFileStore"/>: scaffolds typed directories
/// + SCHEMA.md (BR-001/2), reads/writes pages with frontmatter (BR-003), lists wikis/pages,
/// and resolves cross-references (BR-004). Each wiki is a subdirectory of the wiki root.
/// </summary>
public sealed class FileSystemWikiRepository(IWikiFileStore files) : IWikiRepository
{
    private const string SchemaFile = "SCHEMA.md";
    private const string IndexFile = "index.md";
    private const string LogFile = "log.md";
    private const string Keep = ".gitkeep";

    public async Task CreateWikiAsync(WikiSchema schema, CancellationToken cancellationToken = default)
    {
        if (await WikiExistsAsync(schema.WikiName, cancellationToken))
        {
            throw new InvalidOperationException($"Wiki '{schema.WikiName}' already exists.");
        }

        foreach (var dir in WikiSchema.Directories)
        {
            await files.WriteAsync($"{schema.WikiName}/{dir}/{Keep}", string.Empty, cancellationToken);
        }

        await files.WriteAsync($"{schema.WikiName}/{SchemaFile}",
            SchemaSerializer.Render(schema), cancellationToken);
    }

    public Task<bool> WikiExistsAsync(string wikiName, CancellationToken cancellationToken = default) =>
        files.ExistsAsync($"{wikiName}/{SchemaFile}", cancellationToken);

    public async Task<IReadOnlyList<WikiInfo>> ListWikisAsync(CancellationToken cancellationToken = default)
    {
        var pageCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        await foreach (var path in files.ListAsync(string.Empty, cancellationToken))
        {
            var name = path.Split('/')[0];
            pageCounts.TryAdd(name, 0);
            if (IsPage(path))
            {
                pageCounts[name]++;
            }
        }

        var infos = new List<WikiInfo>();
        foreach (var (name, count) in pageCounts)
        {
            if (!await WikiExistsAsync(name, cancellationToken))
            {
                continue; // a stray directory, not a wiki
            }
            var schema = await ReadSchemaAsync(name, cancellationToken);
            infos.Add(new WikiInfo(name, schema.LinkStyle, count));
        }
        return infos.OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<WikiSchema> ReadSchemaAsync(string wikiName, CancellationToken cancellationToken = default) =>
        SchemaSerializer.Parse(await files.ReadAsync($"{wikiName}/{SchemaFile}", cancellationToken));

    public Task WritePageAsync(string wikiName, string relativePath, WikiPage page, CancellationToken cancellationToken = default) =>
        files.WriteAsync($"{wikiName}/{relativePath}", FrontmatterSerializer.Serialize(page), cancellationToken);

    public async Task<WikiPage> ReadPageAsync(string wikiName, string relativePath, CancellationToken cancellationToken = default) =>
        FrontmatterSerializer.Deserialize(await files.ReadAsync($"{wikiName}/{relativePath}", cancellationToken));

    public async Task<IReadOnlyList<string>> ListPagesAsync(string wikiName, CancellationToken cancellationToken = default)
    {
        var pages = new List<string>();
        await foreach (var path in files.ListAsync(wikiName, cancellationToken))
        {
            if (IsPage(path))
            {
                pages.Add(path[(wikiName.Length + 1)..]); // strip "{wikiName}/"
            }
        }
        return pages.OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    public async Task<LinkResolutionReport> ResolveLinksAsync(string wikiName, string relativePath, CancellationToken cancellationToken = default)
    {
        var schema = await ReadSchemaAsync(wikiName, cancellationToken);
        var page = await ReadPageAsync(wikiName, relativePath, cancellationToken);
        var pages = await ListPagesAsync(wikiName, cancellationToken);

        var resolved = new List<ResolvedLink>();
        foreach (var reference in CrossReferenceParser.Parse(page.Content, schema.LinkStyle))
        {
            var match = ResolveTarget(reference.Target, schema.LinkStyle, relativePath, pages);
            resolved.Add(new ResolvedLink(reference, match is not null, match));
        }
        return new LinkResolutionReport(relativePath, resolved);
    }

    private static string? ResolveTarget(string target, LinkStyle style, string fromPath, IReadOnlyList<string> pages)
    {
        if (style == LinkStyle.MarkdownLink)
        {
            // Resolve relative to the source page's directory.
            var baseDir = Path.GetDirectoryName(fromPath)?.Replace(Path.DirectorySeparatorChar, '/') ?? string.Empty;
            var combined = string.IsNullOrEmpty(baseDir) ? target : $"{baseDir}/{target}";
            var normalized = NormalizePath(combined);
            return pages.Contains(normalized, StringComparer.Ordinal) ? normalized : null;
        }

        // Wikilink: match the slugified title against any page filename (without extension).
        var slug = Slug.From(target);
        return pages.FirstOrDefault(p =>
            string.Equals(Path.GetFileNameWithoutExtension(p), slug, StringComparison.OrdinalIgnoreCase));
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

    private static bool IsPage(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith($"/{SchemaFile}", StringComparison.Ordinal) &&
        !path.EndsWith($"/{IndexFile}", StringComparison.Ordinal) &&
        !path.EndsWith($"/{LogFile}", StringComparison.Ordinal) &&
        !path.Contains("/raw/", StringComparison.Ordinal);
}