using LlmWiki.Agents;
using LlmWiki.Application.Linting;
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

    [Fact]
    public void AddLlmWikiAgents_RegistersLintService()
    {
        var services = new ServiceCollection();

        services.AddLlmWikiAgents();

        //the lint orchestrator is wired behind its port (Phase 7).
        Assert.Contains(services, d => d.ServiceType == typeof(ILintService));
    }
}
