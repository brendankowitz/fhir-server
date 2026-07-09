# ADR-2607: Ignixa Merge Readiness — SdkMode Flag, Gap Register, and Deferral Policy

**Status**: Proposed
**Date**: 2026-07-08
**Feature**: sdk-migration

## Context

The `feature/ignixa-sdk` branch integrates the in-house Ignixa SDK alongside the Firely SDK, aiming for: (1) a flag forcing 100% Firely, (2) a flag forcing 100% Ignixa, (3) SDK abstractions propagated everywhere Firely is still used directly, (4) a ranked plan of remaining gaps, and (5) minimal Firely-interop shims outside deliberate deferrals.

Five independent investigations (2026-07-08) audited the branch. Their decisive shared finding, confirmed three times independently and re-verified in code for this ADR: **the Ignixa MVC JSON formatters are registered but never selected at runtime.** `IgnixaFormatterConfiguration` (`IConfigureOptions<MvcOptions>`, `ServiceCollectionExtensions.cs:148-166`) inserts them at index 0, but `FormatterConfiguration` (`IPostConfigureOptions<MvcOptions>`, `FhirModule.cs:146-149`, `FormatterConfiguration.cs:36-46`) runs afterward and re-inserts the legacy Firely formatters ahead of them, and the legacy formatters claim every `[FromBody]`/result type controllers use. The HTTP boundary is therefore 100% Firely today; the test-readiness report's claim that Ignixa formatters were E2E-validated is falsified. Together with two smaller defects (the DB-read deserializer dropping the Ignixa node at `FhirModule.cs:127`, and `FhirResult` converting every response to a Firely POCO at `FhirResult.cs:162-176`), essentially all API traffic routes around the Ignixa integration. Separately, several Firely code paths ($import parsing, DB-read JSON deserialization, validator registration) were deleted or made unconditional, so neither a 100%-Firely nor a 100%-Ignixa configuration is currently wireable, and no flag of any kind exists.

## Options Considered

