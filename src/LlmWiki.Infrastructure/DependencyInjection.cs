using LlmWiki.Application.Diagnostics;
using LlmWiki.Application.Ports;
using LlmWiki.Infrastructure.Chat;
using LlmWiki.Infrastructure.Embeddings;
using LlmWiki.Infrastructure.FileStore;
using LlmWiki.Infrastructure.Persistence;
using LlmWiki.Infrastructure.VectorStore;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;

namespace LlmWiki.Infrastructure;

/// <summary>
/// Composition for the Infrastructure layer. Owns ALL Semantic Kernel and Oracle wiring
/// (NFR-07) so the API and CLI hosts only need to call <see cref="AddLlmWikiInfrastructure"/>.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddLlmWikiInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Strongly-typed options (Oracle / Embedding / Chat) from env-backed config.
        services.AddLlmWikiOptions(configuration);

        var embedding = configuration.GetSection(EmbeddingOptions.SectionName).Get<EmbeddingOptions>() ?? new EmbeddingOptions();
        var chat = configuration.GetSection(ChatOptions.SectionName).Get<ChatOptions>() ?? new ChatOptions();

        AddEmbeddings(services, embedding);
        AddChat(services, chat);

        // Oracle connectivity probe (real) + project persistence (stub until Phase 3).
        services.AddSingleton<IDatabaseHealthCheck, OracleDatabaseHealthCheck>();
        services.AddSingleton<IProjectRepository, OracleProjectRepository>();

        // Phase 4: real Oracle VECTOR + Oracle Text hybrid store (replaces the stub).
        services.AddSingleton<IVectorStore, OracleVectorStore>();

        // Phase 1: real file store + wiki-aware repository.
        services.AddSingleton<IWikiFileStore, FileSystemWikiFileStore>();
        services.AddSingleton<IWikiRepository, FileSystemWikiRepository>();

        // Phase 3: agent-owned index.md / log.md journal.
        services.AddSingleton<IWikiJournal, FileSystemWikiJournal>();

        // Diagnostics orchestrator — asserts the embedding dimension from config.
        services.AddSingleton<IDiagnosticsService>(sp => new DiagnosticsService(
            sp.GetRequiredService<IDatabaseHealthCheck>(),
            sp.GetRequiredService<IEmbeddingService>(),
            sp.GetRequiredService<IChatService>(),
            embedding.Dimensions));

        return services;
    }

    private static void AddEmbeddings(IServiceCollection services, EmbeddingOptions options)
    {
        // SK Ollama connector registers IEmbeddingGenerator<string, Embedding<float>>.
        services.AddOllamaEmbeddingGenerator(options.Model, new Uri(options.Endpoint));
        services.AddSingleton<IEmbeddingService, OllamaEmbeddingService>();
    }

    private static void AddChat(IServiceCollection services, ChatOptions options)
    {
        // Local Ollama via its OpenAI-compatible endpoint. No API key needed (the placeholder
        // satisfies the SK connector, which requires a non-empty key); the chat model must be
        // pulled in Ollama beforehand.
        if (options.IsOllama)
        {
            var endpoint = string.IsNullOrWhiteSpace(options.Endpoint)
                ? "http://localhost:11434/v1/"
                : options.Endpoint;

            services.AddOpenAIChatCompletion(options.Model, new Uri(endpoint), apiKey: "ollama");
            services.AddSingleton<IChatService, SemanticKernelChatService>();
            return;
        }

        // Default wiring: OpenAI connector. Anthropic is a drop-in via its OpenAI-compatible
        // endpoint. If no key is configured, register a fallback so the host still starts.
        if (options.IsAnthropic)
        {
            if (string.IsNullOrWhiteSpace(options.AnthropicApiKey))
            {
                services.AddSingleton<IChatService>(_ => new NotConfiguredChatService("anthropic"));
                return;
            }

            services.AddOpenAIChatCompletion(
                options.Model,
                new Uri("https://api.anthropic.com/v1/"),
                options.AnthropicApiKey);
            services.AddSingleton<IChatService, SemanticKernelChatService>();
            return;
        }

        if (string.IsNullOrWhiteSpace(options.OpenAiApiKey))
        {
            services.AddSingleton<IChatService>(_ => new NotConfiguredChatService("openai"));
            return;
        }

        services.AddOpenAIChatCompletion(options.Model, options.OpenAiApiKey);
        services.AddSingleton<IChatService, SemanticKernelChatService>();
    }
}
