# Phase 1 — Wiki Scaffolding

## Context

Phase 0 left a buildable clean-architecture skeleton with `IWikiFileStore` as a loud
`NotImplementedException` stub and a placeholder `PageType` enum. Phase 1 (BR-001…BR-005)
turns that skeleton into a working **file-store layer**: it creates typed wiki directories
+ a machine-readable `SCHEMA.md`, reads/writes pages with YAML frontmatter, parses and
resolves cross-references, and exposes a dev CLI to create/list/inspect wikis and author
pages. Everything is on-disk only — Oracle, embeddings, and project metadata are later phases.

**Locked decisions (from review):**
1. **Replace** the placeholder `PageType` (`Article/Reference/Note/Generated`) with the BR-003
   taxonomy `Entity/Concept/Summary/Overview`.
2. **Convention + toggles** schema model: typed dirs (`summaries/entities/topics/raw`) are
   fixed; **link style** and the **frontmatter field set** are configurable per wiki and
   recorded in `SCHEMA.md` (satisfies BR-005).
3. **Full CLI**: `wiki create | list | inspect` plus `wiki page add | show`.

**Standing working agreement (NEW — applied this plan onward):** every plan / change set must
enumerate **all new and updated files with their full code** for review. This is recorded in
CLAUDE.md (step 0) and saved as a memory.

---

## Step 0 — Record the working agreement (do first)

- **Update** `CLAUDE.md`: add a `## Working agreement` section:
  > When proposing a plan or a change set, always list **every** new/updated file and include
  > the full code to be added/changed, so it can be reviewed before implementation.
- **Save memory** `~/.claude/projects/-home-avolel-Code-LLM-WIKI/memory/` as a `feedback` entry
  (`always-show-full-file-changes`) + index line in `MEMORY.md`.

---

## Architecture

- **Domain** stays pure (no deps): enriched `WikiPage`, new `PageType`, `LinkStyle`,
  `WikiSchema`, and a pure `CrossReferenceParser` + link records.
- **Application** keeps `IWikiFileStore` (raw rooted I/O) and adds **`IWikiRepository`** — the
  wiki-aware port (scaffold / page round-trip / list / resolve).
- **Infrastructure** fills the `FileSystemWikiFileStore` stub (real disk I/O) and adds
  `FileSystemWikiRepository` + internal `FrontmatterSerializer` / `SchemaSerializer`
  (YamlDotNet). This is where the only new dependency lives.
- **Shared** adds `WikiOptions` (`WIKI_ROOT`) and wires it through the existing config map.
- **Cli** gains the `wiki` command group.

Each wiki is a subdirectory of `WIKI_ROOT`; a directory is "a wiki" iff it contains `SCHEMA.md`.

---

## New / updated files

### 1. `Directory.Packages.props` — add YamlDotNet (UPDATE)
Add one pinned version (use the latest stable at implementation time; shown 16.3.0):
```xml
<PackageVersion Include="YamlDotNet" Version="16.3.0" />
```

### 2. `src/LlmWiki.Infrastructure/LlmWiki.Infrastructure.csproj` — reference it (UPDATE)
Add inside the existing `<ItemGroup>` of `PackageReference`s (no `Version=`, per CPM):
```xml
<PackageReference Include="YamlDotNet" />
```

### 3. `src/LlmWiki.Domain/PageType.cs` (REPLACE)
```csharp
namespace LlmWiki.Domain;

/// <summary>Classifies a <see cref="WikiPage"/> per the BR-003 frontmatter taxonomy.</summary>
public enum PageType
{
    Summary,
    Entity,
    Concept,
    Overview
}
```

### 4. `src/LlmWiki.Domain/WikiPage.cs` (REPLACE)
```csharp
namespace LlmWiki.Domain;

/// <summary>
/// A markdown wiki page: YAML frontmatter (BR-003) plus a markdown body. Identified on disk
/// by its relative path within a wiki; persisted to Oracle in later phases.
/// </summary>
public sealed class WikiPage
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Title { get; init; }

    public PageType Type { get; init; } = PageType.Summary;

    public string Content { get; init; } = string.Empty;

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyList<string> Sources { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
```

### 5. `src/LlmWiki.Domain/LinkStyle.cs` (NEW)
```csharp
namespace LlmWiki.Domain;

/// <summary>Cross-reference style a wiki uses, recorded in its SCHEMA.md (BR-004).</summary>
public enum LinkStyle
{
    /// <summary>Obsidian-style [[Target]] links.</summary>
    Wikilink,

    /// <summary>Standard markdown [text](path.md) links.</summary>
    MarkdownLink
}
```

