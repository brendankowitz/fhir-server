# Ignixa Compiler Capabilities Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close every capability gap in `Ignixa.Search.Sql` that blocks the FHIR Server from using it as its only SQL generator.

**Architecture:** All work happens in the `ignixa-fhir` repository on branch `ignixa-fhir-server-adoption`. The compiler's three-stage pipeline (`Resolve` → `Lower` → `SqlBuilder`) is extended with new plan-shape capabilities. `ISymbolResolver` stays the only I/O seam. No FHIR Server code is touched by this plan; that is Plan 2, written after this one ships packages.

**Tech Stack:** C#, .NET 9 / .NET 10 (multi-targeted), xUnit, Shouldly.

**Execution repo:** `C:\Users\bkowitz\.copilot\repos\ignixa-fhir` — **not** the fhir-server worktree this plan file lives in.

---

## Context you need before starting

### The corpus is already saturated — it is a regression guard, not a capability gate

`test/Ignixa.Search.Sql.Tests/Corpus/` holds 185 real FHIR searches captured from a live conformance
run, each paired with the SQL the shipping engine executed. Run it:

```bash
cd C:\Users\bkowitz\.copilot\repos\ignixa-fhir
dotnet test test/Ignixa.Search.Sql.Tests --filter "FullyQualifiedName~Corpus"
```

All 4 tests pass today, and `DifferentialBaseline.CompiledQueries = 185` — every captured query
already compiles. **Do not treat "the corpus passes" as evidence that a capability works.** The
report at `test/Ignixa.Search.Sql.Tests/Corpus/reports/differential-report.md` shows the real
picture:

| Verdict | Count | Meaning |
|---|---:|---|
| Match | 69 | Same tables, same filters |
| CompilerDoesLess | 46 | Compiler omits a filter the shipping engine applies |
| CompilerDoesMore | 14 | Compiler adds a filter |
| Divergent | 56 | Both |

`$everything` is the sharpest example: `/Patient/{id}/$everything` **compiles successfully and
silently returns only the Patient**, dropping the entire compartment traversal. The shipping engine
reads `ReferenceSearchParam` twice and `Resource` twice with a `UNION ALL`; the compiler emits
none of it. A green compile is not a correct answer.

Therefore every task in this plan carries its own behavioural test. The corpus's role is narrowed
to two guards, added in Task 1.

### Anti-pattern: filing an omitted filter under "the compiler is leaner"

The corpus README frames divergence as triage, and some divergences genuinely are wins. Task 2
fixes one that is not: for `/Patient?organization=ignixa-ref-org-123` the shipping engine emits
`ReferenceResourceTypeId = <Organization>` and the compiler emits nothing, so the compiler matches
a reference to *any* resource type carrying that id. That returns rows that are not matches. When
you find an omitted filter, prove it is redundant before recording it as a win.

---

## File structure

Files created by this plan:

| File | Responsibility |
|---|---|
| `src/Core/Ignixa.Search.Sql/Ast/ResourceVisibility.cs` | Which of current/history/deleted rows a plan may see |
| `src/Core/Ignixa.Search.Sql/Ast/ProjectionSpec.cs` | Which resource columns the terminal SELECT returns |
| `src/Core/Ignixa.Search.Sql/Ast/SurrogateIdRange.cs` | An inclusive `ResourceSurrogateId` bound |
| `src/Core/Ignixa.Search/Models/AccessConstraint.cs` | Provider-neutral per-resource-type authorization predicate |
| `src/Core/Ignixa.Search.Sql/Lowering/AccessConstraintApplier.cs` | Applies constraints to every row-producing stage |
| `src/Core/Ignixa.Search.Sql/Lowering/EverythingLoweringRule.cs` | Expands `PatientEverythingExpression` into existing plan primitives |
| `test/Ignixa.Search.Sql.Tests/Corpus/DivergenceBaseline.cs` | Verdict-class counters guarded against regression |

Files modified:

| File | Change |
|---|---|
| `Ast/QueryPlan.cs` | Gains `Visibility`, `Projection`, `SurrogateRange`, `SearchParameterHash` |
| `Ast/CteDefinition.cs` | `ResourceSource` gains a resource-type **list**; new `MultiTypeResourceSource` |
| `Builders/SqlBuilder.cs` | Visibility parameterised at 6 sites; projection stage; surrogate/hash filters |
| `Lowering/Lower.cs` | `Run` signature gains the new options; multi-type; `$everything` dispatch |
| `Lowering/ReferenceColumnEquality.cs` | Declared-target-type narrowing |
| `Lowering/StructuralContext.cs` | Multi-type resource source; constraint application |
| `Core/Ignixa.Search/Models/SearchOptions.cs` | Gains `ResourceVersionTypes`, `AccessConstraints` |

**Naming contract used by every task below.** Later tasks reference these exact names; do not rename them:

- `ResourceVisibility.Current`, `.IncludeHistory`, `.IncludeDeleted`
- `QueryPlan.Visibility`, `.Projection`, `.SurrogateRange`, `.SearchParameterHash`
- `ProjectionSpec.Columns`
- `AccessConstraint.ResourceType`, `.Predicate`
- `Lower.Run(...)` parameter names `visibility`, `projection`, `surrogateRange`, `searchParameterHash`, `accessConstraints`, `resourceTypes`

---

## Task 1: Guard the divergence classes, not just the compile count

`DifferentialBaseline` guards only how many queries compile, which is already at its ceiling. This
task adds counters for the verdict classes so later tasks can prove they moved the needle, and so a
regression that turns a `Match` into a `Divergent` fails the build.

**Files:**
- Create: `test/Ignixa.Search.Sql.Tests/Corpus/DivergenceBaseline.cs`
- Modify: `test/Ignixa.Search.Sql.Tests/Corpus/LegacyCorpusDifferentialTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `LegacyCorpusDifferentialTests.cs`, inside the class, after the existing baseline test:

```csharp
    [Fact]
    public async Task GivenTheCapturedCorpus_WhenCompiled_ThenNoFewerQueriesMatchTheShippingEngineThanTheBaseline()
    {
        var results = await RunAsync();
        var matched = results.Count(r => r.Verdict == ShapeVerdict.Match);

        // Raise this as divergences are closed. Never lower it without recording why in the report.
        matched.ShouldBeGreaterThanOrEqualTo(DivergenceBaseline.MatchingQueries);
    }

    [Fact]
    public async Task GivenTheCapturedCorpus_WhenCompiled_ThenNoMoreQueriesOmitAFilterThanTheBaseline()
    {
        var results = await RunAsync();
        var doesLess = results.Count(r => r.Verdict == ShapeVerdict.CompilerDoesLess);

        // Lower this as omitted filters are restored. Never raise it: a new omission is a
        // correctness regression until proven redundant.
        doesLess.ShouldBeLessThanOrEqualTo(DivergenceBaseline.QueriesOmittingAFilter);
    }
```

Create `DivergenceBaseline.cs`:

```csharp
namespace Ignixa.Search.Sql.Tests.Corpus;

/// <summary>
/// How closely the compiler currently tracks the shipping engine on the captured corpus. Distinct from
/// <see cref="DifferentialBaseline"/>, which guards only that a query compiles at all: every captured
/// query already compiles, so the compile count cannot detect a semantic regression. These counters can.
/// </summary>
public static class DivergenceBaseline
{
    /// <summary>Captured queries whose compiled shape reads the same tables with the same filters.</summary>
    public const int MatchingQueries = 69;

    /// <summary>Captured queries where the shipping engine applies a filter the compiler does not.</summary>
    public const int QueriesOmittingAFilter = 46;
}
```

- [ ] **Step 2: Run the tests to see whether they compile and pass**

```bash
cd C:\Users\bkowitz\.copilot\repos\ignixa-fhir
dotnet test test/Ignixa.Search.Sql.Tests --filter "FullyQualifiedName~Corpus"
```

Expected: a compile error naming `DifferentialResult.Verdict` if that property does not exist.
`DifferentialResult` is a 5-field record whose last field is the `ShapeComparison` result. Open
`test/Ignixa.Search.Sql.Tests/Corpus/DifferentialResult.cs` and
`test/Ignixa.Search.Sql.Tests/Corpus/ShapeVerdict.cs` and use whatever the verdict is actually
called — if it is reached via the comparison field, write `r.Comparison?.Verdict` instead of
`r.Verdict` in both tests. Adjust and re-run until the tests compile.

- [ ] **Step 3: Confirm both new tests pass at the recorded baseline**

Expected: 6 passed, 0 failed, on both `net9.0` and `net10.0`.

If a count is off by a small amount, correct the constant in `DivergenceBaseline` to the observed
value rather than changing the test — the constants are a record of today's behaviour, not a target.

- [ ] **Step 4: Commit**

```bash
git add test/Ignixa.Search.Sql.Tests/Corpus/DivergenceBaseline.cs test/Ignixa.Search.Sql.Tests/Corpus/LegacyCorpusDifferentialTests.cs
git commit -m "test(corpus): guard divergence classes, not just the compile count

