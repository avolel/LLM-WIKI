# Plan — Phase 8: Interfaces (React Native client + the API surface it needs)

## Context

Phases 0–7 built the entire backend: a file-backed wiki, agentic ingestion, the index/log journal, Oracle hybrid retrieval, grounded/cited query synthesis, the Oracle project registry, and the lint/health-check workflow — all exposed over a CLI (`doctor`/`wiki`/`project`/`ingest`/`search`/`reindex`/`ask`/`lint`) and a partial HTTP surface (`GET /health`, `GET /diagnostics`, `POST /query`, `GET`/`POST /projects`, `POST /lint` + `/lint/apply`). What is **missing** is the product's front door: a human-usable client. The `app/` Expo project today is only the **Phase 0 connectivity slice** — a diagnostics `HomeScreen`, a typed `getHealth`/`getDiagnostics` client, and empty `screens/state/navigation/theme` stubs whose comments literally say "wired in Phase 8." The BRD's CLI `ingest` (BR-075) already shipped in Phase 2, so Phase 8's remaining work is the **React Native client** plus the small amount of HTTP surface it needs to browse and open pages.

Phase 8 (BR-070…BR-075, NFR-08, NFR-09) delivers a React Native (Expo) chat/browse client for **web** and adds three focused API pieces so the client can function: a **browse endpoint** (page tree by category), a **single-page read endpoint** (so citations open a real page), and a **save-answer endpoint** (persist a good answer without re-synthesising). It also enables **CORS** (the Expo web build is a browser origin and is otherwise blocked) and **string enum serialization** (so the client sees `"Entity"` not `1`). No new Oracle table, no new agent workflow — this phase is glue and UI over the existing ports.

