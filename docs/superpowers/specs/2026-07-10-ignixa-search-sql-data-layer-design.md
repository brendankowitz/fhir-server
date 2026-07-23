# Ignixa Search Libraries for the FHIR Server SQL Data Layer

**Status:** Proposed  
**Date:** 2026-07-10

## Context

FHIR Server currently parses search parameters into expression types owned by
`Microsoft.Health.Fhir.Core`. The SQL Server backend then applies an ordered
chain of SQL rewriters and renders the result with the legacy SQL query
generators. Cosmos DB also consumes the FHIR Server legacy expression tree.
This duplicates search semantics across parsing, lowering, and storage
backends.

The Ignixa repository now provides two libraries intended to be reused by
consumers:

- `Ignixa.Search` parses query parameters into a typed, backend-neutral search
  expression tree and exposes version-aware `SearchOptions` construction.
- `Ignixa.Search.Sql` resolves symbols, lowers the typed expression into a
  CTE-based query plan, and emits deterministic parameterized T-SQL.

The SQL library is currently alpha and is not wired into a production data
layer. Its current result shape is an ordered set of resource keys, with
additional match metadata for includes and a count-only shape. It deliberately
does not own database connections, retry policy, resource hydration, or FHIR
Bundle construction.

The FHIR Server must support STU3, R4, R4B, and R5, tenant-specific search
parameters, Cosmos DB, SQL Server, history and deleted-resource modes, includes,
accurate totals, and continuation tokens. The migration therefore must replace
the parser and SQL data-layer path without changing the public FHIR behavior.

## Decision

Use `Ignixa.Search` and `Ignixa.Search.Sql` as external library dependencies and
make their typed search options and expression tree the canonical search
representation in FHIR Server.

The migration will:

1. Route FHIR Server search-option construction through Ignixa's parser and
   version-aware options builder.
2. Keep FHIR Server's existing `SearchOptions` as a temporary execution
   envelope for server-specific controls not yet represented by Ignixa.
3. Lower the Ignixa expression to Ignixa's legacy expression shape for Cosmos
   during the transition, then bridge that shape to the current FHIR Server
   Cosmos query-builder types.
4. Use the Ignixa SQL compiler stages for SQL Server rather than adding new
   SQL rewriters or a second renderer.
5. Align the SQL schema, lookup tables, index writes, and read path with the
   compiler's catalog contract.
6. Keep the current SQL execution shell, including `SqlServerSearchService` and
   `ISqlRetryService`, until the compiled path has passed differential
   validation.
7. Retire the legacy SQL rewriters and query generators only after compiled
   reads, writes, continuation, includes, and all supported query modes meet
   parity requirements.

The first FHIR Server branch starts from current `main`. The previous
`brendankowitz-simplify-sql-data-layer` branch is reference material only; its
semantic types are not copied into FHIR Server.

## Goals

- Have one parser and one typed semantic representation for all storage
  backends.
- Use the Ignixa SQL compiler for SQL search predicate and query-plan
  generation.
- Preserve the FHIR Server request, result, error, include, history, and
  continuation contracts.
- Make the SQL schema and index-writing path agree with the compiler catalog.
- Provide deterministic differential and live SQL validation before cutover.
- Allow incremental rollout and explicit fallback while the Ignixa compiler
  remains alpha.

## Non-goals

- Replacing the Cosmos DB storage service.
- Changing the FHIR REST surface or Bundle response contract.
- Reimplementing Ignixa parser, expression, lowering, or SQL-emission logic in
  FHIR Server.
- Making FHIR-specific resource hydration part of the generic Ignixa SQL
  emitter.
- Removing the existing SQL retry, routing, decompression, or resource
  materialization infrastructure in the first migration milestone.

## Architecture

### Canonical parsing

The implementation behind FHIR Server's existing `ISearchOptionsFactory` will
adapt its query tuples to Ignixa `QueryParameter` records and invoke:

```text
ISearchOptionsBuilderFactory.Create(fhirVersion, tenantId)
    -> ISearchOptionsBuilder.Build(resourceType, parameters)
```

The Ignixa `SearchOptions` object becomes authoritative for:

- the typed search expression;
- sort, include, and reverse-include expressions;
- `_count`, `_total`, and `_summary`;
- `_elements`, `_type`, and continuation values;
- unsupported-parameter and Bundle-issue outcomes.

