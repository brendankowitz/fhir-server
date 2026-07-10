# FHIR Search SQL Compiler North-Star Design

**Status:** Proposed
**Date:** 2026-07-10
**Scope:** Search parsing, semantic translation, SQL planning, SQL generation, execution, and migration

## Executive recommendation

Replace the current search-to-SQL rewrite pipeline with a staged FHIR search compiler:

1. Parse request syntax without making backend decisions.
2. Bind and validate FHIR semantics once into a typed, backend-neutral semantic query.
3. Lower the semantic query into a backend-neutral logical relational plan.
4. Normalize the logical plan with small, semantics-preserving rules.
5. Use a bounded, memoized, cost-based SQL Server planner to choose physical operators against the existing schema.
6. Generate SQL through an immutable, typed SQL abstract syntax tree.
7. Execute a prepared query and feed observed outcomes back into statistics and plan policy through an explicit, asynchronous channel.

The shared compiler owns FHIR meaning. Each backend owns relational lowering, physical planning, and code generation. SQL Server and Cosmos therefore cannot silently disagree about comparator, modifier, chain, include, compartment, or authorization semantics, while remaining free to use different storage strategies.

This is intentionally more than a visitor cleanup. The present subsystem has the responsibilities and complexity of a query compiler, but its compiler stages are represented by one mutable expression model, a hand-ordered rewrite chain, and a stateful SQL emitter. Making the compiler explicit is the simplification.

## Current-state diagnosis

The current production path is approximately:

```text
HTTP query parameters
  -> SearchOptionsFactory
  -> ExpressionParser / SearchParameterExpressionParser
  -> Core Expression tree
  -> SqlSearchOptions
  -> 22 ordered expression rewrites
  -> SqlRootExpression
  -> IncludeRewriter
  -> fast-path or custom-query selection
  -> SqlQueryGenerator
  -> SqlCommandSimplifier
  -> SQL execution
  -> result and continuation-token materialization
```

Representative entry points are:

- `src/Microsoft.Health.Fhir.Shared.Core/Features/Search/SearchOptionsFactory.cs`
- `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/Parsers/ExpressionParser.cs`
- `src/Microsoft.Health.Fhir.Core/Features/Search/Expressions/Parsers/SearchParameterExpressionParser.cs`
- `src/Microsoft.Health.Fhir.SqlServer/Features/Search/SqlServerSearchService.cs`
- `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Expressions/Visitors/SqlRootExpressionRewriter.cs`
- `src/Microsoft.Health.Fhir.SqlServer/Features/Search/Expressions/Visitors/QueryGenerators/SqlQueryGenerator.cs`

The principal architecture-induced complexity is:

- `SqlServerSearchService.CreateDefaultSearchExpression` encodes roughly 22 load-bearing rewrite passes in positional order. Dependencies, required input shapes, and produced invariants are not represented in types.
- The SQL search layer contains approximately 24 rewrite/visitor files and 22 query-generator files.
- `SqlQueryGenerator` is approximately 1,900 lines, has 25 table-kind branches, and carries extensive mutable generation state for CTEs, unions, includes, chains, sorting, and limits.
- FHIR semantics and SQL tuning are duplicated or interleaved. Date equality behavior, for example, is distributed across Core expression construction, a Core equality rewriter, SQL-specific scalar rewriting, feature flags, and SQL generation.
- SQL-specific concepts leak into Core through SQL-named compartment rewriters, surrogate-ID query hints, SQL-only fields, and physical index-seek concerns.
- Fast paths, stored procedures, runtime regeneration, and hash-selected custom SQL bypass or replace parts of the normal pipeline instead of participating as declared physical strategies.
- Query-plan reuse mitigation is bolted onto SQL generation rather than modeled as a planning and parameter-sensitivity concern.
- SQL and Cosmos may implement the same FHIR search input differently because semantic decisions are made in backend paths.

FHIR search is inherently complex. Chains, reverse chains, composite parameters, includes, partial-precision dates, missing/not semantics, reference forms, compartments, authorization, sort, and continuation cannot be designed away. The target architecture isolates that essential complexity so storage and optimization complexity do not multiply it.

## Goals

