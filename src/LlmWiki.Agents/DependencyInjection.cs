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
        // No agents registered yet — plugins/processes land in later phases.
        return services;
    }
}
