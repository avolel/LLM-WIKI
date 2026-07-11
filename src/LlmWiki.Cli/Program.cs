using System.CommandLine;
using LlmWiki.Application.Diagnostics;
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using LlmWiki.Infrastructure;
using LlmWiki.Shared.Configuration;
using LlmWiki.Agents;
using LlmWiki.Application.Indexing;
using LlmWiki.Application.Ingestion;
using LlmWiki.Application.Query;
using Microsoft.Extensions.DependencyInjection;

var root = new RootCommand("LLM Wiki CLI — local operations and diagnostics.");

var doctor = new Command("doctor", "Run the Phase 0 connectivity checks and report pass/fail.");
doctor.SetAction((_, ct) => RunDoctorAsync(ct));

root.Subcommands.Add(doctor);
root.Subcommands.Add(BuildWikiCommand());
root.Subcommands.Add(BuildIngestCommand());
root.Subcommands.Add(BuildSearchCommand());
root.Subcommands.Add(BuildReindexCommand());
root.Subcommands.Add(BuildAskCommand());

return await root.Parse(args).InvokeAsync();

// Helper to build a DI provider for each command execution. 
//In a real app, you'd likely want to optimize this (e.g. reuse providers, or at least cache singletons), but for this CLI it's simple and fast enough.
static ServiceProvider BuildProvider()
{
    var services = new ServiceCollection();
    services.AddLlmWikiInfrastructure(LlmWikiConfiguration.Build());
    services.AddLlmWikiAgents();
    return services.BuildServiceProvider();
}

static Command BuildIngestCommand()
{
    var wikiArg = new Argument<string>("wiki") { Description = "Target wiki name." };
    var fileArg = new Argument<FileInfo>("file") { Description = "Source file to ingest (markdown/text)." };
    var ingest = new Command("ingest", "Ingest a source into a wiki: copies it into raw/ then builds pages.");

    ingest.Arguments.Add(wikiArg);
    ingest.Arguments.Add(fileArg);

    ingest.SetAction(async (pr, ct) =>
    {
        await using var provider = BuildProvider();
        var repo = provider.GetRequiredService<IWikiRepository>();
        var files = provider.GetRequiredService<IWikiFileStore>();
        var service = provider.GetRequiredService<IIngestionService>();

        var wikiName = pr.GetValue(wikiArg)!;
        if (!await repo.WikiExistsAsync(wikiName, ct))
        {
            await Console.Error.WriteLineAsync($"Wiki '{wikiName}' not found.");
            return 1;
        }

        var file = pr.GetValue(fileArg)!;
        var content = await File.ReadAllTextAsync(file.FullName, ct);

        // Place the source under the immutable raw/ dir (write-once; never overwrite).
        var rawPath = $"{wikiName}/raw/{file.Name}";
        if (!await files.ExistsAsync(rawPath, ct))
        {
            await files.WriteAsync(rawPath, content, ct);
        }

        var report = await service.IngestAsync(wikiName, $"raw/{file.Name}", content, ct);

        Console.WriteLine($"Ingested {file.Name} into '{wikiName}':");
        foreach (var o in report.Outcomes)
            Console.WriteLine($"  [{o.Change,-11}] {o.RelativePath}{(o.Detail is null ? "" : $" — {o.Detail}")}");
        foreach (var c in report.Contradictions)
            Console.WriteLine($"  [contradiction] {c.PageRelativePath}: {c.Description}");
        foreach (var g in report.Gaps)
            Console.WriteLine($"  [gap] {g.Subject}: {g.Detail}");
        Console.WriteLine(report.HasFailures ? "Completed with failures." : "Done.");
        return report.HasFailures ? 1 : 0;
    });
    return ingest;
}

