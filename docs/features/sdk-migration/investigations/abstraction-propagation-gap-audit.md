# Investigation: Abstraction Propagation Gap Audit

**Feature**: sdk-migration
**Status**: In Progress
**Created**: 2026-07-08
**Branch audited**: `personal/bkowitz/ignixa-sdk-next-steps-fable` (tip of `feature/ignixa-sdk` + validation fixes, HEAD `3d8730828`)

## Approach

Take the five abstractions introduced by the Ignixa integration work — `IFhirPathProvider`/`ICompiledFhirPath`, `IResourceElement`, `IIgnixaSchemaContext`/`IFhirSchemaProvider`, and `IIgnixaJsonSerializer` — and audit every remaining direct `Hl7.Fhir.*`/`Hl7.FhirPath` usage in `src/` that is *not* behind one of them. For each functional layer, determine:

1. Does an abstraction already exist that covers the layer (gap = propagation to more call sites)?
2. Or does no abstraction exist (gap = new interface design for objective 3)?

All counts and claims below were re-derived by grep against the current working tree; numbers in earlier docs (e.g. "400+/602+ files" in `readme.md`) are stale and are superseded by this audit.

## Tradeoffs

The implied strategy this audit supports — *propagate the existing abstractions to all call sites, adding new abstractions only where none exists, behind a Firely/Ignixa feature flag* — has these tradeoffs:

| Pros | Cons |
|------|------|
| Reuses proven interfaces (`IFhirPathProvider` already validated by CI across 4 versions) | Existing abstraction contracts are themselves Firely-typed (`ITypedElement`, `EvaluationContext`), so propagation alone does not remove Firely |
| Feature flag gives a safe rollback story (objective 1) and a hard-cutover story (objective 2) | Dual providers behind every abstraction doubles the test matrix until Firely is deleted |
| Layer-by-layer migration keeps PRs reviewable | Layers with no abstraction (Bundle, Patch, Conformance, Profile Validation) need net-new interface design, not just call-site edits |
| Shims (`Ignixa.Extensions.FirelySdk5`) let unmigrated layers keep working | Shims on hot paths (search indexing) currently pay a double conversion per FHIRPath evaluation |

## Alignment

- [x] Follows architectural layering rules (abstractions live in Core; implementations in Shared.Core/Ignixa; DI in Api modules)
- [x] Developer Experience (works with minimal setup — no new external services)
- [x] Specification compliance (FHIRPath 2.0, FHIR JSON round-trip fidelity already CI-validated)
- [ ] Consistent with existing patterns — **partially**: the duplicated `Microsoft.Health.Fhir.Ignixa` standalone project vs. `Shared.Core\Ignixa` shared items violates the single-definition pattern (see Evidence §2.3)

## Evidence

### 1. Headline numbers (re-derived 2026-07-08)

| Metric | Value | Old doc claim |
|---|---|---|
| Files in `src/` with `using Hl7.*` | **432** | "400+" |
| — production code (excl. `*UnitTests*`, `Tests.Common`, `TestUtilities`, `Shared.Tests`) | **235** | — |
| — test code | **197** | — |
| `using Hl7.*` directives in production code | **390** | "602+ usages" |
| Version-specific projects (Stu3/R4/R4B/R5 × Api/Client/Core/Web + UnitTests) | **28 directories** | "16 (4×4)" |
| Firely SDK version | **5.13.1** (`Directory.Packages.props:5`); legacy validation pinned to deprecated **5.11.0** (`:7`) | 5.11.4 |
| Ignixa package version | **0.0.163** (`Directory.Packages.props:10`) | 0.0.127 |

Production `using` directives by namespace (top): `Hl7.Fhir.Model` 121, `Hl7.Fhir.ElementModel` 98, `Hl7.FhirPath` 47, `Hl7.Fhir.Serialization` 33, `Hl7.Fhir.Rest` 26, `Hl7.Fhir.Utility` 18, `Hl7.Fhir.Specification.*` 15, `Hl7.Fhir.Introspection` 7, `Hl7.Fhir.Validation` 2.

Production files by project: `Microsoft.Health.Fhir.Core` 102, `Shared.Core` 59, `Shared.Api` 41, `Api` 6, `SqlServer` 5, `ValueSets` 4, `CosmosDb` 1, version-specific Core projects 4.