FHIR Server's `SearchOptions` remains as a compatibility envelope while the
migration is in progress. It carries the Ignixa options plus controls that are
specific to the current server execution model, including:

- resource-version and history/deleted selection;
- `onlyIds` and async-operation state;
- feed ranges and query hints;
- access-control predicates;
- include-operation state;
- legacy continuation metadata where required.

The old FHIR Server parser is not used for production requests after the
canonical parser is enabled. It remains available only as a differential-test
oracle until the migration is complete.

### Version and tenant integration

The FHIR Server version-specific registration maps its STU3, R4, R4B, and R5
request contexts to the corresponding Ignixa `FhirVersion`. The options-builder
factory is created with the request tenant ID so tenant and implementation-guide
search-parameter definitions participate in parsing and binding.

The existing search-parameter definition and status managers are adapted to
Ignixa definition interfaces. A parameter must be resolved using the same
canonical URL, override URL, code, type, and resource scope used by indexing.
The adapter must not silently substitute a definition with different indexing
semantics.

### Cosmos compatibility bridge

`Ignixa.Search.Expressions.LegacyExpressionLowerer` is the only semantic
lowering boundary for legacy consumers. Its output remains Ignixa expression
types and therefore cannot be passed directly to the current Cosmos query
builder, which consumes FHIR Server expression types.

FHIR Server will add one explicit bridge with the following properties:

- It accepts the lowered Ignixa expression tree.
- It maps structural nodes without changing Boolean shape, chain direction,
  include semantics, or missing/not semantics.
- It maps predicate leaves while preserving search-parameter code, modifier,
  comparator, composite component position, and typed value.
- It contains no Cosmos SQL or SQL Server behavior.
- It rejects an unmappable node with a structured capability error before
  executing a query.

The bridge is temporary. Once Cosmos has a native Ignixa expression consumer,
the bridge and the old FHIR Server expression types can be removed.

### SQL compiler adapter

FHIR Server will add a SQL compiler adapter that orchestrates the public Ignixa
stages in their defined order:

```text
Ignixa SearchOptions
    -> Resolve.RunAsync
    -> Lower.Run
    -> SqlBuilder.Run
    -> EmittedSql
```

`Ignixa.Search.Sql` remains responsible for expression semantics, CTE
construction, predicate scope, parameterization, sorting, and keyset paging.
FHIR Server does not add SQL rewriters or modify emitted SQL text.

The alpha tracing-oriented `SearchCompiler` API is not treated as the
production execution contract. The Ignixa dependency must expose, or FHIR
Server must consume through a thin adapter over public stages, a production
compile result containing:

- emitted SQL and typed parameters;
- resolved symbols and unresolved definitions;
- result-shape metadata;
- the compiler/schema identity;
- structured failures with the responsible stage and parameter when known.

If this contract requires changes in Ignixa, those changes are made and
versioned in the Ignixa repository before the FHIR Server dependency is
updated. FHIR Server does not fork the emitter.

### Symbol resolution

FHIR Server implements `Ignixa.Search.Sql.Symbols.ISymbolResolver` in the SQL
data layer. It resolves:

- search-parameter IDs by canonical definition and override URL;
- resource-type IDs;
- token-system IDs;
- quantity-code IDs.

The resolver performs all I/O during the Resolve stage. It uses request-scoped
caching and set-based lookup for system IDs and repeated definitions. A missing
catalog row is returned according to the Ignixa resolver contract; the
compiler adapter decides whether it represents an empty match, an unsupported
definition, or a fallback case. No later lowering stage performs database I/O.

### Schema and catalog

The SQL schema is the source of truth for the compiler catalog. Before
compiled reads are enabled, the implementation verifies the exact FHIR Server
`main` schema version and compares every compiler-referenced table and column,
including:

- resource type and resource identity columns;
- string, token, reference, date, number, quantity, and composite indexes;
- overflow columns and their length/collation behavior;
- system and quantity-code lookup tables;
- resource version, deletion, and search-parameter status columns.

The catalog is generated from the verified DDL. A copied or stale schema
snapshot is not accepted. If the current schema is incompatible, the branch
adds a migration and updates index writes and lookup maintenance before
compiled reads are enabled.

