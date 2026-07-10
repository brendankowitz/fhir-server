# Phase 3 Cleanup: Rebuild Helper + Documentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Address the concrete follow-ups Fable's final whole-plan review of Force-Ignixa Phase 3 (`docs/superpowers/plans/2026-07-09-force-ignixa-phase3.md`) recommended: extract the now-4x-duplicated mutate-then-rebuild pattern into a shared helper, write down the reuse-vs-rebuild rule this plan's Task 2/4/5 each had to re-derive independently, enrich the upstream-gap tracker with a mechanical resolution tripwire, and note Task 5's dormancy at its actual code site (not just in ledger/test comments).

**Architecture:** Purely additive/refactor-only — this plan changes no runtime behavior for any resource. The helper extraction is a pure code-motion refactor (verified byte-for-byte equivalent via existing test suites); the documentation tasks add new files/comments only.

**Tech Stack:** C#/.NET 9, Markdown docs.

## Global Constraints

- This repo enforces StyleCop alphabetical `using` ordering as a build error (`TreatWarningsAsErrors`).
- Build verification command for every task: `dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60` — `0 Warning(s)` beyond pre-existing. Pre-existing, unrelated failures you may see and must not try to fix: the four `*.Tests.E2E` SDK-version environment failures, and occasional transient Roslyn/MSBuild crashes (retry once before reporting a concern).
- Task 1 is a pure refactor: every one of the 4 call sites' *behavior* must be identical before and after — only the code's location changes. Do not alter any surrounding logic while you're in these files.

---

### Task 1: Extract the mutate-then-rebuild helper

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Ignixa/IgnixaResourceElementExtensions.cs` (or create a new file in this project if that one doesn't fit — see Step 1)
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/Create/CreateResourceHandler.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/Upsert/UpsertResourceHandler.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/Upsert/ConditionalUpsertResourceHandler.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/Upsert/BulkUpdateService.cs`

**Why:** Fable's final Phase 3 review identified this exact sequence duplicated verbatim at 4 call sites (`CreateResourceHandler.cs:108`, `UpsertResourceHandler.cs:144`, `ConditionalUpsertResourceHandler.cs:90`, `BulkUpdateService.cs:665`):
```csharp
var ignixaElement = new IgnixaResourceElement(resourceJsonNode, _schemaContext.Schema);
resourceElement = new ResourceElement(ignixaElement.ToTypedElement(), ignixaElement);
```
Past the rule of three. This is also the exact incantation Task 2 of the Phase 3 plan got wrong on its first attempt (reused the stale wrapper instead of rebuilding) — centralizing it means the correct behavior only has to be gotten right once, and its doc comment becomes the one place this rule lives at the point of use.

- [ ] **Step 1: Read all 4 call sites and confirm they're identical**

Read each of the 4 files' relevant section in full. Confirm the two-line sequence (mutate the node, then these two lines) is truly identical in shape at each site — same two constructor calls, same variable roles (a mutated `ResourceJsonNode`, an `IIgnixaSchemaContext`-derived schema, an output `ResourceElement` variable). If any site differs meaningfully (e.g., different exception handling, different variable naming that reflects a real semantic difference, not just a name), note this in your report and decide whether it still fits a single shared helper — if it doesn't cleanly fit, report NEEDS_CONTEXT rather than forcing a bad abstraction.

- [ ] **Step 2: Add the helper**

Add to `src/Microsoft.Health.Fhir.Ignixa/IgnixaResourceElementExtensions.cs` (read this file first to match its existing style — it should already have `IgnixaResourceElement`/`ResourceElement`-related extension methods, e.g. `ToResourceElement()`):

```csharp
/// <summary>
/// Rebuilds a fresh <see cref="ResourceElement"/> wrapper after <paramref name="resourceJsonNode"/>
/// has been mutated in place.
/// </summary>
/// <remarks>
/// After mutating a <see cref="ResourceJsonNode"/> held by a <see cref="ResourceElement"/>, always
/// rebuild via this method rather than reusing the original <see cref="ResourceElement"/> instance.
/// The original instance's cached <c>Instance</c>/<c>ToPoco()</c> views can go stale after in-place
/// node mutation -- this bit a Phase 3 task in production
/// (see docs/features/sdk-migration/node-mutation.md for the full rule and the two shapes of
/// ResourceInstance that make this hazard non-obvious at the type level).
/// Reuse-in-place is permitted ONLY when every downstream consumer of the returned value is proven
/// to read exclusively via <c>GetIgnixaNode()</c> -- and that proof must be recorded in a comment
/// at the call site, not assumed.
/// </remarks>
public static ResourceElement RebuildResourceElement(this ResourceJsonNode resourceJsonNode, IIgnixaSchemaContext schemaContext)
{
    EnsureArg.IsNotNull(resourceJsonNode, nameof(resourceJsonNode));
    EnsureArg.IsNotNull(schemaContext, nameof(schemaContext));

    var ignixaElement = new IgnixaResourceElement(resourceJsonNode, schemaContext.Schema);
    return new ResourceElement(ignixaElement.ToTypedElement(), ignixaElement);
}
```

