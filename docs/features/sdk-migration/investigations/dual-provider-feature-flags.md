# Investigation: Dual-Provider Feature Flags (Force-Firely / Force-Ignixa)

**Feature**: sdk-migration
**Status**: Complete
**Created**: 2026-07-08
**Branch audited**: `personal/bkowitz/ignixa-sdk-next-steps-fable` (tip of `feature/ignixa-sdk`, head `3d8730828`)

## Approach

Add a single composition-time SDK mode switch that selects which SDK backs every swappable
seam in the server:

```csharp
// src/Microsoft.Health.Fhir.Core/Configs/CoreFeatureConfiguration.cs (proposed)
public FhirSdkMode SdkMode { get; set; } = FhirSdkMode.Hybrid;

public enum FhirSdkMode
{
    Hybrid = 0,   // current branch behavior: Ignixa-first with Firely fallback
    Firely = 1,   // objective 1: 100% Firely on every code path
    Ignixa = 2,   // objective 2: 100% Ignixa, no Firely fallback
}
```

```jsonc
// appsettings.json
"FhirServer": {
  "CoreFeatures": {
    "SdkMode": "Hybrid"   // "Firely" | "Ignixa" | "Hybrid"
  }
}
```

**One enum, not two booleans.** Two independent booleans (`ForceFirely`, `ForceIgnixa`) admit an
invalid state (both true) and an ambiguous state (both false vs. "auto"). The three modes are
mutually exclusive by construction, and `Hybrid` makes the fallback posture explicit instead of
being the undocumented absence of both flags.

**Composition root, not per-request.** Every Ignixa/Firely seam in the codebase is a singleton
DI registration or a global `MvcOptions` mutation:

- MVC formatter lists are app-wide (`FormatterConfiguration.cs:36-46`, `ServiceCollectionExtensions.cs:157-166`)
- `IFhirPathProvider` is a singleton feeding `TypedElementSearchIndexer` (`SearchModule.cs:144-147`)
- `IModelAttributeValidator` is a singleton (`ValidationModule.cs:48-53`)
- `ResourceDeserializer`'s format dictionary is built once at startup (`FhirModule.cs:108-139`)

Per-request switching would require scoped duplicates of all of these plus request-context checks
inside formatter `CanRead`/`CanWrite`, doubling the test matrix for no rollout benefit —
deployments can already ring by config. Mode changes require restart; that is acceptable and
matches the existing `Features:SupportsXml` precedent (`XmlFormatterFeatureModule.cs:26-48`),
which gates formatter registration at module load via ctor-injected `FhirServerConfiguration`.

**Where the switch is consumed.** All Ignixa registrations live in four Shared.Api modules, so
each module reads the mode from ctor-injected `FhirServerConfiguration` (the exact
`XmlFormatterFeatureModule` pattern):

| Seam | Registration site | Firely mode | Ignixa mode | Hybrid (default) |
|---|---|---|---|---|
| MVC JSON formatters | `FhirModule.cs:166-180` + `ServiceCollectionExtensions.cs:95-106` | Firely formatters only | Ignixa formatters only | Ignixa first, Firely fallback (**must be fixed — see RC-1**) |
| DB-read deserializer (JSON) | `FhirModule.cs:116-129` | `FhirJsonParser` (pre-branch behavior) | Ignixa parse | Ignixa parse |
| FHIRPath provider | `FhirModule.cs:106` / `SearchModule.cs:147` | `FirelyFhirPathProvider` (skip `AddIgnixaFhirPath`; `TryAddSingleton` fallback already exists) | `IgnixaFhirPathProvider` | `IgnixaFhirPathProvider` |
| Model validation | `ValidationModule.cs:48-53` | `ModelAttributeValidator` directly | `IgnixaResourceValidator` without Firely re-validation | `IgnixaResourceValidator` wrapping Firely |
| $import parser | `OperationsModule.cs:54` | **GAP: Firely parser was deleted, must be restored from `main`** | current `ImportResourceParser` | current `ImportResourceParser` |
| Schema context | `FhirModule.cs:102` | keep registered (inert; consumed only when Ignixa nodes flow) | required | required |

