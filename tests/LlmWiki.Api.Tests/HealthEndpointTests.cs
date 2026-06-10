using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LlmWiki.Api.Tests;

public class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    [Fact]
    public async Task Health_ReturnsOk()
    {
        // Boots the full app (incl. Infrastructure DI). /health touches no external services.
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
