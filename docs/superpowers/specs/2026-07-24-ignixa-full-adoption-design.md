# Full adoption of Ignixa.Search and Ignixa.Search.Sql

Date: 2026-07-24
Status: Design approved, not yet implemented
Branch: `ignixa-full-adoption` (fhir-server), plus a companion branch in `brendankowitz/ignixa-fhir`

## Purpose

Replace the FHIR Server's hand-written search expression model, expression rewriters, and SQL
generation with the Ignixa libraries. After this change, `Ignixa.Search` owns the expression model
and parser, `Ignixa.Search.Sql` owns SQL generation, and the FHIR Server owns only I/O: symbol
lookup, statement execution, and result materialization.

This is a full adoption, not an integration. There are no adapters, no bridges, and no routing
between an old path and a new path. The legacy expression model is deleted outright, including on
the Cosmos path, which is converted to consume Ignixa expressions.

## Success criteria

1. `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/` no longer exists.
2. `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Expressions/` no longer exists.
3. Every existing SQL and Cosmos search unit test passes. Tests that assert on legacy expression
   *types* may be rewritten against Ignixa nodes; tests that assert on *behavior* must pass
   unchanged.
4. Every existing SQL integration test passes against a live database.
5. The legacy-SQL corpus in ignixa-fhir shows strictly fewer queries omitting a filter, and strictly
   more matching the shipping engine, than today's baseline (46 and 69 respectively). **Not** "100%
   compiles" — all 185 already compile, so that measure cannot detect a regression.
6. The branch is a genuine merge candidate, not a spike.

## Current state

### Legacy surface being retired

| Area | Files | Approx. lines |
|---|---|---|
| `SqlServer/Features/Search/Expressions/**` | 58 | 5,200 |
| `SqlServerSearchService.cs` | 1 | 2,901 |
| `Core/Features/Search/Expressions/**` | 41 | 3,507 |
| Cosmos `Features/Search/**` (converted, not deleted) | 10 | 1,857 |

The largest single files are `SqlQueryGenerator.cs` (1,661 lines) and `SqlServerSearchService.cs`
(2,901 lines). Both shrink dramatically: the former disappears, the latter drops to roughly 600
lines of execute-and-materialize logic.

### Ignixa surface being adopted

`Ignixa.Search.Sql` is a three-stage pipeline with a single I/O seam:

```
Ignixa.Search.Expressions.Expression
        │
        ▼  Symbols/Resolve.cs        ── ISymbolResolver (the only I/O)
   resolved symbols
        │
        ▼  Lowering/Lower.cs
     QueryPlan  (Ctes, Match, Top, OuterPredicate, Includes, Sort, Page, CountOnly)
        │
        ▼  Builders/SqlBuilder.cs
    EmittedSql (Sql, Parameters, TextRanges?)
```

`ISymbolResolver` is the only interface the FHIR Server must implement. Its contract is that a
missing symbol is data, not an error: an unresolvable code lowers to `Predicate.False(reason)`
rather than throwing.

## Architecture after the change

```
HTTP request
    │
    ▼
SearchOptionsFactory  ──▶  Ignixa.Search parser  ──▶  Ignixa Expression tree
    │                                                        │
    │  MS SearchOptions : Ignixa SearchOptions               │
    │  (+ AccessConstraints, ResourceVersionTypes)           │
    │                                                        │
    ├────────────── SQL path ───────────────┐                │
    │                                       ▼                │
    │                       Ignixa.Search.Sql compiler  ◀────┘
    │                                       │
    │                        IgnixaSqlSymbolResolver (FHIR Server)
    │                                       │
    │                                  EmittedSql
    │                                       │
    │                          SqlServerSearchService: execute + materialize
    │
    └────────────── Cosmos path ────────────┐
                                            ▼
                            ExpressionQueryBuilder (retargeted at Ignixa tree)
                                            │
                                       Cosmos SQL
```

### Options model

Ignixa's `SearchOptions` becomes the base type; the FHIR Server's `SearchOptions` derives from it
and adds server-only fields. Ignixa's type is already a plain inheritable `public class` and
already models `MaxItemCount`, `ContinuationToken`, `Expression`, `Sort`, `Include`, `RevInclude`,
`Elements`, `Total`, `Summary`, `UnsupportedParams`, `BundleIssues`, `ResourceType`,
`ResourceTypes`, `StartSurrogateId`, `EndSurrogateId`, `IncludesMaxItemCount`, and
`IncludesContinuationToken`.