Every captured query already compiles, so CompiledQueries cannot detect a
semantic regression. Guard the Match and CompilerDoesLess counts too."
```

---

## Task 2: Narrow an untyped reference search to the parameter's declared target types

`/Patient?organization=ignixa-ref-org-123` must only match `Organization/ignixa-ref-org-123`. Today
`ReferenceColumnEquality.Build` emits no `ReferenceResourceTypeId` filter when the search value
carries no resource type, so it also matches `Practitioner/ignixa-ref-org-123`. The shipping engine
narrows to the search parameter's declared targets, ORing them and allowing NULL.

**Files:**
- Modify: `src/Core/Ignixa.Search.Sql/Lowering/ReferenceColumnEquality.cs`
- Modify: `src/Core/Ignixa.Search.Sql/Lowering/Leaf/ReferenceLoweringRule.cs`
- Test: `test/Ignixa.Search.Sql.Tests/Lowering/ReferenceLoweringRuleTests.cs`

- [ ] **Step 1: Read the two files you are about to change**

```bash
cd C:\Users\bkowitz\.copilot\repos\ignixa-fhir
cat src/Core/Ignixa.Search.Sql/Lowering/Leaf/ReferenceLoweringRule.cs
cat test/Ignixa.Search.Sql.Tests/Lowering/ReferenceLoweringRuleTests.cs
```

You need one fact before writing code: how `ReferenceLoweringRule` obtains the `SearchParameterInfo`
for the leaf. The declared targets are already available — `SearchParameterInfo` exposes
`TargetResourceTypes` (verified at `src/Core/Ignixa.Search/Models/SearchParameterInfo.cs:114`) and
its primary constructor takes a `targetResourceTypes` argument — so no `Resolve`-stage change is
needed.

- [ ] **Step 2: Write the failing test**

Append to `ReferenceLoweringRuleTests.cs`:

```csharp
    [Fact]
    public void GivenAnUntypedReferenceValue_WhenLowered_ThenItIsNarrowedToTheParametersDeclaredTargetTypes()
    {
        var parameter = new SearchParameterInfo(
            "organization",
            "organization",
            SearchParamType.Reference,
            new Uri("http://hl7.org/fhir/SearchParameter/Patient-organization"),
            targetResourceTypes: ["Organization"]);

        var predicate = new SearchParameterPredicateExpression(
            parameter,
            SearchComparator.Eq,
            modifier: null,
            new ReferenceSearchValue(ReferenceKind.InternalOrExternal, baseUri: null, resourceType: null, resourceId: "org-123"));

        var symbols = new SymbolTable(
            new Dictionary<string, short> { [parameter.Url!.ToString()] = 210 },
            new Dictionary<string, short> { ["Patient"] = 103, ["Organization"] = 111 });

        var plan = Lower.Run(
            predicate,
            symbols,
            "Patient",
            includes: [],
            revIncludes: [],
            includeLimit: 0,
            sort: [],
            SortPhase.Valued,
            page: null).Plan;

        var sql = SqlBuilder.Emit(plan).Sql;

        sql.ShouldContain("ReferenceResourceTypeId");
    }
```

- [ ] **Step 3: Run it and watch it fail**

```bash
dotnet test test/Ignixa.Search.Sql.Tests --filter "FullyQualifiedName~GivenAnUntypedReferenceValue_WhenLowered_ThenItIsNarrowedToTheParametersDeclaredTargetTypes"
```

Expected: FAIL — either a compile error because `SearchParameterInfo` has no
`targetResourceTypes` parameter (fix the test to match the real constructor, keeping the intent),
or an assertion failure because the emitted SQL contains no `ReferenceResourceTypeId`.

- [ ] **Step 4: Add the narrowing to `ReferenceColumnEquality.Build`**

Replace the early return for the untyped case:

```csharp
        // A value the parser could not resolve to a resource type is still constrained: the search
        // parameter itself declares which types it may point at, and the shipping engine narrows to
        // them. Without this a bare id matches a reference to any type carrying that id, which
        // returns rows that are not matches.
        if (string.IsNullOrEmpty(value.ResourceType))
        {
            var declared = context.DeclaredTargetResourceTypeIds(parameter);
            if (declared.Count == 0)
            {
                return idPredicate;
            }

            Predicate targets = new Predicate.Equal(
                new SqlColumnRef(table.TableName, resourceTypeColumn),
                context.Parameter(declared[0]));

            for (var i = 1; i < declared.Count; i++)
            {
                targets = new Predicate.Or(
                    targets,
                    new Predicate.Equal(
                        new SqlColumnRef(table.TableName, resourceTypeColumn),
                        context.Parameter(declared[i])));
            }

            // A stored row may carry a null type when the reference was indexed untyped; the
            // shipping engine admits those rather than dropping them.
            targets = new Predicate.Or(targets, new Predicate.IsNull(new SqlColumnRef(table.TableName, resourceTypeColumn)));

            return new Predicate.And(targets, idPredicate);
        }
