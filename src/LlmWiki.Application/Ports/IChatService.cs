namespace LlmWiki.Application.Ports;

/// <summary>
/// Port for hosted chat completion. Backed in Infrastructure by a Semantic Kernel
/// <c>IChatCompletionService</c> (OpenAI by default, Anthropic as a drop-in). The
/// round-trip path is exercised by Phase 0 diagnostics.
/// </summary>
public interface IChatService
{
    /// <summary>Send a prompt and return the assistant's reply text.</summary>
    Task<string> CompleteAsync(string prompt, bool jsonMode = false, CancellationToken cancellationToken = default);
}
