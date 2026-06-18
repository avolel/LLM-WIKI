using System.CommandLine;
using LlmWiki.Application.Diagnostics;
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using LlmWiki.Infrastructure;
using LlmWiki.Shared.Configuration;
using Microsoft.Extensions.DependencyInjection;

var root = new RootCommand("LLM Wiki CLI — local operations and diagnostics.");

// ---- doctor (Phase 0) ------------------------------------------------------
var doctor = new Command("doctor", "Run the Phase 0 connectivity checks and report pass/fail.");
doctor.SetAction((_, ct) => RunDoctorAsync(ct));
root.Subcommands.Add(doctor);

// ---- wiki (Phase 1) --------------------------------------------------------
root.Subcommands.Add(BuildWikiCommand());

return await root.Parse(args).InvokeAsync();

static ServiceProvider BuildProvider()
{
    var services = new ServiceCollection();
    services.AddLlmWikiInfrastructure(LlmWikiConfiguration.Build());
    return services.BuildServiceProvider();
}

static async Task<int> RunDoctorAsync(CancellationToken cancellationToken)
{
    await using var provider = BuildProvider();
    var diagnostics = provider.GetRequiredService<IDiagnosticsService>();
    var report = await diagnostics.RunAsync(cancellationToken);

    Console.WriteLine("LLM Wiki — Phase 0 diagnostics");
    Console.WriteLine("------------------------------");
    foreach (var check in report.Checks)
    {
        Console.WriteLine($"[{(check.Passed ? "PASS" : "FAIL")}] {check.Name,-10} {check.Detail}");
    }
    Console.WriteLine();
    Console.WriteLine(report.AllPassed ? "All checks passed." : "One or more checks FAILED.");
    return report.AllPassed ? 0 : 1;
}

static Command BuildWikiCommand()
{
    var wiki = new Command("wiki", "Create, list, and inspect wikis.");

    // create
    var createName = new Argument<string>("name") { Description = "Wiki name (directory)." };
    var linkStyle = new Option<LinkStyle>("--link-style")
    {
        Description = "Cross-reference style.",
        DefaultValueFactory = _ => LinkStyle.Wikilink,
    };
    var create = new Command("create", "Scaffold a new wiki (typed dirs + SCHEMA.md).");
    create.Arguments.Add(createName);
    create.Options.Add(linkStyle);
    create.SetAction(async (pr, ct) =>
    {
        await using var provider = BuildProvider();
        var repo = provider.GetRequiredService<IWikiRepository>();
        var schema = new WikiSchema { WikiName = pr.GetValue(createName)!, LinkStyle = pr.GetValue(linkStyle) };
        await repo.CreateWikiAsync(schema, ct);
        Console.WriteLine($"Created wiki '{schema.WikiName}' ({schema.LinkStyle}).");
        return 0;
    });
    wiki.Subcommands.Add(create);

    // list
    var list = new Command("list", "List all wikis.");
    list.SetAction(async (_, ct) =>
    {
        await using var provider = BuildProvider();
        var repo = provider.GetRequiredService<IWikiRepository>();
        var wikis = await repo.ListWikisAsync(ct);
        if (wikis.Count == 0) { Console.WriteLine("No wikis found."); return 0; }
        foreach (var w in wikis)
        {
            Console.WriteLine($"{w.Name,-20} {w.LinkStyle,-12} {w.PageCount} page(s)");
        }
        return 0;
    });
    wiki.Subcommands.Add(list);

    // inspect
    var inspectName = new Argument<string>("name") { Description = "Wiki name." };
    var inspect = new Command("inspect", "Show a wiki's schema and pages.");
    inspect.Arguments.Add(inspectName);
    inspect.SetAction(async (pr, ct) =>
    {
        await using var provider = BuildProvider();
        var repo = provider.GetRequiredService<IWikiRepository>();
        var name = pr.GetValue(inspectName)!;
        if (!await repo.WikiExistsAsync(name, ct))
        {
            await Console.Error.WriteLineAsync($"Wiki '{name}' not found.");
            return 1;
        }
        var schema = await repo.ReadSchemaAsync(name, ct);
        var pages = await repo.ListPagesAsync(name, ct);
        Console.WriteLine($"Wiki:        {schema.WikiName}");
        Console.WriteLine($"Link style:  {schema.LinkStyle}");
        Console.WriteLine($"Frontmatter: {string.Join(", ", schema.FrontmatterFields)}");
        Console.WriteLine($"Pages ({pages.Count}):");
        foreach (var p in pages) Console.WriteLine($"  {p}");
        return 0;
    });
    wiki.Subcommands.Add(inspect);

    wiki.Subcommands.Add(BuildPageCommand());
    return wiki;
}

