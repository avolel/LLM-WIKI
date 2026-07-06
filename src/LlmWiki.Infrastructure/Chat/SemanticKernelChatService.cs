using LlmWiki.Application.Ports;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace LlmWiki.Infrastructure.Chat;

/// <summary>
/// Real chat adapter over the Semantic Kernel <see cref="IChatCompletionService"/> registered
/// by the composition root (OpenAI by default, Anthropic as a drop-in). Exercised by Phase 0
/// diagnostics.
/// </summary>
public sealed class SemanticKernelChatService(IChatCompletionService chatCompletion) : IChatService
{
    public async Task<string> CompleteAsync(string prompt, bool jsonMode = false, CancellationToken cancellationToken = default)
    {
        var history = new ChatHistory();
        history.AddUserMessage(prompt);

        // json_object is honored by the OpenAI connector and by Ollama's OpenAI-compatible
        // endpoint, so the model returns a bare JSON object instead of prose the caller must parse.
        var settings = jsonMode
            ? new OpenAIPromptExecutionSettings { ResponseFormat = "json_object" }
            : null;

        var reply = await chatCompletion.GetChatMessageContentAsync(
            history,
            executionSettings: settings,
            cancellationToken: cancellationToken);

        return reply.Content ?? string.Empty;
    }
}
