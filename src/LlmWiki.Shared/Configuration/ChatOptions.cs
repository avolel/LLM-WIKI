namespace LlmWiki.Shared.Configuration;

/// <summary>Hosted chat provider settings, selected at runtime by <see cref="Provider"/>.</summary>
public sealed class ChatOptions
{
    public const string SectionName = "Chat";

    /// <summary>"openai" (default), "anthropic", or "ollama" (env: CHAT_PROVIDER).</summary>
    public string Provider { get; set; } = "openai";

    /// <summary>Model id, e.g. gpt-4o-mini, claude-*, or a local Ollama model like llama3.1 (env: CHAT_MODEL).</summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>OpenAI-compatible chat endpoint. Only used for "ollama" (env: CHAT_ENDPOINT).
    /// Defaults to the local Ollama server when empty.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>OpenAI API key (env: OPENAI_API_KEY). Never logged.</summary>
    public string OpenAiApiKey { get; set; } = string.Empty;

    /// <summary>Anthropic API key (env: ANTHROPIC_API_KEY). Never logged.</summary>
    public string AnthropicApiKey { get; set; } = string.Empty;

    public bool IsAnthropic =>
        string.Equals(Provider, "anthropic", StringComparison.OrdinalIgnoreCase);

    public bool IsOllama =>
        string.Equals(Provider, "ollama", StringComparison.OrdinalIgnoreCase);
}
