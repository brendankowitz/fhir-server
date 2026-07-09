# Investigation: Validation SDK Dependency (Ignixa vs Firely)

**Feature**: sdk-migration
**Status**: Complete
**Created**: 2026-07-08

## Approach

Assess whether the Ignixa SDK's validation capability — as merged upstream in ignixa-fhir PR #310
("Oracle-conformant FHIR validation", merged 2026-07-07, commit `64195494`) — is ready to replace
fhir-server's Firely-based validation, and what it would take to get there. This supports migration
objectives 3 (propagate abstractions everywhere Firely is still used directly) and 4 (ranked gap
plan), with a verdict framed against objective 2 (a flag that forces 100% Ignixa) and objective 5
(minimize Firely-interop shims except deliberately deferred gaps).

fhir-server has **two distinct validation surfaces**, and they are in very different states:

1. **Structural/attribute validation** (`IModelAttributeValidator`) — runs on every create/update.
   Already partially migrated: `IgnixaResourceValidator` exists on this branch and is registered as
   the primary implementation. But it is *additive*, not a replacement (see Evidence).
2. **Profile validation** (`IProfileValidator`) — runs for `$validate` and for
   `x-ms-profile-validation` / config-enabled writes. **100% Firely today**, built on the
   *deprecated* `Hl7.Fhir.Validation.Legacy.*` packages pinned at 5.11.0 (the rest of Firely is
   5.13.1 — the legacy validator was discontinued upstream at 5.11.0).

The proposed end-state: an `IgnixaProfileValidator : IProfileValidator` built on upstream's new
`PackageBackedValidator` composition (`Ignixa.PackageManagement`), fed by the server's DB-stored
conformance resources via an adapter, selected by the same Firely/Ignixa feature flag as the rest of
the migration, with the Firely `ProfileValidator` retained as the flag-Firely path during the parity
period.

## Tradeoffs

| Pros | Cons |
|------|------|
| Escapes the deprecated `Hl7.Fhir.Validation.Legacy` 5.11.0 dead-end (unmaintained, version-pinned below the rest of Firely) | fhir-server pins Ignixa **0.0.163** (2026-02-10); the validation capability ships in **0.6.7** — a ~5-month, all-packages upgrade is a prerequisite with its own regression surface |
| Upstream conformance is measured, not asserted: **zero over-strict**, 90.6% supported-scope vs the HL7 Java reference oracle (ADR 2607) | Snapshot generation M3 gaps (`contentReference` expansion, choice-`[x]` narrowing, re-slicing) — arbitrary user-POSTed profiles using those constructs validate incompletely (silently permissive) |
| Full profile validation is real: package-backed profile/extension/CodeSystem resolution, snapshot gen (M1+M2), slicing (value/pattern/exists/type), proven by offline bp-profile e2e + 33 US Core scenario tests | `profile`-discriminator slicing deferred upstream (`conformsTo()` is a stub) — skipped with an informational issue, never a false reject, but under-strict for IGs that use it |
| Never-over-strict design philosophy matches a server's worst-failure-mode priority (don't reject valid resources) | Terminology is membership-only; `$expand`/`$lookup` stubbed; intensional (compose.filter) ValueSets not locally decidable → warns where Firely's `LocalTerminologyService` (expansion up to `MaxExpansionSize`) can error. Behavior delta on binding errors |
| `IProfileValidator` seam already isolates the swap to one class + DI registration; `PackageBackedValidator` inputs are plain records constructible from DB resources | `PackageValidationOptions` is package-shaped (`ExtractedResource`); needs an adapter from `ServerProvideProfileValidation`'s DB-search source plus refresh/invalidation wiring (Firely validator today rebuilds every 30 min) |
| Native `IElement` validation removes the `ToTypedElement()`/`ToPoco()` Firely shims from the validation path (objective 5) | Ignixa `ValidationIssue` (severity/path/message) must be mapped to `OperationOutcomeIssue` (severity/code/diagnostics/expression) — issue-code fidelity needs a mapping table |

## Alignment

