# Best Practices

Engineering conventions for this repository. These adapt our shared org standards to this repo's actual stack — **.NET 10 minimal APIs** (not MVC controllers), **Oracle + ODP.NET** (not EF Core/Postgres), **Semantic Kernel**, and an **Expo** client. Where this repo deliberately diverges from the org standard, it's called out. See [CLAUDE.md](CLAUDE.md) for commands and architecture; record cross-cutting decisions as an ADR in `docs/adr/`.

> Phase 0 status: most of the API surface below doesn't exist yet. Treat this as the contract feature phases build to — when you add the first real resource endpoint, it must land in this shape.

---

## API Design

### Response shape

**Every response — success and error — is wrapped in `ApiResponse<T>`.** Clients always parse one shape.

```json
// success
{ "success": true, "data": { "id": "...", "title": "..." }, "error": null }

// error
{
  "success": false,
  "data": null,
  "error": { "code": "RESOURCE_NOT_FOUND", "message": "The requested page was not found.", "details": {} }
}
```

- Define `ApiResponse<T>` once in `LlmWiki.Shared` (it's part of the wire contract, shared by hosts) with `ApiResponse.Ok(data)` / `ApiResponse.Fail(error)` factories. Don't hand-roll the envelope per endpoint.
- In minimal-API handlers, return the envelope via `TypedResults`/`Results.Json` with the correct status code — the envelope is the body, the HTTP status code still carries semantics (below).
- The Phase 0 operational endpoints (`/health`, `/diagnostics`) predate this contract and return raw payloads by design; see Versioning. New endpoints under the versioned API must use `ApiResponse<T>`.

### Error shape

The `error` object inside the envelope:
- `code` — a stable, SCREAMING_SNAKE_CASE identifier clients can switch on (`RESOURCE_NOT_FOUND`, `VALIDATION_FAILED`).
- `message` — human-readable, safe to surface in a UI. Never leak stack traces, SQL, or secrets.
- `details` — optional structured context (e.g. per-field validation errors); `{}` when none.

Build the error in global exception/error middleware, not scattered across handlers.

### Status codes

Use HTTP status codes semantically, even though the body is enveloped:

| Code | Meaning | | Code | Meaning |
|------|---------|-|------|---------|
| 200  | OK | | 403  | Forbidden |
| 201  | Created | | 404  | Not Found |
| 204  | No Content | | 409  | Conflict |
| 400  | Bad Request | | 422  | Unprocessable Entity |
| 401  | Unauthorized | | 500  | Internal Server Error |

### Versioning

Version the resource API behind a consistent prefix: `/api/v1/...`. **Operational endpoints (`/health`, `/diagnostics`) are intentionally unversioned and un-enveloped** — they're liveness/diagnostic probes, not part of the client API surface.

### URL naming

Plural nouns, no verbs in paths.
- Correct: `GET /api/v1/pages`, `DELETE /api/v1/pages/{id}`
- Wrong: `GET /getPages`, `POST /deletePage`

**Exception — auth endpoints.** `/auth/login`, `/auth/logout`, `/auth/refresh`, `/auth/register`, and `/users/me/change-password` use action-style paths by design (one-shot operations, not CRUD on a "session" resource — the shape every JWT/OAuth client expects). New auth-flow endpoints may follow the same form; everything outside `/auth/*` and password-change follows the resource-naming rule.

### Pagination

Paginate all list endpoints — cursor-based preferred, offset/limit acceptable. Never return an unbounded collection. (Wiki pages and search results will be large; this matters before Phase 4.)

---

## C# / .NET Backend

### Async

Truly async end-to-end. Never block with `.Result` or `.Wait()`. `async void` is forbidden (event handlers excepted). Our ports are already async — keep adapters async to the underlying driver (ODP.NET, SK, HTTP).

### Cancellation

Every async handler and service method takes a `CancellationToken` (last parameter, `= default` on interfaces) and **passes it through** to all downstream calls — exactly as the `Application/Ports` interfaces already do. Honor it in adapters.

### Dependency injection / lifetimes

Register by interface, not concrete type (as `AddLlmWikiInfrastructure` does). Choose lifetimes intentionally:
- **Scoped** — anything that touches a per-request Oracle connection/unit-of-work.
- **Transient** — stateless helpers.
- **Singleton** — thread-safe, genuinely shared state only.

> Phase 0 note: the stub adapters are registered as singletons because they hold no state. When the real Oracle persistence lands (Phase 3), connection-bound services move to **Scoped** — don't keep a stateful DB adapter as a singleton.

### Exception handling

Never catch `Exception` broadly to swallow it. Let unhandled exceptions bubble to global error middleware (which renders the `ApiResponse` error shape). Catch a *specific* exception only to handle it or to rethrow with added context. The one sanctioned "catch and continue" is the diagnostics orchestrator: each check catches its own failure, records pass/fail + detail, and lets the others run.

### Endpoint design (minimal APIs)

Keep endpoint handlers thin: validate input, delegate to an `Application` service (via a port), map the result into `ApiResponse<T>` + status code. **No business logic and no data access in `Program.cs`.** This is the minimal-API equivalent of "thin controllers."

> We use ODP.NET, not EF Core — so EF-specific practices (migrations, functional-index hand-edits, `DbContext` lifetimes) don't apply here. SQL lives in the Oracle adapters under `Infrastructure/Persistence`.

---

## TypeScript / React Frontend

### TypeScript strictness

No `any`. Every parameter, return type, and state variable is explicitly typed or correctly inferred. Use `unknown` over `any` when a shape is genuinely unknown. `npm run lint` (`tsc --noEmit`) must stay clean — it's a CI gate.

### Component structure

Function components, not classes. One component per file. Co-locate the component's types and helpers in the same file unless shared — then extract to a shared types file. Type the API envelope (`ApiResponse<T>`) once on the client and reuse it.

### Data fetching

Fetch/mutation logic lives in custom hooks or a data-fetching layer (e.g. React Query when it's added), never inline in component bodies. Components consume data; they don't orchestrate fetches. Read the API base URL from `EXPO_PUBLIC_API_BASE_URL` — don't hardcode hosts.

### State / Context

Avoid prop drilling beyond 2 levels. Use React Context for cross-cutting concerns (auth, theme). Do **not** put server state in Context — that belongs in the data-fetching layer.

---

## Testing

### Scope

Unit tests cover pure domain logic and the `Application` service layer, using fakes/stubs for ports — not a live DB. Integration tests own real Oracle interaction (and run only when the Docker infra is up).

### Naming

`Should_[ExpectedResult]_When_[Condition]`. One logical assertion per test (several `Assert` calls are fine if they verify one concept — see `DiagnosticsServiceTests`).

### Philosophy

Test behavior, not implementation details. A test should break when behavior changes, not when internal structure is refactored.

### Isolation

Each test arranges its own state, shares no mutable state with others, and cleans up after itself. DB-backed tests use isolated transactions or roll back.

---

## Testing Frameworks

### Backend — xUnit

**Stack:** xUnit · `Microsoft.AspNetCore.Mvc.Testing` (`WebApplicationFactory<Program>`) · coverlet. Projects: `tests/LlmWiki.*.Tests/`.

```bash
dotnet test LlmWiki.slnx                                            # all
dotnet test tests/LlmWiki.Domain.Tests/LlmWiki.Domain.Tests.csproj # one project
dotnet test LlmWiki.slnx --filter "FullyQualifiedName~Diagnostics" # by filter
dotnet test LlmWiki.slnx --collect:"XPlat Code Coverage"           # coverage
```

**Divergence from the org standard:** this repo uses **hand-written `sealed` fakes** against the `Application` ports (see `FakeDatabase`/`FakeEmbeddings`/`FakeChat` in `DiagnosticsServiceTests`) — **not Moq, and not EF Core InMemory** (we have no EF Core). Keep fakes local to the test file. Don't introduce a mocking framework without a deliberate reason. API tests boot the real host via `WebApplicationFactory<Program>` and assert on HTTP behavior.

### Frontend — not yet set up

The Expo app currently has **no test runner**; `npm run lint` (`tsc --noEmit`) and `npm run export:web` are the only CI gates. When component/E2E testing is added, adopt the org-standard stack and wire the npm scripts:
- **Unit/component** — `jest-expo` + `@testing-library/react-native`; query by role/text/label, never by test ID. Test hooks with `renderHook`.
- **Web E2E** — Playwright (Chromium) against `expo start --web`.
- **Native E2E** — Detox (requires `npx expo prebuild` to generate `ios/`/`android/` first).

---

## Repository architecture rules

These are non-negotiable for this codebase (full rationale in [CLAUDE.md](CLAUDE.md) and [ADR 0001](docs/adr/0001-phase-0-foundations.md)):

- **Dependencies point inward.** `Domain` → nothing; `Application` → `Domain`; `Infrastructure`/`Agents` → `Application`; `Api`/`Cli` are the only composition roots. Semantic Kernel and Oracle stay out of `Domain`/`Application` — expose them through ports.
- **Ports & adapters.** A new capability is a port in `Application/Ports/` + an adapter in `Infrastructure/` registered in `AddLlmWikiInfrastructure`. **Fill the existing stub** (`OracleProjectRepository`, `OracleVectorStore`, `FileSystemWikiFileStore`) when its phase lands — don't add a parallel type. Not-ready stubs throw `NotImplementedException("...until Phase N")` so misuse fails loudly; the one sanctioned degraded-but-running fallback is `NotConfiguredChatService`.
- **Config & secrets.** Nothing sensitive in source/appsettings/VCS — secrets come from gitignored `env/.env` (Gitleaks gates CI). Adding a setting touches three places: `env/.env.example`, the `*Options` class, and the `EnvToConfigKey` map in `LlmWikiConfiguration`. Bind strongly-typed options; don't read `Environment.GetEnvironmentVariable` from feature code.
- **Central Package Management.** Versions are pinned in `Directory.Packages.props`; a `.csproj` references packages with **no `Version=`**. SK preview warnings (`SKEXPxxxx`) are suppressed centrally in `Directory.Build.props` — don't `#pragma` them per file.
- **C# style** (enforced by `.editorconfig`): file-scoped namespaces, `using`s outside the namespace, `System.*` first, nullable enabled, `sealed` + primary constructors as the surrounding code does. `CA2007` is intentionally off — don't add `.ConfigureAwait(false)`.
- **Git.** Per [CLAUDE.md](CLAUDE.md), **never commit** — stage changes and draft a message; the human runs `git commit`. Keep all three CI jobs green (.NET build+test, Expo build+lint, Gitleaks).