### 6. `src/LlmWiki.Domain/WikiSchema.cs` (NEW)
```csharp
namespace LlmWiki.Domain;

/// <summary>
/// A wiki's conventions, written to and read back from SCHEMA.md (BR-002, BR-005).
/// "Convention + toggles": typed directories are fixed, while link style and the frontmatter
/// field set are configurable per wiki so wikis with different conventions coexist.
/// </summary>
public sealed class WikiSchema
{
    public required string WikiName { get; init; }

    public LinkStyle LinkStyle { get; init; } = LinkStyle.Wikilink;

    public IReadOnlyList<string> FrontmatterFields { get; init; } = DefaultFrontmatterFields;

    /// <summary>Typed content directories created for every wiki (BR-001).</summary>
    public static readonly IReadOnlyList<string> Directories = ["summaries", "entities", "topics", "raw"];

    /// <summary>Default frontmatter keys per BR-003.</summary>
    public static readonly IReadOnlyList<string> DefaultFrontmatterFields =
        ["title", "type", "created", "updated", "tags", "sources"];
}
```

### 7. `src/LlmWiki.Domain/CrossReference.cs` (NEW)
```csharp
using System.Text.RegularExpressions;

namespace LlmWiki.Domain;

/// <summary>A cross-reference found in a page body.</summary>
public sealed record CrossReference(string Target, string Raw);

/// <summary>Outcome of resolving one cross-reference against the wiki's pages.</summary>
public sealed record ResolvedLink(CrossReference Reference, bool Exists, string? ResolvedPath);

/// <summary>Aggregate link-resolution result for a page (BR-004).</summary>
public sealed record LinkResolutionReport(string PagePath, IReadOnlyList<ResolvedLink> Links)
{
    public IReadOnlyList<ResolvedLink> Broken => Links.Where(l => !l.Exists).ToList();
    public bool AllResolved => Links.All(l => l.Exists);
}

/// <summary>Extracts cross-references from markdown according to a wiki's link style.</summary>
public static partial class CrossReferenceParser
{
    // [[Target]] or [[Target|alias]]
    [GeneratedRegex(@"\[\[([^\]|]+)(?:\|[^\]]+)?\]\]")]
    private static partial Regex WikilinkRegex();

    // [text](relative/path.md)
    [GeneratedRegex(@"\[[^\]]+\]\(([^)]+)\)")]
    private static partial Regex MarkdownLinkRegex();

    public static IReadOnlyList<CrossReference> Parse(string body, LinkStyle style)
    {
        var regex = style == LinkStyle.Wikilink ? WikilinkRegex() : MarkdownLinkRegex();
        var results = new List<CrossReference>();
        foreach (Match m in regex.Matches(body))
        {
            results.Add(new CrossReference(m.Groups[1].Value.Trim(), m.Value));
        }
        return results;
    }
}
```

### 8. `src/LlmWiki.Application/Ports/IWikiRepository.cs` (NEW)
```csharp
using LlmWiki.Domain;

namespace LlmWiki.Application.Ports;

/// <summary>Summary of a wiki on disk, for list/inspect.</summary>
public sealed record WikiInfo(string Name, LinkStyle LinkStyle, int PageCount);

/// <summary>
/// High-level, wiki-aware operations over the file store: scaffold wikis (BR-001/2),
/// read/write pages with frontmatter (BR-003), list wikis/pages, and resolve cross-references
/// (BR-004). Backed in Infrastructure by <see cref="IWikiFileStore"/> + YAML serialization.
/// </summary>
public interface IWikiRepository
{
    Task CreateWikiAsync(WikiSchema schema, CancellationToken cancellationToken = default);

    Task<bool> WikiExistsAsync(string wikiName, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WikiInfo>> ListWikisAsync(CancellationToken cancellationToken = default);

    Task<WikiSchema> ReadSchemaAsync(string wikiName, CancellationToken cancellationToken = default);

    Task WritePageAsync(string wikiName, string relativePath, WikiPage page, CancellationToken cancellationToken = default);

    Task<WikiPage> ReadPageAsync(string wikiName, string relativePath, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListPagesAsync(string wikiName, CancellationToken cancellationToken = default);

    Task<LinkResolutionReport> ResolveLinksAsync(string wikiName, string relativePath, CancellationToken cancellationToken = default);
}
```

