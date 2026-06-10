using LlmWiki.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace LlmWiki.Agents.Tests;

public class AgentsRegistrationTests
{
    [Fact]
    public void AddLlmWikiAgents_IsIdempotentNoOp_InPhase0()
    {
        var services = new ServiceCollection();

        var result = services.AddLlmWikiAgents();

        // Phase 0: no agents registered yet, but the hook resolves and returns the collection.
        Assert.Same(services, result);
    }
}