- [x] Follows architectural layering rules — `IProfileValidator`/`IModelAttributeValidator` seams already exist; upstream deliberately keeps `Hl7.Fhir.*` out of its core/package layers
- [x] Developer Experience (works with minimal setup) — everything needed is on nuget.org as **0.6.7** (`Ignixa.Validation`, `Ignixa.PackageManagement` both published); no source build required
- [x] Specification compliance — graded continuously against the official HL7 `fhir-test-cases` validator suite with the Java reference validator as oracle; exclusions frozen and auditable in upstream ADR 2607
- [ ] Consistent with existing patterns — profile sourcing is DB-backed here vs package-backed upstream; adapter + cache-invalidation wiring is new code, and dual-engine behavior during transition must be flag-controlled (it currently is not)

## Evidence

### 1. Current state in fhir-server (this branch)

#### Structural validation — Ignixa path exists, but Firely still runs on ~every resource

`IgnixaResourceValidator` **exists and is wired as primary**:

- `src/Microsoft.Health.Fhir.Shared.Core/Features/Validation/IgnixaResourceValidator.cs` — uses
  `Ignixa.Validation` (`StructureDefinitionSchemaBuilder.BuildSchema`, cached per resource type)
  with `ValidationDepth.Compatibility` + `SkipTerminologyValidation = true` (lines 97–101).
- Registered as the `IModelAttributeValidator` singleton in
  `src/Microsoft.Health.Fhir.Shared.Api/Modules/ValidationModule.cs:48-53`, with the Firely
  `ModelAttributeValidator` kept as fallback.
- Unit coverage exists: `Shared.Core.UnitTests/Features/Validation/IgnixaResourceValidatorTests.cs`.

However, three qualifiers matter for objectives 1/2/5:

1. **The Firely engine still executes on essentially every resource.** After a successful Ignixa
   validation, the code *still* calls the Firely fallback
   (`IgnixaResourceValidator.cs:182` — `return _fallbackValidator.TryValidate(value, ...)`), and
   `ModelAttributeValidator.TryValidate` round-trips through `value.ToPoco()` +
   `DotNetAttributeValidation` (`ModelAttributeValidator.cs:27-28`). Ignixa validation is currently
   a *pre-check*, not a replacement — the POCO/shim cost and Firely dependency remain on the write
   path.
2. **14 conformance resource types bypass Ignixa entirely** (`IgnixaResourceValidator.cs:51-67`:
   StructureDefinition, ValueSet, CodeSystem, SearchParameter, CapabilityStatement, ...) — routed
   to Firely because Ignixa did not (at 0.0.163) validate their nested types properly. Upstream
   PR #310 added CodeSystem/ValueSet/StructureDefinition-specific checks
   (`CodeSystemPropertyTypeCheck`, `ValueSetFilterCheck`, `ValueSetIncludeSystemCheck`, differential
   `eld-*` handling), so this list is re-testable after the package upgrade.
3. **No feature flag.** Registration is unconditional — there is currently no way to force the
   100%-Firely configuration (objective 1) or assert a 100%-Ignixa one (objective 2) for structural
   validation.

#### Profile validation — 100% Firely, on a deprecated package line

- `src/Microsoft.Health.Fhir.Shared.Core/Features/Validation/ProfileValidator.cs` is the only
  `IProfileValidator` implementation: Firely legacy `Hl7.Fhir.Validation.Validator`
  (`ProfileValidator.cs:101`) over
  `MultiResolver(CachedResolver(ZipSource.CreateValidationSource()), profilesResolver)`
  (`ProfileValidator.cs:57`), with `LocalTerminologyService` + ValueSet expansion capped by
  `MaxExpansionSize` (`ProfileValidator.cs:76-81`), `GenerateSnapshot = true`,
  `ResolveExternalReferences = false`, and a `cid-0` constraint-ignore for R4/R4B
  (`ProfileValidator.cs:83-99`).
- Profile source: `ServerProvideProfileValidation`
  (`Shared.Core/Features/Validation/ServerProvideProfileValidation.cs`) — DB-search over
  server-stored `StructureDefinition`/`ValueSet`/`CodeSystem` (line 41), cached, background-refreshed.