**Decisions locked with the user:**
1. **Query progress = loading state only.** `POST /query` stays a single blocking JSON response; the client shows a spinner (BR-074, NFR-08). True SSE/token streaming (NFR-05's streaming clause) is **deferred** to "Going Further" — it would require threading `IAsyncEnumerable` through `IQueryService` and the `IChatService` port, a cross-layer change out of proportion to the UI goal.
2. **Web-first.** Build and verify against the web target (matches the existing CI `expo export --platform web` gate). Code stays RN-portable so native still runs, but no device-specific verification this phase.
3. **Minimal / low-dependency UI.** A custom tab switcher (`useState` over screens) instead of React Navigation, and a small custom markdown renderer for the answer/citation/page subset — avoiding compat risk on the bleeding-edge stack (Expo SDK 56, RN 0.85.3, React 19.2.3). **No heavy libraries added.**
4. **Query/browse/projects only.** The client covers chat (BR-070/072), clickable citations that open a page (BR-071), the wiki browser (BR-073), project list/create/select, and save-answer-as-page (BR-045 UI). **Ingestion stays CLI-only** (BR-075, already shipped) — no `POST /ingest` this phase.

**Design principles held:** the client talks only to the existing HTTP contract behind one typed `src/api/client.ts` (NFR-07 stays intact — SK/Oracle never leak toward the UI); the API additions reuse ports that already exist (`IWikiRepository.ListPagesAsync`/`ReadPageAsync`, `IQueryService.SaveAnswerAsync`) — no new orchestration; CORS is permissive because the target is a single local user (NFR-04); every network call has an explicit loading + error state (NFR-08); one Expo codebase renders markdown consistently (NFR-09).

---

## Key facts grounding the design

- **The client is a Phase-0 scaffold with every Phase-8 folder already present as an intentional stub.** [app/src/api/client.ts](../../app/src/api/client.ts) has `API_BASE_URL` (from `EXPO_PUBLIC_API_BASE_URL`, default `http://localhost:5080`) and a private `getJson<T>` helper — the exact pattern to extend. [app/App.tsx](../../app/App.tsx) renders `<HomeScreen/>` inside `SafeAreaView`; [app/src/screens/HomeScreen.tsx](../../app/src/screens/HomeScreen.tsx) is the one real screen (diagnostics via `useDiagnostics`). `ChatScreen`/`BrowseScreen`/`ProjectsScreen` are "coming later" placeholders; [app/src/state/index.ts](../../app/src/state/index.ts) is `export {}`; [app/src/navigation/index.ts](../../app/src/navigation/index.ts) is a `ROUTES` const with no navigator; [app/src/theme/index.ts](../../app/src/theme/index.ts) is minimal tokens whose comment says "Expanded into a full theme in Phase 8."
- **No navigation/markdown/state libraries are installed.** [app/package.json](../../app/package.json) has only `expo`, `react`, `react-dom`, `react-native`, `react-native-web`, `expo-status-bar`, `@expo/metro-runtime`. Scripts: `web` (`expo start --web`), `lint` (`tsc --noEmit`), `export:web` (`expo export --platform web`). The CI `app` job (Node 24) runs `npm ci` → `npm run lint` → `npm run export:web` ([.github/workflows/ci.yml](../../.github/workflows/ci.yml)); Phase 8 code must keep both green. The minimal-dependency decision means we add **no** new runtime deps.
- **The chat contract is client-driven and non-streaming.** [QueryController.cs](../../src/LlmWiki.Api/Controllers/QueryController.cs) `POST /query` takes `QueryRequest(Wiki, Question, History?, TopK?, Type?, Save?)` and returns `QueryResult(WikiName, Question, Answer, Covered, Citations, SuggestedTitle)` where `Citation(RelativePath, Title, Type)`. The **server holds no conversation state** — the client resends full `History` each turn (BR-044). `SaveAnswerAsync` already exists on `IQueryService`; the controller currently saves only by re-running synthesis with `Save=true`, which we improve with a dedicated save endpoint to avoid re-synthesis.
- **The ports for browse already exist — only controllers are missing.** [IWikiRepository.cs](../../src/LlmWiki.Application/Ports/IWikiRepository.cs) exposes `ListPagesAsync(wiki) → IReadOnlyList<string>` (relative paths) and `ReadPageAsync(wiki, relativePath) → WikiPage` (Title, Type, Content markdown, Tags, Sources, dates). `Citation.RelativePath` is exactly the key `ReadPageAsync` takes, so opening a citation (BR-071) is a direct call once an endpoint exists. The CLI `wiki inspect`/`wiki page show` already use these.
- **Enums serialize as integers today and there is no CORS.** [Program.cs](../../src/LlmWiki.Api/Program.cs) sets no `JsonSerializerOptions` and registers no CORS. `PageType`/`LintSeverity`/`LintCategory`/`LinkStyle` therefore serialize as ints, and a browser (Expo web) `fetch` is blocked with no `Access-Control-Allow-Origin`. Both are fixed centrally in `Program.cs`.
- **Lint report shape (already served) for a possible Health tab:** [LintReport.cs](../../src/LlmWiki.Application/Linting/LintReport.cs) — `LintFinding(Severity, Category, Summary, Pages, SuggestedAction?, Fix?)`, `SuggestedFix(RelativePath, Title, Type, Body)`; `POST /lint` + `POST /lint/apply` already exist ([LintController.cs](../../src/LlmWiki.Api/Controllers/LintController.cs)). Surfacing lint in the UI is **out of scope** this phase (see below) but the contract is ready if desired.

---

## Files to change

### API (small: reuse existing ports)

#### 1. `src/LlmWiki.Api/Program.cs` (UPDATE — add CORS + string-enum JSON)

Add a permissive default CORS policy and a string-enum converter for both the controller pipeline and the minimal-API JSON. Insert into service registration:

```csharp
using System.Text.Json.Serialization;
// ...
builder.Services.AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Minimal APIs (/health, /diagnostics) use a separate serializer — keep them consistent.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Single local user (NFR-04): allow the Expo web origin to call the API.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
```

And in the middleware pipeline, before `app.MapControllers()`:

```csharp
app.UseCors();
```

> Note for implementation: after enabling string enums, run the existing `LlmWiki.Api.Tests` — if any assert integer enum values in response JSON they need updating to the string name. `QueryRequest.Type` (a `PageType?`) posted by tests round-trips through the same options, so typed requests are unaffected.

#### 2. `src/LlmWiki.Api/Controllers/WikiController.cs` (NEW — browse tree + single-page read)

Backs BR-073 (page tree by category) and BR-071 (open a cited page). Pure passthrough to `IWikiRepository`; groups relative paths by their top-level typed directory. The single-page route is a catch-all so the `/`-containing relative path binds cleanly.

```csharp
using LlmWiki.Application.Ports;
using LlmWiki.Domain;
using Microsoft.AspNetCore.Mvc;

namespace LlmWiki.Api.Controllers;

/// <summary>
/// Read-only browse surface for the Phase 8 client: the wiki page tree grouped by category
/// (BR-073) and a single page's full content so a citation can be opened (BR-071).
/// Thin passthrough to <see cref="IWikiRepository"/> — no new orchestration.
/// </summary>
[ApiController]
[Route("wikis")]
public sealed class WikiController(IWikiRepository repo) : ControllerBase
{
    [HttpGet("{wiki}/pages")]
    public async Task<IActionResult> TreeAsync(string wiki, CancellationToken ct)
    {
        if (!await repo.WikiExistsAsync(wiki, ct)) return NotFound();
        var paths = await repo.ListPagesAsync(wiki, ct);
        var categories = paths
            .GroupBy(p => p.Contains('/') ? p[..p.IndexOf('/')] : "root")
            .OrderBy(g => g.Key)
            .Select(g => new PageCategory(
                g.Key,
                g.OrderBy(p => p)
                 .Select(p => new PageRef(p, SlugOf(p)))
                 .ToList()))
            .ToList();
        return Ok(new WikiTree(wiki, categories));
    }

    // Catch-all: relativePath is e.g. "entities/acme.md".
    [HttpGet("{wiki}/pages/{**relativePath}")]
    public async Task<IActionResult> PageAsync(string wiki, string relativePath, CancellationToken ct)
    {
        if (!await repo.WikiExistsAsync(wiki, ct)) return NotFound();
        try
        {
            var page = await repo.ReadPageAsync(wiki, relativePath, ct);
            return Ok(page);
        }
        catch (FileNotFoundException) { return NotFound(); }
        catch (DirectoryNotFoundException) { return NotFound(); }
    }

    private static string SlugOf(string path)
    {
        var name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;
        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;
    }
}

public record PageRef(string RelativePath, string Slug);
public record PageCategory(string Name, IReadOnlyList<PageRef> Pages);
public record WikiTree(string WikiName, IReadOnlyList<PageCategory> Categories);
```

> Confirm during implementation which exception `FileSystemWikiRepository.ReadPageAsync` throws for a missing page and catch exactly that (the two above are the likely candidates).

#### 3. `src/LlmWiki.Api/Controllers/QueryController.cs` (UPDATE — add a save endpoint)

Add a dedicated save route so the client persists an already-synthesised answer without paying for a second synthesis. Reuses `IQueryService.SaveAnswerAsync`.

```csharp
[HttpPost("save")]
public async Task<IActionResult> SaveAsync(SaveAnswerRequest req, CancellationToken ct)
{
    if (!await repo.WikiExistsAsync(req.Wiki, ct)) return NotFound();
    if (!req.Result.Covered) return BadRequest("won't save an uncovered (gap) answer");
    var outcome = await svc.SaveAnswerAsync(req.Wiki, req.Result, ct);
    return Ok(outcome);
}
// ...alongside the existing QueryRequest record:
public record SaveAnswerRequest(string Wiki, QueryResult Result);
```

> The existing `Save` flag on `QueryRequest` stays as-is for CLI/back-compat; the client uses `/query/save`.

### Client (`app/` — the bulk of the phase)

#### 4. `app/src/api/client.ts` (UPDATE — full typed surface)

Extend the existing file with all types + calls the client needs, mirroring the current `getJson` style and adding a `postJson` helper. Enums are string unions matching the C# names (now that the API emits string enums).

```typescript
export const API_BASE_URL =
  process.env.EXPO_PUBLIC_API_BASE_URL ?? 'http://localhost:5080';

// ---- shared enums (string, matching the API) ----
export type PageType = 'Summary' | 'Entity' | 'Concept' | 'Overview' | 'Answer';
export type LinkStyle = 'Wikilink' | 'MarkdownLink';

// ---- diagnostics (existing) ----
export interface HealthResponse { status: string; }
export interface DiagnosticCheck { name: string; passed: boolean; detail: string; }
export interface DiagnosticsReport { checks: DiagnosticCheck[]; allPassed: boolean; }

// ---- projects ----
export interface ProjectInfo {
  name: string; createdAt: string; lastIngestAt: string | null;
  pageCount: number; sourceCount: number;
}

// ---- query ----
export interface Citation { relativePath: string; title: string; type: PageType; }
export interface ConversationTurn { question: string; answer: string; }
export interface QueryResult {
  wikiName: string; question: string; answer: string; covered: boolean;
  citations: Citation[]; suggestedTitle: string;
}
export interface PageOutcome { relativePath: string; title: string; change: string; detail: string | null; }

// ---- browse ----
export interface PageRef { relativePath: string; slug: string; }
export interface PageCategory { name: string; pages: PageRef[]; }
export interface WikiTree { wikiName: string; categories: PageCategory[]; }
export interface WikiPage {
  title: string; type: PageType; content: string;
  tags: string[]; sources: string[]; createdAt: string; updatedAt: string;
}

async function getJson<T>(path: string): Promise<T> {
  const res = await fetch(`${API_BASE_URL}${path}`);
  if (!res.ok && res.status !== 503) {
    throw new Error(`GET ${path} failed: ${res.status} ${res.statusText}`);
  }
  return (await res.json()) as T;
}

async function postJson<T>(path: string, body: unknown): Promise<T> {
  const res = await fetch(`${API_BASE_URL}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`POST ${path} failed: ${res.status} ${res.statusText}`);
  return (await res.json()) as T;
}