// Phase 4: hybrid search — embed the query, then rank pages by fused vector + Oracle Text score.
static Command BuildSearchCommand()
{
    var wikiArg = new Argument<string>("wiki") { Description = "Wiki to search." };
    var queryArg = new Argument<string>("query") { Description = "Search text (natural language or exact term)." };
    var topK = new Option<int>("--top-k") { Description = "Number of results.", DefaultValueFactory = _ => 5 };
    var type = new Option<PageType?>("--type") { Description = "Restrict to a page type (entity|concept|summary|overview)." };

    var search = new Command("search", "Hybrid (vector + full-text) search within a wiki.");

    search.Arguments.Add(wikiArg);
    search.Arguments.Add(queryArg);
    search.Options.Add(topK);
    search.Options.Add(type);

    search.SetAction(async (pr, ct) =>
    {
        await using var provider = BuildProvider();
        var repo = provider.GetRequiredService<IWikiRepository>();
        var embeddings = provider.GetRequiredService<IEmbeddingService>();
        var vectors = provider.GetRequiredService<IVectorStore>();

        var wikiName = pr.GetValue(wikiArg)!;
        if (!await repo.WikiExistsAsync(wikiName, ct))
        {
            await Console.Error.WriteLineAsync($"Wiki '{wikiName}' not found.");
            return 1;
        }

        var query = pr.GetValue(queryArg)!;
        var embedding = await embeddings.EmbedAsync(query, ct);
        var hits = await vectors.SearchAsync(wikiName, query, embedding, pr.GetValue(topK), pr.GetValue(type), ct);

        if (hits.Count == 0) { Console.WriteLine("no matches"); return 0; }

        var i = 1;
        foreach (var h in hits)
        {
            Console.WriteLine($"{i++}. {h.RelativePath}  {h.Score:F4}  — {h.Title} [{h.Type}]");
        }
        return 0;
    });
    return search;
}

// Phase 4: backfill — embed every existing page of a wiki into Oracle (no LLM calls, no content edits).
static Command BuildReindexCommand()
{
    var wikiArg = new Argument<string>("wiki") { Description = "Wiki to (re)embed into the vector store." };
    var reindex = new Command("reindex", "Embed all existing pages of a wiki into Oracle (backfill the search index).");
    reindex.Arguments.Add(wikiArg);
    reindex.SetAction(async (pr, ct) =>
    {
        await using var provider = BuildProvider();
        var repo = provider.GetRequiredService<IWikiRepository>();
        var indexer = provider.GetRequiredService<IWikiIndexer>();

        var wikiName = pr.GetValue(wikiArg)!;
        if (!await repo.WikiExistsAsync(wikiName, ct))
        {
            await Console.Error.WriteLineAsync($"Wiki '{wikiName}' not found.");
            return 1;
        }

        var report = await indexer.ReindexAsync(wikiName, ct);
        Console.WriteLine($"Reindexed '{wikiName}': {report.Embedded} embedded, {report.Failed} failed.");
        foreach (var f in report.Failures)
            Console.WriteLine($"  [failed] {f.RelativePath} — {f.Error}");
        return report.HasFailures ? 1 : 0;
    });
    return reindex;
}