### 9. `src/LlmWiki.Shared/Configuration/WikiOptions.cs` (NEW)
```csharp
namespace LlmWiki.Shared.Configuration;

/// <summary>Wiki file-store settings.</summary>
public sealed class WikiOptions
{
    public const string SectionName = "Wiki";

    /// <summary>Root directory holding all wikis, one subdirectory each (env: WIKI_ROOT).</summary>
    public string RootPath { get; set; } = "wiki";
}
```

### 10. `src/LlmWiki.Shared/Configuration/LlmWikiConfiguration.cs` (UPDATE)
- Add to the `EnvToConfigKey` dictionary:
```csharp
["WIKI_ROOT"] = $"{WikiOptions.SectionName}:{nameof(WikiOptions.RootPath)}",
```
- Add to `AddLlmWikiOptions`:
```csharp
services.Configure<WikiOptions>(configuration.GetSection(WikiOptions.SectionName));
```

### 11. `env/.env.example` (UPDATE)
Append a documented placeholder:
```bash
# ---- Wiki file store -------------------------------------------------------
WIKI_ROOT=wiki
```

### 12. `src/LlmWiki.Infrastructure/FileStore/FileSystemWikiFileStore.cs` (REPLACE — fill the stub)
```csharp
using System.Runtime.CompilerServices;
using LlmWiki.Application.Ports;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Infrastructure.FileStore;

/// <summary>
/// Local-disk implementation of <see cref="IWikiFileStore"/> (BR-001). All paths are relative
/// to the configured wiki root; writes create parent directories so the typed wiki layout
/// materialises on first write. Paths that escape the root are rejected.
/// </summary>
public sealed class FileSystemWikiFileStore : IWikiFileStore
{
    private readonly string _root;

    public FileSystemWikiFileStore(IOptions<WikiOptions> options)
    {
        _root = Path.GetFullPath(options.Value.RootPath);
    }

    public async Task<string> ReadAsync(string relativePath, CancellationToken cancellationToken = default) =>
        await File.ReadAllTextAsync(Resolve(relativePath), cancellationToken);

    public async Task WriteAsync(string relativePath, string content, CancellationToken cancellationToken = default)
    {
        var full = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, content, cancellationToken);
    }

    public Task<bool> ExistsAsync(string relativePath, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(Resolve(relativePath)));

    public async IAsyncEnumerable<string> ListAsync(
        string prefix, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var dir = Resolve(prefix);
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            await Task.Yield();
        }
    }

    private string Resolve(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relativePath));
        if (full != _root &&
            !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Path '{relativePath}' escapes the wiki root.");
        }
        return full;
    }
}
```

### 13. `src/LlmWiki.Infrastructure/FileStore/FrontmatterSerializer.cs` (NEW)
```csharp
using LlmWiki.Domain;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LlmWiki.Infrastructure.FileStore;

/// <summary>Reads/writes a page's YAML frontmatter + markdown body (BR-003).</summary>
internal static class FrontmatterSerializer
{
    private const string Fence = "---";

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(LowerCaseNamingConvention.Instance)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(LowerCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static string Serialize(WikiPage page)
    {
        var dto = new FrontmatterDto
        {
            Title = page.Title,
            Type = page.Type.ToString().ToLowerInvariant(),
            Created = page.CreatedAt.ToString("yyyy-MM-dd"),
            Updated = page.UpdatedAt.ToString("yyyy-MM-dd"),
            Tags = page.Tags.ToList(),
            Sources = page.Sources.ToList(),
        };

        var yaml = Serializer.Serialize(dto).TrimEnd();
        return $"{Fence}\n{yaml}\n{Fence}\n\n{page.Content.TrimStart()}";
    }

    public static WikiPage Deserialize(string fileText)
    {
        if (!fileText.StartsWith(Fence, StringComparison.Ordinal))
        {
            throw new FormatException("Page is missing YAML frontmatter.");
        }

        var end = fileText.IndexOf($"\n{Fence}", Fence.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new FormatException("Page frontmatter is not closed with '---'.");
        }

        var yaml = fileText[Fence.Length..end];
        var body = fileText[(end + Fence.Length + 1)..].TrimStart('\n');
        var dto = Deserializer.Deserialize<FrontmatterDto>(yaml) ?? new FrontmatterDto();

        return new WikiPage
        {
            Title = dto.Title ?? string.Empty,
            Type = Enum.TryParse<PageType>(dto.Type, ignoreCase: true, out var t) ? t : PageType.Summary,
            Content = body,
            Tags = dto.Tags ?? [],
            Sources = dto.Sources ?? [],
            CreatedAt = ParseDate(dto.Created),
            UpdatedAt = ParseDate(dto.Updated),
        };
    }

    private static DateTimeOffset ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, out var d) ? d : DateTimeOffset.UtcNow;

    private sealed class FrontmatterDto
    {
        public string? Title { get; set; }
        public string? Type { get; set; }
        public string? Created { get; set; }
        public string? Updated { get; set; }
        public List<string>? Tags { get; set; }
        public List<string>? Sources { get; set; }
    }
}
```