export const getHealth = () => getJson<HealthResponse>('/health');
export const getDiagnostics = () => getJson<DiagnosticsReport>('/diagnostics');

export const listProjects = () => getJson<ProjectInfo[]>('/projects');
export const createProject = (name: string, linkStyle?: LinkStyle) =>
  postJson<ProjectInfo>('/projects', { name, linkStyle });

export const postQuery = (
  wiki: string, question: string, history: ConversationTurn[], topK = 5,
) => postJson<QueryResult>('/query', { wiki, question, history, topK });

export const saveAnswer = (wiki: string, result: QueryResult) =>
  postJson<PageOutcome>('/query/save', { wiki, result });

export const getWikiTree = (wiki: string) =>
  getJson<WikiTree>(`/wikis/${encodeURIComponent(wiki)}/pages`);

export const getPage = (wiki: string, relativePath: string) =>
  getJson<WikiPage>(
    `/wikis/${encodeURIComponent(wiki)}/pages/` +
    relativePath.split('/').map(encodeURIComponent).join('/'),
  );
```

#### 5. `app/src/state/index.ts` (UPDATE — app context: active project)

Replace the empty stub with a small Context holding the active project (BR-050 "select the active project on startup"), persisted to `localStorage` on web when available.

```tsx
import React, { createContext, useContext, useMemo, useState } from 'react';

