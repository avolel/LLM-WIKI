using LlmWiki.Domain;

namespace LlmWiki.Agents.Prompts;

/// <summary>Page-type-specific prompts (BR-016), constrained to the source content (R-01) and
/// guided by the wiki's schema conventions.</summary>
internal static class IngestionPrompts
{
    public static string Extract(WikiSchema schema, string sourceContent) => $$"""
        You are a knowledge-extraction agent for a structured wiki named "{{schema.WikiName}}".
        Read the SOURCE below and extract ONLY facts present in it. Do NOT invent or infer beyond the text.
        Return a single JSON object, no markdown fence, matching exactly this shape:
        {
          "sourceTitle": string,
          "summary": string,            // 3-6 sentence faithful summary
          "keyPoints": [string],
          "entities": [{"name": string, "description": string, "thin": boolean}],
          "concepts": [{"name": string, "description": string, "thin": boolean}],
          "tags": [string],
          "topicTitle": string,         // the overarching topic this source belongs to
          "topicSummary": string        // how this source relates to that topic
        }
        Set "thin" to true when the source only mentions an entity/concept in passing.

        SOURCE:
        {{sourceContent}}
        """;

    public static string Reconcile(string newSourceSummary, string existingPagesBlock) => $$"""
        Compare the NEW source summary against EXISTING wiki pages. Report only genuine factual
        contradictions — where a claim in the new source conflicts with a claim already on a page.
        Do not report mere additions. Return a single JSON object, no markdown fence:
        { "contradictions": [{"page": "<relative path>", "description": "<both sides, cited>"}] }

        NEW SOURCE SUMMARY:
        {{newSourceSummary}}

        EXISTING PAGES:
        {{existingPagesBlock}}
        """;
}
