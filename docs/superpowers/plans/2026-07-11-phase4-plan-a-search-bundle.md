# Phase 4 Plan A: Ignixa-Native Search/History Bundle Assembly (US-15) + XML Formatter Gap

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `FhirSdkMode.Ignixa`/`Hybrid` assemble search/history bundle responses natively — a second `IBundleFactory` implementation backed by Ignixa's `BundleJsonNode`, with entry bodies staying zero-copy exactly as the existing raw-splice mechanism (US-2, `BundleSerializer`) already proves out — and close a real content-negotiation gap this activates in the default (Hybrid) mode: node-backed results currently 406 on `Accept: application/fhir+xml`.

**Architecture:** `IBundleFactory` (`src/Microsoft.Health.Fhir.Core/Features/Search/IBundleFactory.cs`) already IS the abstraction US-15 needs — this plan adds a second implementation and mode-selects it, mirroring `ValidationModule`/`OperationsModule`'s established pattern. No new top-level abstraction is invented. A new sealed carrier type (`IgnixaRawBundle`) holds the assembled bundle as a skeleton (`BundleJsonNode`, no entry array) plus a list of entries (metadata node + either a raw resource body or a constructed node body) — deliberately NOT a `BundleJsonNode` subclass, because `BundleJsonNode`'s serialization is a non-overridable static extension that cannot special-case raw-body splicing; a distinct type makes "this can only be serialized by the one serializer written for it" true by construction, not by convention.

**Tech Stack:** C#/.NET 9, `System.Text.Json.Nodes`/`Utf8JsonWriter`, `Ignixa.Serialization.SourceNodes` (`BundleJsonNode`, `BundleComponentJsonNode`, etc.), xunit.

