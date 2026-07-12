# ADR 0004 — Phase 8 client (React Native / Expo) + browse API

- Status: Accepted
- Date: 2026-07-12

## Context

Phases 0–7 built the entire backend and its CLI + partial HTTP surface: file-backed wiki, agentic
ingestion, journal, Oracle hybrid retrieval, grounded/cited query synthesis, the project registry, and
the lint/health-check workflow. What was missing is the product's **front door** — a human-usable
client. The `app/` Expo project was only the Phase-0 connectivity slice (a diagnostics screen + a typed
`getHealth`/`getDiagnostics` client, with `screens/state/navigation/theme` intentionally stubbed "for
Phase 8"). Phase 8 (BR-070…BR-075, NFR-08/09) delivers a React Native (Expo) chat/browse client for
**web** and adds the small amount of HTTP surface it needs: a browse tree, a single-page read, and a
dedicated save-answer endpoint. No new Oracle table and no new agent workflow — this phase is glue and
UI over existing ports.

## Decisions

1. **Query progress is loading-state-only; SSE/token streaming is deferred.** `POST /query` stays a
   single blocking JSON response and the client shows a spinner ("Thinking…") — enough to satisfy
   BR-074/NFR-08. True streaming (NFR-05's streaming clause) would thread `IAsyncEnumerable` through
   `IQueryService` **and** the `IChatService` port and add an SSE `/query/stream` + `EventSource`
   consumer — a cross-layer change out of proportion to the UI goal. Deferred to "Going Further".

2. **Web-first.** Build and verify against the web target, matching the existing CI gate
   (`expo export --platform web`). Code stays RN-portable so native still runs under Expo Go, but no
   device/emulator-specific verification (LAN-IP base URL, per-target markdown) this phase (NFR-09).

3. **Minimal / low-dependency UI — no navigation, markdown, or state library added.** A custom
   `useState` tab switcher instead of React Navigation and a small custom markdown renderer for the
   answer/citation/page subset, avoiding compatibility risk on the bleeding-edge stack (Expo SDK 56,
   RN 0.85.3, React 19.2.3). The client keeps the single typed `src/api/client.ts` boundary so SK/Oracle
   never leak toward the UI (NFR-07); no vendor deps cross into the app.

4. **Query / browse / projects only — ingestion and lint stay out of the UI.** The client covers chat
   (BR-070/072), clickable citations that open a page (BR-071), the wiki browser (BR-073), project
   list/create/select (BR-050/052), and save-answer-as-page (BR-045). Ingestion remains the shipped CLI
   command (BR-075) — no `POST /ingest`. Lint's `POST /lint`·`/lint/apply` already exist and could back a
   future Health tab, but surfacing it interactively is not built this phase.

5. **API glue reuses existing ports; CORS + string enums are turned on centrally.** `WikiController`
   passes through `IWikiRepository.ListPagesAsync`/`ReadPageAsync` (grouping paths into a `WikiTree`);
   `POST /query/save` reuses `IQueryService.SaveAnswerAsync` so a good answer persists without a second
   synthesis (and refuses uncovered answers, 400). `Program.cs` enables a permissive default CORS policy
   (single local user, NFR-04 — the Expo web build is a browser origin otherwise blocked) and a
   `JsonStringEnumConverter` on both the controller and minimal-API pipelines so enums serialize as their
   names (`"Entity"`), matching the client's string-union model.

## Consequences

The whole BRD surface (backend + CLI + API + web client) is now real; no application-adapter stubs
remain. The string-enum change is a wire-format change: existing API tests that deserialize a response
enum (`PageOutcome.Change`) were given a converter-aware `JsonSerializerOptions`, and a regression test
asserts the string name now appears. The client has no JS test runner — verification is the existing
`tsc --noEmit` + `expo export` CI gates plus manual walkthrough — matching the established `app` job and
keeping scope to the phase. Deferred: SSE/token streaming, native device verification, and ingestion/lint
UI, all per the decisions above.
