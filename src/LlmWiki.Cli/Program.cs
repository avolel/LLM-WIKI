using System.CommandLine;
using LlmWiki.Application.Diagnostics;
using LlmWiki.Infrastructure;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.DependencyInjection;

var root = new RootCommand("LLM Wiki CLI — local operations and diagnostics.");

// `doctor` mirrors the API /diagnostics endpoint: Oracle, embedding (768-dim), and chat checks.
var doctor = new Command("doctor", "Run the Phase 0 connectivity checks and report pass/fail.");
doctor.SetAction((_, ct) => RunDoctorAsync(ct));
root.Subcommands.Add(doctor);

return await root.Parse(args).InvokeAsync();

static async Task<int> RunDoctorAsync(CancellationToken cancellationToken)
{
    // Minimal composition root for the CLI — same Infrastructure wiring as the API.
    var services = new ServiceCollection();
    services.AddLlmWikiInfrastructure(LlmWikiConfiguration.Build());
    await using var provider = services.BuildServiceProvider();

    var diagnostics = provider.GetRequiredService<IDiagnosticsService>();
    var report = await diagnostics.RunAsync(cancellationToken);

    Console.WriteLine("LLM Wiki — Phase 0 diagnostics");
    Console.WriteLine("------------------------------");
    foreach (var check in report.Checks)
    {
        var marker = check.Passed ? "PASS" : "FAIL";
        Console.WriteLine($"[{marker}] {check.Name,-10} {check.Detail}");
    }

    Console.WriteLine();
    Console.WriteLine(report.AllPassed ? "All checks passed." : "One or more checks FAILED.");
    return report.AllPassed ? 0 : 1;
}
