# Investigation: Shim Minimization Audit — Firely↔Ignixa Interop Boundaries

**Feature**: sdk-migration
**Status**: Complete — verdict below
**Created**: 2026-07-08
**Branch audited**: `personal/bkowitz/ignixa-sdk-next-steps-fable` (tip of `feature/ignixa-sdk`, HEAD `3d8730828`)

## Approach

Catalog every Firely↔Ignixa interop shim, conversion, and fallback path in the **current** codebase; classify each as **ELIMINATE NOW** (native path exists or is trivial), **ELIMINATE LATER** (blocked on a named missing Ignixa SDK capability), or **PERMANENT BY DESIGN** (deliberate interop boundary); and rank each by the performance/complexity cost of keeping it. Every claim below was re-verified against code on this branch — the prior investigation docs (`ignixa-integration-investigation.md`, `ignixa-test-readiness-report.md`) are stale in several load-bearing places, documented in Evidence §6.

The audit method: exhaustive grep sweeps for `ToPoco`, `ToTypedElement`, `ToResourceElement`, `GetIgnixaNode`, `FirelySdk5`, `FhirJsonParser`/`FhirJsonSerializer` construction, and MVC formatter registration; manual trace of the four runtime pipelines (HTTP boundary, persistence write, persistence read, search indexing); production call-site classification by feature area and request-rate; and a capability survey of the Ignixa SDK checkout at `E:\data\src\ignixa-fhir`.

## Tradeoffs

Tradeoffs of the recommended direction (aggressively eliminate shims on hot paths; keep deliberate boundaries at cold/external edges):

| Pros | Cons |
|------|------|
| Removes hidden per-request POCO materialization on CRUD, search-index, and response paths — the shims currently negate most of Ignixa's measured 3x/10x wins outside `$import` | Fixing the formatter-ordering defect activates Ignixa code paths that E2E has **not** actually exercised (see Evidence §1) — requires a full E2E re-validation pass |
| A single, explicit interop surface (`Ignixa.Extensions.FirelySdk5` adapter) is auditable; scattered `ToPoco` call sites are not | Some eliminations require SDK features that do not exist yet (element subsetting, FHIRPath patch) — forcing them now would mean reimplementing SDK concerns in the server |
| Dead scaffolding removal (duplicate files, unused factories, unused package pins) reduces confusion for every future contributor | The `IFhirPathProvider` contract change (ITypedElement → IElement) ripples through the search converter layer — a large mechanical refactor |
| Directly enables objectives 1–2 (clean Firely-only / Ignixa-only flags), because each remaining shim becomes an explicit, flag-gated seam instead of an implicit fallback | Keeping XML/terminology/E2E boundaries on Firely indefinitely means the Firely package reference cannot be dropped until those are separately resolved |

## Alignment

- [x] Follows architectural layering rules (shim inventory respects Core/Shared.Core/Api layer boundaries; recommendations do not introduce upward dependencies)
- [x] Developer Experience (identifies dead code and misleading registrations that currently trap contributors)
- [x] Specification compliance (flags `_summary`/`_elements`/XML behaviors that must be preserved through any elimination)
- [x] Consistent with existing patterns (reuses the `XmlFormatterFeatureModule` feature-flag pattern as the model for the Firely/Ignixa boundary flag)

## Evidence

### 1. Headline finding: the Ignixa MVC formatters are dead code at runtime

The docs claim the Ignixa formatters are "registered at higher MVC priority" and the test-readiness report credits passing E2E to "the full HTTP pipeline with Ignixa formatters active." **Both claims are false on the current branch.**

Mechanism (all verified):

1. `AddIgnixaSerializationWithFormatters()` registers `IgnixaFormatterConfiguration` as **`IConfigureOptions<MvcOptions>`** and inserts the Ignixa formatters at index 0 — `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/ServiceCollectionExtensions.cs:103`, `:164-165`.
2. `FormatterConfiguration` is registered as **`IPostConfigureOptions<MvcOptions>`** — `src/Microsoft.Health.Fhir.Shared.Api/Modules/FhirModule.cs:146-149` — and inserts every DI-registered `TextInputFormatter`/`TextOutputFormatter` at indices 0..N: `src/Microsoft.Health.Fhir.Shared.Api/Features/Formatters/FormatterConfiguration.cs:38-46`.
3. The .NET options pipeline runs **all** `IConfigureOptions` before **any** `IPostConfigureOptions`. The legacy Firely formatters (registered `.AsService<TextInputFormatter>()` / `.AsService<TextOutputFormatter>()` at `FhirModule.cs:166-174`, plus the XML formatters from `XmlFormatterFeatureModule.cs:37-47`) therefore land **in front of** the Ignixa formatters.
4. The legacy `FhirJsonInputFormatter.CanReadType` claims `Resource` **and** `ResourceElement` (`src/Microsoft.Health.Fhir.Shared.Api/Features/Formatters/FhirJsonInputFormatter.cs:43-49`) — the complete set of controller `[FromBody]` types in the codebase (`FhirController.cs:169/193/246/266` bind `ResourceElement`; everything else binds `Resource`/`Parameters`). MVC selects the first formatter whose `CanRead`/`CanWrite` matches, so the legacy formatter always wins.
5. The legacy `FhirJsonOutputFormatter.CanWriteType` claims `Resource` + `RawResourceElement` — exactly what `FhirResult` produces (see §3). The Ignixa output formatter's extra types (`ResourceJsonNode`, `IgnixaResourceElement`) are never returned by any action result.