Components with data-driven fallback need **no flag check at all** — they branch on
`ResourceElement.GetIgnixaNode() == null`, and in Firely mode no producer ever attaches a node:
`RawResourceFactory.Create` (`RawResourceFactory.cs:41-55`), `ResourceToNdjsonBytesSerializer`
(`ResourceToNdjsonBytesSerializer.cs:59-73`), `CreateResourceHandler`
(`CreateResourceHandler.cs:97-114`), `UpsertResourceHandler` (`UpsertResourceHandler.cs:133-150`),
`IgnixaResourceValidator.TryValidate` (`IgnixaResourceValidator.cs:105-119`). This is the
strongest part of the current design and the flag should preserve it rather than adding
per-callsite mode checks.

**Formatter ordering must get a single owner.** Today two different options phases fight over the
formatter lists (see RC-1). Proposal: delete the `IConfigureOptions`-based
`IgnixaFormatterConfiguration` (`ServiceCollectionExtensions.cs:148-167`), register the Ignixa
formatters `AsService<TextInputFormatter>/<TextOutputFormatter>` like every other formatter, and
make `FormatterConfiguration.PostConfigure` (`FormatterConfiguration.cs:36-46`) the sole authority
that assembles the final order per mode, with a unit test asserting the order for each mode and a
startup log line stating the effective mode.

**FHIR-version interaction.** The flag is orthogonal to version. Server binaries are per-version
(`R4.Web`, etc.) and `IgnixaSchemaContext` already selects the schema provider from
`IModelInfoProvider.Version` (`IgnixaSchemaContext.cs:69-79`). Note: the STU3/R4B
wrong-schema-provider bug described in the test-readiness report is **fixed** in the current tree
— `STU3CoreSchemaProvider`/`R4BCoreSchemaProvider` are wired correctly (verified
`IgnixaSchemaContext.cs:73,75`). Two version-related rules for the flag: (a) in Firely mode,
`IgnixaSchemaContext` must not be eagerly constructed for a version Ignixa doesn't support
(today it throws `NotSupportedException` from the ctor — `IgnixaSchemaContext.cs:77`); (b) in
Ignixa mode with `Features:SupportsXml = true`, fail or warn at startup, because the XML pipeline
is Firely-only in all modes (deliberate deferral — see Evidence §7).

## Tradeoffs

| Pros | Cons |
|------|------|
| Single enum makes invalid flag combinations unrepresentable | Restart required to change mode (no per-request A/B) |
| `Firely` mode is a true rollback switch: registrations revert to pre-branch graph, and data-driven fallbacks need no flag checks | `Ignixa` mode cannot honestly mean "100% Ignixa" until 4 Critical gaps are closed (RC-1..RC-4); until then it means "Ignixa everywhere it exists, Firely for bundles/conformance/XML/patch/projection" |
| Consolidating formatter ordering into `FormatterConfiguration` fixes a real latent bug (RC-1) and makes order testable | Firely `$import` parser must be resurrected from `main` — the only place force-Firely needs new (restored) code |
| Follows existing `SupportsXml` module-gating precedent; no new infrastructure | Three modes triple the CI matrix for the pipelines that exercise serialization (mitigate: Hybrid on PR, Firely+Ignixa nightly) |
| Mode is observable (startup log + capability statement software name unaffected) | `Hybrid` mode's "Ignixa-first" only becomes true after RC-1 is fixed — flipping it changes runtime behavior that today silently runs Firely |

## Alignment

- [x] Follows architectural layering rules (flag in Core config; all consumption at Api-module composition root; no per-callsite mode checks in Core handlers)
- [x] Developer Experience (default `Hybrid` needs zero config; `dotnet run` unchanged)
- [x] Specification compliance (mode does not change FHIR semantics; parse-strictness differences are tracked as RC-9)
- [x] Consistent with existing patterns (`XmlFormatterFeatureModule` flag-gated formatter registration; `CoreFeatureConfiguration` enum-valued settings like `IncludeTotalInBundle`)

## Evidence

Every claim below was verified against the working tree at `3d8730828`, not against the older
docs in this folder (several of which are now stale).

### 1. RC-1 — The Ignixa MVC formatters are never selected at runtime (Critical, blocks objective 2; silently satisfies objective 1)