### 14. `src/LlmWiki.Infrastructure/FileStore/SchemaSerializer.cs` (NEW)
SCHEMA.md = a machine-readable YAML header (round-trips link style + frontmatter fields per
BR-005) followed by a human-readable documentation body (BR-002).
```csharp
using System.Text;
using LlmWiki.Domain;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LlmWiki.Infrastructure.FileStore;

/// <summary>Renders and parses SCHEMA.md — a documented, machine-readable wiki schema (BR-002).</summary>
internal static class SchemaSerializer
{
    private const string Fence = "---";

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(LowerCaseNamingConvention.Instance).Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(LowerCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties().Build();

    public static string Render(WikiSchema schema)
    {
        var dto = new SchemaDto
        {
            WikiName = schema.WikiName,
            LinkStyle = schema.LinkStyle.ToString(),
            FrontmatterFields = schema.FrontmatterFields.ToList(),
            Directories = WikiSchema.Directories.ToList(),
        };
        var yaml = Serializer.Serialize(dto).TrimEnd();

        var sb = new StringBuilder();
        sb.Append(Fence).Append('\n').Append(yaml).Append('\n').Append(Fence).Append("\n\n");
        sb.Append($"# {schema.WikiName} — Wiki Schema\n\n");
        sb.Append("## Directories\n\n");
        sb.Append("- `summaries/` — one page per ingested source.\n");
        sb.Append("- `entities/` — people, companies, concepts.\n");
        sb.Append("- `topics/` — topic/overview pages connecting knowledge.\n");
        sb.Append("- `raw/` — immutable source files (never modified).\n\n");
        sb.Append("## Frontmatter\n\nEvery page carries: ")
          .Append(string.Join(", ", schema.FrontmatterFields)).Append(".\n\n");
        sb.Append("## Cross-references\n\n");
        sb.Append(schema.LinkStyle == LinkStyle.Wikilink
            ? "Use `[[Target Title]]` wikilinks.\n"
            : "Use `[text](relative/path.md)` markdown links.\n");
        return sb.ToString();
    }

    public static WikiSchema Parse(string schemaText)
    {
        var yaml = ExtractFrontmatter(schemaText);
        var dto = Deserializer.Deserialize<SchemaDto>(yaml) ?? new SchemaDto();
        return new WikiSchema
        {
            WikiName = dto.WikiName ?? string.Empty,
            LinkStyle = Enum.TryParse<LinkStyle>(dto.LinkStyle, ignoreCase: true, out var s)
                ? s : LinkStyle.Wikilink,
            FrontmatterFields = dto.FrontmatterFields is { Count: > 0 } f
                ? f : WikiSchema.DefaultFrontmatterFields,
        };
    }

    private static string ExtractFrontmatter(string text)
    {
        if (!text.StartsWith(Fence, StringComparison.Ordinal))
        {
            throw new FormatException("SCHEMA.md is missing its YAML header.");
        }
        var end = text.IndexOf($"\n{Fence}", Fence.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new FormatException("SCHEMA.md header is not closed.");
        }
        return text[Fence.Length..end];
    }

    private sealed class SchemaDto
    {
        public string? WikiName { get; set; }
        public string? LinkStyle { get; set; }
        public List<string>? FrontmatterFields { get; set; }
        public List<string>? Directories { get; set; }
    }
}
```

### 15. `src/LlmWiki.Infrastructure/FileStore/FileSystemWikiRepository.cs` (NEW)
```csharp
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
        var slug = Slugify(target);
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

    public static string Slugify(string title) =>
        string.Join('-', title.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static bool IsPage(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
        !path.EndsWith($"/{SchemaFile}", StringComparison.Ordinal) &&
        !path.Contains("/raw/", StringComparison.Ordinal);
}
```

