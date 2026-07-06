namespace LlmWiki.Domain;

/// <summary>
/// Converts a human title into a stable, filesystem-safe slug used for page filenames
/// (e.g. "Acme Corp" → "acme-corp"). Lifted out of the file store so Domain consumers and the
/// Agents layer can produce matching paths without depending on Infrastructure.
/// </summary>
public static class Slug
{
    public static string From(string title)
    {
        var chars = title.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
            .ToArray();
        var cleaned = new string(chars);
        return string.Join('-', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    } 
}
