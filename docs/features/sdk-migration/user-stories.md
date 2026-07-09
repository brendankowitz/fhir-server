# SDK Migration — User Stories Backlog

Derived from the gap register in [ADR-2607](adr-2607-ignixa-merge-readiness.md) (G-numbers below reference its table). Ordered by recommended execution sequence, which respects dependencies — not pure severity. Stories that cannot proceed until something ships in the ignixa-fhir SDK are in the final section.

Permanent-by-design items carry no story: the `RawResourceFactory` Firely fallback (it *is* the Firely-mode path), `FirelyFhirPathProvider` registration, in-code `OperationOutcome`/error rendering, and the Firely-based E2E `FhirClient` (deliberate cross-SDK conformance check).

---

## Phase 1 — Foundation: make the three modes definable

## US-1: Introduce `SdkMode` flag with single-owner formatter ordering

**Objective(s):** 1, 2, 4
**Severity:** Critical
**Blocked by:** none
**Source:** dual-provider-feature-flags §1/Approach (RC-1); shim-minimization-audit §1 (S1/S2); abstraction-propagation-gap-audit §2.1 (G1, G2)

Add `SdkMode` (`Hybrid`/`Firely`/`Ignixa`) to `CoreFeatureConfiguration`, consumed at module load by the Shared.Api modules. Delete the `IConfigureOptions`-based `IgnixaFormatterConfiguration` and make `FormatterConfiguration.PostConfigure` the sole authority assembling formatter order per mode — this fixes the defect where Ignixa formatters are registered but never selected. Done: an order-asserting unit test per mode, a startup log line stating the effective mode, and the legacy JSON formatter no longer claiming `ResourceElement` in Ignixa mode. Must land together with US-2.

## US-2: Raw-bundle handling in the Ignixa output formatter

**Objective(s):** 2
**Severity:** Critical
**Blocked by:** none (co-requisite of US-1 — the order flip must not ship without this)
**Source:** shim-minimization-audit §1 corollary (S6) (G3)

`IgnixaFhirJsonOutputFormatter` has no `RawBundleEntryComponent`/`BundleSerializer` handling, so fixing formatter ordering naively would serialize every search/history bundle with null entries. Done: the Ignixa output formatter passes raw entry JSON through (port of the `BundleSerializer` zero-copy splice or an Ignixa-native equivalent), covered by a search-response round-trip test.

## US-3: Ignixa package upgrade 0.0.163 → 0.6.7 + add `Ignixa.PackageManagement`

**Objective(s):** 2, 5
**Severity:** Critical (P0 prerequisite for all validation work)
**Blocked by:** none — ship first and alone as its own PR
**Source:** validation-sdk-dependency §2/Verdict (G5)

The current pin (0.0.163, 2026-02-10) predates the entire upstream validation effort; `PackageBackedValidator` ships in 0.6.7 (published on nuget.org) in `Ignixa.PackageManagement`, which fhir-server does not reference. The jump moves all eight referenced Ignixa packages (~5 months of changes). Done: `IgnixaPackageVersion` = 0.6.7, `Ignixa.PackageManagement` added, full-solution build and test pass as a standalone regression-scoped PR.

## US-4: Fix DB-read Ignixa node drop

**Objective(s):** 2, 5
**Severity:** High
**Blocked by:** none
**Source:** shim-minimization-audit S9; dual-provider-feature-flags §5 (RC-5b) (G8)

`FhirModule.cs:127` constructs `ResourceElement` with the one-arg ctor, so `ResourceInstance` is never set and `GetIgnixaNode()` returns null for every DB-read resource, silently disabling the export, validator, and `RawResourceFactory` fast paths. Done: two-arg ctor (as `IgnixaResourceElementExtensions.ToResourceElement()` already does) plus a regression test asserting DB-read elements carry the node.

## US-5: Instrument `RawResourceFactory` fallback hit rate

**Objective(s):** 5
**Severity:** Low
**Blocked by:** none
**Source:** shim-minimization-audit S10 (G25)

The Firely fallback in `RawResourceFactory` is permanent (it is the Firely-mode path), but its hit rate is unobservable — which is how the node-drop regression (US-4) hid. Done: a counter/log on the fallback path so Hybrid/Ignixa deployments can see what fraction of writes bypass Ignixa.