- Define FHIR search semantics once and make cross-backend parity an invariant.
- Maximize SQL Server query performance while retaining the current physical schema.
- Make logical equivalence, physical alternatives, costs, and selected plans inspectable.
- Eliminate positional pass ordering, shared mutable emitter state, and post-generation SQL replacement.
- Make adding a search feature or optimization local and testable.
- Make SQL text and parameterization deterministic for a given physical plan.
- Support parameter skew through explicit plan families rather than ad hoc query text variation.
- Migrate incrementally with legacy and compiler engines running side-by-side.
- Preserve authorization, compartment, continuation, include, ordering, and result-shape behavior throughout migration.

## Non-goals

- Redesigning the existing SQL search-index schema.
- Building a general-purpose SQL optimizer for arbitrary SQL.
- Standardizing a serialized cross-product plan protocol such as Substrait.
- Replacing SQL Server's own optimizer. The FHIR planner chooses FHIR-specific relational shape and access strategies, then SQL Server optimizes the emitted statement.
- Requiring SQL and Cosmos to share physical operators, plans, or code generators.

## Options considered

### 1. FHIR compiler with bounded cost-based planning

Build explicit semantic, logical, physical, and SQL representations. Use a memo and cost model to explore physical alternatives for expensive shapes.

**Advantages**

- Cleanest semantic and backend boundaries.
- Supports join ordering, selective seeding, materialization, include placement, parameter sensitivity, and plan families.
- Gives every stage an explainable and independently testable contract.
- Converts current fast paths and custom queries into normal planner alternatives.

**Disadvantages**

- Highest initial investment.
- Requires statistics, costing, optimizer-budget, and cache-invalidation discipline.
- A poorly calibrated cost model can choose worse plans than a deterministic strategy.

### 2. Typed relational plan with deterministic optimization

Use the same semantic and logical boundaries, but run a fixed sequence of declared heuristic rules and fixed physical strategies.

**Advantages**

- Removes most current accidental complexity.
- Easier to build, test, and operate.
- Deterministic compilation and low planning cost.

**Disadvantages**

- Leaves performance opportunities unrealized for chains, includes, sorting, skew, and competing join orders.
- Risks recreating load-bearing rule order if rule contracts and phase boundaries are weak.

### 3. Modular direct SQL builder

Replace the monolithic generator with composable builders and a typed SQL AST, but do not introduce logical or physical plans.

**Advantages**

- Smallest migration and conceptual cost.
- Appropriate when each FHIR predicate has one obvious physical implementation.

**Disadvantages**

- Cross-cutting optimization remains scattered.
- Backend-neutral semantics remain difficult to enforce.
- There is no inspectable plan between FHIR meaning and SQL syntax.
- It does not address the root causes behind the current rewrite chain.

## Decision

Adopt option 1: a FHIR search compiler with bounded cost-based SQL Server planning.

The design uses cost-based exploration only where meaningful physical alternatives exist. Parsing, binding, semantic normalization, and simple logical normalization remain deterministic. The optimizer is bounded by elapsed time, memo groups, alternatives, and chain depth. A complete safe canonical plan is always available for supported search shapes.

## Architecture

### Stage 1: syntax parsing

The parser produces a syntax tree that preserves:

- resource types;
- raw parameter names and values;
- modifiers and comparators;
- chains and reverse chains;
- composite values;
- `_include`, `_revinclude`, and iteration;
- sort, count, total, summary, and paging inputs;
- source locations for diagnostics.

The syntax tree contains no resolved `SearchParameterInfo`, table name, column, index, surrogate ID, or backend capability.

### Stage 2: semantic binding and validation

The binder resolves the syntax tree against shared FHIR metadata and produces an immutable `BoundSearchQuery`.

The bound query owns:

- resolved SearchParameter identities and types;
- normalized search values and precision;
- comparator and modifier meaning;
- typed chain and reverse-chain navigation;
- include expansion meaning;
- missing/not semantics;
- compartment and authorization constraints;
- sort and result-shape requirements;
- logical paging requirements;
- explicit capability errors.

