# Rule: Mutating a Node-Backed ResourceElement

`ResourceElement.ResourceInstance` can hold either of two different shapes when a resource is
Ignixa-node-backed, and the difference matters after mutation:

- **Bare `ResourceJsonNode`** (set by `FhirModule`'s DB-read deserializer) -- does NOT implement
  `IResourceElement`, so `ResourceElement.LastUpdated`/`.ToPoco()`/`.Instance` fall back to a cached
  `ITypedElement` snapshot captured at construction. That snapshot goes stale after the underlying
  node is mutated in place.
- **`IgnixaResourceElement`** (set by the 4 handler call sites via `RebuildResourceElement`) --
  implements `IResourceElement`, so scalar reads go straight to the live node and stay fresh after
  mutation. Note: resources arriving from HTTP request bodies (via the input formatter) are NOT
  in this shape; they arrive as bare `ResourceJsonNode` (the stale bucket below), and therefore
  also require `RebuildResourceElement` after any in-place mutation—see `ConditionalUpsertResourceHandler`
  for the pattern.

A third shape exists but sits outside this rule entirely: **`IgnixaRawBundle`** (produced by
`IgnixaBundleFactory`, set directly as `ResourceElement.ResourceInstance` -- not accessed via
`GetIgnixaNode()`) is immutable after construction, so it's exempt from the reuse-vs-rebuild choice
by construction: the factory fully mutates and assembles the skeleton and every entry before
wrapping them in `IgnixaRawBundle`, and nothing mutates them afterward. It is read exclusively via
`GetIgnixaRawBundle()` (`ResourceElementIgnixaExtensions.cs`) -- never `.ToPoco()`/`.Instance`, which
silently return a hollow bundle: a real id/meta/type/total/link, but an empty entry array, since only
the skeleton participates in the typed-element view. That hollow-`ToPoco()` hazard is locked in by a
regression test, `IgnixaBundleFactoryTests.GivenAnIgnixaSearchBundleWithEntries_WhenToPocoIsCalled_ThenPocoEntriesAreHollowButRawBundleEntriesAreReal`.

`GetIgnixaNode()` deliberately accepts both shapes (it only needs the raw node), which is why this
hazard is invisible at the type level -- nothing stops you from mutating a node and returning the
original `ResourceElement`, and the compiler will not warn you if that turns out to be wrong for your
specific consumer.

## The rule

After mutating a `ResourceJsonNode` held by a `ResourceElement`:

- **Default: rebuild.** Call `resourceJsonNode.RebuildResourceElement(schemaContext)`
  (`IgnixaResourceElementExtensions.cs`) and use the returned value, not the original `ResourceElement`.
- **Reuse-in-place is permitted only when you have proven every downstream consumer of the value reads
  exclusively via `GetIgnixaNode()`** (never `.ToPoco()`, `.Instance`, `.Scalar()`, `.Predicate()`, or
  anything that could read the cached view). That proof must be written as a comment at the call site,
  not just asserted in a PR description -- see `TryAddSoftDeletedExtension`
  (`Extensions/ModelExtensions.cs`) for the pattern: its only consumer,
  `ResourceToNdjsonBytesSerializer.SerializeToJson`, is traced and the reasoning is recorded inline.

## History

This rule exists because Force-Ignixa Phase 3's Task 2 (native id-stamping in
`ConditionalUpsertResourceHandler`) shipped a real bug on first implementation: it reused a stale
wrapper after in-place mutation, and a downstream `UpsertResourceHandler.Handle` call read the
(stale, empty) id via `.ToPoco()`, which in the common no-id-in-request-body case would have silently
created a new resource with a random id instead of updating the matched one. Caught by task review,
fixed, independently re-verified. Two later tasks in the same plan (export's
`TryAddSoftDeletedExtension`, bulk-update's `StampLastUpdated`) each had to independently re-derive
this same consumer-trace reasoning -- this doc and the `RebuildResourceElement` helper exist so a
future task doesn't have to a fourth time.

See also [provider-map.md](provider-map.md) and
[docs/superpowers/plans/2026-07-09-force-ignixa-phase3.md](../../superpowers/plans/2026-07-09-force-ignixa-phase3.md)
(Task 2's brief/report/review history) for the full incident.