Verify `IIgnixaSchemaContext` is visible from `Microsoft.Health.Fhir.Ignixa` (it should be, since `IgnixaResourceElement`'s own constructor already takes a `Schema` derived from it in the 4 call sites) — if it's not visible from this project for a layering reason, put the helper in `Microsoft.Health.Fhir.Core.Extensions` (alongside `ResourceElementIgnixaExtensions.cs`, which already hosts `GetIgnixaNode()`) instead, and note this deviation in your report with the reason.

- [ ] **Step 3: Replace all 4 call sites**

In each of the 4 handler files, replace the two-line sequence with:
```csharp
resourceElement = resourceJsonNode.RebuildResourceElement(_schemaContext);
```
(adjust the exact variable names to match each site's existing local variable names — do not rename anything else in these files). Add `using Microsoft.Health.Fhir.Ignixa;` (or `Microsoft.Health.Fhir.Core.Extensions;`, matching wherever you placed the helper in Step 2) in alphabetical order if not already present in each file.

- [ ] **Step 4: Build and run the full relevant test suite**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
```

Expected: clean build, all tests pass with IDENTICAL counts to before this change (pure refactor — the tests covering `CreateResourceHandler`, `UpsertResourceHandler`, `ConditionalUpsertResourceHandler`, `BulkUpdateService`'s native paths, several of which were written specifically to catch reuse-vs-rebuild regressions in earlier Phase 3 tasks, must all still pass unchanged — if any of them fail, that's a signal the refactor changed behavior, not just location, and must be investigated before proceeding).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Extract RebuildResourceElement helper (Fable Phase 3 review follow-up)

Centralizes the mutate-then-rebuild sequence duplicated at 4 call sites
(CreateResourceHandler, UpsertResourceHandler, ConditionalUpsertResourceHandler,
BulkUpdateService) into one helper whose doc comment is the canonical,
discoverable home for the reuse-vs-rebuild rule -- the exact bug class
Phase 3 Task 2 shipped and had to fix after review. Pure refactor, no
behavior change; verified via the existing native-path regression tests."
```

---

### Task 2: Documentation follow-ups

**Files:**
- Create: `docs/features/sdk-migration/node-mutation.md`
- Modify: `docs/features/sdk-migration/provider-map.md` (link the new doc)
- Modify: `docs/features/sdk-migration/ignixa-upstream-gaps.md` (add tripwire + SDK-version + resolution-action columns)
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/Upsert/BulkUpdateService.cs` (one-line dormancy comment)

**Interfaces:** None — documentation and a comment only.

- [ ] **Step 1: Write the node-mutation rule doc**

Create `docs/features/sdk-migration/node-mutation.md`:

```markdown
# Rule: Mutating a Node-Backed ResourceElement

`ResourceElement.ResourceInstance` can hold either of two different shapes when a resource is
Ignixa-node-backed, and the difference matters after mutation:

- **Bare `ResourceJsonNode`** (set by `FhirModule`'s DB-read deserializer) -- does NOT implement
  `IResourceElement`, so `ResourceElement.LastUpdated`/`.ToPoco()`/`.Instance` fall back to a cached
  `ITypedElement` snapshot captured at construction. That snapshot goes stale after the underlying
  node is mutated in place.
- **`IgnixaResourceElement`** (set by the 4 handler call sites via `RebuildResourceElement`, and by
  the input formatter) -- implements `IResourceElement`, so scalar reads go straight to the live node
  and stay fresh after mutation.

`GetIgnixaNode()` deliberately accepts both shapes (it only needs the raw node), which is why this
hazard is invisible at the type level -- nothing stops you from mutating a node and returning the
original `ResourceElement`, and the compiler will not warn you if that turns out to be wrong for your
specific consumer.

## The rule

After mutating a `ResourceJsonNode` held by a `ResourceElement`:

- **Default: rebuild.** Call `resourceJsonNode.RebuildResourceElement(schemaContext)`
  (`IgnixaResourceElementExtensions.cs`) and use the returned value, not the original `ResourceElement`.
- **Reuse-in-place is permitted only when you have proven every downstream consumer of the value reads
  exclusively via `GetIgnixaNode()`** (never `.ToPoco()`, `.Instance`, `.Scalar()`, `.Predicate()`, or
  anything that could read the cached view). That proof must be written as a comment at the call site,
  not just asserted in a PR description -- see `TryAddSoftDeletedExtension`
  (`Extensions/ModelExtensions.cs`) for the pattern: its only consumer,
  `ResourceToNdjsonBytesSerializer.SerializeToJson`, is traced and the reasoning is recorded inline.

## History

This rule exists because Force-Ignixa Phase 3's Task 2 (native id-stamping in
`ConditionalUpsertResourceHandler`) shipped a real bug on first implementation: it reused a stale
wrapper after in-place mutation, and a downstream `UpsertResourceHandler.Handle` call read the
(stale, empty) id via `.ToPoco()`, which in the common no-id-in-request-body case would have silently
created a new resource with a random id instead of updating the matched one. Caught by task review,
fixed, independently re-verified. Two later tasks in the same plan (export's
`TryAddSoftDeletedExtension`, bulk-update's `StampLastUpdated`) each had to independently re-derive
this same consumer-trace reasoning -- this doc and the `RebuildResourceElement` helper exist so a
future task doesn't have to a fourth time.

See also [provider-map.md](provider-map.md) and
[docs/superpowers/plans/2026-07-09-force-ignixa-phase3.md](../../superpowers/plans/2026-07-09-force-ignixa-phase3.md)
(Task 2's brief/report/review history) for the full incident.
```

- [ ] **Step 2: Link it from provider-map.md**

Read `docs/features/sdk-migration/provider-map.md` first. Add a line near the top (following its existing style, alongside the existing "See ... for ..." line):
```markdown
See [node-mutation.md](node-mutation.md) for the rule on rebuilding vs. reusing a ResourceElement after mutating its underlying node.
```

- [ ] **Step 3: Enrich the upstream-gap tracker**

Read `docs/features/sdk-migration/ignixa-upstream-gaps.md` first. Add three columns to the existing table (`SDK version observed`, `Tripwire`, `On resolution`) and fill them in for the existing row (issue #320):

```markdown
| # | Issue | Gap | fhir-server workaround it blocks removing | SDK version observed | Tripwire | On resolution | Status |
|---|---|---|---|---|---|---|---|
| 1 | [ignixa-fhir#320](https://github.com/brendankowitz/ignixa-fhir/issues/320) | `ValidationDepth.Compatibility` doesn't run profile-tier checks (`CodeSystemPropertyTypeCheck`, `ValueSetIncludeSystemCheck`, `ValueSetFilterCheck`) -- `ValidationSchema.Validate` gates them to `Depth == Full` exactly, not `>= Spec`-style. `Compatibility` is meant to provide Firely-equivalent validation behavior, so this is a real gap, not intentional scoping. | `IgnixaResourceValidator`'s `ConformanceResourceTypes` exclusion list keeps `CodeSystem` and `ValueSet` permanently routed to the Firely fallback validator (see [provider-map.md](provider-map.md), US-11 / Phase 3 Task 3) | 0.6.7 | The two `Assert.DoesNotContain(compatibility.Issues, ...)` negative tests in `IgnixaResourceValidatorTests.cs` (Task 3 / US-11) will start FAILING once this gap closes -- that's the signal to act, not a flake. | Remove `CodeSystem`/`ValueSet` from `ConformanceResourceTypes` in `IgnixaResourceValidator.cs`, and update the two negative tests to assert the checks now DO catch invalid instances at Compatibility depth. | Open |
```

Update the doc's intro paragraph to mention the new columns' purpose in one sentence (the tripwire lets resolution be detected mechanically on the next SDK bump rather than by memory).

- [ ] **Step 4: Note Task 5's dormancy at the code site**

Read `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/Upsert/BulkUpdateService.cs`'s `StampLastUpdated` method (or wherever the native branch added by Phase 3 Task 5 lives — find it if the method was renamed/moved by Task 1 of this plan). Add a one-line comment directly above the native branch's `GetIgnixaNode()` check:
```csharp
// NOTE: this native branch is currently unreachable via the public API -- FhirPathPatchPayload.GetPatchedResourceElement
// always round-trips through a Firely POCO before this method runs, so GetIgnixaNode() is always null here today.
// It activates once the patch pipeline goes node-native; re-verify end-to-end via UpdateMultipleAsync at that point,
// not just via the reflection-invoked unit tests that currently cover it (see docs/features/sdk-migration/node-mutation.md).
```

- [ ] **Step 5: Commit**

```bash
git add docs/features/sdk-migration/node-mutation.md docs/features/sdk-migration/provider-map.md docs/features/sdk-migration/ignixa-upstream-gaps.md src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/Upsert/BulkUpdateService.cs
git commit -m "Document the reuse-vs-rebuild rule + enrich upstream-gap tracker (Fable Phase 3 review follow-up)

Adds node-mutation.md as the canonical home for the rule Phase 3's Task 2
bug (and Tasks 4/5's independent re-derivations of the same reasoning)
established. Adds a mechanical resolution tripwire + SDK version to the
upstream-gap tracker so ignixa-fhir#320 closing is detected by a failing
test, not by memory. Notes Task 5's dormancy at its actual code site."
```

---

## After This Plan

Fable's two remaining Phase 3 recommendations are deliberately NOT part of this plan and are scoped separately: (1) Task 6's XML content-negotiation gap (bare `ResourceJsonNode` returned before formatter selection, so a node-backed non-raw `ResourceElement` requesting XML would 406) — this must be addressed as part of Phase 4's design, since Phase 4's bundle work is what first sends non-raw node-backed elements through this path; (2) the candidate structural fix of eliminating the bare-`ResourceJsonNode`-as-`ResourceInstance` shape entirely (unifying on `IgnixaResourceElement`) — Fable explicitly flagged this needs its own design/blast-radius review, not to be folded into this cleanup plan.