Ignixa's base type gains two properties in this work:

- `ResourceVersionTypes` — which of latest/history/soft-deleted are visible.
- `AccessConstraints` — the provider-neutral authorization model (see below).

### SQL projection

Ignixa emits the complete statement, including the resource projection. `SqlBuilder` already joins
`dbo.Resource` directly, so the projection stage extends an existing join rather than introducing
a new one. The FHIR Server executes the statement and reads the `DataReader`; it does not append,
wrap, or rewrite the SQL.

### Authorization (SMART)

SMART scopes stop being expression rewrites and become a declarative constraint model.

- The FHIR Server translates claims into `AccessConstraints` and attaches them to `SearchOptions`.
- Ignixa's compiler enforces constraints **structurally**: on the match set, on every include
  stage, on every `:iterate` stage, and on every chain target. A constraint cannot be bypassed by
  navigating a reference, which is the failure mode the current rewriter approach is prone to.
- Cosmos reads the same `AccessConstraints` and enforces identical semantics.

`SmartCompartmentSearchRewriter`, `SqlCompartmentSearchRewriter`, and `NotReferencingExpression`
are all deleted. `NotReferencingExpression` exists solely to express "this resource has no value
for reference parameter X", which the constraint model expresses directly as part of a compartment
rule ("visible if the reference is absent, or points into the compartment"). No new public
expression node is required.

## Ignixa-side workstreams

These land in a branch of `brendankowitz/ignixa-fhir` **before** any fhir-server cutover commit.

1. **Resource projection** — `QueryPlan` and `SqlBuilder` gain a projection stage so the emitted
   statement returns the resource columns the server needs, not just `(T1, Sid1)`.
2. **Resource visibility / version types** — `IsHistory = 0 AND IsDeleted = 0` is currently
   hardcoded at roughly six emitter sites in `SqlBuilder.cs`. It becomes a parameter derived from
   `ResourceVersionTypes`.
3. **Surrogate-ID range lowering** — `StartSurrogateId` / `EndSurrogateId` lower into the plan,
   supporting `$export` and reindex-style bounded scans.
4. **Access constraints** — the constraint model plus structural enforcement described above.
5. **Multi-type and system-wide search** — `Lower` currently throws when there is no single target
   resource type. It must support a resource-type set and the system-wide case.
6. **Search-parameter-hash filter** — needed by reindex; filters on the resource's parameter hash.
7. **Includes-only query mode** — the second-page case where only include results are requested.
8. **`$everything` plan shape** — `PatientEverythingExpression` becomes a first-class plan shape
   alongside includes, rather than being expanded by the caller.

Additionally, a correctness gap found while planning: an untyped reference search
(`/Patient?organization=X`) emits no `ReferenceResourceTypeId` filter, so it matches a reference to
any resource type carrying that id. It must narrow to the search parameter's declared target types.

`:not` was originally listed here as a gap. It is not — `LowerSearchParameter` handles it, and the
`NotSupportedException` at `Lower.cs:103` guards a shape the binder never produces.

### Verification gate for the Ignixa branch

`test/Ignixa.Search.Sql.Tests/Corpus/LegacyCorpusDifferentialTests.cs` compiles the 430 KB
`legacy-sql-corpus.json` extracted from real FHIR Server queries. **All 185 entries already
compile**, so compile success proves nothing — `/Patient/{id}/$everything` compiles today and
silently returns only the Patient. The gate is therefore the divergence classes: 46 queries where
the compiler omits a filter the shipping engine applies, and 69 that match exactly. Both must move
in the right direction, guarded by a new `DivergenceBaseline`.

Each capability additionally carries its own behavioural test; the corpus is a regression net, not
a capability oracle.

## FHIR Server SQL cutover

`SqlServerSearchService` is reduced to:

1. Build `SearchOptions` (now Ignixa-derived).
2. Call the Ignixa compiler with `IgnixaSqlSymbolResolver`.
3. Execute `EmittedSql` with its parameters.
4. Materialize the `DataReader` into `SearchResult`.
5. Drive sort-phase transitions and keyset pagination. Both remain caller responsibilities by
   Ignixa's design: `SortSpec.Phase` is an explicit input, and `PageSpec` boundary values must
   already have sentinel substitution applied.

Deleted in this step:

- `SqlServer/Features/Search/Expressions/**` (all 58 files, including `SqlQueryGenerator`, all
  query generators, and all visitors/rewriters).