- Consumers: `ResourceProfileValidator` (create/update path; `x-ms-profile-validation` header +
  strict-handling severity escalation, `ResourceProfileValidator.cs:56-81`) and
  `ValidateOperationHandler` (`$validate`, with optional explicit profile,
  `ValidateOperationHandler.cs:47`). Both call `TryValidate(ITypedElement, string)` — a Firely
  `ITypedElement` seam, which for Ignixa-parsed resources is reached through the
  `Ignixa.Extensions.FirelySdk5` shim today.
- Package pins (`Directory.Packages.props`): `Hl7FhirVersion` **5.13.1** (line 5) but
  `Hl7FhirLegacyVersion` **5.11.0** (line 7) — the legacy validation packages were discontinued at
  5.11.0. Profile validation is therefore riding a frozen, deprecated validator regardless of the
  Ignixa question. This is an independent forcing function.

### 2. Upstream SDK state (ignixa-fhir `main` after PR #310)

Verified against the actual merged code at `origin/main` (`1f0f659d`, 2026-07-08) in the local
`E:\data\src\ignixa-fhir` checkout (read via `git show`; the checkout's working tree is on an
unrelated docs branch — do not trust it for validation state without fetching).

#### What shipped in PR #310 (`64195494`, merged 2026-07-07)

- **`PackageBackedValidator`** (`src/Core/Ignixa.PackageManagement/Validation/PackageBackedValidator.cs`)
  — the product entry point: composes `ProfileLayeredSchemaProvider` (package
  StructureDefinitions layered over a base `IFhirSchemaProvider`), `PackageCodeSystemSource` +
  `PackageValueSetSource` into an `InMemoryTerminologyService`, and a
  `StructureDefinitionSchemaResolver → CachedValidationSchemaResolver →
  ProfileAwareValidationSchemaResolver` chain. `ResolveForElement` performs `meta.profile`-aware
  resolution — exactly the shape `$validate` and write-path profile validation need.
- **Inputs are server-friendly.** `PackageValidationOptions.PackageResources` is
  `IReadOnlyList<ExtractedResource>` where `ExtractedResource` is a plain record
  (`ResourceType`, `Canonical`, `Version`, `ResourceId`, `ResourceJson`, `FhirVersion`) — trivially
  constructible from `ServerProvideProfileValidation`'s DB-sourced resources; no NPM package file is
  actually required. Options include `ExcludeBaseTypeStructureDefinitions` (don't let stored core
  StructureDefinitions shadow the generated base schema — directly relevant since fhir-server stores
  base-spec SDs) and `LayerPackageValueSets`.
- **Snapshot generation** (`Infrastructure/Snapshot/`): `ElementMerger` + `SnapshotGenerator` with
  recursive `baseDefinition` resolution and cycle detection. Per
  `docs/features/validation/investigations/differential-snapshot-generation.md`: **M1 shipped**
  (base merge; 100% match against shipped snapshots for 7 R4-core dual-form profiles, 296 elements)
  and **M2 shipped** (slice insertion incl. the US Core implicit extension-slicing pattern).
  **M3 not started**: `contentReference` expansion, choice-`[x]` narrowing, re-slicing,
  complex-datatype child expansion.
- **Slicing** (`Ignixa.Validation/Checks/SlicingCheck.cs`, 356 lines, + structured
  `DiscriminatorDefinition`/`SliceDefinition` types in `Ignixa.Abstractions/Structure/`): value,
  pattern, exists, and type discriminators enforced with per-slice min/max and closed/openAtEnd;
  `profile` discriminators **deferred** (emit informational `slicing-deferred`, never a false
  reject) pending `conformsTo()`.
- **Terminology surface**: new `ICodeSystemProvider` (`Ignixa.Abstractions/ICodeSystemProvider.cs`)
  — tri-state membership (`true`/`false`/`null` = not locally enumerable) and display resolution.
  The T1 severity decision is in effect: an unverifiable binding is a **warning**, never an error.
- **Conformance posture** (`docs/adr/adr-2607-validation-oracle-conformance.md`): R4 clean-base
  slice, 193 scored: **over-strict 0** (down from 54, every fix root-caused), raw 84.5%,
  **supported-scope 163/180 = 90.6%**. Over-strict = 0 is gated as a merge blocker upstream.