Unknown parameters, invalid modifiers, invalid comparator/type combinations, unsupported capabilities, and malformed values fail here. SQL and Cosmos consume the same bound query and shared semantic conformance fixtures.

No backend may reinterpret the bound query. A backend may reject an explicitly unsupported capability, but it may not silently substitute different semantics.

### Stage 3: logical relational planning

Each backend lowers `BoundSearchQuery` into a typed logical algebra. The algebra describes what relations must be computed without naming SQL tables or choosing join algorithms.

Core logical operators include:

- `ResourceScan`
- `SearchIndexScan`
- `Filter`
- `Project`
- `Join`
- `SemiJoin`
- `AntiJoin`
- `Union`
- `Distinct`
- `Sort`
- `Limit`
- `IncludeExpand`
- `ChainNavigate`
- `KeysetPage`

Every operator carries an explicit typed row schema. Column references are typed identities or ordinals after binding, not strings. Structural errors such as incompatible union projections are therefore impossible to render as SQL.

Authorization and compartment restrictions are logical predicates or joins with required security properties. Optimizer rules may move them only when the rule proves those properties are preserved.

### Stage 4: logical normalization

Logical normalization uses small, semantics-preserving rules over declared patterns. Examples include:

- boolean normalization;
- predicate pushdown;
- projection pruning;
- semi-join and anti-join normalization;
- include-after-limit transformation when FHIR result semantics permit it;
- chain expansion into relational navigation;
- redundant predicate elimination;
- sort and keyset-page normalization.

Each rule declares:

- the operator pattern it matches;
- required properties and preconditions;
- the equivalent result it produces;
- schema and semantic guarantees;
- whether it is terminating and idempotent;
- its phase or dependencies when ordering is unavoidable.

Rule application produces a trace. Semantic rules do not inspect schema versions, SQL statistics, SQL fields, or SQL exceptions.

### Stage 5: SQL Server physical planning

The SQL Server planner lowers normalized logical operators to alternatives over the existing schema. Physical alternatives include:

- resource and search-index seeks or scans;
- selective predicate seeding;
- nested-loops, hash, and merge joins;
- semi-join and anti-join implementations;
- CTE, derived-table, temporary-table, and spool-style materialization;
- ordered index access or Top-N sorting;
- include lookup before or after limiting where semantically valid;
- keyset paging;
- stored-procedure or specialized-query implementations with declared preconditions.

#### Memo

The planner uses a memo of equivalence groups. Transformation rules add equivalent logical expressions. Implementation rules add backend-specific physical expressions. Required physical properties include:

- output row schema;
- ordering;
- uniqueness;
- cardinality bounds;
- result kind;
- continuation shape;
- parameter-sensitivity class.

The selected plan is the lowest-cost complete expression satisfying the required properties.

#### Bounded search

Planning is bounded by:

- elapsed compilation time;
- memo group count;
- alternatives per group;
- transformation count;
- chain and include expansion depth.

If the budget expires, the planner selects the best complete plan found. If none exists, it selects a known-safe canonical physical plan for that supported logical shape. Budget exhaustion and the selected fallback are present in explain output and metrics.

An unsupported logical shape is not converted to hand-built SQL. It produces a structured planning failure or remains routed to the legacy engine during migration.

### Catalog and statistics

The planner consumes an injected catalog; it does not own schema metadata or collect statistics during compilation.

The catalog provides:

- existing table, column, index, and stored-procedure mappings;
- schema-version capabilities;
- uniqueness, nullability, and ordering properties;
- supported physical implementations;
- cardinality and distinct-count estimates;
- histograms and selectivity estimates;
- reference fan-out and target-type distributions;
- correlation estimates where available;
- statistics freshness and confidence.

Current schema-version branches become catalog capabilities or alternative implementation rules. They do not appear in semantic binding or logical normalization.

Statistics are updated asynchronously from database metadata, Query Store, and sampled execution telemetry. A request does not create statistics as a side effect of compiling or executing a read.

### Cost model

Costs are multi-dimensional and versioned:

- estimated rows;
- logical reads and I/O;
- CPU;
- memory grant;
- spill risk;
- network and result volume;
- compilation cost.

FHIR-specific factors include:

- chain and reverse-chain fan-out;
- include amplification;
- missing/not anti-join selectivity;
- composite-value correlation;
- sort coverage;
- page size;
- parameter skew.

Cost coefficients are calibrated offline from representative workloads and production telemetry. The selected coefficient version is part of explain output and cache identity.

### Stage 6: typed SQL AST and rendering

The physical plan lowers into an immutable, typed SQL AST containing:

- selects, joins, applies, unions, CTEs, and materialization statements;
- typed table and column references;
- typed predicates and expressions;
- parameters with stable identities, SQL types, and nullability;
- explicit row schemas;
- ordering and limit clauses;
- query hints represented as typed policy, not appended text.

SQL rendering is a deterministic terminal operation. The renderer cannot add FHIR semantics, reorder physical operators, choose access strategies, or inspect runtime exceptions.

The same physical plan always yields the same SQL text and parameter ordering for the same dialect and capability version.

### Stage 7: execution

Execution receives a prepared command and a typed result-shape contract. It is responsible for:

- command execution and retry policy;
- materialization;
- include and match classification;
- totals;
- continuation-token encoding;
- actual cardinality and resource-use telemetry.

Transient failures retry the same plan. A SQL query-planning failure may trigger one explicit replan from the same logical plan with the failed physical fingerprint excluded. The replan is recorded with both explain bundles and never changes FHIR semantics.

There is no hash-based, post-generation replacement of SQL. Specialized or administrator-supplied plans participate as physical implementations with explicit:

- logical-shape preconditions;
- schema capabilities;
- result schema;
- cost;
- version;
- explain identity;
- validation tests.

## Shared and backend-specific ownership

### Shared compiler

The shared compiler owns:

- syntax parsing;
- SearchParameter resolution;
- type and capability validation;
- FHIR comparator and modifier semantics;
- chain, reverse-chain, include, missing, not, sort, and paging meaning;
- authorization and compartment meaning;
- canonical bound query representation;
- semantic hashing;
- cross-backend conformance tests.

### SQL Server backend

The SQL Server backend owns:

- logical lowering for the SQL storage model;
- existing-schema catalog mappings;
- physical alternatives and implementation rules;
- statistics and cost estimation;
- memo search and optimizer budgets;
- parameter-sensitivity policy;
- typed SQL generation;
- SQL execution and physical telemetry.

### Cosmos backend

The Cosmos backend consumes the same bound query but owns its own logical lowering, physical choices, query generation, and execution. It does not consume SQL physical operators or SQL schema metadata.

## Parameterization and plan caching

### Canonical shape identity

The plan-cache key includes:

- normalized logical plan hash;
- result and continuation shape;
- authorization shape;
- backend and compiler versions;
- schema and capability versions;
- cost-model version;
- applicable plan-policy version.

Literal values are excluded from the base shape key.

### Deterministic parameters

Parameters have stable ordering, names, and SQL types derived from the physical plan. Deterministic parameterization improves application compilation-cache hits and SQL Server plan-cache reuse.

### Plan families

Queries susceptible to parameter skew may have plan families:

- a generic plan;
- one or more selectivity-bucketed plans;
- an explicitly forced custom plan when policy requires it.

Literal values select a family through a statistics-derived bucket. They do not change logical semantics or cause arbitrary SQL text generation.

The policy is observable and versioned. Cache invalidation occurs when relevant schema, catalog, statistics, compiler, or policy versions change.

## Explainability and observability

Every compiled query can produce an explain bundle containing:

- normalized bound semantic query and semantic hash;
- logical plan and hash;
- normalization rules fired;
- memo groups and relevant alternatives;
- costs, estimates, and confidence;
- selected physical plan and hash;
- optimizer budget use;
- plan-family and cache decision;
- schema, statistics, capability, compiler, and cost-model versions;
- typed SQL AST summary and SQL hash;
- estimated versus actual cardinalities;
- execution and replan history;
- continuation and result-shape contracts.

Production logging may sample or redact values, but stable structural identities remain available for diagnosis. Explain output is designed for humans and machine comparison.

## Error handling

Failures are typed by stage:

| Stage | Behavior |
|---|---|
| Parse | Return a FHIR OperationOutcome with parameter/value source location. |
| Bind | Return an OperationOutcome for unknown parameters, invalid types/modifiers, unsupported capabilities, or invalid values. |
| Logical planning | Report a compiler capability or invariant failure with the bound query. |
| Physical planning | Report rejected alternatives and missing implementation, or use a declared safe plan when only the optimization budget expired. |
| SQL AST validation | Fail before execution on invalid row schemas, columns, parameters, or union compatibility. |
| SQL rendering | Treat nondeterminism or unsupported AST nodes as compiler defects. |
| Execution | Retry only classified transient failures with the same plan; perform at most one observable same-logical-plan replan for a SQL planning failure. |

No stage returns a success-shaped fallback. Invalid input is not converted into an empty search. Compiler defects are not hidden by the legacy engine once a query shape has been promoted.

## Continuation tokens

Continuation is a semantic result requirement with a backend-specific physical payload.

During migration, a versioned token envelope contains:

- issuing engine;
- compiler and continuation format versions;
- canonical query-shape identity;
- result-order identity;
- backend payload.

A token always routes to the engine that issued it until the page sequence ends. The new engine does not attempt to reinterpret a legacy token, and the legacy engine does not reinterpret a compiler token.

The physical plan must prove a stable, unique ordering suitable for keyset paging. The continuation serializer consumes the physical result-shape contract rather than inspecting generated SQL.

## Testing strategy

### Shared semantic conformance

A generated and curated FHIR corpus verifies that syntax binds to one canonical semantic query across backends. It covers:

- all search parameter types;
- comparators and modifiers;
- precision boundaries;
- missing and not;
- chains and reverse chains;
- composites;
- include, reverse include, and iteration;
- compartments and authorization;
- sorting, totals, paging, and continuation.

### Rule-law tests

Property-based tests verify:

- input and output schemas are compatible;
- transformed plans are semantically equivalent;
- security properties are preserved;
- normalization terminates;
- rules marked idempotent are idempotent;
- memo alternatives remain in the same equivalence group.

### Planner fixtures

Synthetic catalogs and statistics produce deterministic tests of:

- alternatives generated;
- estimated costs;
- selected physical plan;
- required physical properties;
- budget exhaustion;
- stale or low-confidence statistics;
- generic and selectivity-bucketed plan families;
- cache invalidation.

### SQL structural tests

Tests validate the typed SQL AST before snapshotting rendered SQL. Snapshots remain focused on dialect output and do not carry the entire burden of proving semantics.

Row-schema validation structurally prevents bugs such as mismatched union projections.

### Differential shadow tests

The legacy and compiler engines execute the same request against the same SQL data. Comparison includes:

- matching resource IDs;
- match/include classification;
- deterministic order;
- totals;
- continuation sequences;
- error outcomes;
- logical reads;
- CPU;
- memory and spills;
- compile and execution latency.

Parity failures retain both explain bundles and block promotion for the canonical query-shape family.

### Performance corpus

A representative corpus covers common and pathological query shapes, data distributions, and parameter skew. Performance gates compare both median and tail behavior and prevent a small set of fast queries from masking severe regressions.

## Strangler migration

### Phase 1: semantic front end

Introduce the syntax tree, binder, and `BoundSearchQuery`. Adapt the bound query into the current Core expression model so existing SQL and Cosmos remain primary.

This phase centralizes FHIR semantics before replacing backend planning.

### Phase 2: shadow compilation

For production requests, compile logical plans, physical plans, and SQL without execution. Measure:

- shape coverage;
- parse and bind parity;
- compilation failures;
- optimizer cost and budget use;
- plan and SQL stability;
- catalog and statistics gaps.

### Phase 3: shadow execution

Sample eligible read-only searches and execute both engines. Compare correctness and performance through the differential corpus and production shadow telemetry.

### Phase 4: canary routing

Promote canonical query-shape families independently. Routing considers:

- canonical shape hash;
- issuing continuation engine;
- tenant or deployment policy;
- compiler version;
- known capability and performance gates.

Rollback is explicit and shape-scoped. New page sequences may return to the legacy engine after rollback; existing continuation sequences remain with their issuing engine.

