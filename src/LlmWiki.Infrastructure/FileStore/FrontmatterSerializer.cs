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