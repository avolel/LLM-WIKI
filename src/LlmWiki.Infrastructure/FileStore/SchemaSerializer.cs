using System.Text;
using LlmWiki.Domain;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace LlmWiki.Infrastructure.FileStore;

/// <summary>Renders and parses SCHEMA.md — a documented, machine-readable wiki schema.</summary>
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
        sb.Append("- `answers/` — saved query answers (`type: answer`), created on demand.\n");
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