- `IgnixaSqlCompilerAdapter` and `IgnixaSqlCompileOnlyRouter` — the compile-only scaffolding from
  the prior integration branch. There is nothing left to route.

Retained: `IgnixaSqlSymbolResolver`, which is the FHIR Server's implementation of the one seam
Ignixa exposes. Its deliberate omission of `OverridesUrl` fallback is retained and remains covered
by `GetSearchParamIdAsync_DoesNotUseOverridesUrl`.

## Cosmos conversion

Ignixa's expression model is a near-superset of the legacy model, with an identical visitor shape
(`IExpressionVisitor<TContext, TOutput>`) and the same node names, so this is largely mechanical.

| Legacy node | Ignixa |
|---|---|
| `SearchParameterExpression`, `Binary`, `String`, `Multiary`, `Chained`, `Include`, `In<T>`, `Not`, `Union`, `Sort`, `MissingField`, `MissingSearchParameter`, `CompartmentSearch`, `NotReferenced` | same names, same semantics |
| `SmartCompartmentSearchExpression` | deleted — replaced by `AccessConstraints` |
| `NotReferencingExpression` | deleted — folded into the constraint model |
| — | Ignixa adds `SearchParameterPredicateExpression`, `CompositeComponentExpression`, `PatientEverythingExpression` |

Work:

1. `ExpressionQueryBuilder` and `QueryBuilder` retarget their visitor to
   `Ignixa.Search.Expressions`, and handle the additional node kinds.
2. `CosmosCompartmentSearchRewriter` and its `CompartmentSearchRewriter` base move from Core into
   the CosmosDb project, retargeted at the Ignixa tree. Compartment rewriting becomes Cosmos's
   private concern.
3. `IgnixaCosmosExpressionRouter`, `IIgnixaLegacyExpressionBridge`, and
   `IgnixaRoutingExpressionComparer` are deleted.
4. `Core/Features/Search/Expressions/**` is deleted in full.

## Error handling

Today an unsupported search can silently degrade through rewriter fallbacks. After the cutover
there is no fallback path, so failure handling becomes explicit:

- `NotSupportedException` from `Lower` maps to `SearchOperationNotSupportedException`, surfacing as
  a FHIR `OperationOutcome` rather than a 500. The existing `NotSupportedException` messages in
  `Lower` are already written for a human reader and are used as the diagnostic text.
- Unresolvable symbols continue to lower to `Predicate.False`, producing an empty result set with
  no error, matching current behavior for unknown codes and systems.

## Testing and parity strategy

Parity is proven in three layers, in order.

1. **Ignixa layer** — the divergence-class gate described above. Blocking.
2. **fhir-server unit layer** — the existing SQL and Cosmos search unit suites. A behavioral test
   requiring modification is treated as a defect signal, not as expected churn.
3. **fhir-server integration layer** — existing SQL integration tests against a live database,
   unchanged.

A prerequisite for layer 3: `SqlServerFhirStorageTestsFixture.cs:264` and
`CosmosDbFhirStorageTestsFixture.cs:309` currently fail to build because they omit the
`ignixaSearchTenantAccessor` argument to the `SearchOptionsFactory` constructor. This
pre-existing break must be fixed before the full-parity bar can be met.

## Delivery approach

Ignixa-first, single cutover.

1. Complete all eight Ignixa workstreams in the ignixa-fhir branch; corpus gate green.
2. Publish the resulting package versions.
3. In fhir-server, perform the SQL cutover, the Cosmos conversion, and the Core deletion as one
   coherent change.

The tradeoff is accepted deliberately: the fhir-server tree will not build mid-cutover. The
divergence-class gate is what makes this acceptable — capability risk is retired in ignixa-fhir,
where it can be measured, before any deletion happens here.

## Package distribution

The pinned Ignixa packages (`Ignixa.Search 0.6.32`, `Ignixa.Search.Sql 0.6.32-alpha`) are not on
nuget.org, which tops out at `0.6.28`, and the Microsoft Health OSS feed returns 401. Restores
currently require a local artifact source. Before merge, the packages must be available from a
feed that CI and a clean developer clone can reach. This is a hard prerequisite for the branch
being a real merge candidate.

## Non-goals

- No database schema changes.
- No new search capabilities beyond parity with today's behavior.
- No performance tuning beyond what follows naturally from consistently parameterized SQL.
- No changes to the Ignixa EF-based `Ignixa.DataLayer.SqlEntityFramework` stack, which is a
  separate implementation and not a target here.
