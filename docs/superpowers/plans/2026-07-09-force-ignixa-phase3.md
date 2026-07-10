# Force-Ignixa Phase 3 (single-resource traffic) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `FhirSdkMode.Ignixa` serve single-resource CRUD, validation, export, and bulk-update traffic natively (no silent Firely POCO round-trips) wherever a resource genuinely carries an Ignixa node — completing the objective-2 groundwork the backlog calls US-4, US-10, US-11, US-12 (partial), and US-14 (partial). US-12's remaining piece (removing the unconditional upfront `ToPoco()` in Create/Upsert) and US-13 (conditional-reference resolution) are one entangled task, scoped separately as a follow-on plan once its resolver design is ready — do not attempt to fold it into this plan.

**Architecture:** Every fix in this plan is data-driven (`GetIgnixaNode() != null` / equivalent), not `SdkMode`-branched — this is the established pattern for handlers that are the same class in every mode (`CreateResourceHandler`/`UpsertResourceHandler` already do this; `IgnixaResourceValidator` is the one exception, since its Firely-re-validation skip is explicitly an Ignixa-mode-only behavior change per its design decision below). No new DI wiring is needed for any task in this plan.

**Tech Stack:** C#/.NET 9, `Ignixa.Serialization.SourceNodes.ResourceJsonNode` (mutable `System.Text.Json` tree), xunit.

## Global Constraints

- `ResourceElement.GetIgnixaNode()` (`src/Microsoft.Health.Fhir.Core/Extensions/ResourceElementIgnixaExtensions.cs:29-44`) is the standard way to detect whether a `ResourceElement` carries an Ignixa node — it checks `ResourceInstance is ResourceJsonNode` or `ResourceInstance is IgnixaResourceElement`. `ResourceInstance` is only set by the two-arg `ResourceElement` constructor (`internal ResourceElement(ITypedElement instance, object resourceInstance)`, `src/Microsoft.Health.Fhir.Core/Models/ResourceElement.cs:38-43`) — the single-arg constructor never sets it. Every task in this plan that adds a "does this carry a node" check must use `GetIgnixaNode()`, not invent a second detection mechanism.
- `IgnixaResourceElement.SetVersionId(string)` and `.SetLastUpdated(DateTimeOffset)` (`src/Microsoft.Health.Fhir.Ignixa/IgnixaResourceElement.cs:216-228`) are the existing, proven native-mutation primitives — reuse them; do not write new node-mutation helpers that duplicate what they already do.
- This repo enforces StyleCop alphabetical `using` ordering as a build error (`TreatWarningsAsErrors`).
- Build verification command for every task: `dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60` — `0 Warning(s)` beyond pre-existing. Pre-existing, unrelated failures you may see and must not try to fix: the four `*.Tests.E2E` SDK-version environment failures, and occasional transient Roslyn/MSBuild crashes (retry once before reporting a concern).
- Every task must land as a genuinely additive change to existing behavior: Firely-backed resources (or, before Task 4 of the prior reorg plan and this plan converge, any resource where `GetIgnixaNode()` returns null) must take exactly the code path they take today. Do not remove or restructure the POCO fallback path in any handler this plan touches — only add the native branch alongside it, or (Task 3 only) skip a specific redundant call.

---

### Task 1: Fix DB-read Ignixa node drop (US-4)

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Modules/FhirModule.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/FhirModuleTests.cs`

**Interfaces:** No signature changes — this task only changes which `ResourceElement` constructor is called, so `GetIgnixaNode()` starts returning non-null for DB-read resources in Ignixa/Hybrid mode. Every task below except Task 3 depends on this (their native paths are otherwise unreachable for DB-read/export/bulk-update input, which is dead-code-shippable but worthless without this).

- [ ] **Step 1: Fix the constructor call**

Read `src/Microsoft.Health.Fhir.Shared.Api/Modules/FhirModule.cs` first — find the JSON branch inside the DB-read deserializer dictionary factory (search for `FhirResourceFormat.Json,` inside the `IReadOnlyDictionary<FhirResourceFormat, Func<string, string, DateTimeOffset, ResourceElement>>` registration; this branch already has an `if (_sdkMode == FhirSdkMode.Firely)` guard from prior work — you are editing only the Ignixa branch below it). It currently ends with:

```csharp
// Convert to ResourceElement for backward compatibility
return new ResourceElement(ignixaElement.ToTypedElement());
```

Change to:

```csharp
// Convert to ResourceElement for backward compatibility, preserving the node
// (two-arg ctor) so GetIgnixaNode() works for DB-read resources.
return new ResourceElement(ignixaElement.ToTypedElement(), resourceNode);
```

(`resourceNode` is the `ResourceJsonNode` already in scope from `var resourceNode = ignixaSerializer.Parse(str);` a few lines above — do not construct a new one. This exactly matches the pattern `IgnixaResourceElementExtensions.ToResourceElement()` already uses elsewhere: `ResourceInstance = ignixaElement.ResourceNode`, which is the same `resourceNode` value by a different path.)

- [ ] **Step 2: Close the loop on the Phase 2 test workaround**

Read `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Modules/FhirModuleTests.cs` first. It currently has a test (from prior work) asserting the Ignixa/Hybrid DB-read path via `element.Instance.GetType().Name == "TypedElementAdapter"` — a workaround documented at the time as needed *because* this exact bug made `GetIgnixaNode()` unusable. Now that Step 1 fixes the bug, update that test to assert the stable, public way instead:

```csharp
[Theory]
[InlineData(FhirSdkMode.Hybrid)]
[InlineData(FhirSdkMode.Ignixa)]
public void GivenHybridOrIgnixaMode_WhenDeserializingJson_ThenIgnixaParserIsUsed(FhirSdkMode mode)
{
    var provider = BuildProvider(mode);
    var deserializers = provider.GetRequiredService<IReadOnlyDictionary<FhirResourceFormat, Func<string, string, DateTimeOffset, ResourceElement>>>();

    var patientJson = "{\"resourceType\":\"Patient\",\"id\":\"test\"}";
    var element = deserializers[FhirResourceFormat.Json](patientJson, "1", DateTimeOffset.UtcNow);

    Assert.Equal("Patient", element.InstanceType);
    Assert.NotNull(element.GetIgnixaNode());
}
```

(Replace the existing type-name-string assertion with `Assert.NotNull(element.GetIgnixaNode())`; keep the `GivenFirelyMode_...` test's `Assert.Null(GetIgnixaNode(element))`-equivalent assertion as-is if present — read the current file to confirm its exact current shape before editing, since this plan's summary may not match verbatim.) Add `using Microsoft.Health.Fhir.Core.Extensions;` in alphabetical order if `GetIgnixaNode()` isn't already resolvable (check first; it may already be covered).

- [ ] **Step 3: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
```

