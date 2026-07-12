# Ignixa SDK Bump: 0.6.7 → 0.6.19

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bump the pinned Ignixa SDK version from `0.6.7` to `0.6.19`, fix the one breaking change (`BaseJsonNode.MutableNode` → `internal`), and close out `docs/features/sdk-migration/ignixa-upstream-gaps.md` row 1 (`ignixa-fhir#320`), whose fix landed in this release exactly as the tracker's own "On resolution" instructions anticipated.

**Design provenance:** Investigated by a Fable (principal-coding-agent) pass that confirmed this is a single real release (0.6.8–0.6.18 were CI-internal, never shipped — the version gap is not 12 separate releases), audited every commit in the range for relevance, and found every `MutableNode` call site in this repo with its exact fix. Findings are folded into the tasks below; consult the investigation's full report (referenced in `.superpowers/sdd/progress.md`) if a task needs more context than this plan provides.

## Global Constraints

- This repo enforces StyleCop alphabetical `using` ordering as a build error (`TreatWarningsAsErrors`).
- Build verification: `dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60` — `0 Warning(s)` beyond pre-existing. Pre-existing, unrelated, NOT-to-fix failures: the four `*.Tests.E2E` SDK-version environment failures, occasional transient Roslyn/MSBuild crashes (retry once).
- The `IMutableJsonNode` cast (`((IMutableJsonNode)node).MutableNode`) is the uniform fix pattern for every `MutableNode` call site — do not use `SetProperty(string, JsonNode?)` as an alternative even though it remains public, since it deep-clones already-parented values (a subtle aliasing behavior change versus direct mutation).
- Task 3 (closing gap row 1) is the one task in this plan with a real production behavior change (shifts CodeSystem/ValueSet validation authority from the Firely fallback to Ignixa's own schema validation in Ignixa mode) — this is the long-planned, documented intent of that gap row, not a new decision, but land it as its own reviewable commit, not bundled with the mechanical fixes.

---

### Task 1: Bump the version pin + restore + verify transitive deps

**Files:**
- Modify: `Directory.Packages.props`

**Interfaces:** None — package version bump only.

- [ ] **Step 1: Bump the pin**

Change `Directory.Packages.props`'s `<IgnixaPackageVersion>` from `0.6.7` to `0.6.19`. Update the comment near line 88 (the `Microsoft.Extensions.Logging.Abstractions` pin explanation, which references `Ignixa.Abstractions 0.6.7`'s requirement) to say `0.6.19` instead — re-verify the requirement still holds at 0.6.19 (it should, per the investigation's transitive-dependency check, but confirm).

- [ ] **Step 2: Restore and inspect the transitive closure**

```bash
dotnet restore Microsoft.Health.Fhir.sln
```

Read the resulting `src/Microsoft.Health.Fhir.Ignixa/obj/project.assets.json` (or any other convenient project's) to confirm the investigation's finding holds: no new or changed transitive dependencies for any of the 9 Ignixa packages this repo consumes. If restore complains about an unpinned transitive dependency needing an explicit `<PackageVersion>` under this repo's Central Package Management, add the pin — don't guess a version, use whatever `dotnet restore`'s error/warning specifies.

- [ ] **Step 3: Commit**

```bash
git add Directory.Packages.props
git commit -m "Bump Ignixa SDK 0.6.7 -> 0.6.19

Single real release (0.6.8-0.6.18 were CI-internal, never shipped).
Transitive dependency closure unchanged per investigation -- no new
Directory.Packages.props pins needed. The one breaking change
(BaseJsonNode.MutableNode -> internal) is fixed in the next task."
```

(Do not build/test yet — this commit alone will not compile, since Task 2 hasn't fixed the breaking `MutableNode` change. That's expected; Task 2 lands immediately after.)

---

### Task 2: Fix the `MutableNode` breaking change (13 call sites, 7 files)

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Extensions/ModelExtensions.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Import/IgnixaImportResourceParser.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/IgnixaBundleFactory.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/Bundle/IgnixaBundleSerializer.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Features/Resources/Bundle/IgnixaBundleEntryView.cs`
- Modify: `src/Microsoft.Health.Fhir.Ignixa.UnitTests/IgnixaReferenceScannerTests.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Resources/Bundle/IgnixaBundleSerializerTests.cs`

**Interfaces:** No public API changes — every fix is `((IMutableJsonNode)node).MutableNode[...]` in place of the now-inaccessible `node.MutableNode[...]`. `IMutableJsonNode` lives in `Ignixa.Serialization.SourceNodes`.

**Depends on:** Task 1 (won't compile without the version bump first).

- [ ] **Step 1: Read every call site first**

Read all 7 files' current `.MutableNode` usages in full (13 call sites total, per the investigation: `ModelExtensions.cs:137,141`; `IgnixaImportResourceParser.cs:107`; `IgnixaBundleFactory.cs:353`; `IgnixaBundleSerializer.cs:52,77,104`; `IgnixaBundleEntryView.cs:68,76,83,93`; `IgnixaReferenceScannerTests.cs:188`; `IgnixaBundleSerializerTests.cs:39,42`) — confirm these line numbers before editing, since other work may have shifted them since the investigation ran.

- [ ] **Step 2: Fix each site**

Apply the cast pattern uniformly. Specifics per the investigation (verify against the actual current code, this is a starting point not gospel):

- `ModelExtensions.cs` (`AddSoftDeletedExtensionNative`): hoist `JsonObject node = ((IMutableJsonNode)resource).MutableNode;` once at the top of the method, replacing both line-137/141 direct accesses.
- `IgnixaImportResourceParser.cs`: `((IMutableJsonNode)resource).MutableNode["meta"]`.
- `IgnixaBundleFactory.cs` (`ToIgnixaIssueComponent`'s `location` write): `((IMutableJsonNode)issueComponent).MutableNode["location"] = location;`. Add `using Ignixa.Serialization.SourceNodes;` in alphabetical order. Reword the nearby comment ("MutableNode escape hatch" → "IMutableJsonNode escape hatch").
- `IgnixaBundleSerializer.cs`: three call sites (`bundle.Skeleton`, `entry.Metadata`, `entry.ResourceNode`) — direct casts (the first two unconditional, the third inside its existing null-check branch). Add `using Ignixa.Serialization.SourceNodes;` (file currently has no Ignixa usings, per the investigation — confirm).
- `IgnixaBundleEntryView.cs`: four call sites with varying null-handling (`.MutableNode["contentType"]` guarded by a null-safe short-circuit; one inside an existing `!= null` check as a plain cast; one via `as IMutableJsonNode)?.MutableNode["data"]` preserving the existing null-conditional; one building `JsonObject requestNode = (_entry.Request as IMutableJsonNode)?.MutableNode;`) — match each site's EXISTING null-handling style exactly, don't simplify/change null semantics while fixing the cast. Add `using Ignixa.Serialization.SourceNodes;`. Update the file's remarks/doc comments that reference "public `MutableNode`" to reflect the new access pattern.
- `IgnixaReferenceScannerTests.cs` / `IgnixaBundleSerializerTests.cs`: same cast pattern; the latter already has the needed `using`.

- [ ] **Step 3: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -80
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.Ignixa.UnitTests/Microsoft.Health.Fhir.Ignixa.UnitTests.csproj --no-restore
```

Expected: clean build (0 warnings beyond pre-existing), and — per the investigation's #329 (`UnknownPropertyCheck` tightening) watch item — **some test failures here are expected and legitimate**, not a sign this task did something wrong. If any validation-related tests newly fail, do NOT treat this task as broken — that's Task 4's job to triage (stricter validation may legitimately reject fixtures that previously passed silently). For THIS task, only investigate a failure if it's clearly NOT validation-related (e.g., a compile error, a `MutableNode`-access-pattern regression, a null-reference from a cast site) — those genuinely are this task's responsibility to fix.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "Fix BaseJsonNode.MutableNode breaking change (0.6.19)

MutableNode became internal in 0.6.19 (was public in 0.6.7); raw JSON
node mutation now requires the IMutableJsonNode explicit-interface cast.
Fixed all 13 call sites across 7 files with the uniform cast pattern,
preserving each site's existing null-handling exactly. No behavior
change -- purely a compile-fix for the SDK's tightened access surface."
```

---

### Task 3: Close upstream gap row 1 (`ignixa-fhir#320`)

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Validation/IgnixaResourceValidator.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Validation/IgnixaResourceValidatorTests.cs`
- Modify: `docs/features/sdk-migration/ignixa-upstream-gaps.md`
- Modify: `docs/features/sdk-migration/provider-map.md` (if it references the exclusion-list routing)

**Interfaces:** `IgnixaResourceValidator.ConformanceResourceTypes` shrinks by two entries. This is the one production-behavior change in this plan — see Global Constraints.

**Depends on:** Task 2 (needs a clean build first to see whether the tripwire tests actually fail as predicted).

- [ ] **Step 1: Confirm the tripwire fires**

```bash
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
```

Read `IgnixaResourceValidatorTests.cs`'s two `Assert.DoesNotContain(compatibility.Issues, ...)` negative tests (around lines 239/257 per the investigation — re-verify). Confirm they now FAIL — this is the documented tripwire from `ignixa-upstream-gaps.md` row 1 firing exactly as designed, proving `CodeSystemPropertyTypeCheck`/`ValueSetIncludeSystemCheck`/`ValueSetFilterCheck` (and, per the investigation, a fourth marked check, `CodeSystemSupplementContentCheck`) now genuinely run at `Compatibility` depth. If they DON'T fail, STOP — the gap may not actually be resolved as expected, and this task should not proceed on an unverified premise; report BLOCKED with what you found instead.

- [ ] **Step 2: Narrow the exclusion list**

Read `IgnixaResourceValidator.cs`'s `ConformanceResourceTypes` (around lines 60-79 per the investigation). Remove `"CodeSystem"` and `"ValueSet"` from the list. Update the doc comment immediately above it that explains the exclusion rationale — it should no longer describe these two types as excluded, and should note (if it doesn't already) that this list's remaining members lack the same conformance-evidence backing that justified removing CodeSystem/ValueSet, per the pattern established in Phase 3 Task 3's original comment.

- [ ] **Step 3: Flip the two tests**

Change the two negative (`DoesNotContain`) tests to positive (`Contains`) assertions — they should now assert that Ignixa's own Compatibility-depth validation correctly catches the invalid CodeSystem/ValueSet instances these tests construct. Rename the test methods to reflect the new assertion direction (they were likely named something like `Given...OnlySurfacesAtFullDepth` — rename to reflect that it now also surfaces at Compatibility depth, or restructure if the test's Full-depth comparison is no longer the interesting part).

- [ ] **Step 4: Update the gap tracker and provider map**

In `docs/features/sdk-migration/ignixa-upstream-gaps.md` row 1: mark Status as `Resolved (0.6.19)` (or this repo's equivalent convention — check if any other row has ever been resolved for the exact wording pattern to use, otherwise use your judgment matching the table's existing style). Keep the row rather than deleting it — it's useful history.

Check `docs/features/sdk-migration/provider-map.md` for any text describing CodeSystem/ValueSet as permanently routed to the Firely fallback validator (per the US-11/Phase-3 note referenced in the gap tracker) — update it to reflect the new routing.

- [ ] **Step 5: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
```

Expected: clean build, the two flipped tests pass, no other validation test regresses (aside from any #329-related fixture fallout already triaged/deferred to Task 4).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Close ignixa-fhir#320: remove CodeSystem/ValueSet from Ignixa validation exclusion list

0.6.19 fixes ValidationSchema.Validate to run CodeSystem/ValueSet
profile-tier conformance checks at Compatibility depth, not just Full --
exactly the gap tracked in ignixa-upstream-gaps.md row 1. The two
tripwire tests (Phase 3 Task 3/US-11) failed as designed, confirming the
fix landed; flipped them to assert the checks now correctly catch
invalid instances. Shifts CodeSystem/ValueSet validation authority from
the Firely fallback to Ignixa's own schema validation in Ignixa mode
(Hybrid still dual-validates) -- the long-documented intended resolution
for this gap, not a new decision."
```

---

### Task 4: Triage `#329` (`UnknownPropertyCheck`) fallout + gap-tracker hygiene

**Files:** Not fixed in advance — depends on what Task 2/3's test runs actually surfaced.

**Depends on:** Tasks 2 and 3.

- [ ] **Step 1: Identify genuine #329-related failures**

Review the full test suite results from Tasks 2 and 3. Per the investigation: 0.6.19's `UnknownPropertyCheck` (a spec-tier check, runs at `Compatibility` depth) now (a) validates `id`/`meta`/`implicitRules`/`language`/`text`/`contained`/`extension`/`modifierExtension` per-type instead of blanket-allowing them (so bare-`Resource` types like `Binary`/`Bundle`/`Parameters` that don't declare `text`/`contained`/`extension` may now correctly reject fixtures that previously passed silently), and (b) reports null-valued unknown JSON members that were previously invisible to validation. Any NEW test failure (not already known/pre-existing per this repo's documented pre-existing-failure list) that traces to one of these two behaviors is legitimate stricter validation — the fixture/test data is wrong, not the SDK.

- [ ] **Step 2: Fix affected fixtures/tests, not the validator**

For each genuine failure found, fix the test's input data (remove the invalid/unknown property, or adjust the fixture to be spec-valid) rather than working around the validator. Do not weaken `IgnixaResourceValidator` or add a new exclusion to accommodate a fixture that's genuinely spec-invalid.

If zero new failures are found (the most likely outcome, per the investigation's own assessment that it couldn't identify a specific offender by inspection), report that explicitly — a clean result here is a good, valid outcome, not evidence you didn't look hard enough.

- [ ] **Step 2: Gap-tracker hygiene**

Update `docs/features/sdk-migration/ignixa-upstream-gaps.md` row 3 (`ignixa-fhir#340`, filed in Phase 4 Plan B Task 3): its "workaround" wording currently references raw access via the (now-internal) public `MutableNode` — refresh it to describe access via the `IMutableJsonNode` cast instead, matching Task 2's fix pattern.

Confirm gap row 2's tripwire test (`IgnixaReferenceScannerTests`, the `Reference`-schema-collision guard from Phase 4 Plan B Task 1) still passes — per the investigation, `SchemaAwareElement.cs` was untouched in this version range, so it should be unaffected; this is a quick confirming check, not expected to require any fix.

- [ ] **Step 3: Full verification sweep**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -80
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.Ignixa.UnitTests/Microsoft.Health.Fhir.Ignixa.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.R4B.Core.UnitTests/Microsoft.Health.Fhir.R4B.Core.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.R5.Core.UnitTests/Microsoft.Health.Fhir.R5.Core.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.Stu3.Core.UnitTests/Microsoft.Health.Fhir.Stu3.Core.UnitTests.csproj --no-restore
```

All four FHIR versions must build/test clean (aside from the documented pre-existing E2E SDK-version failures).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "0.6.19 bump: triage UnknownPropertyCheck fallout, gap-tracker hygiene

[Describe what was actually found/fixed, or state explicitly that the
sweep was clean with zero new failures traced to the stricter
UnknownPropertyCheck behavior.] Refreshed gap row 3's workaround wording
to reference the IMutableJsonNode cast pattern (Task 2) instead of the
now-internal public MutableNode. Confirmed gap row 2's tripwire still
passes (SchemaAwareElement untouched in this version range)."
```

---

## After This Plan

The Ignixa SDK is current at 0.6.19. Gap row 1 is resolved. Gap rows 2 and 3 remain open (their causal SDK code was untouched by this bump). Resume Phase 4 Plan B at Task 4 (native recomposition) — this bump does not touch anything Plan B Task 3 built, so no re-verification of that task's own work is needed beyond the general full-suite sweep in Task 4 of this plan.
