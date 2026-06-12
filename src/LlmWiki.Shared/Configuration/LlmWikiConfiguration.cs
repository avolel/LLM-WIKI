using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LlmWiki.Shared.Configuration;

/// <summary>
/// Central configuration entry point. Loads <c>env/.env</c>, translates the flat env-var
/// names used in <c>env/.env.example</c> into bindable option sections, and registers the
/// strongly-typed option objects. Used by both the API and CLI composition roots.
/// </summary>
public static class LlmWikiConfiguration
{
    /// <summary>Maps the flat env-var names (NFR-01 template) to "Section:Key" config paths.</summary>
    private static readonly Dictionary<string, string> EnvToConfigKey = new(StringComparer.Ordinal)
    {
        ["ORACLE_CONNECTION_STRING"] = $"{OracleOptions.SectionName}:{nameof(OracleOptions.ConnectionString)}",
        ["OLLAMA_ENDPOINT"] = $"{EmbeddingOptions.SectionName}:{nameof(EmbeddingOptions.Endpoint)}",
        ["EMBEDDING_MODEL"] = $"{EmbeddingOptions.SectionName}:{nameof(EmbeddingOptions.Model)}",
        ["EMBEDDING_DIM"] = $"{EmbeddingOptions.SectionName}:{nameof(EmbeddingOptions.Dimensions)}",
        ["CHAT_PROVIDER"] = $"{ChatOptions.SectionName}:{nameof(ChatOptions.Provider)}",
        ["CHAT_MODEL"] = $"{ChatOptions.SectionName}:{nameof(ChatOptions.Model)}",
        ["OPENAI_API_KEY"] = $"{ChatOptions.SectionName}:{nameof(ChatOptions.OpenAiApiKey)}",
        ["ANTHROPIC_API_KEY"] = $"{ChatOptions.SectionName}:{nameof(ChatOptions.AnthropicApiKey)}",
        ["WIKI_ROOT"] = $"{WikiOptions.SectionName}:{nameof(WikiOptions.RootPath)}",
    };

    /// <summary>
    /// Builds an <see cref="IConfiguration"/> from <c>env/.env</c> + process environment.
    /// Call this when there is no host-provided configuration (e.g. the CLI).
    /// </summary>
    public static IConfiguration Build() =>
        new ConfigurationBuilder().AddLlmWikiEnv().Build();

    /// <summary>
    /// Loads <c>env/.env</c> and adds the mapped section keys as an in-memory source.
    /// Use from a host builder (e.g. the API) to fold env secrets into its configuration.
    /// </summary>
    public static IConfigurationBuilder AddLlmWikiEnv(this IConfigurationBuilder builder)
    {
        DotEnvLoader.Load();

        var mapped = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (envName, configKey) in EnvToConfigKey)
        {
            var value = Environment.GetEnvironmentVariable(envName);
            if (!string.IsNullOrEmpty(value))
            {
                mapped[configKey] = value;
            }
        }

        return builder.AddInMemoryCollection(mapped);
    }

    /// <summary>
    /// Registers <see cref="OracleOptions"/>, <see cref="EmbeddingOptions"/> and
    /// <see cref="ChatOptions"/> bound from the supplied configuration.
    /// </summary>
    public static IServiceCollection AddLlmWikiOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<OracleOptions>(configuration.GetSection(OracleOptions.SectionName));
        services.Configure<EmbeddingOptions>(configuration.GetSection(EmbeddingOptions.SectionName));
        services.Configure<ChatOptions>(configuration.GetSection(ChatOptions.SectionName));
        services.Configure<WikiOptions>(configuration.GetSection(WikiOptions.SectionName));
        return services;
    }
}