**Design provenance:** This plan implements a design produced by one Fable research/design pass and independently adversarially reviewed by a second Fable pass (full transcripts available via the session's agent history if needed; the load-bearing conclusions are folded into the tasks below). The companion plan (Phase 4 Plan B, US-16 batch/transaction) is a separate document — it depends on this plan's carrier/serializer (Task 1) and formatter branches (Task 3), but is scoped and sequenced independently because of its much larger size and a Critical redesign needed for its reference-resolution piece.

## Global Constraints

- This repo enforces StyleCop alphabetical `using` ordering as a build error (`TreatWarningsAsErrors`).
- The shared-project pattern requires every `.cs` file to be listed in the owning layer's `.projitems` file.
- Build verification command for every task: `dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60` — `0 Warning(s)` beyond pre-existing. Pre-existing, unrelated, NOT-to-fix failures: the four `*.Tests.E2E` SDK-version environment failures, occasional transient Roslyn/MSBuild crashes (retry once).
- **Task ordering is load-bearing, not just narrative**: Task 4 (XML formatter accept+convert) MUST land and be verified before Task 5 (mode-selection wiring flip). Landing Task 5 first would put live Ignixa bundles in the default Hybrid mode with no XML handling — a real regression window, not a theoretical one (MVC's `RespectBrowserAcceptHeader=true` means any XML-Accept client would 406 immediately). Do not reorder.
- Every task must be additive: Firely mode's `BundleFactory`/`BundleSerializer`/`RawBundleEntryComponent` path must be completely unchanged by this plan — verify with existing `BundleFactoryTests`/`BundleSerializerTests` passing unmodified throughout.
- Per `docs/features/sdk-migration/node-mutation.md`'s established rule: `IgnixaRawBundle` and its constituent nodes are assembled once and never mutated afterward — this plan's design deliberately avoids the reuse-vs-rebuild hazard by making the carrier immutable post-construction. If any task's implementation needs to mutate a node after initial construction (it shouldn't), stop and treat that as a design deviation requiring escalation, not a routine implementation detail.

---

### Task 1: `IgnixaRawBundle` carrier + `IgnixaBundleSerializer`

**Files:**
- Create: `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/Bundle/IgnixaRawBundle.cs`
- Create: `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/Bundle/IgnixaBundleSerializer.cs`
- Create: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Resources/Bundle/IgnixaBundleSerializerTests.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Microsoft.Health.Fhir.Shared.Core.projitems`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Microsoft.Health.Fhir.Shared.Core.UnitTests.projitems`

**Interfaces:** New public types, no existing interfaces touched. This task produces the substrate everything else in this plan (and Phase 4 Plan B) builds on — get the serializer's parity with `BundleSerializer` exactly right, since it's the highest-reuse, highest-leverage piece.

- [ ] **Step 1: Read the precedent in full**

Read `src/Microsoft.Health.Fhir.Shared.Core/Features/Resources/Bundle/BundleSerializer.cs` and `src/Microsoft.Health.Fhir.Core/Extensions/RawResourceElementExtensions.cs` (specifically `SerializeToStreamAsUtf8Json`) in full. Note exactly how `BundleSerializer` interleaves a `Utf8JsonWriter` (for the skeleton/metadata) with raw splice writes (for entry bodies) — in particular its manual comma-writing logic (it writes commas by hand because the outer writer doesn't know spliced bytes exist), and confirm which properties it writes into the object *before* the splice vs. *after*. You are generalizing this pattern for an arbitrary property set (Task 2's factory will produce entries with varying metadata shapes — search-mode entries, history entries with different verbs, OperationOutcome issue entries), not literally copying `BundleSerializer`'s hardcoded field list.

Also confirm: `SerializeToStreamAsUtf8Json` patches `meta.versionId` unconditionally and `meta.lastUpdated` conditionally (when `IsMetaSet` is false on the `RawResourceElement`), including synthesizing a whole `meta` object if one is absent. Any entry-body splice in the new serializer MUST go through this exact method for `RawResourceElement`-backed bodies — do not write a simplified raw-string splice that bypasses this patching, or search results will silently regress stale/missing `versionId`/`lastUpdated`.

- [ ] **Step 2: Define the carrier types**

Create `IgnixaRawBundle.cs`:

```csharp
namespace Microsoft.Health.Fhir.Core.Features.Resources.Bundle;

/// <summary>
/// Carries an Ignixa-native bundle response for zero-copy serialization. Immutable after
/// construction -- do not mutate any node reachable from this type after building it. See
/// docs/features/sdk-migration/node-mutation.md for why (this type sidesteps the reuse-vs-rebuild
/// hazard entirely by never being mutated post-construction).
///
/// Deliberately NOT a subclass of <see cref="BundleJsonNode"/>: BundleJsonNode's serialization is a
/// non-overridable static extension, so a subclass could not make generic serialization correct --
/// any code path that grabbed the node and serialized it generically would silently emit a bundle
/// with no entry bodies. This type can only be serialized by <see cref="IgnixaBundleSerializer"/>.
///
/// WARNING: calling ResourceElement.ToPoco() on the ResourceElement wrapping this carrier does NOT
/// throw, and does NOT produce an error -- it silently returns a Bundle POCO with the correct
/// id/meta/type/total/link but a hollow (empty) entry array, since only the skeleton participates in
/// the typed-element view. Every consumer of this bundle must read it via GetIgnixaRawBundle()
/// (mirroring GetIgnixaNode()), never via ToPoco()/.Instance. As of this writing, all six production
/// consumers of IBundleFactory are pure pass-throughs to FhirResult and never call ToPoco() -- verify
/// this is still true before adding a new consumer.
/// </summary>
public sealed class IgnixaRawBundle
{
    public IgnixaRawBundle(BundleJsonNode skeleton, IReadOnlyList<IgnixaRawBundleEntry> entries)
    {
        EnsureArg.IsNotNull(skeleton, nameof(skeleton));
        EnsureArg.IsNotNull(entries, nameof(entries));

        Skeleton = skeleton;
        Entries = entries;
    }

    /// <summary>The bundle's id/meta/type/total/link properties. Its entry array, if any, is ignored by the serializer -- <see cref="Entries"/> is authoritative.</summary>
    public BundleJsonNode Skeleton { get; }

    public IReadOnlyList<IgnixaRawBundleEntry> Entries { get; }
}

public sealed class IgnixaRawBundleEntry
{
    private IgnixaRawBundleEntry(BundleComponentJsonNode metadata, RawResourceElement rawResource, ResourceJsonNode resourceNode)
    {
        Metadata = metadata;
        RawResource = rawResource;
        ResourceNode = resourceNode;
    }

    /// <summary>fullUrl/search/request/response properties. Its resource property, if any, is ignored by the serializer.</summary>
    public BundleComponentJsonNode Metadata { get; }

    /// <summary>Non-null for entries whose body is a raw, unparsed resource read from storage (the common case -- search/history results).</summary>
    public RawResourceElement RawResource { get; }

    /// <summary>Non-null for entries whose body was constructed in-process (e.g. an OperationOutcome issue entry). Mutually exclusive with <see cref="RawResource"/>; entries may also have neither (a request-only entry, e.g. a batch error with no resource body).</summary>
    public ResourceJsonNode ResourceNode { get; }

    public static IgnixaRawBundleEntry ForRawResource(BundleComponentJsonNode metadata, RawResourceElement rawResource)
    {
        EnsureArg.IsNotNull(metadata, nameof(metadata));
        EnsureArg.IsNotNull(rawResource, nameof(rawResource));
        return new IgnixaRawBundleEntry(metadata, rawResource, null);
    }

    public static IgnixaRawBundleEntry ForConstructedResource(BundleComponentJsonNode metadata, ResourceJsonNode resourceNode)
    {
        EnsureArg.IsNotNull(metadata, nameof(metadata));
        EnsureArg.IsNotNull(resourceNode, nameof(resourceNode));
        return new IgnixaRawBundleEntry(metadata, null, resourceNode);
    }

    public static IgnixaRawBundleEntry MetadataOnly(BundleComponentJsonNode metadata)
    {
        EnsureArg.IsNotNull(metadata, nameof(metadata));
        return new IgnixaRawBundleEntry(metadata, null, null);
    }
}
```

Adjust exact namespace/using conventions to match this file's siblings (check `BundleSerializer.cs`'s namespace declaration and copy it). Verify the actual public type names for `BundleJsonNode`/`BundleComponentJsonNode` and their property surface against the pinned Ignixa package version (`Directory.Packages.props`'s `IgnixaPackageVersion`) — the sketch above assumes the shapes described in this plan's design research; confirm before finalizing.

- [ ] **Step 3: Implement `IgnixaBundleSerializer`**

Create `IgnixaBundleSerializer.cs` with a method matching `BundleSerializer`'s shape (e.g. `Task Serialize(IgnixaRawBundle bundle, Stream stream, bool pretty)`), generalizing the interleaved-writer/comma-dance technique from Step 1:

1. Open a `Utf8JsonWriter` (same `JsonWriterOptions` as `BundleSerializer` — `UnsafeRelaxedJsonEscaping`, pretty-mode-conditional indentation) and a `StreamWriter` over the same stream for splice writes, matching `BundleSerializer`'s existing dual-writer setup exactly (read its constructor/setup code, don't reinvent it).
2. Write the outer object start, then each of `Skeleton`'s properties EXCEPT `entry` by iterating `Skeleton.MutableNode`'s properties and writing each via `WritePropertyName` + `property.Value.WriteTo(writer)` (NOT a single `WriteTo` call on the whole node — that would write a complete value, not properties into an already-open object).
3. Write the `entry` array: for each `IgnixaRawBundleEntry`, write `{`, then `Metadata`'s properties (again per-property, EXCEPT any `resource` property) via the same technique, then:
   - if `RawResource != null`: write `"resource":`, flush the `Utf8JsonWriter`, call `rawResource.SerializeToStreamAsUtf8Json(streamWriter, ...)` (match its actual signature — check whether it takes a `Stream` or `TextWriter`, and pass whatever `pretty`/version-context parameters it requires, matching how `BundleSerializer` calls it), then resume the `Utf8JsonWriter` for the entry's closing `}` and the following comma/next-entry logic;
   - if `ResourceNode != null`: write `"resource":` then `resourceNode.MutableNode.WriteTo(writer)` (a complete value write is correct here, since this body is already fully in the DOM);
   - if neither: write nothing further for this entry.
   - Close the entry object.
