# Investigation: XML Pipeline Ignixa Adoption

**Feature**: sdk-migration
**Status**: Complete
**Created**: 2026-07-08

## Approach

Re-examine the prior assessment (ignixa-integration-investigation.md, section 4.2) that rated the XML pipeline "Firely-only; XML is low-volume; 🟢 Low; out of scope", and decide between three options for the XML input/output pipeline in the Ignixa migration:

- **Option A — Build now**: implement schema-driven FHIR XML parse/serialize in Ignixa (upstream in `Ignixa.Serialization`, or a local shim in fhir-server) and register Ignixa XML formatters ahead of the Firely ones, mirroring the JSON formatter pattern.
- **Option B — Defer (explicit objective-5 carve-out)**: keep the Firely XML formatters as a named, documented exception even when the force-Ignixa flag is on; file the upstream Ignixa XML work item; achieve a true "100% Ignixa" deployment only via `SupportsXml=false`.
- **Option C — Drop**: make XML available only under the force-Firely flag; the force-Ignixa configuration rejects XML entirely.

### Current architecture (this branch)

```
XML request (Content-Type: application/fhir+xml, or _format=xml)
    │
    ├─ XmlFormatterFeatureModule (gated by Features:SupportsXml)
    │      registers FhirXmlInputFormatter / FhirXmlOutputFormatter / NonFhirResourceXmlOutputFormatter
    │
    ├─ MVC formatter order after startup:
    │      [0] IgnixaFhirJsonInputFormatter / IgnixaFhirJsonOutputFormatter   (JSON media types only)
    │      [1..] FhirJson*, FhirXml* formatters from DI (FormatterConfiguration)
    │      → XML content negotiation always falls through to the Firely XML formatters
    │
    ├─ INPUT:  Firely FhirXmlParser → Hl7.Fhir.Model.Resource (POCO)
    │
    └─ OUTPUT: RawResourceElement (stored JSON)
                 → ResourceDeserializer JSON path = Ignixa parse → adapter ITypedElement
                 → ToPoco<Resource>()  (Ignixa→Firely shim crossing)
                 → Firely FhirXmlSerializer.Serialize(...)
```

Note the last line: on this branch, **XML output already crosses the Ignixa→Firely interop shim on every request** — the stored raw JSON is parsed by Ignixa, wrapped in the `ITypedElement` adapter, rebuilt into a Firely POCO, then serialized to XML by Firely. XML is no longer a purely-Firely side channel; it is a consumer of the shim that objective 5 wants minimized.

## Tradeoffs

### Option A — Build schema-driven XML in Ignixa now

| Pros | Cons |
|------|------|
| Removes the last serialization-format gap toward a literal "100% Ignixa" (objective 2) | FHIR XML is a large, subtle format: schema-driven array/choice-type resolution, primitive `value` attribute typing, primitive extensions (`_property`), contained resources, xhtml `<div>`, `id`/`url` attributes — Firely's implementation is a decade of hardening |
| Eliminates the per-request Ignixa→POCO→Firely-XML shim crossing on XML output | No production XML code exists anywhere in ignixa-fhir `src/` to build on — this is a from-scratch upstream feature, not an integration task |
| Symmetric formatter architecture (Ignixa XML formatters mirror Ignixa JSON formatters) | Both directions needed (parse **and** serialize); the only existing sketch is test-only, one-directional, and schema-less (see Evidence) |
| | High correctness risk for low request volume; XML bugs would be silent data-shape corruption, not errors |

### Option B — Defer as explicit objective-5 carve-out (keep Firely XML)

