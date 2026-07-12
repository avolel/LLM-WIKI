# Plan: Delete a wiki / project (CLI + frontend)

## Context

Today the system can create, list, select, ingest, search, and query wikis/projects, but there is **no way to delete one**. A `grep` for delete/remove/drop across Infrastructure confirms no deletion capability exists at any layer. Users who create a throwaway or mis-named project are stuck with orphan directories on disk, dangling embeddings in Oracle, and a growing registry.

In this codebase **a project *is* the per-wiki tenant** (CLAUDE.md: "a project **is** the existing per-wiki tenant"), so "delete a wiki" and "delete a project" are one operation. A complete, truthful teardown must remove all four artifacts of a wiki:

1. The wiki directory on disk (`{WIKI_ROOT}/{name}` — pages, `index.md`/`log.md`, and the `raw/` source copies).
2. All Oracle embeddings for that wiki (`wiki_page` rows where `wiki_name = name`).
3. The Oracle registry row (`wiki_project` where `name = name`).
4. The host-local active-project pointer, if it points at the deleted wiki.

**Decisions locked with the user:** full teardown incl. `raw/`; confirmation on both surfaces (CLI `y/N` prompt bypassable with `--yes`, frontend `window.confirm`); CLI command in **both** `project` and `wiki` groups (both run the same teardown).

## Design

No new orchestration service — matching the existing convention where `create` coordinates the ports **inline** at each composition root (CLI `BuildProjectCommand`, `ProjectController.CreateAsync`). We add four small **delete primitives** to existing ports and coordinate them at the call sites. Oracle steps are **best-effort** (try/catch, warn) so a DB outage never blocks removing the canonical on-disk wiki — mirrors the create path's NFR-06 handling. No new DI registration is needed (methods added to already-registered singletons).

Teardown order at each call site: guard `WikiExistsAsync` → delete disk dir (canonical, may throw a real IO error) → best-effort purge embeddings → best-effort delete registry row → (CLI only) clear pointer if it was active.

Per the existing rule that `select`/the active pointer is CLI-local and has no API endpoint, the **DELETE API does not touch `ICurrentProjectStore`**; the frontend clears its own `localStorage` active pointer via the existing `setActiveProject(null)`.

---

## Backend changes

### 1. `IWikiFileStore` — disk delete primitive
`src/LlmWiki.Application/Ports/IWikiFileStore.cs` — add:
```csharp
/// <summary>Recursively delete everything under a relative path (a wiki directory). No-op if absent.</summary>
Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default);
```

`src/LlmWiki.Infrastructure/FileStore/FileSystemWikiFileStore.cs` — implement, reusing the existing `Resolve` escape-guard:
```csharp
public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
{
    var full = Resolve(relativePath);
    if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    else if (File.Exists(full)) File.Delete(full);
    return Task.CompletedTask;
}
```

### 2. `IWikiRepository` — wiki-level delete
`src/LlmWiki.Application/Ports/IWikiRepository.cs` — add:
```csharp
Task DeleteWikiAsync(string wikiName, CancellationToken cancellationToken = default);
```
`src/LlmWiki.Infrastructure/FileStore/FileSystemWikiRepository.cs` — implement (delegates to the file store, deleting the whole `{wikiName}` subtree):
```csharp
public Task DeleteWikiAsync(string wikiName, CancellationToken cancellationToken = default) =>
    files.DeleteAsync(wikiName, cancellationToken);
```

### 3. `IVectorStore` — purge a wiki's embeddings
`src/LlmWiki.Application/Ports/IVectorStore.cs` — add:
```csharp
/// <summary>Delete every embedded page belonging to one wiki (NFR-10 isolation predicate).</summary>
Task DeleteWikiAsync(string wikiName, CancellationToken cancellationToken = default);
```
`src/LlmWiki.Infrastructure/VectorStore/OracleVectorStore.cs` — implement using the established `OpenAsync` guard + `BindByName` + `wiki_name` predicate:
```csharp
public async Task DeleteWikiAsync(string wikiName, CancellationToken ct = default)
{
    await using var conn = await OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM wiki_page WHERE wiki_name = :wiki";
    cmd.BindByName = true;
    cmd.Parameters.Add(":wiki", wikiName);
    await cmd.ExecuteNonQueryAsync(ct);
}
```