4. Close the `entry` array and the outer object. Flush both writers.

Match `BundleSerializer`'s EXACT existing quirks, not the FHIR spec's idealized behavior, so parity tests (Step 4) have a real baseline to check against:
   - `pretty` is NOT propagated into spliced entry bodies (confirm this is true of `BundleSerializer` in Step 1's read, and replicate it — entry bodies stay however they were stored, only the skeleton/metadata respect `pretty`).
   - The splice path is always UTF-8 (does not honor `selectedEncoding`) — confirm and replicate.

- [ ] **Step 4: Write parity tests**

Create `IgnixaBundleSerializerTests.cs`. Build a small in-memory `IgnixaRawBundle` (a skeleton with 2-3 properties, 2-3 entries mixing `RawResource`- and `ResourceNode`-backed bodies, including at least one entry with neither), serialize it, and assert:
- The output is valid JSON (parse it back).
- Every skeleton property appears at the top level.
- Every entry's metadata + resource body appears correctly nested.
- A `RawResource` entry's `meta.versionId`/`meta.lastUpdated` reflect the patching behavior from Step 1 when `IsMetaSet` is false (construct a `RawResourceElement` deliberately missing meta and confirm the serializer's output has it synthesized correctly — this is the regression guard for the meta-patching reuse requirement).
- `pretty: true` indents the skeleton/metadata but does NOT re-indent a raw-spliced entry body (matching `BundleSerializer`'s existing behavior) — construct a body with specific whitespace/formatting and confirm it passes through unchanged.

- [ ] **Step 5: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
```

Expected: clean build, all existing tests pass unchanged, plus new `IgnixaBundleSerializerTests` passing.

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "US-15 (Task 1): IgnixaRawBundle carrier + IgnixaBundleSerializer

New sealed carrier type for Ignixa-native bundle assembly -- a skeleton
BundleJsonNode plus a list of entries, each holding metadata plus either a
raw (zero-copy) or constructed resource body. Deliberately not a
BundleJsonNode subclass, since BundleJsonNode's serialization is a
non-overridable static extension that can't special-case raw splicing.
IgnixaBundleSerializer generalizes BundleSerializer's proven
interleaved-writer splice technique (including its meta-patching reuse via
RawResourceElement.SerializeToStreamAsUtf8Json) for this carrier's
variable entry shapes. No production code wires this in yet -- pure
addition, unreachable until Task 5."
```

---

### Task 2: `IgnixaBundleFactory` + parity tests

**Files:**
- Create: `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/IgnixaBundleFactory.cs`
- Create: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Search/IgnixaBundleFactoryTests.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core/Microsoft.Health.Fhir.Shared.Core.projitems`
- Modify: `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Microsoft.Health.Fhir.Shared.Core.UnitTests.projitems`

**Interfaces:** Produces `IgnixaBundleFactory : IBundleFactory` — no changes to `IBundleFactory` itself. Not wired into DI yet (Task 5).

- [ ] **Step 1: Read the precedent in full**

Read `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/BundleFactory.cs` in full — `CreateSearchBundle`, `CreateHistoryBundle`, `CreateBundle`, `CreateLinks`, the search-mode/history-verb/`IsDeleted` logic, and `BundleIssues`/`SearchIssues` OperationOutcome-entry construction. You are porting this logic 1:1 onto Ignixa node types, not redesigning it — every link/verb/mode decision the Firely version makes, the Ignixa version must make identically, just writing to a different node API.

- [ ] **Step 2: Implement `IgnixaBundleFactory`**

Create `IgnixaBundleFactory.cs` implementing `IBundleFactory`. Port each method from `BundleFactory`, writing to `BundleJsonNode`/`BundleLinkJsonNode`/`BundleComponentJsonNode`/`BundleComponentSearchJsonNode` (`.Mode`)/`BundleComponentRequestJsonNode`/`BundleComponentResponseJsonNode` instead of the Firely POCO equivalents. Confirm each of these node types' actual property surface against the pinned Ignixa package version before assuming a property exists (the plan's design research found `Response.Outcome` and `Response.Location` exist upstream — confirm, don't assume).

For bundle-level and search-level `OperationOutcome` issue entries (`BundleIssues`/`SearchIssues` in the Firely version), write a small local mapper converting the FHIR server's internal `OperationOutcomeIssue` model to an upstream `OperationOutcomeJsonNode` (check this type exists with the needed `issue` array shape; if the exact type name differs, use whatever Ignixa's `Ignixa.Serialization` package actually calls its OperationOutcome node type).

History-entry verb/status mapping (including the STU3 PATCH→PUT mapping and the Import pseudo-verb, and `IsDeleted` sourced from the `ResourceWrapper`) — port this string-level logic directly; it doesn't depend on Firely types.

Construct the returned `ResourceElement` as:
```csharp
var rawBundle = new IgnixaRawBundle(skeleton, entries);
var ignixaElement = new IgnixaResourceElement(skeleton, schemaContext.Schema);
return new ResourceElement(ignixaElement.ToTypedElement(), rawBundle);
```
This gives `GetResultTypeName()`/audit logging a working `InstanceType` ("Bundle") via the skeleton-backed typed element, while `ResourceInstance` (the thing `GetIgnixaRawBundle()`/`ToPoco()` see) is the carrier. Verify the two-arg `ResourceElement` constructor is reachable from this assembly (it's `internal` — confirm `InternalsVisibleTo` covers `Microsoft.Health.Fhir.*.Core`, which it already must, since other Shared.Core code constructs `ResourceElement` this way).

Add the `GetIgnixaRawBundle()` extension method (paralleling `GetIgnixaNode()`) to `src/Microsoft.Health.Fhir.Core/Extensions/ResourceElementIgnixaExtensions.cs`: `public static IgnixaRawBundle GetIgnixaRawBundle(this ResourceElement resourceElement) => resourceElement.ResourceInstance as IgnixaRawBundle;`

- [ ] **Step 3: Write parity tests**

Read `src/Microsoft.Health.Fhir.Shared.Core.UnitTests/Features/Search/BundleFactoryTests.cs` (or wherever `BundleFactory`'s existing tests live) to find its test-data construction pattern (how it builds a `SearchResult` fixture). Reuse the SAME `SearchResult` fixtures/builder through both `BundleFactory` and `IgnixaBundleFactory`, and assert semantic equivalence of the output: same link URLs/rel values, same total, same entry count, same per-entry search-mode/verb/status, same `IsDeleted` handling. You do not need byte-identical JSON (the two serializers have different internal formatting) — assert on the logical structure (parse both outputs, compare the parsed trees' relevant fields), which is a stronger and more maintainable test than string comparison.

Include a case for `CreateHistoryBundle` with a deleted resource (to exercise the `IsDeleted`/`DELETE` verb path) and a case with a bundle-level `OperationOutcome` issue entry.

Add a regression test proving the "no consumer calls ToPoco() on this" invariant documented on `IgnixaRawBundle`: construct a bundle via `IgnixaBundleFactory`, assert `resourceElement.GetIgnixaRawBundle() != null`, and separately (as documentation of the hazard, not a thing to "fix") assert that `resourceElement.ToPoco<Bundle>().Entry.Count == 0` while `resourceElement.GetIgnixaRawBundle().Entries.Count > 0` — i.e., explicitly prove and lock in the hollow-vs-real discrepancy so a future change to this behavior is caught by a failing test, not discovered in production.

- [ ] **Step 4: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "US-15 (Task 2): IgnixaBundleFactory

Second IBundleFactory implementation, porting BundleFactory's link/entry/
search-mode/history-verb construction logic onto Ignixa's BundleJsonNode
family. Not wired into DI yet (Task 5) -- pure addition, unreachable.
Includes a regression test that explicitly locks in and documents the
hollow-ToPoco() hazard the IgnixaRawBundle carrier's doc comment warns
about, per this plan's design review."
```

---

### Task 3: `FhirResult` branch + JSON output formatter handling

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Features/ActionResults/FhirResult.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Features/Formatters/IgnixaFhirJsonOutputFormatter.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Features/ActionResults/FhirResultTests.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Features/Formatters/IgnixaFhirJsonOutputFormatterTests.cs`

**Interfaces:** No signature changes to `FhirResult`/`IgnixaFhirJsonOutputFormatter` — new branches in existing methods only.

- [ ] **Step 1: `FhirResult` branch**

Read the current `GetResultToSerialize()` in `FhirResult.cs` (it already has a `GetIgnixaNode()` branch from earlier Phase 3 work). Add a new check AHEAD of that branch: if `resourceElement.GetIgnixaRawBundle()` is non-null, return it directly. Order matters: `GetIgnixaRawBundle()` and `GetIgnixaNode()` are mutually exclusive by construction (a carrier-backed `ResourceElement`'s `ResourceInstance` is never simultaneously a `ResourceJsonNode`), but check the carrier first for clarity/intent even though either order is technically safe.

- [ ] **Step 2: `IgnixaFhirJsonOutputFormatter` branches**

Read the current `IgnixaFhirJsonOutputFormatter.cs` in full (`CanWriteType`, `WriteResponseBodyAsync`, and its existing `Bundle`/projection-handling branch from the earlier US-2 work). Add `IgnixaRawBundle` to `CanWriteType`. In `WriteResponseBodyAsync`, add a branch (placed sensibly relative to the existing `Bundle`/`RawBundleEntryComponent` branches — read how those are ordered and follow the same pattern):

- No projection (`_summary`/`_elements` absent or `_summary=false`-equivalent): call the new `IgnixaBundleSerializer` directly — the zero-copy path.
- Projection present: convert to a Firely `Bundle` POCO and reuse the EXISTING projection-conversion + `WriteFirelyResourceAsync` path this file already has for `RawBundleEntryComponent`-based bundles (read that existing code — it should already know how to parse a skeleton and deserialize entry bodies via `ResourceDeserializer`; your `IgnixaRawBundle` case needs the same treatment, just reading from the carrier's `Skeleton`/`Entries` instead of a Firely `Bundle`'s `RawBundleEntryComponent` entries). Do not write a second, parallel projection implementation — factor out/reuse if the existing code isn't already shaped to accept either input.

- [ ] **Step 3: Tests**

Add tests to `FhirResultTests.cs`: a carrier-backed `ResourceElement` result returns the `IgnixaRawBundle` from `GetResultToSerialize()` (construct via `IgnixaBundleFactory` from Task 2, or a minimal hand-built carrier if that's simpler for a unit test — check the file's existing pattern for how it constructs node-backed test fixtures and follow it).

Add tests to `IgnixaFhirJsonOutputFormatterTests.cs`: a no-projection carrier serializes via the zero-copy path (assert the output matches what `IgnixaBundleSerializer` alone would produce — i.e., this formatter branch doesn't do anything extra/different); a `_summary`/`_elements` request against the same carrier produces a valid projected Firely-POCO-backed JSON bundle (parse the output, confirm the elements filter was actually applied — mirror whatever the existing `RawBundleEntryComponent` projection test asserts).

- [ ] **Step 4: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
```

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "US-15 (Task 3): FhirResult + IgnixaFhirJsonOutputFormatter handle IgnixaRawBundle

FhirResult unwraps a carrier-backed ResourceElement to the raw carrier
(mirroring the existing GetIgnixaNode() branch). The JSON output formatter
gains a zero-copy no-projection path via IgnixaBundleSerializer and reuses
the existing projection-conversion machinery for _summary/_elements
requests. Still unreachable in production -- IgnixaBundleFactory isn't
wired into DI until Task 5."
```

---

### Task 4: XML (and HTML) output formatter accept + convert

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Features/Formatters/FhirXmlOutputFormatter.cs`
- Modify: `src/Microsoft.Health.Fhir.Shared.Api.UnitTests/Features/Formatters/FhirXmlOutputFormatterTests.cs`

**Interfaces:** `FhirXmlOutputFormatter`'s constructor may gain new dependencies (see Step 2) — update its DI registration and any direct test construction accordingly.

**Why this task exists and must land before Task 5:** `FhirXmlOutputFormatter.CanWriteType` currently accepts only `Resource`/`RawResourceElement`. Once `IgnixaBundleFactory` is live in Hybrid (the default mode, Task 5), any `Accept: application/fhir+xml` search/history request would 406 — a real, live regression in the default mode, not a dormant edge case (MVC is configured with `RespectBrowserAcceptHeader = true`). This closes that gap globally (for any node-backed or carrier-backed result, not just bundles) — Fable's design review flagged that a bare `ResourceJsonNode` result reaching this formatter today, from earlier Phase 3 work, may already have this exact gap for non-bundle resources; this task closes it for both cases in one pass.

- [ ] **Step 1: Read the current formatter and its dependencies**

Read `FhirXmlOutputFormatter.cs` in full — `CanWriteType`, `WriteResponseBodyAsync`, its existing constructor and fields (it likely already handles `RawBundleEntryComponent` via some deserializer — find that exact pattern, since you're extending it, not inventing a new one). Also read `IgnixaFhirJsonOutputFormatter.cs`'s constructor for reference on what dependencies (`IIgnixaJsonSerializer`, `ResourceDeserializer`, `IIgnixaSchemaContext`, etc.) are typically needed to go from an Ignixa node to a Firely POCO — you'll likely need a similar dependency here.

- [ ] **Step 2: Add `CanWriteType` support and conversion**

Add `ResourceJsonNode` and `IgnixaRawBundle` to `CanWriteType`. In `WriteResponseBodyAsync`, add conversion branches:
- `ResourceJsonNode` → Firely `Resource` POCO (via whichever mechanism is cleanest given what's already injected — `ToTypedElement().ToPoco<Resource>()` if an `IIgnixaSchemaContext` is available, or serialize-then-`FhirJsonParser`-parse if that's simpler and avoids a new dependency; prefer minimizing new constructor dependencies if an existing one already suffices).
- `IgnixaRawBundle` → Firely `Bundle` POCO: parse `Skeleton` to a `Bundle` shell, then for each entry deserialize its `RawResource`/`ResourceNode` body via the SAME `ResourceDeserializer`-based pattern this formatter (or the JSON formatter's existing projection branch, if you're reusing that code) already uses for `RawBundleEntryComponent` bodies — do not write a third, independent deserialization path.

Then hand off to the existing Firely XML serialization code, unchanged.

If this requires new constructor dependencies, add them and update this formatter's DI registration (find it — likely in `SdkModeFeatureModule.RegisterFirelyJsonFormatters`'s XML sibling, or wherever `FhirXmlOutputFormatter` is currently registered, possibly unconditionally since XML stays Firely-only in every mode per the provider-map's "deliberately unpaired" designation) and any places that construct it directly in tests.

- [ ] **Step 3: Decide on `HtmlOutputFormatter`**

Read `HtmlOutputFormatter.cs`'s `CanWriteType` — it likely also accepts only `Resource`. Browsers send `Accept: */*` in practice, so a node-backed/carrier-backed result degrading to raw JSON instead of a proper HTML narrative view is a quieter regression than XML's 406, but still worth an explicit decision rather than silent drift. Recommended: apply the same fix (accept `ResourceJsonNode`/`IgnixaRawBundle`, convert to POCO, reuse existing HTML rendering) for consistency — but if this formatter's rendering logic is substantially more involved than XML's (e.g., it does narrative-specific processing that's harder to retrofit), it's acceptable to explicitly scope this out and file it as a follow-up rather than block this task; document whichever choice you make in your report.

- [ ] **Step 4: Tests**

Add tests to `FhirXmlOutputFormatterTests.cs`: a bare `ResourceJsonNode` result serializes to valid FHIR XML matching what the equivalent Firely-POCO input would produce; an `IgnixaRawBundle` result serializes to valid FHIR XML with all entries present (this is the key regression guard — construct a carrier with 2+ entries, serialize to XML, parse the XML back, confirm entry count and key fields match). If you added `HtmlOutputFormatter` support in Step 3, add an equivalent test there too.

- [ ] **Step 5: Build and test**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
```

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "US-15 (Task 4): FhirXmlOutputFormatter accepts and converts node/carrier results

Closes a real content-negotiation gap: Accept: application/fhir+xml
against a node-backed or carrier-backed result currently 406s (XML stays
permanently Firely-POCO per the provider map's US-E3 designation, so this
formatter converts rather than gaining a native path). Must land before
Task 5 flips Hybrid mode's default IBundleFactory selection, or Hybrid
mode's default search/history responses would 406 on XML Accept."
```

---

### Task 5: Mode-selection wiring + provider-map update

**Files:**
- Modify: `src/Microsoft.Health.Fhir.Shared.Api/Modules/SearchModule.cs`
- Modify: `docs/features/sdk-migration/provider-map.md`

**Interfaces:** None new — DI registration change only.

**Depends on:** Tasks 3 and 4 (must both be complete and merged — see Global Constraints). Do not start this task if Task 4 hasn't landed.

- [ ] **Step 1: Wire mode selection**

Read `SearchModule.Load` in full (it already receives `FhirServerConfiguration` per the established pattern). Add:

```csharp
if (_configuration.CoreFeatures.SdkMode == FhirSdkMode.Firely)
{
    services.AddSingleton<IBundleFactory, BundleFactory>();
}
else
{
    services.AddSingleton<IBundleFactory, IgnixaBundleFactory>();
}
```

(Adjust to match this module's exact existing field/property naming for the config object and its existing registration style — check whether it uses the fluent `services.Add<T>()...` builder other modules use, or plain `AddSingleton`, and match it.) Remove/replace whatever unconditional `BundleFactory` registration currently exists.

- [ ] **Step 2: Update the provider map**

Read `docs/features/sdk-migration/provider-map.md`. Add a new row to the "mode-gated seam" table:

```markdown
| Search/history bundle assembly | `Shared.Core/Features/Search/BundleFactory.cs` | `Shared.Core/Features/Search/IgnixaBundleFactory.cs` | `Shared.Api/Modules/SearchModule.cs` |
```

Move this seam OUT of the "Planned pairs" table at the bottom (it's currently listed there as a future item per US-15) and remove that row.

- [ ] **Step 3: Build, full test suite, and manual smoke check**

```bash
dotnet build Microsoft.Health.Fhir.sln -c Debug --no-restore 2>&1 | tail -60
dotnet test src/Microsoft.Health.Fhir.R4.Core.UnitTests/Microsoft.Health.Fhir.R4.Core.UnitTests.csproj --no-restore
dotnet test src/Microsoft.Health.Fhir.Api.UnitTests/Microsoft.Health.Fhir.R4.Api.UnitTests.csproj --no-restore
```

Since this is the wiring flip that makes Hybrid mode's default search/history responses come from the new code path for the first time, this is the highest-value point in this plan to also run integration tests if the environment supports it (check for a `Microsoft.Health.Fhir.Shared.Tests.Integration`-style project covering search — if one exists and is runnable in this environment, run it; if not runnable here, note this in your report rather than skipping silently).

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "US-15 (Task 5): Wire IgnixaBundleFactory into Hybrid/Ignixa mode

SearchModule now mode-selects IBundleFactory, matching the established
ValidationModule/OperationsModule pattern. This is the flip that makes
Hybrid mode's (default) search/history responses come from the Ignixa-
native path for the first time -- Tasks 3 and 4 (XML/formatter handling)
must have already landed, per this plan's Global Constraints."
```

---

### Task 6: Integration/E2E sweep

**Files:** None fixed — this task's job is to find and fix gaps, not implement a predetermined change.

**Depends on:** Task 5.

- [ ] **Step 1: Run the full existing search/history/bundle-related integration and E2E suites** across all three `SdkMode` values (however this repo's test configuration selects mode — check `appsettings.json`/test configuration for how `SdkMode` is set for integration runs, and whether it needs to be parameterized to run all three, or whether separate CI configurations already exist).

- [ ] **Step 2: Specifically exercise, in Ignixa/Hybrid mode**: plain search, history, `$everything`, compartment search, DocRef, `_pretty=true`, `_summary=count`/`_summary=true`, `_elements=...`, `_count=0`, `Accept: application/fhir+xml`, `_format=xml`. Compare each response's semantic content (not necessarily byte-identical formatting) against the same request in Firely mode.

- [ ] **Step 3: For every gap found**, either fix it within this task if small and clearly in-scope for this plan, or — if it reveals a real design gap rather than an implementation bug — STOP and report BLOCKED with specifics rather than papering over it with a workaround.

- [ ] **Step 4: Commit** whatever fixes were needed, with a commit message describing what integration gap each one closes. If zero fixes were needed, report that explicitly (a clean sweep is a valid, good outcome) rather than fabricating busywork.

---

## After This Plan

US-15 is complete: `FhirSdkMode.Ignixa`/`Hybrid` assemble search/history bundles natively, entry bodies stay zero-copy, and the XML/HTML content-negotiation gap this activates is closed globally (for any future node-backed or carrier-backed result, not just bundles). Firely mode is untouched throughout. This plan's Task 1 output (`IgnixaRawBundle`/`IgnixaBundleSerializer`) and Task 3's formatter branches are load-bearing prerequisites for Phase 4 Plan B (US-16, batch/transaction processing) — do not start Plan B's response-recomposition tasks before this plan's Tasks 1 and 3 are merged. Plan B is a separate document; its reference-resolution piece (transaction-only) required a targeted redesign after this plan's design review found the original approach unsound (a schema-free JSON-property-name walk collides with real non-Reference FHIR elements named `reference`, e.g. `Expression.reference`) — that redesign is tracked separately and gates only Plan B's later tasks, not this plan.