**Consequence:** the HTTP boundary is 100 % Firely today. Requests are parsed by `FhirJsonParser`, responses serialized by `FhirJsonSerializer`/`BundleSerializer`. Every downstream "Ignixa fast path" that is keyed off request-parsed resources (`GetIgnixaNode() != null`) never fires for API traffic. The passing BulkUpdate E2E runs validated the *Firely* pipeline plus the genuinely-active Ignixa paths (§2), not the Ignixa formatters.

**Corollary latent bug:** `IgnixaFhirJsonOutputFormatter` has **no** `RawBundleEntryComponent`/`BundleSerializer` handling (zero references in `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IgnixaFhirJsonOutputFormatter.cs`). `RawBundleEntryComponent.Resource` is only populated inside the legacy formatter's projection branch (`FhirJsonOutputFormatter.cs:92-107`); otherwise it stays null (`src/Microsoft.Health.Fhir.Shared.Core/Features/Search/RawBundleEntryComponent.cs:16-38`). If the ordering defect were "fixed" naively today, every search/history bundle would serialize with empty entries. Fixing the ordering and adding raw-bundle handling must land together.

### 2. What actually runs on Ignixa today

| Pipeline | Ignixa? | Evidence |
|---|---|---|
| `$import` parse → wrapper | ✅ Fully native | `ImportResourceParser.cs:39-75` — Ignixa parse, native meta mutation, native FHIRPath conditional-reference check, wrapper via `IgnixaResourceElementExtensions.Create` which preserves the node (`Microsoft.Health.Fhir.Ignixa/IgnixaResourceElementExtensions.cs:30-35,52-60`) |
| DB read → `ResourceElement` (JSON) | ✅ Parse only — **but drops the node** | `FhirModule.cs:108-129`: Ignixa parse + `ToTypedElement()` shim, then `new ResourceElement(ignixaElement.ToTypedElement())` — the **one-arg** ctor, so `ResourceInstance` is never set and `GetIgnixaNode()` returns null downstream |
| Search-index extraction (FHIRPath) | ✅ Engine native, ⚠️ contract shimmed | `IgnixaFhirPathProvider` registered via `AddIgnixaFhirPath` (`FhirModule.cs:106`), replacing the `FirelyFhirPathProvider` default (`SearchModule.cs:147`). But `ICompiledFhirPath.Evaluate` accepts/returns Firely `ITypedElement`, converting in (`ToIgnixaElement()`) and per-result out (`.ToTypedElement()`) — `IgnixaCompiledFhirPath.cs:49-69,124-127` |
| Resource validation (create/update) | ⚠️ Registered, never hit | `IgnixaResourceValidator` is the `IModelAttributeValidator` (`ValidationModule.cs:52`) but dispatches on `value.GetIgnixaNode()` (`IgnixaResourceValidator.cs:110`) — null for all API traffic (§1) and all DB reads (node drop above), so the Firely fallback runs ~100 % of the time |
| Export NDJSON | ⚠️ Fast path unreachable in the paths that matter | `ResourceToNdjsonBytesSerializer.cs:59-73` — fast path keyed on `GetIgnixaNode()`; the only callers that serialize elements (anonymized / soft-deleted / meta-not-set exports, `ExportJobTask.cs:814-846`) deserialize via `ResourceDeserializer` → node dropped → falls to `Instance.ToJson()` (Firely serialization over the FirelySdk5 adapter). Plain export writes raw JSON directly (fine, no shim) |
| HTTP parse/serialize | ❌ Firely (see §1) | — |
| API write persistence (`RawResourceFactory`) | ❌ Firely fallback (input is POCO-backed) | `RawResourceFactory.cs:41-54` — `TryGetIgnixaResourceNode` null for API traffic → `CreateFromFirely` (`:108-142`): `ToPoco<Resource>()` + `FhirJsonSerializer.SerializeToString` |
| Batch/transaction bundles | ❌ Firely end-to-end | `BundleHandler.cs:212` (`request.Bundle.ToPoco<Bundle>()`), `:795/:869` (per-entry `FhirJsonParser`) |
| Patch (FHIRPath + JSON) | ❌ Firely end-to-end | `FhirPathPatchPayload.cs:28-58` (`RawResource.ToITypedElement(...).ToPoco<Resource>()` → POCO patch builder → `ToResourceElement()`); `RawResourceExtensions.cs:26-37` is pure Firely (`FhirJsonNode.Read`); `JsonPatchPayload.cs:64` same shape |
| Storage layers (SQL / Cosmos) | ➖ No shim | Zero `Ignixa` references in `Microsoft.Health.Fhir.SqlServer` / `CosmosDb` — they consume `RawResource` strings; the raw-JSON contract is SDK-neutral |

