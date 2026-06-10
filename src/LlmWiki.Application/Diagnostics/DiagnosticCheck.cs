namespace LlmWiki.Application.Diagnostics;

/// <summary>Result of one connectivity check.</summary>
public sealed record DiagnosticCheck(string Name, bool Passed, string Detail);

/// <summary>Aggregate of all Phase 0 connectivity checks.</summary>
public sealed record DiagnosticsReport(IReadOnlyList<DiagnosticCheck> Checks)
{
    public bool AllPassed => Checks.All(c => c.Passed);
}