const KEY = 'llmwiki.activeProject';
const load = () => {
  try { return typeof localStorage !== 'undefined' ? localStorage.getItem(KEY) : null; }
  catch { return null; }
};
const save = (v: string | null) => {
  try { if (typeof localStorage !== 'undefined') {
    if (v) localStorage.setItem(KEY, v); else localStorage.removeItem(KEY);
  } } catch { /* non-web: in-memory only */ }
};

interface AppState { activeProject: string | null; setActiveProject: (p: string | null) => void; }
const AppContext = createContext<AppState | null>(null);

export function AppProvider({ children }: { children: React.ReactNode }) {
  const [activeProject, setActiveProjectRaw] = useState<string | null>(load);
  const value = useMemo<AppState>(() => ({
    activeProject,
    setActiveProject: (p) => { setActiveProjectRaw(p); save(p); },
  }), [activeProject]);
  return <AppContext.Provider value={value}>{children}</AppContext.Provider>;
}

export function useApp(): AppState {
  const ctx = useContext(AppContext);
  if (!ctx) throw new Error('useApp must be used within AppProvider');
  return ctx;
}
```

> `state/index.ts` becomes `state/index.tsx` (it now has JSX). Update the filename accordingly.

#### 6. `app/src/theme/index.ts` (UPDATE — expand tokens)

Add radii, font sizes, and a couple of surface/user-bubble colors used by the chat/markdown components; keep the existing keys so `HomeScreen`/`CheckRow` are unaffected.

```typescript
export const theme = {
  colors: {
    background: '#ffffff', surface: '#f6f8fa', text: '#11181C', muted: '#687076',
    pass: '#1a7f37', fail: '#cf222e', accent: '#0a7ea4',
    border: '#e1e4e8', userBubble: '#0a7ea4', userText: '#ffffff', code: '#f0f1f3',
  },
  radius: { sm: 6, md: 10, lg: 16 },
  font: { sm: 13, md: 15, lg: 18, xl: 28 },
  spacing: (n: number) => n * 8,
} as const;
export type Theme = typeof theme;
```

#### 7. `app/src/navigation/index.ts` (UPDATE — tab list)

Keep `ROUTES`; add the ordered tab set and labels the custom `TabBar` iterates.

```typescript
export const ROUTES = { Chat: 'Chat', Browse: 'Browse', Projects: 'Projects', Status: 'Status' } as const;
export type RouteName = keyof typeof ROUTES;
export const TABS: { key: RouteName; label: string }[] = [
  { key: 'Chat', label: 'Chat' },
  { key: 'Browse', label: 'Browse' },
  { key: 'Projects', label: 'Projects' },
  { key: 'Status', label: 'Status' },
];
```

#### 8. `app/App.tsx` (UPDATE — provider + custom tab switcher)

Wrap everything in `AppProvider`, hold the active tab in `useState`, render the active screen above a `TabBar`. No navigation library.

```tsx
import { useState } from 'react';
import { SafeAreaView, StyleSheet, View } from 'react-native';
import { StatusBar } from 'expo-status-bar';
import { AppProvider } from './src/state';
import { TabBar } from './src/components/TabBar';
import { TABS, type RouteName } from './src/navigation';
import { ChatScreen } from './src/screens/ChatScreen';
import { BrowseScreen } from './src/screens/BrowseScreen';
import { ProjectsScreen } from './src/screens/ProjectsScreen';
import { HomeScreen } from './src/screens/HomeScreen';
import { theme } from './src/theme';