Conversion call sites in production code: `.ToPoco` **49** (34 files), `.ToResourceElement(` **47**, `.ToTypedElement(` **29**.

### 2. Cross-cutting findings (not specific to any one layer)

#### 2.1 There is NO feature flag — neither objective 1 nor objective 2 is currently wireable

All Ignixa registration is unconditional:

- `Shared.Api/Modules/FhirModule.cs:102` — `AddSingleton<IIgnixaSchemaContext, IgnixaSchemaContext>()`
- `FhirModule.cs:106` — `AddIgnixaFhirPath(...)`, whose implementation does `services.RemoveAll<IFhirPathProvider>()` and replaces the Firely provider (`Shared.Core/Ignixa/ServiceCollectionExtensions.cs:134-139`)
- `FhirModule.cs:180` — `AddIgnixaSerializationWithFormatters()` inserts Ignixa formatters at MVC position 0 (`ServiceCollectionExtensions.cs:164-165`)
- `FhirModule.cs:108-129` — the JSON entry of the `ResourceDeserializer` dictionary is Ignixa-only (parse via `IIgnixaJsonSerializer`, wrap via `IgnixaResourceElement.ToTypedElement()`); there is no Firely JSON deserialization path left for stored resources
- `Api/Modules/ValidationModule.cs:48-53` — `IgnixaResourceValidator` is the unconditional primary `IModelAttributeValidator`
- `Shared.Core/Features/Operations/Import/ImportResourceParser.cs:25-41` — $import parsing is Ignixa-only (no Firely fallback at all)

Grep for `Ignixa` across all `appsettings*.json` and `*Configuration*.cs`: **zero hits**. Nothing in `CoreFeatureConfiguration` or elsewhere can toggle the engine. The current tree is a **fixed hybrid**: Ignixa for JSON parse/serialize/FHIRPath/attribute-validation, Firely for everything else. Objective 1 (100% Firely) is therefore just as blocked as objective 2 (100% Ignixa) — several Firely paths (JSON resource deserialization from the DB, $import parsing) no longer exist to fall back to.

#### 2.2 The abstraction contracts are themselves Firely-typed

The propagated abstractions transport Firely interface types, so even fully-propagated layers keep a hard `Hl7.Fhir.Base` dependency and pay shim costs:

- `Core/Features/Search/FhirPath/ICompiledFhirPath.cs:38-55` — `Evaluate(ITypedElement, EvaluationContext)`, `Scalar<T>(ITypedElement, ...)`, `Predicate(ITypedElement, ...)`. Firely's `ITypedElement` and `EvaluationContext` are the contract.
- Consequence in the hot path: `Shared.Core/Ignixa/FhirPath/IgnixaCompiledFhirPath.cs:46-72` converts the incoming `ITypedElement` to Ignixa `IElement` **per evaluation** via `ToSourceNode().ToElement(_schema)` (`:127-133`) and converts every result back with `result.ToTypedElement()` (`:70`). Search indexing evaluates one expression per search parameter per resource, so every indexed write pays 2×-per-expression shim conversions even on the "native Ignixa" path.
- `Core/Models/IModelInfoProvider.cs:21-39` — `IStructureDefinitionSummaryProvider`, `GetEvaluationContext(Func<string, ITypedElement>)`, `ToTypedElement(ISourceNode)`, `ToTypedElement(RawResource)`: the central version-abstraction is Firely-shaped and implemented by the 4 version-specific Core projects.
- `Core/Models/ResourceElement.cs:29-49` — the universal resource wrapper *is* an `ITypedElement` holder (`Instance` property), and its `Scalar/Select/Predicate` (`:90-104`) call Firely's `Hl7.FhirPath` extension methods directly, **bypassing `IFhirPathProvider` entirely**. The `IResourceElement` fast path only covers `Id`/`VersionId`/`InstanceType`/`LastUpdated` (`:55-88`; `Core/Models/IResourceElement.cs:10-19` has only those 4 members).

#### 2.3 Duplicated and orphaned Ignixa code