### 4. `IProjectRepository` — delete the registry row
`src/LlmWiki.Application/Ports/IProjectRepository.cs` — add:
```csharp
Task DeleteAsync(string name, CancellationToken cancellationToken = default);
```
`src/LlmWiki.Infrastructure/Persistence/OracleProjectRepository.cs` — implement (same shape as `RegisterAsync`, but `DELETE`):
```csharp
public async Task DeleteAsync(string name, CancellationToken ct = default)
{
    await using var conn = await OpenAsync(ct);
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "DELETE FROM wiki_project WHERE name = :name";
    cmd.BindByName = true;
    cmd.Parameters.Add(":name", name);
    await cmd.ExecuteNonQueryAsync(ct);
}
```

### 5. `ICurrentProjectStore` — clear the pointer (CLI-local)
`src/LlmWiki.Application/Ports/ICurrentProjectStore.cs` — add:
```csharp
/// <summary>Clear the active-project pointer (e.g. its wiki was deleted). Safe if none is set.</summary>
Task ClearAsync(CancellationToken cancellationToken = default);
```
`src/LlmWiki.Infrastructure/FileStore/FileCurrentProjectStore.cs` — implement (`GetAsync` already tolerates a missing file):
```csharp
public Task ClearAsync(CancellationToken ct = default)
{
    if (File.Exists(_path)) File.Delete(_path);
    return Task.CompletedTask;
}
```

### 6. API — `DELETE /projects/{name}`
`src/LlmWiki.Api/Controllers/ProjectController.cs` — add `IVectorStore vectors` to the primary constructor and a delete action. CORS already allows any method; no `Program.cs` change.
```csharp
[ApiController]
[Route("projects")]
public sealed class ProjectController(
    IProjectRepository projects, IWikiRepository wiki, IVectorStore vectors) : ControllerBase
{
    // ... existing List/Get/Create ...

    /// <summary>Delete a project/wiki: disk tree + embeddings + registry row (BR-050). 404 if absent.</summary>
    [HttpDelete("{name}")]
    public async Task<IActionResult> DeleteAsync(string name, CancellationToken ct)
    {
        if (!await wiki.WikiExistsAsync(name, ct)) return NotFound();
        await wiki.DeleteWikiAsync(name, ct);                 // canonical
        try { await vectors.DeleteWikiAsync(name, ct); } catch { /* best-effort (NFR-06) */ }
        try { await projects.DeleteAsync(name, ct); } catch { /* best-effort */ }
        return NoContent();
    }
}
```
(The API deliberately does **not** clear `ICurrentProjectStore` — it is host/CLI-local, consistent with `select` having no endpoint.)

---

## CLI changes — `src/LlmWiki.Cli/Program.cs`

Add one shared static helper, then wire a `delete` subcommand into **both** `BuildProjectCommand()` and `BuildWikiCommand()`.

**Shared helper** (near `ResolveWikiAsync`):
```csharp
static async Task<int> DeleteWikiAsync(string name, bool yes, CancellationToken ct)
{
    await using var provider = BuildProvider();
    var repo = provider.GetRequiredService<IWikiRepository>();
    var vectors = provider.GetRequiredService<IVectorStore>();
    var projects = provider.GetRequiredService<IProjectRepository>();
    var current = provider.GetRequiredService<ICurrentProjectStore>();

    if (!await repo.WikiExistsAsync(name, ct))
    {
        await Console.Error.WriteLineAsync($"Wiki '{name}' not found.");
        return 1;
    }
    if (!yes)
    {
        Console.Write($"Delete wiki '{name}' and ALL its data (pages, raw sources, embeddings)? [y/N] ");
        var reply = (Console.ReadLine() ?? "").Trim();
        if (!reply.Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Aborted.");
            return 0;
        }
    }

    await repo.DeleteWikiAsync(name, ct);                     // canonical
    try { await vectors.DeleteWikiAsync(name, ct); }
    catch (Exception ex) { await Console.Error.WriteLineAsync($"warning: could not purge embeddings — {ex.Message}"); }
    try { await projects.DeleteAsync(name, ct); }
    catch (Exception ex) { await Console.Error.WriteLineAsync($"warning: could not remove registry row — {ex.Message}"); }
    if (await current.GetAsync(ct) == name) await current.ClearAsync(ct);

    Console.WriteLine($"Deleted wiki '{name}'.");
    return 0;
}
```