```

Add `SearchParameterInfo parameter` to `Build`'s parameter list and pass it from every call site
(`ReferenceLoweringRule` and `ReferenceTokenLoweringRule` — find them with
`grep -rn "ReferenceColumnEquality.Build" src/`).

Add to `LeafContext`:

```csharp
    /// <summary>
    /// The ResourceTypeIds a reference parameter declares it may point at, skipping any the symbol table
    /// could not resolve. Empty when the parameter declares no targets, which leaves the reference
    /// unconstrained by type.
    /// </summary>
    public IReadOnlyList<short> DeclaredTargetResourceTypeIds(SearchParameterInfo parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        if (parameter.TargetResourceTypes is not { Count: > 0 } targets)
        {
            return [];
        }

        var ids = new List<short>(targets.Count);
        foreach (var target in targets)
        {
            if (_symbols.TryGetResourceTypeId(target, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
```

If `SymbolTable` has no `TryGetResourceTypeId`, read `src/Core/Ignixa.Search.Sql/Symbols/SymbolTable.cs`
and use its actual lookup shape — do not add a second lookup method that duplicates an existing one.

- [ ] **Step 5: Run the test and watch it pass**

```bash
dotnet test test/Ignixa.Search.Sql.Tests --filter "FullyQualifiedName~GivenAnUntypedReferenceValue"
```

Expected: PASS on both target frameworks.

- [ ] **Step 6: Run the whole SQL suite, including the corpus**

```bash
dotnet test test/Ignixa.Search.Sql.Tests
```

Expected: all tests pass. The `QueriesOmittingAFilter` guard should now be *below* 46 — several
corpus entries (`/Patient?organization=...`, `/Patient?general-practitioner=...`,
`/DocumentReference/$docref?patient=...`) omitted exactly this filter.

- [ ] **Step 7: Lower the baseline to the improvement you just made**

Read the new count from the generated report:

```bash
grep -A 8 "^## Summary" test/Ignixa.Search.Sql.Tests/bin/Debug/net10.0/legacy-sql-differential-report.md
```

Set `DivergenceBaseline.QueriesOmittingAFilter` to the new `CompilerDoesLess` count and
`MatchingQueries` to the new `Match` count. Copy the regenerated report over
`test/Ignixa.Search.Sql.Tests/Corpus/reports/differential-report.md` so the checked-in snapshot
stays current.

- [ ] **Step 8: Re-run and commit**

```bash
dotnet test test/Ignixa.Search.Sql.Tests
git add -A
git commit -m "fix(lowering): narrow an untyped reference search to its declared target types

A bare id matched a reference to any resource type carrying that id, so
/Patient?organization=X also matched Practitioner/X. Narrow to the search
parameter's declared targets, admitting a null stored type, matching the
shipping engine."
```

---

## Task 3: Make resource visibility a plan input instead of a hardcoded filter

`IsHistory = 0 AND IsDeleted = 0` is hardcoded at six sites in `SqlBuilder.cs` (lines ~281, 313,
328, 535, 549, 586). The FHIR Server needs history and soft-deleted rows for `_history`, `$export`,
and reindex, so this becomes a plan input.

**Files:**
- Create: `src/Core/Ignixa.Search.Sql/Ast/ResourceVisibility.cs`
- Modify: `src/Core/Ignixa.Search.Sql/Ast/QueryPlan.cs`
- Modify: `src/Core/Ignixa.Search.Sql/Builders/SqlBuilder.cs`
- Modify: `src/Core/Ignixa.Search.Sql/Lowering/Lower.cs`
- Modify: `src/Core/Ignixa.Search/Models/SearchOptions.cs`
- Test: `test/Ignixa.Search.Sql.Tests/Ast/EmitTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `EmitTests.cs`:

```csharp
    [Fact]
    public void GivenAPlanThatIncludesHistory_WhenEmitted_ThenNoIsHistoryFilterIsApplied()
    {
        var plan = new QueryPlan(
            [new CteDefinition.ResourceSource(103)],
            new CteRef(0),
            Visibility: new ResourceVisibility(IncludeHistory: true, IncludeDeleted: false));

        var sql = SqlBuilder.Emit(plan).Sql;

        sql.ShouldNotContain("IsHistory = 0");
        sql.ShouldContain("IsDeleted = 0");
    }

    [Fact]
    public void GivenAPlanWithDefaultVisibility_WhenEmitted_ThenBothCurrentRowFiltersAreApplied()
    {
        var plan = new QueryPlan([new CteDefinition.ResourceSource(103)], new CteRef(0));

        var sql = SqlBuilder.Emit(plan).Sql;

        sql.ShouldContain("IsHistory = 0");
        sql.ShouldContain("IsDeleted = 0");
    }
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test test/Ignixa.Search.Sql.Tests --filter "FullyQualifiedName~GivenAPlanThatIncludesHistory"
```

Expected: FAIL — compile error, `ResourceVisibility` does not exist.

- [ ] **Step 3: Create `ResourceVisibility.cs`**

```csharp
namespace Ignixa.Search.Sql.Ast;

/// <summary>
/// Which rows of dbo.Resource a plan may see. The default, <see cref="Current"/>, excludes superseded
/// versions and soft-deleted rows — the only shape an ordinary search wants. A caller reading history
/// (_history), exporting, or reindexing relaxes one or both, so the filter is a plan input rather than
/// something Emit assumes.
/// </summary>
/// <param name="IncludeHistory">When true, no <c>IsHistory = 0</c> filter is emitted.</param>
/// <param name="IncludeDeleted">When true, no <c>IsDeleted = 0</c> filter is emitted.</param>
public sealed record ResourceVisibility(bool IncludeHistory, bool IncludeDeleted)
{
    /// <summary>Current, non-deleted rows only — what an ordinary search means by "a resource".</summary>
    public static ResourceVisibility Current { get; } = new(IncludeHistory: false, IncludeDeleted: false);
}
```

- [ ] **Step 4: Add `Visibility` to `QueryPlan`**

Append the parameter to the record, after `CountOnly`:

```csharp
    bool CountOnly = false,
    ResourceVisibility? Visibility = null)
```

and inside the record body, above `Explain()`:

```csharp
    /// <summary>The plan's visibility, defaulting to current non-deleted rows when the caller named none.</summary>
    public ResourceVisibility EffectiveVisibility => Visibility ?? ResourceVisibility.Current;
```

Nullable-with-a-default is used rather than a non-null default value because a record's positional
default must be a compile-time constant, which a record instance is not.

- [ ] **Step 5: Thread it through `SqlBuilder`**

Add near the top of `SqlBuilder`:

```csharp
    /// <summary>
    /// The current-row filter for a dbo.Resource scan under a given visibility, already prefixed with
    /// " AND " and the caller's column qualifier, or empty when both relaxations are on.
    /// </summary>
    private static string ResourceRowFilter(ResourceVisibility visibility, string qualifier)
    {
        var clauses = new List<string>(2);
        if (!visibility.IncludeHistory)
        {
            clauses.Add($"{qualifier}IsHistory = 0");
        }

        if (!visibility.IncludeDeleted)
        {
            clauses.Add($"{qualifier}IsDeleted = 0");
        }

        return clauses.Count == 0 ? string.Empty : " AND " + string.Join(" AND ", clauses);
    }
```

Then replace each of the six hardcoded sites. Every emitter that needs it must receive the
visibility — thread a `ResourceVisibility visibility` parameter down from `Emit` through
`EmitCte`, `EmitParamSource`, `EmitChainJoin`, `EmitNotReferencedSource`, `EmitResourceSource`, and
`EmitIncludeStage`.

- The `ParamSource` history clause (line ~281) stays driven off the catalog, but gates on
  visibility too:

```csharp
        var historyClause = !visibility.IncludeHistory && p.Table.Columns.Any(c => c.Name == "IsHistory")
            ? " AND IsHistory = 0"
            : string.Empty;
```

- The four `r.IsHistory = 0 AND r.IsDeleted = 0` join sites (lines ~313, 328, 586 and the
  `EmitNotReferencedSource` WHERE at ~535) become `{ResourceRowFilter(visibility, "r.")}`. Note the
  join sites currently begin the line with `AND`; using `ResourceRowFilter` means the line becomes
  `$"       {ResourceRowFilter(visibility, "r.").TrimStart()}\n"` — check the emitted text in the
  test output and keep the SQL valid when the filter is empty (no dangling `AND`, no blank line
  that breaks a following `INNER JOIN`).

- `EmitResourceSource` (line ~549) becomes:

```csharp
        return $"    SELECT ResourceTypeId AS T1, ResourceSurrogateId AS Sid1\n" +
               $"    FROM dbo.Resource\n" +
               $"    WHERE ResourceTypeId = {EmitParam(new SqlParameterRef(rs.ResourceTypeId), parameters)}{ResourceRowFilter(visibility, string.Empty)}{predicateClause}";
```

- [ ] **Step 6: Add the parameter to `Lower.Run`**

Add to the signature, after `approximationReferenceTime`:

```csharp
        DateTimeOffset? approximationReferenceTime = null,
        ResourceVisibility? visibility = null)
```

and pass it into the constructed plan:

```csharp
            new QueryPlan(context.Ctes, match, top, outerPredicate, includeStages, sortSpec, page, countOnly, visibility),
```

- [ ] **Step 7: Run the tests**

```bash
dotnet test test/Ignixa.Search.Sql.Tests
```

Expected: PASS. Existing emit tests assert on SQL text and will catch a dangling `AND` or a broken
join immediately — if any fail, the whitespace handling in Step 5 is wrong, not the design.

- [ ] **Step 8: Add `ResourceVersionTypes` to the options model**

In `src/Core/Ignixa.Search/Models/SearchOptions.cs`, add above the closing brace:

```csharp
    /// <summary>
    /// Which resource versions the search may return. Defaults to <see cref="ResourceVersionTypes.Latest"/>,
    /// the only shape an ordinary search wants; _history, $export, and reindex widen it.
    /// </summary>
    public ResourceVersionTypes ResourceVersionTypes { get; set; } = ResourceVersionTypes.Latest;
```

and add the flags enum below the `SummaryType` enum in the same file:

```csharp
/// <summary>
/// Which versions of a resource a search may return. A flags enum because _history returns latest and
/// history together, and $export may additionally need soft-deleted rows.
/// </summary>
[Flags]
public enum ResourceVersionTypes
{
    /// <summary>No version selected. Not a valid search input; present so the default is explicit.</summary>
    None = 0,

    /// <summary>The current version of each resource.</summary>
    Latest = 1,

    /// <summary>Superseded versions.</summary>
    History = 2,

    /// <summary>Soft-deleted rows.</summary>
    SoftDeleted = 4,
}
```

- [ ] **Step 9: Commit**

```bash
dotnet test test/Ignixa.Search.Sql.Tests
git add -A
git commit -m "feat(sql): make resource visibility a plan input

IsHistory/IsDeleted were hardcoded at six emitter sites, so a caller could
not read history or soft-deleted rows. Add ResourceVisibility to QueryPlan
and ResourceVersionTypes to SearchOptions."
```

---

## Task 4: Emit the resource projection

`EmittedSql` returns `(T1, Sid1)` today and leaves the caller to fetch rows. The FHIR Server needs
the compiler to emit the whole statement, so the plan gains a projection stage. `SqlBuilder` already
joins `dbo.Resource` for `CountOnly` with an outer predicate, so the join exists to extend.

**Files:**
- Create: `src/Core/Ignixa.Search.Sql/Ast/ProjectionSpec.cs`
- Modify: `src/Core/Ignixa.Search.Sql/Ast/QueryPlan.cs`
- Modify: `src/Core/Ignixa.Search.Sql/Builders/SqlBuilder.cs`
- Test: `test/Ignixa.Search.Sql.Tests/Ast/EmitTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void GivenAPlanWithAProjection_WhenEmitted_ThenTheTerminalSelectReturnsTheNamedResourceColumns()
    {
        var plan = new QueryPlan(
            [new CteDefinition.ResourceSource(103)],
            new CteRef(0),
            Projection: new ProjectionSpec(["ResourceId", "Version", "RawResource", "IsDeleted"]));

        var sql = SqlBuilder.Emit(plan).Sql;

        sql.ShouldContain("r.ResourceId");
        sql.ShouldContain("r.RawResource");
        sql.ShouldContain("INNER JOIN dbo.Resource r");
    }

    [Fact]
    public void GivenAPlanWithNoProjection_WhenEmitted_ThenTheTerminalSelectReturnsIdentityColumnsOnly()
    {
        var plan = new QueryPlan([new CteDefinition.ResourceSource(103)], new CteRef(0));

        var sql = SqlBuilder.Emit(plan).Sql;

        sql.ShouldNotContain("RawResource");
    }
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test test/Ignixa.Search.Sql.Tests --filter "FullyQualifiedName~GivenAPlanWithAProjection"
```

Expected: FAIL — compile error, `ProjectionSpec` does not exist.

- [ ] **Step 3: Create `ProjectionSpec.cs`**

```csharp
namespace Ignixa.Search.Sql.Ast;

/// <summary>
/// The dbo.Resource columns the terminal SELECT returns alongside the identity columns. A null
/// projection on a <see cref="QueryPlan"/> keeps the historical (T1, Sid1) shape, where the caller
/// fetches rows itself; naming columns makes the compiler emit the whole statement instead.
/// </summary>
/// <remarks>
/// Column names are emitted verbatim, qualified with the terminal join's <c>r.</c> alias. They are
/// compiler-supplied identifiers, never user input, so no quoting or validation is applied — the same
/// trust boundary every other identifier in this emitter sits behind.
/// </remarks>
/// <param name="Columns">Column names in the order they should appear in the SELECT list.</param>
public sealed record ProjectionSpec(IReadOnlyList<string> Columns);
```

- [ ] **Step 4: Add `Projection` to `QueryPlan`**

```csharp
    ResourceVisibility? Visibility = null,
    ProjectionSpec? Projection = null)
```

- [ ] **Step 5: Emit the projection**

In `SqlBuilder.Emit`, in the terminal SELECT for the no-includes and includes shapes, when
`plan.Projection` is non-null: append the projected columns to the select list, add
`INNER JOIN dbo.Resource r ON r.ResourceTypeId = <match>.T1 AND r.ResourceSurrogateId = <match>.Sid1`
followed by `ResourceRowFilter(plan.EffectiveVisibility, "r.")`, and reuse the existing outer-predicate
join rather than emitting a second one when both are present.

Add a helper:

```csharp
    /// <summary>The projected column list, prefixed with ", " and qualified with the terminal join alias, or empty.</summary>
    private static string ProjectionColumns(ProjectionSpec? projection)
        => projection is null
            ? string.Empty
            : ", " + string.Join(", ", projection.Columns.Select(c => $"r.{c}"));
```

`CountOnly` ignores the projection: a count has no rows to project. Assert that explicitly:

```csharp
    [Fact]
    public void GivenACountOnlyPlanWithAProjection_WhenEmitted_ThenTheProjectionIsIgnored()
    {
        var plan = new QueryPlan(
            [new CteDefinition.ResourceSource(103)],
            new CteRef(0),
            CountOnly: true,
            Projection: new ProjectionSpec(["RawResource"]));

        var sql = SqlBuilder.Emit(plan).Sql;

        sql.ShouldContain("COUNT_BIG(DISTINCT");
        sql.ShouldNotContain("RawResource");
    }
```

- [ ] **Step 6: Run the tests**

```bash
dotnet test test/Ignixa.Search.Sql.Tests
```

Expected: PASS, including `EmitSqlGrammarTests`, which parses emitted SQL — a malformed SELECT list
or duplicated join fails there first.

- [ ] **Step 7: Document the result shape on `EmittedSql`**

Update the XML doc on `EmittedSql` to state that a plan with a projection appends the projected
columns after the identity columns (and after `IsMatch`/`IsPartial` when includes are present), so
a caller reads them by ordinal from `plan.Projection.Columns`.

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(sql): emit the resource projection

The compiler returned identity columns and left the fetch to its caller.
Add ProjectionSpec so it emits the whole statement, which the FHIR Server
needs in order to stop appending SQL of its own."
```

---

## Task 5: Lower a surrogate-ID range

`SearchOptions.StartSurrogateId` / `EndSurrogateId` exist on the options model but never reach the
plan. `$export` partitions work across writers with them.

**Files:**
- Create: `src/Core/Ignixa.Search.Sql/Ast/SurrogateIdRange.cs`
- Modify: `src/Core/Ignixa.Search.Sql/Ast/QueryPlan.cs`, `Builders/SqlBuilder.cs`, `Lowering/Lower.cs`
- Test: `test/Ignixa.Search.Sql.Tests/Ast/EmitTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void GivenAPlanWithASurrogateIdRange_WhenEmitted_ThenBothBoundsAreBoundParameters()
    {
        var plan = new QueryPlan(
            [new CteDefinition.ResourceSource(103)],
            new CteRef(0),
            SurrogateRange: new SurrogateIdRange(new SqlParameterRef(5000L), new SqlParameterRef(6000L)));

        var emitted = SqlBuilder.Emit(plan);

        emitted.Sql.ShouldContain("Sid1 >=");
        emitted.Sql.ShouldContain("Sid1 <=");
        emitted.Parameters.Select(p => p.Value).ShouldContain(5000L);
        emitted.Parameters.Select(p => p.Value).ShouldContain(6000L);
    }
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test test/Ignixa.Search.Sql.Tests --filter "FullyQualifiedName~GivenAPlanWithASurrogateIdRange"
```

Expected: FAIL — compile error, `SurrogateIdRange` does not exist.

- [ ] **Step 3: Create `SurrogateIdRange.cs`**

```csharp
namespace Ignixa.Search.Sql.Ast;

/// <summary>
/// An inclusive ResourceSurrogateId window applied to the match set, used to partition a bulk read
/// across workers. Both bounds render as bound parameters rather than literals: they are caller input,
/// and inlining them would defeat plan reuse across partitions that differ only in their window.
/// </summary>
/// <param name="Start">The inclusive lower bound.</param>
/// <param name="End">The inclusive upper bound.</param>
public sealed record SurrogateIdRange(SqlParameterRef Start, SqlParameterRef End);
```

- [ ] **Step 4: Add `SurrogateRange` to `QueryPlan` and emit it**

Add `SurrogateIdRange? SurrogateRange = null` to the record. In `SqlBuilder`, add its two clauses to
the terminal WHERE list alongside the outer predicate, in every shape including `CountOnly`:

```csharp
        if (plan.SurrogateRange is { } range)
        {
            whereClauses.Add($"{CteLabel(plan.Match.Index)}.Sid1 >= {EmitParam(range.Start, parameters)}");
            whereClauses.Add($"{CteLabel(plan.Match.Index)}.Sid1 <= {EmitParam(range.End, parameters)}");
        }
```

Use whatever alias the surrounding shape already gave the match CTE (`m` in the `CountOnly` path);
do not introduce a second alias for the same CTE.

- [ ] **Step 5: Add the parameter to `Lower.Run`**

```csharp
        ResourceVisibility? visibility = null,
        SurrogateIdRange? surrogateRange = null)
```

and pass it to the `QueryPlan` constructor.

- [ ] **Step 6: Run the tests and commit**

```bash
dotnet test test/Ignixa.Search.Sql.Tests
git add -A
git commit -m "feat(sql): lower a surrogate-id range onto the match set

SearchOptions carried Start/EndSurrogateId but nothing consumed them, so a
partitioned bulk read could not be expressed."
```

---

## Task 6: Filter on the search-parameter hash

Reindex finds resources whose indexed parameters are stale by comparing
`dbo.Resource.SearchParamHash` against the current definition hash. This is a resource-column
filter, so it belongs on the plan next to the surrogate range.

**Files:**
- Modify: `src/Core/Ignixa.Search.Sql/Ast/QueryPlan.cs`, `Builders/SqlBuilder.cs`, `Lowering/Lower.cs`
- Test: `test/Ignixa.Search.Sql.Tests/Ast/EmitTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void GivenAPlanWithASearchParameterHash_WhenEmitted_ThenRowsCarryingThatHashAreExcluded()
    {
        var plan = new QueryPlan(
            [new CteDefinition.ResourceSource(103)],
            new CteRef(0),
            SearchParameterHash: new SqlParameterRef("abc123"));

        var emitted = SqlBuilder.Emit(plan);

        emitted.Sql.ShouldContain("SearchParamHash");
        emitted.Parameters.Select(p => p.Value).ShouldContain("abc123");
    }
```

- [ ] **Step 2: Run it and watch it fail**

Expected: FAIL — `QueryPlan` has no `SearchParameterHash`.

- [ ] **Step 3: Add `SearchParameterHash` to `QueryPlan`**

```csharp
    /// <summary>
    /// When set, restricts the match set to rows whose dbo.Resource.SearchParamHash differs from this
    /// value — the resources reindex must revisit because their indexed parameters predate the current
    /// definition set. A row with a NULL hash has never been indexed and always qualifies.
    /// </summary>
    SqlParameterRef? SearchParameterHash = null)
```

- [ ] **Step 4: Emit it**

It needs the `dbo.Resource` join, the same one the projection and outer predicate use. Add to the
terminal WHERE list:

```csharp
        if (plan.SearchParameterHash is { } hash)
        {
            whereClauses.Add($"(r.SearchParamHash IS NULL OR r.SearchParamHash <> {EmitParam(hash, parameters)})");
        }
```

and make the `dbo.Resource` join emit when any of projection, outer predicate, or search-parameter
hash is present — extract that condition into a single local so the three cases cannot drift:

```csharp
        var needsResourceJoin = plan.OuterPredicate is not null
            || plan.Projection is not null
            || plan.SearchParameterHash is not null;
```

- [ ] **Step 5: Add a test proving the join appears exactly once**

```csharp
    [Fact]
    public void GivenAPlanWithBothAProjectionAndAHashFilter_WhenEmitted_ThenTheResourceJoinAppearsOnce()
    {
        var plan = new QueryPlan(
            [new CteDefinition.ResourceSource(103)],
            new CteRef(0),
            Projection: new ProjectionSpec(["RawResource"]),
            SearchParameterHash: new SqlParameterRef("abc123"));

        var sql = SqlBuilder.Emit(plan).Sql;

        Regex.Matches(sql, "INNER JOIN dbo.Resource r").Count.ShouldBe(1);
    }
```

Add `using System.Text.RegularExpressions;` at the top of the file if it is not already there.

- [ ] **Step 6: Add the parameter to `Lower.Run`, run the tests, and commit**

```bash
dotnet test test/Ignixa.Search.Sql.Tests
git add -A
git commit -m "feat(sql): filter the match set on the search-parameter hash

Reindex selects resources whose indexed parameters predate the current
definition set; nothing in the plan could express that."
```

---

## Task 7: Support a multi-type and system-wide match set

`Lower.Run` throws when `targetResourceType` is null unless the whole expression is a wildcard
compartment search. System-wide search (`GET /?_type=Patient,Observation` and bare `GET /`) needs a
match set spanning several types, or all types.

**Files:**
- Modify: `src/Core/Ignixa.Search.Sql/Ast/CteDefinition.cs`, `Builders/SqlBuilder.cs`,
  `Lowering/Lower.cs`, `Lowering/StructuralContext.cs`
- Test: `test/Ignixa.Search.Sql.Tests/Lowering/LowerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void GivenSeveralResourceTypesAndNoExpression_WhenLowered_ThenTheMatchSetSpansAllOfThem()
    {
        var symbols = new SymbolTable(
            new Dictionary<string, short>(),
            new Dictionary<string, short> { ["Patient"] = 103, ["Observation"] = 104 });

        var plan = Lower.Run(
            expression: null,
            symbols,
            targetResourceType: null,
            includes: [],
            revIncludes: [],
            includeLimit: 0,
            sort: [],
            SortPhase.Valued,
            page: null,
            resourceTypes: ["Patient", "Observation"]).Plan;

        var sql = SqlBuilder.Emit(plan).Sql;

        sql.ShouldContain("ResourceTypeId IN (103, 104)");
    }

    [Fact]
    public void GivenNoResourceTypeAtAll_WhenLowered_ThenTheMatchSetIsEveryType()
    {
        var symbols = new SymbolTable(new Dictionary<string, short>(), new Dictionary<string, short>());

        var plan = Lower.Run(
            expression: null,
            symbols,
            targetResourceType: null,
            includes: [],
            revIncludes: [],
            includeLimit: 0,
            sort: [],
            SortPhase.Valued,
            page: null,
            resourceTypes: []).Plan;

        var sql = SqlBuilder.Emit(plan).Sql;

        sql.ShouldNotContain("ResourceTypeId =");
        sql.ShouldNotContain("ResourceTypeId IN");
    }
```

- [ ] **Step 2: Run them and watch them fail**

```bash
dotnet test test/Ignixa.Search.Sql.Tests --filter "FullyQualifiedName~GivenSeveralResourceTypesAndNoExpression"
```

Expected: FAIL — `Lower.Run` has no `resourceTypes` parameter.

- [ ] **Step 3: Add `MultiTypeResourceSource` to `CteDefinition`**

```csharp
    /// <summary>
    /// Current rows of dbo.Resource across several resource types, or across every type when
    /// <paramref name="ResourceTypeIds"/> is empty — the system-wide search base set. Kept separate from
    /// <see cref="ResourceSource"/> rather than widening it to a list, because ResourceSource's single
    /// short is what lets a chain's target scope stay a scalar; conflating them would push an
    /// "exactly one" assertion into every consumer of that scope.
    /// </summary>
    public sealed record MultiTypeResourceSource(IReadOnlyList<short> ResourceTypeIds, Predicate? Predicate = null) : CteDefinition;
```

- [ ] **Step 4: Emit it**

Add to `EmitCte`'s switch, above the `_ =>` arm:

```csharp
        CteDefinition.MultiTypeResourceSource mts => EmitMultiTypeResourceSource(mts, parameters, visibility),
```

and:

```csharp
    /// <summary>Renders a MultiTypeResourceSource: a dbo.Resource scan across a set of types, or every type when the set is empty.</summary>
    private static string EmitMultiTypeResourceSource(
        CteDefinition.MultiTypeResourceSource mts,
        List<EmittedSqlParameter> parameters,
        ResourceVisibility visibility)
    {
        var predicateClause = mts.Predicate is null ? string.Empty : $" AND {EmitPredicate(mts.Predicate, parameters)}";
        var typeClause = mts.ResourceTypeIds.Count == 0
            ? string.Empty
            : $" AND ResourceTypeId IN ({string.Join(", ", mts.ResourceTypeIds)})";

        var rowFilter = ResourceRowFilter(visibility, string.Empty);
        var where = (typeClause + rowFilter + predicateClause).TrimStart();
        where = where.StartsWith("AND ", StringComparison.Ordinal) ? where[4..] : where;

        return $"    SELECT ResourceTypeId AS T1, ResourceSurrogateId AS Sid1\n" +
               $"    FROM dbo.Resource\n" +
               (where.Length == 0 ? string.Empty : $"    WHERE {where}");
    }
```

Type ids are emitted as literals, matching `ParamSource` and `ChainJoin` — they are catalog
surrogates the compiler resolved, not caller input, and `PlanExplainer` depends on the parameter
ordinals staying free of them.

- [ ] **Step 5: Add `PlanExplainer` support**

`PlanExplainer` throws `NotSupportedException` for an unknown `CteDefinition`, and
`SearchCompiler` deliberately attributes that to the explainer rather than to Lower. Add a
`MultiTypeResourceSource` arm to `PlanExplainer` in the same shape as its `ResourceSource` arm, and
add a case to `test/Ignixa.Search.Sql.Tests/Ast/PlanExplainerTests.cs` asserting it prints.

- [ ] **Step 6: Add `resourceTypes` to `Lower.Run` and use it**

```csharp
        SurrogateIdRange? surrogateRange = null,
        IReadOnlyList<string>? resourceTypes = null)
```

Replace `RequireResourceType(targetResourceType)` in the two no-expression / no-remaining arms with a
call to a new helper that prefers the single type and falls back to the set:

```csharp
    /// <summary>
    /// The base match set when no expression narrows it: a single-type ResourceSource when a target type
    /// is named, otherwise a MultiTypeResourceSource over the requested types — empty meaning every type.
    /// </summary>
    private static CteRef LowerBaseSet(
        StructuralContext context,
        string? targetResourceType,
        IReadOnlyList<string>? resourceTypes)
        => targetResourceType is { } single
            ? context.LowerResourceSource(single)
            : context.LowerMultiTypeResourceSource(resourceTypes ?? []);
```

Add `LowerMultiTypeResourceSource` to `StructuralContext`, mirroring `LowerResourceSource`: resolve
each name through `_leafContext.ResourceTypeId`, add the CTE, record a `CteOrigin`, return the ref.

Keep `RequireResourceType` for the paths that genuinely need a scalar scope (chains, sort, includes)
— this task widens only the base set, not those.

- [ ] **Step 7: Prove the existing guards still throw**

The three `NotSupportedException` guards at `Lower.cs` lines ~53, ~64, ~75 must still fire for
typed leaves, `_sort`, and `_include` under a null scope. Confirm the existing tests covering them
still pass:

```bash
dotnet test test/Ignixa.Search.Sql.Tests --filter "FullyQualifiedName~LowerTests"
```

Expected: PASS. If a guard test now fails, `LowerBaseSet` was wired into a path that still requires a
scalar scope — revert that call site to `RequireResourceType`.

- [ ] **Step 8: Run everything and commit**

```bash
dotnet test test/Ignixa.Search.Sql.Tests
git add -A
git commit -m "feat(lowering): support a multi-type and system-wide match set

Lower required a single target resource type, so GET /?_type=A,B and a bare
system-wide search could not be expressed."
```

---

## Task 8: Access constraints, enforced structurally

SMART scopes stop being expression rewrites. The caller supplies per-resource-type predicates and
the compiler applies them to **every** row-producing stage, so a constraint cannot be bypassed by
navigating a reference.

**Files:**
- Create: `src/Core/Ignixa.Search/Models/AccessConstraint.cs`
- Create: `src/Core/Ignixa.Search.Sql/Lowering/AccessConstraintApplier.cs`
- Modify: `src/Core/Ignixa.Search.Sql/Lowering/Lower.cs`, `Lowering/StructuralContext.cs`
- Modify: `src/Core/Ignixa.Search/Models/SearchOptions.cs`
- Test: `test/Ignixa.Search.Sql.Tests/Lowering/AccessConstraintTests.cs` (create)

- [ ] **Step 1: Create the constraint model**

```csharp
// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Ignixa.Search.Expressions;

namespace Ignixa.Search.Models;

/// <summary>
/// A restriction on which resources of one type a caller may see, independent of what they searched for.
/// Expressed as an ordinary <see cref="Expression"/> so both the SQL compiler and a document-store query
/// builder enforce identical semantics from one source, rather than each re-deriving the rule from claims.
/// </summary>
/// <remarks>
/// The compiler applies constraints to every stage that produces rows — the match set, each include and
/// :iterate stage, and each chain target — not only the match set. Applying them at the match set alone
/// would let an _include reach a resource the caller may not see, which is the failure mode an
/// expression-rewriting approach is prone to.
/// </remarks>
/// <param name="ResourceType">The resource type the constraint governs.</param>
/// <param name="Predicate">What must hold for a resource of that type to be visible.</param>
public sealed record AccessConstraint(string ResourceType, Expression Predicate);
```

Add to `SearchOptions`:

```csharp
    /// <summary>
    /// Restrictions on which resources the caller may see, at most one per resource type. Empty means
    /// unrestricted. Enforced structurally by the compiler, not by rewriting the search expression.
    /// </summary>
    public IReadOnlyList<AccessConstraint> AccessConstraints { get; set; } = Array.Empty<AccessConstraint>();
```

- [ ] **Step 2: Write the failing test**

Create `test/Ignixa.Search.Sql.Tests/Lowering/AccessConstraintTests.cs`:

```csharp
using Ignixa.Search.Expressions;
using Ignixa.Search.Indexing.SearchValues;
using Ignixa.Search.Models;
using Ignixa.Search.Sql.Ast;
using Ignixa.Search.Sql.Builders;
using Ignixa.Search.Sql.Lowering;
using Ignixa.Search.Sql.Symbols;
using Ignixa.Specification.ValueSets.Normative;

namespace Ignixa.Search.Sql.Tests.Lowering;

/// <summary>
/// Constraints must bind to every stage that produces rows. A test that only checks the match set would
/// pass against an implementation that lets an _include walk straight past the restriction.
/// </summary>
public class AccessConstraintTests
{
    private const short ObservationTypeId = 104;
    private const short StatusParamId = 220;

    private static (SymbolTable Symbols, AccessConstraint Constraint) Arrange()
    {
        var status = new SearchParameterInfo(
            "status", "status", SearchParamType.Token,
            new Uri("http://hl7.org/fhir/SearchParameter/Observation-status"));

        var symbols = new SymbolTable(
            new Dictionary<string, short> { [status.Url!.ToString()] = StatusParamId },
            new Dictionary<string, short> { ["Observation"] = ObservationTypeId, ["Patient"] = 103 });

        var constraint = new AccessConstraint(
            "Observation",
            new SearchParameterPredicateExpression(
                status, SearchComparator.Eq, modifier: null,
                new TokenSearchValue(system: null, code: "final", text: null)));

        return (symbols, constraint);
    }

    [Fact]
    public void GivenAConstrainedType_WhenSearchedDirectly_ThenTheConstraintNarrowsTheMatchSet()
    {
        var (symbols, constraint) = Arrange();

        var plan = Lower.Run(
            expression: null,
            symbols,
            "Observation",
            includes: [],
            revIncludes: [],
            includeLimit: 0,
            sort: [],
            SortPhase.Valued,
            page: null,
            accessConstraints: [constraint]).Plan;

        SqlBuilder.Emit(plan).Sql.ShouldContain($"SearchParamId = {StatusParamId}");
    }

    [Fact]
    public void GivenAConstrainedType_WhenReachedOnlyThroughAnInclude_ThenTheIncludeStageIsStillConstrained()
    {
        var (symbols, constraint) = Arrange();

        var subject = new SearchParameterInfo(
            "subject", "subject", SearchParamType.Reference,
            new Uri("http://hl7.org/fhir/SearchParameter/Observation-subject"));

        var revInclude = new IncludeExpression(
            "Patient", subject, "Observation", "Patient",
            referencedTypes: ["Patient"], wildCard: false, reversed: true, iterate: false);

        var plan = Lower.Run(
            expression: null,
            symbols,
            "Patient",
            includes: [],
            revIncludes: [revInclude],
            includeLimit: 10,
            sort: [],
            SortPhase.Valued,
            page: null,
            accessConstraints: [constraint]).Plan;

        // The Observation rows arrive only via the _revinclude stage. If the constraint is applied to the
        // match set alone, this assertion fails and the caller sees Observations they may not read.
        SqlBuilder.Emit(plan).Sql.ShouldContain($"SearchParamId = {StatusParamId}");
    }
}
```

`IncludeExpression`'s constructor arity differs between versions — open
`src/Core/Ignixa.Search/Expressions/IncludeExpression.cs` and match it exactly, keeping the intent
(a reverse include of `Observation:subject` onto a `Patient` match set).

- [ ] **Step 3: Run and watch both fail**

```bash
dotnet test test/Ignixa.Search.Sql.Tests --filter "FullyQualifiedName~AccessConstraintTests"
```

Expected: FAIL — `Lower.Run` has no `accessConstraints` parameter.

- [ ] **Step 4: Create `AccessConstraintApplier`**

```csharp
using Ignixa.Search.Expressions;
using Ignixa.Search.Models;
using Ignixa.Search.Sql.Ast;

namespace Ignixa.Search.Sql.Lowering;

/// <summary>
/// Binds <see cref="AccessConstraint"/>s to the CTE graph. A constrained stage becomes the intersection
/// of what it produced and what the constraint admits, so the restriction survives every later set
/// operation rather than being a filter a subsequent union could widen back out.
/// </summary>
internal sealed class AccessConstraintApplier
{
    private readonly IReadOnlyDictionary<string, AccessConstraint> _byType;

    public AccessConstraintApplier(IReadOnlyList<AccessConstraint>? constraints)
    {
        _byType = constraints is { Count: > 0 }
            ? constraints.ToDictionary(c => c.ResourceType, StringComparer.Ordinal)
            : new Dictionary<string, AccessConstraint>(StringComparer.Ordinal);
    }

    public bool IsEmpty => _byType.Count == 0;

    /// <summary>
    /// Intersects <paramref name="stage"/> with the constraint for <paramref name="resourceType"/>, or
    /// returns it unchanged when that type is unconstrained.
    /// </summary>
    public CteRef Apply(CteRef stage, string resourceType, StructuralContext context, Func<Expression, StructuralContext, string, CteRef> lowerNode)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(lowerNode);

        if (!_byType.TryGetValue(resourceType, out var constraint))
        {
            return stage;
        }

        return context.Intersect(stage, lowerNode(constraint.Predicate, context, resourceType));
    }
}
```

`lowerNode` is passed in rather than referenced directly because `Lower.LowerNode` is private
static; injecting it keeps the applier free of a circular dependency on `Lower`.

- [ ] **Step 5: Apply it at every row-producing site in `Lower.Run`**

There are four:

1. The match set, after `match` is assigned — constrained on `targetResourceType`, or on each of
   `resourceTypes` when the scope is multi-type.
2. Each `IncludeStage` produced by `BuildIncludeStages` — constrained on each of its
   `OutputTypeIds`. Because an include stage is a plan record and not a `CteRef`, this means
   lowering the constraint into a CTE and adding it to the stage's seed set, or emitting the
   constraint as an additional `EXISTS` on the stage. Choose the `EXISTS` form: it keeps
   `IncludeStage`'s shape intact and matches how `EmitSeedExists` already correlates.
3. Each `:iterate` stage — same treatment as (2); they are the same record type.
4. Each chain target inside `StructuralContext.LowerChain` — constrained on the chain's inner
   resource type.

Add a test for the chain case in the same file, mirroring the include test.

- [ ] **Step 6: Run the tests**

```bash
dotnet test test/Ignixa.Search.Sql.Tests
```

Expected: PASS, all three constraint tests plus the existing suite.

- [ ] **Step 7: Assert the unconstrained path is untouched**

```csharp
    [Fact]
    public void GivenNoConstraints_WhenLowered_ThenThePlanIsIdenticalToOneLoweredWithoutTheParameter()
    {
        var (symbols, _) = Arrange();

        LoweredPlan Build(IReadOnlyList<AccessConstraint>? constraints) => Lower.Run(
            expression: null, symbols, "Observation", includes: [], revIncludes: [],
            includeLimit: 0, sort: [], SortPhase.Valued, page: null, accessConstraints: constraints);

        SqlBuilder.Emit(Build(null).Plan).Sql.ShouldBe(SqlBuilder.Emit(Build([]).Plan).Sql);
    }