### SQL execution and result hydration

The initial compiled executor remains inside the existing SQL Server search
execution shell. It uses the existing connection factory, retry service,
read-replica routing, timeout handling, logging, query cache/recompile policy,
resource decompression, and resource materializer.

The Ignixa compiler returns ordered resource keys:

```text
(ResourceTypeId, ResourceSurrogateId)
```

When includes are present, it additionally returns `IsMatch` and `IsPartial`.
When count-only is requested, it returns the count shape. The FHIR Server
adapter:

1. validates the expected result shape from the lowered plan;
2. removes duplicate keys without changing the first-seen order;
3. maps resource type and surrogate IDs to the existing resource projection;
4. hydrates resources while preserving compiler order;
5. maps include rows and partial stages to the existing result-entry contract;
6. returns totals and count-only results through the existing Bundle pipeline.

Resource hydration is intentionally outside the generic compiler. This keeps
the library usable by other data layers and prevents FHIR response concerns
from entering SQL lowering.

History, deleted-resource, version, access-control, feed-range, and other
physical filters are represented as explicit compiler/data-layer options. They
are never applied after an already paged result set. A query shape is not
enabled on the compiled path until these filters are supported by the plan or
the query is routed to the legacy engine.

### Continuation and includes

Compiled continuation tokens contain:

- an engine identifier;
- the Ignixa compiler and schema identity;
- target resource type or resource-type set;
- sort definitions and the last emitted sort tuple;
- the unique resource key tie-breaker;
- include phase state when applicable.

Legacy tokens remain routable to the legacy SQL path. Compiled tokens remain
routable to the compiled path. A token from one engine must never silently
resume in the other.

The compiled path uses the Ignixa keyset paging contract and preserves FHIR
Server's second-phase missing-sort behavior where required. Include and
reverse-include continuation remain separate from primary-result continuation
until the compiler and response adapter prove equivalent behavior.

## Error handling and fallback

- Invalid query syntax and invalid control values map to the existing FHIR
  BadRequest and OperationOutcome behavior.
- Ignixa unsupported parameters and Bundle issues are preserved rather than
  discarded.
- Unresolved definitions are classified before SQL execution. They never
  become an accidental broad search or a late `KeyNotFoundException`.
- Known compiler capability failures are recorded with stage and parameter
  context, then routed to the legacy path while the feature flag permits
  fallback.
- Schema/catalog mismatches fail health validation and prevent compiled
  execution; they are not converted into empty results.
- Cancellation and database failures propagate through the existing retry and
  request pipeline. No broad catch or success-shaped fallback is added.
- Once a query shape is declared fully supported and fallback is disabled,
  compiler failures are surfaced as operational errors with a trace ID rather
  than silently changing semantics.

## Testing and validation

### Unit and golden tests

- Parse the supported FHIR search grammar for every FHIR version.
- Verify parser outcomes, modifiers, comparators, composites, chains,
  includes, `_type`, `_summary`, `_total`, and continuation parameters.
- Verify the Cosmos bridge preserves expression structure and typed values.
- Verify symbol resolution, URL overrides, missing catalog entries, batching,
  and request-scoped caching.
- Pin Ignixa query plans and emitted SQL with existing Ignixa golden tests.
- Verify emitted parameters are typed and user values are never interpolated.
- Verify result-shape metadata, stable tie-breaking, count-only, and include
  shapes.

### Integration and end-to-end tests

- Execute representative compiled queries against the supported SQL schema.
- Validate resource hydration, version/deleted filters, access control,
  totals, `_summary=count`, sort, missing-sort, and continuation.
- Validate `_include`, `_revinclude`, `:iterate`, partial include stages, and
  include continuation.
- Validate Cosmos results through the Ignixa parser plus compatibility bridge.
- Exercise STU3, R4, R4B, R5, custom tenant parameters, and overridden
  definitions.

### Differential validation

For the same query and database snapshot, compare legacy and compiled paths
for:

- resource identity and first-seen order;
- duplicate elimination;
- match/include classification and partial state;
- totals and count-only values;
- continuation-token page sequences;
- FHIR errors and OperationOutcome issues;
- history, deletion, and access-control visibility.