---

## Phase 2 — Force-Firely to green (objective 1, rollback safety)

## US-6: Restore the Firely `$import` parser behind the flag

**Objective(s):** 1
**Severity:** High
**Blocked by:** US-1
**Source:** dual-provider-feature-flags §4 (RC-4) (G7)

`ImportResourceParser` was rewritten in place to be Ignixa-only; no Firely implementation survives. Done: the `main` implementation restored and registered for `SdkMode.Firely` at `OperationsModule.cs:54`, with the Ignixa parser remaining for Hybrid/Ignixa; $import integration tests pass in Firely mode.

## US-7: Flag-gate DB-read JSON deserialization

**Objective(s):** 1
**Severity:** High
**Blocked by:** US-1
**Source:** dual-provider-feature-flags §5 (RC-5a); abstraction-propagation-gap-audit §2.1 (G7)

The JSON entry of the `ResourceDeserializer` dictionary (`FhirModule.cs:116-129`) is unconditionally Ignixa. Done: Firely mode restores `FhirJsonParser` + `SetMetadata` (the pre-branch behavior still present for XML), selected by `SdkMode`.

## US-8: Flag-gate validation, FHIRPath provider, and schema-context registrations

**Objective(s):** 1
**Severity:** High
**Blocked by:** US-1
**Source:** dual-provider-feature-flags §6/Verdict (RC-6a); abstraction-propagation-gap-audit §2.1 (G7)

Firely mode must register `ModelAttributeValidator` directly (not the Ignixa wrapper), skip `AddIgnixaFhirPath` (the `TryAddSingleton` Firely fallback at `SearchModule.cs:147` then applies), and keep `IIgnixaSchemaContext` registered but never eagerly constructed (its ctor throws for unsupported versions). Done: Firely-mode CI green across all four FHIR versions with zero Ignixa code on any request path.

## US-9: Parse-strictness parity corpus across all three modes

**Objective(s):** 1, 2
**Severity:** Medium
**Blocked by:** US-1 (needs all three modes wired: US-6..US-8 for Firely, US-10+ for Ignixa)
**Source:** dual-provider-feature-flags §9 (RC-9) (G16)

Firely parses with `PermissiveParsing = true`; Ignixa strictness has needed three recent fixes. Mode flips change which payloads are accepted — client-visible. Done: a shared accept/reject payload corpus executed under Hybrid, Firely, and Ignixa modes in CI, with intentional differences documented.

---

## Phase 3 — Force-Ignixa serves single-resource traffic natively (objective 2)

## US-10: Pass the Ignixa node through `FhirResult`

**Objective(s):** 2, 5
**Severity:** Critical
**Blocked by:** US-1, US-2
**Source:** dual-provider-feature-flags §2 (RC-2); shim-minimization-audit S7 (G6)

`FhirResult.GetResultToSerialize()` converts every non-raw `ResourceElement` response to a Firely POCO — for Ignixa-backed elements, a full node→POCO shim conversion per response. Done: in Ignixa/Hybrid mode the Ignixa node passes through so the output formatter serializes natively; `JobResult`/`OperationOutcome` filter POCOs are accepted (inventoried under objective 3).

## US-11: Ignixa-only structural validation in Ignixa mode

**Objective(s):** 2
**Severity:** High
**Blocked by:** US-1, US-3
**Source:** dual-provider-feature-flags §6 (RC-6b); validation-sdk-dependency §1/§3 (G9)

`IgnixaResourceValidator` always re-runs the Firely validator even on success (`IgnixaResourceValidator.cs:182`), and 14 conformance resource types bypass Ignixa entirely. Done: Ignixa mode skips the Firely re-validation; the conformance-type exclusion list is re-tested against 0.6.7 (which added CodeSystem/ValueSet/StructureDefinition checks) and shrunk or eliminated, with conformance evidence for the semantic delta.

## US-12: CRUD write handlers mutate metadata natively

**Objective(s):** 3, 5
**Severity:** High
**Blocked by:** US-1, US-4
**Source:** shim-minimization-audit S16; abstraction-propagation-gap-audit §3.13 (G12)

