using System.Text.Json.Serialization;
using LlmWiki.Agents;
using LlmWiki.Application.Diagnostics;
using LlmWiki.Infrastructure;
using LlmWiki.Shared.Configuration;

var builder = WebApplication.CreateBuilder(args);

// Fold env/.env secrets into configuration (nothing sensitive in source/appsettings).
builder.Configuration.AddLlmWikiEnv();

// Composition root: Infrastructure owns Semantic Kernel + Oracle wiring; Agents hook is a Phase 0 no-op.
builder.Services.AddLlmWikiInfrastructure(builder.Configuration);
builder.Services.AddLlmWikiAgents();
builder.Services.AddOpenApi();

// MVC controllers + Swagger UI power the query surface (POST /query). The existing
// /health and /diagnostics stay as minimal APIs — a deliberate mix — and keep their OpenAPI doc.
// Phase 8: emit enums as their string names (e.g. "Entity" not 1) so the Expo client's typed
// string-union model matches the wire format.
builder.Services.AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Minimal APIs (/health, /diagnostics) use a separate serializer — keep them consistent.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Single local user (NFR-04): allow the Expo web origin (a browser) to call the API.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
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

// Phase 8: let the browser-origin Expo web client through.
app.UseCors();

// the MVC controllers (POST /query, /wikis, /projects, /lint).
app.MapControllers();

app.Run();

// Exposed so integration tests can host the API via WebApplicationFactory.
public partial class Program;