#### Publish status — the version gap is the headline

| Fact | Value |
|------|-------|
| fhir-server pin (`Directory.Packages.props:10`) | `IgnixaPackageVersion` = **0.0.163** |
| What 0.0.163 is | tag `release/0.0.163` → commit `e4f2e399`, **2026-02-10** — pre-dates the entire validation conformance effort (harness, snapshot gen, slicing, PackageBackedValidator) |
| First version containing PR #310 | **0.6.7** (tag `release/0.6.7` → `1f0f659d`, 2026-07-08; `git merge-base --is-ancestor` confirms `64195494` is included; 0.6.4 = `4acbf51f` does **not** include it) |
| On nuget.org? | **Yes** — `Ignixa.Validation` and `Ignixa.PackageManagement` both list 0.6.7 as latest (verified via the nuget.org flat-container index, 2026-07-08) |
| `Ignixa.PackageManagement` referenced by fhir-server? | **No** — absent from `Directory.Packages.props`; only the `ignixa/` reference app uses it. It must be added (packable, `net9.0;net10.0`, depends only on `Ignixa.Abstractions` + `Ignixa.Validation`) |
| API drift risk for existing code | Low for the validator itself: at 0.6.7 `StructureDefinitionSchemaBuilder.BuildSchema` only gained optional parameters, and `ValidationDepth.Compatibility`, `ValidationSettings.SkipTerminologyValidation`, `ValidationState.WithInstance` all still exist with the same shapes `IgnixaResourceValidator` uses. But the 0.0.163→0.6.7 jump moves **all eight** referenced Ignixa packages (~5 months of serialization/search/FhirPath/specification changes), so the upgrade needs a full-solution build + test pass as its own PR |

**Conclusion on publish status: the capability is published and consumable today, but fhir-server is
not pointed at it, and does not reference the package that hosts it.**

#### Do ADR 2607's exclusions matter for fhir-server?

| ADR 2607 exclusion / gap | Matters here? | Why |
|---|---|---|
| Remote terminology (SNOMED/LOINC membership) | **No** | fhir-server's Firely path is also local-only (`LocalTerminologyService`, `ResolveExternalReferences = false`). No behavior regression. |
| SNOMED-ECL / VS filter-expression parsing | **No** | Not exercised by the current Firely pipeline either. |
| SearchParameter static analysis | **No** (for validation) | Separate concern; fhir-server has its own SearchParameter validation path. |
| Digital signatures, `for-publication` mode | **No** | Never in a server validator's remit; not offered today. |
| Offline-resolution-blocked (10 cases) | **No** | An artifact of the upstream benchmark not vendoring IG packages; fhir-server *supplies* its conformance resources, so the capability (proven by the US Core scenario tests) is what counts. |
| Tracked feature gaps (7 cases: CodeSystem-membership binding, narrative-link resolution, `_category` shadow binding, ...) | **Marginal** | Individually narrow; all are under-strict (accept where reference rejects), so worst case is a missed error, not a false rejection. |
| Snapshot M3 (`contentReference`, choice narrowing, re-slicing) | **Yes — the main real gap** | fhir-server accepts arbitrary user-POSTed profiles; Firely's `GenerateSnapshot = true` handles these constructs today. Under Ignixa they'd validate incompletely (silently permissive). |
| `profile`-discriminator slicing | **Yes, minor** | Some IGs use it; upstream degrades to an informational skip (safe, but under-strict vs Firely). |
| Terminology expansion (`$expand` stubbed, intensional ValueSets) | **Yes, minor** | Firely can expand stored ValueSets (≤ `MaxExpansionSize`) and produce binding errors that Ignixa will only warn on. A deliberate severity-philosophy difference (never-over-strict), but a visible behavior delta for `$validate` consumers. |

### 3. Integration seams and effort shape

1. **Package upgrade (prerequisite for everything):** bump `IgnixaPackageVersion` 0.0.163 → 0.6.7,
   add `Ignixa.PackageManagement` to `Directory.Packages.props` and the Ignixa project. Regression
   surface = the whole existing Ignixa integration (serialization, search, FhirPath), not just
   validation.