### Phase 5: retirement

Delete legacy passes, generators, flags, and bypasses only after every supported shape clears:

- semantic parity;
- result and continuation parity;
- performance thresholds;
- operational explainability;
- rollback readiness.

Current fast paths, stored procedures, and custom query forms are retired or reintroduced as declared physical implementation rules before their legacy paths are removed.

## Success criteria

The pivot is successful when:

- SQL and Cosmos bind identical FHIR requests to identical semantic queries.
- No SQL-specific field, schema version, statistic, or query hint exists in shared semantic representations.
- Physical rules and their dependencies are discoverable through registries rather than a call-site sequence.
- `SqlQueryGenerator`-style global mutable state is absent; SQL generation is a pure physical-plan-to-AST-to-text operation.
- Every supported query has an explainable logical plan, physical plan, SQL hash, and cache decision.
- Adding a search type or physical strategy is additive and does not require editing unrelated generator branches.
- Rule correctness is primarily proven below E2E tests.
- Differential parity reaches the agreed corpus threshold before a shape is promoted.
- Query compilation and execution meet explicit latency, CPU, logical-read, memory, and spill targets.
- Hash-selected SQL replacement and exception-driven hidden generation modes no longer exist.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| Cost model chooses poor plans | Keep a safe canonical plan, explain estimates, compare shadow performance, version coefficients, and promote by shape. |
| Optimizer compilation cost is excessive | Bound memo search, cache by canonical shape, account for compile cost, and use deterministic normalization before exploration. |
| New representations become another translation burden | Keep each representation immutable, typed, stage-specific, and smaller than its predecessor; prohibit backend fields in semantic types. |
| Semantic behavior changes during migration | Centralize binding first, run shared conformance fixtures, and compare old/new results before promotion. |
| Continuation sequences cross engines | Version tokens and route every token to its issuing engine. |
| Existing special SQL is lost | Model valuable fast paths and stored procedures as costed physical implementations with explicit contracts. |
| Statistics are absent or stale | Include confidence, use conservative estimates, retain safe plans, and update statistics asynchronously. |
| Cost-based planning is over-engineered for simple searches | Use direct implementation rules for one-obvious-plan shapes; memo exploration only pays for alternatives that rules introduce. |

## Architectural consequences

The codebase gains more explicit types and compiler modules, but loses implicit coupling, mutable traversal state, and combinatorial pass-order reasoning.

The architecture will initially coexist with duplicate execution engines. That temporary duplication is deliberate and observable. It enables semantic centralization, shadow comparison, shape-scoped rollout, and rollback without forcing a high-risk cutover.

The highest-value simplification is not fewer total concepts. It is that each concept has one owner:

- FHIR meaning belongs to semantic binding.
- Relational equivalence belongs to logical rules.
- SQL performance belongs to physical planning.
- SQL syntax belongs to code generation.
- transient behavior belongs to execution policy.
- observed outcomes belong to telemetry and statistics.

## References

- HL7 FHIR R4 Search: <https://hl7.org/fhir/R4/search.html>
- Apache Calcite relational algebra: <https://calcite.apache.org/docs/algebra.html>
- Apache Calcite `HepPlanner`: <https://calcite.apache.org/javadocAggregate/org/apache/calcite/plan/hep/HepPlanner.html>
- Apache Calcite `VolcanoPlanner`: <https://calcite.apache.org/javadocAggregate/org/apache/calcite/plan/volcano/VolcanoPlanner.html>
- Apache DataFusion architecture: <https://datafusion.apache.org/contributor-guide/architecture.html>
- PostgreSQL planner and optimizer: <https://www.postgresql.org/docs/current/planner-optimizer.html>
- PostgreSQL prepared statements and generic/custom plans: <https://www.postgresql.org/docs/current/sql-prepare.html>
- Substrait specification: <https://substrait.io/spec/specification/>
- EF Core query processing and compiled queries: <https://learn.microsoft.com/ef/core/querying/how-query-works>
- Existing related ADRs:
  - `docs/arch/adr-2604-disable-query-plan-reuse.md`
  - `docs/arch/adr-2605-scalar-temporal-equality-rewriter.md`
