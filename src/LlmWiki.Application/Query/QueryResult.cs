using LlmWiki.Domain;

namespace LlmWiki.Application.Query;

/// <summary>Knobs for one query: how many candidates to retrieve and an optional page-type filter.</summary>
public sealed record QueryOptions(int TopK = 5, PageType? TypeFilter = null);

/// <summary>One prior Q/A turn, replayed into the synthesis prompt for follow-ups.</summary>
public sealed record ConversationTurn(string Question, string Answer);

/// <summary>A page the answer drew from, resolvable to an openable file.</summary>
public sealed record Citation(string RelativePath, string Title, PageType Type);

/// <summary>
/// Structured result of one query. <see cref="Answer"/> is markdown whose format the model chooses
/// ; <see cref="Covered"/> is false when the corpus does not cover the question.
/// </summary>
public sealed record QueryResult(
    string WikiName,
    string Question,
    string Answer,               // markdown; format chosen by the model
    bool Covered,                // false => honest gap report
    IReadOnlyList<Citation> Citations,
    string SuggestedTitle);      // used as the slug when saved
