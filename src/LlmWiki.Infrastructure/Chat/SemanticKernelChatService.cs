using LlmWiki.Application.Ports;
using Microsoft.SemanticKernel.ChatCompletion;

namespace LlmWiki.Infrastructure.Chat;

/// <summary>
/// Real chat adapter over the Semantic Kernel <see cref="IChatCompletionService"/> registered
/// by the composition root (OpenAI by default, Anthropic as a drop-in). Exercised by Phase 0
/// diagnostics.
/// </summary>
public sealed class SemanticKernelChatService(IChatCompletionService chatCompletion) : IChatService
{
    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var history = new ChatHistory();
        history.AddUserMessage(prompt);

        var reply = await chatCompletion.GetChatMessageContentAsync(
            history,
            cancellationToken: cancellationToken);

        return reply.Content ?? string.Empty;
    }
}