Expected: clean build, all tests pass, including the updated `FhirModuleTests`.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "US-4: Fix Ignixa node drop on DB-read JSON deserialization

FhirModule's DB-read deserializer used the single-arg ResourceElement
constructor, which never sets ResourceInstance, so GetIgnixaNode() always
returned null for anything read back from storage -- silently defeating
every downstream fast path gated on it (export, validator, RawResourceFactory,
and this session's Phase 3 fixes). Switch to the two-arg constructor,
preserving the already-parsed ResourceJsonNode, matching the pattern
IgnixaResourceElementExtensions.ToResourceElement() already uses."
```

---

### Task 2: Native id path for conditional-update single-match

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/Upsert/ConditionalUpsertResourceHandler.cs`
- Test: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Resources/Upsert/ConditionalUpsertResourceHandlerTests.cs` (extend existing file)

**Interfaces:** None — internal method body change only.

**Correction to note (verified during planning, not from the original research pass):** `HandleSingleMatch`'s resource mutation operates on `request.Resource` — the incoming HTTP request resource, already node-backed in Ignixa/Hybrid mode via the input formatter's two-arg `ResourceElement` construction — not on `match.Resource` (the DB-read resource the request matched against, used here only for its `Version`/`ResourceId`). **This task does not depend on Task 1** (unlike the initial research pass assumed, which conflated "the matched existing resource" with "the resource being written"). Land it in any order relative to Task 1; sequenced here as Task 2 for narrative continuity with the rest of the plan, not because of a real dependency.

- [ ] **Step 1: Read the current handler**

Read `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/Upsert/ConditionalUpsertResourceHandler.cs` in full. Confirm `HandleSingleMatch` currently reads:

```csharp
public override async Task<UpsertResourceResponse> HandleSingleMatch(ConditionalUpsertResourceRequest request, SearchResultEntry match, CancellationToken cancellationToken)
{
    ResourceWrapper resourceWrapper = match.Resource;
    Resource resource = request.Resource.ToPoco();
    var version = WeakETag.FromVersionId(resourceWrapper.Version);

    // One Match, no resource id provided OR (resource id provided and it matches the found resource): The server performs the update against the matching resource
    if (string.IsNullOrEmpty(resource.Id) || string.Equals(resource.Id, resourceWrapper.ResourceId, StringComparison.Ordinal))
    {
        resource.Id = resourceWrapper.ResourceId;
        return await _mediator.Send<UpsertResourceResponse>(new UpsertResourceRequest(resource.ToResourceElement(), request.BundleResourceContext, version), cancellationToken);
    }
    else
    {
        throw new BadRequestException(string.Format(Core.Resources.ConditionalUpdateMismatchedIds, resourceWrapper.ResourceId, resource.Id));
    }
}
```

- [ ] **Step 2: Add the native-node branch**

Replace the body with a version that checks `request.Resource.GetIgnixaNode()` first, mirroring the id-stamping pattern already proven in `CreateResourceHandler`/`UpsertResourceHandler` (mutate `resourceJsonNode.Id` directly instead of `resource.Id` on a POCO):

```csharp
public override async Task<UpsertResourceResponse> HandleSingleMatch(ConditionalUpsertResourceRequest request, SearchResultEntry match, CancellationToken cancellationToken)
{
    ResourceWrapper resourceWrapper = match.Resource;
    var version = WeakETag.FromVersionId(resourceWrapper.Version);

    var resourceJsonNode = request.Resource.GetIgnixaNode();
    if (resourceJsonNode != null)
    {
        // Native path: stamp the id directly on the node, no POCO round-trip.
        if (string.IsNullOrEmpty(resourceJsonNode.Id) || string.Equals(resourceJsonNode.Id, resourceWrapper.ResourceId, StringComparison.Ordinal))
        {
            resourceJsonNode.Id = resourceWrapper.ResourceId;
            return await _mediator.Send<UpsertResourceResponse>(new UpsertResourceRequest(request.Resource, request.BundleResourceContext, version), cancellationToken);
        }
        else
        {
            throw new BadRequestException(string.Format(Core.Resources.ConditionalUpdateMismatchedIds, resourceWrapper.ResourceId, resourceJsonNode.Id));
        }
    }

    // Fallback to POCO path for non-Ignixa resources
    Resource resource = request.Resource.ToPoco();

    if (string.IsNullOrEmpty(resource.Id) || string.Equals(resource.Id, resourceWrapper.ResourceId, StringComparison.Ordinal))
    {
        resource.Id = resourceWrapper.ResourceId;
        return await _mediator.Send<UpsertResourceResponse>(new UpsertResourceRequest(resource.ToResourceElement(), request.BundleResourceContext, version), cancellationToken);
    }
    else
    {
        throw new BadRequestException(string.Format(Core.Resources.ConditionalUpdateMismatchedIds, resourceWrapper.ResourceId, resource.Id));
    }
}
```

**Important:** in the native branch, pass `request.Resource` (the original node-backed `ResourceElement`, id already mutated in place on its underlying node) directly into the new `UpsertResourceRequest` — do NOT call `.ToResourceElement()` on it (that's only correct for the POCO `resource` variable in the fallback branch; calling it on an already-`ResourceElement` value doesn't type-check and isn't needed, since `request.Resource` already IS a `ResourceElement`). This mirrors exactly how `CreateResourceHandler`'s native branch reuses the mutated node in place rather than rebuilding a `ResourceElement`.

Add `using Microsoft.Health.Fhir.Core.Extensions;` in alphabetical order if `GetIgnixaNode()` isn't already resolvable in this file (check the current using block first).

- [ ] **Step 3: Add a regression test**

Read `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Resources/Upsert/ConditionalUpsertResourceHandlerTests.cs` in full first to match its existing construction/mocking pattern exactly (how it builds a `ConditionalUpsertResourceHandler`, mocks `IFhirDataStore`/`ISearchService`/`IMediator`, and constructs a `SearchResultEntry` for the single-match case). Add a new test asserting that when `request.Resource` carries an Ignixa node (construct one the same way `FhirModuleTests`/`OperationsModuleTests` do — via a real parse through `IgnixaJsonSerializer` + `IgnixaResourceElement`, not a hand-built mock, so the test exercises real node mutation), `HandleSingleMatch` stamps the id on the node and the mediator receives a `UpsertResourceRequest` whose `Resource.GetIgnixaNode()` is still non-null (i.e., no POCO round-trip occurred) with `.Id` matching `resourceWrapper.ResourceId`. Keep all existing tests in this file unchanged — this is additive only, per this plan's Global Constraints.

- [ ] **Step 4: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
```

Expected: clean build, all existing `ConditionalUpsertResourceHandlerTests` still pass, plus the new test.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "US-12 (partial): Native id path for conditional-update single-match

ConditionalUpsertResourceHandler.HandleSingleMatch always ToPoco'd the
request resource to stamp the matched resource's id, even when the request
already carried an Ignixa node. Add a native branch mirroring the pattern
already proven in CreateResourceHandler/UpsertResourceHandler. Firely-backed
requests keep the existing POCO path unchanged."
```

---

### Task 3: Ignixa-mode validation short-circuit + evidence-gated exclusion list

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Validation/IgnixaResourceValidator.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Modules/ValidationModule.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Validation/IgnixaResourceValidatorTests.cs`

**Interfaces:**
- Produces: `IgnixaResourceValidator`'s constructor gains a `bool skipFallbackOnSuccess` parameter (or equivalent — see Step 1 for the exact shape). `ValidationModule` passes `_sdkMode == FhirSdkMode.Ignixa` for it. No other consumer of `IgnixaResourceValidator` exists outside `ValidationModule` and its own test file (verify with a repo-wide grep for `new IgnixaResourceValidator` before assuming this).

This task has two independent halves — do them in order, but both are required for the task to be complete.

#### Half A: Skip the redundant Firely re-validation in Ignixa mode only

- [ ] **Step 1: Add the mode-aware skip**

Read `src/Microsoft.Health.Fhir.Shared.Core/Features/Validation/IgnixaResourceValidator.cs` in full. Its constructor currently is:

```csharp
public IgnixaResourceValidator(
    IIgnixaSchemaContext schemaContext,
    ModelAttributeValidator fallbackValidator)
{
    EnsureArg.IsNotNull(schemaContext, nameof(schemaContext));
    EnsureArg.IsNotNull(fallbackValidator, nameof(fallbackValidator));

    _schemaContext = schemaContext;
    _fallbackValidator = fallbackValidator;
    ...
}
```

Add a third constructor parameter and field:

```csharp
private readonly bool _skipFallbackOnSuccess;

public IgnixaResourceValidator(
    IIgnixaSchemaContext schemaContext,
    ModelAttributeValidator fallbackValidator,
    bool skipFallbackOnSuccess)
{
    EnsureArg.IsNotNull(schemaContext, nameof(schemaContext));
    EnsureArg.IsNotNull(fallbackValidator, nameof(fallbackValidator));

    _schemaContext = schemaContext;
    _fallbackValidator = fallbackValidator;
    _skipFallbackOnSuccess = skipFallbackOnSuccess;
    ...
}
```

(Keep every other existing line in the constructor body unchanged — this is additive only.) Then in `TryValidateIgnixa`, change the tail:

```csharp
if (!result.IsValid)
{
    return false;
}

return _fallbackValidator.TryValidate(value, validationResults, recurse);
```

to:

```csharp
if (!result.IsValid)
{
    return false;
}

if (_skipFallbackOnSuccess)
{
    return true;
}

return _fallbackValidator.TryValidate(value, validationResults, recurse);
```

Do not change the `ConformanceResourceTypes` branch earlier in `TryValidateIgnixa` (`if (ConformanceResourceTypes.Contains(resourceType)) { ... return _fallbackValidator.TryValidate(...); }`) — conformance types must keep unconditionally routing to Firely validation in every mode, per Half B below; `_skipFallbackOnSuccess` only applies to the non-conformance, schema-validated path.

- [ ] **Step 2: Wire the mode check in `ValidationModule`**

Read `src/Microsoft.Health.Fhir.Shared.Api/Modules/ValidationModule.cs` in full. Its `else` branch (Hybrid/Ignixa) currently constructs:

```csharp
services.AddSingleton<IModelAttributeValidator>(sp =>
{
    var schemaContext = sp.GetRequiredService<IIgnixaSchemaContext>();
    var fallbackValidator = sp.GetRequiredService<ModelAttributeValidator>();
    return new IgnixaResourceValidator(schemaContext, fallbackValidator);
});
```

Change to pass the mode-derived flag (the module already has `_sdkMode` in scope from its constructor):

```csharp
services.AddSingleton<IModelAttributeValidator>(sp =>
{
    var schemaContext = sp.GetRequiredService<IIgnixaSchemaContext>();
    var fallbackValidator = sp.GetRequiredService<ModelAttributeValidator>();
    return new IgnixaResourceValidator(schemaContext, fallbackValidator, skipFallbackOnSuccess: _sdkMode == FhirSdkMode.Ignixa);
});
```

(Hybrid passes `false` — same double-validation behavior as today, since Hybrid's purpose is safety-net dual-checking, not a bug to fix. Firely mode is untouched — it never constructs `IgnixaResourceValidator` at all.)

- [ ] **Step 3: Add a mock-verification regression test for the skip**

Read `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Validation/IgnixaResourceValidatorTests.cs` in full first — note its existing constructor calls `new IgnixaResourceValidator(_schemaContext, new ModelAttributeValidator())` (two args) and will need a third argument added everywhere it's constructed in this file (default to whatever preserves each existing test's current behavior — check each test's intent; if a test doesn't care about the fallback-skip behavior, pass `skipFallbackOnSuccess: false` to keep it byte-for-byte equivalent to today).

Add a new test using a substitutable/spy fallback validator (e.g. `NSubstitute.Substitute.For<ModelAttributeValidator>()` if that type can be substituted — check whether `ModelAttributeValidator` has virtual members / is substitutable by reading its class declaration first; if it's sealed or has no virtual `TryValidate`, use a thin test double subclass instead) to assert: given a valid Ignixa resource and `skipFallbackOnSuccess: true`, `_fallbackValidator.TryValidate(...)` is never called; given `skipFallbackOnSuccess: false` (Hybrid-equivalent) with the same valid resource, it IS called exactly once. This is the regression guard for the actual behavior change — the existing 3 tests only assert `isValid`, not fallback-call-count, so this is genuinely new coverage, not a modification of prior assertions.

#### Half B: Evidence-gated exclusion-list disposition (CodeSystem, ValueSet only)

- [ ] **Step 4: Write negative-case conformance tests for CodeSystem and ValueSet**

In the same test file, add tests that construct an intentionally-invalid `CodeSystem` (e.g., a required property missing, or a value violating a constraint that Ignixa 0.6.7's `CodeSystemPropertyTypeCheck` — `src/Core/Ignixa.Validation/Checks/CodeSystemPropertyTypeCheck.cs` in the local `E:\data\src\ignixa-fhir` checkout, read it first to know exactly what it checks — is designed to catch) and a similarly intentionally-invalid `ValueSet` (targeting `ValueSetFilterCheck`/`ValueSetIncludeSystemCheck` in the same directory). For each, temporarily remove the type from `ConformanceResourceTypes` (in a test-local way — do not edit the production list yet) and assert that Ignixa's own schema validation (not the Firely fallback) now correctly rejects the invalid instance. **This is the evidence gate**: if either test cannot be made to pass (Ignixa's check doesn't actually catch your invalid-instance construction, or catching it requires more validation depth than this task's scope), that type stays in the exclusion list and Step 5 below excludes it, with the specific gap noted in your task report — this is a legitimate, complete outcome for this task, not a failure.

- [ ] **Step 5: Update the production exclusion list based on the evidence**

In `IgnixaResourceValidator.cs`, remove `"CodeSystem"` and/or `"ValueSet"` from `ConformanceResourceTypes` **only for whichever type(s) Step 4's tests actually proved pass** (this may be zero, one, or both — let the evidence decide, do not remove both by default). Add a one-line code comment above the remaining list explaining the evidence bar for future removal, e.g.: `// StructureDefinition and the other types below predate Ignixa's newest conformance checks (0.6.7, PR #310) and have not been conformance-tested for removal from this list — see docs/features/sdk-migration/investigations/validation-sdk-dependency.md for the evidence bar.`

- [ ] **Step 6: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
```

Expected: clean build, all existing `IgnixaResourceValidatorTests` still pass (with their constructor calls updated for the new parameter), plus new tests from Steps 3-4.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "US-11: Ignixa-mode validation short-circuit + exclusion-list evidence pass

IgnixaResourceValidator always re-ran the Firely fallback validator even
after a successful Ignixa validation, in every mode. Add a
skipFallbackOnSuccess flag, wired to (SdkMode == Ignixa) in ValidationModule
-- Hybrid keeps dual-validating (its safety-net purpose), Firely mode is
unaffected (never constructs this validator).

Separately, evidence-test CodeSystem and ValueSet against Ignixa 0.6.7's
new conformance checks and narrow the 14-type conformance exclusion list
accordingly. StructureDefinition and the other types are NOT removed --
their validation mechanism predates 0.6.7 and has no conformance evidence
backing removal; they continue routing to Firely validation in every mode,
which is the documented, intentional remaining scope of this exclusion."
```

---

### Task 4: Native soft-delete extension mutation + export ordering-bug fix

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Extensions/ModelExtensions.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Export/ResourceToNdjsonBytesSerializer.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Operations/Export/ResourceToNdjsonBytesSerializerTests.cs`

**Interfaces:** `TryAddSoftDeletedExtension` (extension method on `ResourceElement`, `ModelExtensions.cs`) keeps its existing public signature and its POCO fallback behavior for non-Ignixa input — only its Ignixa-node branch is new.

**Depends on:** Task 1 (export input is DB-read; this task's native branch is unreachable without it).

**Two things this task fixes, not one:** (a) adds a genuine native mutation path for node-backed resources, mirroring `IgnixaImportResourceParser`'s (renamed by the reorg plan — if that plan hasn't run yet in your working tree, the file may still be named `ImportResourceParser.cs`; check which name currently exists and use it) `RemoveSoftDeletedExtension` pattern in reverse; (b) fixes a real ordering bug where `TryAddSoftDeletedExtension` always returns a fresh POCO-backed element (via `poco.ToResourceElement()`, single-arg-ctor-equivalent), which makes the *subsequent* `GetIgnixaNode()` check in `SerializeToJson` always miss — so today, passing `addSoftDeletedExtension: true` forces the slow Firely fallback path even when the input was originally node-backed and even when the extension was already present (nothing to add). Fixing (a) inherently fixes (b), since the native branch returns a still-node-backed `ResourceElement`.

- [ ] **Step 1: Read the current code**

Read `src/Microsoft.Health.Fhir.Shared.Core/Extensions/ModelExtensions.cs` in full, focusing on `TryAddSoftDeletedExtension` (confirmed current shape):

```csharp
public static ResourceElement TryAddSoftDeletedExtension(this ResourceElement resource)
{
    EnsureArg.IsNotNull(resource, nameof(resource));

    Resource poco = resource.ToPoco();
    poco.Meta ??= new Meta();

    if (!poco.Meta.Extension.Any(x => string.Equals(x.Url, KnownFhirPaths.AzureSoftDeletedExtensionUrl, StringComparison.OrdinalIgnoreCase)))
    {
        poco.Meta.Extension.Add(
            new Extension
            {
                Url = KnownFhirPaths.AzureSoftDeletedExtensionUrl,
                Value = new FhirString("soft-deleted"),
            });
    }

    return poco.ToResourceElement();
}
```

Read `src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Import/ImportResourceParser.cs` (or `IgnixaImportResourceParser.cs` if the reorg plan's Task 2 already ran — check which exists) in full for `RemoveSoftDeletedExtension`'s exact current shape, since you're mirroring it precisely, not approximately.

- [ ] **Step 2: Add the native branch**

In `ModelExtensions.cs`, change `TryAddSoftDeletedExtension` to check for a node first:

```csharp
public static ResourceElement TryAddSoftDeletedExtension(this ResourceElement resource)
{
    EnsureArg.IsNotNull(resource, nameof(resource));

    var resourceJsonNode = resource.GetIgnixaNode();
    if (resourceJsonNode != null)
    {
        AddSoftDeletedExtensionNative(resourceJsonNode);
        return resource;
    }

    Resource poco = resource.ToPoco();
    poco.Meta ??= new Meta();

    if (!poco.Meta.Extension.Any(x => string.Equals(x.Url, KnownFhirPaths.AzureSoftDeletedExtensionUrl, StringComparison.OrdinalIgnoreCase)))
    {
        poco.Meta.Extension.Add(
            new Extension
            {
                Url = KnownFhirPaths.AzureSoftDeletedExtensionUrl,
                Value = new FhirString("soft-deleted"),
            });
    }

    return poco.ToResourceElement();
}

private static void AddSoftDeletedExtensionNative(ResourceJsonNode resource)
{
    var metaNode = resource.MutableNode["meta"] as System.Text.Json.Nodes.JsonObject;
    if (metaNode == null)
    {
        metaNode = new System.Text.Json.Nodes.JsonObject();
        resource.MutableNode["meta"] = metaNode;
    }

    var extensionArray = metaNode["extension"] as System.Text.Json.Nodes.JsonArray;
    if (extensionArray != null)
    {
        foreach (var ext in extensionArray)
        {
            if (ext is System.Text.Json.Nodes.JsonObject extObj &&
                extObj.TryGetPropertyValue("url", out var urlNode) &&
                urlNode is System.Text.Json.Nodes.JsonValue urlValue &&
                string.Equals(urlValue.GetValue<string>(), KnownFhirPaths.AzureSoftDeletedExtensionUrl, StringComparison.OrdinalIgnoreCase))
            {
                // Already present -- nothing to add.
                return;
            }
        }
    }
    else
    {
        extensionArray = new System.Text.Json.Nodes.JsonArray();
        metaNode["extension"] = extensionArray;
    }

    extensionArray.Add(new System.Text.Json.Nodes.JsonObject
    {
        ["url"] = KnownFhirPaths.AzureSoftDeletedExtensionUrl,
        ["valueString"] = "soft-deleted",
    });
}
```

Verify `ResourceJsonNode.MutableNode` is the correct property name for the mutable `System.Text.Json.Nodes.JsonObject` backing the resource (used by `RemoveSoftDeletedExtension`'s exact current code, per Step 1's read — match its property access pattern exactly, including whether it accesses `.MutableNode` directly or via some other path; the sketch above assumes `resource.MutableNode["meta"]` based on the prior investigation's citation, but confirm against the actual file you just read, not this plan's paraphrase). `System.Text.Json.Nodes` may already be in scope via a `using` in this file — check and use the shorter unqualified names (`JsonObject`, `JsonArray`, `JsonValue`) if so, matching the file's existing style, rather than the fully-qualified names shown above (written fully-qualified here only to be unambiguous in the plan text).

Add `using Ignixa.Serialization.SourceNodes;` (for `ResourceJsonNode`) and `using Microsoft.Health.Fhir.Core.Extensions;` (for `GetIgnixaNode()`, if not already present) in alphabetical order — check the current using block first.

- [ ] **Step 3: Fix the `SerializeToJson` ordering consequence**

Read `src/Microsoft.Health.Fhir.Shared.Core/Features/Operations/Export/ResourceToNdjsonBytesSerializer.cs` in full — confirm `SerializeToJson` still reads as:

```csharp
private string SerializeToJson(ResourceElement resourceElement)
{
    var ignixaNode = resourceElement.GetIgnixaNode();
    if (ignixaNode != null)
    {
        return _ignixaSerializer.Serialize(ignixaNode, pretty: false);
    }

    return resourceElement.Instance.ToJson();
}
```

**No change needed to this method itself** — Step 2's fix to `TryAddSoftDeletedExtension` (returning the same node-backed `resource` instance instead of a fresh POCO-backed one) is what makes this method's existing `GetIgnixaNode()` check start succeeding for node-backed input. Confirm this by reading `StringSerialize` (the caller) too:

```csharp
public string StringSerialize(ResourceElement resourceElement, bool addSoftDeletedExtension = false)
{
    EnsureArg.IsNotNull(resourceElement, nameof(resourceElement));

    if (addSoftDeletedExtension)
    {
        resourceElement = resourceElement.TryAddSoftDeletedExtension();
    }

    return SerializeToJson(resourceElement);
}
```

If this still matches current code exactly, this step requires no edits — it exists to make you verify the fix chain actually closes, not to introduce a second change.

- [ ] **Step 4: Add regression tests**

Read `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Operations/Export/ResourceToNdjsonBytesSerializerTests.cs` in full first — note its current `_resourceDeserializaer` is built as a Firely-only deserializer (per its own comment), so it never constructs a node-backed `ResourceElement` today. Add a new node-backed construction path (parse a resource via `IgnixaJsonSerializer` + `IgnixaResourceElement` the same way `FhirModuleTests`/`ConditionalUpsertResourceHandlerTests` (Task 2) do, then use the two-arg `ResourceElement` constructor or `IgnixaResourceElementExtensions.ToResourceElement()`) and add tests asserting: (a) `StringSerialize(nodeBackedElement, addSoftDeletedExtension: true)` on a resource without the extension adds it and the result still round-trips through the Ignixa serializer (not the Firely fallback — assert this indirectly by checking the output JSON's exact property ordering/formatting matches Ignixa's serializer output style, or more directly if the test can inject a spy/mock serializer to assert which one was called); (b) calling it twice (extension already present) doesn't duplicate the extension; (c) a Firely-backed (non-node) `ResourceElement` still takes the existing POCO path unchanged (regression guard for the fallback branch). Keep all existing tests in this file unchanged.

- [ ] **Step 5: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
```

Expected: clean build, all existing `ResourceToNdjsonBytesSerializerTests` pass unchanged, plus new tests.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "US-14 (slice A): Native soft-delete extension mutation for export

TryAddSoftDeletedExtension always ToPoco'd, mutated, and rebuilt via the
single-arg ResourceElement constructor -- which meant every export with
addSoftDeletedExtension:true silently forced the slow Firely serialization
fallback afterward (SerializeToJson's GetIgnixaNode() check always missed
the freshly-rebuilt POCO-backed element), even for originally node-backed
input and even when the extension was already present. Add a native branch
mirroring ImportResourceParser's RemoveSoftDeletedExtension in reverse --
this fixes both the missing native path and the ordering bug in one change,
since the native branch returns the same still-node-backed element."
```

---

### Task 5: Native lastUpdated stamp in bulk update

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/Upsert/BulkUpdateService.cs`
- Test: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Resources/Upsert/BulkUpdateServiceTests.cs` (extend existing file)

**Interfaces:** None.

**Depends on:** Task 1 (bulk-update input is DB-read; native branch unreachable without it).

- [ ] **Step 1: Read the current code**

Read `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/Upsert/BulkUpdateService.cs` in full, confirming `StampLastUpdated`'s current shape:

```csharp
private static ResourceElement StampLastUpdated(ResourceElement resourceElement)
{
    var lastUpdated = Clock.UtcNow.UtcDateTime.TruncateToMillisecond();
    var resource = resourceElement.ToPoco<Resource>();
    resource.Meta ??= new Meta();
    resource.Meta.LastUpdated = lastUpdated;

    return resource.ToResourceElement();
}
```

And its caller `CreateUpdateWrapper` (read the surrounding ~10 lines to confirm how `StampLastUpdated` is invoked, so your edit fits the actual call site without needing to change the caller).

- [ ] **Step 2: Add the native branch**

This is a direct reuse of the already-proven `IgnixaResourceElement.SetLastUpdated` pattern (same one `FhirModule`'s DB-read deserializer and `CreateResourceHandler`/`UpsertResourceHandler`'s native branches already use):

```csharp
private static ResourceElement StampLastUpdated(ResourceElement resourceElement)
{
    var lastUpdated = Clock.UtcNow.UtcDateTime.TruncateToMillisecond();

    var resourceJsonNode = resourceElement.GetIgnixaNode();
    if (resourceJsonNode != null)
    {
        resourceJsonNode.Meta.LastUpdated = lastUpdated;
        return resourceElement;
    }

    var resource = resourceElement.ToPoco<Resource>();
    resource.Meta ??= new Meta();
    resource.Meta.LastUpdated = lastUpdated;

    return resource.ToResourceElement();
}
```

(`resourceJsonNode.Meta.LastUpdated = lastUpdated;` matches the exact pattern already used in `FhirModule.cs`'s DB-read deserializer, which sets `resourceJsonNode.Meta.LastUpdated` directly rather than going through `SetLastUpdated(DateTimeOffset)` — check both patterns against the current `ResourceJsonNode.Meta` type: if `Meta.LastUpdated` is a plain settable property taking `DateTime`/`DateTimeOffset` matching `lastUpdated`'s type here, use direct assignment for consistency with `FhirModule.cs`; if `IgnixaResourceElement.SetLastUpdated(DateTimeOffset)` is the only available mutation surface from a bare `ResourceJsonNode` — i.e. if `.Meta.LastUpdated` isn't directly settable on the node type itself outside an `IgnixaResourceElement` wrapper — construct `new IgnixaResourceElement(resourceJsonNode, /* schema */).SetLastUpdated(lastUpdated)` instead, but this requires a schema reference this method may not have in scope; verify which is actually available before choosing, and prefer the direct node-property route if `lastUpdated`'s `DateTime` type needs conversion to `DateTimeOffset` first — do that conversion explicitly rather than relying on an implicit cast.)

Add `using Microsoft.Health.Fhir.Core.Extensions;` in alphabetical order if `GetIgnixaNode()` isn't already resolvable.

- [ ] **Step 3: Add a regression test**

Read `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Resources/Upsert/BulkUpdateServiceTests.cs` in full first to match its construction pattern. Add a test constructing a node-backed `ResourceElement`, invoking whatever public path reaches `StampLastUpdated` (it's `private static` — find the public method that calls it, likely part of `CreateUpdateWrapper` or similar, and test through that), and asserting the result is still node-backed (`GetIgnixaNode() != null`) with `Meta.LastUpdated` updated to a recent timestamp. Add a second test confirming Firely-backed input still takes the POCO path (regression guard). Keep existing tests unchanged.

- [ ] **Step 4: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
```

Expected: clean build, all existing `BulkUpdateServiceTests` pass unchanged, plus new tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "US-14 (slice B): Native lastUpdated stamp in bulk update

BulkUpdateService.StampLastUpdated always ToPoco'd to set meta.lastUpdated,
even for node-backed resources. Reuse the same native mutation pattern
already proven in CreateResourceHandler/UpsertResourceHandler and
FhirModule's DB-read deserializer. Firely-backed resources keep the
existing POCO path unchanged."
```

---

### Task 6: FhirResult node-aware unwrap

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Features/ActionResults/FhirResult.cs`
- Test: `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Features/ActionResults/FhirResultTests.cs` (extend existing file)

**Interfaces:** None.

**Depends on:** nothing in this plan — ordered last because it displaces no currently-live traffic (every genuine CRUD response already flows through `RawResourceElement`, which `FhirResult` already passes through untouched; this closes a gap for future/edge callers and de-risks Phase 4's bundle work, which is the first thing likely to send a genuinely node-backed non-raw `ResourceElement` through this path).

- [ ] **Step 1: Read the current code**

Read `src/Microsoft.Health.Fhir.Shared.Api/Features/ActionResults/FhirResult.cs` in full, confirming `GetResultToSerialize()`'s current shape:

```csharp
protected override object GetResultToSerialize()
{
    if (Result is ResourceElement)
    {
        return (Result as ResourceElement)?.ToPoco();
    }
    else if (Result is RawResourceElement)
    {
        return Result;
    }
    else
    {
        throw new NotImplementedException();
    }
}
```

Also confirm (this is load-bearing for Step 2, verify don't assume): neither `IgnixaFhirJsonOutputFormatter.CanWriteType` nor `FhirJsonOutputFormatter.CanWriteType` accepts a bare `ResourceElement` — read both files' `CanWriteType` methods. `IgnixaFhirJsonOutputFormatter.CanWriteType` accepts `ResourceJsonNode`, `IgnixaResourceElement`, `Resource`, `RawResourceElement`.

- [ ] **Step 2: Add the node-aware unwrap**

Change `GetResultToSerialize()` to unwrap to a type the Ignixa formatter's `CanWriteType` actually accepts when a node is present, falling back to `ToPoco()` otherwise:

```csharp
protected override object GetResultToSerialize()
{
    if (Result is ResourceElement resourceElement)
    {
        var ignixaNode = resourceElement.GetIgnixaNode();
        if (ignixaNode != null)
        {
            return ignixaNode;
        }

        return resourceElement.ToPoco();
    }
    else if (Result is RawResourceElement)
    {
        return Result;
    }
    else
    {
        throw new NotImplementedException();
    }
}
```

(Returning the `ResourceJsonNode` directly, since `IgnixaFhirJsonOutputFormatter.CanWriteType` accepts `ResourceJsonNode` — confirmed in Step 1. This avoids constructing an intermediate `IgnixaResourceElement` wrapper, which would need a schema reference this method doesn't have in scope; the bare node is sufficient since `CanWriteType`/`WriteResponseBodyAsync` already handle `ResourceJsonNode` as a first-class case.) Add `using Microsoft.Health.Fhir.Core.Extensions;` in alphabetical order if `GetIgnixaNode()` isn't already resolvable in this file.

- [ ] **Step 3: Add regression tests**

Read `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Features/ActionResults/FhirResultTests.cs` in full first. Add tests: (a) a node-backed `ResourceElement` result returns the underlying `ResourceJsonNode` from `GetResultToSerialize()` (you may need to expose/call this via reflection or a test-visible seam if it's `protected` and the existing tests don't already have a pattern for invoking it — check how the existing 4 tests exercise this method); (b) a Firely-backed (non-node) `ResourceElement` result still returns a POCO via `ToPoco()`, unchanged from today; (c) a `RawResourceElement` result is passed through unchanged (regression guard, should already be covered — confirm rather than duplicate if so).

- [ ] **Step 4: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
```

Expected: clean build, all existing `FhirResultTests` pass unchanged, plus new tests.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "US-10 (reshaped): FhirResult unwraps to the Ignixa node when present

GetResultToSerialize() always called ToPoco() on ResourceElement results.
Every current CRUD response already flows through RawResourceElement (this
formatter path is defensive/future-proofing, not a live perf fix), but the
gap is real: neither JSON output formatter accepts a bare ResourceElement,
so the fix unwraps to the ResourceJsonNode the Ignixa formatter's
CanWriteType already accepts, rather than passing the ResourceElement
through unchanged (which would 406). Firely-backed results keep the
existing ToPoco() path."
```

---

## After This Plan

Combined with the prior Force-Firely phase (US-1/2/3/6/7/8) and this plan's six tasks, `SdkMode.Ignixa` now has native paths for DB-read, single-resource CRUD (including conditional-update's id stamp), validation (with the redundant Firely re-validation removed where evidence supports it), export, bulk-update, and defensive response serialization. **Not done by this plan, and explicitly out of scope:** US-12's remaining piece (removing the unconditional upfront `ToPoco()` in `CreateResourceHandler`/`UpsertResourceHandler`, which requires a node-native conditional-reference resolver) and US-13 — these are one entangled task requiring new subsystem design (enumerate reference fields via `IReferenceMetadataProvider`, FHIRPath-select matching elements, mutate the underlying JSON in place; no existing code to mirror), scoped as a separate follow-on plan once that design is ready. Also out of scope, per the architecture review that scoped this plan: `DeletionService.CreateSoftDeletedWrapper` (declined permanently — builds a tombstone from scratch, no node to preserve) and `DeletionService.RemoveReferences` (deferred — a third, unrelated mutation kind with no ready pattern; reassess after the reference-resolver task lands, since its machinery may generalize to this). Next: Phase 4 (US-15/US-16, search/history bundle assembly and batch/transaction processing) is the largest remaining gap toward a production-ready Ignixa mode.
