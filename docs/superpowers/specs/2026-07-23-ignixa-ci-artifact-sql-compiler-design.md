# Ignixa Main CI Artifact SQL Compiler Integration

**Status:** Proposed  
**Date:** 2026-07-23  
**Related design:** `2026-07-10-ignixa-search-sql-data-layer-design.md`

## Context

FHIR Server currently consumes `Ignixa.Search` 0.6.28 and
`Ignixa.Search.Sql` 0.6.28-alpha. The SQL integration work was blocked because
that compiler package predated the Ignixa SQL catalog and symbol-resolution
changes now present on Ignixa `main`.

The required developmental artifacts are available from the successful Ignixa
CI run [30040808293](https://github.com/brendankowitz/ignixa-fhir/actions/runs/30040808293),
which built Ignixa `main` at commit
`0566dcb3e436a05afcdbcd581df702c79280693f`:

- `Ignixa.Search.0.6.32.nupkg`
- `Ignixa.Search.Sql.0.6.32-alpha.nupkg`

The artifact is intentionally a local developer dependency for this phase.
FHIR Server CI publication and cross-repository artifact acquisition are
out of scope until the integration is proven.

## Decision

FHIR Server will consume the coordinated Ignixa CI artifacts as a local NuGet
source and will add a compile-only SQL compiler adapter. The existing legacy
SQL path remains authoritative for request results.

The dependency versions will be pinned to:

```xml
<IgnixaSearchPackageVersion>0.6.32</IgnixaSearchPackageVersion>
<IgnixaSearchSqlPackageVersion>0.6.32-alpha</IgnixaSearchSqlPackageVersion>
```

The Ignixa build commit will be recorded with the dependency change. The
`.nupkg` files will not be committed to FHIR Server. Developers will extract
the `nuget-packages-core` artifact outside the repository and pass that
directory as an explicit restore source.

The SQL integration boundary is:

```text
FHIR SearchOptions
  -> capability router
      -> unsupported or legacy-only: legacy SQL path
      -> eligible: Resolve.RunAsync
                    -> Lower.Run
                    -> SqlBuilder.Run
  -> compile-only artifact and telemetry
  -> legacy SQL response
```

FHIR Server owns request routing, FHIR-specific execution controls, symbol
lookup, feature flags, telemetry, and result-shape validation. Ignixa owns
expression semantics, query-plan construction, SQL parameterization, and SQL
emission.

## Dependency and restore boundary

Both Ignixa packages must come from the same CI build. Mixing the current
published baseline with a `main` compiler package is unsupported because the
SQL compiler API and expression model evolve together.

The local restore override must:

- provide the extracted CI artifact directory as a package source;
- retain the normal package sources for all other dependencies;
- avoid changing the committed package source configuration;
- be documented in the implementation plan and developer verification
  commands.

The phase does not add a FHIR Server workflow to download artifacts. A later
CI integration may resolve a configured Ignixa run by commit and verify the
artifact checksum before restore.

## Resolver integration

The published baseline resolver currently implements only search-parameter and
resource-type lookup. The `main` API additionally requires system and
quantity-code lookup.

`IgnixaSqlSymbolResolver` will implement the expanded contract by delegating
to the initialized `ISqlServerFhirModel`:

- `GetSearchParamIdAsync` -> `TryGetSearchParamId`;
- `GetResourceTypeIdAsync` -> `TryGetResourceTypeId`;
- `GetSystemIdAsync` -> `TryGetSystemId`;
- `GetQuantityCodeIdAsync` -> `TryGetQuantityCodeId`.

The model already holds the reference catalogs in memory, so these methods do
not introduce per-value database I/O. The compiler's default system-batch
method may remain correct for this phase. Cancellation must be checked before
each lookup, and lookup misses must preserve the Ignixa null-result contract.

Tests will cover successful lookup, missing lookup, cancellation, exceptions,
null/invalid inputs, and zero-valued IDs.

## Catalog compatibility

The Ignixa `main` generator reads the Ignixa SQL data-layer DDL and includes
`TokenText` in the generated compiler catalog. `System` and `QuantityCode`
are lookup tables used by symbol resolution; the compiler emits predicates
against `SystemId` and `QuantityCodeId` columns in search-index tables rather
than SQL joins to those lookup tables.

The FHIR Server compatibility tests will therefore be split:

1. **Compiler catalog contract:** assert exact table, column, type, length,
   collation, and nullability facts for tables emitted by `SqlBuilder`,
   including `TokenText`.
2. **Resolver lookup contract:** assert that the FHIR Server `System` and
   `QuantityCode` DDL and model expose the identifiers and values required by
   the expanded resolver.

The test must not require lookup tables to appear in `SqlCatalog.Default`.
This is a correction to the previous manifest, not a weakening of schema
validation.

## Compile-only adapter

The adapter will consume the canonical Ignixa expression carried by the
existing FHIR Server `SearchOptions` envelope and invoke the public stages in
order:

1. `Resolve.RunAsync`;
2. `Lower.Run`;
3. `SqlBuilder.Run`.

The adapter result will contain:

- emitted SQL;
- typed SQL parameters;
- lowered plan and result-shape metadata;
- unresolved symbol information;
- Ignixa package identity and FHIR Server schema version;
- a deterministic, redacted plan fingerprint for telemetry.

User values and parameter contents must not be written to ordinary telemetry.
Plan shape and failure metadata may be recorded without exposing search
values.

The compile-only router is disabled by default. When enabled, it will attempt
compilation only when the FHIR Server request semantics can be represented by
the Ignixa plan. Initial exclusions include history/deleted/version filters,
access-control predicates, feed-range restrictions, and legacy-only
continuation state. Includes, reverse-includes, sort, count, and paging are
eligible only when the compiler result shape explicitly represents them.

The router does not execute emitted SQL or alter the response. The legacy SQL
engine remains the response authority and provides the result returned to the
caller.

## Failure and fallback behavior

Known compiler capability failures are recorded with stage and parameter
context, then classified as legacy routing. They must not become empty results
or broad searches.

Cancellation and database/model lookup failures propagate through the
existing request pipeline. Unexpected compiler failures are not silently
converted to successful fallback. The adapter will use a narrow capability
failure boundary; if the CI package exposes only an unstructured
`NotSupportedException`, that handling will be isolated in the adapter and
will not depend on parsing exception text across the data layer.

Missing system or quantity-code values follow Ignixa's semantic contract:
known misses become false predicates when the compiler supports that shape;
resolver I/O failures remain errors.

## Validation

This phase is complete only when the following pass against the extracted
CI packages:

- net10 restore and build;
- compile-time compatibility with the 0.6.32 APIs;
- resolver unit tests;
- compiler catalog compatibility tests;
- lookup-table contract tests;
- compile-only adapter plan and SQL tests;
- capability-routing and result-shape tests;
- existing parser, Cosmos bridge, and SQL Server unit suites.

The phase does not claim SQL execution parity. Live execution, resource
hydration, continuation-token parity, write alignment, differential
shadowing, and canary rollout remain subsequent gates.

## Alternatives rejected

### Keep the published 0.6.28-alpha package

Rejected because it lacks the resolver methods and catalog/compiler behavior
needed by the current Ignixa `main` implementation.

### Commit the `.nupkg` files to FHIR Server

Rejected because binaries in source control obscure provenance and make
artifact refreshes difficult to audit.

### Add a local fake or patched `SqlCatalog`

Rejected because it would create a second schema authority and conceal
compiler/data-layer drift.

### Execute compiled SQL immediately

Rejected because result hydration, FHIR-specific filters, continuation, and
access-control parity are not yet proven.

## Consequences

This design lets FHIR Server exercise the current Ignixa compiler without
waiting for a public package release and without changing production query
results. It also exposes the exact upstream API and schema contracts that the
next execution phase must satisfy.

The cost is a local restore prerequisite and a temporary compile-only path.
The dependency remains developmental, so the Ignixa commit and package
versions must stay pinned until CI artifact acquisition and compatibility
validation are automated.