| Pros | Cons |
|------|------|
| Zero new risk; XML behavior unchanged for existing deployments (`SupportsXml: true` is the shipped default) | Force-Ignixa flag is "100% Ignixa for JSON paths" rather than literally 100%; must be documented as a named exception |
| A true 100%-Ignixa deployment is already achievable with existing config: force-Ignixa + `SupportsXml=false` (capability statement drops `xml`; XML requests get 406) | Keeps Firely POCO model + `FhirXmlParser`/`FhirXmlSerializer` referenced from the API layer while the flag exception exists |
| Matches actual priority: XML is default-on but low-traffic; effort goes to higher-severity gaps | Per-request shim crossing on XML output remains (bounded by XML traffic volume) |
| Upstream Ignixa XML can be built properly (schema-driven, tested against the full E2E `Format.All` matrix) without blocking the flag work | |

### Option C — Drop XML behind the force-Firely flag

| Pros | Cons |
|------|------|
| Simplest possible Ignixa story | Breaking change: `SupportsXml: true` is the shipped default (`appsettings.json`), XML is advertised in the capability statement, and the managed service exposes it |
| | Large E2E surface runs `Format.All` (Create/Read/Delete/History/Bundles/Conditional*/Everything/MemberMatch/Metadata/…) — dropping XML invalidates half the format matrix |
| | FHIR spec permits JSON-only servers, but existing XML clients of deployed servers would hard-fail |

## Alignment

- [x] Follows architectural layering rules (Option B changes nothing; XML formatters stay in the API layer behind the existing `SupportsXml` feature module)
- [x] Developer Experience (no new setup; `SupportsXml` already toggles the pipeline end-to-end including capability statement and 406 handling)
- [x] Specification compliance (FHIR permits JSON-only servers; declared formats drive `_format`/Accept validation via the capability statement)
- [x] Consistent with existing patterns (mirrors how the JSON pipeline was migrated: Ignixa formatters inserted ahead of Firely ones — the same slot exists for future Ignixa XML formatters)

## Evidence

### 1. Current XML formatter code and registration

- Input: `src/Microsoft.Health.Fhir.Shared.Api/Features/Formatters/FhirXmlInputFormatter.cs:23-91` — wraps Firely `FhirXmlParser.ParseAsync<Resource>`; claims `application/fhir+xml`, `application/xml`, `text/xml`, `application/*+xml`.
- Output: `src/Microsoft.Health.Fhir.Shared.Api/Features/Formatters/FhirXmlOutputFormatter.cs:26-135` — Firely `FhirXmlSerializer`; handles `RawResourceElement` (line 73) and `Bundle` with `RawBundleEntryComponent` entries (lines 81-99) by deserializing stored JSON and calling `.ToPoco<Resource>()`.
- Non-FHIR XML: `NonFhirResourceXmlOutputFormatter.cs:13-27` (health-check/other payloads as XML).
- Feature gating: `src/Microsoft.Health.Fhir.Shared.Api/Modules/FeatureFlags/XmlFormatter/XmlFormatterFeatureModule.cs:30-48` — the three formatters are registered in DI **only** when `Features:SupportsXml` is true.
- Capability statement: `XmlFormatterConfiguration.cs:29-36` adds `application/fhir+xml` and `xml` to `CapabilityStatement.format` only when `SupportsXml` is true.
- Formatter ordering: `FormatterConfiguration.cs:36-46` inserts DI-registered formatters at the front of MVC's lists; the Ignixa JSON formatters are then inserted at index 0 by `IgnixaFormatterConfiguration` (`src/Microsoft.Health.Fhir.Shared.Core/Ignixa/ServiceCollectionExtensions.cs:157-166`). The Ignixa formatters claim only JSON media types, so XML negotiation always falls through to the Firely XML formatters. No change to XML routing occurred on this branch.
- Unconditional Firely XML registrations independent of the formatter flag: `src/Microsoft.Health.Fhir.Shared.Api/Modules/FhirModule.cs:59-65` (singleton `FhirXmlParser`/`FhirXmlSerializer`), `FhirModule.cs:90` (XML entry in the `ResourceSerializer` dictionary), `FhirModule.cs:131-137` (XML entry in the `ResourceDeserializer` dictionary — used if a data store ever holds XML-format raw resources).

### 2. Usage/priority signal — "low volume" needs qualification