### 3. Shim inventory

Legend: **Hot** = per-request/per-resource on CRUD/search/import/export; **Warm** = per-request on less common operations; **Cold** = startup/admin/error paths. Severity = cost of *keeping* the shim (perf + complexity + confusion). Full production call-site sweep: ~83 sites (test projects excluded).

#### 3.1 HTTP boundary

| # | Shim | Location | Mechanism | Heat | Severity | Classification |
|---|------|----------|-----------|------|----------|----------------|
| S1 | Legacy Firely formatters ahead of Ignixa (ordering defect) | `FormatterConfiguration.cs:38-46` vs `ServiceCollectionExtensions.cs:164-165`; `FhirModule.cs:146-149,166-174` | PostConfigure inserts legacy at index 0 after Configure inserted Ignixa at index 0 | Hot | **Critical** | **ELIMINATE NOW** — replace the dueling `Insert(0)` calls with one flag-controlled registration (model: `XmlFormatterFeatureModule`); this *is* the objective-1/2 feature-flag seam |
| S2 | Legacy `FhirJsonInputFormatter` claiming + wrapping `ResourceElement` | `FhirJsonInputFormatter.cs:43-49,121` | Firely parse → `model.ToResourceElement()` → POCO-backed element (no `ResourceInstance` node) | Hot | **Critical** (with S1) | **ELIMINATE NOW** — under the Ignixa flag this formatter must not claim `ResourceElement` |
| S3 | Ignixa input formatter → Firely `Resource` targets | `IgnixaFhirJsonInputFormatter.cs:160-187` | `IgnixaResourceElement.ToTypedElement().ToPoco<Resource>()` (adapter walk + POCO build; the old JSON-round-trip double-parse is already gone) | Hot once S1 fixed | High | **ELIMINATE LATER** — requires the `Resource`/`Parameters`-typed operation endpoints (`$validate`, batch/transaction, all `Parameters` operations: `ValidateController.cs:46/96`, `FhirController.cs:604/725`, `ImportController.cs:97`, `MemberMatchController.cs:49`, etc.) to move off Firely types — that is objective 3 scope. Until then this is the *deliberate* narrow bridge |
| S4 | Ignixa output formatter Firely-`Resource` write path | `IgnixaFhirJsonOutputFormatter.cs:163-168,236-249` | Direct Firely serialize to stream (single hop — old triple-hop fixed) | Hot once S1 fixed | Medium | **PERMANENT BY DESIGN** while any in-code Firely POCO responses exist (OperationOutcome, CapabilityStatement); revisit under objective 3 |
| S5 | Ignixa output formatter `_summary`/`_elements` projection round-trip | `IgnixaFhirJsonOutputFormatter.cs:137-150,178-183,251-260` | Ignixa serialize → Firely parse → Firely `SerializeAsync(summary, elements)` | Warm | Medium | **ELIMINATE LATER** — blocked on Ignixa element-subsetting/summary serialization (see §5) |
| S6 | Missing raw-bundle handling in Ignixa output formatter | `IgnixaFhirJsonOutputFormatter.cs` (absence); `RawBundleEntryComponent.cs:16-38` | n/a — correctness gap, not a hop | Hot once S1 fixed | **Critical** (blocker for S1) | **ELIMINATE NOW** — port the `BundleSerializer` raw-entry pass-through (or an Ignixa-native equivalent) before flipping S1 |
| S7 | `FhirResult.GetResultToSerialize()` → `ToPoco()` | `FhirResult.cs:162-167` (via `ResourceActionResult.cs:102`) | Every non-raw `ResourceElement` response becomes a Firely POCO before MVC sees it | Hot | High | **ELIMINATE NOW** (small): pass the `ResourceElement` through and teach the (flag-selected) output formatter to unwrap `GetIgnixaNode()`; neither formatter currently claims `ResourceElement` so this must land with S1/S6 |
| S8 | XML formatters (parse + serialize + DB XML deserialize func) | `FhirXmlInputFormatter.cs:76`, `FhirXmlOutputFormatter.cs:73,89`, `FhirModule.cs:131-137` | Firely XML end-to-end | Warm (low volume) | Low | **PERMANENT BY DESIGN (deferred)** — Ignixa has no XML pipeline (§5); XML stays Firely until the SDK grows one or XML support is formally dropped. Explicit deferral under objective 5's carve-out |