- The standalone project `src/Microsoft.Health.Fhir.Ignixa/` (7 source files: serializer, resource element, schema context interface, FHIRPath provider) duplicates files under `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/` with **identical namespaces** (`Microsoft.Health.Fhir.Ignixa`, `Microsoft.Health.Fhir.Core.Features.Persistence`). The Shared.Core copies are the live ones (they add the formatters, `ServiceCollectionExtensions`, and compile into every version-specific Core assembly). The standalone project is referenced by `Microsoft.Health.Fhir.Api.csproj:29` yet a grep for `Ignixa` in that project's sources returns **zero hits** — it is dead weight whose duplicate types create a latent type-identity trap (same full name in `Microsoft.Health.Fhir.Ignixa.dll` and in each `*.Core.dll`).
- `Shared.Core/Features/Persistence/FhirServerBuilderIgnixaPersistenceRegistrationExtensions.cs:39` defines `AddIgnixaPersistence()` registering `IIgnixaRawResourceFactory` and `IIgnixaResourceDeserializer` — grep shows **zero call sites**. `IgnixaRawResourceFactory` and `IgnixaResourceDeserializer` are orphaned; the live persistence path is the dual-mode `RawResourceFactory` (§3.9).

### 3. Layer-by-layer inventory

Summary table (details follow):

| # | Layer | Abstraction exists? | State | Gap severity |
|---|-------|--------------------|-------|--------------|
| 1 | JSON serialization/formatters | Yes (`IIgnixaJsonSerializer` + formatters) | Propagated; Firely retained for POCO responses, `_summary`/`_elements`, legacy formatters | Medium |
| 2 | XML pipeline | **No** | 100% Firely (sibling investigation covers) | High (flag only here) |
| 3 | Search extraction/indexing | Yes (`IFhirPathProvider`) but Firely-typed | Propagated to indexer; 34 converters + definition mgmt still raw Firely | High |
| 4 | FHIRPath (general) | Yes (`IFhirPathProvider`) | Only the indexer uses it; all ad-hoc FHIRPath bypasses it | High |
| 5 | Validation (attribute) | Yes (`IModelAttributeValidator`) | Ignixa primary + Firely fallback | Low |
| 6 | Validation (profile/$validate) | Interface exists (`IProfileValidator`) but impl 100% Firely-legacy | Deprecated `Hl7.Fhir.Validation.Legacy.*` 5.11.0 (sibling investigation covers) | **Critical** (flag only here) |
| 7 | Conformance/CapabilityStatement | **No** | Firely POCO end-to-end | Medium |
| 8 | Terminology | Yes (`ITerminologyServiceProxy`) | Sole impl is Firely (`FirelyTerminologyServiceProxy`) | Medium |
| 9 | Bundle/transaction | **No** | Firely `Bundle` POCO end-to-end, incl. double-parse on input | **Critical** |
| 10 | Patch (all 3 kinds) | **No** | Firely POCO/`ElementNode` end-to-end | Medium |
| 11 | Export/Import | Yes (`IIgnixaJsonSerializer`) | NDJSON + $import fully Ignixa; group extraction + anonymizer Firely | Low |
| 12 | CRUD write handlers | Partial (`GetIgnixaNode()` fast path) | Still `ToPoco` per write before fast path | Medium-High |
| 13 | Other operations (Everything, MemberMatch, SearchParameterState, BulkUpdate, SMART, ConvertData) | Mixed | POCO-based where they touch resources | Low-Medium |
| 14 | Storage layer (SqlServer/Cosmos) | n/a | 6 files, trivial usages (enums/utility) | Low |
| 15 | Model-info/version routing | Yes but Firely-shaped (`IModelInfoProvider`) | 4 version-specific Core projects implement it | **Critical** (endgame) |

#### 3.1 JSON serialization / formatters — abstraction exists, propagation mostly done

Live path: `IgnixaFhirJsonInputFormatter`/`IgnixaFhirJsonOutputFormatter` at MVC priority 0. The Phase-3 anti-patterns from `ignixa-integration-investigation.md` §5 are **fixed** in current code:

- Input: controller expecting Firely `Resource` gets `ignixaElement.ToTypedElement().ToPoco<Resource>()` — a shim conversion, no JSON round-trip (`Shared.Core/Ignixa/IgnixaFhirJsonInputFormatter.cs:186`). Controllers expecting `ResourceElement` get the Ignixa-backed wrapper (`:229`).
- Output: Firely `Resource` POCOs are written directly by the Firely serializer, no triple-hop (`Shared.Core/Ignixa/IgnixaFhirJsonOutputFormatter.cs:163-167`).