Create/upsert/conditional-upsert handlers do `ToPoco` → mutate id/versionId/meta → `ToResourceElement` on every write even when the resource arrived node-backed. `IgnixaResourceElement.SetVersionId`/`SetLastUpdated` and `ResourceJsonNode.Id` already support this natively. Done: node-backed resources flow through the handlers without POCO materialization; Firely-backed resources keep the existing path.

## US-13: Ignixa-native conditional-reference resolution

**Objective(s):** 2
**Severity:** Medium
**Blocked by:** US-12
**Source:** dual-provider-feature-flags §7 (G17)

`CreateResourceHandler`/`UpsertResourceHandler` drop to POCO whenever conditional references were resolved. `ImportResourceParser.CheckConditionalReferenceInResource` already walks references on nodes — mirror that as a reference rewrite. Done: conditional-reference writes stay node-backed end-to-end.

## US-14: Native soft-delete/extension mutation in export, delete, and bulk update

**Objective(s):** 3, 5
**Severity:** Medium
**Blocked by:** US-4
**Source:** shim-minimization-audit S19/S21 (G21)

`ResourceToNdjsonBytesSerializer.TryAddSoftDeletedExtension`, `DeletionService`, and `BulkUpdateService` round-trip through POCOs to add/remove the soft-deleted extension — a JSON mutation Ignixa already performs in reverse in `ImportResourceParser.RemoveSoftDeletedExtension`. Done: these paths mutate the JSON node directly when one is present.

---

## Phase 4 — Bundles (objective 2's largest work item)

## US-15: Ignixa-native search/history bundle assembly

**Objective(s):** 2
**Severity:** Critical
**Blocked by:** US-2, US-3
**Source:** dual-provider-feature-flags §3 (RC-3); abstraction-propagation-gap-audit §3.10; shim-minimization-audit S15 (G4)

`BundleFactory` builds every search/history response as a Firely `Bundle` POCO skeleton with raw-JSON entries. `BundleJsonNode` exists upstream in `Ignixa.Serialization` and can splice raw entry JSON without POCOs. Done: an `IBundleBuilder`-style abstraction with an Ignixa implementation used in Ignixa/Hybrid mode; search remains zero-copy for entry bodies; Firely mode keeps `BundleFactory`.

## US-16: Batch/transaction processing on Ignixa

**Objective(s):** 2
**Severity:** Critical (high effort)
**Blocked by:** US-15
**Source:** dual-provider-feature-flags §3 (RC-3); shim-minimization-audit S17 (G4)

Bundle POST binding was deliberately reverted to Firely (`FhirController.cs:725`) and `BundleHandler` decomposes/recomposes entries as POCOs — the largest Firely parse consumer after the formatters. Done: Ignixa-mode batch/transaction decomposes `BundleJsonNode` entries, routes them, and recomposes the response natively; Firely mode unchanged.

---

## Phase 5 — Objective-3 propagation

## US-17: `IElement`-native FHIRPath and indexer contract

**Objective(s):** 3, 5
**Severity:** High
**Blocked by:** US-1 (recommended after Phase 1; this decision dictates the shape of US-18/US-19)
**Source:** abstraction-propagation-gap-audit §2.2; shim-minimization-audit S12/S13 (G11)

`ICompiledFhirPath`/`IFhirPathProvider` are typed on Firely `ITypedElement`, forcing per-evaluation conversions in search indexing (one expression per search parameter per resource). Done: `IElement`-native overloads (or a parallel contract) so Ignixa-backed resources evaluate without crossing `ITypedElement`, including the reference-resolver seam; Firely path unaffected.

## US-18: Migrate search-value converters and definition management

**Objective(s):** 3
**Severity:** High
**Blocked by:** US-17
**Source:** abstraction-propagation-gap-audit §3.3 (G13)

The 34 `ITypedElementToSearchValueConverter` implementations, search-parameter definition management (`SearchParameterDefinitionBuilder`, `CompartmentDefinitionManager`, bundle wrappers), and `SearchParameterComparer`/`SearchParameterSupportResolver` (which construct `FhirPathCompiler` directly) all consume Firely types. Done: converters and definition management run on the US-17 contract; no direct `FhirPathCompiler` construction outside `FirelyFhirPathProvider`.

## US-19: Route ad-hoc FHIRPath through `IFhirPathProvider`