**In `BuildProjectCommand()`** (after `select`):
```csharp
var delName = new Argument<string>("name") { Description = "Project to delete." };
var delYes = new Option<bool>("--yes", "-y") { Description = "Skip the confirmation prompt." };
var delete = new Command("delete", "Delete a project/wiki and all its data (irreversible).");
delete.Arguments.Add(delName);
delete.Options.Add(delYes);
delete.SetAction((pr, ct) => DeleteWikiAsync(pr.GetValue(delName)!, pr.GetValue(delYes), ct));
project.Subcommands.Add(delete);
```

**In `BuildWikiCommand()`** — an identical `delete` subcommand (own `Argument`/`Option` instances, same `SetAction` → `DeleteWikiAsync`), described as "Delete a wiki and all its data (irreversible)."

---

## Frontend changes

### 1. `app/src/api/client.ts` — DELETE helper + typed call
Add a `deleteJson` helper (handles 204 No Content — no body to parse) and `deleteProject`:
```ts
async function deleteJson(path: string): Promise<void> {
  const response = await fetch(`${API_BASE_URL}${path}`, { method: 'DELETE' });
  if (!response.ok) {
    throw new Error(`Request to ${path} failed: ${response.status} ${response.statusText}`);
  }
}

// ---- projects ----
export const deleteProject = (name: string) =>
  deleteJson(`/projects/${encodeURIComponent(name)}`);
```

### 2. `app/src/screens/ProjectsScreen.tsx` — delete button per card
Mirror the existing `create` handler shape (busy flag + try/catch/finally + `await load()`):
```ts
const [deleting, setDeleting] = useState<string | null>(null);

const remove = async (name: string) => {
  if (deleting) return;
  if (typeof window !== 'undefined' &&
      !window.confirm(`Delete "${name}" and all its data? This cannot be undone.`)) return;
  setDeleting(name);
  setCreateError(null);
  try {
    await deleteProject(name);
    if (name === activeProject) setActiveProject(null);   // clears localStorage (existing behaviour)
    await load();
  } catch (err) {
    setCreateError(err instanceof Error ? err.message : String(err));
  } finally {
    setDeleting(null);
  }
};
```
Render a small "Delete" `Pressable` inside the existing `styles.cardHeader` (right-aligned; `cardHeader` already uses `justifyContent: 'space-between'`). Stop the tap from also selecting the card — the delete `Pressable` handles its own `onPress` and the outer card's `onPress` still selects; guard the delete button with `onPress={(e) => { e.stopPropagation?.(); void remove(p.name); }}`. Import `deleteProject` from `../api/client`. Add `deleteBtn`/`deleteText` styles (use `theme.colors.fail`).

`BrowseScreen`/`ChatScreen` already handle `activeProject === null` gracefully, so no changes there.

---

## Tests
- `tests/LlmWiki.Api.Tests` — add a test mirroring the project create/get tests: create a wiki, `DELETE /projects/{name}` → `204`; then `GET /projects/{name}` → `404` and `WikiExistsAsync` is false. Delete of a non-existent name → `404`.
- `tests/LlmWiki.Infrastructure.Tests` (or existing file-store tests) — `FileSystemWikiFileStore.DeleteAsync` removes a directory tree and is a no-op on a missing path; `Resolve` escape-guard still rejects `../` traversal.
- Oracle-backed `DeleteAsync`/`DeleteWikiAsync` follow the existing (integration-style) Oracle test conventions if present; otherwise covered manually (verification below).

---

## Verification (end-to-end)
1. Build + unit tests: `dotnet build LlmWiki.slnx` and `dotnet test LlmWiki.slnx`; `cd app && npm run lint`.
2. With Oracle + Ollama up (`cd docker && docker compose up -d`):
   - `dotnet run --project src/LlmWiki.Cli -- project create scratch` then `ingest scratch ./docs/sample-source.md` (creates disk pages + embeddings + registry row + active pointer).
   - `dotnet run --project src/LlmWiki.Cli -- project delete scratch` → confirm prompt → `y`. Then check: `{WIKI_ROOT}/scratch` gone; `project list` no longer shows it; `.current-project` cleared; Oracle `SELECT COUNT(*) FROM wiki_page WHERE wiki_name='scratch'` and `wiki_project WHERE name='scratch'` both `0`.
   - Re-test the `--yes` bypass and the `wiki delete` variant.
3. Frontend: `cd app && npm run web` → Projects tab → create a project, then click **Delete** → confirm dialog → the card disappears, the active-project highlight clears if it was active, and Browse/Chat show the "select a project" empty state.
4. API directly: `curl -i -X DELETE http://localhost:5080/projects/scratch` → `204`; repeat → `404`.
