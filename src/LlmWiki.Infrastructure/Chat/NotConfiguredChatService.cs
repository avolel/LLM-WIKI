using LlmWiki.Application.Ports;

namespace LlmWiki.Infrastructure.Chat;

/// <summary>
/// Registered in place of the real chat adapter when no API key is configured, so the host
/// still starts (e.g. for /health) and the diagnostics chat check fails with a clear message
/// instead of crashing startup.
/// </summary>
public sealed class NotConfiguredChatService(string provider) : IChatService
{
    public Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            $"Chat provider '{provider}' has no API key configured " +
            "(set OPENAI_API_KEY or ANTHROPIC_API_KEY in env/.env).");
}