**Objective(s):** 3
**Severity:** High
**Blocked by:** US-17
**Source:** abstraction-propagation-gap-audit §3.4 (G14)

`ResourceElement.Scalar/Select/Predicate` call Firely extension methods directly (~25 consumer files), bypassing the provider; `IgnixaResourceElement`'s native evaluation parses expressions per call, uncached. Done: `ResourceElement` dispatches through `IFhirPathProvider` (natively when node-backed), expression parsing is cached, and the global `FhirPathCompiler.DefaultSymbolTable` mutation is removed.

## US-20: Build `IgnixaProfileValidator` behind the flag with a parity harness

**Objective(s):** 2, 3
**Severity:** High
**Blocked by:** US-3 (default flip additionally blocked by US-E4)
**Source:** validation-sdk-dependency §3/Verdict (G10)

Profile validation rides a deprecated, version-frozen Firely legacy validator (5.11.0). Build `IgnixaProfileValidator : IProfileValidator` on `PackageBackedValidator`: adapt `ServerProvideProfileValidation`'s DB resources to `ExtractedResource`, set `ExcludeBaseTypeStructureDefinitions = true`, map `ValidationIssue` → `OperationOutcomeIssue`, mirror the 30-minute refresh cadence. Done: selectable via `SdkMode`, plus a dual-run harness diffing both validators' `OperationOutcome`s over the test corpus — the measurable exit criterion for the deferral (US-E4).

## US-21: Retype operation endpoints off Firely `Resource`/`Parameters`

**Objective(s):** 3
**Severity:** Medium (High cost)
**Blocked by:** US-1, US-10
**Source:** shim-minimization-audit S3/§5 (G15)

$validate, batch/transaction, $import, $member-match, and other `Parameters` operations bind Firely types, forcing the adapter→POCO bridge on every call (no cheaper upstream bridge exists). Done: endpoints bind `ResourceElement`/node-friendly types; the POCO bridge remains only where a named deferral applies.

## US-22: Burn down low-traffic `ToPoco` call sites

**Objective(s):** 3
**Severity:** Low
**Blocked by:** US-12 (reuses its patterns)
**Source:** shim-minimization-audit S22/S23/S27; abstraction-propagation-gap-audit §3.14 (G27)

$member-match, $everything, group export, search-parameter state/reindex admin, and startup spec ingestion each carry a handful of POCO conversions on cold/warm paths. Done: converted opportunistically using established node-native patterns; remaining sites are inventoried with a one-line justification each.

## US-23: Delete dead and duplicated Ignixa code

**Objective(s):** 5
**Severity:** Low (hygiene, but drift is already real)
**Blocked by:** none
**Source:** abstraction-propagation-gap-audit §2.3; shim-minimization-audit §3.5; dual-provider-feature-flags §8 (G26)

Delete the 7 uncompiled, already-drifting duplicate files under `Shared.Core/Ignixa/`, the orphaned `AddIgnixaPersistence`/`IgnixaRawResourceFactory`/`IgnixaResourceDeserializer` scaffolding, and the unused `Ignixa.Extensions.FirelySdk6`/`Ignixa.Search` package pins; consolidate the standalone `Microsoft.Health.Fhir.Ignixa` project's duplicate type identities. Done: one definition per type, zero orphaned registrations, build green.

## US-24: Replace `IModelInfoProvider` and collapse version-specific projects (endgame)

**Objective(s):** 3
**Severity:** Low priority now / Critical effort
**Blocked by:** US-15, US-16, US-17, US-18, US-19 (close only after the layers above stop consuming `ITypedElement`)
**Source:** abstraction-propagation-gap-audit §3.15 (G28)

The Firely-shaped `IModelInfoProvider` forces the 28-directory version-specific project fan-out. Ignixa's single-assembly `FhirVersion`/schema-provider model is the replacement, but only after every layer above stops consuming Firely element types. Done: single assembly serves all FHIR versions; version-specific Core projects retired.

---

## Blocked on external ignixa-fhir SDK work

These are explicit objective-5 deferrals per ADR-2607; each names the upstream feature that unblocks it. File the corresponding ignixa-fhir work items now.

## US-E1: Native `_summary`/`_elements` projection