```

- [ ] **Step 8: Commit**

```bash
git add -A
git commit -m "feat(lowering): enforce access constraints structurally

SMART scopes were expressed by rewriting the search expression, which left
_include and chain stages able to reach resources the caller may not see.
Bind constraints to every row-producing stage instead."
```

---

## Task 9: Includes-only query mode

The `$includes` operation fetches a second page of included resources without re-running the match
page. The plan needs to express "run the include stages, seeded from a known match page, and return
only their rows".

**Files:**
- Modify: `src/Core/Ignixa.Search.Sql/Ast/QueryPlan.cs`, `Builders/SqlBuilder.cs`
- Test: `test/Ignixa.Search.Sql.Tests/Ast/EmitTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void GivenAnIncludesOnlyPlan_WhenEmitted_ThenMatchRowsAreExcludedFromTheResult()
    {
        var plan = new QueryPlan(
            [new CteDefinition.ResourceSource(103)],
            new CteRef(0),
            Includes:
            [
                new IncludeStage(
                    IncludeDirection.Forward,
                    ReferenceSearchParamId: 210,
                    SeedTypeIds: [103],
                    OutputTypeIds: [111],
                    SeedStages: [],
                    SeedFromMatch: true,
                    Iterate: false,
                    Limit: 10),
            ],
            IncludesOnly: true);

        var sql = SqlBuilder.Emit(plan).Sql;

        sql.ShouldContain("IsMatch");
        sql.ShouldNotContain("SELECT 1 AS IsMatch");
    }
