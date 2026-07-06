using LlmWiki.Agents.Ingestion;
using LlmWiki.Application.Ingestion;
using Microsoft.Extensions.DependencyInjection;

namespace LlmWiki.Agents;

/// <summary>
/// Composition for the Agents layer (Semantic Kernel plugins + Process Framework workflows).
/// Phase 0 placeholder: the folders <c>Plugins/</c>, <c>Processes/</c> and <c>Prompts/</c> are
/// empty until the agent phases. The hook exists now so hosts wire it once and never again.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddLlmWikiAgents(this IServiceCollection services)
    {
        // Phase 2: ingestion orchestrator (plain; SK Process Framework can replace it behind the port).
        services.AddSingleton<IIngestionService, IngestionService>();
        return services;
    }
}