### 16. `src/LlmWiki.Infrastructure/DependencyInjection.cs` (UPDATE)
Replace the `IWikiFileStore` stub registration and add the repository. Inside
`AddLlmWikiInfrastructure`, where ports are registered:
```csharp
// Phase 1: real file store + wiki-aware repository.
services.AddSingleton<IWikiFileStore, FileSystemWikiFileStore>();
services.AddSingleton<IWikiRepository, FileSystemWikiRepository>();
```
The type name `FileSystemWikiFileStore` is unchanged — we filled the stub, not replaced it.
Ensure `WikiOptions` is bound — it is, via the `AddLlmWikiOptions` update (file #10), which this
method already calls.

### 17. `src/LlmWiki.Cli/Program.cs` (REPLACE)
```csharp
using System.CommandLine;
using LlmWiki.Application.Diagnostics;
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using LlmWiki.Infrastructure;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.DependencyInjection;

var root = new RootCommand("LLM Wiki CLI — local operations and diagnostics.");

// ---- doctor (Phase 0) ------------------------------------------------------
var doctor = new Command("doctor", "Run the Phase 0 connectivity checks and report pass/fail.");
doctor.SetAction((_, ct) => RunDoctorAsync(ct));
root.Subcommands.Add(doctor);

// ---- wiki (Phase 1) --------------------------------------------------------
root.Subcommands.Add(BuildWikiCommand());

return await root.Parse(args).InvokeAsync();

static ServiceProvider BuildProvider()
{
    var services = new ServiceCollection();
    services.AddLlmWikiInfrastructure(LlmWikiConfiguration.Build());
    return services.BuildServiceProvider();
}

static async Task<int> RunDoctorAsync(CancellationToken cancellationToken)
{
    await using var provider = BuildProvider();
    var diagnostics = provider.GetRequiredService<IDiagnosticsService>();
    var report = await diagnostics.RunAsync(cancellationToken);

    Console.WriteLine("LLM Wiki — Phase 0 diagnostics");
    Console.WriteLine("------------------------------");
    foreach (var check in report.Checks)
    {
        Console.WriteLine($"[{(check.Passed ? "PASS" : "FAIL")}] {check.Name,-10} {check.Detail}");
    }
    Console.WriteLine();
    Console.WriteLine(report.AllPassed ? "All checks passed." : "One or more checks FAILED.");
    return report.AllPassed ? 0 : 1;
}

static Command BuildWikiCommand()
{
    var wiki = new Command("wiki", "Create, list, and inspect wikis.");

    // create
    var createName = new Argument<string>("name") { Description = "Wiki name (directory)." };
    var linkStyle = new Option<LinkStyle>("--link-style")
    {
        Description = "Cross-reference style.",
        DefaultValueFactory = _ => LinkStyle.Wikilink,
    };
    var create = new Command("create", "Scaffold a new wiki (typed dirs + SCHEMA.md).");
    create.Arguments.Add(createName);
    create.Options.Add(linkStyle);
    create.SetAction(async (pr, ct) =>
    {
        await using var provider = BuildProvider();
        var repo = provider.GetRequiredService<IWikiRepository>();
        var schema = new WikiSchema { WikiName = pr.GetValue(createName)!, LinkStyle = pr.GetValue(linkStyle) };
        await repo.CreateWikiAsync(schema, ct);
        Console.WriteLine($"Created wiki '{schema.WikiName}' ({schema.LinkStyle}).");
        return 0;
    });
    wiki.Subcommands.Add(create);

    // list
    var list = new Command("list", "List all wikis.");
    list.SetAction(async (_, ct) =>
    {
        await using var provider = BuildProvider();
        var repo = provider.GetRequiredService<IWikiRepository>();
        var wikis = await repo.ListWikisAsync(ct);
        if (wikis.Count == 0) { Console.WriteLine("No wikis found."); return 0; }
        foreach (var w in wikis)
        {
            Console.WriteLine($"{w.Name,-20} {w.LinkStyle,-12} {w.PageCount} page(s)");
        }
        return 0;
    });
    wiki.Subcommands.Add(list);

    // inspect
    var inspectName = new Argument<string>("name") { Description = "Wiki name." };
    var inspect = new Command("inspect", "Show a wiki's schema and pages.");
    inspect.Arguments.Add(inspectName);
    inspect.SetAction(async (pr, ct) =>
    {
        await using var provider = BuildProvider();
        var repo = provider.GetRequiredService<IWikiRepository>();
        var name = pr.GetValue(inspectName)!;
        if (!await repo.WikiExistsAsync(name, ct))
        {
            Console.Error.WriteLine($"Wiki '{name}' not found.");
            return 1;
        }
        var schema = await repo.ReadSchemaAsync(name, ct);
        var pages = await repo.ListPagesAsync(name, ct);
        Console.WriteLine($"Wiki:        {schema.WikiName}");
        Console.WriteLine($"Link style:  {schema.LinkStyle}");
        Console.WriteLine($"Frontmatter: {string.Join(", ", schema.FrontmatterFields)}");
        Console.WriteLine($"Pages ({pages.Count}):");
        foreach (var p in pages) Console.WriteLine($"  {p}");
        return 0;
    });
    wiki.Subcommands.Add(inspect);

    wiki.Subcommands.Add(BuildPageCommand());
    return wiki;
}

static Command BuildPageCommand()
{
    var page = new Command("page", "Add or show pages within a wiki.");

    // add
    var addWiki = new Argument<string>("wiki") { Description = "Wiki name." };
    var addPath = new Argument<string>("path") { Description = "Page path, e.g. entities/acme-corp.md." };
    var title = new Option<string>("--title") { Description = "Page title.", Required = true };
    var type = new Option<PageType>("--type") { DefaultValueFactory = _ => PageType.Summary };
    var tags = new Option<string[]>("--tag") { Description = "Repeatable.", AllowMultipleArgumentsPerToken = true };
    var sources = new Option<string[]>("--source") { Description = "Repeatable.", AllowMultipleArgumentsPerToken = true };
    var body = new Option<string>("--body") { Description = "Markdown body (or use --body-file)." };
    var bodyFile = new Option<FileInfo>("--body-file") { Description = "Read body from a file." };
    var add = new Command("add", "Write a page with frontmatter.");
    add.Arguments.Add(addWiki); add.Arguments.Add(addPath);
    add.Options.Add(title); add.Options.Add(type); add.Options.Add(tags);
    add.Options.Add(sources); add.Options.Add(body); add.Options.Add(bodyFile);
    add.SetAction(async (pr, ct) =>
    {
        await using var provider = BuildProvider();
        var repo = provider.GetRequiredService<IWikiRepository>();
        var file = pr.GetValue(bodyFile);
        var content = file is not null
            ? await File.ReadAllTextAsync(file.FullName, ct)
            : pr.GetValue(body) ?? string.Empty;
        var p = new WikiPage
        {
            Title = pr.GetValue(title)!,
            Type = pr.GetValue(type),
            Content = content,
            Tags = pr.GetValue(tags) ?? [],
            Sources = pr.GetValue(sources) ?? [],
        };
        await repo.WritePageAsync(pr.GetValue(addWiki)!, pr.GetValue(addPath)!, p, ct);
        Console.WriteLine($"Wrote {pr.GetValue(addPath)}.");
        return 0;
    });
    page.Subcommands.Add(add);

    // show
    var showWiki = new Argument<string>("wiki") { Description = "Wiki name." };
    var showPath = new Argument<string>("path") { Description = "Page path." };
    var show = new Command("show", "Print a page and resolve its links.");
    show.Arguments.Add(showWiki); show.Arguments.Add(showPath);
    show.SetAction(async (pr, ct) =>
    {
        await using var provider = BuildProvider();
        var repo = provider.GetRequiredService<IWikiRepository>();
        var name = pr.GetValue(showWiki)!; var path = pr.GetValue(showPath)!;
        var p = await repo.ReadPageAsync(name, path, ct);
        Console.WriteLine($"# {p.Title} [{p.Type}]  tags=[{string.Join(",", p.Tags)}]");
        Console.WriteLine(p.Content);
        var report = await repo.ResolveLinksAsync(name, path, ct);
        Console.WriteLine($"\nLinks ({report.Links.Count}, {report.Broken.Count} broken):");
        foreach (var l in report.Links)
        {
            Console.WriteLine($"  [{(l.Exists ? "ok" : "BROKEN")}] {l.Reference.Target} -> {l.ResolvedPath ?? "(unresolved)"}");
        }
        return 0;
    });
    page.Subcommands.Add(show);

    return page;
}
```
> Note: exact `System.CommandLine` 2.0.9 surface (`Required`, `DefaultValueFactory`,
> `AllowMultipleArgumentsPerToken`) may need a minor tweak at build time; the command/handler
> shape mirrors the existing `doctor` pattern.

### 18. `tests/LlmWiki.Domain.Tests/WikiPageTests.cs` (UPDATE)
The default type changed from `Article` to `Summary`:
```csharp
using LlmWiki.Domain;

namespace LlmWiki.Domain.Tests;

public class WikiPageTests
{
    [Fact]
    public void NewPage_GetsIdAndDefaultsToSummary()
    {
        var page = new WikiPage { Title = "Hello" };

        Assert.NotEqual(Guid.Empty, page.Id);
        Assert.Equal("Hello", page.Title);
        Assert.Equal(PageType.Summary, page.Type);
        Assert.Empty(page.Tags);
        Assert.Empty(page.Sources);
    }
}
```

### 19. `tests/LlmWiki.Domain.Tests/CrossReferenceParserTests.cs` (NEW)
```csharp
using LlmWiki.Domain;

namespace LlmWiki.Domain.Tests;

public class CrossReferenceParserTests
{
    [Fact]
    public void Parse_Wikilinks_ExtractsTargetsAndStripsAliases()
    {
        var refs = CrossReferenceParser.Parse(
            "See [[Acme Corp]] and [[Wile E Coyote|the coyote]].", LinkStyle.Wikilink);

        Assert.Equal(["Acme Corp", "Wile E Coyote"], refs.Select(r => r.Target));
    }

    [Fact]
    public void Parse_MarkdownLinks_ExtractsHrefs()
    {
        var refs = CrossReferenceParser.Parse(
            "See [Acme](entities/acme-corp.md).", LinkStyle.MarkdownLink);

        Assert.Equal(["entities/acme-corp.md"], refs.Select(r => r.Target));
    }
}
```

### 20. `tests/LlmWiki.Infrastructure.Tests/FileSystemWikiRepositoryTests.cs` (NEW)
Drives the full stack (real `FileSystemWikiFileStore` over a temp dir) and maps 1:1 to the
Phase 1 acceptance criteria.
```csharp
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using LlmWiki.Infrastructure.FileStore;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Options;

namespace LlmWiki.Infrastructure.Tests;

public sealed class FileSystemWikiRepositoryTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "llmwiki-tests", Guid.NewGuid().ToString("N"));
    private readonly IWikiRepository _repo;

    public FileSystemWikiRepositoryTests()
    {
        var files = new FileSystemWikiFileStore(Options.Create(new WikiOptions { RootPath = _root }));
        _repo = new FileSystemWikiRepository(files);
    }

    [Fact]
    public async Task CreateWiki_WritesTypedDirsAndSchema()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });

        foreach (var dir in WikiSchema.Directories)
        {
            Assert.True(Directory.Exists(Path.Combine(_root, "alpha", dir)));
        }
        Assert.True(File.Exists(Path.Combine(_root, "alpha", "SCHEMA.md")));
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsFrontmatter()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        await _repo.WritePageAsync("alpha", "entities/acme-corp.md", new WikiPage
        {
            Title = "Acme Corp",
            Type = PageType.Entity,
            Tags = ["company", "tech"],
            Sources = ["raw/acme.md"],
            Content = "Acme makes anvils. See [[Wile E Coyote]].",
        });

        var read = await _repo.ReadPageAsync("alpha", "entities/acme-corp.md");

        Assert.Equal("Acme Corp", read.Title);
        Assert.Equal(PageType.Entity, read.Type);
        Assert.Equal(["company", "tech"], read.Tags);
        Assert.Equal(["raw/acme.md"], read.Sources);
        Assert.Contains("anvils", read.Content);
    }

    [Fact]
    public async Task ResolveLinks_FlagsResolvedAndBroken()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha", LinkStyle = LinkStyle.Wikilink });
        await _repo.WritePageAsync("alpha", "entities/wile-e-coyote.md",
            new WikiPage { Title = "Wile E Coyote", Type = PageType.Entity });
        await _repo.WritePageAsync("alpha", "entities/acme-corp.md", new WikiPage
        {
            Title = "Acme Corp",
            Type = PageType.Entity,
            Content = "Customer: [[Wile E Coyote]]. Rival: [[Road Runner]].",
        });

        var report = await _repo.ResolveLinksAsync("alpha", "entities/acme-corp.md");

        Assert.Equal(2, report.Links.Count);
        Assert.Single(report.Broken);
        Assert.Equal("Road Runner", report.Broken[0].Reference.Target);
    }

    [Fact]
    public async Task TwoWikis_DifferentSchemas_CoexistAndAreListed()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha", LinkStyle = LinkStyle.Wikilink });
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "beta", LinkStyle = LinkStyle.MarkdownLink });

        var wikis = await _repo.ListWikisAsync();

        Assert.Equal(2, wikis.Count);
        Assert.Equal(LinkStyle.Wikilink, wikis.Single(w => w.Name == "alpha").LinkStyle);
        Assert.Equal(LinkStyle.MarkdownLink, wikis.Single(w => w.Name == "beta").LinkStyle);
    }

    [Fact]
    public async Task DeletingWikiDirectory_RemovesItFromListing()
    {
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "alpha" });
        await _repo.CreateWikiAsync(new WikiSchema { WikiName = "beta" });

        Directory.Delete(Path.Combine(_root, "beta"), recursive: true);

        var wikis = await _repo.ListWikisAsync();

        Assert.Equal(["alpha"], wikis.Select(w => w.Name));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
```

### 21. `tests/LlmWiki.Infrastructure.Tests/DependencyInjectionTests.cs` (UPDATE)
Add one assertion to the existing `AddLlmWikiInfrastructure_RegistersAllPorts` test:
```csharp
Assert.NotNull(provider.GetRequiredService<IWikiRepository>());
```
(`IWikiFileStore` is now the real implementation but still resolves; no other change needed.)

### 22. Docs (NEW)
- `docs/adr/0002-phase-1-wiki-scaffolding.md` — ADR (Status/Date/Context/Decisions/Consequences)
  recording: PageType replacement, convention+toggles schema, YamlDotNet choice, IWikiRepository
  as the wiki-aware port over IWikiFileStore.

### 23. `CLAUDE.md` (UPDATE)
- Add the `## Working agreement` section (step 0).
- Update the **Stub convention** note: `IWikiFileStore`/`FileSystemWikiFileStore` is now
  implemented (Phase 1), and `IWikiRepository`/`FileSystemWikiRepository` is the new wiki-aware
  port. Note `WIKI_ROOT` config and the new YamlDotNet dependency.

---

## Verification

1. **Build + unit tests** (no external infra needed — file store uses a temp dir):
   ```bash
   dotnet build LlmWiki.slnx
   dotnet test  LlmWiki.slnx --filter "FullyQualifiedName~FileSystemWikiRepository"
   dotnet test  LlmWiki.slnx
   ```
2. **CLI end-to-end** (against a scratch root):
   ```bash
   export WIKI_ROOT=$(mktemp -d)
   dotnet run --project src/LlmWiki.Cli -- wiki create alpha --link-style Wikilink
   dotnet run --project src/LlmWiki.Cli -- wiki create beta  --link-style MarkdownLink
   dotnet run --project src/LlmWiki.Cli -- wiki list                       # shows alpha + beta, distinct styles
   dotnet run --project src/LlmWiki.Cli -- wiki page add alpha entities/wile-e-coyote.md --title "Wile E Coyote" --type Entity
   dotnet run --project src/LlmWiki.Cli -- wiki page add alpha entities/acme-corp.md --title "Acme Corp" --type Entity \
       --tag company --tag tech --body 'Customer: [[Wile E Coyote]]. Rival: [[Road Runner]].'
   dotnet run --project src/LlmWiki.Cli -- wiki inspect alpha             # schema + page list
   dotnet run --project src/LlmWiki.Cli -- wiki page show alpha entities/acme-corp.md   # 1 resolved, 1 BROKEN
   ```
3. **On-disk spot check**: confirm `$WIKI_ROOT/alpha/{summaries,entities,topics,raw}/` exist,
   `SCHEMA.md` has the YAML header + docs, and `entities/acme-corp.md` has valid frontmatter.
4. **Acceptance mapping**: dirs+SCHEMA.md (test #20a), frontmatter parses (#20b), links resolve
   (#20c), two schemas coexist + list (#20d), deletion reflected (#20e).

## Out of scope (later phases)
Oracle persistence/embeddings (P4/P6), ingestion & bidirectional backlink updates (P2),
`index.md`/`log.md` (P3), interactive ingestion. Phase 1 link resolution is read-only reporting.