The branch intends Ignixa formatters to take priority: `AddIgnixaSerializationWithFormatters()`
(`FhirModule.cs:180`) registers `IgnixaFormatterConfiguration : IConfigureOptions<MvcOptions>`
which does `options.InputFormatters.Insert(0, ...)` (`ServiceCollectionExtensions.cs:103,157-166`).

But the legacy `FormatterConfiguration` is an `IPostConfigureOptions<MvcOptions>`
(`FormatterConfiguration.cs:17`, registered at `FhirModule.cs:146-149`) that inserts every
DI-registered `TextInputFormatter`/`TextOutputFormatter` at indices `0..n-1`
(`FormatterConfiguration.cs:38-46`). `OptionsFactory` runs **all** `IConfigureOptions` before
**any** `IPostConfigureOptions`, so the Firely formatters land in front. Verified empirically with
a minimal repro of the exact registration pattern (net9.0, `Microsoft.AspNetCore.App`):

```text
[0] FhirJsonInputFormatter      <- legacy Firely (PostConfigure, Insert(0))
[1] FhirXmlInputFormatter       <- legacy Firely (PostConfigure, Insert(1))
[2] IgnixaFhirJsonInputFormatter <- Ignixa (Configure, Insert(0)) — never reached
```

MVC picks the first formatter whose `CanRead`/`CanWrite` passes. The legacy JSON input formatter
handles both `Resource` and `ResourceElement` (`FhirJsonInputFormatter.cs:43-49`, wrapping POCOs
into `ResourceElement` at `:119-122`) with identical media types, so it wins every JSON request.
The legacy output formatter handles `Resource` and `RawResourceElement`
(`FhirJsonOutputFormatter.cs:63-68`) — the only types controllers emit (see RC-2) — so it wins
every JSON response. The Ignixa formatters only trigger for `ResourceJsonNode` /
`IgnixaResourceElement` action parameters/returns, which no controller declares.

**Consequences today:**
- HTTP-parsed resources are always POCO-backed → `GetIgnixaNode()` returns null → the Ignixa
  fast paths in `RawResourceFactory`, `Create/UpsertResourceHandler`, and
  `IgnixaResourceValidator` never fire for HTTP traffic (they do fire for `$import`).
- The "Ignixa formatter validated by CI E2E" claims in
  `reports/ignixa-test-readiness-report.md` §5.2-5.3 are misattributed; those runs exercised the
  Firely formatters plus the genuinely-unconditional Ignixa paths (DB-read deserializer, FHIRPath
  provider, import).
- The `eb7c30aa0` "Fix Ignixa E2E regressions" failures are consistent with this: they trace to
  the DB-read path (validate-by-id normalization) and `ResourceElement` route binding, not to
  formatter selection.

**Fix (needed for both objectives):** single-owner ordering in `FormatterConfiguration` per mode,
as described in Approach, plus an order-asserting unit test. Objective 1's formatter requirement
is — accidentally — already met.

### 2. RC-2 — `FhirResult` converts every response to a Firely POCO (Critical, blocks objective 2)

`FhirResult.GetResultToSerialize()` (`FhirResult.cs:162-176`):

```csharp
if (Result is ResourceElement)
{
    return (Result as ResourceElement)?.ToPoco();   // <- every non-raw response becomes a POCO
}
else if (Result is RawResourceElement)
{
    return Result;
}
```

Even with Ignixa formatters first, `context.Object` is always a POCO or `RawResourceElement`, and
the Ignixa output formatter's POCO branch deliberately writes with the **Firely** serializer
(`IgnixaFhirJsonOutputFormatter.cs:163-169`). Worse, for an Ignixa-backed `ResourceElement`,
`ToPoco()` (`ModelExtensions.cs:41-54`) goes through the `Ignixa.Extensions.FirelySdk5` shim —
a full node→POCO conversion on every response.

**Fix:** in Ignixa mode `GetResultToSerialize()` must pass through the Ignixa node
(`GetIgnixaNode()` / `IgnixaResourceElement`) so the output formatter serializes natively;
`JobResult`, `OperationOutcome` filters (`OperationOutcomeExceptionFilterAttribute`) construct
POCOs and are acceptable deferrals (low volume) but must be inventoried under objective 3.

