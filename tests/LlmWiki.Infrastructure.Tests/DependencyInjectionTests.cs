using LlmWiki.Application.Diagnostics;
using LlmWiki.Application.Ports;
using LlmWiki.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LlmWiki.Infrastructure.Tests;

public class DependencyInjectionTests
{
    [Fact]
    public void AddLlmWikiInfrastructure_RegistersAllPorts()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Embedding:Endpoint"] = "http://localhost:11434",
                ["Embedding:Model"] = "nomic-embed-text",
                ["Embedding:Dimensions"] = "768",
                ["Chat:Provider"] = "openai",
                // No API key on purpose: chat falls back to NotConfiguredChatService.
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLlmWikiInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IDatabaseHealthCheck>());
        Assert.NotNull(provider.GetRequiredService<IEmbeddingService>());
        Assert.NotNull(provider.GetRequiredService<IChatService>());
        Assert.NotNull(provider.GetRequiredService<IVectorStore>());
        Assert.NotNull(provider.GetRequiredService<IWikiFileStore>());
        Assert.NotNull(provider.GetRequiredService<IProjectRepository>());
        Assert.NotNull(provider.GetRequiredService<IDiagnosticsService>());
    }

    [Fact]
    public async Task ChatService_WithoutApiKey_FailsWithClearMessage()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Chat:Provider"] = "openai" })
            .Build();

        var services = new ServiceCollection();
        services.AddLlmWikiInfrastructure(configuration);
        using var provider = services.BuildServiceProvider();

        var chat = provider.GetRequiredService<IChatService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.CompleteAsync("hi"));
    }
}