#### 3.2 Persistence

| # | Shim | Location | Mechanism | Heat | Severity | Classification |
|---|------|----------|-----------|------|----------|----------------|
| S9 | DB-read deserializer drops the Ignixa node | `FhirModule.cs:127` | One-arg `ResourceElement(ITypedElement)` ctor — `ResourceInstance` never set → `GetIgnixaNode()` null for **every** DB-read element | Hot | **High** | **ELIMINATE NOW** — use the internal two-arg ctor (as `IgnixaFhirJsonInputFormatter.cs:229` and `IgnixaResourceElementExtensions.cs:34` already do). One-line fix; re-activates the export, validator, and `RawResourceFactory` fast paths for DB-sourced elements |
| S10 | `RawResourceFactory.CreateFromFirely` fallback | `RawResourceFactory.cs:108-142` | `ToPoco<Resource>()` + Firely `SerializeToString` (single serialize — old triple-hop fixed) | Hot today (100 % of API writes, per §1); ~0 % after S1+S2+S9 | Medium | **PERMANENT BY DESIGN** as a fallback (Firely-only flag mode needs it; objective 1), but instrument it — a counter/log so fallback hit-rate is observable and regressions like S9 can't hide again |
| S11 | Unused Ignixa persistence scaffolding | `IgnixaRawResourceFactory.cs`, `IgnixaResourceDeserializer.cs`, `FhirServerBuilderIgnixaPersistenceRegistrationExtensions.cs` | `AddIgnixaPersistence()` is never called; `IIgnixaRawResourceFactory`/`IIgnixaResourceDeserializer` have zero production consumers | — | Low (confusion) | **ELIMINATE NOW** — delete, or wire in as the Ignixa-flag implementations; do not leave parallel dead abstractions |

#### 3.3 Search & FHIRPath

| # | Shim | Location | Mechanism | Heat | Severity | Classification |
|---|------|----------|-----------|------|----------|----------------|
| S12 | `IFhirPathProvider`/`ICompiledFhirPath` typed on Firely `ITypedElement` | `IFhirPathProvider.cs`, `IgnixaCompiledFhirPath.cs:49-69` | Per evaluation: `ITypedElement → ToIgnixaElement()` in; per result element: `.ToTypedElement()` out; plus Firely↔Ignixa `EvaluationContext`/resolver wrapping (`:132-179`) | **Hot** — every search parameter of every write/import/reindex | **High** | **ELIMINATE LATER** — requires an `IElement`-native indexer contract; ripples into `TypedElementSearchIndexer` (`TypedElementSearchIndexer.cs:80-99,106,163,283`) and the entire `ITypedElementToSearchValueConverter` family. This is the architectural core of objective 3. Mitigating fact (verified, §5): the FirelySdk5 adapters are lazy and identity-unwrapping in both directions, so once elements are Ignixa-backed (post S1/S2/S9) the per-evaluation cost drops to O(1) wraps — the contract change is then about type honesty and the converter layer, less about raw hops |
| S13 | `LightweightReferenceToElementResolver` returning `ITypedElement` | `LightweightReferenceToElementResolver.cs`; wrapped per-call at `IgnixaCompiledFhirPath.cs:160-175` | resolve() support crosses the boundary twice per resolution | Warm | Medium | **ELIMINATE LATER** — falls out of S12's contract change |
| S14 | `FirelyFhirPathProvider` default registration | `SearchModule.cs:147` (`TryAddSingleton`) | Firely engine kept as the DI fallback | — | Low | **PERMANENT BY DESIGN** — this is exactly the objective-1 (Firely-only) implementation; make the choice explicit via the same flag as S1 instead of registration-order coincidence |
| S15 | Search/history bundle assembly on Firely POCO skeleton | `BundleFactory.cs:43-57,120-139,226,244,268`; `RawBundleEntryComponent` | Bundle metadata built as POCOs; entries carry raw JSON; `OperationOutcomeIssue.ToPoco()` per issue; final `bundle.ToResourceElement()` per response | **Hot** — every search/history response | Medium (entry bodies already zero-copy via `BundleSerializer`) | **ELIMINATE LATER** — unblocked upstream: `BundleJsonNode`/`OperationOutcomeJsonNode` exist in `Ignixa.Serialization` (§5); acceptable meanwhile because the per-entry payloads never round-trip |