The sampled execution harness also records latency, CPU, logical reads,
memory grants, and spills. It reports mismatches as failures, not warnings.

## Rollout

1. Compile-only shadowing validates parser binding, symbol resolution, plan
   shape, and emitted SQL without executing the new query.
2. Sampled execution shadowing runs both engines and compares results while
   returning the legacy response.
3. Shape-scoped canaries enable compiled execution for query capabilities and
   resource types with complete parity coverage.
4. Tenant and environment rollout expands only after operational metrics remain
   within the agreed baseline and fallback rates are understood.
5. Full cutover disables legacy SQL generation for supported shapes while
   preserving explicit routing for legacy continuation tokens and unsupported
   modes.
6. After token expiry and migration verification, remove the old rewriters,
   query generators, and compatibility bridge.

Feature flags and telemetry identify the parser, compiler, schema, and
execution engine for every request. A rollback changes routing; it does not
invalidate or reinterpret tokens.

## Implementation sequence and gates

### Gate 0: branch and dependency

Create the FHIR Server branch from current `main`, pin the Ignixa library
artifact or commit, and verify a clean net10 restore/build. A temporary
project reference is allowed only while developing an upstream Ignixa change
and must be replaced by a pinned package before production.

### Gate 1: parser and options

Route `ISearchOptionsFactory` through Ignixa, preserve the FHIR Server
compatibility envelope, and pass all parser and control-parameter tests for
STU3, R4, R4B, and R5.

### Gate 2: Cosmos bridge

Enable the bridge for Cosmos and pass differential resource, error, sort, and
continuation tests. The old parser is test-only after this gate.

### Gate 3: compiler and schema

Land required Ignixa production API changes, implement the symbol resolver,
verify the catalog against the exact SQL DDL, and pass live SQL schema and
lookup tests.

### Gate 4: compiled reads

Execute compiled SQL through the existing SQL Server shell and pass result
hydration, totals, includes, history, access-control, sorting, and
continuation tests.

### Gate 5: writes and backfill

Align index writes and lookup maintenance with the compiler schema, run
backfill/reindex, and prove that compiled reads see all resources expected by
the legacy path.

### Gate 6: differential canary

Run shadow and canary traffic, compare outputs and performance, and resolve
every deterministic semantic mismatch before expanding coverage.

### Gate 7: retirement

Retire the legacy SQL rewriters only after all supported query modes have
zero deterministic result mismatches, no unexplained fallback, complete
continuation coverage, and an operational rollback path.

## Consequences

### Positive

- Search semantics live in one maintained library rather than separate FHIR
  Server parser and SQL rewriter implementations.
- SQL generation becomes deterministic, parameterized, inspectable, and
  independently testable.
- Cosmos and SQL can migrate independently behind a shared canonical parser.
- The data layer owns only symbol resolution, schema, writes, execution, and
  hydration.
- Differential rollout provides evidence before removing mature code.

### Costs and risks

- `Ignixa.Search.Sql` is alpha, so FHIR Server must pin versions and maintain
  cross-repository compatibility CI.
- The temporary Cosmos expression bridge adds translation code and must be
  deleted after Cosmos adopts Ignixa expressions.
- Existing FHIR Server result and history behavior is richer than the
  compiler's key-row result shape and requires explicit adapter work.
- The SQL schema and write path must be migrated together; a read-only compiler
  integration is not sufficient.
- Continuation tokens must carry engine identity, which adds serialization and
  operational migration work.
- The broad migration spans two repositories and requires synchronized
  Ignixa API, schema, and FHIR Server changes.

## Acceptance criteria

The design is complete when:

- FHIR Server production parsing uses `Ignixa.Search` for all enabled FHIR
  versions and tenant definitions.
- Cosmos receives a correctly bridged lowering from the Ignixa expression.
- SQL Server executes `Ignixa.Search.Sql` output through existing retry and
  materialization infrastructure.
- The compiler catalog, SQL schema, lookup tables, and index writes are
  verified as one contract.
- Result ordering, includes, totals, history/deleted visibility, access
  control, and continuation sequences match the legacy path.
- Differential and live SQL tests cover every enabled query shape.
- Compiled and legacy continuation tokens cannot be cross-routed.
- Legacy SQL rewriters and generators are removed only after the retirement
  gate passes.
