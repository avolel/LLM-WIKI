using LlmWiki.Application.Diagnostics;
using LlmWiki.Application.Ports;

namespace LlmWiki.Application.Tests;

public class DiagnosticsServiceTests
{
    [Fact]
    public async Task RunAsync_AllHealthy_ReportsAllPassed()
    {
        var service = new DiagnosticsService(
            new FakeDatabase(throwOnRun: false),
            new FakeEmbeddings(dimensions: 768),
            new FakeChat(reply: "ok"),
            expectedDimensions: 768);

        var report = await service.RunAsync();

        Assert.True(report.AllPassed);
        Assert.Equal(3, report.Checks.Count);
    }

    [Fact]
    public async Task RunAsync_WrongEmbeddingDimension_FailsEmbeddingCheckOnly()
    {
        var service = new DiagnosticsService(
            new FakeDatabase(throwOnRun: false),
            new FakeEmbeddings(dimensions: 512),
            new FakeChat(reply: "ok"),
            expectedDimensions: 768);

        var report = await service.RunAsync();

        Assert.False(report.AllPassed);
        Assert.False(report.Checks.Single(c => c.Name == "Embedding").Passed);
        Assert.True(report.Checks.Single(c => c.Name == "Oracle").Passed);
        Assert.True(report.Checks.Single(c => c.Name == "Chat").Passed);
    }

    [Fact]
    public async Task RunAsync_OracleThrows_FailsOracleCheckWithoutThrowing()
    {
        var service = new DiagnosticsService(
            new FakeDatabase(throwOnRun: true),
            new FakeEmbeddings(dimensions: 768),
            new FakeChat(reply: "ok"),
            expectedDimensions: 768);

        var report = await service.RunAsync();

        Assert.False(report.Checks.Single(c => c.Name == "Oracle").Passed);
    }

    private sealed class FakeDatabase(bool throwOnRun) : IDatabaseHealthCheck
    {
        public Task CreateTableRoundtripAsync(CancellationToken cancellationToken = default) =>
            throwOnRun ? throw new InvalidOperationException("boom") : Task.CompletedTask;
    }

    private sealed class FakeEmbeddings(int dimensions) : IEmbeddingService
    {
        public Task<ReadOnlyMemory<float>> EmbedAsync(string text, CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(new float[dimensions]);
    }

    private sealed class FakeChat(string reply) : IChatService
    {
        public Task<string> CompleteAsync(string prompt, bool jsonMode = false, CancellationToken cancellationToken = default) =>
            Task.FromResult(reply);
    }
}
