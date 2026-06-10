using LlmWiki.Application.Ports;

namespace LlmWiki.Application.Diagnostics;

/// <summary>
/// Orchestrates the Phase 0 acceptance checks against the ports:
/// (1) Oracle connect + CREATE TABLE/drop round-trip;
/// (2) embed a test string and assert the vector length equals <paramref name="expectedDimensions"/>;
/// (3) hosted chat round-trip returns non-empty text.
/// Each check is isolated so one failure does not mask the others.
/// </summary>
public sealed class DiagnosticsService(
    IDatabaseHealthCheck database,
    IEmbeddingService embeddings,
    IChatService chat,
    int expectedDimensions) : IDiagnosticsService
{
    private const string ProbeText = "LLM Wiki Phase 0 diagnostics probe.";

    public async Task<DiagnosticsReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var checks = new List<DiagnosticCheck>
        {
            await RunOracleCheckAsync(cancellationToken),
            await RunEmbeddingCheckAsync(cancellationToken),
            await RunChatCheckAsync(cancellationToken),
        };

        return new DiagnosticsReport(checks);
    }

    private async Task<DiagnosticCheck> RunOracleCheckAsync(CancellationToken ct)
    {
        try
        {
            await database.CreateTableRoundtripAsync(ct);
            return new DiagnosticCheck("Oracle", true, "CREATE TABLE / DROP round-trip succeeded.");
        }
        catch (Exception ex)
        {
            return new DiagnosticCheck("Oracle", false, ex.Message);
        }
    }

    private async Task<DiagnosticCheck> RunEmbeddingCheckAsync(CancellationToken ct)
    {
        try
        {
            var vector = await embeddings.EmbedAsync(ProbeText, ct);
            var passed = vector.Length == expectedDimensions;
            var detail = passed
                ? $"Embedding returned {vector.Length}-dim vector."
                : $"Expected {expectedDimensions} dimensions but got {vector.Length}.";
            return new DiagnosticCheck("Embedding", passed, detail);
        }
        catch (Exception ex)
        {
            return new DiagnosticCheck("Embedding", false, ex.Message);
        }
    }

    private async Task<DiagnosticCheck> RunChatCheckAsync(CancellationToken ct)
    {
        try
        {
            var reply = await chat.CompleteAsync("Reply with the single word: ok", ct);
            var passed = !string.IsNullOrWhiteSpace(reply);
            var detail = passed
                ? $"Chat replied ({reply.Trim().Length} chars)."
                : "Chat returned empty text.";
            return new DiagnosticCheck("Chat", passed, detail);
        }
        catch (Exception ex)
        {
            return new DiagnosticCheck("Chat", false, ex.Message);
        }
    }
}