```

Open `src/Core/Ignixa.Search.Sql/Ast/IncludeStage.cs` to confirm — the argument order above matches
the record as of this writing (`Direction, ReferenceSearchParamId, SeedTypeIds, OutputTypeIds,
SeedStages, SeedFromMatch, Iterate, Limit`).

- [ ] **Step 2: Run it and watch it fail**

Expected: FAIL — `QueryPlan` has no `IncludesOnly`.

- [ ] **Step 3: Add `IncludesOnly` to `QueryPlan` and `Lower.Run`**

On `QueryPlan`:

```csharp
    /// <summary>
    /// When true, the emitted statement returns include-stage rows only, omitting the match page from the
    /// result while still using it to seed the stages. This is the $includes operation's second page: the
    /// caller already has the match rows and asks only for more included resources.
    /// </summary>
    bool IncludesOnly = false)
```

On `Lower.Run`, appended after the parameters added by Tasks 3, 5, 6, 7, and 8:

```csharp
        IReadOnlyList<AccessConstraint>? accessConstraints = null,
        bool includesOnly = false)
```

and pass `includesOnly` to the `QueryPlan` constructor. Every parameter added by this plan is
appended rather than inserted, and every test calls them by name, so the positional order across
tasks does not matter — but keep appending, so an in-flight branch from an earlier task does not
silently bind to the wrong argument.

- [ ] **Step 4: Emit it**

In the includes shape, drop the `UNION ALL` arm that contributes the match page rows with
`IsMatch = 1` when `plan.IncludesOnly` is true, keeping the match CTE itself because the stages'
`EXISTS` correlate to it.

- [ ] **Step 5: Guard the contradictory combination**

`IncludesOnly` with no includes returns nothing, which is a caller error rather than a legitimate
empty result. Add to `Lower.Run`, before constructing the plan:

```csharp
        if (includesOnly && includeStages is not { Count: > 0 })
        {
            throw new NotSupportedException(
                "IncludesOnly was requested with no _include or _revinclude stages, which can only ever " +
                "return an empty result. This is a caller error rather than a query that legitimately " +
                "matches nothing, so it is reported rather than silently emitted.");
        }
