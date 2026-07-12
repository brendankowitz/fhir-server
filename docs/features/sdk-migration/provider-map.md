# Firely/Ignixa Provider Map

One row per mode-gated seam: which implementation serves `SdkMode.Firely`, which
serves `SdkMode.Ignixa`/`Hybrid`, and where the selection happens. Keep this
current as new seams gain per-mode implementations (each new US-1x/2x story
that adds one should add a row here as part of its done-criteria).

See [node-mutation.md](node-mutation.md) for the rule on rebuilding vs. reusing a ResourceElement after mutating its underlying node.

| Seam | Firely implementation | Ignixa implementation | Selected by |
|---|---|---|---|
| JSON input formatter | `Shared.Api/Features/Formatters/FhirJsonInputFormatter.cs` | `Shared.Api/Features/Formatters/IgnixaFhirJsonInputFormatter.cs` | `Shared.Api/Modules/FeatureFlags/SdkMode/SdkModeFeatureModule.cs` |
| JSON output formatter | `Shared.Api/Features/Formatters/FhirJsonOutputFormatter.cs` | `Shared.Api/Features/Formatters/IgnixaFhirJsonOutputFormatter.cs` | same |
| FHIRPath provider | `Core/Features/Search/FhirPath/FirelyFhirPathProvider.cs` + `FirelyCompiledFhirPath.cs` | `Microsoft.Health.Fhir.Ignixa/FhirPath/IgnixaFhirPathProvider.cs` + `IgnixaCompiledFhirPath.cs` | `Shared.Api/Modules/FhirModule.cs` (skips `AddIgnixaFhirPath` in Firely mode) + `Shared.Api/Modules/SearchModule.cs` (`TryAddSingleton` fallback) |
| Structural validation | `Shared.Core/Features/Validation/ModelAttributeValidator.cs` | `Shared.Core/Features/Validation/IgnixaResourceValidator.cs` (wraps the Firely fallback) | `Shared.Api/Modules/ValidationModule.cs` |
| `$import` parsing | `Shared.Core/Features/Operations/Import/ImportResourceParser.cs` | `Shared.Core/Features/Operations/Import/IgnixaImportResourceParser.cs` | `Shared.Api/Modules/OperationsModule.cs` |
| DB-read JSON deserialization | inline Firely branch | inline Ignixa branch | inline `_sdkMode` check, `Shared.Api/Modules/FhirModule.cs` (JSON deserializer dictionary factory) |
| Search/history bundle assembly | `Shared.Core/Features/Search/BundleFactory.cs` | `Shared.Core/Features/Search/IgnixaBundleFactory.cs` | `Shared.Api/Modules/SearchModule.cs` |

## SDK-neutral by design (no pairing, and none expected)

These are data-driven — the same class handles both SDKs at runtime based on
whether the resource in hand actually carries an Ignixa node
(`GetIgnixaNode() != null`), with no `SdkMode` check and no separate
implementation class. Do not create a Firely/Ignixa pair for these; per
ADR-2607 this is the intended long-term shape once every write/read path is
node-aware.

- `Shared.Core/Features/Persistence/RawResourceFactory.cs`
- `Shared.Core/Features/Resources/Bundle/BundleSerializer.cs`
- `Shared.Core/Features/Resources/Create/CreateResourceHandler.cs`, `Shared.Core/Features/Resources/Upsert/UpsertResourceHandler.cs` (partial — see `user-stories.md` US-12)
- `Shared.Core/Features/Resources/ResourceReferenceResolver.cs` (Firely `Resource`, unchanged) / `Shared.Core/Features/Resources/IgnixaResourceReferenceResolver.cs` (Ignixa `ResourceJsonNode`, new in US-16 Task 1) — two distinct classes, but the choice between them is per-resource-representation (whichever the caller already holds, a Firely POCO or an Ignixa node), not an `SdkMode` check; both delegate to the same `ResourceReferenceResolver.TryResolveReferenceValueAsync` for the actual resolve/cache/throw decision, so there's exactly one implementation of that logic to keep in sync. Not yet wired into any caller (`BundleHandler` wiring is Task 6 of US-16) — listed here now because the seam itself is data-driven-by-design, matching this section's convention, not because a `SdkMode`-gated selection point exists yet.

## Deliberately unpaired (deferred, named exceptions per objective 5)

| Seam | Why no Ignixa side exists | Story |
|---|---|---|
| XML formatters | No upstream Ignixa XML support at all | US-E3 |
| Terminology | Upstream is membership-only; `$expand`/`$lookup` stubbed | US-E5 |
| FHIRPath/JSON Patch | Blocked on upstream `FhirPatchEngine` packaging | US-E2 |
| Anonymized export | `Microsoft.Health.Fhir.Anonymizer.*` packages are Firely-POCO-based | US-E6 |

## Planned pairs (Firely side exists today; Ignixa side is future work)

| Story | Seam | Firely side today | Planned Ignixa side |
|---|---|---|---|
| US-16 | Batch/transaction | `Shared.Api/Features/Resources/Bundle/BundleHandler.cs` (+ 4 versioned partials) | new Ignixa bundle processing |
| US-20 | Profile validation | `Shared.Core/Features/Validation/ProfileValidator.cs` | `IgnixaProfileValidator` (planned name) |
