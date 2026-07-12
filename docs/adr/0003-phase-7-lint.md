# ADR 0003 — Phase 7 lint / health-check

- Status: Accepted
- Date: 2026-07-11

## Context

Phases 0–6 built the *authoring and retrieval* half of the wiki: ingest sources into typed pages,
journal an `index.md`/`log.md`, retrieve with hybrid search, synthesise cited answers, and track
projects. What was missing is the *maintenance* half — the reason the BRD prefers a compiled wiki over
plain RAG (§3.3): keeping the wiki **internally consistent**. Nothing detected that a page links to a
page that no longer exists, that a page is orphaned, that two pages contradict each other, or that a
referenced concept was never written. Phase 7 (BR-060…BR-063) adds a lint / health-check workflow:
walk the wiki, produce a **prioritised** report (critical → warning → suggestion), and let the user
**accept/reject** actionable suggestions — no page changes without confirmation unless an explicit
auto-fix mode is on. Unlike Phases 4/6, there was no stub to fill; `Linting` is a fresh vertical
alongside `Ingestion`/`Query`/`Indexing`.

## Decisions

1. **Hybrid computation — deterministic structural pass + one LLM call.** The checks the acceptance
   criteria say must be caught *every time* (broken links, orphans, missing pages, thin pages) are
   computed deterministically by reusing `IWikiRepository.ResolveLinksAsync` + the resolved
   inbound-link graph — no new parsing code, no LLM variance. A single JSON-mode `IChatService` call
   (`LintPrompts.Analyze`, parsed via the shared fence-stripper) adds the findings that genuinely need
   judgment: contradictions, stale claims, and suggested questions/sources. This mirrors the
   `QueryService` orchestrator shape exactly (ports only, one `CompleteAsync(jsonMode:true)`), so the
   SK Process Framework can later replace it behind `ILintService` (NFR-07).

2. **Stub-creation is the only page-mutating fix this phase.** Accepting a *missing-page* finding
   writes a stub page, then best-effort rebuilds the index, appends a `lint` log line, and embeds —
   mirroring `QueryService.SaveAnswerAsync`'s per-step write boundaries (NFR-06). All other findings
   (contradictions, orphans, stale claims, suggestions) are **report-only**; we do not let LLM-authored
   writes mutate existing pages this phase. A broken markdown link is upgraded to an applyable missing
   page only when its target *normalizes* (relative to the source page) to an unambiguous single typed
   directory (`concepts/anvil.md`); a bare wikilink target has no unambiguous directory and stays
   report-only.

3. **Lint output is derived — no Oracle table.** Findings are recomputed on every run and the pass is
   recorded in `log.md` (BR-021); there is nothing durable to persist that a re-run wouldn't
   regenerate. This keeps files canonical and Oracle confined to the search index + project registry.

4. **Full API (report + apply) ahead of Phase 8's client.** The CLI gets `lint [wiki] [--fix]
   [--report]` (interactive accept/reject/modify by default). The API gets **both** `POST /lint`
   (report) and `POST /lint/apply` (apply one echoed-back finding), so the future React-Native client
   (Phase 8) can drive accept/reject over HTTP without a follow-up API phase. `LintController` mirrors
   `QueryController` and is auto-wired by the existing `app.MapControllers()`.

## Consequences

Phase 7 is the last unbuilt *application* surface — after it, only Phase 8's React-Native client
remains. The lint pass never corrupts the wiki: the semantic pass and the log line are best-effort, and
apply uses per-step boundaries so a journal/embed failure is a recorded note on the `PageOutcome`,
never a throw (NFR-06). `raw/` is untouched — `ListPagesAsync` excludes it (NFR-02). Auto-editing
contradictions/stale claims, page deletion/merge suggestions, and the RN lint UI are out of
BR-060…063 and deferred.