```

with a test asserting the throw.

- [ ] **Step 6: Run the tests and commit**

```bash
dotnet test test/Ignixa.Search.Sql.Tests
git add -A
git commit -m "feat(sql): add an includes-only query mode

The \$includes operation fetches a second page of included resources; the
plan could only express match-plus-includes, never includes alone."
```

---

## Task 10: Lower `$everything`

`PatientEverythingExpression` exists in `Ignixa.Search` but `Lower.LowerNode` has no arm for it, so
it falls to the `_ =>` throw. Worse, in practice `/Patient/{id}/$everything` currently compiles as a
plain search and **silently returns only the Patient** — verify this before starting so you know
what you are fixing.

**Files:**
- Create: `src/Core/Ignixa.Search.Sql/Lowering/EverythingLoweringRule.cs`
- Modify: `src/Core/Ignixa.Search.Sql/Lowering/Lower.cs`
- Test: `test/Ignixa.Search.Sql.Tests/Lowering/EverythingLoweringRuleTests.cs` (create)

- [ ] **Step 1: Confirm the current behaviour**

```bash
cd C:\Users\bkowitz\.copilot\repos\ignixa-fhir
grep -B 2 -A 20 'CompilerDoesLess: `/Patient/ignixa-evx-pat/\$everything?_count=100' test/Ignixa.Search.Sql.Tests/Corpus/reports/differential-report.md
```

Expected: the report shows the shipping engine reading `ReferenceSearchParam` twice and `Resource`
twice with `union-all` and `row-number`, and the compiler emitting none of it. This is the gap.

- [ ] **Step 2: Read the expression you are lowering**

```bash
cat src/Core/Ignixa.Search/Expressions/PatientEverythingExpression.cs
```

Note its exact property names — the code below assumes a patient resource id, an optional `_since`,
and an optional type filter, but the record is the source of truth.

- [ ] **Step 3: Write the failing test**

Create `EverythingLoweringRuleTests.cs`:

```csharp
using Ignixa.Search.Expressions;
using Ignixa.Search.Sql.Ast;
using Ignixa.Search.Sql.Builders;
using Ignixa.Search.Sql.Lowering;
using Ignixa.Search.Sql.Symbols;

