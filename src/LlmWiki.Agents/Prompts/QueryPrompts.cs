using System.Text;
using LlmWiki.Application.Query;
using LlmWiki.Domain;

namespace LlmWiki.Agents.Prompts;

/// <summary>
/// Synthesis prompt for the query workflow. Sibling to <see cref="IngestionPrompts"/>:
/// grounded to the provided CONTEXT, schema-parameterised, and instructed to report gaps
/// honestly rather than speculate.
/// </summary>
internal static class QueryPrompts
{
    public static string Synthesize(
        WikiSchema schema, string indexMarkdown, string question,
        string retrievedContext, IReadOnlyList<ConversationTurn> history) => $$"""
        You answer questions about the wiki "{{schema.WikiName}}" using ONLY the CONTEXT below.
        - Cite the specific pages you use by their relative path (e.g. entities/acme.md).
        - If the CONTEXT does not cover the question, set "covered": false and say so plainly —
          do NOT speculate (honest-gap requirement).
        - Choose the format that fits: prose, a markdown table for comparisons, a list for timelines.
        - "title" is a short, descriptive title for this answer (used if it is saved as a page).
        Return one JSON object, no fence: {"title":"…","answer":"…","covered":true,"citations":["…"]}

        WIKI INDEX:
        {{indexMarkdown}}

        {{FormatHistory(history)}}QUESTION: {{question}}

        CONTEXT:
        {{retrievedContext}}
        """;

    /// <summary>Renders prior Q/A turns for a follow-up; empty string for a first question.</summary>
    private static string FormatHistory(IReadOnlyList<ConversationTurn> history)
    {
        if (history.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("CONVERSATION SO FAR:");
        foreach (var turn in history)
        {
            sb.AppendLine($"Q: {turn.Question}");
            sb.AppendLine($"A: {turn.Answer}");
        }
        sb.AppendLine();
        return sb.ToString();
    }
}
