# Phase 4 Plan B: Batch/Transaction Processing on Ignixa (US-16)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `FhirSdkMode.Ignixa`/`Hybrid` decompose, route, and recompose FHIR batch and transaction bundles natively — without Firely POCO parsing per entry — while keeping `BundleHandler`'s routing/orchestration/throttling/statistics engine completely unchanged and Firely mode byte-identical to today.

**Depends on:** Phase 4 Plan A (`docs/superpowers/plans/2026-07-11-phase4-plan-a-search-bundle.md`) Tasks 1 and 3 — this plan's response recomposition rides the `IgnixaRawBundle`/`IgnixaBundleSerializer` carrier and `FhirResult`/formatter branches Plan A builds. Do not start Task 3 of this plan before Plan A's Tasks 1 and 3 are merged.

**Architecture:** Data-driven (`GetIgnixaNode() != null`), not mode-branched, mirroring Phase 3's established pattern for same-class-in-every-mode handlers — `BundleHandler` stays one class with a native branch, not a second parallel handler. The engine (routing via `_router`, per-entry inner-request dispatch, transaction rollback, 429 retry/throttling, statistics, auditing) is untouched; only the codec at 4 seams (binding, decomposition, reference resolution, recomposition) gains a native alternative alongside the existing POCO path.

**Design provenance:** This plan implements a design produced by one Fable research/design pass, independently adversarially reviewed by a second Fable pass (which found one Critical issue in the original reference-resolution approach), and a third targeted Fable redesign of that one piece (which found the Ignixa SDK already provides a sound solution — a schema-typed element tree with a `Meta<JsonNode>()` mutable-node escape hatch — empirically validated against real FHIR schema collision cases). The load-bearing conclusions from all three passes are folded into the tasks below.

## Global Constraints