1. **Two independent booleans (`ForceFirely`, `ForceIgnixa`)** — *(rejected: admits an invalid both-true state and an ambiguous both-false state)*
2. **Per-request / per-tenant SDK switching** — *(rejected: every seam is a singleton DI registration or global `MvcOptions` mutation; scoped duplication doubles memory and the test matrix for no rollout benefit — deployments can ring by config)*
3. **Per-area flags (serialization/FHIRPath/validation independently selectable)** — *(rejected: YAGNI; combinatorial matrix is untestable, and Hybrid's data-driven fallbacks already give per-resource granularity)*
4. **Single `SdkMode` enum (`Hybrid`/`Firely`/`Ignixa`) consumed at the composition root** — *(chosen)*

## Decision

Adopt a single **`SdkMode` enum on `CoreFeatureConfiguration`** with values `Hybrid` (default, current Ignixa-first-with-Firely-fallback posture), `Firely` (objective 1), and `Ignixa` (objective 2), consumed only at module load by the four Shared.Api modules that register SDK seams — the exact `XmlFormatterFeatureModule` precedent. Mode changes require restart. Invalid flag combinations are unrepresentable by construction. This confirms the architecture all three flag-relevant investigations converged on ([dual-provider-feature-flags](investigations/dual-provider-feature-flags.md) Approach/Verdict).

Two subsidiary decisions:

- **Formatter ordering gets a single owner.** The `IConfigureOptions`-based `IgnixaFormatterConfiguration` is deleted; Ignixa formatters register `AsService<TextInputFormatter/TextOutputFormatter>` like every other formatter, and `FormatterConfiguration.PostConfigure` becomes the sole authority assembling the final order per mode, with an order-asserting unit test and a startup log line stating the effective mode. This fixes the headline defect rather than patching around it.
- **The flag selects between the two existing formatter stacks** rather than consolidating to a single formatter pair with an injected serializer strategy. Rationale: reversibility — both code paths stay warm during the parity period, and consolidation would rewrite the boundary twice when Firely is eventually removed. (This was the one decision the shim audit flagged for human ratification; this ADR ratifies the two-stack option.)

Components with data-driven fallback (`GetIgnixaNode() == null` branching in `RawResourceFactory`, NDJSON export, create/upsert handlers, `IgnixaResourceValidator`) keep that pattern and need **no per-callsite flag checks** — in Firely mode no producer attaches a node, so the fallback is automatic. This is the strongest part of the current design and is preserved deliberately.

### Gap and Blocker Register (objective 4)

Synthesized and de-duplicated across all five investigations. IDs are stable and referenced by [user-stories.md](user-stories.md). "Blocks" names the objective(s) blocked. Cross-investigation IDs (RC-n = dual-provider, S-n = shim audit) are noted for traceability.

| # | Gap / blocker | Severity | Blocks | Disposition | Source |
|---|---|---|---|---|---|
| G1 | Ignixa MVC formatters never selected (Configure vs PostConfigure ordering); HTTP boundary 100% Firely; RC-1/S1/S2 | **Critical** | 2 (and falsifies Hybrid's assumed behavior; masks 3, 5) | Fix now — becomes the flag seam | dual-provider §1; shim-audit §1; abstraction-audit §2.1; code-verified |
| G2 | No `SdkMode` flag; all Ignixa registrations unconditional | **Critical** | 1, 2 | Fix now (with G1) | abstraction-audit §2.1; dual-provider Approach |
| G3 | Ignixa output formatter has no raw-bundle (`RawBundleEntryComponent`/`BundleSerializer`) handling — a naive G1 fix empties every search/history response; S6 | **Critical** | 2 (co-requisite of G1) | Fix now, must ship with G1 | shim-audit §1 corollary |
| G4 | Bundle pipeline Firely end-to-end: search/history assembly (`BundleFactory`) and batch/transaction (`BundleHandler`); RC-3/S15/S17 | **Critical** | 2 | Fix (largest single work item); `BundleJsonNode` exists upstream | dual-provider §3; abstraction-audit §3.10; shim-audit S15/S17 |
| G5 | Ignixa package pin 0.0.163 (2026-02-10) predates all upstream validation work; `Ignixa.PackageManagement` unreferenced; capability ships in **0.6.7** on nuget.org | **Critical** (P0 prerequisite) | 2 (gates G9, G10) | Fix now — own PR, first and alone | validation-sdk-dependency §2, Verdict |
| G6 | `FhirResult.GetResultToSerialize()` POCO-izes every non-raw response (`FhirResult.cs:162-176`); RC-2/S7 | **Critical** | 2, 5 | Fix (with G1/G3) | dual-provider §2; shim-audit S7 |
| G7 | Force-Firely paths deleted/unconditional: $import parser gone (RC-4), DB-read JSON deserialization Ignixa-only (RC-5a), `IModelAttributeValidator` Ignixa-wrapped (RC-6a) | **High** | 1 | Restore/flag-gate behind `SdkMode` | dual-provider §4-6; abstraction-audit §2.1 |
| G8 | DB-read deserializer drops the Ignixa node (one-arg `ResourceElement` ctor, `FhirModule.cs:127`) — disables every downstream fast path for DB-sourced resources; RC-5b/S9 | **High** | 2, 5 | Fix now (one line) | shim-audit S9; dual-provider §5 |
| G9 | `IgnixaResourceValidator` always re-runs Firely on success (`IgnixaResourceValidator.cs:182`); 14 conformance types bypass Ignixa; RC-6b/S20 | **High** | 2 | Fix in Ignixa mode; re-test exclusion list against 0.6.7 | dual-provider §6; validation-sdk-dependency §1 |
| G10 | Profile validation ($validate, profile-gated writes) 100% Firely on **deprecated** `Hl7.Fhir.Validation.Legacy` 5.11.0; `PackageBackedValidator` unadopted | **High** | 2 (parity-gated); independent forcing function | Build `IgnixaProfileValidator` behind flag; default flip deferred (see Deferrals) | validation-sdk-dependency §1-3; abstraction-audit §3.6 |
| G11 | `IFhirPathProvider`/`ICompiledFhirPath`/`IModelInfoProvider`/`ResourceElement` contracts are Firely-typed (`ITypedElement`); per-evaluation double shim on search indexing; S12/S13 | **High** | 3, 5 | Contract decision + `IElement`-native path | abstraction-audit §2.2; shim-audit S12 |
| G12 | CRUD write handlers `ToPoco`→mutate→`ToResourceElement` on every write despite native `SetVersionId`/`SetLastUpdated` existing; S16 | **High** | 3, 5 | Mechanical rewrite after G1/G8 | shim-audit S16; abstraction-audit §3.13 |
| G13 | 34 `ITypedElementToSearchValueConverter` implementations + search-parameter definition management + `SearchParameterComparer`/`SupportResolver` raw Firely | **High** | 3 | Migrate after G11 contract lands | abstraction-audit §3.3 |
| G14 | Ad-hoc FHIRPath bypasses `IFhirPathProvider` (`ResourceElement.Scalar/Select/Predicate` used by ~25 files; global `FhirPathCompiler` symbol table; Ignixa path parses expressions uncached) | **High** | 3 | Route through provider; cache | abstraction-audit §3.4 |
| G15 | Operation endpoints typed as Firely `Resource`/`Parameters` ($validate, batch, $import, $member-match, ...) force the POCO bridge; no upstream `ResourceJsonNode`↔POCO bridge exists; S3 | **High→Medium** | 3 | Retype under objective 3; deliberate narrow bridge meanwhile | shim-audit S3, §5 |
| G16 | Parse-strictness parity untested: Firely `PermissiveParsing` vs Ignixa strictness — mode flips change which payloads are rejected; RC-9 | **Medium** | 1, 2 | Shared accept/reject corpus run under all three modes | dual-provider §9 |
| G17 | Conditional-reference resolution drops to POCO in create/upsert handlers | **Medium** | 2 | Ignixa-native rewrite (precedent in `ImportResourceParser`) | dual-provider §7 |
| G18 | `_summary`/`_elements` projection Firely-only (Ignixa formatter round-trips through Firely); S5 | **Medium** | 2 | **Deferral** — blocked on upstream subsetting (see Deferrals) | shim-audit S5, §5; abstraction-audit §3.1 |
| G19 | Patch (FHIRPath/JSON) Firely `ElementNode`/POCO end-to-end; S18 | **Medium** | 2 | **Deferral** — upstream `FhirPatchEngine` exists but unpackaged (see Deferrals) | shim-audit S18, §5; abstraction-audit §3.11 |
| G20 | XML pipeline 100% Firely; Ignixa has no XML support at all; XML output already crosses the Ignixa→POCO shim per request; S8 | **Medium** | 2 | **Deferral** — `SupportsXml=false` carve-out (see Deferrals) | xml-pipeline-ignixa-adoption Verdict; shim-audit S8 |
| G21 | Export/delete/bulk-update soft-delete-extension mutators POCO round-trip; S19/S21 | **Medium** | 3, 5 | Native JSON mutation after G8 | shim-audit S19/S21 |
| G22 | Terminology Firely-only behind `ITerminologyServiceProxy`; Ignixa `$expand`/`$lookup` stubbed upstream; S25 | **Medium** | 2 (if terminology enabled) | **Deferral** behind existing proxy (see Deferrals) | abstraction-audit §3.8; shim-audit S25, §5 |
| G23 | Conformance/CapabilityStatement built as Firely POCOs, no abstraction; S24 | **Medium** | 2 (cold, cached) | **Deferral** (see Deferrals) | abstraction-audit §3.7; shim-audit S24 |
| G24 | Anonymized export depends on external Firely-based `Microsoft.Health.Fhir.Anonymizer.*` packages | **Medium** | 2 (if anonymized export enabled) | **Deferral** — upstream dependency (see Deferrals) | abstraction-audit §3.12 |
| G25 | `RawResourceFactory` Firely fallback hit-rate unobservable — regressions like G8 can hide; S10 | **Low** | — (observability) | Instrument; fallback itself is permanent (it *is* the Firely-mode path) | shim-audit S10 |
| G26 | Dead/duplicated code: uncompiled drifting `Shared.Core/Ignixa/*` copies, orphaned `AddIgnixaPersistence` scaffolding, unused `FirelySdk6`/`Ignixa.Search` pins, duplicate standalone `Microsoft.Health.Fhir.Ignixa` types | **Low** (hygiene) | — | Delete/consolidate | abstraction-audit §2.3; shim-audit §3.5; dual-provider §8 |
| G27 | Low-traffic `ToPoco` sites: $member-match, $everything, group export, search-param admin, startup spec ingestion; S22/S23/S27 | **Low** | 3 (mechanical) | Burn down opportunistically after G12 patterns exist | shim-audit S22/S23/S27; abstraction-audit §3.14 |
| G28 | `IModelInfoProvider` + 4 version-specific Core projects — the single-assembly endgame | **Critical effort, last in order** | 3 (endgame) | Close only after G11-G14 and G4 | abstraction-audit §3.15 |

**Execution sequence** (per dual-provider Verdict, ratified here): (1) G1+G2+G3 — flag plus formatter fix makes all three modes *definable*; (2) G7 (+G16) — Firely mode to green CI gives rollback safety; (3) G5, G6, G8, G9 — Ignixa mode serves single-resource traffic natively; (4) G4 — bundles; (5) G10-G15, G17, G21 — objective-3 propagation; deferrals documented throughout; G28 last.

### Objective-5 deferrals (explicit carve-outs)

These remain on Firely (or on the interop shim) **deliberately**; each names its blocking condition and exit criterion. Everything not listed here is migration debt to burn down.

| Deferral | Why | Exit criterion |
|---|---|---|
| **XML pipeline** (G20) | Ignixa has zero XML support upstream (verified: no FHIR XML code in `Ignixa.Serialization`); a from-scratch schema-driven serializer is multi-week work for the lowest-traffic surface. A genuinely 100%-Ignixa deployment already exists via `SupportsXml=false` (spec-compliant 406). Ignixa mode + `SupportsXml=true` must warn/fail at startup. | Upstream `Ignixa.Serialization` ships bidirectional schema-driven XML |
| **`_summary`/`_elements` projection** (G18) | Only partial root-level `_elements` exists at Ignixa app layer; no `_summary` projection, no SUBSETTED tagging. Reimplementing subsetting server-side would duplicate an SDK concern. | Subsetting lands in `Ignixa.Serialization` proper |
| **FHIRPath/JSON Patch** (G19) | `FhirPatchEngine` (all five ops on `ResourceJsonNode`) exists upstream in `Ignixa.Application` but is not in any shipped package; patch semantics are subtle and volume is low. | Engine packaged (or port approved), then migrate |
| **Profile-validation default** (G10) | `IgnixaProfileValidator` gets built now, but Firely stays the default until parity: three named upstream gaps — snapshot M3 (`contentReference`, choice-`[x]` narrowing, re-slicing), `profile`-discriminator slicing (`conformsTo()` stub), expansion-based binding severity. All under-strict-only (never false rejections). | Passes `ProfileValidatorTests` + $validate E2E corpus via the dual-run parity harness |
| **Terminology** (G22) | The external contract *is* Firely's `ITerminologyService`; Ignixa terminology is membership-only with `$expand`/`$lookup` stubbed. Already isolated behind `ITerminologyServiceProxy`. | Ignixa terminology service parity |
| **Conformance/CapabilityStatement construction** (G23) | Cold, cached path; building it as raw JSON is worse than the POCO; `CapabilityStatementJsonNode` exists only at Ignixa app layer. | Typed conformance builders shipped in Ignixa packages |
| **Anonymized export** (G24) | External `Microsoft.Health.Fhir.Anonymizer.*` packages operate on Firely POCOs. | Upstream anonymizer migration |
| **Operation endpoints typed `Resource`/`Parameters`** (G15) | No `ResourceJsonNode`↔POCO bridge exists upstream; the lazy adapter + `ToPoco` is the cheapest available bridge until endpoints are retyped under objective 3. | Objective-3 endpoint retyping |
| **E2E test client on Firely `FhirClient`** | A Firely-based client is an independent cross-SDK conformance check of Ignixa server output — a feature, not debt. E2E is out of migration scope per the feature readme. | None (permanent) |

## Consequences

- **Positive**: invalid SDK-mode states are unrepresentable; `Firely` mode is a true rollback switch (registrations revert to the pre-branch graph with no per-callsite checks); formatter order becomes owned, tested, and logged instead of an options-phase race; each remaining shim becomes an explicit flag-gated seam; the honest state of the migration ("Ignixa serves zero HTTP traffic today") is now documented and measurable (G25 instrumentation).
- **Negative / follow-up**: three modes triple the serialization CI matrix (mitigation: Hybrid on PR, Firely+Ignixa nightly); mode changes need restart (no per-request A/B); the deleted Firely $import parser must be resurrected from `main`; fixing G1 activates Ignixa paths E2E has never actually exercised, so a full E2E re-validation pass is mandatory before Hybrid's new behavior ships; flipping any mode changes parse-strictness behavior visible to clients (G16 corpus is the guard).
- **Supersedes stale claims**: the test-readiness report's "Ignixa formatters E2E-validated" (§5.2-5.3) and its STU3/R4B schema-provider bug (§2.1 — since fixed), and `ignixa-integration-investigation.md` §4-5 double-parse/triple-hop claims (since fixed), are superseded by the five 2026-07-08 investigations.
- **Ratified here, previously open**: two formatter stacks selected by flag (not a consolidated single stack); XML disposition is Option B (defer with `SupportsXml=false` carve-out), not build-now or drop.
- The implementation backlog derived from this register is [user-stories.md](user-stories.md).
