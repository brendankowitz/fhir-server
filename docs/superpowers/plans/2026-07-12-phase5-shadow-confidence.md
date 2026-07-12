# Phase 5½: Shadow Confidence (Scientist-Style Comparison for the Ignixa Migration)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. This document is currently a design + task-breakdown proposal (produced by a Fable investigation) — before execution, expand each task into a full brief the way this session's other plans do, since some tasks (2, 3, 4 especially) need more implementation detail than is captured here.

**Goal:** Build additional confidence in the Ignixa migration beyond what hand-authored parity tests provide, by running Firely and Ignixa implementations side-by-side on the same real input (test replay first, narrow production shadow later), comparing outputs, and publishing PHI-safe divergence signals — without ever changing what's returned to a caller.

**Origin:** The user asked whether [Scientist.NET](https://github.com/scientistproject/Scientist.net) (GitHub's "run control and candidate, always trust control, log divergence" pattern) could help, prompted by this session finding a real parity gap (an `Expression`-datatype STU3 test fixture bug) that had escaped every prior review because no test corpus happened to exercise it. A Fable investigation (full findings preserved in `.superpowers/sdd/progress.md`'s ledger and the investigation transcript) recommends adopting the **pattern**, not the **package**, and identifies which seams are safe to compare on live traffic vs. replay-only.

**Sequencing:** After Phase 4 Plan B's remaining tasks (4-7) complete. Shares no code with Phase 5 (US-17+, the `IModelInfoProvider`/FHIRPath contract work) — can run before, after, or interleaved with it. Named "Phase 5½" to avoid colliding with `user-stories.md`'s existing Phase 5 numbering.

## Key findings from the investigation

- **The cheapest experiment is already running and being thrown away.** `IgnixaResourceValidator` in Hybrid mode (`_skipFallbackOnSuccess == false`) already executes BOTH the Ignixa schema validator and the Firely fallback validator on every schema-validated resource — and discards the comparison. Adding a mismatch counter here costs nothing extra at runtime.
- **Adopt Scientist's vocabulary (`Use`/`Try`/`Compare`/`Clean`/`Enabled`/`IResultPublisher`), not the NuGet package**, for four reasons: (1) Scientist.NET's `Use`/`Try` execution order is randomized and NOT configurable — unsafe for seams that read/mutate shared request-context state (both bundle factories append to `IFhirRequestContext.BundleIssues`); a control-first, snapshot/restore harness is needed instead. (2) Scientist's `ResultPublisher`/`Enabled` are process-wide mutable statics, inconsistent with this codebase's constructor-injection discipline. (3) The comparator, PHI-safe publisher, and sampling config are custom work regardless of whether the shell library is used — the shell is a small fraction of the total effort (~150 lines). (4) No new third-party runtime dependency needs review-cost justification when an in-repo equivalent is this cheap.
- **Seams split cleanly into "safe on live traffic" and "replay-only, permanently"**:
  - **E0 (validation)** and **E1 (`IBundleFactory.CreateSearchBundle`/`CreateHistoryBundle`)**: synchronous, no I/O, don't mutate their input (aside from the `BundleIssues` hazard, which is mitigable) — safe to shadow-compare on sampled live traffic.
  - **E2 (reference resolution)** and **E3 (batch decomposition)**: do real database I/O (conditional search) and/or mutate the resource/shared dictionaries in place — running "both" in production would double real DB load and risk non-idempotent side effects. These stay replay/E2E-only, permanently, not "until we're braver."
- **Discharges an outstanding Plan A merge-gate obligation**: Fable's Plan A sign-off made merging conditional on a 3-mode CI/live smoke test. This phase's Task 5 (running the E2E suite with shadow-compare in `ThrowOnMismatch` mode) upgrades that obligation from "eyeball three runs" into an automated gate.

## Global Constraints

- **No production run-both on any seam involving I/O, input mutation, or shared mutable state.** E2/E3 are replay-only permanently — this is not a temporary restriction to revisit later.
- **No raw payload or diff values in any production telemetry sink, ever.** The mismatch report type must be structurally incapable of carrying PHI (JSON pointer *paths* only — `/entry/3/search/mode` — never the values at those paths, resource type + issue codes/counts, no resource content). Enforce this via the report type's shape, not reviewer vigilance.
- **No automatic behavior change on mismatch** — no auto-fallback, no circuit breaker. This is observability only; the control/primary result is always what's returned to the caller, unconditionally.
- **Shadow-compare is orthogonal to `SdkMode`, not a new mode value.** `SdkMode` answers "which implementation serves the caller"; shadow-compare answers "does the other one agree." Implement as a decorator around the mode-selected implementation, registered in the same module that does mode selection (e.g. `SearchModule` for E1), preserving the single-owner-per-seam principle established since US-1.
- **Production enablement is a separate, explicit, human-ratified gate** (Task 8) — Tasks 1-7 deliver full value (replay-based confidence, the Plan A merge gate) without ever turning shadow-compare on in production. Config must default `Enabled: false`, and `ThrowOnMismatch: true` must be refused by config validation outside `Development`/CI environments — make the unsafe state unrepresentable, don't just document it.
- Build verification: `dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60` — `0 Warning(s)` beyond pre-existing. Pre-existing NOT-to-fix failures: the four `*.Tests.E2E` SDK-version environment failures, occasional transient Roslyn/MSBuild crashes (retry once).

---

### Task 1: E0 — validation divergence telemetry

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Validation/IgnixaResourceValidator.cs`
- Modify/create: telemetry test file alongside `IgnixaResourceValidatorTests.cs`

**Interfaces:** New metric counter(s) via the existing `BaseMeterMetricHandler`/`"FhirServer"` meter pattern — no new abstraction needed, this is instrumentation inside an already-dual-executing code path.

**Smallest task in this phase, shippable standalone, no dependency on Tasks 2-4.**

- [ ] Read `IgnixaResourceValidator.cs`'s `TryValidateIgnixa` in full (the Hybrid dual-run fall-through around lines 196-236, per the investigation).
- [ ] Add a counter/structured log at the pass→Firely-fallback-also-runs point: if Ignixa passes but the Firely fallback (already being called) disagrees, that's a real divergence on real traffic — record resource type + both sides' issue-code sets (not values).
- [ ] Add a config-gated (default off) fail-path probe: when Ignixa rejects a resource, optionally also run the Firely fallback to detect "Ignixa stricter than Firely" divergences — this is the direction that already changes client-visible behavior in Hybrid today (client gets rejected when Firely-mode would have accepted), so it's valuable, but costs an extra validation call on the failure path only. Gate behind a config flag (default off) since this is a real, if small, cost — get the human's read on whether to default it on later, per this plan's "Decisions needing human ratification."
- [ ] Wire the new counter(s) via `TryAddSingleton` in `AddMetricEmitter`, following the exact pattern of existing `I*MetricHandler` registrations.
- [ ] Tests: construct resources where Ignixa and Firely validation would disagree, confirm the counter/log fires with the right resource-type/issue-code data and nothing PHI-bearing.
- [ ] Build, test, commit.

---

### Task 2: `FhirJsonComparer` + normalization registry

**Files:**
- Create: a new comparer class (location TBD at implementation time — likely `src/Microsoft.Health.Fhir.Core/Features/...` given it's SDK-neutral utility code)
- Modify: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Search/IgnixaBundleFactoryTests.cs` (retrofit as acceptance proof)

**Interfaces:** Produces a diff, not a bool — a list of differing JSON-pointer paths + mismatch kind (value/missing/extra/candidate-threw), not just equal/unequal. Everything in Tasks 3+ consumes this.

**This is the largest new component in the phase — budget accordingly.**

- [ ] Design a DFS-based comparator over two `JsonNode` trees with a normalization pre-pass (strip known-volatile fields like `meta.lastUpdated`; handle numeric-literal-fidelity differences between serializers, e.g. `1.0` vs `1.00`; handle array-order sensitivity appropriately — FHIR arrays are generally order-significant, confirm this assumption holds for the fields this phase's seams actually produce).
- [ ] Cover adversarial cases in tests: array reorder, missing-vs-null, numeric formatting differences, nested object differences producing correctly-pathed pointers (e.g. `/entry/3/search/mode`).
- [ ] Retrofit into `IgnixaBundleFactoryTests.cs`: replace/augment its current field-by-field assertions with a full normalized-JSON diff via this comparer — this both proves the comparer works and strengthens an existing test for free.
- [ ] Build, test, commit.

---

### Task 3: Shadow-compare core (`ShadowExperiment`, config, publisher)

**Files:**
- Create: `ShadowCompareConfiguration` (config class)
- Create: `ShadowExperiment`-style harness (control-first execution, sampling, candidate exception capture)
- Create: `ShadowComparisonReport` (the PHI-safe report type — paths/kinds only, never values)
- Create: `IShadowResultPublisher` + a default meter/logger-backed implementation
- Modify: `FhirServerServiceCollectionExtensions.cs`'s `AddMetricEmitter` (or equivalent) for wiring
- Modify: startup logging to report effective shadow-compare config, mirroring the existing `SdkMode` startup log line

**Interfaces:** `ShadowCompareConfiguration` (`Enabled: bool = false`, `SamplingPercentage: int = 1`, `ThrowOnMismatch: bool = false`, `Seams: HashSet<string>`). The harness's execution contract: control/primary always runs and its result is always returned; candidate exceptions are caught and recorded, never propagated; comparison + publish happen off the response-critical path where safely possible (the investigation notes `IgnixaRawBundle`'s immutable-after-construction design makes background diff/publish race-free for E1).

- [ ] Design and implement the harness (deterministic control-first order — this is the load-bearing safety property Scientist.NET can't guarantee due to its randomized ordering).
- [ ] Implement `ShadowComparisonReport` so its shape structurally cannot carry PHI — no raw value fields, only paths/counts/type-names. Verify by construction (no property on the type accepts arbitrary payload content), not by convention.
- [ ] Implement the default publisher: metric counter (`ShadowCompare.Runs`, tagged seam + outcome) and latency histograms (`ShadowCompare.PrimaryLatency`/`.CandidateLatency` — a free Firely-vs-Ignixa perf comparison on identical real inputs) via the existing meter pattern; structured `ILogger.LogWarning` on mismatch with the report. Wrap publish in try/catch — a publisher failure must never fail the real request, mirroring `ApiNotificationMiddleware`'s existing pattern.
- [ ] Config validation: refuse `ThrowOnMismatch: true` outside `Development`/CI environments — a hard validation failure at startup, not a warning.
- [ ] Tests: sampling behaves correctly at the boundary: candidate exceptions never propagate; publisher failures never fail the request; config validation rejects the unsafe combination.
- [ ] Build, test, commit.

---

### Task 4: E1 — `ShadowComparingBundleFactory` (search/history bundle assembly)

**Files:**
- Create: `ShadowComparingBundleFactory : IBundleFactory` (decorator)
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Modules/SearchModule.cs` (wrap the existing mode-selected registration)
- Modify: `docs/features/sdk-migration/provider-map.md` (a "shadow-comparable" note on the search/history bundle assembly row)

**Interfaces:** Decorates the mode-selected `IBundleFactory` (Ignixa in Hybrid, per current default) with the other implementation as candidate.

**Depends on:** Tasks 2 and 3.

- [ ] Implement the decorator: run the mode-selected implementation as primary (unchanged production behavior), and — when sampled — also run the other implementation as candidate, with `IFhirRequestContext.BundleIssues` isolation (snapshot before candidate runs, restore after — or run candidate against a cloned `IFhirRequestContext` via the same settable-context technique this session's own tests already use).
- [ ] Add a contamination regression test: prove the candidate run doesn't leak into the context the primary/caller actually sees (e.g., primary appends an issue; assert candidate neither sees it nor double-appends anything the caller would observe).
- [ ] Add a mismatch-produces-report test using Task 2's comparer.
- [ ] Wire into `SearchModule.cs` alongside the existing mode-selection branch — read the current code first, since Task 5 of Plan A already established the mode-select pattern there.
- [ ] Build, test, commit.

---

### Task 5: Stage-1 replay gate (discharges the Plan A merge-gate obligation)

**Files:** Test/CI configuration changes — no new production code.

**Depends on:** Task 4.

- [ ] Run the integration/E2E suite (wherever Cosmos/SQL emulators are actually available — this session's dev sandbox lacks them, confirmed repeatedly; this task likely needs to run in CI or a properly-provisioned environment) in Hybrid mode with `Enabled=true, SamplingPercentage=100, ThrowOnMismatch=true` for the Validation/SearchBundle/HistoryBundle seams.
- [ ] Triage every divergence found: for each, determine whether it's a real bug (fix it) or a legitimate, intentional difference (add it to the comparator's `Ignore`/normalization list with a documented reason per entry — mirroring the "intentional differences documented" discipline established for US-9-style parse-strictness work).
- [ ] This task's clean pass is the direct fulfillment of Fable's outstanding Plan A merge condition — reference that explicitly when reporting this task's completion.
- [ ] Commit whatever normalization-list additions or bug fixes were needed.

---

### Task 6: E2 — reference-resolver replay experiment

**Files:**
- Modify/extend: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Resources/IgnixaResourceReferenceResolverTests.cs` (promote the existing hand-corpus pattern to the integration tier over replayed/seeded transaction bundles)

**Interfaces:** None new — this promotes an existing test PATTERN (clone inputs, dual dictionaries, `DeepEquals` + dictionary parity) to run over a broader, replayed corpus rather than just the hand-authored fixtures.

**Depends on:** Task 2 (comparer). Gate: should be green before Phase 4 Plan B Task 6 wires the reference resolver into production `BundleHandler` traffic — this task exists to build confidence BEFORE that wiring flip, not after.

- [ ] Expand the existing resolver parity test's corpus to include replayed or more broadly-generated transaction-bundle shapes, not just the hand-authored adversarial cases.
- [ ] Confirm output parity (serialized entry JSON, resolved counts, final dictionary contents) across the expanded corpus.
- [ ] Build, test, commit.

---

### Task 7: E3 — decomposition-view comparison

**Files:**
- Test-only additions comparing `FirelyBundleEntryView` and `IgnixaBundleEntryView` output for the same input bundle.

**Interfaces:** None new.

**Depends on:** Phase 4 Plan B Tasks 4+ (the decomposition views need to be comparable end-to-end, i.e., meaningfully divergent-or-not, which requires more of Plan B's native path to exist).

- [ ] For a shared set of input bundles, adapt entries through both `IBundleEntryView` implementations and compare (method, path, all four conditional headers, serialized body bytes) per entry — before anything executes, per the investigation's explicit warning against comparing at the `GenerateRequest`/executed-request level (double-describing inner requests would be architecturally noisy).
- [ ] Build, test, commit.

---

### Task 8: Production-enablement runbook + human sign-off (docs only, no code)

**Files:**
- Create: a runbook doc (location TBD — likely alongside `docs/features/sdk-migration/`)

**Depends on:** Tasks 1-7 all complete. **This task does not unilaterally enable production shadow-compare** — it prepares the decision materials for a human to ratify.

- [ ] Document a recommended sampling percentage and rationale.
- [ ] Document dashboard/alert queries against `ShadowCompare.Mismatches` (or whatever the final metric names are) for whoever operates this in production.
- [ ] Write a PHI-review checklist for the report type, to hand to whatever compliance/security review process this organization uses before enabling any new telemetry in a healthcare system.
- [ ] Document rollback: since shadow-compare never changes what's returned to callers, "rollback" is simply flipping `Enabled` back to `false` — but state this explicitly so it's not treated as a bigger operational change than it is.
- [ ] This task is complete when the runbook exists and the human has been presented the decision — actually flipping `Enabled: true` in a real production config is the human's call, not something any task in this plan does automatically.

---

## Decisions needing human ratification (not blocking Tasks 1-7)

1. **Pattern-not-package** (adopt Scientist's vocabulary, not the NuGet dependency) — reversible in about a day either direction if the team disagrees later.
2. **Whether to ever enable production shadow-compare at all** (Task 8's actual gate) — Tasks 1-7 deliver full value (replay-based confidence + the Plan A merge gate) without ever answering this question.
3. **E0's fail-path Firely probe** (Task 1) — small added cost on the validation-failure path only; recommended, but genuinely optional and config-gated.
4. **Exact sequencing relative to US-17+ (Phase 5)** — no code coupling either way; recommended default is Tasks 1-5 immediately after Plan B (Task 5 unblocks the merge gate), Tasks 6-7 interleaved with Plan B's own tail.

## Explicitly out of scope

- Shadow-comparing JSON formatters, FHIRPath/search indexing, XML, terminology, patch, or profile validation. (US-20's own planned dual-run parity harness should consume this phase's comparer + report/publisher infrastructure rather than inventing parallel machinery — a coordination note for whoever plans US-20, not new scope here.)
- Any write-side seam (import parsing into persistence, CRUD metadata mutation) — the pattern is read-side only by design.
- Automatic behavior switching on mismatch (no auto-fallback, no circuit breaker).
- Replaying captured production PHI into staging as part of this phase — "replayed traffic" means the existing E2E/integration corpora and synthetic bundles; a true de-identified production-replay corpus would need the anonymizer pipeline and its own review.
