# SMART Authorization Constraints for the Ignixa Search Compiler

**Status:** Approved design

**Date:** 2026-07-23

**Related design:** `2026-07-23-ignixa-ci-artifact-sql-compiler-design.md`

## Context

FHIR Server currently applies SMART-specific restrictions through legacy search
rewriters and authorization checks. These transformations can add or reshape
query behavior rather than merely validate the request. Examples include:

- SMART patient compartments, including referenced resources, the user's own
  resource, universal resources, and Device-specific exceptions.
- Resource-type restrictions from SMART clinical scopes.
- Restrictions on chained searches, `_include`, and `_revinclude`.
- Scope-dependent query behavior that must remain in effect through count,
  sorting, paging, and continuation boundaries.

The Ignixa parser and SQL compiler are becoming the canonical FHIR search
pipeline. The compiler must not be allowed to replace these security
transformations with an unfiltered query. At the same time, putting OAuth
claims parsing or FHIR Server's SMART policy model into a reusable search
compiler would couple Ignixa to one server's authorization implementation.

The existing compile-only integration design deliberately excludes
access-control predicates and SMART compartments until this boundary is
defined.

## Goals

1. Preserve FHIR Server ownership of SMART claims, authorization policy, and
   request context.
2. Represent the resulting authorization constraints in the canonical Ignixa
   search representation whenever possible.
3. Apply authorization before lowering computes count, sort, include, `Top`, or
   continuation semantics.
4. Allow the same logical constraints to lower to SQL and to the existing
   Cosmos legacy objects.
5. Provide a safe migration path that keeps the legacy secured implementation
   authoritative until parity is demonstrated.
6. Add only provider-neutral extension points to Ignixa when its current
   expression model cannot represent a required SMART constraint.

## Non-goals

- Moving SMART claims parsing, OAuth scope interpretation, or access policy
  configuration into Ignixa.
- Adding raw SQL callbacks, SQL fragments, or post-emission query mutation to
  the Ignixa compiler.
- Replacing resource hydration, bundle construction, continuation-token
  handling, or write paths as part of the first SMART integration.
- Treating compiler capability failure as permission to execute an
  unconstrained query.

## Decision

Use a hybrid of two approaches:

1. **Long-term boundary:** FHIR Server owns SMART policy and supplies typed
   authorization constraints to the canonical Ignixa search pipeline.
2. **Migration technique:** initially translate the existing FHIR Server SMART
   rewrite semantics into Ignixa expressions before calling `Resolve`, `Lower`,
   and `SqlBuilder`.

Approach 1 is the durable architecture because authorization policy depends on
claims, tenant configuration, clinical-scope rules, and FHIR Server request
context. Approach 3 alone—rewriting directly into Ignixa expressions inside
FHIR Server—is the safest short-term spike, but becomes a second
FHIR Server-specific compiler front end if it must understand every new
expression and lowering rule forever.

Ignixa should therefore remain SMART-agnostic while providing generic,
provider-neutral capabilities for policy composition. If ordinary expression
composition is sufficient, FHIR Server should use it without an upstream
change. If a required rule cannot be expressed, Ignixa may add generic
operators or lowering extension points, such as:

- resource-type-scoped unions;
- reference existence and non-existence predicates;
- constraints scoped to a root, chained, include, or reverse-include leg;
- a typed pre-lowering constraint composition hook.

These APIs must not contain SMART scope names, OAuth claims, FHIR Server
configuration types, or arbitrary SQL.

## Architecture

### Ownership boundaries

**FHIR Server owns:**

- Authentication and claims extraction.
- SMART scope and clinical-scope interpretation.
- Patient/user compartment policy.
- The set of resource types and actions permitted by the request.
- Construction of a typed `SmartAuthorizationContext`.
- Conversion of that context into canonical Ignixa constraints.
- Legacy secured fallback behavior and rollout controls.

**Ignixa owns:**

- Canonical search expression semantics.
- Symbol resolution for search parameters and catalog values.
- Query-plan construction.
- Correct ordering of authorization constraints relative to count, sort,
  includes, and paging.
- Parameterized SQL emission.

The compiler must not infer authorization from raw claims and must not be
treated as the authorization decision point.

### Data flow

```text
HTTP request + authenticated claims
  -> FHIR Server SmartAuthorizationContext
  -> policy constraint builder
  -> base Ignixa search expression
       composed with root/chained/include policy constraints
  -> Resolve
  -> Lower
       authorization applied before count/sort/Top/page
  -> SqlBuilder
  -> SQL execution and hydration (later phase)
```

For Cosmos, the same canonical policy expression is lowered through the
existing Ignixa-to-legacy bridge. The bridge remains responsible for mapping
canonical expressions into legacy Cosmos search objects; it must not recreate
SMART policy independently.

### SMART compartment semantics

The policy builder must preserve the current
`SmartCompartmentSearchRewriter` semantics:

- resources that refer to the compartment subject;
- the subject's own resource;
- universal resource types such as Location, Organization, Practitioner, and
  Medication;
- Device resources without a patient reference;
- Device resources assigned to the current Patient, when the Device patient
  restriction is enabled;
- filtered resource-type sets.

These rules are naturally represented as a union of constrained legs. The
normal user search expression must be combined with the authorized union, not
executed first and filtered afterward.

### Scope, chain, and include semantics

Resource-type restrictions from SMART clinical scopes must be enforced for the
root search and for any resource type reached through chains, includes, or
reverse-includes. Existing access-control validation that rejects disallowed
chained or included resource types remains required even when the compiler is
used.

Where a policy requires different constraints for different legs, the
composition must retain those leg boundaries. A single broad root predicate
must not be used as a substitute if it changes the meaning of a union or
reference traversal.

## Extension-point rules

The first implementation should construct a canonical Ignixa expression in
FHIR Server and use the public compiler stages unchanged. This proves whether
the current expression model is sufficient.

An upstream Ignixa change is justified only when all of the following are true:

1. The behavior is a general search or query-planning concept, not a
   FHIR Server policy decision.
2. The behavior is needed by both SQL and another lowering target, or is
   explicitly modeled as a target-neutral expression.
3. The behavior can be represented without embedding OAuth or server-specific
   configuration.
4. The behavior has parser, lowering, and target-level tests.

FHIR Server must not mutate `QueryPlan` or emitted SQL after lowering to add
security predicates. Such mutation could place authorization after paging,
alter parameterization, or create SQL-only behavior that Cosmos cannot share.

## Failure handling

The compiler path is fail-closed with respect to authorization:

- If a SMART rule cannot be represented or lowered, use the existing secured
  legacy implementation.
- A capability failure must never become an empty, broad, or unauthorized
  compiler query.
- Resolver/model failures, authorization-context failures, cancellation, and
  unexpected compiler failures propagate according to the existing request
  pipeline; they are not silently converted into successful fallback.
- A compiled plan is eligible for execution only after its authorization
  shape has been explicitly validated.

Telemetry may record the policy shape, stage, failure category, resource-type
counts, package identity, schema version, and deterministic redacted
fingerprints. It must not record claims, scope values, patient identifiers,
search values, SQL text, parameter values, continuation tokens, or raw
exception messages.

## Rollout

1. **Characterization:** preserve and expand the existing SMART rewriter tests
   as canonical-expression tests, including the compartment union legs and
   Device behavior.
2. **Compile-only shadowing:** compile eligible requests behind the existing
   disabled-by-default switch while the legacy path returns the response.
3. **Differential validation:** compare resource IDs, total counts, sort order,
   include/reverse-include sets, chained results, and page boundaries between
   the secured legacy path and the compiler path.
4. **Allowlisted execution:** enable compiled execution only for shapes with
   demonstrated parity. Keep unsupported shapes on the legacy path.
5. **Expansion:** add generic Ignixa capabilities for proven representation
   gaps, then repeat the differential matrix for SQL and Cosmos.

## Validation

The test matrix must include:

- Patient and non-Patient compartments.
- Universal resource types and filtered resource-type sets.
- Device with no patient reference, Device assigned to the current patient,
  and Device assigned to another patient.
- Clinical-scope resource-type restrictions.
- Disallowed and allowed chained searches.
- Disallowed and allowed `_include` and `_revinclude` targets.
- Mixed resource-type searches and per-leg policy constraints.
- Count-only requests, sorting, `Top`, normal paging, and continuation.
- SQL compiler artifacts and Cosmos legacy lowering for the same canonical
  policy expression.
- Resolver misses, unsupported operators, cancellation, and infrastructure
  failures.
- Negative tests proving that unsupported authorization shapes cannot execute
  through an unconstrained compiler plan.

Differential tests should compare semantics, not SQL text. SQL shape and
parameterization remain separate compiler contract tests.

## Alternatives considered

### Put SMART semantics entirely in Ignixa

Rejected. Ignixa would need to understand FHIR Server claims, OAuth scope
interpretation, tenant policy, and authorization configuration. This would
couple a reusable compiler to one server and make authorization ownership
ambiguous.

### Keep all SMART rewriting in legacy FHIR Server objects

Rejected as the long-term solution. It would force the SQL compiler path to
either duplicate the rewrite logic or bypass it, and it would prevent Cosmos
and SQL from sharing one canonical policy representation. It remains the
secured fallback during migration.

### Add arbitrary post-lowering SQL predicates

Rejected. This risks applying constraints after paging or count, breaks
provider portability, and makes parameterization and security correctness
dependent on FHIR Server SQL internals.

## References

- FHIR R4 Search: https://hl7.org/fhir/R4/search.html
- SMART App Launch scopes: https://hl7.org/fhir/smart-app-launch/scopes-and-launch-context.html
- Existing FHIR Server design:
  `docs/superpowers/specs/2026-07-23-ignixa-ci-artifact-sql-compiler-design.md`
