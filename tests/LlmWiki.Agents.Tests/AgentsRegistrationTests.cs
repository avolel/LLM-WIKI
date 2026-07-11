using LlmWiki.Agents;
using LlmWiki.Application.Query;
using Microsoft.Extensions.DependencyInjection;

namespace LlmWiki.Agents.Tests;

public class AgentsRegistrationTests
{
    [Fact]
    public void AddLlmWikiAgents_ReturnsTheSameCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddLlmWikiAgents();

        Assert.Same(services, result);
    }

    [Fact]
    public void AddLlmWikiAgents_RegistersQueryService()
    {
        var services = new ServiceCollection();

        services.AddLlmWikiAgents();

        //the query orchestrator is wired behind its port.
        Assert.Contains(services, d => d.ServiceType == typeof(IQueryService));
    }
}