export default function App() {
  const [tab, setTab] = useState<RouteName>('Chat');
  return (
    <AppProvider>
      <SafeAreaView style={styles.container}>
        <View style={styles.body}>
          {tab === 'Chat' && <ChatScreen />}
          {tab === 'Browse' && <BrowseScreen />}
          {tab === 'Projects' && <ProjectsScreen />}
          {tab === 'Status' && <HomeScreen />}
        </View>
        <TabBar tabs={TABS} active={tab} onSelect={setTab} />
        <StatusBar style="auto" />
      </SafeAreaView>
    </AppProvider>
  );
}
const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: theme.colors.background },
  body: { flex: 1 },
});
```

#### 9. `app/src/components/TabBar.tsx` (NEW)

A bottom bar of `Pressable` tabs highlighting the active one. Full code (small): maps `tabs`, renders each label, calls `onSelect(key)`, styles the active tab with `theme.colors.accent`.

#### 10. `app/src/components/Markdown.tsx` (NEW — small custom renderer)

Renders the markdown subset the agent produces (BR-070 markdown answers, BR-043 table/list formats, NFR-09 consistent rendering) into RN primitives — **no external markdown library**. Support: `#`/`##`/`###` headings, paragraphs, `-`/`*` and numbered lists, fenced ```code``` blocks and inline `` `code` ``, `**bold**`/`*italic*`, `[text](path)` links, and simple `|`-delimited tables. Links call an optional `onLinkPress(href)` so citations/wikilinks can open a page. Structure: a line-based block parser (headings/lists/code-fences/tables/paragraphs) plus a small inline-span parser for bold/italic/code/links. Keep it pragmatic — unknown syntax falls back to plain `<Text>`. (Full implementation written at build time; ~150 lines.)