// Phase 5: query/synthesis. With a question, answer once and exit; without one, open a REPL that
// keeps conversation history in-process (BR-044) and can save a good answer back as a page (BR-045).
static Command BuildAskCommand()
{
    var wikiArg = new Argument<string>("wiki") { Description = "Wiki to ask." };
    var questionArg = new Argument<string?>("question")
    {
        Description = "Question to answer once. Omit to open an interactive REPL.",
        Arity = ArgumentArity.ZeroOrOne,
    };
    var topK = new Option<int>("--top-k") { Description = "Candidates to retrieve.", DefaultValueFactory = _ => 5 };
    var type = new Option<PageType?>("--type") { Description = "Restrict to a page type (entity|concept|summary|overview|answer)." };

    var ask = new Command("ask", "Ask a question and get a grounded, cited answer (REPL for follow-ups).");
    ask.Arguments.Add(wikiArg);
    ask.Arguments.Add(questionArg);
    ask.Options.Add(topK);
    ask.Options.Add(type);

    ask.SetAction(async (pr, ct) =>
    {
        await using var provider = BuildProvider();
        var repo = provider.GetRequiredService<IWikiRepository>();
        var svc = provider.GetRequiredService<IQueryService>();

        var wikiName = pr.GetValue(wikiArg)!;
        if (!await repo.WikiExistsAsync(wikiName, ct))
        {
            await Console.Error.WriteLineAsync($"Wiki '{wikiName}' not found.");
            return 1;
        }

        var options = new QueryOptions(pr.GetValue(topK), pr.GetValue(type));
        var single = pr.GetValue(questionArg);

        // One-shot mode: answer the supplied question and exit.
        if (!string.IsNullOrWhiteSpace(single))
        {
            var result = await svc.AnswerAsync(wikiName, single, [], options, ct);
            PrintResult(result);
            return 0;
        }

        // REPL mode: maintain history across turns; ':save' persists the last answer, ':quit' exits.
        Console.WriteLine($"Asking '{wikiName}'. Type a question, ':save' to save the last answer, ':quit' to exit.");
        var history = new List<ConversationTurn>();
        QueryResult? last = null;

        while (true)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line is null || line.Trim() is ":quit" or ":q") break;
            var input = line.Trim();
            if (input.Length == 0) continue;

            if (input is ":save")
            {
                if (last is null) { Console.WriteLine("Nothing to save yet."); continue; }
                var outcome = await svc.SaveAnswerAsync(wikiName, last, ct);
                Console.WriteLine(outcome.Change == PageChange.Failed
                    ? $"Save failed: {outcome.Detail}"
                    : $"Saved {outcome.RelativePath}{(outcome.Detail is null ? "" : $" (note: {outcome.Detail})")}");
                continue;
            }

            last = await svc.AnswerAsync(wikiName, input, history, options, ct);
            PrintResult(last);
            history.Add(new ConversationTurn(input, last.Answer));
        }
        return 0;
    });
    return ask;
}

// Shared rendering for a synthesised answer: the markdown body, an honest-gap banner (BR-042),
// then the resolvable citation list (BR-041).
static void PrintResult(QueryResult result)
{
    if (!result.Covered)
    {
        Console.WriteLine("⚠  not covered by this wiki");
    }
    Console.WriteLine();
    Console.WriteLine(result.Answer);
    Console.WriteLine();
    if (result.Citations.Count > 0)
    {
        Console.WriteLine("Sources:");
        foreach (var c in result.Citations)
        {
            Console.WriteLine($"  {c.RelativePath}  — {c.Title} [{c.Type}]");
        }
    }
    else
    {
        Console.WriteLine("Sources: (none)");
    }
}

// connectivity checks and diagnostics.
//This is a simple smoke test to verify that the environment is correctly configured before attempting any operations that could cause data loss (like creating wikis or writing pages).
static async Task<int> RunDoctorAsync(CancellationToken cancellationToken)
{
    await using var provider = BuildProvider();
    var diagnostics = provider.GetRequiredService<IDiagnosticsService>();
    var report = await diagnostics.RunAsync(cancellationToken);

    Console.WriteLine("LLM Wiki — diagnostics");
    Console.WriteLine("------------------------------");
    foreach (var check in report.Checks)
    {
        Console.WriteLine($"[{(check.Passed ? "PASS" : "FAIL")}] {check.Name,-10} {check.Detail}");
    }
    Console.WriteLine();
    Console.WriteLine(report.AllPassed ? "All checks passed." : "One or more checks FAILED.");
    return report.AllPassed ? 0 : 1;
}

// wiki and page management commands.
static Command BuildWikiCommand()
{
    var wiki = new Command("wiki", "Create, list, and inspect wikis.");

    // Create WikiSchema, which includes the wiki name and link style (e.g. wikilinks vs markdown links). 
    // This will be stored in a SCHEMA.md file within the wiki directory and used to validate pages and resolve links.
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

    // list all wikis, showing their name, link style, and page count. 
    // This is a simple way to verify that the CLI can read existing wikis and their schemas.
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

    // inspect a wiki by name, showing its schema details and a list of its pages.
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

    // show a page's content and metadata, and resolve its links to verify that the link style is working correctly.
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