- **Default-enabled**: `src/Microsoft.Health.Fhir.Shared.Web/appsettings.json:21` ships `"SupportsXml": true`. The CLR default is false (`src/Microsoft.Health.Fhir.Api/Configs/FeatureConfiguration.cs:21`), but every deployment built from the shipped Web project has XML on unless explicitly disabled.
- **Content negotiation is spec-wired**: `FormatParametersValidator.cs:51-150` honors `_format=xml` (takes precedence over Accept) and validates Accept headers against `CapabilityStatement.format` — so `SupportsXml=false` cleanly produces 406 `NotAcceptableException` for XML clients; the disable path is a complete, already-tested mechanism.
- **Large E2E surface**: `test/Microsoft.Health.Fhir.Shared.Tests.E2E/HttpIntegrationFixtureArgumentSetsAttribute.cs` parameterizes fixtures by format, and `Format.All` (JSON+XML) is used by the core CRUD/bundle suites: `CreateTests.cs:28`, `ReadTests.cs:22`, `DeleteTests.cs:28`, `HistoryTests.cs:42`, `BundleBatchTests.cs:29`, `BundleTransactionTests.cs:32`, `BundleEdgeCaseTests.cs:28`, `ConditionalCreateTests.cs:26`, `ConditionalUpdateTests.cs:23`, `ConditionalDeleteTests.cs:29`, `EverythingOperationTests.cs:28`, `MemberMatchTests.cs:20`, `MetadataTests.cs:24`, `ObservationResolveReferenceTests.cs:24`, `ExceptionTests.cs:25`, `OperationVersionsTests.cs:21`. The shipped client library also supports XML (`src/Microsoft.Health.Fhir.Shared.Client/FhirClient.cs`).
- Conclusion: "low volume" plausibly holds for production *traffic*, but the *feature* is default-on, capability-advertised, client-supported, and covers roughly half the E2E format matrix. Objective 2's force-Ignixa flag cannot ship without an explicit XML story.

### 3. New finding — XML output already rides the interop shim on this branch

`FhirXmlOutputFormatter.WriteResponseBodyAsync` (line 73) deserializes `RawResourceElement` via `ResourceDeserializer`. On this branch the JSON entry of that dictionary (`FhirModule.cs:116-128`) parses with `IIgnixaJsonSerializer`, wraps in `IgnixaResourceElement`, and exposes it as `ITypedElement` via the Firely adapter (`IgnixaResourceElement.ToTypedElement()`, `src/Microsoft.Health.Fhir.Shared.Core/Ignixa/IgnixaResourceElement.cs:129-132`). `.ToPoco<Resource>()` then rebuilds a full Firely POCO through that adapter before Firely serializes it to XML. So every XML read/search response performs: Ignixa parse → adapter → POCO materialization → Firely XML serialize. This is a per-request shim crossing of exactly the kind objective 5 targets — previously invisible because section 4.2 treated XML as an isolated Firely side channel.

### 4. Ignixa production packages have no XML support

- Case-insensitive grep for `xml` across `E:\data\src\ignixa-fhir\src\Core\Ignixa.Serialization\**\*.cs`: **zero hits**. The package is JSON-only (`Abstractions/`, `Converters/`, `SourceNodes/` all operate on JSON).
- Repo-wide grep for `Xml` in ignixa-fhir `src/`: only incidental matches — generated ValueSet `.resx` resources in `Ignixa.Specification`, `Ignixa.NarrativeGenerator/Security/XhtmlSanitizer.cs` (xhtml, not FHIR XML), `Ignixa.Search/Models/FhirResourceFormat.cs` (an enum declaring `Xml = 1` with no serialization behind it), and the Firely SDK6 `TypedElementAdapter`.
- Ignixa's own reference server (`src/Application/Ignixa.Api`) is JSON-only. XML support would be a from-scratch upstream feature.

### 5. The test helper is not a viable starting point

`E:\data\src\ignixa-fhir\test\Ignixa.FhirPath.Tests\TestHelpers\FhirXmlToJsonConverter.cs` — verified:

