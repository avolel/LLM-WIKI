namespace LlmWiki.Shared.Configuration;

/// <summary>Hosted chat provider settings, selected at runtime by <see cref="Provider"/>.</summary>
public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    /// <summary>"openai" (default) or "anthropic" (env: CHAT_PROVIDER).</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>Model id, e.g. gpt-4o-mini or claude-* (env: CHAT_MODEL).</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>OpenAI API key (env: OPENAI_API_KEY). Never logged.</summary>
    public string OpenAiApiKey { get; set; } = string.Empty;

    /// <summary>Anthropic API key (env: ANTHROPIC_API_KEY). Never logged.</summary>
    public string AnthropicApiKey { get; set; } = string.Empty;

    public bool IsAnthropic =>
        string.Equals(Provider, "anthropic", StringComparison.OrdinalIgnoreCase);
}
