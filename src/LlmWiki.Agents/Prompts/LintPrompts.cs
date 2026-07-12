using LlmWiki.Domain;

namespace LlmWiki.Agents.Prompts;

/// <summary>
/// Semantic health-check prompt: given the index + a digest of the pages, find contradictions and
/// stale claims, and suggest research directions. Structural issues (broken links, orphans) are found
/// deterministically, NOT here, so this prompt stays focused on judgment (BR-060/062).
/// </summary>
internal static class LintPrompts
{
    public static string Analyze(WikiSchema schema, string indexMarkdown, string pagesDigest) => $$"""
        You are auditing the wiki "{{schema.WikiName}}" for internal consistency using ONLY the PAGES below.
        Report ONLY issues you can justify from the PAGES — do not invent problems.
        - contradictions: two pages that assert incompatible facts. List BOTH page paths and describe the conflict.
        - staleClaims: a claim that is likely outdated or superseded by another page. Name the page.
        - questions: useful follow-up questions the wiki does not yet answer (research planning).
        - sources: kinds of source material the user should seek to fill gaps.
        Return one JSON object, no fence:
        {"contradictions":[{"pages":["a.md","b.md"],"description":"…"}],
         "staleClaims":[{"page":"…","description":"…"}],
         "questions":["…"],"sources":["…"]}

        WIKI INDEX:
        {{indexMarkdown}}

        PAGES:
        {{pagesDigest}}
        """;
}