namespace Ignixa.Search.Sql.Tests.Lowering;

public class EverythingLoweringRuleTests
{
    [Fact]
    public void GivenAPatientEverythingSearch_WhenLowered_ThenTheCompartmentIsTraversedNotJustThePatient()
    {
        var symbols = new SymbolTable(
            new Dictionary<string, short>(),
            new Dictionary<string, short> { ["Patient"] = 103, ["Observation"] = 104 });

        var expression = new PatientEverythingExpression("pat-1");

        var plan = Lower.Run(
            expression,
            symbols,
            "Patient",
            includes: [],
            revIncludes: [],
            includeLimit: 100,
            sort: [],
            SortPhase.Valued,
            page: null).Plan;

        var sql = SqlBuilder.Emit(plan).Sql;

        // The compartment traversal is the whole point of $everything; a plan that reads only
        // dbo.Resource has silently dropped it.
        sql.ShouldContain("dbo.ReferenceSearchParam");
        plan.Ctes.Count.ShouldBeGreaterThan(1);
    }
}
```

- [ ] **Step 4: Run it and watch it fail**

```bash
dotnet test test/Ignixa.Search.Sql.Tests --filter "FullyQualifiedName~EverythingLoweringRuleTests"
```

Expected: FAIL — the emitted SQL reads only `dbo.Resource`.

- [ ] **Step 5: Create `EverythingLoweringRule`**

`$everything` expands into primitives that already exist and are already tested, rather than a new
emitter: the patient row itself, unioned with a `CompartmentSource` over the patient compartment's
member types, plus include stages for the standard links. Reusing tested emitters is the reason to
prefer expansion over a bespoke plan node here.

```csharp
using Ignixa.Search.Expressions;
using Ignixa.Search.Sql.Ast;

