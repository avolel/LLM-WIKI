using LlmWiki.Agents;
using LlmWiki.Application.Diagnostics;
using LlmWiki.Infrastructure;
using LlmWiki.Shared.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Fold env/.env secrets into configuration (NFR-01: nothing sensitive in source/appsettings).
builder.Configuration.AddLlmWikiEnv();

// Composition root: Infrastructure owns SK + Oracle wiring; Agents hook is a Phase 0 no-op.
builder.Services.AddLlmWikiInfrastructure(builder.Configuration);
builder.Services.AddLlmWikiAgents();
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Liveness: process is up. Cheap, no dependencies touched.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
   .WithName("Health");

// Phase 0 acceptance: runs the three connectivity checks (Oracle, embedding, chat).
// Returns 200 when all pass, 503 otherwise, with per-check detail.
app.MapGet("/diagnostics", async (IDiagnosticsService diagnostics, CancellationToken ct) =>
{
    var report = await diagnostics.RunAsync(ct);
    return report.AllPassed
        ? Results.Ok(report)
        : Results.Json(report, statusCode: StatusCodes.Status503ServiceUnavailable);
})
.WithName("Diagnostics");

app.Run();

// Exposed so integration tests can host the API via WebApplicationFactory.
public partial class Program;