### 3. RC-3 — The bundle pipeline is Firely end-to-end (Critical, blocks objective 2)

Search/history responses: `BundleFactory.CreateSearchBundle` builds an `Hl7.Fhir.Model.Bundle`
whose entries are `RawBundleEntryComponent` wrappers (`BundleFactory.cs:43-63`). Serialization is
the legacy formatter's special case (`FhirJsonOutputFormatter.cs:85-113`) delegating to
`BundleSerializer` (Newtonsoft zero-copy splice of raw JSON; registered `FhirModule.cs:66`). The
Ignixa output formatter has **no** equivalent: a `Bundle` with `RawBundleEntryComponent` entries
reaching its `Resource` branch would serialize `entry.resource` as null (resources are only
attached to the POCO when `_elements`/`_summary` forces it, `FhirJsonOutputFormatter.cs:90-108`).

Batch/transaction input was deliberately reverted to Firely binding in `eb7c30aa0`
("Restore bundle POST binding to Firely resources", `FhirController.cs:725`), and `BundleHandler`
processes entries as POCOs.

**Fix for objective 2:** an Ignixa-native bundle assembler (a `ResourceJsonNode` bundle splicing
raw entry JSON is a natural fit for Ignixa's mutable JSON model) plus Ignixa bundle-entry routing
in `BundleHandler`. This is the largest single work item; until it lands, "Ignixa mode" must
document bundles as a Firely carve-out — which materially weakens the "100%" claim since search
is the majority of traffic.

### 4. RC-4 — `$import` has no Firely path left (High, blocks objective 1)

`ImportResourceParser` was rewritten in place to be Ignixa-only
(`ImportResourceParser.cs:25-74`): Ignixa parse, Ignixa schema context, FHIRPath-on-node
conditional-reference checks, `IgnixaResourceElement`-based wrapper creation. There is no
fallback and no surviving Firely implementation. Force-Firely requires restoring the `main`
implementation behind the mode switch (registration at `OperationsModule.cs:54`).

### 5. RC-5 — DB-read deserialization is unconditionally Ignixa, and drops the node (High for objective 1; Medium bug for objective 2)

`FhirModule.cs:116-129` routes every stored-JSON deserialization through Ignixa. Force-Firely
needs this entry flag-gated back to `FhirJsonParser` + `SetMetadata` (pre-branch behavior, still
present for XML at `:131-136`).

Separate bug: line 127 uses the single-arg ctor —
`new ResourceElement(ignixaElement.ToTypedElement())` — so `ResourceInstance` is never set and
`GetIgnixaNode()` returns null for **all DB-read resources**, silently disabling downstream
Ignixa fast paths (e.g., bulk update re-serialization) and diverging from the canonical
`IgnixaResourceElementExtensions.ToResourceElement()` which passes the node
(`IgnixaResourceElementExtensions.cs:34`). One-line fix; belongs with the flag work because
Ignixa mode's correctness accounting depends on knowing which elements carry nodes.

### 6. RC-6 — Validation is a Firely/Ignixa braid, not a switch (High, both objectives)

- `ValidationModule.cs:48-53` unconditionally registers `IgnixaResourceValidator` as
  `IModelAttributeValidator`. Firely mode must register `ModelAttributeValidator` directly.
- `IgnixaResourceValidator` **always** re-runs the Firely validator even when Ignixa validation
  succeeds (`IgnixaResourceValidator.cs:182`), and falls back entirely for 14 conformance
  resource types (`:51-67,139-143`). So today validation cost is Ignixa + Firely, and Ignixa mode
  "without Firely fallback" is a semantic change (Ignixa `Compatibility` depth vs. Firely
  attribute validation) that needs its own conformance testing before the double-validation is
  removed.
- `$validate` deliberately normalizes through POCOs (`ValidateController.cs:120`, added by
  `eb7c30aa0`), and `ProfileValidator` uses the deprecated Firely legacy validator package;
  `FirelyTerminologyServiceProxy` is Firely-only. Recommend: explicit deferral for profile
  validation + terminology in Ignixa mode (documented carve-out), tracked under objective 3.

### 7. Remaining Firely-only surfaces under force-Ignixa (Medium — each needs a fix or an explicit deferral)

| Surface | Location | Recommendation |
|---|---|---|
| `_elements`/`_summary`/projection | Both output formatters delegate to Firely `SerializeAsync` (`IgnixaFhirJsonOutputFormatter.cs:178-183`, `FhirJsonOutputFormatter.cs:146-162`) | Implement native subsetting on `ResourceJsonNode` (mutable JSON makes this tractable); until then, documented fallback |
| Conditional-reference resolution | `CreateResourceHandler.cs:100` / `UpsertResourceHandler.cs:136` drop to POCO when `resolvedReferences > 0` | Ignixa-native reference rewrite (precedent: `ImportResourceParser.CheckConditionalReferenceInResource` already walks references on nodes) |
| FHIR Patch / JSON Patch | `FhirPathPatchPayload.cs:28-58` (raw → `ITypedElement` → `ToPoco` → `FhirPathPatchBuilder`), `OperationAdd/Insert/...` all POCO | Deliberate deferral; native patch on mutable `ResourceJsonNode` is the eventual win but is its own project |
| XML pipeline | `FhirXmlInputFormatter`/`FhirXmlOutputFormatter`, XML dict entry `FhirModule.cs:131-136`; `IgnixaResourceDeserializer` throws on XML (`IgnixaResourceDeserializer.cs:104-108`) | **Deliberate deferral in all modes** (Ignixa has no XML support); Ignixa mode + `SupportsXml=true` should fail startup validation |
| Conformance/$metadata | `SystemConformanceProvider` + `IProvideCapability` build Firely POCOs | Deliberate deferral (low volume, cached) |
| FHIRPath shim round-trip | `IFhirPathProvider` contract is `ITypedElement`-shaped; `IgnixaCompiledFhirPath` converts `ITypedElement → IElement` per evaluation and back (`IgnixaCompiledFhirPath.cs:49-68,124-127`) | Objective-3 work: give the indexer an `IElement`-native overload so Ignixa-backed resources skip the double shim |
| `ToPoco` call sites | 49 occurrences across 34 production files (controllers, `DeletionService`, `BulkUpdateService`, `PatientEverythingService`, `MemberMatchService`, `GroupMemberExtractor`, `ListSearchPipeBehavior`, ...) | Objective-3 inventory; under Ignixa mode each is a shim conversion (correct but slow), not a correctness blocker |

### 8. Dead and stale code discovered (Low)

- `IgnixaRawResourceFactory`, `IgnixaResourceDeserializer`, and `AddIgnixaPersistence()`
  (`FhirServerBuilderIgnixaPersistenceRegistrationExtensions.cs:39-55`) are **never invoked**
  anywhere in the solution — remove or wire up before they rot further.
- Seven files in `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/` (`IgnixaJsonSerializer.cs`,
  `IIgnixaJsonSerializer.cs`, `IgnixaResourceElement.cs`, `IgnixaResourceElementExtensions.cs`,
  `IIgnixaSchemaContext.cs`, `FhirPath/IgnixaFhirPathProvider.cs`,
  `FhirPath/IgnixaCompiledFhirPath.cs`) are **not in the `.projitems`** (only the two formatters
  and `ServiceCollectionExtensions` are compiled from that folder;
  `Microsoft.Health.Fhir.Shared.Core.projitems:14-16`). The compiled copies live in the
  `Microsoft.Health.Fhir.Ignixa` project, which the projitems references (`:11`). Six of the seven
  stale disk copies are byte-identical today; `FhirPath/IgnixaCompiledFhirPath.cs` has already
  diverged — delete them. *(Corrected by the 2026-07-08 adversarial review: original said all
  copies were byte-identical.)*
- `docs/features/sdk-migration/investigations/ignixa-integration-investigation.md` §4.1 and the
  test-readiness report §2.1 are stale on: formatter location, STU3/R4B schema providers (fixed),
  and the input-formatter double-parse (now `ToPoco` via shim, `IgnixaFhirJsonInputFormatter.cs:181-187`).

### 9. Parse-strictness parity (RC-9, Medium, both objectives)

The Firely parser is configured `PermissiveParsing = true, TruncateDateTimeToDate = true`
(`FhirModule.cs:55`), with an even laxer variant for bundle POSTs
(`FhirJsonInputFormatter.cs:65-84`). Ignixa parsing/validation strictness has required three
recent fixes (`3d8730828`, `6d85841bf`, `8d96ee590`). Any mode flip changes which parser rejects
which payloads — client-visible. The flag work should ship with a shared accept/reject corpus
test run under all three modes.

### Alternatives considered

1. **Two independent booleans** — rejected: invalid states representable; see Approach.
2. **Per-request header/tenant switch** — rejected for now: all seams are singletons/global MVC
   options; scoped duplication doubles memory and test matrix. Revisit only if a canary-by-tenant
   requirement appears.
3. **Per-area flags** (`FhirPath: Firely`, `Serialization: Ignixa`, ...) — rejected as YAGNI; the
   Hybrid mode's data-driven fallbacks already give per-resource granularity where it matters,
   and a matrix of per-area combinations is untestable. A single enum can grow an override later
   if a real need appears.

## Verdict

**Recommended**: single `SdkMode` enum (`Hybrid`/`Firely`/`Ignixa`) on `CoreFeatureConfiguration`,
consumed at module load, with `FormatterConfiguration` as the single owner of formatter order.

**Objective 1 (force-Firely) is cheap** — the fallback-oriented design means most components need
no change. Gate 4 registrations + restore one deleted class:

| # | Finding | Severity | Blocks |
|---|---|---|---|
| RC-4 | Firely `$import` parser deleted; must be restored behind the flag | **High** | Obj 1 |
| RC-5a | DB-read JSON deserialization unconditionally Ignixa (`FhirModule.cs:116-129`) | **High** | Obj 1 |
| RC-6a | `IModelAttributeValidator` unconditionally Ignixa-wrapped (`ValidationModule.cs:48-53`) | **High** | Obj 1 |
| — | FHIRPath provider gate (skip `AddIgnixaFhirPath`; `TryAddSingleton` fallback already in place at `SearchModule.cs:147`) | Low | Obj 1 |
| — | Skip `AddIgnixaSerializationWithFormatters`; keep `IIgnixaSchemaContext` registered but lazy (ctor throws for unsupported versions, `IgnixaSchemaContext.cs:77`) | Low | Obj 1 |

**Objective 2 (force-Ignixa) is blocked by four Critical findings** — the honest current state is
that Ignixa serialization serves **zero** HTTP traffic today:

| # | Finding | Severity | Blocks |
|---|---|---|---|
| RC-1 | Formatter ordering: Ignixa formatters registered but never selected (Configure vs PostConfigure); empirically verified | **Critical** | Obj 2 (and falsifies Hybrid's assumed behavior) |
| RC-2 | `FhirResult.GetResultToSerialize()` POCO-izes every response (`FhirResult.cs:162-176`) | **Critical** | Obj 2 |
| RC-3 | Bundle pipeline (search/history/batch/transaction) Firely end-to-end; Ignixa formatter would emit null entries for raw bundles | **Critical** | Obj 2 |
| RC-6b | Ignixa validation always re-runs Firely (`IgnixaResourceValidator.cs:182`); removing fallback is a semantic change needing conformance evidence | **High** | Obj 2 |
| §7 | `_elements`/`_summary`, conditional references, patch, XML, conformance, terminology, 49 `ToPoco` sites | **Medium** (each) | Obj 2 — recommend explicit deferrals for XML, patch, conformance, terminology; fixes for projection and conditional references |
| RC-5b | DB-read path drops the Ignixa node (single-arg `ResourceElement`, `FhirModule.cs:127`) | **Medium** | Obj 2 perf/coverage |
| RC-9 | Parse-strictness parity untested across modes | **Medium** | Both |
| §8 | Dead code (`AddIgnixaPersistence` et al.) + 7 stale uncompiled duplicate files | **Low** | Neither (hygiene) |

**Suggested sequencing**: (1) land the flag + RC-1 fix + order test — this makes all three modes
*definable*; (2) Firely mode to green CI (RC-4/5a/6a) — rollback safety; (3) RC-2 + RC-5b —
Ignixa mode serves single-resource traffic natively; (4) RC-3 bundles; (5) burn down §7 with
explicit deferral notes for XML/patch/conformance/terminology.