namespace Ignixa.Search.Sql.Lowering;

/// <summary>
/// Lowers <see cref="PatientEverythingExpression"/> by expanding it into the plan primitives that already
/// exist — the patient's own row unioned with its compartment members — rather than introducing a new
/// CteDefinition. Every emitter this reaches is already covered by the per-rule suites, so the operation
/// inherits their coverage instead of needing its own emitter tests.
/// </summary>
internal static class EverythingLoweringRule
{
    public static CteRef Lower(PatientEverythingExpression expression, StructuralContext context, string resourceType)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(context);

        var patient = context.LowerResourceSourceForId(resourceType, expression.ResourceId);
        var compartment = context.LowerPatientCompartment(expression);

        return context.Union([patient, compartment]);
    }
}
```

Add `LowerResourceSourceForId` and `LowerPatientCompartment` to `StructuralContext`. The second is
the substantial one: it resolves the patient compartment's member types and their membership search
parameters and produces a `CompartmentSource` CTE per member type, unioned. `LowerCompartment`
already does exactly this for an ordinary compartment search — read it and reuse it rather than
writing a parallel implementation.

- [ ] **Step 6: Add the dispatch arm**

In `Lower.LowerNode`, above the `_ =>` throw:

```csharp
        PatientEverythingExpression everything => EverythingLoweringRule.Lower(everything, context, resourceType),
```

- [ ] **Step 7: Run the test**

```bash
dotnet test test/Ignixa.Search.Sql.Tests --filter "FullyQualifiedName~EverythingLoweringRuleTests"
```

Expected: PASS.

- [ ] **Step 8: Add `_since` and `_type` coverage**

Add two more tests: one asserting a `_since` value produces a `_lastUpdated` bound on the compartment
members, and one asserting a `_type` filter narrows the member types. Implement whichever fails.

- [ ] **Step 9: Confirm the corpus improved**

```bash
dotnet test test/Ignixa.Search.Sql.Tests
grep -A 8 "^## Summary" test/Ignixa.Search.Sql.Tests/bin/Debug/net10.0/legacy-sql-differential-report.md
```

Expected: the four `$everything` entries move out of `CompilerDoesLess`. Update
`DivergenceBaseline` and the checked-in report snapshot as in Task 2 Step 7.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(lowering): lower \$everything to a real compartment traversal

/Patient/{id}/\$everything compiled as a plain search and silently returned
only the Patient. Expand it into the patient row unioned with its
compartment members."
```

---

## Task 11: Publish the packages

The FHIR Server pins `Ignixa.Search 0.6.32` and `Ignixa.Search.Sql 0.6.32-alpha`, neither of which
is on any feed a clean clone or CI can reach — nuget.org tops out at `0.6.28` and the Microsoft
Health OSS feed returns 401. Plan 2 cannot merge until this is fixed, so fix it here.

**Files:**
- Modify: whichever CI workflow publishes packages (find it with the command below)

- [ ] **Step 1: Find the publish path**

```bash
cd C:\Users\bkowitz\.copilot\repos\ignixa-fhir
ls .github/workflows/
grep -rln "nuget push\|dotnet nuget push\|NuGetCommand" .github/
```

- [ ] **Step 2: Run the full suite one last time**

```bash
dotnet test
```

Expected: the whole repository's tests pass, not only `Ignixa.Search.Sql.Tests`. `Ignixa.Search`
gained `AccessConstraint` and two `SearchOptions` properties, so its own suite must pass too.

- [ ] **Step 3: Bump the version and publish a prerelease**

Bump the version in whatever file carries it (`Directory.Build.props` or the individual `.csproj`
files — check both), publish, and record the two exact package versions in the commit message.
Those versions are Plan 2's input.

- [ ] **Step 4: Verify a clean restore resolves them**

```bash
cd $env:TEMP
mkdir ignixa-restore-check; cd ignixa-restore-check
dotnet new classlib -f net9.0 --force
dotnet add package Ignixa.Search.Sql --version <the version you published> --prerelease
```

Expected: restore succeeds **without** a `--source` pointing at a local artifact directory. If it
does not, the package is not actually reachable and this task is not done.

- [ ] **Step 5: Commit**

```bash
cd C:\Users\bkowitz\.copilot\repos\ignixa-fhir
git add -A
git commit -m "build: publish Ignixa.Search and Ignixa.Search.Sql to a reachable feed

The FHIR Server pinned versions that existed only in a local artifact
directory, so a clean clone and CI could not restore."
```

---

## Done criteria

- [ ] `dotnet test` passes across the whole `ignixa-fhir` repository.
- [ ] `DivergenceBaseline.QueriesOmittingAFilter` is strictly lower than 46.
- [ ] `DivergenceBaseline.MatchingQueries` is strictly higher than 69.
- [ ] `test/Ignixa.Search.Sql.Tests/Corpus/reports/differential-report.md` is regenerated and committed.
- [ ] Both packages restore from a public feed with no `--source` override.
- [ ] The two published version numbers are recorded, ready for Plan 2.

## Deliberately not in this plan

- **`:not` support.** The spec listed it as a gap. It is not: `LowerSearchParameter` handles `:not`
  for both the single-value and comma-separated cases, and the `NotSupportedException` at
  `Lower.cs:103` is a deliberate guard against a shape the binder never produces. The corpus
  confirms `active:not=false` compiles today. Do not "fix" it.
- **Any FHIR Server change.** That is Plan 2, written once this plan's packages exist.