2. **`IgnixaProfileValidator : IProfileValidator`:** build `PackageValidationOptions` from
   `ServerProvideProfileValidation`'s resources (adapter → `ExtractedResource`), call
   `PackageBackedValidator.Create(...)` with the version-appropriate `*CoreSchemaProvider`, validate
   via `SchemaResolver.ResolveForElement`/`GetSchema(profile)`, and map issues to
   `OperationOutcomeIssue` (severity/code/expression mapping table needed — upstream's
   `hapi-message-format` investigation is prior art). Set `ExcludeBaseTypeStructureDefinitions = true`
   so stored base-spec SDs don't shadow the generated schema. Mirror the existing 30-minute rebuild
   cadence (`ProfileValidator._validatatorRefresh`) for cache invalidation when stored profiles
   change.
3. **Flag gating (objectives 1 & 2):** both `ValidationModule` registrations
   (`IModelAttributeValidator`, `IProfileValidator`) become flag-selected. In flag-Ignixa mode,
   `IgnixaResourceValidator` must stop chaining into the Firely fallback on success
   (`IgnixaResourceValidator.cs:182`) and the conformance-type fallback list must be re-tested
   against 0.6.7 and shrunk or eliminated.
4. **Shim removal (objective 5):** `IProfileValidator.TryValidate(ITypedElement, ...)` is a
   Firely-typed seam. Long-term the interface should accept the server's resource abstraction so the
   Ignixa path validates `IElement` natively; short-term the `ToTypedElement()` shim stays for the
   flag-Firely path only.

## Verdict

**Non-blocker for objective 2, with one hard prerequisite and one deliberate deferral.**

- **Hard prerequisite (P0, severity HIGH):** the Ignixa package upgrade 0.0.163 → 0.6.7 plus adding
  the missing `Ignixa.PackageManagement` reference. Nothing about the new validation capability is
  reachable from fhir-server's current pins — every other validation conclusion is gated on this.
  The capability itself is published on nuget.org; no source-build or unreleased dependency is
  required.
- **Structural validation (P1, severity MEDIUM):** the Ignixa path already exists and is primary,
  but it is additive (Firely `DotNetAttributeValidation` still runs on ~every write via `ToPoco()`,
  and 14 conformance types bypass Ignixa). Flag-gate it, re-test the conformance-type exclusion list
  against 0.6.7, and make flag-Ignixa mode genuinely Firely-free on this path.
- **Profile validation (P1 to start, parity-gated to finish):** build `IgnixaProfileValidator` on
  `PackageBackedValidator` behind the flag. Upstream capability is credible — measured zero
  over-strict, 90.6% supported-scope, US Core proven, snapshot gen + slicing real — and the incumbent
  is a *deprecated, version-frozen* Firely legacy validator, so the migration direction is not in
  question.
- **Deliberate deferral under objective 5's carve-out (explicit):** keep the Firely
  `ProfileValidator` as the flag-Firely path *and the default* until an Ignixa implementation passes
  fhir-server's `ProfileValidatorTests` + `$validate` E2E corpus, **because** of three named
  upstream gaps: snapshot M3 (`contentReference`/choice-narrowing/re-slicing — the only gap that can
  make user-POSTed profiles silently under-validate), `profile`-discriminator slicing
  (`conformsTo()` stub), and expansion-based terminology binding severity. All three are
  under-strict-only (never false rejections), all are tracked upstream (roadmap Phases 2–3), and
  none blocks shipping the flag — they bound how long "flag-Ignixa = production-ready for profile
  validation" takes, not whether the architecture works.
- **Explicitly irrelevant:** ADR 2607's remote-TX / SNOMED-ECL / SearchParameter-analysis / dsig /
  for-publication exclusions match capabilities fhir-server's Firely pipeline never had
  (local-only terminology, no external resolution). They should not appear in the migration gap
  list.

**Recommendation:** do the 0.6.7 upgrade PR first and alone; land flag gating for
`IModelAttributeValidator` second; prototype `IgnixaProfileValidator` third with a parity harness
(run both validators, diff `OperationOutcome`s on the server's test corpus) so the deferral above
has a measurable exit criterion.