#### 3.4 Operations & handlers (call-site sweep, production only)

| # | Area (sites) | Representative locations | Heat | Severity | Classification |
|---|--------------|--------------------------|------|----------|----------------|
| S16 | CRUD write handlers (10) | `CreateResourceHandler.cs:61,113`, `UpsertResourceHandler.cs:96,111,149`, `ConditionalUpsertResourceHandler.cs:68,75`, `ResourceWrapperFactoryExtensions.cs:30`, `ProvenanceHeaderBehavior.cs:82` | **Hot** — `ToPoco` + mutate + `ToResourceElement` on every create/update (id/meta stamping) | **High** | **ELIMINATE LATER** (soon): the handlers mutate id/versionId/meta — `IgnixaResourceElement.SetVersionId/SetLastUpdated` and `ResourceJsonNode.Id` already support this natively; blocked only on S1/S2/S9 delivering node-backed elements into the handlers, then mechanical rewrite |
| S17 | Batch/transaction (4) | `BundleHandler.cs:212,232,271`, `FhirController.cs:725` | Hot | High | **ELIMINATE LATER** — bundle decomposition/recomposition on `ResourceJsonNode` requires native bundle APIs (§5); largest single consumer of Firely parse after the formatters |
| S18 | Patch pipeline (10) | `FhirPathPatchPayload.cs:36,46`, `JsonPatchPayload.cs:64,71`, `FhirPathPatch/Operations/*.cs` (6 sites) | Warm | Medium | **ELIMINATE LATER** — unblocked upstream: `FhirPatchEngine` (all 5 ops on `ResourceJsonNode`) exists in `Ignixa.Application` (§5); needs packaging or a port, then this deferral closes |
| S19 | Delete / bulk update (5) | `DeletionService.cs:533,604,606`, `BulkUpdateService.cs:648,652` | Warm | Medium | **ELIMINATE LATER** — same soft-delete-extension mutation as S21; native JSON mutation once elements are node-backed |
| S20 | Validation tiering (7) | `IgnixaResourceValidator.cs:105-155` (dispatch + conformance-type fallback), `ModelAttributeValidator.cs:27`, `ServerProvideProfileValidation.cs:305`, `ValidateController.cs:52,112,120,122` | Warm | Medium | Split: the `GetIgnixaNode()` dispatch and conformance-type Firely fallback are **PERMANENT BY DESIGN** for now (Ignixa.Validation doesn't cover conformance-resource nesting — deliberate, documented in the validator itself); `$validate` profile validation stays Firely until Ignixa.Validation reaches profile/slicing parity (§5) — **ELIMINATE LATER**, explicitly deferred |
| S21 | Export element overrides (3) | `ResourceToNdjsonBytesSerializer.cs:53` (`TryAddSoftDeletedExtension` POCO round-trip), `:72` (`Instance.ToJson()` fallback), `ExportJobTask.cs:822/835` | Warm (anonymized/deleted exports only) | Medium | **ELIMINATE NOW** (after S9): soft-delete extension add is a JSON mutation Ignixa already does in reverse (`ImportResourceParser.RemoveSoftDeletedExtension`, `ImportResourceParser.cs:105+`); mirror it |
| S22 | Member-match / $everything / group export (9) | `MemberMatchService.cs:112-123`, `PatientEverythingService.cs:324`, `GroupMemberExtractor.cs:103` | Cold/Warm | Low | **ELIMINATE LATER** — low traffic; convert opportunistically after S12/S16 patterns exist |
| S23 | Search-param management / reindex admin (6) | `SearchParameterStateUpdateHandler.cs:133,226`, `SearchParameterStateHandler.cs:125`, `SearchParameterFilterAttribute.cs:78`, `ReindexSingleResourceRequestHandler.cs:101`, `ReindexJobRecordExtensions.cs:119` | Cold | Low | **ELIMINATE LATER** (last) — admin-rate; POCO ergonomics genuinely help here |
| S24 | Conformance / capability / OperationDefinition (4) | `SystemConformanceProvider.cs:182,189,391`, `OperationDefinitionRequestHandler.cs:41` | Cold (cached) | Low | **PERMANENT BY DESIGN** until Ignixa offers typed resource *construction* (§5) — building CapabilityStatement as raw JSON is worse than the POCO |
| S25 | Terminology proxy (6) | `FirelyTerminologyServiceProxy.cs:98,293-328` | Cold | Low | **PERMANENT BY DESIGN** — the external contract *is* Firely's `ITerminologyService`; isolate, don't eliminate |
| S26 | Error/result rendering (4) | `OperationOutcomeExceptionFilterAttribute.cs:68`, `FhirResult.cs:112`, `JobResult.cs:39` | Cold | Low | **PERMANENT BY DESIGN** with S4 (in-code OperationOutcome POCOs) |
| S27 | Startup spec ingestion | `SearchParameterDefinitionBuilder.cs`, `CompartmentDefinitionManager.cs`, `VersionSpecificModelInfoProvider.cs` (`ToTypedElement` sites) | Cold (startup once) | Low | **ELIMINATE LATER** (with objective 3's `IModelInfoProvider` replacement); zero runtime cost today |

Notable negative results from the sweep: `UpdateId`/`UpdateVersion`/`UpdateLastUpdated` (`ModelExtensions.cs:79-104`) have **zero** production call sites — test-only; candidates for relocation to test utilities. The only production round-trip mutator is `TryAddSoftDeletedExtension` (S21).

#### 3.5 Dead code and package cruft

| Item | Evidence | Action |
|---|---|---|
| Duplicate uncompiled `Shared.Core/Ignixa/*` files | `Shared.Core.projitems:14-16` compiles only the two formatters + `ServiceCollectionExtensions` from that folder; `IgnixaResourceElement.cs`, `IgnixaJsonSerializer.cs`, `IIgnixa*.cs`, `IgnixaResourceElementExtensions.cs`, `FhirPath/*` there are byte-identical or **older drifted** copies (the dead `IgnixaCompiledFhirPath.cs` still uses the superseded `ToSourceNode().ToElement(schema)` conversion) of the live `src/Microsoft.Health.Fhir.Ignixa/` project files | **ELIMINATE NOW** — delete ~8 files; drift is already real |
| `Ignixa.Extensions.FirelySdk6` package pin | `Directory.Packages.props:153`; zero `PackageReference` consumers (`Microsoft.Health.Fhir.Ignixa.csproj:16-21` references FirelySdk5 only) | **ELIMINATE NOW** |
| `Ignixa.Search` package pin | `Directory.Packages.props:155`; zero consumers | **ELIMINATE NOW** (re-add when query translation work actually starts) |
| Test-only mock | `Substitute.For<IFhirPathProvider>` — 1 site (`SmartSearchTests`), already root-caused in the readiness report as the reindex-E2E failure source | Test fix, not a production shim |
| E2E test infrastructure on Firely `FhirClient` | `test/**` (out of scope per feature readme) | **PERMANENT BY DESIGN** — a Firely-based client is an independent conformance check of Ignixa server output; keeping it is a feature, not debt |

### 4. Interaction map — why three small defects mask everything

```
API write today:  Firely parse (S1/S2) → POCO-backed ResourceElement
                    → handler ToPoco/mutate/ToResourceElement (S16)
                    → RawResourceFactory Firely fallback (S10)
                    → search indexing: POCO ITypedElement → ToIgnixaElement (S12) → Ignixa eval → ToTypedElement per hit (S12)
                    → validator: GetIgnixaNode()==null → Firely fallback (S20)
API write after S1+S2+S6+S7+S9: Ignixa parse → node-backed element → native meta mutation (S16 rewrite)
                    → RawResourceFactory Ignixa serialize
                    → indexing: adapter unwrap (S12 mitigation) → Ignixa eval
                    → validator Ignixa fast path
```

S1 (ordering), S9 (node drop), and S7 (`FhirResult.ToPoco`) are the three levers; every High/Critical row above is either one of them or is unmasked by fixing them.

### 5. Ignixa SDK upstream capability check (`E:\data\src\ignixa-fhir`)

Surveyed at HEAD `a21805be` (checkout was on `docs/codegen-investigation`, a docs-only branch — statuses reflect effectively-main code). "App layer" = `Ignixa.Application` reference implementation, **not** shipped in the NuGet packages the fhir-server consumes — those capabilities exist as code to port or as packaging requests, not as referenceable APIs.

| Capability | Needed by | Upstream status |
|---|---|---|
| Element subsetting / `_summary` / `_elements` (incl. SUBSETTED tagging) | S5 | **PARTIAL, app layer only.** `Ignixa.Application/Features/Bundle/Serialization/ResourceElementsSerializer.cs` does streaming root-level `_elements` filtering with mandatory-field retention, but nested paths are copied unpruned; `_summary` is parsed (`Ignixa.Search/Models/SearchOptions.cs`) but only `Count` acted on — no InSummary-driven projection; SUBSETTED meta tag absent. **Feature request: subsetting in `Ignixa.Serialization` proper** |
| FHIRPath Patch on `ResourceJsonNode` | S18 | **EXISTS, app layer.** `Ignixa.Application/Features/Patch/FhirPatchEngine` — all five ops (add/insert/delete/move/replace), Parameters parsing, conditional patch, immutable-path guards; built on the SDK-core `IJsonNodeMutator`. **Unblocked: port or request packaging** |
| FirelySdk5 bridge mechanics | S3, S12 | **LAZY ADAPTERS, identity-unwrapping both directions.** `Ignixa.Extensions.FirelySdk5` compile-links FirelySdk6 sources (`FIRELYSDK5` define). `IElement.ToTypedElement()` → `TypedElementAdapter`; `ITypedElement.ToIgnixaElement()` → `IgnixaElementAdapter` with cached children; each unwraps the other, so round-trips don't stack and construction is O(1). **No serialize+parse anywhere.** But **no POCO bridge**: nothing converts `ResourceJsonNode` ↔ Firely `Resource`/`Base` — the S3 `ToPoco` cost (Firely POCO build over the adapter) has no cheaper SDK alternative today |
| XML parse/serialize | S8 | **ABSENT.** No FHIR XML anywhere in the SDK (only XHTML narrative sanitization). Genuine long-term feature request or a formal XML-stays-Firely decision |
| Validation parity | S20 | **SUBSTANTIAL; slicing ABSENT.** Profile validation via `StructureDefinitionSchemaBuilder` + `ProfileAwareValidationSchemaResolver` (meta.profile composition, IG layering), ~18 check types, terminology service (in-memory Expand/Lookup/Translate/Subsumes), 4 depth levels incl. the `Compatibility` migration mode this server uses. Discriminator slicing explicitly out of scope (per-slice constraints silently ignored). `$validate` hardening actively landing upstream (d694219c) |
| Native bundle / OperationOutcome / conformance construction | S15, S17, S24, S26 | **EXISTS.** Typed JsonNode builders in `Ignixa.Serialization/Models/`: `BundleJsonNode` (type/total/link/entry), `OperationOutcomeJsonNode` (full issue model), `ParametersJsonNode`, plus `CapabilityStatementJsonNode` at app layer. Sufficient to replace in-code Firely POCO construction |
| Firely `ISourceNode` bridging | S27 | **PARTIAL.** Firely `ISourceNode` → Ignixa exists (`SourceNavigatorAdapter`, `ToElement(ISchema)`); reverse direction only via `ToTypedElement()` + Firely's own conversion |
| In-progress upstream work on these gaps | — | **None** (last ~30 commits). Full `_summary`/`_elements`, XML, POCO bridge, and slicing are the genuine new-feature-request candidates |

### 6. Corrections to prior documents (verified stale claims)

| Prior claim | Reality on this branch |
|---|---|
| `ignixa-integration-investigation.md` §4.2/§5.1: input formatter double-parses (Ignixa serialize → Firely parse) | Fixed — now `ToTypedElement().ToPoco<Resource>()` (`IgnixaFhirJsonInputFormatter.cs:181-187`); the remaining cost is the adapter→POCO build, not a JSON round-trip |
| §5.2: output formatter triple-hop for Firely resources | Fixed — direct Firely stream write (`IgnixaFhirJsonOutputFormatter.cs:163-168`) |
| §5.3: `RawResourceFactory` triple-hop | Fixed — direct Firely serialize in the fallback (`RawResourceFactory.cs:128-133`) |
| §4.1 / readiness report §5.2: "Ignixa formatters registered at higher MVC priority… full HTTP pipeline with Ignixa formatters validated by E2E" | **False** — formatters are dead at runtime (Evidence §1); E2E validated the Firely boundary |
| Readiness report §2.1: STU3/R4B mapped to `R4CoreSchemaProvider` (critical bug) | Fixed — `IgnixaSchemaContext.cs:73-76` now maps `STU3CoreSchemaProvider`/`R4BCoreSchemaProvider` |
| §6.3: "No unit tests for Ignixa formatters / round-trip fidelity" | Fixed — `IgnixaFhirJsonOutputFormatterTests.cs`, `IgnixaSerializationRoundTripTests.cs`, `IgnixaFhirPathProviderTests.cs` exist (they unit-test the formatters directly, which is why the runtime ordering defect went unnoticed) |

## Verdict

**The dominant shim problem is not any single conversion — it is that three defects (S1 formatter ordering, S9 node drop, S7 `FhirResult.ToPoco`) route ~100 % of API traffic around the Ignixa integration, so every downstream fast path is dead and every published performance claim outside `$import` is unvalidated.** Objective 5 is therefore sequenced as: fix the three levers (with S6 as a co-requisite correctness fix), then burn down the unmasked hot-path shims, and hold four deliberate boundaries.

Ranked disposition (severity = cost of keeping):

| Rank | Shim | Severity | Classification |
|---|---|---|---|
| 1 | S1 formatter ordering (+ S2 legacy claim of `ResourceElement`) | **Critical** | ELIMINATE NOW — becomes the objective-1/2 feature flag |
| 2 | S6 missing raw-bundle handling in Ignixa output formatter | **Critical** (blocker for #1) | ELIMINATE NOW (must ship with #1) |
| 3 | S9 DB-read node drop (`FhirModule.cs:127`) | **High** | ELIMINATE NOW (one line) |
| 4 | S7 `FhirResult.ToPoco` | **High** | ELIMINATE NOW (with #1/#2) |
| 5 | S12/S13 `IFhirPathProvider` ITypedElement contract | **High** | ELIMINATE LATER (objective-3 core); NOW: adapter-unwrap mitigation |
| 6 | S16 CRUD handler `ToPoco`→mutate→`ToResourceElement` | **High** | ELIMINATE LATER (mechanical after #1–#4) |
| 7 | S17 batch/transaction Firely pipeline | High | ELIMINATE LATER (`BundleJsonNode` exists upstream — large server-side refactor is the cost) |
| 8 | S3 Ignixa input → Firely `Resource` targets | High→Medium | ELIMINATE LATER (objective-3: retype operation endpoints) |
| 9 | S10 `RawResourceFactory` Firely fallback | Medium | PERMANENT (as the Firely-flag path) + instrument hit-rate |
| 10 | S5 `_summary`/`_elements` projection round-trip | Medium | ELIMINATE LATER (blocked: SDK element subsetting) |
| 11 | S18 patch POCO pipeline | Medium | ELIMINATE LATER (unblocked: `FhirPatchEngine` exists upstream at app layer; needs packaging/port) |
| 12 | S15 bundle-factory POCO skeleton | Medium | ELIMINATE LATER (entry bodies already zero-copy) |
| 13 | S19/S21 delete/bulk-update/export mutators | Medium | ELIMINATE NOW-ish after #3 (native JSON mutation) |
| 14 | S20 validation tiering & `$validate` | Medium | PERMANENT dispatch + deferred profile validation (blocked: Ignixa.Validation parity) |
| 15 | S22/S23/S27 cold admin/startup sites | Low | ELIMINATE LATER (last) |
| 16 | S8 XML pipeline | Low | PERMANENT BY DESIGN (deferred — no SDK XML) |
| 17 | S24/S25/S26 conformance builders, terminology proxy, outcome rendering | Low | PERMANENT BY DESIGN (external contracts / in-code construction) |
| 18 | §3.5 dead files + package pins + unused persistence scaffolding | Low (pure confusion) | ELIMINATE NOW |
| 19 | E2E Firely `FhirClient` | — | PERMANENT BY DESIGN (independent conformance check) |

Explicit deferrals under objective 5's carve-out (each names its gap and upstream status): **XML** (Ignixa has no XML pipeline — absent upstream, needs a feature request or a formal XML-stays-Firely decision), **`_summary`/`_elements`** (only partial root-level `_elements` at Ignixa app layer; no `_summary` projection, no SUBSETTED tagging — feature request for `Ignixa.Serialization`), **FHIRPath patch** (engine exists upstream in `Ignixa.Application` but is not packaged — packaging/port request, then close), **profile `$validate`** (Ignixa.Validation is substantial but discriminator slicing is explicitly out of scope — parity gap), **terminology** (contract is Firely's `ITerminologyService`), **operation endpoints typed as Firely `Resource`/`Parameters`** (no `ResourceJsonNode`↔POCO bridge exists upstream; the adapter+`ToPoco` path is the cheapest available until endpoints are retyped under objective 3), **E2E client** (deliberate cross-SDK conformance check).

One architectural decision needs human ratification: whether the objective-1/2 flag selects between the *existing two formatter stacks* (fast, keeps both code paths warm) or consolidates to a *single formatter pair with an injected serializer strategy* (cleaner, but rewrites the boundary twice if Firely is later removed). This audit recommends the former for reversibility.