Remaining Firely usage in this layer:

| Item | Evidence | Note |
|---|---|---|
| Firely `FhirJsonParser`/`FhirJsonSerializer` registered as concrete singletons and injected in ~15 classes | `Shared.Api/Modules/FhirModule.cs:55-63` | Concrete-type injection blocks a clean engine swap |
| Legacy `FhirJsonInputFormatter`/`FhirJsonOutputFormatter` still registered (lower priority) | `FhirModule.cs:166-174` | Would be the objective-1 path — but nothing selects them |
| `_summary`/`_elements` projection always serialized by Firely, even for Ignixa-parsed resources and raw DB resources (which are first `ToPoco`'d) | `IgnixaFhirJsonOutputFormatter.cs:236-246`; `Shared.Api/Features/Formatters/FhirJsonOutputFormatter.cs:94-121,162` | Ignixa `ResourceJsonNode` element-subsetting not implemented |
| `ResourceJsonNode` responses are converted to Firely before writing when projection is requested | `IgnixaFhirJsonOutputFormatter.cs:180-181,251` | |
| Controllers binding Firely `Resource` (forces POCO conversion): `FhirController.BatchAndTransactions` (`Shared.Api/Controllers/FhirController.cs:725`), `ValidateController.Validate`/`ValidateByIdPost` (`ValidateController.cs:46,96`) | | CRUD endpoints already take `ResourceElement` (`FhirController.cs:169,193,246,266`) |

**Verdict for layer**: abstraction exists; gap is (a) a flag to choose engine, (b) `_summary`/`_elements` on `ResourceJsonNode`, (c) retiring concrete Firely serializer injections.

#### 3.2 XML — no abstraction, 100% Firely (flagged only; sibling investigation covers depth)

`FhirXmlInputFormatter`, `FhirXmlOutputFormatter`, `NonFhirResourceXmlOutputFormatter` (`Shared.Api/Features/Formatters/`), plus the XML entry of the deserializer dictionary (`FhirModule.cs:131-137`) all use Firely `FhirXmlParser`/`FhirXmlSerializer`. No `IIgnixaXmlSerializer` equivalent exists. Any XML-format request bypasses Ignixa entirely, including persistence (`RawResource` stored via Firely XML path).

#### 3.3 Search parameter extraction / indexing — abstraction exists, but the pipeline below it is raw Firely

- `Core/Features/Search/TypedElementSearchIndexer.cs:34,112-114,213-215` consumes `IFhirPathProvider`/`ICompiledFhirPath` — propagation done at the top.
- **34 converter implementations** of `ITypedElementToSearchValueConverter` under `Core/Features/Search/Converters/` (36 files in the directory) consume Firely `ITypedElement` and its `Scalar()` extension directly (e.g. `CodeableConceptToTokenSearchValueConverter.cs`, `IdentifierToTokenSearchValueConverter.cs`). There is no `IElement`-native converter path; every extracted value flows through the Firely interface, forcing the `IgnixaCompiledFhirPath` result-side shim described in §2.2.
- The evaluation context comes from `IModelInfoProvider.GetEvaluationContext` (`TypedElementSearchIndexer.cs:77-80`) — Firely `EvaluationContext` with a Firely `ITypedElement` resolver (`IReferenceToElementResolver`).
- Search parameter *definition management* is raw Firely: `SearchParameterDefinitionBuilder`, `CompartmentDefinitionManager`, and the `BundleWrappers` (`BundleWrapper`, `BundleEntryWrapper`, `SearchParameterWrapper` — `Core/Features/Definition/`, 6 files) parse spec bundles via `ITypedElement.Select/Scalar`.
- Custom search parameter support still compiles with Firely directly, bypassing `IFhirPathProvider`: `Shared.Core/Features/Search/Parameters/SearchParameterComparer.cs:29,36` and `SearchParameterSupportResolver.cs:22` (`new FhirPathCompiler()`).

**Verdict for layer**: propagation gap at the bottom (converters, definition management, comparer/resolver), plus the Firely-typed contract problem (§2.2). A native-`IElement` converter interface (or converters keyed on Ignixa `IType`) is the missing abstraction.

#### 3.4 FHIRPath (general) — abstraction exists, used by exactly one consumer

`IFhirPathProvider` has two implementations (`FirelyFhirPathProvider` — `Core/Features/Search/FhirPath/FirelyFhirPathProvider.cs:23`; `IgnixaFhirPathProvider`) and one consumer (`TypedElementSearchIndexer`). Everything else evaluates FHIRPath through Firely extension methods on `ITypedElement`:

- `Core/Models/ResourceElement.cs:90-104` (`Scalar`/`Select`/`Predicate`) — used by ~25 production files (`KnownFhirPaths`, filters, handlers)
- `Core/Extensions/TypedElementExtensions.cs`, `Shared.Api/Features/Formatters/FormatParametersValidator.cs`
- Global symbol-table mutation: `FhirPathCompiler.DefaultSymbolTable.AddFhirExtensions()` at `FhirModule.cs:68,164` (twice)
- 47 production `using Hl7.FhirPath` directives overall

Note `IgnixaResourceElement` itself now evaluates natively (`Shared.Core/Ignixa/IgnixaResourceElement.cs:145-186` uses Ignixa `FhirPathParser`/`FhirPathEvaluator` on `IElement`, parsing the expression **per call, uncached**), but `ResourceElement` never routes to it — the Ignixa-backed `ResourceInstance` fast path only covers the 4 `IResourceElement` properties.

**Verdict for layer**: propagation gap — route `ResourceElement.Scalar/Select/Predicate` through `IFhirPathProvider` (and let it dispatch natively when `ResourceInstance` is `IgnixaResourceElement`), cache `IgnixaResourceElement`'s parsed expressions, and eliminate direct `FhirPathCompiler` usage.

#### 3.5 / 3.6 Validation — attribute validation migrated; profile validation 100% Firely (sibling investigation covers depth)

- `IModelAttributeValidator` → `IgnixaResourceValidator` primary with Firely `ModelAttributeValidator` fallback (`Api/Modules/ValidationModule.cs:42-53`). Recent commits on this branch (`8d96ee590`, `6d85841bf`, `3d8730828`) hardened its recursive/primitive validation.
- Profile validation (`$validate`, profile-constrained writes) is exclusively Firely legacy: `Shared.Core/Features/Validation/ProfileValidator.cs:10-14,30` uses `Hl7.Fhir.Validation.Validator` from the **deprecated** `Hl7.Fhir.Validation.Legacy.*` packages pinned at 5.11.0 (`Directory.Packages.props:7,54-57`) while the rest of the SDK is 5.13.1. `ServerProvideProfileValidation` (profile store) is Firely POCO-based. `Ignixa.Validation` is referenced in `Directory.Packages.props:157` but no production type uses it for profile validation.

#### 3.7 Conformance / CapabilityStatement — NO abstraction

`CapabilityStatementBuilder` (`Core/Features/Conformance/CapabilityStatementBuilder.cs`), `ICapabilityStatementBuilder`, `ConformanceProviderBase`, and the JSON converters (`Serialization/CodingJsonConverter.cs`, `EnumLiteralJsonConverter.cs`) build the capability statement over Firely model types (6 files with `Hl7.*` usings in the conformance folders); the intermediate `ListedCapabilityStatement` model is custom but serializes to a Firely `CapabilityStatement` `ITypedElement` for the response. Cold path, correctness-fine — but a real objective-3 gap because no interface isolates the capability document from the Firely model.

#### 3.8 Terminology — abstraction exists, single Firely implementation

`ITerminologyServiceProxy` (`Core/Features/Conformance/ITerminologyServiceProxy.cs`) is the abstraction; `FirelyTerminologyServiceProxy` (`Shared.Core/Features/Conformance/FirelyTerminologyServiceProxy.cs:12-17,56-61`) implements it over Firely's `ITerminologyService`/`LocalTerminologyService` and `Hl7.Fhir.Specification.Source` resolvers. `TerminologyRequestHandler` and `TerminologyController` sit above the abstraction. Whether Ignixa can supply terminology operations ($validate-code/$expand/$lookup semantics, ValueSet expansion) is **unverified** — no `Ignixa.Terminology` equivalent is referenced.

#### 3.9 Persistence raw-resource path — abstraction propagated (dual-mode)

`Shared.Core/Features/Persistence/RawResourceFactory.cs:41-55`: Ignixa fast path when `resource.GetIgnixaNode()` returns a node (`:47-51`, serializing at `:92`), Firely `ToPoco` fallback otherwise (`:108-110`). The old triple-hop is gone — the Firely fallback serializes with Firely only. `ResourceDeserializer`'s JSON path is Ignixa-only (§2.1). The orphaned `IgnixaRawResourceFactory`/`IgnixaResourceDeserializer` pair (§2.3) should be deleted or wired.

#### 3.10 Bundle processing / transactions — NO abstraction (hot path)

- `FhirController.BatchAndTransactions` binds Firely `Resource` (`Shared.Api/Controllers/FhirController.cs:725`) → input formatter POCO-converts the whole bundle.
- `BundleHandler` is Firely end-to-end: `request.Bundle.ToPoco<Hl7.Fhir.Model.Bundle>()` (`Shared.Api/Features/Resources/Bundle/BundleHandler.cs:212`), response bundles constructed as Firely POCOs (`:222,264`), all internals typed `Hl7.Fhir.Model.Bundle` (`:289,362,507,534,558,725`). 8 files in the Bundle folder.
- Search/history responses: `BundleFactory` (`Shared.Core/Features/Search/BundleFactory.cs:45-63`) builds Firely `Bundle` skeletons whose entries are `RawBundleEntryComponent` (raw-JSON-carrying Firely `EntryComponent` subclass), serialized by the custom `BundleSerializer`. Every search response therefore materializes a Firely object graph for the envelope even though entry payloads are zero-copy.
- Ignixa's mutable `ResourceJsonNode` could assemble bundle envelopes without POCOs, but no `IBundleBuilder`-style abstraction exists.

#### 3.11 Patch — NO abstraction (all three patch kinds)

16 files under `Shared.Core/Features/Resources/Patch/`:

- FHIRPath Patch: `FhirPathPatchPayload.cs` converts raw JSON → Firely POCO, and the whole engine (`FhirPathPatchBuilder`, `Operations/Operation*.cs`, `Helpers/ElementModelExtensions.cs`) mutates Firely `ElementNode`.
- JSON Patch: `JsonPatchPayload.cs` operates on POCO + Newtonsoft `JObject`.
- The handlers (`PatchResourceHandler`, `ConditionalPatchResourceHandler`) are POCO-typed.

Ignixa's mutable `ResourceJsonNode` was explicitly designed for efficient patch (per `complete-ignixa-replacement.md`), but nothing uses it here. This is the clearest **deliberate-deferral candidate**: correctness is proven on Firely, volume is low, and a rewrite is high-risk (FHIRPath Patch has subtle insert/move semantics).

#### 3.12 Export / Import — abstraction propagated on the hot paths

- Export NDJSON: `ResourceToNdjsonBytesSerializer` has the Ignixa fast path (`Shared.Core/Features/Operations/Export/ResourceToNdjsonBytesSerializer.cs`).
- Import: `ImportResourceParser` is **fully Ignixa** — parse, meta-stamping, index extraction input, and raw-resource creation all via `IIgnixaJsonSerializer`/`IIgnixaSchemaContext` (`Shared.Core/Features/Operations/Import/ImportResourceParser.cs:25-55`). Note: no Firely fallback — an objective-1 gap.
- Remaining Firely in this layer: `GroupMemberExtractor.ToPoco` (group export), `SqlExportOrchestratorJob.cs:13` (`Hl7.Fhir.Model` for group/filter types), and **anonymized export**, which depends on the external `Microsoft.Health.Fhir.Anonymizer.<ver>.Core` packages (`Microsoft.Health.Fhir.R4.Core.csproj` PackageReference) — those operate on Firely POCOs and cannot be migrated without upstream work. Deferral candidate.

#### 3.13 CRUD write handlers — partial fast path, still POCO-first

`CreateResourceHandler` (`Shared.Core/Features/Resources/Create/CreateResourceHandler.cs:61`) does `request.Resource.ToPoco<Resource>()` for meta/versioning logic, then re-wraps the Ignixa node for the outgoing element (`:97-112`). Same pattern in `UpsertResourceHandler`; `DeletionService`, `ConditionalUpsertResourceHandler`, `BulkUpdateService` are POCO-typed throughout (`.ToPoco` call-site list, §1). So every write still materializes a Firely POCO *once* even when the resource arrived through Ignixa. The gap: meta manipulation (`VersionId`, `LastUpdated`, soft-delete extension) can be done on `ResourceJsonNode` directly — `IgnixaResourceElement.SetVersionId/SetLastUpdated` already exist (`IgnixaResourceElement.cs:216-225`) but handlers don't use them as the primary path.

#### 3.14 Other operations

| Area | State | Evidence |
|---|---|---|
| SMART scopes | Light Firely use (types only) in `Api/Features/SMART/SmartClinicalScopesMiddleware.cs`, `Core/Features/Context/ScopeRestriction.cs` | Low |
| $member-match | POCO: `MemberMatchService.cs:112,119` (`ToPoco<Patient>`) | Low |
| $everything | POCO: `PatientEverythingService.cs:324` | Low |
| SearchParameterState / $status | POCO: `SearchParameterStateUpdateHandler.ToPoco` | Low |
| $convert-data | Uses `Microsoft.Health.Fhir.Liquid.Converter` — independent of Firely | None |
| Subscriptions / GraphQL | Not present in this codebase (no such dirs) | n/a |
| Storage (SqlServer/CosmosDb) | 6 files, trivial: `SqlServerFhirModel.cs:14` (`Hl7.Fhir.Model`), `SqlServerSearchService.cs:21`/`ImportOrchestratorJob.cs:16` (`Hl7.Fhir.Rest` — `SummaryType` etc.), `SqlServerFhirDataStore.cs:16` (`Hl7.FhirPath.Sprache` — likely vestigial), `CosmosQueueClient.cs:15` | Low |
| `ValueSets` project | 4 files referencing Firely coded types | Low |
| `Shared.Client` | 5 files — FHIR client library used by E2E tests (out of migration scope per readme) | n/a |

#### 3.15 Model-info / version routing — the endgame dependency

`IModelInfoProvider` + static `ModelInfoProvider` (Firely-shaped, §2.2) is implemented per version by the 4 `*.Core` projects referencing `Hl7.Fhir.STU3/R4/R4B/R5` 5.13.1. This is what forces the 28-directory version-specific project fan-out. Ignixa's single-assembly `FhirVersion`/schema-provider model (already live in `IgnixaSchemaContext`, `Shared.Core/Features/Persistence/IgnixaSchemaContext.cs:69-79` — the STU3/R4B wrong-provider bug from `ignixa-test-readiness-report.md` §2.1 is **confirmed fixed**) is the replacement, but retiring `IModelInfoProvider` requires every layer above to stop consuming `ITypedElement` first. This is the last gap to close, not the first.

### 4. Alternatives considered (future investigation candidates)

1. **Ignixa-native converter/second contract** — add `IElement`-based overloads to `ICompiledFhirPath` and an `IElement`-native search-value converter set, so the Ignixa path never touches `ITypedElement`. (Highest performance payoff; medium design effort.)
2. **Engine feature flag via DI composition** — a `FhirSdkEngine` config enum (Firely | Ignixa | Hybrid) consumed by `FhirModule`/`SearchModule`/`ValidationModule` to select formatter order, `IFhirPathProvider`, deserializer dictionary, and validator. (Prerequisite for objectives 1 and 2; small effort; requires restoring the Firely JSON deserialization and $import paths.)
3. **Bundle envelope builder abstraction** — `IBundleBuilder` producing either Firely `Bundle` or `ResourceJsonNode`, removing the largest remaining hot-path POCO materialization.

## Verdict

**The abstractions exist and are validated, but propagation stopped at the first consumer of each; four layers have no abstraction at all; and no engine flag exists, so neither objective 1 nor objective 2 is currently achievable by configuration.**

Severity-ranked gaps (blocker status is against **objective 2 — 100% Ignixa**):

| Rank | Gap | Severity | Obj-2 blocker? | Notes |
|---|---|---|---|---|
| 1 | No Firely/Ignixa feature flag; registrations unconditional; Firely JSON-deserialize and $import paths deleted (blocks **objective 1** too) | **Critical** | Yes (and blocks obj 1) | §2.1 — do this first; everything else lands behind it |
| 2 | Profile validation ($validate) 100% Firely on deprecated 5.11.0 legacy packages; `Ignixa.Validation` unused for profiles | **Critical** | Yes | §3.6 — sibling investigation owns the design |
| 3 | Bundle/transaction pipeline Firely POCO end-to-end (no abstraction) | **Critical** | Yes | §3.10 — hot path; needs new `IBundleBuilder`-style interface |
| 4 | `ICompiledFhirPath`/`IModelInfoProvider`/`ResourceElement` contracts are Firely-typed; Ignixa hot path pays per-evaluation double shim | **High** | Yes (contract-level) | §2.2 — decide: extend contracts with `IElement` overloads vs. new contracts |
| 5 | 34 search-value converters + definition management + `SearchParameterComparer`/`SupportResolver` raw Firely | **High** | Yes | §3.3 |
| 6 | Ad-hoc FHIRPath (`ResourceElement.Scalar/Select/Predicate`, `TypedElementExtensions`, global `FhirPathCompiler` symbol table) bypasses `IFhirPathProvider` | **High** | Yes | §3.4 |
| 7 | XML pipeline 100% Firely, no abstraction | **High** | Yes (unless XML support is descoped/deferred) | §3.2 — sibling investigation owns depth |
| 8 | CRUD write handlers POCO-first despite Ignixa fast path existing | **Medium-High** | Yes | §3.13 |
| 9 | Conformance/CapabilityStatement Firely POCO, no abstraction | **Medium** | Yes (cold path) | §3.7 |
| 10 | `_summary`/`_elements` projection Firely-only; concrete `FhirJsonSerializer`/`FhirJsonParser` singletons injected in ~15 classes | **Medium** | Yes | §3.1 |
| 11 | Terminology impl Firely-only behind existing `ITerminologyServiceProxy`; Ignixa capability unverified | **Medium** | Yes if terminology enabled | §3.8 — **recommend explicit deferral** behind the proxy until Ignixa terminology exists |
| 12 | Patch (FHIRPath/JSON/Merge) Firely `ElementNode`/POCO, no abstraction | **Medium** | Yes | §3.11 — **recommend explicit deferral** (proven semantics, low volume); revisit with Ignixa mutable-node patch support |
| 13 | Duplicate standalone `Microsoft.Health.Fhir.Ignixa` project + orphaned `AddIgnixaPersistence`/`IgnixaRawResourceFactory`/`IgnixaResourceDeserializer` | **Medium** (hygiene) | No | §2.3 — delete or consolidate |
| 14 | Anonymized export tied to external Firely-based `Microsoft.Health.Fhir.Anonymizer.*` packages | **Medium** | Yes if anonymized export enabled | §3.12 — **recommend explicit deferral** (upstream dependency) |
| 15 | Group export, $everything, $member-match, SearchParameterState, BulkUpdate `ToPoco` call sites | **Low** | Yes (mechanical) | §3.12-3.14 |
| 16 | Storage-layer trivial `Hl7.*` usings (6 files), `ValueSets` project | **Low** | Mostly mechanical (enums/utility swaps) | §3.14 |
| 17 | `IModelInfoProvider` + 4 version-specific Core projects (single-assembly endgame) | **Critical effort, last in order** | Yes | §3.15 — close only after ranks 1-8 |

**Recommended sequencing for objective 3**: rank 1 (flag) → rank 4 (contract decision, since it dictates the shape of everything below) → ranks 5-6 (FHIRPath/converter propagation) → rank 3 (bundle abstraction) → rank 8 → ranks 9-10 → deferrals (11, 12, 14) documented as such → rank 17 last.

**Explicit shim-deferral list (objective 5)**: Terminology (behind `ITerminologyServiceProxy`), Patch (Firely `ElementNode` engine), anonymized export (external package), XML (pending sibling investigation outcome), and E2E/`Shared.Client` (out of scope per feature readme). Everywhere else, `Ignixa.Extensions.FirelySdk5` conversions (`ToTypedElement`/`ToPoco`/`ToSourceNode`) should be treated as migration debt to burn down, with the §2.2 per-evaluation double shim in search indexing as the single highest-value elimination.