#### 11. `app/src/components/CitationChip.tsx` (NEW)

A `Pressable` pill showing a citation's title + type; `onPress` calls `onOpen(citation.relativePath)`. Styled with `theme.radius.sm` and `theme.colors.surface`/`accent`. Full code (small).

#### 12. `app/src/components/PageModal.tsx` (NEW — open a page)

A `Modal` (RN core) that fetches a page via `getPage(wiki, relativePath)` on open, shows a spinner then the frontmatter (title/type/tags) + `Markdown` of `page.content`, with a close button and an error state. Used by both `ChatScreen` (citation tap → BR-071) and `BrowseScreen` (page tap → BR-073). Props: `{ wiki, relativePath, onClose }`.

#### 13. `app/src/screens/ChatScreen.tsx` (NEW — replaces placeholder)

The core surface (BR-070/071/072/074, NFR-08). Holds:
- `messages: {role:'user'|'agent', text, result?}` list rendered in a `ScrollView` (agent text via `Markdown`).
- `history: ConversationTurn[]` carried forward so follow-ups need no restatement (BR-044).
- An input `TextInput` + Send button; on send: guard `activeProject` (prompt to pick one on the Projects tab if null), push the user message, set `loading`, `await postQuery(activeProject, q, history)`, append the agent message, append `{question, answer}` to history.
- **Loading state**: an `ActivityIndicator` "Thinking…" row while awaiting (BR-074, NFR-08).
- Under each agent answer: `Covered ? <CitationChip> row : an honest "Not covered by this wiki" note` (BR-042 surfaced), plus a **Save answer** button when `Covered` → `saveAnswer(activeProject, result)` then a toast/inline "Saved as <title>" (BR-045).
- Tapping a citation opens `PageModal` (BR-071).
- Error state on a failed request (network/500) shown inline.

#### 14. `app/src/screens/BrowseScreen.tsx` (NEW — replaces placeholder)

Wiki-structure browser (BR-073). On focus / when `activeProject` changes: `getWikiTree(activeProject)`; render each `PageCategory` as a section header with its `PageRef` rows (slug). Tapping a row opens `PageModal`. Loading + empty ("no pages yet") + no-project states. A refresh control re-fetches so it reflects disk (BR-073).

#### 15. `app/src/screens/ProjectsScreen.tsx` (NEW — replaces placeholder)

Projects list/select/create (BR-050, BR-052). `listProjects()` on mount; render each with name + `pageCount`/`sourceCount`/`lastIngestAt`, marking the `activeProject` with a ✓; tapping selects it via `useApp().setActiveProject`. A "New project" input + Create button → `createProject(name)`, then refresh and auto-select. Loading/error/empty states.

