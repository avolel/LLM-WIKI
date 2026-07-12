using LlmWiki.Domain;

namespace LlmWiki.Application.Linting;

/// <summary>Priority bucket for a finding (BR-061): critical → warning → suggestion.</summary>
public enum LintSeverity { Critical, Warning, Suggestion }

/// <summary>What kind of issue a finding is (BR-060/062).</summary>
public enum LintCategory
{
    Contradiction, StaleClaim,          // critical (LLM)
    BrokenLink, MissingPage, Orphan,    // warning (structural)
    ThinPage, SuggestedQuestion, SuggestedSource   // suggestion
}

/// <summary>
/// A concrete, applyable fix carried by a finding. This phase only stub-page creation: accepting a
/// MissingPage finding writes this page. Report-only findings carry <c>Fix = null</c>.
/// </summary>
public sealed record SuggestedFix(string RelativePath, string Title, PageType Type, string Body);

/// <summary>One lint finding. <see cref="Pages"/> names the specific page(s) — both sides for a
/// contradiction (BR-061). <see cref="Fix"/> present → applyable via <c>ApplyFixAsync</c>.</summary>
public sealed record LintFinding(
    LintSeverity Severity,
    LintCategory Category,
    string Summary,
    IReadOnlyList<string> Pages,
    string? SuggestedAction = null,
    SuggestedFix? Fix = null);

/// <summary>Result of one lint pass. Findings are pre-sorted critical → warning → suggestion (BR-061).</summary>
public sealed record LintReport(string WikiName, IReadOnlyList<LintFinding> Findings)
{
    public bool IsClean => Findings.Count == 0;
    public int CriticalCount => Findings.Count(f => f.Severity == LintSeverity.Critical);
    public int WarningCount  => Findings.Count(f => f.Severity == LintSeverity.Warning);
}