- **Test-only**: lives under `test/Ignixa.FhirPath.Tests/TestHelpers/`; not part of any shipped Ignixa NuGet package.
- **One-directional**: XML→JSON only. No JSON→XML serializer exists anywhere in the repo, and output (responses to XML-accepting clients) is the dominant direction fhir-server needs.
- **Schema-less array handling**: `ShouldBeArray` (lines 244-248) is a hardcoded list of 15 property names (`identifier`, `name`, `telecom`, `address`, `contact`, `communication`, `link`, `given`, `prefix`, `suffix`, `line`, `coding`, `contained`, `extension`, `modifierExtension`). Any array-valued element not on the list with exactly one occurrence is silently emitted as a singular object — e.g. `Patient.generalPractitioner`, `Observation.category`, `Observation.performer`, `Bundle.entry`, `CodeableConcept.coding` siblings beyond the listed set. This is silent shape corruption, not an error.
- **Type inference by string parsing**: `WritePrimitiveValue` (lines 142-160) guesses JSON types — a FHIR *string* whose value is `"123"` becomes a JSON number; `"true"` becomes a boolean. FHIR JSON typing is determined by the element's declared type in the schema, not the lexical value.
- **Contained resources mis-shaped**: `WriteValue` (lines 134-139) emits a contained resource as `{"Patient": {...}}` instead of the required `{"resourceType": "Patient", ...}`.

A production-grade converter needs, at minimum: `ISchema`/`IFhirSchemaProvider`-driven cardinality and type resolution, schema-typed primitive emission, contained-resource handling, primitive-extension round-tripping, xhtml narrative handling, and a full JSON→XML direction — i.e., a real upstream `Ignixa.Serialization.Xml` feature, not an evolution of this helper.

## Verdict

**Severity: 🟡 Medium** (upgraded from the prior 🟢 Low). Not because XML traffic is high — "low volume" likely still holds — but because:

1. XML is **default-enabled** in the shipped configuration and advertised in the capability statement, so objective 2's "100% Ignixa, production-ready" flag cannot be defined without an explicit XML disposition.
2. On this branch XML output **already crosses the Ignixa→Firely shim per request** (Ignixa parse → adapter → POCO → Firely XML serialize), making it a live objective-5 concern rather than an isolated Firely side channel.
3. Roughly half the E2E format matrix (`Format.All` suites) exercises XML, so any regression or removal is loud and breaking.

**Recommendation: Option B — defer, as an explicit objective-5 carve-out.** Concretely:

- **Name the shim**: "XML pipeline (input parse + output serialize) remains Firely (`FhirXmlParser`/`FhirXmlSerializer`), including the Ignixa→`ToPoco`→Firely crossing on XML output." This is a deliberate deferral, not an oversight.
- **Define the force-Ignixa flag around it**: force-Ignixa + `SupportsXml=true` → XML requests continue through Firely formatters (documented exception). Force-Ignixa + `SupportsXml=false` → a genuinely 100%-Ignixa deployment using an existing, spec-compliant, already-wired mechanism (capability statement drops `xml`; XML clients receive 406).
- **File the upstream work item** in ignixa-fhir for schema-driven bidirectional XML in `Ignixa.Serialization` (requirements in Evidence §5). Only when that ships does fhir-server add Ignixa XML formatters in the same insert-ahead pattern used for JSON.
- **Do not** build a local shim in fhir-server from the test helper — the schema-less array list, lexical type inference, contained-resource shape bug, and missing JSON→XML direction make it a correctness liability for a low-volume feature.
- **Do not drop XML** (Option C): breaking for existing deployments and managed-service parity, and unjustified when the `SupportsXml=false` opt-out already exists per deployment.

Effort avoided now: a from-scratch, high-subtlety upstream serializer (multi-week) for the lowest-traffic surface. Cost accepted: one named Firely exception under the force-Ignixa flag, bounded by XML request volume, removable when upstream XML lands.