#### 16. `app/src/screens/HomeScreen.tsx` (KEEP, becomes the "Status" tab)

Unchanged — the existing diagnostics screen is reused as the Status tab so connectivity stays visible. (No edit required beyond its inclusion in `App.tsx`.)

#### 17. `app/.env.example` (KEEP)

Already documents `EXPO_PUBLIC_API_BASE_URL=http://localhost:5080`; no change for web. (A note in docs: native devices need the host LAN IP, not `localhost`.)

### Tests

- **`tests/LlmWiki.Api.Tests`** (UPDATE — NEW test files as needed):
  - `WikiControllerTests` — `GET /wikis/{wiki}/pages` returns categories grouped by directory for a seeded wiki; `404` for an unknown wiki; `GET /wikis/{wiki}/pages/{path}` returns a page's content and `404` for a missing page. Uses the existing `WebApplicationFactory<Program>` + `ConfigureTestServices` fake-repository pattern.
  - `QueryControllerTests` — `POST /query/save` returns a `PageOutcome` for a covered result and `400` for an uncovered one (fake `IQueryService`).
  - **Enum-serialization regression:** assert a response body now contains the string name (e.g. `"Entity"`) — and audit existing Api tests for any integer-enum assertions to update after the `JsonStringEnumConverter` change.
- **`app/`** has no JS test runner (CI gates on `tsc --noEmit` + `expo export` only). Verification is manual + the type/export gates; no unit-test project is added (keeps scope to the phase, matches the existing app gate).

### Docs (UPDATE)

- **`docs/code-overview/code-overview.md`** — flip the Phase 8 row in the phase-map table from 🔲 to ✅; add an `app/` section describing the client structure (custom tabs, typed `client.ts`, `AppProvider`, custom `Markdown`); add a UI walkthrough (§6i) for the query/browse/projects flow; note the new `WikiController` + `/query/save` + CORS/string-enum changes.
- **`docs/adr/0004-phase-8-client.md`** (NEW) — record the four locked decisions (loading-state-only over SSE; web-first; minimal/low-dependency UI; query/browse/projects-only), each with its NFR/BR justification and the deferral of streaming/native-verification/ingest-UI.
- **`CLAUDE.md`** — add a Phase 8 paragraph to the "What this is" summary and the Working-agreement history block; add the `cd app && npm run web` client flow and the new endpoints (`GET /wikis/{wiki}/pages`, `GET /wikis/{wiki}/pages/{path}`, `POST /query/save`) to the Commands section; note CORS + string-enum serialization are now on.

---

## Requirements covered

- **BR-070** (chat panel, markdown answers citing pages) — `ChatScreen` + custom `Markdown` renderer over `POST /query`.
- **BR-071** (citations render as clickable links opening the page) — `CitationChip` → `PageModal` → new `GET /wikis/{wiki}/pages/{path}`.
- **BR-072** (full query workflow: hybrid search, multi-page synthesis, follow-ups, save-answer) — driven by `/query` (history carried in client state, BR-044) + `/query/save` (BR-045).
- **BR-073** (wiki-structure browser by category reflecting disk) — `BrowseScreen` over new `GET /wikis/{wiki}/pages`.
- **BR-074 / NFR-08** (loading state during processing; responsive single-user UX) — explicit `ActivityIndicator` states on every network call; streaming deferred per decision 1.
- **BR-075** (standalone CLI `ingest`) — already shipped in Phase 2; unchanged, called out as satisfied.
- **BR-050 / BR-052** (create/list/select active project; Oracle metadata shown) — `ProjectsScreen` over `/projects` + `AppProvider` active-project pointer.
- **BR-042 / BR-045** (honest gaps surfaced; save-answer-as-page) — `Covered` gate in `ChatScreen`, save via `/query/save`.
- **NFR-07** (SK/Oracle confined) — client speaks only HTTP JSON behind `client.ts`; no vendor deps cross into the UI.
- **NFR-09** (one Expo codebase, consistent markdown on web + portable to mobile) — single custom `Markdown` component; web-first build, RN-portable code.
- **NFR-04** (reproducible local stack) — CORS opens the existing local API to the Expo web origin; no new infra.