**Objective(s):** 2, 5
**Severity:** Medium
**Blocked by:** external: element-subsetting/`_summary` serialization (incl. SUBSETTED meta tag) in `Ignixa.Serialization` — today only partial root-level `_elements` exists in the `Ignixa.Application` app layer, not in any shipped package
**Source:** shim-minimization-audit S5/§5 (G18)

Both output formatters delegate projection to Firely `SerializeAsync`, and the Ignixa formatter round-trips Ignixa→Firely to do it. Done (once upstream ships): projection performed natively on `ResourceJsonNode`; the Firely round-trip removed.

## US-E2: FHIRPath/JSON Patch on `ResourceJsonNode`

**Objective(s):** 2, 5
**Severity:** Medium
**Blocked by:** external: packaging of `FhirPatchEngine` from `Ignixa.Application` into a shipped NuGet package (all five ops exist upstream at app layer; a supervised port into fhir-server is the fallback option)
**Source:** shim-minimization-audit S18/§5; abstraction-propagation-gap-audit §3.11 (G19)

The whole patch pipeline (FHIRPath Patch builder, JSON Patch, handlers) mutates Firely `ElementNode`/POCOs. Done (once unblocked): patch executes on the mutable JSON node in Ignixa mode; Firely engine retained for Firely mode during parity.

## US-E3: Ignixa XML pipeline

**Objective(s):** 2, 5
**Severity:** Medium
**Blocked by:** external: schema-driven bidirectional FHIR XML in `Ignixa.Serialization` — currently absent entirely (zero XML code upstream; the test-only XML→JSON helper is not a viable base)
**Source:** xml-pipeline-ignixa-adoption Verdict; shim-minimization-audit S8 (G20)

Deferred per ADR-2607 (Option B): XML stays Firely in all modes as a named exception; a strict 100%-Ignixa deployment uses the existing `SupportsXml=false` mechanism (406 for XML). Interim server-side work item: Ignixa mode + `SupportsXml=true` warns or fails at startup. Done (once upstream ships): Ignixa XML formatters registered in the same per-mode pattern as JSON.

## US-E4: Flip profile-validation default to Ignixa

**Objective(s):** 2
**Severity:** High (gates "production-ready" for objective 2)
**Blocked by:** US-20; external: snapshot-generation M3 (`contentReference` expansion, choice-`[x]` narrowing, re-slicing), `profile`-discriminator slicing (`conformsTo()` stub), and expansion-based terminology binding severity — all tracked upstream (roadmap Phases 2-3), all under-strict-only
**Source:** validation-sdk-dependency §2/Verdict (G10)

Firely remains the profile-validation default until the US-20 parity harness shows the Ignixa validator passing `ProfileValidatorTests` and the $validate E2E corpus. Done: default flipped; deprecated `Hl7.Fhir.Validation.Legacy` 5.11.0 packages removed.

## US-E5: Terminology operations on Ignixa

**Objective(s):** 2
**Severity:** Medium
**Blocked by:** external: Ignixa terminology parity — upstream is membership-only (tri-state `ICodeSystemProvider`) with `$expand`/`$lookup` stubbed and intensional ValueSets not locally decidable
**Source:** abstraction-propagation-gap-audit §3.8; shim-minimization-audit S25/§5; validation-sdk-dependency §2 (G22)

Deferred behind the existing `ITerminologyServiceProxy` seam; `FirelyTerminologyServiceProxy` remains the sole implementation in all modes. Done (once upstream ships): an Ignixa-backed proxy implementation selectable by `SdkMode`.

## US-E6: Anonymized export off Firely

**Objective(s):** 2
**Severity:** Medium
**Blocked by:** external: `Microsoft.Health.Fhir.Anonymizer.*` packages are Firely-POCO-based (separate repo, not ignixa-fhir)
**Source:** abstraction-propagation-gap-audit §3.12 (G24)

Anonymized export necessarily materializes Firely POCOs. Deferred as a named exception in Ignixa mode until the anonymizer supports a non-Firely input. Done: documented carve-out now; migration when upstream supports it.

---

**Totals:** 30 stories — 24 executable in this repo, 6 blocked on external work (5 on ignixa-fhir SDK features/packaging, 1 on the anonymizer packages).