- This repo enforces StyleCop alphabetical `using` ordering as a build error (`TreatWarningsAsErrors`).
- Build verification: `dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60` — `0 Warning(s)` beyond pre-existing. Pre-existing, unrelated, NOT-to-fix failures: the four `*.Tests.E2E` SDK-version environment failures, occasional transient Roslyn/MSBuild crashes (retry once).
- **Batch bundles need zero reference resolution** (verified: the guard is `_bundleType == BundleType.Transaction && entry.Resource != null` in `BundleHandler.GenerateRequest`, and `PopulateReferenceIdDictionary` is also transaction-gated). This means Task 2 (native decomposition) and Task 3 (native recomposition) can land and be fully exercised via BATCH bundles alone, with transactions staying on the POCO path until Task 6 wires the reference resolver in — this is the sequencing safety valve from the design review: if anything in Tasks 2/3 needs rework, it's caught on the lower-risk batch path first.
- Every task must be additive: Firely mode and non-node-backed requests must take EXACTLY the code path they take today. `BundleHandlerTests` and the existing POCO-path tests must pass unchanged throughout this plan.
- Per `docs/features/sdk-migration/node-mutation.md`: any task that mutates a node in place (Task 4's `SetResourceId`, Task 6's reference rewriting) must either rebuild via `RebuildResourceElement` afterward, or write an explicit consumer-trace comment at the call site proving every downstream reader uses `GetIgnixaNode()` exclusively — this is not optional, it's how the last two Critical/Important bugs in this project's history were caused and caught.
- `IgnixaRawBundle` is immutable after construction (Plan A's rule) — the native response assembler in Task 3 must build up its entries in a mutable intermediate form (e.g., a `List<IgnixaRawBundleEntry>`) and construct the final `IgnixaRawBundle` once, at the end, not attempt to mutate an already-constructed instance.

---

### Task 1: `IgnixaReferenceScanner` + shared reference-resolution core

**Files:**
- Create: `src/Microsoft.Health.Fhir.Ignixa/IgnixaReferenceScanner.cs`
- Create: `src/Microsoft.Health.Fhir.Ignixa.UnitTests/IgnixaReferenceScannerTests.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/ResourceReferenceResolver.cs` (extract shared core)
- Create: `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/IgnixaResourceReferenceResolver.cs`
- Create: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Resources/IgnixaResourceReferenceResolverTests.cs`
- Modify: `docs/features/sdk-migration/provider-map.md`
- Modify: `docs/features/sdk-migration/ignixa-upstream-gaps.md` (log a side-finding — see Step 5)
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Features/Persistence/PersistenceModule.cs` (or wherever `ResourceReferenceResolver` is currently registered)
- Modify relevant `.projitems` files

**Interfaces:** Produces `IgnixaReferenceScanner.EnumerateReferences(IElement root) → IEnumerable<IgnixaReferenceHandle>`, `IgnixaReferenceHandle` (readonly struct with `Reference`/`SetReference`), `ResourceReferenceResolver.TryResolveReferenceValueAsync(...)` (new public method, extracted from the existing private loop body — the only change to this both-modes-shared class), and `IgnixaResourceReferenceResolver` (new class, Ignixa-only).

**This task is standalone and independently valuable**: it requires no `BundleHandler` changes and is immediately reviewable on its own. It is ALSO, verbatim, the unblock for the previously-deferred US-12/US-13 (Create/Upsert conditional-reference resolution) — a future task can wire this exact primitive into `CreateResourceHandler`/`UpsertResourceHandler` without further design work. Do not scope-creep that wiring into this task; it stays a separate follow-on.

- [ ] **Step 1: Read the baseline in full**

Read `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/ResourceReferenceResolver.cs` in full — specifically `ResolveReferencesAsync`'s loop body (dictionary lookup → conditional-`?` search via `GetExistingResourceId` → `ModelInfoProvider.IsKnownResource` check → the two `RequestNotValidException` throws → dictionary population/caching). This loop body is the thing you're extracting into a reusable method, not rewriting.

Also read `src/Microsoft.Health.Fhir.Ignixa/IIgnixaSchemaContext.cs`, and (for the schema-typed tree API this task relies on) confirm — against the pinned Ignixa package version (`Directory.Packages.props`'s `IgnixaPackageVersion`, currently `0.6.7`) — that `Ignixa.Abstractions.IElement` exposes `InstanceType` and `Children()`, and that `JsonNodeSourceNode.Meta<T>()`/`SchemaAwareElement.Meta<T>()` returns the live, mutable backing `JsonObject` for an element (this was verified against the local `E:\data\src\ignixa-fhir` checkout at the `release/0.6.7` tag during design; re-confirm at implementation time in case the pinned version has moved).

- [ ] **Step 2: Implement `IgnixaReferenceScanner`**

Create `src/Microsoft.Health.Fhir.Ignixa/IgnixaReferenceScanner.cs`:

```csharp
namespace Microsoft.Health.Fhir.Ignixa;

public static class IgnixaReferenceScanner
{
    public static IEnumerable<IgnixaReferenceHandle> EnumerateReferences(IElement root)
    {
        EnsureArg.IsNotNull(root, nameof(root));
        return EnumerateReferenceElements(root)
            .Select(e => new IgnixaReferenceHandle(e))
            .Where(h => h.HasReferenceObject);
    }

    private static IEnumerable<IElement> EnumerateReferenceElements(IElement element)
    {
        foreach (IElement child in element.Children())
        {
            if (child.InstanceType == "Reference")
            {
                yield return child;
            }

            // Recurse INTO Reference nodes too -- Reference.identifier.assigner is itself a Reference.
            foreach (IElement nested in EnumerateReferenceElements(child))
            {
                yield return nested;
            }
        }
    }
}

public readonly struct IgnixaReferenceHandle
{
    private readonly JsonObject _referenceObject;

    internal IgnixaReferenceHandle(IElement element)
    {
        _referenceObject = element.Meta<JsonNode>() as JsonObject;
    }

    internal bool HasReferenceObject => _referenceObject != null;

    public string Reference => _referenceObject?["reference"] as string ?? (_referenceObject?["reference"] is JsonValue v && v.TryGetValue(out string s) ? s : null);

    public void SetReference(string value)
    {
        EnsureArg.IsNotNull(_referenceObject, nameof(_referenceObject));
        _referenceObject["reference"] = value;
    }
}
```

Adjust the `Reference` getter's exact JSON-value-extraction idiom to match how other code in this codebase already reads a string property off a `JsonObject` (check `IgnixaImportResourceParser.RemoveSoftDeletedExtension`/`ModelExtensions.AddSoftDeletedExtensionNative` from earlier Phase 3 work for the established pattern — use the SAME idiom, don't invent a third). Confirm the exact member names (`IElement.InstanceType`, `.Children()`, `.Meta<T>()`) against the actual SDK source at the pinned version — the design research verified these exist but this plan text may not have the exact signatures right.

- [ ] **Step 3: Extract the shared resolution core in `ResourceReferenceResolver`**

Read the full current `ResolveReferencesAsync` method. Extract its loop body into a new public method on the same class:

```csharp
public async Task<string> TryResolveReferenceValueAsync(
    string reference,
    IDictionary<string, (string resourceId, string resourceType)> referenceIdDictionary,
    string requestUrl,
    CancellationToken cancellationToken)
```

Returning the resolved `"Type/id"` string, or `null` if no resolution was needed/possible (no-op — the caller should leave the original value alone). Preserve the EXACT existing behavior (including both `RequestNotValidException` throw conditions and the dictionary-caching side effect) — this is a pure extraction, not a behavior change. Update `ResolveReferencesAsync` itself to call the new method in its loop, so there is exactly one implementation of this decision logic (both Firely's own loop AND the new Ignixa resolver in Step 4 call through it).

This is the one public-surface change on a both-modes-shared class in this task — call this out explicitly in your task report so it gets scrutiny in review.

- [ ] **Step 4: Implement `IgnixaResourceReferenceResolver`**

Create `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/IgnixaResourceReferenceResolver.cs`:

```csharp
public class IgnixaResourceReferenceResolver
{
    private readonly ResourceReferenceResolver _coreResolver;
    private readonly IIgnixaSchemaContext _schemaContext;

    public IgnixaResourceReferenceResolver(ResourceReferenceResolver coreResolver, IIgnixaSchemaContext schemaContext)
    {
        EnsureArg.IsNotNull(coreResolver, nameof(coreResolver));
        EnsureArg.IsNotNull(schemaContext, nameof(schemaContext));
        _coreResolver = coreResolver;
        _schemaContext = schemaContext;
    }

    public async Task<int> ResolveReferencesAsync(
        ResourceJsonNode resource,
        IDictionary<string, (string resourceId, string resourceType)> referenceIdDictionary,
        string requestUrl,
        CancellationToken cancellationToken)
    {
        EnsureArg.IsNotNull(resource, nameof(resource));

        int resolvedCount = 0;
        foreach (var handle in IgnixaReferenceScanner.EnumerateReferences(resource.ToElement(_schemaContext.Schema)))
        {
            if (string.IsNullOrWhiteSpace(handle.Reference))
            {
                continue;
            }

            var newValue = await _coreResolver.TryResolveReferenceValueAsync(handle.Reference, referenceIdDictionary, requestUrl, cancellationToken);
            if (newValue != null)
            {
                handle.SetReference(newValue);
                resolvedCount++;
            }
        }

        if (resolvedCount > 0)
        {
            resource.InvalidateCaches();
        }

        return resolvedCount;
    }
}
```

Verify `ResourceJsonNode.ToElement(schema)` and `.InvalidateCaches()` are the correct real method names/signatures (check other call sites in the codebase that already use them, e.g. `IgnixaResourceElement`'s own use of `InvalidateCaches`). Register in DI: `services.AddScoped<IgnixaResourceReferenceResolver>();` alongside the existing `ResourceReferenceResolver` registration (find its current registration site — likely `PersistenceModule.cs` — and add the new one next to it; both dependencies it needs, `ResourceReferenceResolver` and `IIgnixaSchemaContext`, are already registered).

- [ ] **Step 5: Log the side-finding**

Read `docs/features/sdk-migration/ignixa-upstream-gaps.md`. This is NOT an upstream SDK gap (it's an existing fhir-server code gap, found as a side-effect of this task's research) — do not add it there. Instead, add a note to `docs/features/sdk-migration/user-stories.md` or wherever this project's own tech-debt/gap register lives (check `adr-2607-ignixa-merge-readiness.md`'s gap register for the right place) describing: `IgnixaImportResourceParser.CheckConditionalReferenceInResource` uses metadata+FHIRPath enumeration for read-only conditional-reference *detection* during import, which — unlike the schema-typed-tree walk this task introduces — misses conditional references inside extensions and contained resources. This is a pre-existing, narrower-impact gap (detection-only, not a data-corruption risk) — do not fix it in this task, just record it for a future task to pick up, noting `IgnixaReferenceScanner` (this task's output) as the tool that would fix it.

- [ ] **Step 6: Tests**

Create `IgnixaReferenceScannerTests.cs` (in `Microsoft.Health.Fhir.Ignixa.UnitTests`, which can instantiate `R4CoreSchemaProvider`/`R5CoreSchemaProvider` directly — check `IgnixaResourceElementTests` for the exact construction pattern). Cover the full disambiguation matrix validated during design:
- `Expression.reference` (type `uri`) inside `PlanDefinition.action.condition.expression` — NOT yielded.
- `Expression.reference` inside an extension-carried Expression — NOT yielded.
- `Immunization.education.reference` (type `uri`) — NOT yielded.
- Choice-typed `subjectReference` (e.g. on `Observation` or `PlanDefinition`) — yielded, `InstanceType == "Reference"`.
- `extension[].valueReference` — yielded.
- A contained resource's own reference field (e.g. contained `Observation.subject`) — yielded.
- `Reference.identifier.assigner` (a Reference nested inside a Reference) — yielded (tests the "recurse into Reference nodes too" requirement).
- A display-only Reference (`{"display": "..."}`, no `reference` property) — the handle exists but `HasReferenceObject`/`Reference` correctly signals "nothing to resolve," and the caller's `IsNullOrWhiteSpace` skip handles it.
- After `SetReference`, confirm any `_reference` shadow property (primitive extension on the reference string) is preserved untouched.
- R5-only: `ActorDefinition.reference`, `Requirements.reference` (both type `url`) — NOT yielded, using `R5CoreSchemaProvider`.

Create `IgnixaResourceReferenceResolverTests.cs` (in `Microsoft.Health.Fhir.Shared.Core.UnitTests`, so it runs per-FHIR-version via the shared-project mechanism, beside `ResourceReferenceResolverTests.cs` — read that file first for its mocking pattern, e.g. how it mocks `ISearchService`). Write the parity test: build a transaction-bundle-shaped corpus (a PlanDefinition with `Expression.reference` + `subjectReference` sharing one `urn:uuid:` placeholder — the adversarial case; an Immunization with `education`; a Patient/Observation pair with intra-bundle `urn:uuid:` references; a conditional reference `Patient?identifier=...`; an entry with both a contained resource and an extension reference; a display-only reference), parse each entry both as a Firely POCO and as a `ResourceJsonNode`, run both resolvers with the SAME mocked `ISearchService` and the same seeded `referenceIdDictionary`, and assert: serialized entry JSON deep-equals between the two paths, per-entry resolved counts match, and final dictionary contents match.

- [ ] **Step 7: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.Ignixa.UnitTests/Microsoft.Health.Fhir.Ignixa.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
```

- [ ] **Step 8: Update provider-map.md**

Add a row noting this seam is data-driven (per-resource, not `SdkMode`-selected), matching the existing convention for `RawResourceFactory`/`CreateResourceHandler` in the "SDK-neutral by design" section, OR the mode-gated table if you determine `SdkMode` selection is more accurate — read the existing table's conventions and pick the right section; state your reasoning in the commit message if it's not obvious.

- [ ] **Step 9: Commit**

```bash
git add -A
git commit -m "US-16 (Task 1): IgnixaReferenceScanner + IgnixaResourceReferenceResolver

Standalone reference-resolution primitive for Ignixa-mode transactions,
built on Ignixa's schema-typed element tree (IElement.InstanceType) and
its Meta<JsonNode>() mutable-node escape hatch -- both already exist
upstream, no new SDK surface needed. Disambiguates Reference-typed
elements from same-named non-Reference elements (Expression.reference,
Immunization.education.reference, etc.) by schema type, not JSON property
name, closing the Critical finding from this plan's design review.

Extracts ResourceReferenceResolver's resolution-decision loop into a new
public TryResolveReferenceValueAsync so both SDK paths share one
implementation of the actual resolve/cache/throw logic -- parity tests
verify traversal equivalence, not decision-logic equivalence, which is
now shared by construction.

This is also the complete unblock for the previously-deferred US-12/13
(Create/Upsert conditional-reference resolution) -- not wired there yet,
tracked as a separate follow-on. Not wired into BundleHandler yet either
(Task 6 of this plan) -- pure addition, unreachable in production."
```

---

### Task 2: Controller binding + `BundleHandler` mode-branch scaffold

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Controllers/FhirController.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Features/Resources/Bundle/BundleHandler.cs`
- Modify relevant test files for both

**Interfaces:** `BundleHandler.Handle`'s entry point changes its incoming type; internally branches on `GetIgnixaNode() != null` but the native branch initially just delegates to the existing POCO path (behavioral no-op) — this task proves the plumbing compiles and routes correctly before Task 3/4 build real native logic on top.

- [ ] **Step 1: Read the current binding and handler entry point**

Read `FhirController.cs` around the batch/transaction action (`BatchAndTransactions` or similarly named, currently binds `[FromBody] Resource bundle`). Read `BundleHandler.cs`'s `Handle` method and how it currently converts the incoming resource to a working `Bundle` POCO (`request.Bundle.ToPoco<Bundle>()` or similar).

- [ ] **Step 2: Change the binding**

Change `[FromBody] Resource bundle` to `[FromBody] ResourceElement bundle` (verify both `FhirJsonInputFormatter` and `IgnixaFhirJsonInputFormatter`/`FhirXmlInputFormatter` already handle `ResourceElement`-typed model binding — this was confirmed during design for the JSON formatters; confirm XML too, since batch/transaction bundles CAN arrive as XML and that path must be completely unaffected). Update whatever constructs the MediatR request object (`BundleRequest` or similar) to carry a `ResourceElement` instead of a `Resource`.

- [ ] **Step 3: Add the mode-branch scaffold in `BundleHandler.Handle`**

```csharp
if (request.Bundle.GetIgnixaNode() != null)
{
    // Native path -- Tasks 3-6 build this out. For now, no-op: convert to POCO exactly
    // like the existing path, so behavior is unchanged while the scaffold is proven out.
    return await HandlePocoAsync(request.Bundle.ToPoco<Bundle>(), ...);
}

return await HandlePocoAsync(request.Bundle.ToPoco<Bundle>(), ...);
```

(Rename the existing `Handle` body to `HandlePocoAsync` or similar if it isn't already a separable method — this is a pure extraction, not a rewrite.) The point of this task is ONLY to prove the binding change and branch point compile and route correctly in all three modes with zero behavior change — do not build any real native logic yet.

- [ ] **Step 4: Tests**

Update existing `BundleHandlerTests`/controller-binding tests for the new `ResourceElement` parameter type — assertions should be unchanged (same POCO path runs regardless of mode at this point). Add one new test per mode confirming the branch is taken/not taken correctly (`GetIgnixaNode() != null` for a node-backed request in Ignixa/Hybrid mode, null for Firely mode) without asserting on behavior differences yet (there are none yet).

- [ ] **Step 5: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
```

Expected: clean build, ALL existing tests pass with IDENTICAL behavior (this task changes a parameter type and adds a dead branch, nothing else).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "US-16 (Task 2): Bind batch/transaction body as ResourceElement, add mode-branch scaffold

Pure plumbing change -- binding type changes, BundleHandler gains a
GetIgnixaNode()-gated branch point, but the native branch currently just
delegates to the existing POCO path. Zero behavior change in any mode;
proves the routing compiles before Tasks 3+ build real native logic on it."
```

---

### Task 3: Native decomposition (batch scope)

**Files:**
- Create: `src/Microsoft.Health.Fhir.Shared.Api/Features/Resources/Bundle/IBundleEntryView.cs`
- Create: `src/Microsoft.Health.Fhir.Shared.Api/Features/Resources/Bundle/FirelyBundleEntryView.cs`
- Create: `src/Microsoft.Health.Fhir.Shared.Api/Features/Resources/Bundle/IgnixaBundleEntryView.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Features/Resources/Bundle/BundleHandler.cs`
- Modify test files accordingly

**Interfaces:** New `IBundleEntryView` abstraction — `GenerateRequest`/`FillRequestLists`'s existing logic gets rewritten ONCE against this interface (not forked into a parallel native method), with two implementations.

**Depends on:** Task 2. Scoped to BATCH bundles only (per Global Constraints — transactions still take the POCO path until Task 6).

- [ ] **Step 1: Read `GenerateRequest`/`FillRequestLists` in full**

Read the complete current decomposition logic in `BundleHandler.cs`. Identify every field/method it reads off a Firely `Bundle.EntryComponent`: `Method`, `Url`/`RequestUrl`, `FullUrl`, conditional headers (`IfMatch`, `IfNoneMatch`, `IfModifiedSince`, `IfNoneExist`), the resource's type name, whether a resource is present, and how the resource body gets serialized into the inner HTTP request (note: there are TWO body shapes — the common "serialize the resource directly" case, AND a Binary-wrapped-JSON-Patch case where the body is base64-decoded from `Binary.Data` with content type `application/json-patch+json`, plus a Parameters-FHIRPatch case with `application/fhir+json` — read the exact code branching on these, this is more than a single `WriteResourceBody(Stream)` call can express).

- [ ] **Step 2: Design and implement `IBundleEntryView`**

Based on Step 1's findings (not the sketch below, which may be incomplete — expand it to cover everything Step 1 found):

```csharp
public interface IBundleEntryView
{
    string Method { get; }
    string Url { get; }
    string FullUrl { get; }
    string IfMatch { get; }
    string IfNoneMatch { get; }
    string IfModifiedSince { get; }
    string IfNoneExist { get; }
    string ResourceTypeName { get; }
    bool HasResource { get; }
    string BodyContentType { get; }          // e.g. "application/fhir+json", "application/json-patch+json"
    void WriteBody(Stream stream);            // writes whatever BodyContentType declares
    void SetResourceId(string id);            // for PopulateReferenceIdDictionary/conditional-create id assignment
}
```

Implement `FirelyBundleEntryView` (wraps `Bundle.EntryComponent`, extracting the EXISTING logic verbatim — this should be a near-mechanical move, not new logic) and `IgnixaBundleEntryView` (wraps `BundleComponentJsonNode`, reading properties via `Metadata.MutableNode` where the typed property doesn't exist — see Step 3 for the known gap). Rewrite `GenerateRequest`/`FillRequestLists` to consume `IBundleEntryView` instead of `Bundle.EntryComponent` directly, with a small adapter step converting the incoming bundle's entries to views based on `GetIgnixaNode() != null`.

- [ ] **Step 3: Handle the missing conditional-header properties**

`BundleComponentRequestJsonNode` (as of the design research) exposes only `Method`/`Url` — `ifMatch`/`ifNoneMatch`/`ifModifiedSince`/`ifNoneExist` are missing from its typed surface. Read them via the public `IMutableJsonNode` explicit-interface-cast access pattern (NOT the `internal MutableNode` property directly — confirm which is actually accessible from this project; the design review flagged `BaseJsonNode.MutableNode` as `internal`, so use whatever public accessor exists, matching the pattern `IgnixaImportResourceParser`'s existing raw-node-property-access code already uses). File an upstream issue for this gap per the established practice — read `docs/features/sdk-migration/ignixa-upstream-gaps.md` for the format, open a GitHub issue in `brendankowitz/ignixa-fhir` describing the missing properties, and add a row to the tracker.

- [ ] **Step 4: Tests**

Write tests proving: (a) `FirelyBundleEntryView`-driven decomposition produces IDENTICAL inner-request construction to the pre-refactor code (a regression guard for the extraction in Step 2 — this should be provable by running the EXISTING `BundleHandlerTests` unchanged and confirming they still pass, since this is meant to be a behavior-preserving refactor of the Firely path); (b) `IgnixaBundleEntryView`-driven decomposition, for a batch bundle with a mix of entry types (plain resource create/update, a Binary-wrapped JSON-Patch entry, a Parameters-FHIRPatch entry, an entry with conditional headers), produces inner requests with correct method/url/headers/body — assert against the actual `HttpRequestMessage`-equivalent construction this handler builds, not just presence of data.

- [ ] **Step 5: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
```

Expected: clean build, ALL existing `BundleHandlerTests` pass unchanged (proving the Firely-path extraction was behavior-preserving), plus new Ignixa-path tests.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "US-16 (Task 3): IBundleEntryView + native batch decomposition

GenerateRequest/FillRequestLists rewritten once against a new
IBundleEntryView abstraction (Firely and Ignixa implementations), instead
of being forked into a parallel native method. Covers both body shapes
(direct resource serialize, and Binary-wrapped JSON-Patch/Parameters
FHIRPatch). Files an upstream gap for BundleComponentRequestJsonNode's
missing conditional-header properties. Scoped to batch bundles -- native
decomposition of transaction entries lands in a later task once reference
resolution (Task 1) is wired in (Task 6)."
```

---

### Task 4: Native recomposition (batch scope)

**Files:**
- Create: `src/Microsoft.Health.Fhir.Shared.Api/Features/Resources/Bundle/IBundleResponseAssembler.cs` (or similar name)
- Create: `src/Microsoft.Health.Fhir.Shared.Api/Features/Resources/Bundle/FirelyBundleResponseAssembler.cs`
- Create: `src/Microsoft.Health.Fhir.Shared.Api/Features/Resources/Bundle/IgnixaBundleResponseAssembler.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Features/Resources/Bundle/BundleHandler.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Features/Resources/Bundle/BundleHandlerParallelOperations.cs`
- Modify test files accordingly

**Interfaces:** New response-assembler seam, both sequential and parallel execution paths route through it.

**Depends on:** Task 3 and Phase 4 Plan A's Tasks 1 and 3 (the `IgnixaRawBundle` carrier and `FhirResult`/formatter handling this task's Ignixa assembler produces output for).

**This is the largest single task in this plan — read this whole task before starting any step.**

- [ ] **Step 1: Read the complete recomposition surface**

Read `CreateEntryComponent` (both call sites — sequential in `BundleHandler.cs`, parallel in `BundleHandlerParallelOperations.cs:390`) plus every OTHER path that constructs a response entry:
- The success path: parses the inner response body as a Firely `Resource`/`OperationOutcome` POCO.
- `PublishNotification` — reads `entry.Response.Status` + `entry.Resource?.TypeName` for metrics (`BundleHandler.cs` around line 534-556).
- `GetFinalHttpStatusCode` — parses the aggregate status from entry statuses.
- **Transaction failure handling**: `TransactionExceptionHandler.ThrowTransactionException(..., (OperationOutcome)entryComponent.Response.Outcome, ...)` (`BundleHandlerParallelOperations.cs:470`) — this REQUIRES a Firely `OperationOutcome` POCO even on the native path's failure branch. This means "zero Firely parse per entry" only holds on SUCCESS paths — the failure path needs either one targeted parse, or (better, if feasible) a `TransactionExceptionHandler` overload/branch that accepts a node-based outcome. Read `TransactionExceptionHandler`'s full signature and decide which is cleaner; document your choice in the report.
- Empty-request entries, cancelled-retry entries (`CreateEntryComponentForCancelledRequest`), the `_isBundleProcessingLogicValid` warning entry, indexed slot assignment (`responseBundle.Entry[index] = ...`), and a shared throttled-entry object reused across multiple slots — all of these must be representable by whatever assembler abstraction you design; enumerate each one explicitly in your implementation, don't discover them as failing tests.

- [ ] **Step 2: Design and implement the response-assembler seam**

Design an interface (exact shape driven by Step 1's findings — don't force-fit a shape decided in advance) with a Firely implementation (current behavior, extracted verbatim) and an Ignixa implementation building up `IgnixaRawBundleEntry` instances into a `List<IgnixaRawBundleEntry>`, with the Ignixa path peeking each inner response body's `resourceType` via a lightweight `Utf8JsonReader` scan (not a full parse) to decide: `OperationOutcome` → parse just that small body into a `ResourceJsonNode` for `response.outcome`; anything else → wrap the raw bytes as a `RawResourceElement` for zero-copy splice. Both the sequential (`BundleHandler.cs`) and parallel (`BundleHandlerParallelOperations.cs`) recomposition call sites must route through the SAME assembler interface — no forked logic between them beyond what already differs (concurrency handling), matching how the codebase already shares `CreateEntryComponent` between both paths today.

At the end of processing, the Ignixa assembler constructs one `IgnixaRawBundle` (skeleton + the accumulated entry list) — per the Global Constraints, build up a mutable list throughout and construct the immutable carrier once, at the end.

- [ ] **Step 3: Tests**

Prove (a) the Firely assembler extraction is behavior-preserving — existing `BundleHandlerTests`/`BundleHandlerParallelOperationsTests` (or equivalent) pass unchanged; (b) the Ignixa assembler correctly handles every case enumerated in Step 1 — success entries (both raw-splice and OperationOutcome-parsed), failure entries (confirm `TransactionExceptionHandler` still receives a usable outcome), cancelled/throttled entries, and status-code aggregation; (c) a batch bundle end-to-end (through Task 3's decomposition AND this task's recomposition) in Ignixa/Hybrid mode produces a correct `IgnixaRawBundle` matching what the equivalent Firely-mode batch would semantically produce (same entry count, same statuses, same resource bodies) — this is the point where batch processing is genuinely native end-to-end for the first time; make this an explicit, clearly-labeled test.

- [ ] **Step 4: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "US-16 (Task 4): Native batch recomposition

Response-assembler seam (Firely/Ignixa) feeding both the sequential and
parallel recomposition paths. Covers the full response surface: success
(raw-splice or parsed-outcome), transaction failure (still requires one
Firely OperationOutcome parse -- TransactionExceptionHandler's contract),
cancelled/throttled/empty entries, status aggregation. Batch bundles are
now genuinely end-to-end native in Ignixa/Hybrid mode; transactions still
route through the POCO path pending Task 6's reference-resolver wiring."
```

---

### Task 5: Native metadata-only ports (transaction validation, reference-id tracking, search-param conflict check)

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Features/Resources/Bundle/BundleHandler.cs` (or wherever `TransactionBundleValidator`/`PopulateReferenceIdDictionary`/search-param-conflict-check live)
- Modify test files accordingly

**Interfaces:** These are metadata-only reads (no resource body parsing) — extend the `IBundleEntryView` abstraction from Task 3 if these reads fit it cleanly, or add a small parallel native read path if they don't (judgment call, see Step 1).

**Depends on:** Task 3.

- [ ] **Step 1: Read the three pieces in full**

Read `TransactionBundleValidator`'s full validation logic (reads `Request.Url`/`Method`/`IfNoneExist`, `FullUrl`, `Resource.TypeName`, plus one Firely-specific detail: raw-verb validation via `Request.MethodElement.ObjectValue` — confirm this exact detail and how to reproduce it from the JSON `method` string directly). Read `PopulateReferenceIdDictionary` (sets an entry's resource id — should already be expressible via `IBundleEntryView.SetResourceId` from Task 3, confirm). Read the search-parameter-conflict-check logic (`CheckSearchParamInputConflictsAndUpdateCache` or similarly named — reads SearchParameter `code`/`base`/`url`; check whether Ignixa's `SearchParameterJsonNode` type, if it exists, covers this surface, or whether raw `MutableNode` reads are needed for some fields).

- [ ] **Step 2: Implement native ports**

Port each piece so it works against `IBundleEntryView`/`ResourceJsonNode` inputs when running the native path, reusing Task 3's `IgnixaBundleEntryView` where its surface already covers what's needed, extending it if a genuinely new metadata field is needed (prefer extending the existing view over inventing a third abstraction). Every decision/validation outcome must match the Firely baseline exactly — these are metadata reads with real business logic (e.g., raw-verb validation rejecting bad HTTP methods), not just data plumbing.

- [ ] **Step 3: Tests**

Parity tests: same bundle (with a few deliberately-invalid entries — bad verb, missing `IfNoneExist` where required, etc.) through both the Firely and Ignixa validation paths, asserting identical accept/reject decisions and identical error messages where the baseline produces one.

- [ ] **Step 4: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "US-16 (Task 5): Native transaction validation, reference-id tracking, search-param conflict check

Ports TransactionBundleValidator, PopulateReferenceIdDictionary, and the
search-parameter-conflict check onto the native entry-view abstraction.
Metadata-only reads, parity-tested against the Firely baseline's exact
accept/reject decisions and error messages. Transactions still route
through the POCO path end-to-end pending Task 6's reference-resolver
wiring -- this task's native validation logic is exercised by tests, not
yet reachable via the public API."
```

---

### Task 6: Wire native transactions end-to-end

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Features/Resources/Bundle/BundleHandler.cs`
- Modify test files accordingly

**Interfaces:** None new — this task connects Tasks 1, 3, 4, and 5's already-built pieces into the live transaction path.

**Depends on:** Tasks 1, 3, 4, 5 all complete.

- [ ] **Step 1: Wire `IgnixaResourceReferenceResolver` into `GenerateRequest`'s transaction branch**

Find the existing `_bundleType == BundleType.Transaction && entry.Resource != null` guard. Inside it, branch on `GetIgnixaNode() != null`: native path calls `IgnixaResourceReferenceResolver.ResolveReferencesAsync` (Task 1's output) on the entry's node; POCO path is unchanged.

**Apply the node-mutation.md rule explicitly here**: the resolver mutates the entry's node in place (`InvalidateCaches()` is called internally by the resolver per Task 1's design, but that only refreshes the resolver's OWN cached view — trace whether the mutated node then flows into Task 3's decomposition (`IgnixaBundleEntryView.WriteBody`) via `MutableNode` (a live, always-fresh read) or via some cached typed-element view. If it's the former, write the explicit consumer-trace comment at this call site proving it (mirroring `TryAddSoftDeletedExtension`'s pattern from Phase 3 — "traced, single consumer, reads via X not Y"). If it's the latter, this needs a rebuild via `RebuildResourceElement` before decomposition proceeds. Do not guess — trace it.**

- [ ] **Step 2: Remove the transaction-scope limitation**

Now that reference resolution is wired, confirm the native branch from Tasks 3/4/5 no longer needs to special-case "batch only" — transactions should now flow through the exact same native decomposition/recomposition/validation as batch, with the addition of Task 1's resolution step.

- [ ] **Step 3: Tests**

End-to-end native transaction tests: a transaction bundle with `urn:uuid:` intra-bundle references (the case Task 1's parity tests already cover at the resolver level — now prove it end-to-end through the full `BundleHandler` pipeline), a transaction with a conditional reference, a transaction that should roll back on a mid-bundle failure (confirm rollback behavior is identical to Firely mode — this exercises the engine code that Task Global Constraints say must be untouched, so this is really a regression-guard test for "did we accidentally change the untouched engine," not new behavior). Run the FULL existing `TransactionTests` integration suite (find it — likely in an integration test project) against Ignixa/Hybrid mode if the environment supports running it.

- [ ] **Step 4: Build and full test suite**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "US-16 (Task 6): Wire native transaction reference resolution end-to-end

Connects Task 1's IgnixaResourceReferenceResolver into GenerateRequest's
transaction branch. Transactions now flow through the same native
decomposition/recomposition/validation path as batch (Tasks 3-5), with
reference resolution as the one transaction-specific addition. This
closes US-16: FhirSdkMode.Ignixa/Hybrid now processes both batch and
transaction bundles without Firely POCO parsing on the success path,
except the one documented TransactionExceptionHandler failure-path parse."
```

---

### Task 7: Integration/E2E parity sweep

**Files:** None fixed.

**Depends on:** Task 6.

- [ ] **Step 1: Run the full existing batch/transaction integration and E2E suites across all three `SdkMode` values.**

- [ ] **Step 2: Specifically exercise, in Ignixa/Hybrid mode**: batch with an inner search entry (confirm it correctly rides Phase 4 Plan A's native search path AND re-splices into the outer batch response without re-parsing), PATCH entries (both Parameters-FHIRPatch and Binary-wrapped-JSON-Patch), conditional create/update inside a transaction, a transaction that fails and rolls back, 429 throttling/retry behavior, `Prefer` header propagation (`return=minimal`/`representation`/`OperationOutcome`), and large bundles (confirm no unexpected behavior change in the parallel execution path specifically, since it has its own recomposition call site).

- [ ] **Step 3: For every gap found**, fix if small and clearly in-scope; STOP and report BLOCKED with specifics if it reveals a real design gap.

- [ ] **Step 4: Commit** whatever fixes were needed. A clean sweep (zero fixes needed) is a valid, good outcome.

---

## After This Plan

US-16 is complete: `FhirSdkMode.Ignixa`/`Hybrid` process both batch and transaction bundles natively, with `BundleHandler`'s engine (routing, orchestration, throttling, statistics, transaction rollback) completely unchanged and Firely mode byte-identical to today. Combined with Phase 4 Plan A (US-15), the two highest-severity remaining items from the original merge-readiness backlog are done. Genuinely new, reusable infrastructure produced along the way: `IgnixaReferenceScanner`/`IgnixaResourceReferenceResolver` (Task 1) — file a follow-on task (not part of this plan) to wire this into `CreateResourceHandler`/`UpsertResourceHandler`, finally closing the previously-deferred US-12/US-13, since the design work for that is now done as a side effect of this plan. Also log, in this project's own gap register (not the upstream-Ignixa tracker), the pre-existing narrower gap found during Task 1: `IgnixaImportResourceParser.CheckConditionalReferenceInResource`'s metadata+FHIRPath-based detection misses references inside extensions/contained resources during import — `IgnixaReferenceScanner` is the tool that would fix it, whenever that's prioritized.