static Command BuildPageCommand()
{
    var page = new Command("page", "Add or show pages within a wiki.");

    // add
    var addWiki = new Argument<string>("wiki") { Description = "Wiki name." };
    var addPath = new Argument<string>("path") { Description = "Page path, e.g. entities/acme-corp.md." };
    var title = new Option<string>("--title") { Description = "Page title.", Required = true };
    var type = new Option<PageType>("--type") { DefaultValueFactory = _ => PageType.Summary };
    var tags = new Option<string[]>("--tag") { Description = "Repeatable.", AllowMultipleArgumentsPerToken = true };
    var sources = new Option<string[]>("--source") { Description = "Repeatable.", AllowMultipleArgumentsPerToken = true };
    var body = new Option<string>("--body") { Description = "Markdown body (or use --body-file)." };
    var bodyFile = new Option<FileInfo>("--body-file") { Description = "Read body from a file." };
    var add = new Command("add", "Write a page with frontmatter.");
    add.Arguments.Add(addWiki); add.Arguments.Add(addPath);
    add.Options.Add(title); add.Options.Add(type); add.Options.Add(tags);
    add.Options.Add(sources); add.Options.Add(body); add.Options.Add(bodyFile);
    add.SetAction(async (pr, ct) =>
    {
        await using var provider = BuildProvider();
        var repo = provider.GetRequiredService<IWikiRepository>();
        var file = pr.GetValue(bodyFile);
        var content = file is not null
            ? await File.ReadAllTextAsync(file.FullName, ct)
            : pr.GetValue(body) ?? string.Empty;
        var p = new WikiPage
        {
            Title = pr.GetValue(title)!,
            Type = pr.GetValue(type),
            Content = content,
            Tags = pr.GetValue(tags) ?? [],
            Sources = pr.GetValue(sources) ?? [],
        };
        await repo.WritePageAsync(pr.GetValue(addWiki)!, pr.GetValue(addPath)!, p, ct);
        Console.WriteLine($"Wrote {pr.GetValue(addPath)}.");
        return 0;
    });
    page.Subcommands.Add(add);

    // show
    var showWiki = new Argument<string>("wiki") { Description = "Wiki name." };
    var showPath = new Argument<string>("path") { Description = "Page path." };
    var show = new Command("show", "Print a page and resolve its links.");
    show.Arguments.Add(showWiki); show.Arguments.Add(showPath);
    show.SetAction(async (pr, ct) =>
    {
        await using var provider = BuildProvider();
        var repo = provider.GetRequiredService<IWikiRepository>();
        var name = pr.GetValue(showWiki)!; var path = pr.GetValue(showPath)!;
        var p = await repo.ReadPageAsync(name, path, ct);
        Console.WriteLine($"# {p.Title} [{p.Type}]  tags=[{string.Join(",", p.Tags)}]");
        Console.WriteLine(p.Content);
        var report = await repo.ResolveLinksAsync(name, path, ct);
        Console.WriteLine($"\nLinks ({report.Links.Count}, {report.Broken.Count} broken):");
        foreach (var l in report.Links)
        {
            Console.WriteLine($"  [{(l.Exists ? "ok" : "BROKEN")}] {l.Reference.Target} -> {l.ResolvedPath ?? "(unresolved)"}");
        }
        return 0;
    });
    page.Subcommands.Add(show);

    return page;
}