---

## Verification (end-to-end)

**Prereqs:** `cd docker && docker compose up -d`; `dotnet build LlmWiki.slnx` and `dotnet test LlmWiki.slnx` green; a project with ingested content, e.g. `dotnet run --project src/LlmWiki.Cli -- project create demo` then `dotnet run --project src/LlmWiki.Cli -- ingest demo ./docs/sample-source.md`.

1. **API additions (BR-073/071):** with `dotnet run --project src/LlmWiki.Api` up —
   - `curl http://localhost:5080/wikis/demo/pages` → JSON `WikiTree` with categories (`summaries`, `entities`, …) and page refs. `curl http://localhost:5080/wikis/nope/pages` → 404.
   - `curl http://localhost:5080/wikis/demo/pages/entities/<slug>.md` → the page's `content`/`title`/`type`. Missing path → 404.
   - Confirm `type` in a `/query` or page response is now a **string** (e.g. `"Entity"`) not `1` (string-enum change).
2. **Save endpoint (BR-045):** `curl -X POST http://localhost:5080/query/save -H 'content-type: application/json' -d '{"wiki":"demo","result":{...a covered QueryResult...}}'` → `PageOutcome` with `change:"Created"`; an uncovered result → 400.
3. **CORS (NFR-04):** from the browser dev console on the running web app, a cross-origin `fetch(API_BASE_URL + '/health')` succeeds (no CORS error) — or inspect the `Access-Control-Allow-Origin` response header on any call.
4. **Client build gates (CI parity):** `cd app && npm install && npm run lint` (tsc clean) and `npm run export:web` (web bundle emitted) both succeed.
5. **Chat + citations (BR-070/071/072/074):** `npm run web`, open the app → **Projects** tab, select `demo`; **Chat** tab, ask a covered question → spinner shows, then a markdown answer with citation chips; tap a chip → `PageModal` opens the cited page. Ask a follow-up without restating the topic → context maintained (BR-044).
6. **Honest gap + save (BR-042/045):** ask something the wiki doesn't cover → "not covered" note, no Save button; ask a covered question → **Save answer** → "Saved as …", and the new `answers/<slug>.md` appears in the **Browse** tab after refresh and via `curl …/wikis/demo/pages`.
7. **Browse reflects disk (BR-073):** **Browse** tab lists pages grouped by category; tapping any page opens it; refresh after a new ingest shows the new pages.
8. **Projects (BR-050/052):** **Projects** tab lists projects with page/source counts; "New project" creates one (`POST /projects`), it appears and becomes active; the selection persists across a browser reload (localStorage).
9. **Status (Phase 0 parity):** **Status** tab still shows the diagnostics checks (unchanged `HomeScreen`).

---

## Out of scope (later phases / Going Further)

- **SSE/token streaming (NFR-05 streaming clause)** — deferred; would thread `IAsyncEnumerable` through `IQueryService`/`IChatService` and add a `/query/stream` (SSE) endpoint + `EventSource` consumer. Loading state satisfies BR-074 for now.
- **Native mobile verification (iOS/Android)** — code stays RN-portable and should run under Expo Go, but device/emulator setup (LAN-IP base URL, native fetch checks) and per-target markdown validation are deferred; this phase verifies web.
- **Ingestion in the UI** — no `POST /ingest`; ingestion remains the existing CLI command (BR-075).
- **Lint in the UI** — `POST /lint` + `/lint/apply` already exist and could back a future Health tab with accept/reject, but surfacing lint interactively in the client is not built this phase.
- **Auth / multi-user, cloud Oracle, Marp/Obsidian, multi-format ingestion** — all remain "Going Further" per the BRD.
